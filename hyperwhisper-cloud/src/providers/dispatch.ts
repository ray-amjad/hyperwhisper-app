// To add a provider: register it in `lib/stt-models.ts`, then add its row here.
// The `Record<SttProviderId, ...>` type makes a missing row a compile error.

import { modelAdapterIdFor, type SttModelAdapterId, type SttProviderId } from '../lib/stt-models';
import type { ProviderRequestContext, TranscriptionResult } from './types';
import type { ProviderAudioReservation } from './audio-reservation';

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
import { ASSEMBLYAI_DICTATION_ADAPTER } from './assemblyai-dictation';
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

/** Why a model rejected the request's options; the route turns it into a 400. */
export interface ModelOptionsRejection {
  /** `reason` in the `transcribe.request_rejected` log event. */
  reason: string;
  /** Error title in the 400 body. */
  title: string;
  /** Error message in the 400 body. */
  message: string;
}

/**
 * A model that is its own upstream product, named by a registry row's
 * `adapter` field. It replaces its provider's adapter for that model only.
 */
export interface ModelAdapter {
  transcribe: TranscribeFn;
  /** Checked before auth or credits; null accepts the options. */
  validateOptions(language: string | undefined, domain: string | undefined): ModelOptionsRejection | null;
  /** Replaces the provider's audio reservation (see audio-reservation.ts). */
  audioReservation(sizeBytes: number): ProviderAudioReservation;
  /** Whether the provider's premium routing tiers (reservation.ts) can run this model. */
  usesProviderPremiumRoutes: boolean;
}

const MODEL_ADAPTERS: Record<SttModelAdapterId, ModelAdapter> = {
  'assemblyai-dictation': ASSEMBLYAI_DICTATION_ADAPTER,
};

export function modelAdapterFor(provider: SttProviderId, model: string | undefined): ModelAdapter | undefined {
  const id = modelAdapterIdFor(provider, model);
  return id ? MODEL_ADAPTERS[id] : undefined;
}

export function validateModelOptions(
  provider: SttProviderId,
  model: string | undefined,
  language: string | undefined,
  domain: string | undefined,
): ModelOptionsRejection | null {
  return modelAdapterFor(provider, model)?.validateOptions(language, domain) ?? null;
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
  const transcribe = modelAdapterFor(provider, context?.model)?.transcribe ?? PROVIDER_FN[provider];
  return transcribe(audio, contentType, language, initialPrompt, context);
}
