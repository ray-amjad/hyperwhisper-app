// META MUSE VOICE TRANSCRIBE PROVIDER
// Synchronous multipart upload to the Meta Model API batch endpoint.

import { META_MUSE_MAX_BYTES } from '../lib/constants';
import { computeMetaMuseTranscriptionCost } from '../lib/cost-calculator';
import { resolveProviderLanguage } from '../lib/language-codes';
import { parsePcm16Wav } from '../lib/wav-header';
import { AudioTooLargeError, ProviderUnavailableError } from './types';
import type { ProviderRequestContext, TranscriptionResult } from './types';
import {
  computeUploadTimeoutMs,
  fetchWithTimeout,
  logProviderEvent,
  providerHttpError,
  splitVocabularyTerms,
} from './utils';

const META_TRANSCRIBE_URL = 'https://api.meta.ai/v1/asr/transcribe';
const META_MODEL = 'muse-voice-transcribe-1.0';
const META_MAX_DURATION_SECONDS = 10 * 60;
const META_ACCEPTED_FORMATS = ['wav'] as const;

interface ParsedWav {
  durationSeconds: number;
  sampleRate: 16_000 | 24_000;
}

interface MetaResponse {
  transcript?: unknown;
  audioDurationMs?: unknown;
}

/** Parse and validate the exact RIFF/WAVE shape accepted by Meta's file API. */
export function parseMetaWav(audio: ArrayBuffer, contentType: string): ParsedWav {
  const { durationSeconds, sampleRate } = parsePcm16Wav(audio, contentType, {
    provider: 'Meta Muse',
    acceptedFormats: META_ACCEPTED_FORMATS,
    channels: 1,
    sampleRates: [16_000, 24_000],
    maxDurationSeconds: META_MAX_DURATION_SECONDS,
    tooLongMessage: 'audio exceeds the 10 minute limit',
  });
  return { durationSeconds, sampleRate: sampleRate as ParsedWav['sampleRate'] };
}

export async function transcribeWithMeta(
  audio: ArrayBuffer,
  contentType: string,
  language?: string,
  initialPrompt?: string,
  context: ProviderRequestContext = {},
): Promise<TranscriptionResult> {
  const startedAt = performance.now();
  const provider = 'meta';
  const model = context.model || META_MODEL;

  const wav = parseMetaWav(audio, contentType);
  if (audio.byteLength > META_MUSE_MAX_BYTES) {
    throw new AudioTooLargeError('Meta Muse', audio.byteLength, META_MUSE_MAX_BYTES);
  }
  const apiKey = process.env.META_MODEL_API_KEY;
  if (!apiKey) throw new Error('META_MODEL_API_KEY not configured');

  const keywords = initialPrompt
    ? splitVocabularyTerms(initialPrompt, { maxTerms: 100, maxTermChars: 80 })
    : [];
  const resolvedLanguage = resolveProviderLanguage({
    provider,
    model,
    language,
    context,
  });
  const bias = resolvedLanguage ? [resolvedLanguage] : undefined;
  const request: Record<string, unknown> = {
    model,
    audioEncoding: 'WAV',
    mode: 'PUSH_TO_TALK',
  };
  if (keywords.length > 0) request.keywords = keywords;
  if (bias) request.languageBias = bias;

  const form = new FormData();
  form.append('request', new Blob([JSON.stringify(request)], { type: 'application/json' }), 'request.json');
  form.append('audio', new Blob([audio], { type: 'audio/wav' }), 'audio.wav');

  logProviderEvent(provider, 'prepare', {
    model,
    audioBytes: audio.byteLength,
    durationSeconds: wav.durationSeconds,
    sampleRate: wav.sampleRate,
    language: language || 'auto',
    languageBias: bias ?? [],
    keywordCount: keywords.length,
  }, context);

  const response = await fetchWithTimeout(provider, META_TRANSCRIBE_URL, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${apiKey}`,
      Accept: 'application/json',
    },
    body: form,
  }, context, computeUploadTimeoutMs(audio.byteLength));

  if (!response.ok) {
    throw await providerHttpError(provider, response, startedAt, context, {
      label: 'Meta Muse',
      authStatuses: [401, 403],
      authMessage: 'Meta Model API key is invalid or unauthorized',
      attachUnavailableDetails: true,
      logDetails: { model },
    });
  }

  let data: MetaResponse;
  try {
    data = await response.json() as MetaResponse;
  } catch {
    throw new ProviderUnavailableError('Meta Muse', 'malformed 200 response body', { kind: 'bad_response' });
  }

  if (
    typeof data.transcript !== 'string'
    || typeof data.audioDurationMs !== 'number'
    || !Number.isFinite(data.audioDurationMs)
    || data.audioDurationMs < 0
  ) {
    throw new ProviderUnavailableError('Meta Muse', 'malformed 200 response fields', { kind: 'bad_response' });
  }

  const text = data.transcript.trim();
  const durationSeconds = data.audioDurationMs / 1000;
  if (!text) {
    return { text: '', durationSeconds: 0, costUsd: 0, source: 'no_speech' };
  }
  if (!(durationSeconds > 0)) {
    throw new ProviderUnavailableError('Meta Muse', 'malformed audio duration', { kind: 'bad_response' });
  }

  return {
    text,
    durationSeconds,
    costUsd: computeMetaMuseTranscriptionCost(durationSeconds),
    source: 'meta',
  };
}
