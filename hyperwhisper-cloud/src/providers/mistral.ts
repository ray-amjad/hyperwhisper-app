// MISTRAL VOXTRAL PROVIDER
// Synchronous multipart transcription ($0.003/audio-min). Vocabulary biasing
// uses the structured `context_bias` list (≤100 phrases), not a free prompt.

import { computeMistralTranscriptionCost } from '../lib/cost-calculator';
import { ProviderInputError, ProviderUnavailableError } from './types';
import type { ProviderRequestContext, TranscriptionResult } from './types';
import { estimateSecondsFromBytes, fetchWithTimeout, logProviderEvent, readErrorBodyPreview } from './utils';

const MISTRAL_URL = 'https://api.mistral.ai/v1/audio/transcriptions';
const DEFAULT_MODEL = 'voxtral-mini-latest';
const MAX_CONTEXT_BIAS_TERMS = 100;

function getExtension(contentType: string): string {
  if (contentType.includes('wav')) return 'wav';
  if (contentType.includes('mp3') || contentType.includes('mpeg')) return 'mp3';
  if (contentType.includes('m4a') || contentType.includes('mp4')) return 'm4a';
  if (contentType.includes('aac')) return 'aac';
  if (contentType.includes('webm')) return 'webm';
  if (contentType.includes('ogg')) return 'ogg';
  if (contentType.includes('flac')) return 'flac';
  return 'wav';
}

/** Split a comma/newline vocabulary prompt into ≤100 `context_bias` phrases. */
function toContextBias(initialPrompt: string): string[] {
  return initialPrompt
    .split(/[,\n;]+/)
    .map((t) => t.trim().replace(/^[-*]\s*/, ''))
    .filter((t) => t.length > 0 && t.length <= 80)
    .slice(0, MAX_CONTEXT_BIAS_TERMS);
}

export async function transcribeWithMistral(
  audio: ArrayBuffer,
  contentType: string,
  language?: string,
  initialPrompt?: string,
  context: ProviderRequestContext = {},
): Promise<TranscriptionResult> {
  const startedAt = performance.now();
  const provider = 'mistral';
  const model = context.model || DEFAULT_MODEL;

  const apiKey = process.env.MISTRAL_API_KEY;
  if (!apiKey) {
    throw new Error('MISTRAL_API_KEY not configured');
  }

  const ext = getExtension(contentType);
  const formData = new FormData();
  formData.append('file', new Blob([audio], { type: contentType }), `audio.${ext}`);
  formData.append('model', model);

  if (language && language.toLowerCase() !== 'auto') {
    // Voxtral expects a bare ISO-639-1 code ("en"), not a hyphenated BCP-47
    // locale — strip to the primary subtag like the sibling adapters.
    const langCode = language.toLowerCase().split(/[-_]/)[0];
    formData.append('language', langCode);
  }

  const contextBias = initialPrompt ? toContextBias(initialPrompt) : [];
  if (contextBias.length) {
    // `context_bias` is typed as an ARRAY (List[str]) in Mistral's API schema and
    // SDKs, so over multipart/form-data it must be sent as one REPEATED form field
    // per term — `context_bias=a` `context_bias=b` — which the server collects into
    // a list under the key. A single comma-joined value is parsed as ONE literal
    // bias phrase ("a,b,c") and silently boosts nothing (still HTTP 200). The
    // prose-guide example showing `context_bias="a,b,c"` is an SDK/JSON call where
    // the SDK splits it; raw multipart needs the repeated-field encoding.
    // Refs: github.com/mistralai/client-python issue #338 (curl shows repeated
    // `-F context_bias=...`); docs.mistral.ai/api (audio/transcriptions →
    // context_bias: array). Each term appended individually below.
    for (const term of contextBias) {
      formData.append('context_bias', term);
    }
  }

  logProviderEvent(provider, 'prepare', {
    model,
    audioBytes: audio.byteLength,
    contentType,
    language: language || 'auto',
    contextBiasCount: contextBias.length,
  }, context);

  const response = await fetchWithTimeout(provider, MISTRAL_URL, {
    method: 'POST',
    headers: { Authorization: `Bearer ${apiKey}` },
    body: formData,
  }, context);

  if (!response.ok) {
    const errorText = await readErrorBodyPreview(response);
    const elapsedMs = Math.round(performance.now() - startedAt);
    const kind = response.status >= 500 ? 'upstream_5xx' : response.status === 429 ? 'rate_limit' : 'http_error';

    logProviderEvent(provider, 'http_error', {
      model, elapsedMs, status: response.status, kind, bodyPreview: errorText,
    }, context);

    if (response.status === 401 || response.status === 403) {
      throw new Error('Mistral API key is invalid or unauthorized');
    }
    if (response.status === 429) {
      throw new ProviderUnavailableError('Mistral', 'rate limit exceeded');
    }
    if (response.status === 402) {
      throw new ProviderUnavailableError('Mistral', 'insufficient funds');
    }
    if (response.status >= 500) {
      throw new ProviderUnavailableError('Mistral', `upstream 5xx: ${response.status}`);
    }
    throw new ProviderInputError('Mistral', response.status, errorText || `HTTP ${response.status}`);
  }

  let data: {
    text?: string;
    language?: string;
    usage?: { prompt_audio_seconds?: number };
  };
  try {
    data = await response.json();
  } catch {
    throw new ProviderUnavailableError('Mistral', 'malformed 200 response body');
  }

  const transcript = data.text || '';
  const rawDurationSeconds = data.usage?.prompt_audio_seconds || 0;

  if (!transcript || transcript.trim().length === 0) {
    logProviderEvent(provider, 'no_speech', {
      model, elapsedMs: Math.round(performance.now() - startedAt), language: data.language,
    }, context);
    return { text: '', language: data.language, durationSeconds: 0, costUsd: 0, source: 'no_speech' };
  }

  // Fail-closed: a successful transcript with a missing/non-positive duration
  // falls back to a byte-size estimate so we never bill $0.
  const durationSeconds = (rawDurationSeconds > 0 && Number.isFinite(rawDurationSeconds))
    ? rawDurationSeconds
    : estimateSecondsFromBytes(audio.byteLength);

  logProviderEvent(provider, 'success', {
    model,
    elapsedMs: Math.round(performance.now() - startedAt),
    transcriptChars: transcript.length,
    durationSeconds,
    language: data.language,
  }, context);

  return {
    text: transcript,
    language: data.language,
    durationSeconds,
    costUsd: computeMistralTranscriptionCost(durationSeconds),
    source: 'mistral',
  };
}
