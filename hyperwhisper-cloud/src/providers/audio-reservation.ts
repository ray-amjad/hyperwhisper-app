import { estimateAudioSecondsFromSize } from '../lib/audio-duration';
import type { SttProviderId } from '../lib/stt-models';
import { parseMetaWav } from './meta';
import { ProviderInputError, UnsupportedAudioFormatError } from './types';

export type BufferedAudioReservationResult =
  | { kind: 'duration'; audioSeconds: number }
  | { kind: 'local-input-error' };

export interface ProviderAudioReservation {
  /** Whether the exact reservation requires the request body. */
  requiresBufferedBody: boolean;
  /** The historical size-only estimate used when no exact duration is known. */
  estimatedAudioSeconds: number;
  /** A safe duration floor for the credit check before body allocation. */
  preBufferAudioSeconds: number;
  /** Resolve the final duration without exposing provider parser errors. */
  resolveBufferedAudio(audio: ArrayBuffer, contentType: string): BufferedAudioReservationResult;
}

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

/**
 * Return the audio reservation behavior for a provider without exposing its
 * accepted formats, byte rates, parser, or parser error classes to the route.
 */
export function providerAudioReservation(
  provider: SttProviderId,
  sizeBytes: number,
): ProviderAudioReservation {
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
