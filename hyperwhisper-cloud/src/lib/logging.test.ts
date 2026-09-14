import { describe, expect, test } from 'bun:test';
import { flyProxyOverheadMs } from './logging';

const NOW_MS = Date.UTC(2026, 8, 14, 12, 0, 0);

describe('flyProxyOverheadMs', () => {
  test('accepts a numeric Fly timestamp from now through 59,999 ms ago', () => {
    expect(flyProxyOverheadMs(String(NOW_MS), NOW_MS)).toBe(0);
    expect(flyProxyOverheadMs(String(NOW_MS - 59_999), NOW_MS)).toBe(59_999);
  });

  test('accepts an RFC 1123 Fly timestamp', () => {
    expect(flyProxyOverheadMs(new Date(NOW_MS - 1_000).toUTCString(), NOW_MS)).toBe(1_000);
  });

  test('rejects a timestamp from the future', () => {
    expect(flyProxyOverheadMs(String(NOW_MS + 1), NOW_MS)).toBeUndefined();
  });

  test('rejects a timestamp at the 60-second limit', () => {
    expect(flyProxyOverheadMs(String(NOW_MS - 60_000), NOW_MS)).toBeUndefined();
  });
});
