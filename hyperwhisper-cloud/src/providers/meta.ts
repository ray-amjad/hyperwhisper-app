// META MUSE VOICE TRANSCRIBE PROVIDER
// Synchronous multipart upload to the Meta Model API batch endpoint.

import { META_MUSE_MAX_BYTES } from '../lib/constants';
import { computeMetaMuseTranscriptionCost } from '../lib/cost-calculator';
import { AudioTooLargeError, ProviderInputError, ProviderUnavailableError, UnsupportedAudioFormatError } from './types';
import type { ProviderRequestContext, TranscriptionResult } from './types';
import {
  computeUploadTimeoutMs,
  fetchWithTimeout,
  isExplicitLanguage,
  logProviderEvent,
  providerHttpError,
  splitVocabularyTerms,
} from './utils';

const META_TRANSCRIBE_URL = 'https://api.meta.ai/v1/asr/transcribe';
const META_MODEL = 'muse-voice-transcribe-1.0';
const META_MAX_DURATION_SECONDS = 10 * 60;
const META_ACCEPTED_FORMATS = ['wav'] as const;

const LANGUAGE_NAME_BY_CODE: Readonly<Record<string, string>> = {
  ar: 'Arabic',
  bn: 'Bengali',
  nl: 'Dutch',
  en: 'English',
  fr: 'French',
  de: 'German',
  he: 'Hebrew',
  hi: 'Hindi',
  id: 'Indonesian',
  it: 'Italian',
  ja: 'Japanese',
  kn: 'Kannada',
  ko: 'Korean',
  ms: 'Malay',
  zh: 'Mandarin Chinese',
  mr: 'Marathi',
  pl: 'Polish',
  pt: 'Portuguese',
  es: 'Spanish',
  fil: 'Tagalog',
  tl: 'Tagalog',
  ta: 'Tamil',
  te: 'Telugu',
  th: 'Thai',
  tr: 'Turkish',
  vi: 'Vietnamese',
};

interface ParsedWav {
  durationSeconds: number;
  sampleRate: 16_000 | 24_000;
}

interface MetaResponse {
  transcript?: unknown;
  audioDurationMs?: unknown;
}

function ascii(view: DataView, offset: number, length: number): string {
  let value = '';
  for (let index = 0; index < length; index++) {
    value += String.fromCharCode(view.getUint8(offset + index));
  }
  return value;
}

function unsupported(contentType: string): UnsupportedAudioFormatError {
  return new UnsupportedAudioFormatError('Meta Muse', contentType, META_ACCEPTED_FORMATS);
}

/** Parse and validate the exact RIFF/WAVE shape accepted by Meta's file API. */
export function parseMetaWav(audio: ArrayBuffer, contentType: string): ParsedWav {
  if (audio.byteLength < 12) throw unsupported(contentType);

  const view = new DataView(audio);
  if (ascii(view, 0, 4) !== 'RIFF' || ascii(view, 8, 4) !== 'WAVE') {
    throw unsupported(contentType);
  }

  const riffEnd = view.getUint32(4, true) + 8;
  if (riffEnd !== audio.byteLength) throw unsupported(contentType);

  let offset = 12;
  let format: {
    audioFormat: number;
    channels: number;
    sampleRate: number;
    byteRate: number;
    blockAlign: number;
    bitsPerSample: number;
  } | undefined;
  let dataBytes: number | undefined;

  while (offset < riffEnd) {
    if (offset + 8 > riffEnd) throw unsupported(contentType);
    const chunkId = ascii(view, offset, 4);
    const chunkSize = view.getUint32(offset + 4, true);
    const chunkDataStart = offset + 8;
    const chunkDataEnd = chunkDataStart + chunkSize;
    const paddedChunkEnd = chunkDataEnd + (chunkSize & 1);
    if (chunkDataEnd > riffEnd || paddedChunkEnd > riffEnd) throw unsupported(contentType);

    if (chunkId === 'fmt ') {
      if (format || chunkSize < 16) throw unsupported(contentType);
      format = {
        audioFormat: view.getUint16(chunkDataStart, true),
        channels: view.getUint16(chunkDataStart + 2, true),
        sampleRate: view.getUint32(chunkDataStart + 4, true),
        byteRate: view.getUint32(chunkDataStart + 8, true),
        blockAlign: view.getUint16(chunkDataStart + 12, true),
        bitsPerSample: view.getUint16(chunkDataStart + 14, true),
      };
    } else if (chunkId === 'data') {
      if (dataBytes !== undefined) throw unsupported(contentType);
      dataBytes = chunkSize;
    }

    offset = paddedChunkEnd;
  }

  if (!format || dataBytes === undefined || dataBytes === 0) throw unsupported(contentType);
  const expectedBlockAlign = format.channels * (format.bitsPerSample / 8);
  const expectedByteRate = format.sampleRate * expectedBlockAlign;
  if (
    format.audioFormat !== 1
    || format.channels !== 1
    || format.bitsPerSample !== 16
    || (format.sampleRate !== 16_000 && format.sampleRate !== 24_000)
    || format.blockAlign !== expectedBlockAlign
    || format.byteRate !== expectedByteRate
    || dataBytes % format.blockAlign !== 0
  ) {
    throw unsupported(contentType);
  }

  const durationSeconds = dataBytes / format.byteRate;
  if (durationSeconds > META_MAX_DURATION_SECONDS) {
    throw new ProviderInputError('Meta Muse', 400, 'audio exceeds the 10 minute limit');
  }

  return {
    durationSeconds,
    sampleRate: format.sampleRate as 16_000 | 24_000,
  };
}

function languageBias(language: string | undefined): string[] | undefined {
  if (!isExplicitLanguage(language)) return undefined;
  const code = language.trim().toLowerCase().split(/[-_]/)[0] ?? '';
  const name = LANGUAGE_NAME_BY_CODE[code];
  return name ? [name] : undefined;
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
  const bias = languageBias(language);
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
