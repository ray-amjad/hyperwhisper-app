// THE DEPLOY-TIME SHUTDOWN BUDGET (#2914)
//
// `runGracefulShutdown` owns the deadlines Fly's 120 s `kill_timeout` forces:
// wait up to 100 s FROM THE SIGNAL for in-flight requests, drain charges,
// latency reports and hold releases until 115 s FROM THE SIGNAL, cap the reply
// flush at that same deadline, and only exit once the error flush is done.
// `index.test.ts` plays one real SIGTERM end to end; it cannot test these
// deadlines, because waiting 100 s for real is not a test.
//
// How these tests drive it:
// - `now` is a fake clock that moves only when the test says simulated time
//   passed (a request ran 60 s, a charge write took 30 s). `exit` and
//   `flushErrors` are captured.
// - Everything else is REAL: `trackInFlightRequests` with a `next` the test
//   controls stands in for a request, `deductCredits` against a stubbed
//   `globalThis.fetch` is the drain work, and the real drain helpers and
//   `waitForInFlightRequests` run with real timers. A budget the code computes
//   wrongly therefore shows up as a real wait, and each test races the exit
//   against a short real timer.
// - `waitForResponseFlush` reads the REAL clock against the deadline the fake
//   clock produced, so where the flush matters the fake clock is anchored to
//   real time.
// - The module latches (request refusal, streaming shutdown) are reset around
//   every test: bun runs every test file in one process.

import { afterEach, beforeEach, describe, expect, test } from 'bun:test';
import { ERROR_FLUSH_TIMEOUT_MS, runGracefulShutdown, SHUTDOWN_DRAIN_MS } from './graceful-shutdown';
import { deductCredits, drainPendingDeductions } from '../middleware/credits';
import { drainPendingHoldReleases } from '../middleware/credit-holds';
import { drainPendingLatencyReports } from './latency-report';
import {
  IN_FLIGHT_REQUEST_WAIT_MS,
  RESPONSE_FLUSH_GRACE_MS,
  resetInFlightRequestsForTests,
  trackInFlightRequests,
} from '../middleware/in-flight-requests';
import { endActiveStreamingSessions, resetStreamingShutdownForTests } from '../routes/ws-streaming-shared';
import { saveEnv } from '../test-support/env';

const AUTH = { identifier: 'HW-ROUTINE-FIXTURE', credits: 1000, licenseKey: 'HW-ROUTINE-FIXTURE' };

const originalFetch = globalThis.fetch;
const realLog = console.log;
const realError = console.error;
const restoreLicenseApi = saveEnv('NEXTJS_LICENSE_API_URL');

let logPayloads: Array<{ event: string; args: unknown[]; at: number }> = [];
/** Undone after every test, even a failed one, so a mutant cannot leak into the next. */
let cleanups: Array<() => void | Promise<void>> = [];

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function jsonOk(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'content-type': 'application/json' } });
}

function logged(event: string): unknown {
  return logPayloads.find((entry) => entry.event === event)?.args[1];
}

function loggedAt(event: string): number | undefined {
  return logPayloads.find((entry) => entry.event === event)?.at;
}

function shutdownEvents(): string[] {
  return logPayloads.map((entry) => entry.event).filter((event) => event.startsWith('machine.'));
}

/** A clock that moves only when the test advances it. */
function fakeClock(startAt: number): { now: () => number; advance: (ms: number) => void; since: () => number } {
  let current = startAt;
  return {
    now: () => current,
    advance: (ms) => { current += ms; },
    since: () => current - startAt,
  };
}

/** Capture `exit`; `exited` resolves with the code. */
function captureExit(): { exit: (code: number) => void; exited: Promise<number>; code: () => number | undefined } {
  let exitCode: number | undefined;
  let resolveExit!: (code: number) => void;
  const exited = new Promise<number>((resolve) => { resolveExit = resolve; });
  return {
    exit: (code) => { exitCode = code; resolveExit(code); },
    exited,
    code: () => exitCode,
  };
}

/** The exit code, or 'still running' if it has not exited within `ms` of real time. */
function exitWithin(exited: Promise<number>, ms: number): Promise<number | 'still running'> {
  return Promise.race([exited, delay(ms).then(() => 'still running' as const)]);
}

/** Start the shutdown; afterEach waits (bounded) for a run a mutant left hanging. */
function startShutdown(...args: Parameters<typeof runGracefulShutdown>): Promise<void> {
  const run = runGracefulShutdown(...args);
  cleanups.push(() => Promise.race([run, delay(3_000)]).then(() => {}));
  return run;
}

