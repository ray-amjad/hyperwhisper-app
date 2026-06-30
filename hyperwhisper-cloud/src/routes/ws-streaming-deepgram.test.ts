import { describe, expect, test } from 'bun:test';
import { durationSecondsForLinear16AudioBytes } from './ws-streaming-deepgram';

describe('durationSecondsForLinear16AudioBytes', () => {
  test('calculates duration from the mono 16 kHz linear16 audio forwarded to Deepgram', () => {
    const bytesPerSecond = 16000 * 1 * 2;

    expect(durationSecondsForLinear16AudioBytes(bytesPerSecond * 5)).toBe(5);
  });

  test('does not depend on overlapping interim or final transcript result durations', () => {
    const overlappingDeepgramResultDurations = [3, 3, 3, 2, 2];
    const bytesActuallyForwarded = 5 * 16000 * 1 * 2;

    expect(overlappingDeepgramResultDurations.reduce((sum, duration) => sum + duration, 0)).toBe(13);
    expect(durationSecondsForLinear16AudioBytes(bytesActuallyForwarded)).toBe(5);
  });
});
