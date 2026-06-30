// XAI GROK STT PROVIDER
// xAI Speech to Text REST API - $0.10/hour

import { computeXaiTranscriptionCost } from '../lib/cost-calculator';
import { ProviderInputError, ProviderUnavailableError } from './types';
import type { ProviderRequestContext, TranscriptionResult } from './types';
import { fetchWithTimeout, logProviderEvent, readErrorBodyPreview } from './utils';

const XAI_STT_URL = 'https://api.x.ai/v1/stt';
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

function getExtension(contentType: string): string {
  if (contentType.includes('wav')) return 'wav';
  if (contentType.includes('mp3') || contentType.includes('mpeg')) return 'mp3';
  if (contentType.includes('m4a') || contentType.includes('mp4')) return 'm4a';
  if (contentType.includes('webm')) return 'webm';
  if (contentType.includes('ogg')) return 'ogg';
  if (contentType.includes('flac')) return 'flac';
  return 'mp3';
}

function normalizedFormattingLanguage(language?: string): string | undefined {
  if (!language || language.toLowerCase() === 'auto') {
    return undefined;
  }

  // Strip any BCP-47 region ("en-US" → "en") to the primary subtag before the
  // supported-set check, so a region-tagged locale still matches instead of
  // silently dropping the formatting language.
  const primary = language.toLowerCase().split(/[-_]/)[0];
  const normalized = primary === 'tl' ? 'fil' : primary;
  return SUPPORTED_FORMATTING_LANGUAGES.has(normalized) ? normalized : undefined;
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
  const ext = getExtension(contentType);
  const formData = new FormData();

  if (formattingLanguage) {
    formData.append('format', 'true');
    formData.append('language', formattingLanguage);
  }

  // xAI requires the file part after all other multipart fields.
  formData.append('file', new Blob([audio], { type: contentType }), `audio.${ext}`);

  logProviderEvent(provider, 'prepare', {
    audioBytes: audio.byteLength,
    contentType,
    language: language || 'auto',
    formattingLanguage: formattingLanguage || 'none',
    ignoresInitialPrompt: Boolean(initialPrompt),
  }, context);

  const response = await fetchWithTimeout(provider, XAI_STT_URL, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${apiKey}`,
    },
    body: formData,
  }, context);

  if (!response.ok) {
    const errorText = await readErrorBodyPreview(response);
    const elapsedMs = Math.round(performance.now() - startedAt);
    const kind = response.status >= 500 ? 'upstream_5xx' : response.status === 429 ? 'rate_limit' : 'http_error';

    logProviderEvent(provider, 'http_error', {
      elapsedMs,
      status: response.status,
      kind,
      bodyPreview: errorText,
    }, context);

    if (response.status === 401 || response.status === 403) {
      throw new Error('xAI API key is invalid or unauthorized');
    }
    if (response.status === 429) {
      throw new ProviderUnavailableError('Grok', 'rate limit exceeded');
    }
    if (response.status >= 500) {
      throw new ProviderUnavailableError('Grok', `upstream 5xx: ${response.status}`);
    }

    // Other 4xx (e.g. 400 on an unaccepted language code/format) — a sibling
    // provider may accept this input, so let the chain fall through.
    throw new ProviderInputError('Grok', response.status, errorText || `HTTP ${response.status}`);
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
    logProviderEvent(provider, 'no_speech', {
      elapsedMs: Math.round(performance.now() - startedAt),
      language: data.language,
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