/** A tracked request that runs until the returned function is called. */
function startStuckRequest(): () => void {
  let finish!: () => void;
  const done = new Promise<void>((resolve) => { finish = resolve; });
  void trackInFlightRequests({} as never, () => done);
  cleanups.push(() => finish());
  return finish;
}

/** Drop whatever another suite (or a mutant) left pending, so counts are this test's own. */
async function settleLeftovers(): Promise<void> {
  await endActiveStreamingSessions();
  await Promise.all([
    drainPendingDeductions(3_000),
    drainPendingLatencyReports(3_000),
    drainPendingHoldReleases(3_000),
  ]);
  resetStreamingShutdownForTests();
  resetInFlightRequestsForTests();
}

beforeEach(async () => {
  cleanups = [];
  // Any network call is a failure unless a test opts into one.
  globalThis.fetch = (async (input: RequestInfo | URL) => {
    throw new Error(`Unexpected fetch: ${String(input)}`);
  }) as unknown as typeof fetch;
  await settleLeftovers();
  process.env.NEXTJS_LICENSE_API_URL = 'https://licence.test';
  logPayloads = [];
  const capture = (...args: unknown[]) => { logPayloads.push({ event: String(args[0]), args, at: Date.now() }); };
  console.log = capture;
  console.error = capture;
});

afterEach(async () => {
  for (const cleanup of cleanups.reverse()) await cleanup();
  await settleLeftovers();
  console.log = realLog;
  console.error = realError;
  globalThis.fetch = originalFetch;
  restoreLicenseApi();
});

