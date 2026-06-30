import { describe, expect, test } from 'bun:test';
import { computeUploadTimeoutMs } from './utils';

describe('computeUploadTimeoutMs (size-scaled audio-upload budget)', () => {
  test('small payloads get the 30s floor', () => {
    expect(computeUploadTimeoutMs(0)).toBe(30_000);
    expect(computeUploadTimeoutMs(1_000_000)).toBe(30_000); // 1 MB → 10s scaled, floored to 30s
  });

  test('large payloads scale at 1s per 100 KB', () => {
    // 100 MB → ceil(100e6 / 100_000) = 1000 × 1000ms = 1000s.
    expect(computeUploadTimeoutMs(100_000_000)).toBe(1_000_000);
    // 300 MB (Azure MAI cap) → 3000s, comfortably above the 15s default that
    // would otherwise abort a large multipart upload.
    expect(computeUploadTimeoutMs(300_000_000)).toBe(3_000_000);
  });

  test('budget is monotonic in payload size', () => {
    expect(computeUploadTimeoutMs(50_000_000)).toBeGreaterThan(computeUploadTimeoutMs(5_000_000));
  });
});
