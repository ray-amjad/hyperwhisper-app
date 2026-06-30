// SONIOX PROVIDER (async, polling — no webhooks)
// Flow: upload file → create transcription → poll job status → fetch transcript
// → DELETE both the transcription and the file (Soniox does NOT auto-delete;
// orphans count against the 1,000-file / 10 GB account caps). A failed job
// returns HTTP 200 with status:"failed" (legacy "error" tolerated) plus an
// error_type slug + error_message; the poll loop classifies by error_type.

import { computeSonioxTranscriptionCost, estimateSonioxContextTokens } from '../lib/cost-calculator';
import { BYTES_PER_MINUTE_ESTIMATE } from '../lib/constants';
import { AudioTooLargeError, ProviderInputError, ProviderUnavailableError } from './types';
import type { ProviderRequestContext, TranscriptionResult } from './types';
import { computeUploadTimeoutMs, estimateSecondsFromBytes, fetchWithTimeout, logProviderEvent, readErrorBodyPreview, sleep } from './utils';

const SONIOX_BASE = 'https://api.soniox.com';
const DEFAULT_MODEL = 'stt-async-v4';
const POLL_INTERVAL_MS = 1_000;
const POLL_DEADLINE_MS = 240_000;
const MAX_CONTEXT_TERMS = 200;
// Soniox supports async files up to 300 min, but our request-scoped poll budget
// is POLL_DEADLINE_MS, and a transcription still "processing" at the deadline
// CANNOT be deleted (Soniox returns 409 transcription_invalid_state) — so
// abandoning it there leaks a running, billable upstream job. Gate overly-long
// uploads BEFORE creating any upstream file/job so we only accept audio we can
// reliably poll to completion and clean up. ~30 min at the 64 kbps byte→seconds
// estimate comfortably covers dictation while keeping a wide margin under the
// deadline at Soniox's faster-than-realtime batch throughput.
const SONIOX_MAX_BYTES = 30 * BYTES_PER_MINUTE_ESTIMATE;

// A Soniox async job that ends in failure carries a stable `error_type` slug
// (branch on the slug, not the message). Only these are genuinely caused by the
// caller's input/request and map to a 4xx the caller could fix; everything else
// (billing exhaustion, rate/limits, internal/service failures) is an upstream
// condition that must surface as a 502, not be mislabeled as a 400/422 input
// rejection on this self-only provider.
// Ref: https://soniox.com/docs/api-reference/errors
const SONIOX_INPUT_ERROR_TYPES = new Set([
  'invalid_request',
  'invalid_audio_file',
  'model_not_available',
]);

function authHeader(apiKey: string): Record<string, string> {
  return { Authorization: `Bearer ${apiKey}` };
}

function getExtension(contentType: string): string {
  if (contentType.includes('wav')) return 'wav';
  if (contentType.includes('mp3') || contentType.includes('mpeg')) return 'mp3';
  if (contentType.includes('m4a') || contentType.includes('mp4')) return 'm4a';
  if (contentType.includes('webm')) return 'webm';
  if (contentType.includes('ogg')) return 'ogg';
  if (contentType.includes('flac')) return 'flac';
  return 'wav';
}

function toContextTerms(initialPrompt: string): string[] {
  return initialPrompt
    .split(/[,\n;]+/)
    .map((t) => t.trim().replace(/^[-*]\s*/, ''))
    .filter((t) => t.length >= 1 && t.length <= 80)
    .slice(0, MAX_CONTEXT_TERMS);
}

function throwForStatus(status: number, bodyPreview: string): never {
  if (status === 401 || status === 403) {
    throw new Error('Soniox API key is invalid or unauthorized');
  }
  if (status === 429) {
    throw new ProviderUnavailableError('Soniox', 'rate limit exceeded');
  }
  // A 402 means THEIR billing/balance failed — an upstream outage, not a
  // client-input error. Surface it as provider-unavailable (→ 502) so we don't
  // mislabel it as a 400 the caller could "fix".
  if (status === 402) {
    throw new ProviderUnavailableError('Soniox', 'insufficient funds');
  }
  if (status >= 500) {
    throw new ProviderUnavailableError('Soniox', `upstream 5xx: ${status}`);
  }
  throw new ProviderInputError('Soniox', status, bodyPreview || `HTTP ${status}`);
}

