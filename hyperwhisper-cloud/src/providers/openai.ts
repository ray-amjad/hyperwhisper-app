// OPENAI TRANSCRIPTION PROVIDER
// Synchronous multipart. whisper-1 is duration-billed and returns verbose_json
// (text + language + duration); gpt-4o-transcribe / gpt-4o-mini-transcribe are
// token-billed and only support response_format=json (no duration/language
// echoed), so cost comes from the `usage` token counts and duration is
// estimated from payload size for telemetry only.

import { computeOpenAITranscriptionCost } from '../lib/cost-calculator';
import { OPENAI_INLINE_MAX_BYTES } from '../lib/constants';
import { AudioTooLargeError, ProviderUnavailableError } from './types';
import type { ProviderRequestContext, TranscriptionResult } from './types';
import {
  DEFAULT_AUDIO_EXTENSIONS,
  audioExtensionFromContentType,
  estimateSecondsFromBytes,
  fetchWithTimeout,
  logProviderEvent,
  providerHttpError,
} from './utils';

const OPENAI_URL = 'https://api.openai.com/v1/audio/transcriptions';
const DEFAULT_MODEL = 'gpt-4o-transcribe';

// Models that accept a structured keyword list. gpt-live-transcribe is
// realtime-only and never reaches this synchronous adapter.
const KEYWORDS_MODELS = new Set(['gpt-transcribe']);
// OpenAI encodes array parameters on this endpoint with a trailing `[]`, one
// field per element — the same shape as the documented
// `timestamp_granularities[]`.
const KEYWORDS_FIELD = 'keywords[]';
const MAX_KEYWORDS = 100;
// Per-term ceiling, matching MAX_VOCABULARY_TERM_CHARS in the Rust core's
// `sanitize_vocabulary_word` — the canonical sanitizer the BYOK half of this
// feature routes through. Without it a pasted 400-character sentence went
// upstream as one "keyword", and every sibling adapter here caps term length.
// Like the canonical sanitizer, an over-long term is TRUNCATED, not dropped.
const MAX_KEYWORD_CHARS = 80;

/** Split a comma/newline vocabulary prompt into `keywords[]` values. */
function toKeywords(initialPrompt: string): string[] {
  const seen = new Set<string>();
  const keywords: string[] = [];
  for (const raw of initialPrompt.split(/[,\n;]+/)) {
    // OpenAI documents that a keyword must be one line with no `<`, `>`, CR or LF.
    // Collapsing whitespace runs matches the canonical sanitizer and keeps a
    // multi-line paste from becoming one unusable hint.
    const keyword = raw
      .trim()
      // Strip a list bullet ONLY when a space follows it. Without that space a
      // leading "-" or "*" is part of the term — "-Xmx", "*args" — and stripping
      // it silently biases the model toward a different token than the user
      // entered, which the BYOK path does not do.
      .replace(/^[-*]\s+/, '')
      .replace(/[<>]/g, '')
      .replace(/\s+/g, ' ')
      .trim()
      // Truncate rather than drop, matching `sanitize_vocabulary_word`, and by
      // code POINT so a term of supplementary characters is not cut early.
      .slice(0, MAX_KEYWORD_CHARS);
    if (keyword.length === 0) continue;
    const key = keyword.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    keywords.push(keyword);
    if (keywords.length === MAX_KEYWORDS) break;
  }
  return keywords;
}

interface OpenAIUsage {
  type?: string;
  seconds?: number;
  input_tokens?: number;
  output_tokens?: number;
}

