// PROVIDER DISPATCH
// The providers layer's own answer to "which adapter runs this provider id".
//
// The transcribe route walks the fallback chain the registry hands it. Before
// this file it also had to know, for every provider in that chain, which module
// the adapter lives in and what the adapter is called — twelve deep imports and
// a hand-written id → function table, next to a second hand-written id → label
// table. Adding a provider meant editing `lib/stt-models.ts` AND the route, and
// the route's tables were private, so the deploy smoke test kept its own copy of
// the label rule.
//
// Now the route asks for a transcription by id and never names an adapter. The
// label half of the same problem lives in the registry (`servedNameFor()` /
// `formatProviderName()` in `lib/stt-models.ts`), because a public label is
// registry data rather than adapter behaviour.
//
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

/** The one shape every STT adapter in this folder implements. */
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

/** The model-specific adapter for (provider, model), if its registry row names one. */
export function modelAdapterFor(provider: SttProviderId, model: string | undefined): ModelAdapter | undefined {
  const id = modelAdapterIdFor(provider, model);
  return id ? MODEL_ADAPTERS[id] : undefined;
}

/**
 * The request-option check for (provider, model): null when the model accepts
 * them. Only a model with its own adapter has rules of its own today.
 */
export function validateModelOptions(
  provider: SttProviderId,
  model: string | undefined,
  language: string | undefined,
  domain: string | undefined,
): ModelOptionsRejection | null {
  return modelAdapterFor(provider, model)?.validateOptions(language, domain) ?? null;
}

/**
 * Run the adapter for `provider` and `context.model`: the model's own adapter
 * when its registry row names one, otherwise `provider`'s. The arguments and the returned
 * `TranscriptionResult` are the adapter contract in `types.ts`, unchanged — the
 * only thing this hides is which function answers.
 *
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
