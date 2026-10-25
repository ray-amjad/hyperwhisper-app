// MISTRAL VOXTRAL PROVIDER
// Synchronous multipart transcription ($0.003/audio-min). Vocabulary biasing
// uses the structured `context_bias` list (≤100 phrases), not a free prompt.

import { computeMistralTranscriptionCost } from '../lib/cost-calculator';
import { ProviderUnavailableError } from './types';
import type { ProviderRequestContext, TranscriptionResult } from './types';
import {
  DEFAULT_AUDIO_EXTENSIONS,
  audioExtensionFromContentType,
  estimateSecondsFromBytes,
  explicitLanguageSubtag,
  fetchWithTimeout,
  logProviderEvent,
  providerHttpError,
  splitVocabularyTerms,
} from './utils';

const MISTRAL_URL = 'https://api.mistral.ai/v1/audio/transcriptions';
const DEFAULT_MODEL = 'voxtral-mini-latest';
const MAX_CONTEXT_BIAS_TERMS = 100;
const MAX_CONTEXT_BIAS_TERM_CHARS = 80;

/** Voxtral additionally takes raw AAC, which the other adapters don't name. */
const MISTRAL_AUDIO_EXTENSIONS = [...DEFAULT_AUDIO_EXTENSIONS, 'aac'] as const;

/**
 * Split a comma/newline vocabulary prompt into ≤100 `context_bias` phrases.
 *
 * Inner whitespace collapses to `_` because Voxtral validates every item under
 * `context_bias_input_method=comma_separated` (its server-side default) and 400s
 * the WHOLE request when one item holds a comma or any whitespace — a single
 * multi-word term like "Claude Code" then kills the transcription, not just that
 * term, and a 400 is not a failover status so no sibling provider covers it.
 * Underscores are the documented way to bias a phrase (docs.mistral.ai
 * audio/speech_to_text → `["affordable_health_care", "American_people"]`), so
 * joining beats dropping the term.
 *
 * Returns the phrases as the user wrote them (whitespace collapsed to one space)
 * and as sent, index for index, so `restoreSpacedTerms` can map one to the other.
 */
function toContextBias(initialPrompt: string): { spaced: string[]; sent: string[] } {
  const spaced = splitVocabularyTerms(initialPrompt, {
    maxTerms: MAX_CONTEXT_BIAS_TERMS,
    maxTermChars: MAX_CONTEXT_BIAS_TERM_CHARS,
    joinWordsWith: ' ',
  });
  return { spaced, sent: spaced.map((term) => term.replace(/\s+/g, '_')) };
}

/**
 * Voxtral writes a biased phrase back the way it was sent: "HyperWhisper_Cloud"
 * for the term "HyperWhisper Cloud" (seen live on `voxtral-mini-latest` on
 * 2026-10-25, after Mistral's 2026-10-20 Voxtral 3 release, #2544). Put the
 * user's spaces back, and nothing else:
 * - only a term that HAD whitespace is rewritten, so `__init__` passes through,
 *   and a joined form the user also listed as written (`snake_case` beside
 *   "Snake Case") is left alone;
 * - only a whole token, exact case, so `user_id_field` keeps its underscores
 *   when "user id" is in the list;
 * - one pass, longest first, so restored text is never matched again.
 */
function restoreSpacedTerms(text: string, bias: { spaced: string[]; sent: string[] }): string {
  const writtenWithUnderscore = new Set(bias.spaced.filter((term, i) => term === bias.sent[i]));
  const spacedBySent = new Map<string, string>();
  bias.spaced.forEach((spaced, i) => {
    const sent = bias.sent[i];
    if (spaced !== sent && !writtenWithUnderscore.has(sent) && !spacedBySent.has(sent)) {
      spacedBySent.set(sent, spaced);
    }
  });
  if (spacedBySent.size === 0) return text;

  const alternatives = [...spacedBySent.keys()]
    .sort((x, y) => y.length - x.length)
    .map((sent) => sent.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'));
  const pattern = new RegExp(`(?<![\\p{L}\\p{N}_])(?:${alternatives.join('|')})(?![\\p{L}\\p{N}_])`, 'gu');
  return text.replace(pattern, (sent) => spacedBySent.get(sent) ?? sent);
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

  const ext = audioExtensionFromContentType(contentType, MISTRAL_AUDIO_EXTENSIONS) ?? 'wav';
  const formData = new FormData();
  formData.append('file', new Blob([audio], { type: contentType }), `audio.${ext}`);
  formData.append('model', model);

  // Voxtral expects a bare ISO-639-1 code ("en"), not a hyphenated BCP-47
  // locale — strip to the primary subtag like the sibling adapters.
  const langCode = explicitLanguageSubtag(language);
  if (langCode !== undefined) {
    formData.append('language', langCode);
  }

  const contextBias = initialPrompt ? toContextBias(initialPrompt) : { spaced: [], sent: [] };
  if (contextBias.sent.length) {
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
    for (const term of contextBias.sent) {
      formData.append('context_bias', term);
    }
  }

  logProviderEvent(provider, 'prepare', {
    model,
    audioBytes: audio.byteLength,
    contentType,
    language: language || 'auto',
    contextBiasCount: contextBias.sent.length,
  }, context);

  const response = await fetchWithTimeout(provider, MISTRAL_URL, {
    method: 'POST',
    headers: { Authorization: `Bearer ${apiKey}` },
    body: formData,
  }, context);

  if (!response.ok) {
    throw await providerHttpError(provider, response, startedAt, context, {
      label: 'Mistral',
      authStatuses: [401, 403],
      authMessage: 'Mistral API key is invalid or unauthorized',
      failoverOn402: true,
      logDetails: { model },
    });
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

  const transcript = restoreSpacedTerms(data.text || '', contextBias);
  const rawDurationSeconds = data.usage?.prompt_audio_seconds || 0;

  if (!transcript || transcript.trim().length === 0) {
    logProviderEvent(provider, 'no_speech', {
      model, elapsedMs: Math.round(performance.now() - startedAt), language: data.language,
      upstreamDurationSeconds: rawDurationSeconds > 0 ? rawDurationSeconds : null,
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
