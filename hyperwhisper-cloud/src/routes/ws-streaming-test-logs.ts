// Test-only helpers for the live-streaming tests: the preflight rejection log
// (#1094) and the socket failure-exit logs (#953).
// Not imported by any production module, and not named *.test.ts, so
// `bun test src` does not run it as a suite.

import { afterEach, beforeEach, expect } from 'bun:test';

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
      for (const line of logged) expect(line).not.toContain('key-1234-abcd'); // the key every preflight test sends
      return rejections[0]!;
    },
  };
}

export type StreamingLogEntry = { event: string; details: Record<string, unknown> };

/**
 * Runs `fn` with console.log, console.warn and console.error captured (#953).
 * Returns the `ws_streaming.*` entries, and every captured call serialised in
 * full — details object included — so a test can assert that no line carries
 * a frame's transcript text.
 */
export function captureStreamingLogs(fn: () => void): { entries: StreamingLogEntry[]; serialised: string[] } {
  const calls: unknown[][] = [];
  const original = { log: console.log, warn: console.warn, error: console.error };
  console.log = (...args: unknown[]) => { calls.push(args); };
  console.warn = (...args: unknown[]) => { calls.push(args); };
  console.error = (...args: unknown[]) => { calls.push(args); };
  try {
    fn();
  } finally {
    Object.assign(console, original);
  }
  const entries = calls
    .filter(([event]) => typeof event === 'string' && event.startsWith('ws_streaming.'))
    .map(([event, details]) => ({ event: event as string, details: (details ?? {}) as Record<string, unknown> }));
  const serialised = calls.map((args) => args
    .map((arg) => (arg instanceof Error ? `${arg.name}: ${arg.message}\n${arg.stack}` : typeof arg === 'string' ? arg : JSON.stringify(arg)))
    .join(' '));
  return { entries, serialised };
}
