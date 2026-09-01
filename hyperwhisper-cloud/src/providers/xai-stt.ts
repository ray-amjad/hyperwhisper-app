// XAI GROK STT PROVIDER
// xAI Speech to Text REST API - $0.10/hour

import { computeXaiTranscriptionCost } from '../lib/cost-calculator';
import { ProviderUnavailableError } from './types';
import type { ProviderRequestContext, TranscriptionResult } from './types';
import {
  DEFAULT_AUDIO_EXTENSIONS,
  audioExtensionFromContentType,
  explicitLanguageSubtag,
  fetchWithTimeout,
  logProviderEvent,
  providerHttpError,
} from './utils';

const XAI_STT_URL = 'https://api.x.ai/v1/stt';
// xAI keyterm limits: max 100 terms, each up to 50 characters.
const MAX_KEYTERMS = 100;
const MAX_KEYTERM_CHARS = 50;
const SUPPORTED_FORMATTING_LANGUAGES = new Set([
  'ar',
  'cs',
  'da',
  'de',
  'en',
  'es',
  'fa',
  'fil',
  'fr',
  'hi',
  'id',
  'it',
  'ja',
  'ko',
  'mk',
  'ms',
  'nl',
  'pl',
  'pt',
  'ro',
  'ru',
  'sv',
  'th',
  'tr',
  'vi',
]);

function normalizedFormattingLanguage(language?: string): string | undefined {
  // Strip any BCP-47 region ("en-US" → "en") to the primary subtag before the
  // supported-set check, so a region-tagged locale still matches instead of
  // silently dropping the formatting language.
  const primary = explicitLanguageSubtag(language);
  if (primary === undefined) {
    return undefined;
  }

  const normalized = primary === 'tl' ? 'fil' : primary;
  return SUPPORTED_FORMATTING_LANGUAGES.has(normalized) ? normalized : undefined;
}

/** Split a comma/newline vocabulary prompt into xAI `keyterm` values. */
function toKeyterms(initialPrompt: string): string[] {
  const seen = new Set<string>();
  const terms: string[] = [];
  for (const raw of initialPrompt.split(/[,\n;]+/)) {
    // Strip angle brackets and collapse whitespace runs, matching the canonical
    // `sanitize_vocabulary_word` the BYOK path routes through — an imported
    // backup can carry either.
    const term = raw
      .trim()
      .replace(/^[-*]\s*/, '')
      .replace(/[<>]/g, '')
      .replace(/\s+/g, ' ')
      .trim();
    if (term.length === 0 || term.length > MAX_KEYTERM_CHARS) continue;
    const key = term.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    terms.push(term);
    if (terms.length === MAX_KEYTERMS) break;
  }
  return terms;
}

export async function transcribeWithXaiGrok(
  audio: ArrayBuffer,
  contentType: string,
  language?: string,
  initialPrompt?: string,
  context: ProviderRequestContext = {},
): Promise<TranscriptionResult> {
  const startedAt = performance.now();
  const provider = 'grok';
  const apiKey = process.env.XAI_API_KEY || process.env.GROK_API_KEY;
  if (!apiKey) {
    throw new ProviderUnavailableError('Grok', 'XAI_API_KEY not configured');
  }

  const formattingLanguage = normalizedFormattingLanguage(language);
  const ext = audioExtensionFromContentType(contentType, DEFAULT_AUDIO_EXTENSIONS) ?? 'mp3';
  const formData = new FormData();

  if (formattingLanguage) {
    formData.append('format', 'true');
    formData.append('language', formattingLanguage);
  }

  // keyterm is a REPEATED field — one append per term, not a joined string.
  // Ref: docs.x.ai speech-to-text ("Repeat the parameter for multiple terms.
  // Max 100 terms, each up to 50 characters.")
  const keyterms = initialPrompt ? toKeyterms(initialPrompt) : [];
  for (const term of keyterms) {
    formData.append('keyterm', term);
  }

  // xAI requires the file part after all other multipart fields.
  formData.append('file', new Blob([audio], { type: contentType }), `audio.${ext}`);

  logProviderEvent(provider, 'prepare', {
    audioBytes: audio.byteLength,
    contentType,
    language: language || 'auto',
    formattingLanguage: formattingLanguage || 'none',
    keytermCount: keyterms.length,
  }, context);

  const response = await fetchWithTimeout(provider, XAI_STT_URL, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${apiKey}`,
    },
    body: formData,
  }, context);

  if (!response.ok) {
    throw await providerHttpError(provider, response, startedAt, context, {
      label: 'Grok',
      authStatuses: [401, 403],
      authMessage: 'xAI API key is invalid or unauthorized',
    });
  }

  let data: {
    text?: string;
    duration?: number;
    language?: string;
    words?: Array<{ start?: number; end?: number; text?: string }>;
    id?: string;
    request_id?: string;
  };
  try {
    data = await response.json();
  } catch {
    // A truncated/invalid 200 body (edge-proxy hiccup) is recoverable by
    // failing over, not by 500ing a request the siblings could serve.
    throw new ProviderUnavailableError('Grok', 'malformed 200 response body');
  }

  const transcript = data.text || '';
  const duration = data.duration
    || data.words?.reduce((max, word) => Math.max(max, typeof word.end === 'number' ? word.end : 0), 0)
    || 0;

  if (!transcript || transcript.trim().length === 0) {
    // No text, but the upstream itself says it processed audio (`duration` is the
    // length of the SUBMITTED audio, not of detected speech). On the first attempt
    // that is worth one sibling call rather than a "No speech detected" the user
    // has to redo. Gated on attempt 1 so a request costs at most one extra call,
    // and elevenlabs — which terminates every covered chain — never refuses.
    // (issue ray-amjad/hyperwhisper-app#381)
    if (duration > 0 && context.attempt === 1) {
      throw new ProviderUnavailableError(
        'Grok',
        `empty transcript for ${duration}s of audio`,
        { kind: 'bad_response', elapsedMs: Math.round(performance.now() - startedAt) },
      );
    }
    logProviderEvent(provider, 'no_speech', {
      elapsedMs: Math.round(performance.now() - startedAt),
      language: data.language,
      upstreamDurationSeconds: duration > 0 ? duration : null,
    }, context);
    return {
      text: '',
      language: data.language || formattingLanguage,
      durationSeconds: 0,
      costUsd: 0,
      source: 'no_speech',
      requestId: data.request_id || data.id,
    };
  }

  logProviderEvent(provider, 'success', {
    elapsedMs: Math.round(performance.now() - startedAt),
    transcriptChars: transcript.length,
    durationSeconds: duration,
    language: data.language || formattingLanguage,
  }, context);

  return {
    text: transcript,
    language: data.language || formattingLanguage,
    durationSeconds: duration,
    costUsd: computeXaiTranscriptionCost(duration),
    source: 'grok',
    requestId: data.request_id || data.id,
  };
}
