// MICROSOFT MAI-TRANSCRIBE 1.5 PROVIDER (Azure Speech / Foundry)
// Multilingual transcription model — 43 languages, phrase-list biasing, $0.006/min.
//
// API: POST https://<resource>.cognitiveservices.azure.com/speechtotext/transcriptions:transcribe?api-version=2025-10-15
// Auth: Ocp-Apim-Subscription-Key
//
// Request shape: multipart/form-data with two parts
//   - `audio`: the binary audio with the original Content-Type
//   - `definition`: JSON describing the enhanced model + locales + phraseList
//
// IMPORTANT: the `definition` part MUST be sent as application/json. The
// service rejects text/plain JSON with HTTP 400.

import { AZURE_MAI_MAX_BYTES } from '../lib/constants';
import { computeAzureMaiTranscriptionCost } from '../lib/cost-calculator';
import { AudioTooLargeError, ProviderUnavailableError, UnsupportedAudioFormatError } from './types';
import type { ProviderRequestContext, TranscriptionResult } from './types';
import { computeUploadTimeoutMs, fetchWithTimeout, logProviderEvent, readErrorBodyPreview } from './utils';

const MAX_PHRASES = 100;
const MAX_PHRASE_LEN = 50;
// MAI-Transcribe 1.5 documents only WAV, MP3, and FLAC as accepted upload
// formats. Anything else (m4a, mp4, webm, opus, ogg, aac, wma) is rejected
// upstream with a generic error; we surface a typed 415 instead so the
// client can re-encode.
// Ref: https://learn.microsoft.com/en-us/azure/ai-services/speech-service/mai-transcribe
const AZURE_MAI_ACCEPTED_FORMATS = ['wav', 'mp3', 'flac'] as const;

function getExtension(contentType: string): 'wav' | 'mp3' | 'flac' {
  const lower = contentType.toLowerCase();
  if (lower.includes('wav')) return 'wav';
  if (lower.includes('mp3') || lower.includes('mpeg')) return 'mp3';
  if (lower.includes('flac')) return 'flac';
  throw new UnsupportedAudioFormatError('Azure MAI', contentType, AZURE_MAI_ACCEPTED_FORMATS);
}

function parsePhraseList(initialPrompt: string): string[] {
  return initialPrompt
    .split(/[,\n;]+/)
    .map(t => t.trim().replace(/^[-*]\s*/, ''))
    .filter(t => t.length > 0 && t.length <= MAX_PHRASE_LEN)
    .slice(0, MAX_PHRASES);
}

function normalizeLocale(language: string): string {
  // MAI-Transcribe 1.5 wants two-letter language codes (`en`, `ja`, `fr`).
  // The wider Fast-Transcription API accepts BCP-47 (`en-US`) but the MAI
  // doc explicitly uses 2-letter; strip the region subtag so we match docs
  // and stay forward-compatible if Azure tightens validation.
  return language.toLowerCase().split('-')[0];
}

// MAI-Transcribe 1.5 is available in 4 Azure regions: eastus, westus,
// northeurope, southeastasia. We provision 3 (skip westus — eastus covers
// CONUS well enough for v1) and pick based on the Fly machine region so
// each request hits the geographically nearest Azure endpoint.
type AzureMaiRegion = 'eastus' | 'northeurope' | 'southeastasia';

const APAC_FLY_REGIONS = new Set(['hkg', 'nrt', 'sin', 'syd', 'maa']);
const EU_FLY_REGIONS = new Set(['ams', 'arn', 'cdg', 'fra', 'lhr', 'mad', 'otp', 'waw', 'jnb']);

function pickAzureRegion(): AzureMaiRegion {
  const flyRegion = (process.env.FLY_REGION || '').toLowerCase();
  let preferred: AzureMaiRegion = 'eastus';
  if (APAC_FLY_REGIONS.has(flyRegion)) preferred = 'southeastasia';
  else if (EU_FLY_REGIONS.has(flyRegion)) preferred = 'northeurope';
  // If the preferred region isn't provisioned (e.g. dev only has eastus, or
  // a regional resource was revoked), fall back to eastus rather than 502.
  if (preferred !== 'eastus' && !getRegionKey(preferred) && getRegionKey('eastus')) {
    return 'eastus';
  }
  return preferred;
}

function getRegionKey(region: AzureMaiRegion): string | undefined {
  switch (region) {
    case 'eastus': return process.env.AZURE_SPEECH_KEY_EASTUS;
    case 'northeurope': return process.env.AZURE_SPEECH_KEY_NORTHEUROPE;
    case 'southeastasia': return process.env.AZURE_SPEECH_KEY_SOUTHEASTASIA;
  }
}

