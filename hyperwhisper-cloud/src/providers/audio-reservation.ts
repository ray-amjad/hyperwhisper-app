import { parseDictationWav } from './assemblyai-dictation';
import { estimateAudioSecondsFromSize, reservationAudioSeconds } from '../lib/audio-duration';
import { MAX_UPLOAD_BYTES_PER_SECOND } from '../lib/constants';
import type { SttProviderId } from '../lib/stt-models';
import { parseMetaWav } from './meta';
import { ProviderInputError, UnsupportedAudioFormatError } from './types';

export type BufferedAudioReservationResult =
  | { kind: 'duration'; audioSeconds: number }
  | { kind: 'local-input-error' };

export interface ProviderAudioReservation {
  /** Whether the exact reservation requires the request body. */
  requiresBufferedBody: boolean;
  /** The size-only estimate used when no exact duration is known. */
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
  model?: string,
): ProviderAudioReservation {
  if (provider === 'assemblyai' && model === 'dictation') {
    return {
      requiresBufferedBody: true,
      estimatedAudioSeconds: 120,
      // WAV permits metadata chunks, so size alone has no safe duration floor.
      preBufferAudioSeconds: 0,
      resolveBufferedAudio(audio, contentType) {
        try { return durationResult(parseDictationWav(audio, contentType).durationSeconds); } catch (error) {
          if (error instanceof UnsupportedAudioFormatError || error instanceof ProviderInputError) return { kind: 'local-input-error' };
          throw error;
        }
      },
    };
  }
  const providerReservation = PROVIDER_RESERVATIONS[provider];
  if (providerReservation) return providerReservation(sizeBytes);

  // Every other provider takes compressed audio, whose size says little about
  // its length: an hour of 8 kbps Opus is 3.6 MB (#2483). So the hold waits for
  // the body and reads the length from the container. The route buffers the
  // whole body anyway; this only moves that before the hold.
  return {
    requiresBufferedBody: true,
    estimatedAudioSeconds: estimateAudioSecondsFromSize(sizeBytes),
    // No legitimate upload has a higher byte rate, so no audio of this size is
    // shorter: this only refuses a balance that clearly cannot cover it.
    preBufferAudioSeconds: sizeBytes / MAX_UPLOAD_BYTES_PER_SECOND,
    // Unreadable audio falls back to a low-bitrate estimate rather than a
    // local input error, which would skip the credit check.
    resolveBufferedAudio: (audio) => durationResult(reservationAudioSeconds(audio)),
  };
}