export async function transcribeWithOpenAI(
  audio: ArrayBuffer,
  contentType: string,
  language?: string,
  initialPrompt?: string,
  context: ProviderRequestContext = {},
): Promise<TranscriptionResult> {
  const startedAt = performance.now();
  const provider = 'openai';
  const model = context.model || DEFAULT_MODEL;
  const isWhisper = model === 'whisper-1';

  const apiKey = process.env.OPENAI_API_KEY;
  if (!apiKey) {
    throw new Error('OPENAI_API_KEY not configured');
  }

  if (audio.byteLength > OPENAI_INLINE_MAX_BYTES) {
    throw new AudioTooLargeError('OpenAI', audio.byteLength, OPENAI_INLINE_MAX_BYTES);
  }

  const ext = audioExtensionFromContentType(contentType, DEFAULT_AUDIO_EXTENSIONS) ?? 'wav';
  const formData = new FormData();
  formData.append('file', new Blob([audio], { type: contentType }), `audio.${ext}`);
  formData.append('model', model);
  // gpt-4o-* models reject verbose_json; only whisper-1 supports it (and only it
  // returns a duration we can bill on).
  formData.append('response_format', isWhisper ? 'verbose_json' : 'json');

  if (language && language.toLowerCase() !== 'auto') {
    // OpenAI's `language` hint expects an ISO-639-1 code (e.g. "en"/"pt"), not a
    // region-qualified BCP-47 tag — strip any region/script subtag so a
    // client-supplied "en-US"/"pt-BR" isn't rejected for this self-only provider.
    const langCode = language.toLowerCase().split(/[-_]/)[0];
    formData.append('language', langCode);
  }
  // gpt-transcribe takes a STRUCTURED keyword list; every other model only has
  // the free-text `prompt`. Sending the terms as keywords keeps them hints
  // rather than part of the instruction.
  const useKeywords = KEYWORDS_MODELS.has(model);
  const keywords = useKeywords && initialPrompt ? toKeywords(initialPrompt) : [];
  for (const keyword of keywords) {
    formData.append(KEYWORDS_FIELD, keyword);
  }
  if (initialPrompt && !useKeywords) {
    formData.append('prompt', initialPrompt);
  }

  logProviderEvent(provider, 'prepare', {
    model,
    audioBytes: audio.byteLength,
    contentType,
    language: language || 'auto',
    hasPrompt: Boolean(initialPrompt) && !useKeywords,
    keywordCount: keywords.length,
  }, context);

  const response = await fetchWithTimeout(provider, OPENAI_URL, {
    method: 'POST',
    headers: { Authorization: `Bearer ${apiKey}` },
    body: formData,
  }, context);

  if (!response.ok) {
    throw await providerHttpError(provider, response, startedAt, context, {
      label: 'OpenAI',
      authStatuses: [401, 403],
      authMessage: 'OpenAI API key is invalid or unauthorized',
      failoverOn402: true,
      logDetails: { model },
    });
  }

  let data: { text?: string; language?: string; duration?: number; usage?: OpenAIUsage };
  try {
    data = await response.json();
  } catch {
    throw new ProviderUnavailableError('OpenAI', 'malformed 200 response body');
  }

  const transcript = data.text || '';
  // whisper-1 verbose_json gives a real duration; gpt-4o gives only tokens, so
  // estimate seconds from payload for telemetry (billing uses tokens regardless).
  const rawWhisperDuration = data.duration ?? data.usage?.seconds ?? 0;
  let durationSeconds = isWhisper
    ? rawWhisperDuration
    : estimateSecondsFromBytes(audio.byteLength);

  if (!transcript || transcript.trim().length === 0) {
    logProviderEvent(provider, 'no_speech', {
      model, elapsedMs: Math.round(performance.now() - startedAt), language: data.language,
    }, context);
    return { text: '', language: data.language, durationSeconds: 0, costUsd: 0, source: 'no_speech' };
  }

  // Fail-closed: whisper-1 is duration-billed, so a successful transcript with a
  // missing/non-positive duration falls back to a byte-size estimate (never $0).
  // The gpt-4o branch already estimates from bytes above.
  if (isWhisper && !(durationSeconds > 0 && Number.isFinite(durationSeconds))) {
    durationSeconds = estimateSecondsFromBytes(audio.byteLength);
  }

  const costUsd = computeOpenAITranscriptionCost(model, {
    durationSeconds,
    inputTokens: data.usage?.input_tokens,
    outputTokens: data.usage?.output_tokens,
  });

  logProviderEvent(provider, 'success', {
    model,
    elapsedMs: Math.round(performance.now() - startedAt),
    transcriptChars: transcript.length,
    durationSeconds,
    inputTokens: data.usage?.input_tokens,
    outputTokens: data.usage?.output_tokens,
    language: data.language,
  }, context);

  return {
    text: transcript,
    language: data.language,
    durationSeconds,
    costUsd,
    source: 'openai',
  };
}
