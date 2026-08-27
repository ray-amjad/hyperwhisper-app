// GEMINI 3.5 TRANSCRIBE PROVIDER — Google's dedicated speech models.
//
// A different product from `providers/gemini.ts`, on a different endpoint:
// `POST /v1beta/interactions`, inline base64 audio, a real `transcription_config`
// with `language_codes` + `custom_vocabulary`, and an `Api-Revision` header that
// pins the request/response shape to a dated revision.
//
//   POST https://generativelanguage.googleapis.com/v1beta/interactions
//   x-goog-api-key: <key> | Api-Revision: 2026-05-20 | Content-Type: application/json
//   {"model":"gemini-3.5-transcribe",
//    "input":[{"type":"audio","mime_type":"audio/mp3","data":"<base64>"}],
//    "generation_config":{"transcription_config":{
//       "language_codes":["en-US"],"custom_vocabulary":["HyperWhisper"]}}}
//
// Transcript at `steps[0].content[0].text`; token counts at
// `usage.input_tokens_by_modality[]`.
//
// TRAP 1 — do NOT route this model through `providers/gemini.ts`.
// `:generateContent` *accepts* `gemini-3.5-transcribe`, *bills the audio*, and
// returns a content part with empty text and no error: a silent, paid no-op.
// Verified against the live API. Only `/v1beta/interactions` serves these models,
// which is why this is a separate adapter rather than another model row on the
// Gemini one. The same rule is documented on the native side in
// `shared-core-rs/crates/hw-net/src/providers/gemini_transcribe.rs`.
//
// TRAP 2 — `custom_vocabulary` is mutually exclusive with BOTH
// `diarization_mode` ("custom_vocabulary is incompatible with diarization.") and
// `timestamp_granularities` ("... incompatible with timestamps."), each an HTTP
// 400. `buildTranscriptionConfig` below is the single place that resolves it.
//
// Data retention: same Google paid-tier posture as `providers/gemini.ts` (no
// training on paid API traffic) — it is the same API key and project. There is
// no per-request opt-out flag on this endpoint. See `mintlify-help/data-privacy.mdx`.

import {
  GEMINI_TRANSCRIBE_AUDIO_TOKENS_PER_SECOND,
  computeGeminiTranscribeCost,
  estimateGeminiTranscribeOutputTokens,
} from '../lib/cost-calculator';
import { GEMINI_TRANSCRIBE_INLINE_MAX_BYTES } from '../lib/constants';
// A pure Content-Type → Google audio MIME mapper. Importing it is NOT the
// generateContent path (TRAP 1): the two endpoints accept the same MIME
// vocabulary, and a second copy of the table would drift.
import { geminiMimeType } from './gemini';
import { AudioTooLargeError, ProviderInputError, ProviderUnavailableError } from './types';
import type { ProviderRequestContext, TranscriptionResult } from './types';
import {
  estimateAudioSeconds,
  fetchWithTimeout,
  isExplicitLanguage,
  logProviderEvent,
  providerHttpError,
  readErrorBodyPreview,
} from './utils';

const INTERACTIONS_URL = 'https://generativelanguage.googleapis.com/v1beta/interactions';
// Google versions this endpoint by date. Omitting the header selects whatever
// the current default revision is, which is not a stable contract — pin it, and
// re-verify the request/response shape before moving it.
const API_REVISION = '2026-05-20';
const DEFAULT_MODEL = 'gemini-3.5-transcribe';
// WebSocket-only (BidiGenerateContent). `/v1beta/interactions` answers a request
// for it with a 404 "Model not found", so this adapter rejects it up front
// rather than spending a round trip — and so the failure names the real reason.
// The live model is served by `routes/ws-streaming-gemini-transcribe.ts`.
export const GEMINI_TRANSCRIBE_LIVE_MODEL = 'gemini-3.5-transcribe-live';

// Vocabulary caps, matching the Rust core's `sanitize_vocabulary_word` /
// `keyword_boost_terms` — the canonical sanitizer the BYOK half of this feature
// routes through, so Cloud and BYOK send the upstream the same terms.
const MAX_VOCABULARY_TERMS = 100;
const MAX_VOCABULARY_TERM_CHARS = 80;