async function bestEffortDelete(
  apiKey: string,
  path: string,
  context: ProviderRequestContext,
): Promise<void> {
  try {
    const response = await fetchWithTimeout('soniox', `${SONIOX_BASE}${path}`, {
      method: 'DELETE',
      headers: authHeader(apiKey),
    }, context);
    // fetchWithTimeout only throws on network/timeout errors — a non-2xx DELETE
    // (rate-limit, 5xx) resolves normally, so an un-checked response silently
    // leaks the file against the 1,000-file / 10 GB account caps. A 404 means
    // the resource is already gone, which is the cleanup goal — tolerate it.
    if (!response.ok && response.status !== 404) {
      logProviderEvent('soniox', 'cleanup_failed', { path, status: response.status }, context);
    }
  } catch (error) {
    logProviderEvent('soniox', 'cleanup_failed', {
      path, message: error instanceof Error ? error.message : String(error),
    }, context);
  }
}

export async function transcribeWithSoniox(
  audio: ArrayBuffer,
  contentType: string,
  language?: string,
  initialPrompt?: string,
  context: ProviderRequestContext = {},
): Promise<TranscriptionResult> {
  const startedAt = performance.now();
  const provider = 'soniox';
  const model = context.model || DEFAULT_MODEL;

  const apiKey = process.env.SONIOX_API_KEY;
  if (!apiKey) {
    throw new Error('SONIOX_API_KEY not configured');
  }

  // Reject audio too long to reliably finish within the poll deadline BEFORE
  // creating any upstream file/job — a job still processing at POLL_DEADLINE_MS
  // can't be deleted (Soniox 409), which would orphan a running, billable job.
  // Surfaces as 413 via the AudioTooLargeError path in transcribe.ts.
  if (audio.byteLength > SONIOX_MAX_BYTES) {
    logProviderEvent(provider, 'audio_too_long', {
      audioBytes: audio.byteLength, maxBytes: SONIOX_MAX_BYTES,
    }, context);
    throw new AudioTooLargeError('Soniox', audio.byteLength, SONIOX_MAX_BYTES);
  }

  let fileId = '';
  let transcriptionId = '';

  try {
    // ── 1. Upload the file ──
    logProviderEvent(provider, 'prepare', {
      model, audioBytes: audio.byteLength, contentType, language: language || 'auto',
    }, context);

    const uploadForm = new FormData();
    uploadForm.append('file', new Blob([audio], { type: contentType }), `audio.${getExtension(contentType)}`);

    const uploadResp = await fetchWithTimeout(provider, `${SONIOX_BASE}/v1/files`, {
      method: 'POST',
      headers: authHeader(apiKey),
      body: uploadForm,
    }, context, computeUploadTimeoutMs(audio.byteLength));

    if (!uploadResp.ok) {
      const bodyPreview = await readErrorBodyPreview(uploadResp);
      logProviderEvent(provider, 'http_error', { phase: 'upload', status: uploadResp.status, bodyPreview }, context);
      throwForStatus(uploadResp.status, bodyPreview);
    }
    try {
      fileId = ((await uploadResp.json()) as { id?: string }).id || '';
    } catch {
      throw new ProviderUnavailableError('Soniox', 'malformed upload response');
    }
    if (!fileId) {
      throw new ProviderUnavailableError('Soniox', 'upload returned no file id');
    }

    // ── 2. Create the transcription ──
    const createBody: Record<string, unknown> = {
      file_id: fileId,
      model,
      enable_language_identification: true,
    };
    if (language && language.toLowerCase() !== 'auto') {
      // Soniox `language_hints` expects ISO language codes (e.g. "en"/"es"), not
      // full BCP-47 tags — strip any region/script subtag so a client-supplied
      // "en-US"/"pt-BR" becomes "en"/"pt" and actually biases detection.
      const hint = language.toLowerCase().split(/[-_]/)[0];
      createBody.language_hints = [hint];
    }
    const terms = initialPrompt ? toContextTerms(initialPrompt) : [];
    if (terms.length) {
      createBody.context = { terms };
    }

    const createResp = await fetchWithTimeout(provider, `${SONIOX_BASE}/v1/transcriptions`, {
      method: 'POST',
      headers: { ...authHeader(apiKey), 'Content-Type': 'application/json' },
      body: JSON.stringify(createBody),
    }, context);

    if (!createResp.ok) {
      const bodyPreview = await readErrorBodyPreview(createResp);
      logProviderEvent(provider, 'http_error', { phase: 'create', status: createResp.status, bodyPreview }, context);
      throwForStatus(createResp.status, bodyPreview);
    }
    try {
      transcriptionId = ((await createResp.json()) as { id?: string }).id || '';
    } catch {
      throw new ProviderUnavailableError('Soniox', 'malformed create response');
    }
    if (!transcriptionId) {
      throw new ProviderUnavailableError('Soniox', 'create returned no transcription id');
    }

    logProviderEvent(provider, 'job_created', { model, transcriptionId, termCount: terms.length }, context);

    // ── 3. Poll job status ──
    const deadline = performance.now() + POLL_DEADLINE_MS;
    const jobUrl = `${SONIOX_BASE}/v1/transcriptions/${transcriptionId}`;
    let polls = 0;
    let durationSeconds = 0;
    let completed = false;

    while (performance.now() < deadline) {
      await sleep(POLL_INTERVAL_MS);
      polls += 1;

      const jobResp = await fetchWithTimeout(provider, jobUrl, {
        method: 'GET',
        headers: authHeader(apiKey),
      }, context);

      if (!jobResp.ok) {
        if (jobResp.status === 401 || jobResp.status === 403) {
          throw new Error('Soniox API key is invalid or unauthorized');
        }
        const bodyPreview = await readErrorBodyPreview(jobResp);
        logProviderEvent(provider, 'poll_http_error', { status: jobResp.status, bodyPreview, polls }, context);
        continue;
      }

      let job: { status?: string; audio_duration_ms?: number; error_message?: string; error_type?: string };
      try {
        job = await jobResp.json();
      } catch {
        continue;
      }

      if (typeof job.audio_duration_ms === 'number') {
        durationSeconds = job.audio_duration_ms / 1000;
      }

      // Soniox documents a failed async job as status:"failed" (we also accept
      // the legacy "error" defensively). Classify by error_type: billing/limit/
      // service/internal failures are upstream conditions → ProviderUnavailableError
      // (502); only genuine bad-input types stay a 422 the caller could fix.
      if (job.status === 'failed' || job.status === 'error') {
        const errorType = (job.error_type || '').toLowerCase();
        logProviderEvent(provider, 'job_error', { model, polls, errorType, message: job.error_message }, context);
        if (errorType && !SONIOX_INPUT_ERROR_TYPES.has(errorType)) {
          throw new ProviderUnavailableError('Soniox', job.error_message || `async job failed: ${errorType}`);
        }
        throw new ProviderInputError('Soniox', 422, job.error_message || 'transcription failed');
      }
      if (job.status === 'completed') {
        completed = true;
        break;
      }
      // queued | processing → keep polling until the deadline
    }

    if (!completed) {
      logProviderEvent(provider, 'poll_deadline', { model, polls, deadlineMs: POLL_DEADLINE_MS }, context);
      throw new ProviderUnavailableError('Soniox', `poll deadline exceeded after ${POLL_DEADLINE_MS}ms`);
    }

    // ── 4. Fetch the transcript text ──
    const transcriptResp = await fetchWithTimeout(provider, `${jobUrl}/transcript`, {
      method: 'GET',
      headers: authHeader(apiKey),
    }, context);

    if (!transcriptResp.ok) {
      const bodyPreview = await readErrorBodyPreview(transcriptResp);
      logProviderEvent(provider, 'http_error', { phase: 'transcript', status: transcriptResp.status, bodyPreview }, context);
      throwForStatus(transcriptResp.status, bodyPreview);
    }

    let transcriptData: { text?: string; tokens?: Array<{ language?: string }> };
    try {
      transcriptData = await transcriptResp.json();
    } catch {
      throw new ProviderUnavailableError('Soniox', 'malformed transcript response');
    }

    const transcript = (transcriptData.text || '').trim();
    const language_ = transcriptData.tokens?.find((t) => t.language)?.language;

    if (!transcript) {
      logProviderEvent(provider, 'no_speech', { model, polls }, context);
      return { text: '', language: language_, durationSeconds: 0, costUsd: 0, source: 'no_speech' };
    }

    // Fail-closed: a successful transcript with a missing/non-positive duration
    // falls back to a byte-size estimate so we never bill $0.
    const billableSeconds = (durationSeconds > 0 && Number.isFinite(durationSeconds))
      ? durationSeconds
      : estimateSecondsFromBytes(audio.byteLength);

    logProviderEvent(provider, 'success', {
      model, polls,
      elapsedMs: Math.round(performance.now() - startedAt),
      transcriptChars: transcript.length,
      durationSeconds: billableSeconds,
      language: language_,
    }, context);

    // Soniox bills the custom-context terms as async input-text tokens on top
    // of the audio/output blend — include them so we don't under-charge a clip
    // sent with a large vocabulary context.
    const contextTextTokens = estimateSonioxContextTokens(terms.join(' '));
    return {
      text: transcript,
      language: language_,
      durationSeconds: billableSeconds,
      costUsd: computeSonioxTranscriptionCost(billableSeconds, contextTextTokens),
      source: 'soniox',
      requestId: transcriptionId,
    };
  } finally {
    // Mandatory cleanup — Soniox never auto-deletes. Best-effort; failures are
    // logged but never mask the transcription result/error.
    if (transcriptionId) {
      await bestEffortDelete(apiKey, `/v1/transcriptions/${transcriptionId}`, context);
    }
    if (fileId) {
      await bestEffortDelete(apiKey, `/v1/files/${fileId}`, context);
    }
  }
}