export async function transcribeWithAzureMai(
  audio: ArrayBuffer,
  contentType: string,
  language?: string,
  initialPrompt?: string,
  context: ProviderRequestContext = {},
): Promise<TranscriptionResult> {
  const startedAt = performance.now();
  const provider = 'azure-mai';

  if (audio.byteLength > AZURE_MAI_MAX_BYTES) {
    logProviderEvent(provider, 'audio_too_large', {
      audioBytes: audio.byteLength,
      maxBytes: AZURE_MAI_MAX_BYTES,
    }, context);
    throw new AudioTooLargeError('Azure MAI', audio.byteLength, AZURE_MAI_MAX_BYTES);
  }

  let ext: 'wav' | 'mp3' | 'flac';
  try {
    ext = getExtension(contentType);
  } catch (error) {
    if (error instanceof UnsupportedAudioFormatError) {
      logProviderEvent(provider, 'unsupported_audio_format', {
        contentType,
        acceptedFormats: AZURE_MAI_ACCEPTED_FORMATS,
      }, context);
    }
    throw error;
  }

  const azureRegion = pickAzureRegion();
  const apiKey = getRegionKey(azureRegion);
  if (!apiKey) {
    throw new Error(`AZURE_SPEECH_KEY_${azureRegion.toUpperCase()} not configured`);
  }

  const url = `https://${azureRegion}.api.cognitive.microsoft.com/speechtotext/transcriptions:transcribe?api-version=2025-10-15`;

  const isMonolingual = language && language.toLowerCase() !== 'auto';
  const phrases = initialPrompt ? parsePhraseList(initialPrompt) : [];

  const definition: Record<string, unknown> = {
    enhancedMode: {
      enabled: true,
      model: 'mai-transcribe-1.5',
    },
  };
  if (isMonolingual) {
    definition.locales = [normalizeLocale(language!)];
  }
  if (phrases.length > 0) {
    definition.phraseList = { phrases };
  }

  const form = new FormData();
  // Azure Fast Transcription docs specify `application/octet-stream` for the
  // audio part; format is inferred from the filename extension. Some original
  // browser MIME types (e.g. `audio/webm; codecs=opus`) have tripped server-
  // side validation in the past — octet-stream + filename is the safe shape.
  form.append('audio', new Blob([audio], { type: 'application/octet-stream' }), `audio.${ext}`);
  // `definition` MUST land as application/json — Azure rejects text/plain.
  form.append(
    'definition',
    new Blob([JSON.stringify(definition)], { type: 'application/json' }),
    'definition.json',
  );

  logProviderEvent(provider, 'prepare', {
    audioBytes: audio.byteLength,
    contentType,
    language: language || 'auto',
    phraseCount: phrases.length,
    azureRegion,
    flyRegion: process.env.FLY_REGION,
  }, context);

  const response = await fetchWithTimeout(provider, url, {
    method: 'POST',
    headers: {
      'Ocp-Apim-Subscription-Key': apiKey,
    },
    body: form,
  }, context, computeUploadTimeoutMs(audio.byteLength));

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

    if (response.status === 401) {
      throw new Error('Azure Speech subscription key is invalid or expired');
    }
    if (response.status === 403) {
      // 403 from Azure Speech is usually "subscription suspended" or
      // "quota exceeded for free tier", not "wrong key".
      throw new Error('Azure Speech subscription is disabled or out of quota');
    }
    if (response.status === 402) {
      throw new Error('Azure Speech account has insufficient funds');
    }
    if (response.status === 429) {
      throw new ProviderUnavailableError('Azure MAI', 'rate limit exceeded');
    }
    if (response.status >= 500) {
      throw new ProviderUnavailableError('Azure MAI', `upstream 5xx: ${response.status}`);
    }

    throw new Error(`Azure MAI error: ${response.status}`);
  }

  const data = await response.json() as {
    combinedPhrases?: Array<{ text?: string }>;
    durationMilliseconds?: number;
    phrases?: Array<{ locale?: string }>;
  };

  const transcript = data.combinedPhrases?.[0]?.text || '';
  const durationSeconds = (data.durationMilliseconds || 0) / 1000;
  const detectedLanguage = data.phrases?.[0]?.locale;

  if (!transcript || transcript.trim().length === 0) {
    logProviderEvent(provider, 'no_speech', {
      elapsedMs: Math.round(performance.now() - startedAt),
      detectedLanguage,
    }, context);
    return {
      text: '',
      language: detectedLanguage,
      durationSeconds: 0,
      costUsd: 0,
      source: 'no_speech',
    };
  }

  logProviderEvent(provider, 'success', {
    elapsedMs: Math.round(performance.now() - startedAt),
    transcriptChars: transcript.length,
    durationSeconds,
    detectedLanguage,
  }, context);

  return {
    text: transcript,
    language: detectedLanguage,
    durationSeconds,
    costUsd: computeAzureMaiTranscriptionCost(durationSeconds),
    source: 'azure-mai',
  };
}