// Explicit timeout budget rather than the shared 15 s DEFAULT_PROVIDER_TIMEOUT_MS.
// Measured against the live endpoint: a warm request is 2.5–4.5 s regardless of
// clip length (a 57 s clip returned in 4.3 s), but the FIRST request against a
// cold path took 35 s, twice. `gemini-transcribe` is self-only, so a timeout is a
// hard 502 with no sibling to absorb it — budget for the cold case. Same reasoning
// (and the same number) as google-chirp's SYNC_RECOGNIZE_TIMEOUT_MS.
const INTERACTIONS_TIMEOUT_MS = 45_000;

/**
 * Optional `transcription_config` extras. Both are dropped when vocabulary terms
 * are present — see {@link buildTranscriptionConfig}.
 *
 * Nothing in this backend requests them: `/transcribe` returns text only, so the
 * word offsets and speaker labels have nowhere to go, and never sending them
 * makes TRAP 2 unreachable in production. They are a parameter, rather than an
 * unwritten convention, so the exclusion rule has one directly testable home —
 * a future word-timings change must go through it instead of adding the field
 * next to the vocabulary and getting a 400 for every dictating user.
 */
export interface TranscriptionConfigExtras {
  /** e.g. `'speaker'`. */
  diarizationMode?: string;
  /** e.g. `['word']`. */
  timestampGranularities?: string[];
}

/**
 * Build `generation_config.transcription_config`.
 *
 * **The single enforcement point for TRAP 2.** Google rejects `custom_vocabulary`
 * with a 400 when it arrives alongside `diarization_mode` or
 * `timestamp_granularities`. Rather than making each call site remember that,
 * this resolves the conflict: when vocabulary terms survive normalization they
 * are sent and the extras are dropped. HyperWhisper is a dictation app — correct
 * spellings of the user's own jargon beat speaker labels and word offsets.
 *
 * Returns a possibly-empty object; the caller omits `generation_config` entirely
 * when nothing is configured (auto-detect, no vocabulary).
 */
export function buildTranscriptionConfig(
  language: string | undefined,
  terms: readonly string[],
  extras: TranscriptionConfigExtras = {},
): Record<string, unknown> {
  const config: Record<string, unknown> = {};

  // An explicit language is passed through as-is. Verified live: this endpoint
  // accepts both the bare subtag ('en') and the region-qualified tag ('en-US'),
  // unlike Google Chirp, which 400s on a bare subtag.
  if (isExplicitLanguage(language)) {
    config.language_codes = [language];
  }

  if (terms.length > 0) {
    // TRAP 2: vocabulary wins. `extras` is discarded, not merged.
    config.custom_vocabulary = [...terms];
    return config;
  }

  const diarization = extras.diarizationMode?.trim();
  if (diarization) {
    config.diarization_mode = diarization;
  }
  if (extras.timestampGranularities && extras.timestampGranularities.length > 0) {
    config.timestamp_granularities = [...extras.timestampGranularities];
  }

  return config;
}

/**
 * Split a comma/newline `initial_prompt` into `custom_vocabulary` values,
 * reproducing the Rust core's `sanitize_vocabulary_word`: strip `<`/`>`,
 * collapse whitespace runs, truncate (never drop) an over-long term, and
 * de-duplicate case-insensitively.
 */
export function toVocabularyTerms(initialPrompt: string): string[] {
  const seen = new Set<string>();
  const terms: string[] = [];

  for (const raw of initialPrompt.split(/[,\n;]+/)) {
    const term = raw
      .trim()
      // Only strip a bullet when a space follows it: without the space a leading
      // '-' or '*' is part of the term ('-Xmx', '*args'), and the BYOK path
      // keeps it.
      .replace(/^[-*]\s+/, '')
      .replace(/[<>]/g, '')
      .replace(/\s+/g, ' ')
      .trim()
      .slice(0, MAX_VOCABULARY_TERM_CHARS);
    if (term.length === 0) continue;

    const key = term.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    terms.push(term);
    if (terms.length === MAX_VOCABULARY_TERMS) break;
  }

  return terms;
}

interface InteractionsUsage {
  input_tokens_by_modality?: Array<{ modality?: string; tokens?: number }>;
  total_input_tokens?: number;
  /** Always 0 on this endpoint — see cost-calculator's GEMINI_TRANSCRIBE_RATES. */
  total_output_tokens?: number;
}

