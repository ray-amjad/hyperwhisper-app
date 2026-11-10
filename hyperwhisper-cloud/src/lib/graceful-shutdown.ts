// GRACEFUL SHUTDOWN (#2031, #2914)
//
// Fly sends SIGINT/SIGTERM on deploy/scale-down with a grace period before
// SIGKILL (`kill_timeout = '120s'` in fly.prod.toml and fly.staging.toml,
// #2011). In order (Ray, #2031):
//   1. end live streaming sessions at once, so they are billed;
//   2. stop taking new `/transcribe` and `/post-process` requests and wait for
//      the in-flight ones, up to IN_FLIGHT_REQUEST_WAIT_MS (100 s) after the
//      signal — otherwise a transcription still waiting on its provider is cut;
//   3. drain the credit deductions, latency reports and hold releases those
//      left behind, until SHUTDOWN_DRAIN_MS (115 s) after the signal. Credit
//      deductions are fired without awaiting after the response is built, so
//      exiting without draining hands the user a transcript it never paid for.
// Every wait returns as soon as its work settles, so a quiet machine still
// exits at once.
//
// `runGracefulShutdown` takes its clock, its exit and its error flush as
// parameters so the deadlines can be tested (#2914). The one-shot latch that
// ignores a second signal lives in the signal handler in `index.ts`, not here.

import { endActiveStreamingSessions } from '../routes/ws-streaming-shared';
import { drainPendingDeductions } from '../middleware/credits';
import { drainPendingHoldReleases } from '../middleware/credit-holds';
import { drainPendingLatencyReports } from './latency-report';
import {
  beginRequestShutdown,
  IN_FLIGHT_REQUEST_WAIT_MS,
  inFlightRequestCount,
  waitForInFlightRequests,
  waitForResponseFlush,
} from '../middleware/in-flight-requests';
import { flushErrorTracking } from './error-tracking';

/** The whole shutdown budget, measured from the signal. Inside Fly's 120 s. */
export const SHUTDOWN_DRAIN_MS = 115_000;

/** How long the exit waits for queued Sentry events. */
export const ERROR_FLUSH_TIMEOUT_MS = 2_000;

export interface GracefulShutdownOptions {
  /** The clock every deadline is measured on (ms epoch). */
  now?: () => number;
  /** Ends the process. */
  exit?: (code: number) => void;
  /** Sends queued error events, up to `timeoutMs`; a no-op with no DSN. */
  flushErrors?: (timeoutMs: number) => Promise<void>;
}

export async function runGracefulShutdown(
  signal: string,
  {
    now = Date.now,
    exit = (code: number) => process.exit(code),
    flushErrors = flushErrorTracking,
  }: GracefulShutdownOptions = {},
): Promise<void> {
  const startedAt = now();
  beginRequestShutdown();

  console.log('machine.shutdown', {
    signal,
    machineId: process.env.FLY_MACHINE_ID || 'local',
    shutdownAt: new Date(startedAt).toISOString(),
  });

  // A live streaming session is billed only when it ends, and Bun runs no
  // socket close handler on process.exit. End them first, so their charges
  // are already in flight when the deduction drain below awaits them.
  const endedSessions = await endActiveStreamingSessions();
  if (endedSessions > 0) {
    console.log('machine.shutdown_ended_streaming_sessions', { count: endedSessions });
  }

  const waitedRequests = await waitForInFlightRequests(startedAt + IN_FLIGHT_REQUEST_WAIT_MS - now());
  if (waitedRequests > 0) {
    console.log('machine.shutdown_waited_requests', {
      count: waitedRequests,
      stillInFlight: inFlightRequestCount(),
      waitedMs: now() - startedAt,
    });
  }

  // Both drains are fire-and-forget writes made after the response is flushed,
  // so they race the same SIGKILL. Run them together rather than in series —
  // the grace period is one budget, not two.
  // Credit-hold releases too: one left behind narrows the user's balance until
  // its TTL frees it (#2008).
  // Each drain awaits only what is pending when it is called. A request still
  // in flight after the wait can fire a charge while a pass runs, so pass again
  // until one finds nothing pending, or the deadline passes.
  const drainDeadline = startedAt + SHUTDOWN_DRAIN_MS;
  // Error events still queued for Sentry; a no-op with no DSN.
  const errorFlush = flushErrors(ERROR_FLUSH_TIMEOUT_MS);
  let drained = 0;
  let drainedReports = 0;
  let drainedHoldReleases = 0;
  for (let budgetMs = drainDeadline - now(); budgetMs > 0; budgetMs = drainDeadline - now()) {
    const [deductions, reports, holdReleases] = await Promise.all([
      drainPendingDeductions(budgetMs),
      drainPendingLatencyReports(budgetMs),
      drainPendingHoldReleases(budgetMs),
    ]);
    drained += deductions;
    drainedReports += reports;
    drainedHoldReleases += holdReleases;
    if (deductions + reports + holdReleases === 0) break;
  }
  await errorFlush;
  if (drained > 0) {
    console.log('machine.shutdown_drained_deductions', { count: drained });
  }
  if (drainedReports > 0) {
    console.log('machine.shutdown_drained_latency_reports', { count: drainedReports });
  }
  if (drainedHoldReleases > 0) {
    console.log('machine.shutdown_drained_hold_releases', { count: drainedHoldReleases });
  }
  // A reply returned during the drains, or just before the signal, still has
  // to be written out; exiting in the same tick cuts it.
  await waitForResponseFlush(drainDeadline);
  exit(0);
}
