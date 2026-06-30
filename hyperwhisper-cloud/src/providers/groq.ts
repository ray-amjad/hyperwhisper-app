// GROQ WHISPER PROVIDER
// Fastest and cheapest STT - $0.00185/min using whisper-large-v3

import { computeGroqTranscriptionCost } from '../lib/cost-calculator';
import { ProviderInputError, ProviderUnavailableError } from './types';
import type { ProviderRequestContext, TranscriptionResult } from './types';
import { fetchWithTimeout, logProviderEvent, readErrorBodyPreview } from './utils';

/**
 * Get file extension from content type
 */
function getExtension(contentType: string): string {
  if (contentType.includes('wav')) return 'wav';
  if (contentType.includes('mp3') || contentType.includes('mpeg')) return 'mp3';
  if (contentType.includes('m4a') || contentType.includes('mp4')) return 'm4a';
  if (contentType.includes('webm')) return 'webm';
  if (contentType.includes('ogg')) return 'ogg';
  if (contentType.includes('flac')) return 'flac';
  return 'wav';
}

/**
 * Transcribe audio with Groq Whisper large-v3
 */
export async function transcribeWithGroq(
  audio: ArrayBuffer,
  contentType: string,
  language?: string,
  initialPrompt?: string,
  context: ProviderRequestContext = {},
): Promise<TranscriptionResult> {
  const startTime = performance.now();
  const provider = 'groq';
  const apiKey = process.env.GROQ_API_KEY;
  if (!apiKey) {
    throw new Error('GROQ_API_KEY not configured');
  }

  const ext = getExtension(contentType);
  const model = context.model || 'whisper-large-v3-turbo';
  const formData = new FormData();

  formData.append('file', new Blob([audio], { type: contentType }), `audio.${ext}`);
  formData.append('model', model);
  formData.append('response_format', 'verbose_json');

  if (language && language.toLowerCase() !== 'auto') {
    // Groq's Whisper API expects a bare ISO-639-1 code ("en"), not a hyphenated
    // BCP-47 locale — strip to the primary subtag like the other Whisper-family
    // adapters. (Deepgram is the exception: it accepts BCP-47 region codes.)
    const langCode = language.toLowerCase().split(/[-_]/)[0];
    formData.append('language', langCode);
  }
  if (initialPrompt) {
    formData.append('prompt', initialPrompt);
  }

  const formDataMs = performance.now() - startTime;
  logProviderEvent(provider, 'prepare', {
    audioBytes: audio.byteLength,
    contentType,
    language: language || 'auto',
    formDataMs: Math.round(formDataMs),
  }, context);

  const response = await fetchWithTimeout(provider, 'https://api.groq.com/openai/v1/audio/transcriptions', {
    method: 'POST',
    headers: {
      'Authorization': `Bearer ${apiKey}`,
    },
    body: formData,
  }, context);
  const fetchMs = performance.now() - startTime;

  // Handle 403 Forbidden - Groq sometimes blocks edge regions
  if (response.status === 403) {
    logProviderEvent(provider, 'http_error', {
      elapsedMs: Math.round(fetchMs),
      status: response.status,
      kind: 'edge_block',
    }, context);
    throw new ProviderUnavailableError('Groq', '403 Forbidden - likely edge region blocked');
  }

  if (!response.ok) {
    const errorText = await readErrorBodyPreview(response);
    const kind = response.status >= 500 ? 'upstream_5xx' : response.status === 429 ? 'rate_limit' : 'http_error';

    logProviderEvent(provider, 'http_error', {
      elapsedMs: Math.round(fetchMs),
      status: response.status,
      kind,
      bodyPreview: errorText,
    }, context);

    if (response.status === 401) {
      throw new Error('Groq API key is invalid');
    }
    if (response.status === 429) {
      throw new ProviderUnavailableError('Groq', 'rate limit exceeded');
    }
    if (response.status >= 500) {
      throw new ProviderUnavailableError('Groq', `upstream 5xx: ${response.status}`);
    }

    // Other 4xx (e.g. 400 on an unaccepted language code/format) — a sibling
    // provider may accept this input, so let the chain fall through.
    throw new ProviderInputError('Groq', response.status, errorText || `HTTP ${response.status}`);
  }

  let data: {
    text?: string;
    language?: string;
    duration?: number;
  };
  try {
    data = await response.json();
  } catch {
    // A truncated/invalid 200 body (edge-proxy hiccup) is recoverable by
    // failing over, not by 500ing a request the siblings could serve.
    throw new ProviderUnavailableError('Groq', 'malformed 200 response body');
  }

  const duration = data.duration || 0;

  const transcript = data.text || '';

  if (!transcript || transcript.trim().length === 0) {
    logProviderEvent(provider, 'no_speech', {
      elapsedMs: Math.round(performance.now() - startTime),
      language: data.language,
    }, context);
    return {
      text: '',
      language: data.language,
      durationSeconds: 0,
      costUsd: 0,
      source: 'no_speech',
    };
  }

  logProviderEvent(provider, 'success', {
    elapsedMs: Math.round(performance.now() - startTime),
    transcriptChars: transcript.length,
    durationSeconds: duration,
    language: data.language,
    formDataMs: Math.round(formDataMs),
    fetchMs: Math.round(fetchMs),
  }, context);

  return {
    text: transcript,
    language: data.language,
    durationSeconds: duration,
    costUsd: computeGroqTranscriptionCost(duration, model),
    source: 'groq',
  };
}