interface InteractionsResponse {
  steps?: Array<{ content?: Array<{ text?: string }> }>;
  usage?: InteractionsUsage;
  id?: string;
  model?: string;
}

/** Concatenate the text of every content entry across every step. */
export function collectTranscript(data: InteractionsResponse): string {
  const parts: string[] = [];
  for (const step of data.steps ?? []) {
    for (const entry of step.content ?? []) {
      if (typeof entry.text === 'string') parts.push(entry.text);
    }
  }
  return parts.join('').trim();
}

export interface InputTokenCounts {
  audioTokens: number;
  textTokens: number;
}

/**
 * Read `usage.input_tokens_by_modality[]` — a LIST of
 * `{ modality: 'audio' | 'text', tokens: N }` entries, not a flat map.
 */
export function readInputTokens(usage: InteractionsUsage | undefined): InputTokenCounts {
  const counts: InputTokenCounts = { audioTokens: 0, textTokens: 0 };
  for (const entry of usage?.input_tokens_by_modality ?? []) {
    const tokens = typeof entry.tokens === 'number' && entry.tokens > 0 ? entry.tokens : 0;
    if (entry.modality === 'audio') counts.audioTokens += tokens;
    else if (entry.modality === 'text') counts.textTokens += tokens;
  }
  return counts;
}

/**
 * Google answers an invalid API key on this endpoint with **400**
 * `"API key not valid. Please pass a valid API key."` (reason `API_KEY_INVALID`),
 * not 401 — verified live. Without this the generic ladder would classify it as
 * a `ProviderInputError`, and a mis-synced key would surface to every user as
 * "Transcription input rejected" instead of an auth failure.
 */
function looksLikeAuthFailure(body: string): boolean {
  const lower = body.toLowerCase();
  return lower.includes('api key not valid')
    || lower.includes('api_key_invalid')
    || lower.includes('invalid authentication')
    || lower.includes('unauthenticated');
}

