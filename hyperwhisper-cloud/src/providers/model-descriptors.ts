// What preflight, pricing and credit reservation need to know about a model
// that is its own upstream product (a registry row's `adapter` field). This
// module imports no adapter: `dispatch.ts` is the only place that does.

import { modelAdapterIdFor, type SttModelAdapterId, type SttProviderId } from '../lib/stt-models';
import { parseDictationWav, validateDictationOptions } from './assemblyai-dictation-rules';
import { ProviderInputError, UnsupportedAudioFormatError } from './types';

export type BufferedAudioReservationResult =
  | { kind: 'duration'; audioSeconds: number }
  | { kind: 'local-input-error' };

export interface ProviderAudioReservation {
  requiresBufferedBody: boolean;
  /** The historical size-only estimate used when no exact duration is known. */
  estimatedAudioSeconds: number;
  /** A safe duration floor for the credit check before body allocation. */
  preBufferAudioSeconds: number;
  /** Resolve the final duration without exposing provider parser errors. */
  resolveBufferedAudio(audio: ArrayBuffer, contentType: string): BufferedAudioReservationResult;
}

/** Why a model rejected the request's options; the route turns it into a 400. */
export interface ModelOptionsRejection {
  /** `reason` in the `transcribe.request_rejected` log event. */
  reason: string;
  /** Error title in the 400 body. */
  title: string;
  /** Error message in the 400 body. */
  message: string;
}

/** It replaces its provider's rules for that model only. */
export interface ModelDescriptor {
  /** Checked before auth or credits; null accepts the options. */
  validateOptions(language: string | undefined, domain: string | undefined): ModelOptionsRejection | null;
  /** Replaces the provider's audio reservation (see audio-reservation.ts). */
  audioReservation(sizeBytes: number): ProviderAudioReservation;
  /** Whether the provider's premium routing tiers (reservation.ts) can run this model. */
  usesProviderPremiumRoutes: boolean;
}

const ASSEMBLYAI_DICTATION: ModelDescriptor = {
  validateOptions(language, domain) {
    try { validateDictationOptions(language, domain); } catch (error) {
      return { reason: 'invalid_dictation_options', title: 'Invalid Dictation options', message: (error as Error).message };
    }
    return null;
  },
  audioReservation: () => ({
    requiresBufferedBody: true,
    estimatedAudioSeconds: 120,
    // WAV permits metadata chunks, so size alone has no safe duration floor.
    preBufferAudioSeconds: 0,
    resolveBufferedAudio(audio, contentType) {
      try { return { kind: 'duration', audioSeconds: parseDictationWav(audio, contentType).durationSeconds }; } catch (error) {
        if (error instanceof UnsupportedAudioFormatError || error instanceof ProviderInputError) return { kind: 'local-input-error' };
        throw error;
      }
    },
  }),
  // A separate product: AssemblyAI's sync fast path never runs it.
  usesProviderPremiumRoutes: false,
};

const MODEL_DESCRIPTORS: Record<SttModelAdapterId, ModelDescriptor> = {
  'assemblyai-dictation': ASSEMBLYAI_DICTATION,
};

export function modelDescriptorFor(provider: SttProviderId, model: string | undefined): ModelDescriptor | undefined {
  const id = modelAdapterIdFor(provider, model);
  return id ? MODEL_DESCRIPTORS[id] : undefined;
}

export function validateModelOptions(
  provider: SttProviderId,
  model: string | undefined,
  language: string | undefined,
  domain: string | undefined,
): ModelOptionsRejection | null {
  return modelDescriptorFor(provider, model)?.validateOptions(language, domain) ?? null;
}
