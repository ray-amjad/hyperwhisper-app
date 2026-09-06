import { BYTES_PER_MINUTE_ESTIMATE } from './constants';

const MIN_ESTIMATED_SECONDS = 10;

/** Conservative duration estimate for encoded audio when only its size is known. */
export function estimateAudioSecondsFromSize(sizeBytes: number): number {
  const estimatedMinutes = sizeBytes / BYTES_PER_MINUTE_ESTIMATE;
  return Math.max(MIN_ESTIMATED_SECONDS, estimatedMinutes * 60);
}
