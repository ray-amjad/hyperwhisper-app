import { describe, expect, test } from 'bun:test';
import { clientOffersLatencyOptOut, MIN_OPT_OUT_VERSION } from './latency-eligibility';
import { UNKNOWN_CLIENT } from './client-info';

describe('clientOffersLatencyOptOut', () => {
  test('admits the first release that shipped the switch', () => {
    expect(clientOffersLatencyOptOut('macos', '2.43.0')).toBe(true);
    expect(clientOffersLatencyOptOut('windows', '1.10.0')).toBe(true);
  });

  test('admits anything newer', () => {
    expect(clientOffersLatencyOptOut('macos', '2.43.1')).toBe(true);
    expect(clientOffersLatencyOptOut('macos', '3.0.0')).toBe(true);
    expect(clientOffersLatencyOptOut('windows', '1.11.0')).toBe(true);
    expect(clientOffersLatencyOptOut('windows', '2.0.0')).toBe(true);
  });

  test('rejects the builds that had no switch', () => {
    // The two versions that were live when the /latency page went up.
    expect(clientOffersLatencyOptOut('macos', '2.42.0')).toBe(false);
    expect(clientOffersLatencyOptOut('windows', '1.9.0')).toBe(false);
  });

  test('compares numerically, not as text', () => {
    // '1.9.0' > '1.10.0' as strings, which is the bug this guards.
    expect(clientOffersLatencyOptOut('windows', '1.9.9')).toBe(false);
    expect(clientOffersLatencyOptOut('macos', '2.9.0')).toBe(false);
  });

  test('rejects a platform with no entry, so a new client stays out until added', () => {
    expect(clientOffersLatencyOptOut('ios', '1.0.0')).toBe(false);
    expect(clientOffersLatencyOptOut('linux', '99.0.0')).toBe(false);
  });

  test('rejects an unreadable client', () => {
    expect(clientOffersLatencyOptOut(UNKNOWN_CLIENT, UNKNOWN_CLIENT)).toBe(false);
    expect(clientOffersLatencyOptOut('macos', UNKNOWN_CLIENT)).toBe(false);
    expect(clientOffersLatencyOptOut(UNKNOWN_CLIENT, '2.43.0')).toBe(false);
  });

  test('rejects a version it cannot parse, rather than guessing', () => {
    expect(clientOffersLatencyOptOut('macos', '2.43.0-beta')).toBe(false);
    expect(clientOffersLatencyOptOut('macos', 'v2.43.0')).toBe(false);
    expect(clientOffersLatencyOptOut('macos', '')).toBe(false);
    expect(clientOffersLatencyOptOut('macos', '2.43.0.1')).toBe(false);
  });

  test('treats a missing component as zero', () => {
    expect(clientOffersLatencyOptOut('macos', '2.43')).toBe(true);
    expect(clientOffersLatencyOptOut('macos', '2.42')).toBe(false);
    expect(clientOffersLatencyOptOut('macos', '3')).toBe(true);
  });

  test('every listed minimum is itself a parseable version', () => {
    for (const [platform, minimum] of Object.entries(MIN_OPT_OUT_VERSION)) {
      expect(clientOffersLatencyOptOut(platform, minimum)).toBe(true);
    }
  });
});
