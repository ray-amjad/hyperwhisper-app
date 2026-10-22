import { RESERVATION_FALLBACK_BYTES_PER_SECOND } from './constants';

const MIN_ESTIMATED_SECONDS = 10;

/**
 * Conservative duration estimate for encoded audio when only its size is known.
 * It assumes a low (8 kbps) bitrate, so low-bitrate audio is not under-reserved
 * (#2483); use reservationAudioSeconds() when the bytes are available.
 */
export function estimateAudioSecondsFromSize(sizeBytes: number): number {
  return Math.max(MIN_ESTIMATED_SECONDS, sizeBytes / RESERVATION_FALLBACK_BYTES_PER_SECOND);
}
