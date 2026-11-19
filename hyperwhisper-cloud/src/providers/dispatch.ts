// To add a provider: register it in `lib/stt-models.ts`, then add its row here.
// The `Record<SttProviderId, ...>` type makes a missing row a compile error.

import { modelAdapterIdFor, type SttModelAdapterId, type SttProviderId } from '../lib/stt-models';
import type { ProviderRequestContext, TranscriptionResult } from './types';

import { transcribeWithDeepgram } from './deepgram';
import { transcribeWithGroq } from './groq';
import { transcribeWithElevenLabs } from './elevenlabs';
import { transcribeWithXaiGrok } from './xai-stt';
import { transcribeWithAzureMai } from './azure-mai';
import { transcribeWithGoogleChirp } from './google-chirp';
import { transcribeWithOpenAI } from './openai';
import { transcribeWithGemini } from './gemini';
import { transcribeWithGeminiTranscribe } from './gemini-transcribe';
import { transcribeWithAssemblyAI } from './assemblyai';
import { transcribeWithAssemblyAIDictation } from './assemblyai-dictation';
import { transcribeWithMistral } from './mistral';
import { transcribeWithSoniox } from './soniox';
import { transcribeWithMeta } from './meta';

export type TranscribeFn = (
  audio: ArrayBuffer,
  contentType: string,
  language?: string,
  initialPrompt?: string,
  context?: ProviderRequestContext,
) => Promise<TranscriptionResult>;

const PROVIDER_FN: Record<SttProviderId, TranscribeFn> = {
  deepgram: transcribeWithDeepgram,
  groq: transcribeWithGroq,
  elevenlabs: transcribeWithElevenLabs,
  grok: transcribeWithXaiGrok,
  'azure-mai': transcribeWithAzureMai,
  'google-chirp': transcribeWithGoogleChirp,
  openai: transcribeWithOpenAI,
  gemini: transcribeWithGemini,
  'gemini-transcribe': transcribeWithGeminiTranscribe,
  assemblyai: transcribeWithAssemblyAI,
  mistral: transcribeWithMistral,
  soniox: transcribeWithSoniox,
  meta: transcribeWithMeta,
};

/**
 * A model that is its own upstream product, named by a registry row's
 * `adapter` field. It replaces its provider's adapter for that model only;
 * its option check and reservation rule are in `model-descriptors.ts`.
 */
const MODEL_ADAPTERS: Record<SttModelAdapterId, TranscribeFn> = {
  'assemblyai-dictation': (audio, contentType, language, initialPrompt, context = {}) =>
    transcribeWithAssemblyAIDictation(audio, contentType, language, initialPrompt, context),
};

function modelAdapterFor(provider: SttProviderId, model: string | undefined): TranscribeFn | undefined {
  const id = modelAdapterIdFor(provider, model);
  return id ? MODEL_ADAPTERS[id] : undefined;
}

/**
 * Every error an adapter throws still reaches the caller as thrown: the route's
 * chain logic is built on those types (`ProviderUnavailableError`,
 * `ProviderInputError`, `AudioTooLargeError`, `UnsupportedAudioFormatError`),
 * so this must never catch or rewrap.
 */
export function transcribeWithProvider(
  provider: SttProviderId,
  audio: ArrayBuffer,
  contentType: string,
  language?: string,
  initialPrompt?: string,
  context?: ProviderRequestContext,
): Promise<TranscriptionResult> {
  const transcribe = modelAdapterFor(provider, context?.model) ?? PROVIDER_FN[provider];
  return transcribe(audio, contentType, language, initialPrompt, context);
}
