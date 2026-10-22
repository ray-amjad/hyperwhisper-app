import { readAudioContainerDuration } from './audio-container-duration';
import { RESERVATION_FALLBACK_BYTES_PER_SECOND } from './constants';

const MIN_ESTIMATED_SECONDS = 10;
/** A week: past this a parsed length is a broken header, not audio anyone sent. */
const MAX_RESERVATION_SECONDS = 7 * 24 * 60 * 60;

/**
 * Conservative duration estimate for encoded audio when only its size is known.
 * It assumes a low (8 kbps) bitrate, so low-bitrate audio is not under-reserved
 * (#2483); use reservationAudioSeconds() when the bytes are available.
 */
export function estimateAudioSecondsFromSize(sizeBytes: number): number {
  return Math.max(MIN_ESTIMATED_SECONDS, sizeBytes / RESERVATION_FALLBACK_BYTES_PER_SECOND);
}

/**
 * The seconds to hold credits for, read from the uploaded bytes (#2483).
 *
 * A recognised container's own length is used, but never less than the size
 * divided by the highest byte rate that format can have: the header is the
 * uploader's to write. Anything unrecognised or unreadable falls back to the
 * 8 kbps size estimate. Never throws, and never returns less than 10 s.
 */
export function reservationAudioSeconds(audio: ArrayBuffer): number {
  const sizeBytes = audio.byteLength;
  const parsed = readAudioContainerDuration(audio);
  const seconds = parsed?.durationSeconds;
  if (!parsed || seconds === undefined || !Number.isFinite(seconds) || seconds <= 0) {
    return estimateAudioSecondsFromSize(sizeBytes);
  }
  const floorSeconds = sizeBytes / parsed.maxBytesPerSecond;
  const clamped = Math.min(MAX_RESERVATION_SECONDS, Math.max(floorSeconds, seconds));
  return Math.max(MIN_ESTIMATED_SECONDS, clamped);
}
