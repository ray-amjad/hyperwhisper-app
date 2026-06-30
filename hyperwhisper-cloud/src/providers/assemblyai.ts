// ASSEMBLYAI PROVIDER (async, polling — no webhooks)
// Flow: upload raw bytes → create transcript (speech_models array) → poll the
// transcript until status is "completed" or "error". Medical is the
// `domain: "medical-v1"` metered add-on, not a separate model. A FAILED
// transcript comes back as HTTP 200 with status:"error", so the poll loop must
// branch on the body status, not just the HTTP code.

import { computeAssemblyAITranscriptionCost } from '../lib/cost-calculator';
import { MEDICAL_DOMAIN } from '../lib/stt-models';
import { ProviderInputError, ProviderUnavailableError } from './types';
import type { ProviderRequestContext, TranscriptionResult } from './types';
import { computeUploadTimeoutMs, estimateSecondsFromBytes, fetchWithTimeout, logProviderEvent, readErrorBodyPreview, sleep } from './utils';

const ASSEMBLYAI_BASE = 'https://api.assemblyai.com';
const DEFAULT_MODEL = 'universal-3-pro';
const MEDICAL_DOMAIN_VALUE = 'medical-v1';
const MAX_KEYTERMS = 200;
// The billable model is whatever AssemblyAI actually RAN (reported in the
// completed transcript), which may differ from the requested model because
// `speech_models` is a priority list that falls back universal-3-pro →
// universal-2 for unsupported languages. Only these ids are recognized for
// billing; an unknown/missing value falls back to the requested model.
const BILLABLE_MODELS = new Set(['universal-3-pro', 'universal-2']);
const POLL_INTERVAL_MS = 2_500;
const POLL_DEADLINE_MS = 240_000;

function authHeaders(apiKey: string): Record<string, string> {
  // AssemblyAI uses a bare API key in `authorization` — NO "Bearer" prefix.
  return { authorization: apiKey };
}

function toKeyterms(initialPrompt: string): string[] {
  return initialPrompt
    .split(/[,\n;]+/)
    .map((t) => t.trim().replace(/^[-*]\s*/, ''))
    .filter((t) => t.length >= 1 && t.length <= 50)
    .slice(0, MAX_KEYTERMS);
}

/** Map an upstream HTTP failure to the right chain-control error. */
function throwForStatus(status: number, bodyPreview: string): never {
  if (status === 401 || status === 403) {
    throw new Error('AssemblyAI API key is invalid or unauthorized');
  }
  if (status === 429) {
    throw new ProviderUnavailableError('AssemblyAI', 'rate limit exceeded');
  }
  // A 402 means THEIR billing/balance failed — an upstream outage, not a
  // client-input error. Surface it as provider-unavailable (→ 502) so we don't
  // mislabel it as a 400 the caller could "fix".
  if (status === 402) {
    throw new ProviderUnavailableError('AssemblyAI', 'insufficient funds');
  }
  if (status >= 500) {
    throw new ProviderUnavailableError('AssemblyAI', `upstream 5xx: ${status}`);
  }
  throw new ProviderInputError('AssemblyAI', status, bodyPreview || `HTTP ${status}`);
}

/**
 * Best-effort DELETE of a created transcript so a leaked job doesn't linger on
 * AssemblyAI after a timeout/throw. Mirrors Soniox's bestEffortDelete: failures
 * are logged (`cleanup_failed`) but never mask the transcription result/error.
 */
async function bestEffortDeleteTranscript(
  apiKey: string,
  transcriptId: string,
  context: ProviderRequestContext,
): Promise<void> {
  try {
    const response = await fetchWithTimeout('assemblyai', `${ASSEMBLYAI_BASE}/v2/transcript/${transcriptId}`, {
      method: 'DELETE',
      headers: authHeaders(apiKey),
    }, context);
    // fetchWithTimeout only throws on network/timeout errors — a non-2xx DELETE
    // (rate-limit, 5xx) resolves normally, so an un-checked response silently
    // leaks the transcript job upstream. A 404 means it's already gone, which is
    // the cleanup goal — tolerate it. Mirrors Soniox's bestEffortDelete.
    if (!response.ok && response.status !== 404) {
      logProviderEvent('assemblyai', 'cleanup_failed', { transcriptId, status: response.status }, context);
    }
  } catch (error) {
    logProviderEvent('assemblyai', 'cleanup_failed', {
      transcriptId, message: error instanceof Error ? error.message : String(error),
    }, context);
  }
}

