// GROQ WHISPER PROVIDER
// Fastest and cheapest STT - $0.00185/min using whisper-large-v3

import { computeGroqTranscriptionCost } from '../lib/cost-calculator';
import { EmptyTranscriptError, ProviderUnavailableError } from './types';
import type { ProviderRequestContext, TranscriptionResult } from './types';
import {
  DEFAULT_AUDIO_EXTENSIONS,
  audioExtensionFromContentType,
  explicitLanguageSubtag,
  fetchWithTimeout,
  logProviderEvent,
  providerHttpError,
} from './utils';

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

  const ext = audioExtensionFromContentType(contentType, DEFAULT_AUDIO_EXTENSIONS) ?? 'wav';
  const model = context.model || 'whisper-large-v3-turbo';
  const formData = new FormData();

  formData.append('file', new Blob([audio], { type: contentType }), `audio.${ext}`);
  formData.append('model', model);
  formData.append('response_format', 'verbose_json');

  // Groq's Whisper API expects a bare ISO-639-1 code ("en"), not a hyphenated
  // BCP-47 locale — strip to the primary subtag like the other Whisper-family
  // adapters. (Deepgram is the exception: it accepts BCP-47 region codes.)
  const langCode = explicitLanguageSubtag(language);
  if (langCode !== undefined) {
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
    throw await providerHttpError(provider, response, startTime, context, {
      label: 'Groq',
      authMessage: 'Groq API key is invalid',
    });
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
    // No text, but the upstream itself says it processed audio (`duration` is the
    // length of the SUBMITTED audio, not of detected speech). That is worth one
    // sibling call rather than a "No speech detected" the user has to redo — but
    // only when the ROUTE says a sibling is there to take it. We never inspect
    // `attempt` for that: the chain is the route's to read.
    // (issue ray-amjad/hyperwhisper-app#381)
    const upstreamDurationSeconds = duration > 0 ? duration : null;
    const refusing = upstreamDurationSeconds !== null && context.mayRefuseEmptyTranscript === true;
    // Logged on BOTH paths, refusal included. This event is the only per-provider
    // count of "the upstream returned nothing", and suppressing it on the refusal
    // path would zero it for exactly the providers and exactly the case the
    // feature exists for. `refused` separates the two outcomes in one query.
    logProviderEvent(provider, 'no_speech', {
      elapsedMs: Math.round(performance.now() - startTime),
      language: data.language,
      upstreamDurationSeconds,
      refused: refusing,
    }, context);
    const noSpeechResult: TranscriptionResult = {
      text: '',
      language: data.language,
      durationSeconds: 0,
      costUsd: 0,
      source: 'no_speech',
    };
    if (refusing) {
      throw new EmptyTranscriptError('Groq', {
        upstreamDurationSeconds,
        elapsedMs: Math.round(performance.now() - startTime),
        // The route keeps this as the request's floor, so a sibling that never
        // answers cannot turn a benign no_speech into an error.
        noSpeechResult,
      });
    }
    return noSpeechResult;
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
