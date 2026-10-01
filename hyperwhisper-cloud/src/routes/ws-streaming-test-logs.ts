// Test-only helper for the live-streaming preflight tests (#1094). Captures
// console.log per test, so a suite can assert what a refused upgrade logged.
// Not imported by any production module, and not named *.test.ts, so
// `bun test src` does not run it as a suite.

import { afterEach, beforeEach, expect } from 'bun:test';

/** The account key every preflight test sends. No captured line may carry it. */
export const TEST_ACCOUNT_KEY = 'key-1234-abcd';

export type RejectionLogCapture = {
  /** The console.log lines captured so far in the current test. */
  lines: () => string[];
  /**
   * Asserts exactly one `ws_streaming.request_rejected` line with this reason
   * and status, and that no captured line carries the account key. Returns
   * the parsed entry for further assertions.
   */
  expectOneRejection: (reason: string, status: number) => Record<string, unknown>;
};

/**
 * Call inside a `describe`. Replaces console.log before each test in that
 * describe and restores it after each one.
 */
export function captureRejectionLogs(): RejectionLogCapture {
  let logged: string[] = [];
  const originalConsoleLog = console.log;

  beforeEach(() => {
    logged = [];
    console.log = (...args: unknown[]) => { logged.push(args.map(String).join(' ')); };
  });

  afterEach(() => {
    console.log = originalConsoleLog;
  });

  return {
    lines: () => logged,
    expectOneRejection(reason, status) {
      const rejections = logged
        .map((line) => { try { return JSON.parse(line) as Record<string, unknown>; } catch { return null; } })
        .filter((entry): entry is Record<string, unknown> => entry?.event === 'ws_streaming.request_rejected');
      expect(rejections).toHaveLength(1);
      expect(rejections[0]!.reason).toBe(reason);
      expect(rejections[0]!.status).toBe(status);
      for (const line of logged) expect(line).not.toContain(TEST_ACCOUNT_KEY);
      return rejections[0]!;
    },
  };
}