export async function transcribeWithAssemblyAI(
  audio: ArrayBuffer,
  contentType: string,
  language?: string,
  initialPrompt?: string,
  context: ProviderRequestContext = {},
): Promise<TranscriptionResult> {
  const startedAt = performance.now();
  const provider = 'assemblyai';
  const model = context.model || DEFAULT_MODEL;
  const medical = (context.domain || '').toLowerCase() === MEDICAL_DOMAIN;

  const apiKey = process.env.ASSEMBLYAI_API_KEY;
  if (!apiKey) {
    throw new Error('ASSEMBLYAI_API_KEY not configured');
  }

  // ── 1. Upload raw audio bytes ──
  logProviderEvent(provider, 'prepare', {
    model, medical, audioBytes: audio.byteLength, contentType, language: language || 'auto',
  }, context);

  const uploadResp = await fetchWithTimeout(provider, `${ASSEMBLYAI_BASE}/v2/upload`, {
    method: 'POST',
    headers: { ...authHeaders(apiKey), 'Content-Type': 'application/octet-stream' },
    body: audio,
  }, context, computeUploadTimeoutMs(audio.byteLength));

  if (!uploadResp.ok) {
    const bodyPreview = await readErrorBodyPreview(uploadResp);
    logProviderEvent(provider, 'http_error', { phase: 'upload', status: uploadResp.status, bodyPreview }, context);
    throwForStatus(uploadResp.status, bodyPreview);
  }

  let uploadUrl: string;
  try {
    uploadUrl = ((await uploadResp.json()) as { upload_url?: string }).upload_url || '';
  } catch {
    throw new ProviderUnavailableError('AssemblyAI', 'malformed upload response');
  }
  if (!uploadUrl) {
    throw new ProviderUnavailableError('AssemblyAI', 'upload returned no upload_url');
  }

  // ── 2. Create the transcript job ──
  // `speech_models` is a priority/fallback list: AssemblyAI tries each model in
  // order and falls back to the next for languages the prior one doesn't cover.
  // universal-3-pro natively supports only 6 languages (EN/ES/PT/FR/DE/IT), so
  // we append universal-2 to reach all 99 — otherwise language_detection on any
  // other language fails (this is a self-only chain). universal-2 covers all 99
  // on its own, so it needs no fallback.
  // Ref: https://www.assemblyai.com/docs/pre-recorded-audio/universal-3-pro —
  // "use ['universal-3-pro', 'universal-2'] to fall back to Universal-2 for
  // unsupported languages."
  const speechModels = model === 'universal-3-pro' ? ['universal-3-pro', 'universal-2'] : [model];
  const createBody: Record<string, unknown> = {
    audio_url: uploadUrl,
    speech_models: speechModels,
  };
  if (language && language.toLowerCase() !== 'auto') {
    // AssemblyAI's `language_code` expects a bare ISO-639-1 code (e.g. "en",
    // "es", "pt"), NOT a hyphenated BCP-47 locale: "en-US" → "en-us" is rejected
    // at job creation. AssemblyAI does support a few underscore English variants
    // (en_us / en_uk), but converting hyphens to underscores wholesale would
    // synthesize unsupported region codes (es_es, fr_fr), so — like the OpenAI
    // and Soniox adapters — we strip the region to the always-valid primary
    // subtag. This self-only provider has no sibling to recover a bad code.
    createBody.language_code = language.toLowerCase().split(/[-_]/)[0];
  } else {
    createBody.language_detection = true;
  }
  const keyterms = initialPrompt ? toKeyterms(initialPrompt) : [];
  if (keyterms.length) {
    createBody.keyterms_prompt = keyterms;
  }
  if (medical) {
    // Medical Mode is an add-on enabled by `domain: "medical-v1"`, NOT a model
    // switch: AssemblyAI documents it as supported on Universal-3 Pro AND
    // Universal-2 (optimized for U3 Pro), "no model switch required". So it
    // pairs correctly with the default universal-3-pro and its universal-2
    // fallback above. Supported languages: EN, ES, DE, FR.
    // Ref: https://www.assemblyai.com/docs/getting-started/models — Medical Mode.
    createBody.domain = MEDICAL_DOMAIN_VALUE;
  }

  const createResp = await fetchWithTimeout(provider, `${ASSEMBLYAI_BASE}/v2/transcript`, {
    method: 'POST',
    headers: { ...authHeaders(apiKey), 'Content-Type': 'application/json' },
    body: JSON.stringify(createBody),
  }, context);

  if (!createResp.ok) {
    const bodyPreview = await readErrorBodyPreview(createResp);
    logProviderEvent(provider, 'http_error', { phase: 'create', status: createResp.status, bodyPreview }, context);
    throwForStatus(createResp.status, bodyPreview);
  }

  let transcriptId: string;
  try {
    transcriptId = ((await createResp.json()) as { id?: string }).id || '';
  } catch {
    throw new ProviderUnavailableError('AssemblyAI', 'malformed create response');
  }
  if (!transcriptId) {
    throw new ProviderUnavailableError('AssemblyAI', 'create returned no transcript id');
  }

  logProviderEvent(provider, 'job_created', { model, medical, transcriptId, keytermCount: keyterms.length }, context);

  // ── 3. Poll until completed / error ──
  // Wrap polling in try/finally so the created transcript is deleted on ANY exit
  // path (success, throw, or poll-deadline) — otherwise a timeout/throw leaks the
  // job upstream. Best-effort; failures are logged but never mask the result.
  try {
    const deadline = performance.now() + POLL_DEADLINE_MS;
    const pollUrl = `${ASSEMBLYAI_BASE}/v2/transcript/${transcriptId}`;
    let polls = 0;

    while (performance.now() < deadline) {
      await sleep(POLL_INTERVAL_MS);
      polls += 1;

      const pollResp = await fetchWithTimeout(provider, pollUrl, {
        method: 'GET',
        headers: authHeaders(apiKey),
      }, context);

      if (!pollResp.ok) {
        // Transient poll error — retry until the deadline rather than failing the
        // whole job on a single blip; hard-fail only on auth.
        const bodyPreview = await readErrorBodyPreview(pollResp);
        if (pollResp.status === 401 || pollResp.status === 403) {
          throw new Error('AssemblyAI API key is invalid or unauthorized');
        }
        logProviderEvent(provider, 'poll_http_error', { status: pollResp.status, bodyPreview, polls }, context);
        continue;
      }

      let job: {
        status?: string;
        text?: string;
        language_code?: string;
        audio_duration?: number;
        error?: string;
        speech_model_used?: string;
        speech_model?: string;
      };
      try {
        job = await pollResp.json();
      } catch {
        continue; // malformed poll body — try again
      }

      if (job.status === 'completed') {
        const transcript = job.text || '';
        const rawDuration = job.audio_duration || 0;

        if (!transcript || transcript.trim().length === 0) {
          logProviderEvent(provider, 'no_speech', { model, polls, language: job.language_code }, context);
          return { text: '', language: job.language_code, durationSeconds: 0, costUsd: 0, source: 'no_speech' };
        }

        // Fail-closed: a successful transcript with a missing/non-positive
        // duration falls back to a byte-size estimate so we never bill $0.
        const durationSeconds = (rawDuration > 0 && Number.isFinite(rawDuration))
          ? rawDuration
          : estimateSecondsFromBytes(audio.byteLength);

        // Bill the model that ACTUALLY ran, not the one requested. With the
        // `speech_models` priority list, universal-3-pro silently falls back to
        // universal-2 for unsupported languages — universal-2 is cheaper and its
        // keyterms are free, so billing the requested universal-3-pro rate (+
        // keyterms add-on) over-charges. `speech_model_used` reports the model
        // that ran; read defensively and only trust a recognized id so an
        // unexpected/missing value keeps the requested model (no regression).
        const reportedModel = (job.speech_model_used || job.speech_model || '').toLowerCase();
        const billedModel = BILLABLE_MODELS.has(reportedModel) ? reportedModel : model;
        if (billedModel !== model) {
          logProviderEvent(provider, 'model_fallback', {
            requested: model, billed: billedModel, language: job.language_code,
          }, context);
        }

        logProviderEvent(provider, 'success', {
          model: billedModel, requestedModel: model, medical, polls,
          elapsedMs: Math.round(performance.now() - startedAt),
          transcriptChars: transcript.length,
          durationSeconds,
          language: job.language_code,
        }, context);

        return {
          text: transcript,
          language: job.language_code,
          durationSeconds,
          costUsd: computeAssemblyAITranscriptionCost(durationSeconds, billedModel, medical, keyterms.length > 0),
          source: 'assemblyai',
          // Report the model that ACTUALLY ran so the route labels X-STT-Model
          // and deduction metadata as universal-2 on a fallback, not the
          // requested universal-3-pro (which is what we billed for too).
          model: billedModel,
          requestId: transcriptId,
        };
      }

      if (job.status === 'error') {
        // Failed transcript returns HTTP 200 + status:"error" — a bad-input signal.
        logProviderEvent(provider, 'job_error', { model, polls, message: job.error }, context);
        throw new ProviderInputError('AssemblyAI', 422, job.error || 'transcription failed');
      }
      // queued | processing → keep polling
    }

    logProviderEvent(provider, 'poll_deadline', { model, polls, deadlineMs: POLL_DEADLINE_MS }, context);
    throw new ProviderUnavailableError('AssemblyAI', `poll deadline exceeded after ${POLL_DEADLINE_MS}ms`);
  } finally {
    // Best-effort cleanup — AssemblyAI keeps the transcript otherwise. Failures
    // are logged but never mask the transcription result/error.
    await bestEffortDeleteTranscript(apiKey, transcriptId, context);
  }
}
