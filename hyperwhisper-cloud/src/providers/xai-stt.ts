// XAI GROK STT PROVIDER
// xAI Speech to Text REST API
// Model: Grok Voice Transcribe 2.0. The endpoint took no `model` parameter
// before 2026-09-19 and this adapter sent none; it now sends the resolved model
// on every request. Ref: https://x.ai/news/grok-voice-transcribe-2

import { computeXaiTranscriptionCost } from '../lib/cost-calculator';
import { getProviderDef } from '../lib/stt-models';
import { XAI_API_KEY_ENV, readApiKey } from './vendor-api-keys';
import { ProviderUnavailableError } from './types';
import type { ProviderRequestContext, TranscriptionResult } from './types';
import { explicitLanguageSubtag } from './language-codes';
import { emptyTranscriptOutcome } from './provider-attempt';
import { billableSeconds, DEFAULT_AUDIO_EXTENSIONS, audioExtensionFromContentType } from './provider-audio';
import { fetchWithTimeout, providerHttpError, readJson200 } from './provider-http';
import { logProviderEvent } from './provider-log';
import { splitVocabularyTerms } from './vocabulary';

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

/**
 * Split a comma/newline vocabulary prompt into xAI `keyterm` values, sanitized
 * like the canonical `sanitize_vocabulary_word` the BYOK path routes through.
 * An over-long term is dropped, not truncated: 50 characters is xAI's limit.
 */
function toKeyterms(initialPrompt: string): string[] {
  return splitVocabularyTerms(initialPrompt, {
    maxTerms: MAX_KEYTERMS,
    maxTermChars: MAX_KEYTERM_CHARS,
    sanitize: true,
    overLongTerm: 'drop',
  });
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
  const apiKey = readApiKey(XAI_API_KEY_ENV);
  if (!apiKey) {
    throw new ProviderUnavailableError('Grok', 'XAI_API_KEY not configured');
  }

  const formattingLanguage = normalizedFormattingLanguage(language);
  // Always pinned: xAI defaults /v1/stt to its newest model, so an unpinned
  // request would change model on xAI's schedule rather than on our deploy.
  const model = context.model || getProviderDef('grok').defaultModel;
  const ext = audioExtensionFromContentType(contentType, DEFAULT_AUDIO_EXTENSIONS) ?? 'mp3';
  const formData = new FormData();

  formData.append('model', model);

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
    model,
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
      authMessage: 'SpaceXAI API key is invalid or unauthorized',
    });
  }

  // A truncated/invalid 200 body (edge-proxy hiccup) is recoverable by
  // failing over, not by 500ing a request the siblings could serve.
  const data = await readJson200<{
    text?: string;
    duration?: number;
    language?: string;
    words?: Array<{ start?: number; end?: number; text?: string }>;
    id?: string;
    request_id?: string;
  }>(provider, 'Grok', response, startedAt, context);

  const transcript = data.text || '';
  const duration = data.duration
    || data.words?.reduce((max, word) => Math.max(max, typeof word.end === 'number' ? word.end : 0), 0)
    || 0;

  if (!transcript || transcript.trim().length === 0) {
    // No text, but the upstream itself says it processed audio (`duration` is the
    // length of the SUBMITTED audio, not of detected speech). That is worth one
    // sibling call rather than a "No speech detected" the user has to redo — but
    // only when the ROUTE says a sibling is there to take it. We never inspect
    // `attempt` for that: the chain is the route's to read. One shared block for
    // all three covered adapters. See emptyTranscriptOutcome in providers/provider-attempt.
    // (issue ray-amjad/hyperwhisper-app#381)
    return emptyTranscriptOutcome(provider, {
      label: 'Grok',
      startedAt,
      context,
      upstreamDuration: duration,
      upstreamRequestId: data.request_id || data.id,
      logDetails: { language: data.language },
      noSpeechResult: {
        text: '',
        language: data.language || formattingLanguage,
        durationSeconds: 0,
        costUsd: 0,
        source: 'no_speech',
        requestId: data.request_id || data.id,
      },
    });
  }

  const billedSeconds = billableSeconds(duration, audio.byteLength);

  logProviderEvent(provider, 'success', {
    elapsedMs: Math.round(performance.now() - startedAt),
    transcriptChars: transcript.length,
    durationSeconds: billedSeconds,
    language: data.language || formattingLanguage,
  }, context);

  return {
    text: transcript,
    language: data.language || formattingLanguage,
    durationSeconds: billedSeconds,
    costUsd: computeXaiTranscriptionCost(billedSeconds),
    source: 'grok',
    requestId: data.request_id || data.id,
  };
}
