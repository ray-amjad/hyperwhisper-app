// What AssemblyAI Dictation accepts, with no adapter or network code, so
// preflight checks and credit reservation can load it without the adapters.
// Contract: https://www.assemblyai.com/docs/dictation
import { parsePcm16Wav } from '../lib/wav-header';
import { ProviderInputError } from './types';

export const DICTATION_LANGUAGES = ['en', 'es', 'de', 'fr', 'it', 'pt', 'tr', 'nl', 'sv', 'no', 'da', 'fi', 'hi', 'vi', 'he', 'ur', 'ko', 'ca', 'gl', 'ru', 'ro', 'et', 'fa', 'yue', 'af', 'mr', 'zu', 'xh', 'nn', 'ar', 'ja', 'zh'] as const;

export function validateDictationOptions(language?: string, domain?: string): string {
  if (domain?.trim()) {
    throw new ProviderInputError('AssemblyAI Dictation', 400, 'Dictation does not support Medical Mode or other domains. Select Dictation without a domain.');
  }
  const primary = (language || '').trim().toLowerCase().split(/[-_]/)[0];
  const code = primary === 'nb' ? 'no' : primary;
  if (!(DICTATION_LANGUAGES as readonly string[]).includes(code)) {
    throw new ProviderInputError('AssemblyAI Dictation', 400, 'Select an explicit supported language instead of Auto for Dictation.');
  }
  return code;
}

/** Inspect the actual RIFF chunks, never a MIME label or a byte-size estimate. */
export function parseDictationWav(audio: ArrayBuffer, contentType: string): { durationSeconds: number } {
  const { durationSeconds } = parsePcm16Wav(audio, contentType, {
    provider: 'AssemblyAI Dictation',
    acceptedFormats: ['PCM16 WAV (convert this recording to WAV)'],
    maxDurationSeconds: 120,
    tooLongMessage: 'audio exceeds 120 seconds. Use a shorter recording or another model.',
  });
  return { durationSeconds };
}
