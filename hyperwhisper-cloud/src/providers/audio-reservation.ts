import { modelDescriptorFor, type BufferedAudioReservationResult, type ProviderAudioReservation } from './model-descriptors';
import { estimateAudioSecondsFromSize } from '../lib/audio-duration';
import type { SttProviderId } from '../lib/stt-models';
import { parseMetaWav } from './meta';
import { ProviderInputError, UnsupportedAudioFormatError } from './types';

type ReservationResolver = (sizeBytes: number) => ProviderAudioReservation;

function durationResult(audioSeconds: number): BufferedAudioReservationResult {
  return { kind: 'duration', audioSeconds };
}

const PROVIDER_RESERVATIONS: Partial<Record<SttProviderId, ReservationResolver>> = {
  meta: (sizeBytes) => ({
    requiresBufferedBody: true,
    estimatedAudioSeconds: Math.max(10, Math.max(0, sizeBytes - 44) / (16_000 * 2)),
    // A canonical 24 kHz mono PCM16 WAV has the highest accepted byte rate.
    // Therefore, a valid Meta WAV with this declared size cannot be shorter.
    preBufferAudioSeconds: Math.max(0, sizeBytes - 44) / (24_000 * 2),
    resolveBufferedAudio(audio, contentType) {
      try {
        return durationResult(parseMetaWav(audio, contentType).durationSeconds);
      } catch (error) {
        if (error instanceof UnsupportedAudioFormatError || error instanceof ProviderInputError) {
          return { kind: 'local-input-error' };
        }
        throw error;
      }
    },
  }),
};

export function providerAudioReservation(
  provider: SttProviderId,
  sizeBytes: number,
  model?: string,
): ProviderAudioReservation {
  const modelDescriptor = modelDescriptorFor(provider, model);
  if (modelDescriptor) return modelDescriptor.audioReservation(sizeBytes);
  const providerReservation = PROVIDER_RESERVATIONS[provider];
  if (providerReservation) return providerReservation(sizeBytes);

  const audioSeconds = estimateAudioSecondsFromSize(sizeBytes);
  return {
    requiresBufferedBody: false,
    estimatedAudioSeconds: audioSeconds,
    preBufferAudioSeconds: audioSeconds,
    resolveBufferedAudio: () => durationResult(audioSeconds),
  };
}