describe('runGracefulShutdown deadlines', () => {
  test('gives up on a stuck request at signal + 100 s, logs it as still in flight, and still drains', async () => {
    // Ending the live sessions took 99.7 s of fake time, so 300 ms of the
    // 100 s request wait is left. A wait budgeted from "now" (100 s) or from
    // the drain budget (115 s) would still be waiting when the race below ends.
    const signalAt = Date.now();
    let clockReads = 0;
    let current = signalAt;
    const now = () => {
      const reading = current;
      if (clockReads++ === 0) current += IN_FLIGHT_REQUEST_WAIT_MS - 300;
      return reading;
    };

    let chargeWritten = false;
    globalThis.fetch = (async (input: RequestInfo | URL) => {
      if (String(input).includes('/api/license/credits')) {
        // Outlasts the 300 ms request wait, so only the drain can await it.
        await delay(450);
        chargeWritten = true;
        return jsonOk({});
      }
      throw new Error(`Unexpected fetch: ${String(input)}`);
    }) as unknown as typeof fetch;

    startStuckRequest();
    void deductCredits(AUTH, 0.05, { requestId: 'before-signal' }, '203.0.113.1');

    const { exit, exited } = captureExit();
    const startedReal = Date.now();
    void startShutdown('SIGTERM', { now, exit, flushErrors: async () => {} });

    expect(await exitWithin(exited, 1_500)).toBe(0);
    // It did wait for the request: the remaining 300 ms, not 0.
    expect(loggedAt('machine.shutdown_waited_requests')! - startedReal).toBeGreaterThanOrEqual(250);
    expect(logged('machine.shutdown_waited_requests'))
      .toEqual({ count: 1, stillInFlight: 1, waitedMs: IN_FLIGHT_REQUEST_WAIT_MS - 300 });
    expect(chargeWritten).toBe(true);
    expect(logged('machine.shutdown_drained_deductions')).toEqual({ count: 1 });
    expect(shutdownEvents()).toEqual([
      'machine.shutdown',
      'machine.shutdown_waited_requests',
      'machine.shutdown_drained_deductions',
    ]);
  });

  test('stops draining at signal + 115 s even while every pass finds new work', async () => {
    // Anchored 115 s before the real clock, so the deadline has passed for
    // `waitForResponseFlush` too and the exit follows the last pass at once.
    const clock = fakeClock(Date.now() - SHUTDOWN_DRAIN_MS);
    // Each charge write takes 30 s of fake time and, like a request still
    // running after the wait, fires the next charge before it returns. So
    // every pass finds work, and only the deadline can end the loop.
    let chainOpen = true;
    cleanups.push(() => { chainOpen = false; });
    const writesDoneAt: number[] = [];
    globalThis.fetch = (async (input: RequestInfo | URL) => {
      if (String(input).includes('/api/license/credits')) {
        await delay(5);
        clock.advance(30_000);
        writesDoneAt.push(clock.since());
        if (chainOpen) void deductCredits(AUTH, 0.05, { requestId: `chain-${writesDoneAt.length}` }, '203.0.113.1');
        return jsonOk({});
      }
      throw new Error(`Unexpected fetch: ${String(input)}`);
    }) as unknown as typeof fetch;

    // A request in flight at the signal that runs for 60 s, then charges. The
    // drain deadline is 115 s after the SIGNAL, not after this wait.
    const finish = startStuckRequest();
    setTimeout(() => {
      clock.advance(60_000);
      void deductCredits(AUTH, 0.05, { requestId: 'in-flight' }, '203.0.113.1');
      finish();
    }, 20);

    const { exit, exited } = captureExit();
    let writesAtExit: number[] = [];
    const exitAndStopChain = (code: number) => {
      chainOpen = false;
      writesAtExit = [...writesDoneAt];
      exit(code);
    };
    void startShutdown('SIGTERM', { now: clock.now, exit: exitAndStopChain, flushErrors: async () => {} });

    expect(await exitWithin(exited, 2_000)).toBe(0);
    // Two passes: 60-90 s and 90-120 s. A third would start at 120 s, past
    // the 115 s deadline. (A deadline counted from the end of the request
    // wait, 175 s, would allow passes until 180 s.)
    expect(writesAtExit).toEqual([90_000, 120_000]);
    expect(logged('machine.shutdown_waited_requests')).toEqual({ count: 1, stillInFlight: 0, waitedMs: 60_000 });
    expect(logged('machine.shutdown_drained_deductions')).toEqual({ count: 2 });
  });

  test('does not hold the exit for a reply flush past the drain deadline', async () => {
    // The fake clock reads the signal as 115 s before the real clock that
    // `waitForResponseFlush` reads: the whole budget is spent, so the deadline
    // has already passed when the exit runs.
    const now = () => Date.now() - SHUTDOWN_DRAIN_MS;
    // A reply returned just before the signal would otherwise hold the exit
    // for RESPONSE_FLUSH_GRACE_MS (500 ms).
    await trackInFlightRequests({} as never, async () => {});

    const { exit, exited } = captureExit();
    const startedReal = Date.now();
    void startShutdown('SIGTERM', { now, exit, flushErrors: async () => {} });

    expect(await exitWithin(exited, RESPONSE_FLUSH_GRACE_MS / 2)).toBe(0);
    expect(Date.now() - startedReal).toBeLessThan(RESPONSE_FLUSH_GRACE_MS / 2);
  });

  test('holds the exit for the reply flush grace while the deadline is still ahead', async () => {
    // The control for the test above: the same just-returned reply, with the
    // deadline 115 s away, does hold the exit for the grace.
    await trackInFlightRequests({} as never, async () => {});

    const { exit, exited } = captureExit();
    const startedReal = Date.now();
    void startShutdown('SIGTERM', { now: Date.now, exit, flushErrors: async () => {} });

    expect(await exitWithin(exited, 2_000)).toBe(0);
    expect(Date.now() - startedReal).toBeGreaterThanOrEqual(RESPONSE_FLUSH_GRACE_MS - 50);
  });

  test('exits only after the error flush has finished', async () => {
    let finishFlush!: () => void;
    const flushTimeouts: number[] = [];
    const flushErrors = (timeoutMs: number) => {
      flushTimeouts.push(timeoutMs);
      return new Promise<void>((resolve) => { finishFlush = resolve; });
    };

    const { exit, exited, code } = captureExit();
    void startShutdown('SIGTERM', { now: Date.now, exit, flushErrors });

    expect(await exitWithin(exited, 100)).toBe('still running');
    expect(code()).toBeUndefined();
    expect(flushTimeouts).toEqual([ERROR_FLUSH_TIMEOUT_MS]);

    finishFlush();
    expect(await exitWithin(exited, 500)).toBe(0);
  });

  test('a quiet machine exits at once with code 0', async () => {
    const clock = fakeClock(Date.now());
    const { exit, exited } = captureExit();
    const startedReal = Date.now();
    const run = startShutdown('SIGINT', { now: clock.now, exit, flushErrors: async () => {} });

    expect(await exitWithin(exited, 100)).toBe(0);
    await run;
    expect(Date.now() - startedReal).toBeLessThan(100);
    expect(shutdownEvents()).toEqual(['machine.shutdown']);
    expect(logged('machine.shutdown')).toMatchObject({
      signal: 'SIGINT',
      shutdownAt: new Date(clock.now()).toISOString(),
    });
  });
});
