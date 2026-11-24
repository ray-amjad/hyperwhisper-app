// MODELS THAT RUN ON THEIR OWN ENDPOINT
//
// A few catalog models share a provider id with that provider's main adapter
// but are a separate vendor product: their own endpoint, request body, accepted
// audio, duration rule and option checks (AssemblyAI Dictation is one). Each
// such model is ONE row here, and every layer that has to treat it differently
// reads the row instead of naming the model:
//   - dispatch runs `transcribe` instead of the provider's main adapter,
//   - the credit reservation sizes the hold with `audioReservation`,
//   - the request-option gate runs `checkOptions` before any auth or credit work,
//   - the main adapter's own routing tiers (AssemblyAI's sync fast path) are
//     never priced for it, because the main adapter never sees it.
//
// Adding such a model is a registry row in `lib/stt-models.ts` plus a row here.

import type { SttProviderId } from '../lib/stt-models';
import type { ProviderAudioReservation } from './audio-reservation';
import type { TranscribeFn } from './dispatch';
import type { RequestOptions } from './request-options';
import { DICTATION_VARIANT } from './assemblyai-dictation';

export interface ModelVariant {
  /** The adapter that answers this model, in place of the provider's own. */
  transcribe: TranscribeFn;
  /** The credit reservation for a request of `sizeBytes` declared bytes. */
  audioReservation: (sizeBytes: number) => ProviderAudioReservation;
  /** Throws ProviderInputError when the request options are not acceptable. */
  checkOptions: (options: RequestOptions) => void;
}

const MODEL_VARIANTS: Partial<Record<SttProviderId, Readonly<Record<string, ModelVariant>>>> = {
  assemblyai: { dictation: DICTATION_VARIANT },
};

/** The variant row for `provider`/`model`, or undefined for every model the main adapter runs. */
export function modelVariant(provider: SttProviderId, model: string | undefined): ModelVariant | undefined {
  const rows = MODEL_VARIANTS[provider];
  // `model` is caller input: an own-property check, so `constructor` is no row.
  return rows && model !== undefined && Object.hasOwn(rows, model) ? rows[model] : undefined;
}