export async function transcribeWithGeminiTranscribe(
  audio: ArrayBuffer,
  contentType: string,
  language?: string,
  initialPrompt?: string,
  context: ProviderRequestContext = {},
): Promise<TranscriptionResult> {
  const startedAt = performance.now();
  const provider = 'gemini-transcribe';
  const model = context.model || DEFAULT_MODEL;

  if (model === GEMINI_TRANSCRIBE_LIVE_MODEL) {
    // Fail before spending a round trip, and name the reason. A
    // ProviderInputError on this self-only chain surfaces as a 400.
    throw new ProviderInputError(
      'Gemini 3.5 Transcribe',
      400,
      `${GEMINI_TRANSCRIBE_LIVE_MODEL} is a WebSocket-only model and is not served by /transcribe`,
    );
  }

  // Same key as the Gemini LLM adapter — same Google project, no new secret.
  const apiKey = process.env.GEMINI_API_KEY || process.env.GOOGLE_GEMINI_API_KEY;
  if (!apiKey) {
    throw new Error('GEMINI_API_KEY not configured');
  }

  // No Files-API overflow path exists on this endpoint, so an oversized payload
  // is a hard 413 (defense in depth behind the route's pre-buffer gate).
  if (audio.byteLength > GEMINI_TRANSCRIBE_INLINE_MAX_BYTES) {
    logProviderEvent(provider, 'audio_too_large', {
      model, audioBytes: audio.byteLength, maxBytes: GEMINI_TRANSCRIBE_INLINE_MAX_BYTES,
    }, context);
    throw new AudioTooLargeError('Gemini 3.5 Transcribe', audio.byteLength, GEMINI_TRANSCRIBE_INLINE_MAX_BYTES);
  }

  const terms = initialPrompt ? toVocabularyTerms(initialPrompt) : [];
  const transcriptionConfig = buildTranscriptionConfig(language, terms);
  const body: Record<string, unknown> = {
    model,
    input: [{
      type: 'audio',
      mime_type: geminiMimeType(contentType),
      data: Buffer.from(audio).toString('base64'),
    }],
  };
  if (Object.keys(transcriptionConfig).length > 0) {
    body.generation_config = { transcription_config: transcriptionConfig };
  }

  logProviderEvent(provider, 'prepare', {
    model,
    audioBytes: audio.byteLength,
    contentType,
    language: language || 'auto',
    vocabularyTermCount: terms.length,
  }, context);

  const response = await fetchWithTimeout(provider, INTERACTIONS_URL, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'x-goog-api-key': apiKey,
      'Api-Revision': API_REVISION,
    },
    body: JSON.stringify(body),
  }, context, INTERACTIONS_TIMEOUT_MS);

  if (!response.ok) {
    if (response.status === 400) {
      // Peek at a clone so providerHttpError below still owns the body it logs.
      const preview = await readErrorBodyPreview(response.clone());
      if (looksLikeAuthFailure(preview)) {
        logProviderEvent(provider, 'http_error', {
          model, status: 400, kind: 'auth', bodyPreview: preview,
        }, context);
        throw new Error('Gemini API key is invalid or unauthorized');
      }
    }
    throw await providerHttpError(provider, response, startedAt, context, {
      label: 'Gemini 3.5 Transcribe',
      authStatuses: [401, 403],
      authMessage: 'Gemini API key is invalid or unauthorized',
      failoverOn402: true,
      logDetails: { model },
      attachUnavailableDetails: true,
    });
  }

  let data: InteractionsResponse;
  try {
    data = await response.json();
  } catch {
    throw new ProviderUnavailableError('Gemini 3.5 Transcribe', 'malformed 200 response body', {
      kind: 'bad_response',
    });
  }

  const transcript = collectTranscript(data);
  const { audioTokens, textTokens } = readInputTokens(data.usage);
  // Audio bills at a flat 25 tokens/sec, so the reported audio-token count IS
  // the duration (236 tokens ⇒ 9.44 s, verified). Fall back to a content-type
  // aware byte estimate only when the usage object is missing entirely.
  const durationSeconds = audioTokens > 0
    ? audioTokens / GEMINI_TRANSCRIBE_AUDIO_TOKENS_PER_SECOND
    : estimateAudioSeconds(audio.byteLength, contentType);

  if (!transcript) {
    // NOT the zero-cost no_speech every duration-billed sibling here returns.
    // Those providers bill us per audio minute and we absorb a silent clip as
    // goodwill; this one is TOKEN-billed and Google charges the full 25
    // tokens/sec of input whether or not a word came back. Reporting $0 makes
    // silent audio a free channel to paid upstream requests — one balance funds
    // unlimited calls — so the input half of the bill is real and is charged.
    // Only the output half is genuinely zero: there is no transcript.
    const noSpeechCostUsd = computeGeminiTranscribeCost(model, {
      audioInputTokens: audioTokens,
      textInputTokens: textTokens,
      outputTokens: 0,
      fallbackDurationSeconds: estimateAudioSeconds(audio.byteLength, contentType),
    });
    logProviderEvent(provider, 'no_speech', {
      model,
      elapsedMs: Math.round(performance.now() - startedAt),
      audioTokens,
      durationSeconds,
      costUsd: noSpeechCostUsd,
    }, context);
    return {
      text: '',
      language,
      durationSeconds,
      costUsd: noSpeechCostUsd,
      source: 'no_speech',
    };
  }

  // `usage.total_output_tokens` is 0 on every response from this endpoint, so
  // the output half of the bill is estimated from the transcript we just
  // received. See GEMINI_TRANSCRIBE_RATES in lib/cost-calculator.ts.
  const outputTokens = estimateGeminiTranscribeOutputTokens(transcript);
  const costUsd = computeGeminiTranscribeCost(model, {
    audioInputTokens: audioTokens,
    textInputTokens: textTokens,
    outputTokens,
    fallbackDurationSeconds: estimateAudioSeconds(audio.byteLength, contentType),
  });

  logProviderEvent(provider, 'success', {
    model,
    elapsedMs: Math.round(performance.now() - startedAt),
    transcriptChars: transcript.length,
    audioTokens,
    textTokens,
    estimatedOutputTokens: outputTokens,
    durationSeconds,
    vocabularyTermCount: terms.length,
  }, context);

  return {
    text: transcript,
    language,
    durationSeconds,
    costUsd,
    source: 'gemini-transcribe',
    requestId: data.id,
  };
}
