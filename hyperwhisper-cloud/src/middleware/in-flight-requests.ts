// IN-FLIGHT REQUEST TRACKING FOR A GRACEFUL SHUTDOWN (issue #2031)
//
// Fly sends SIGTERM on every deploy and scale-down, then SIGKILL after
// `kill_timeout` (120 s). Before this file, `gracefulShutdown` in `index.ts`
// awaited only the charge/latency/hold drains, and each returns at once when
// nothing is pending. A `/transcribe` still waiting on its provider has no
// pending charge yet, so the process exited under it on every deploy.
//
// Ray's decision (2026-10-19, inbox ask #1590):
//   - On SIGTERM, stop taking new `/transcribe` and `/post-process` requests.
//   - Wait for the in-flight ones for up to 100 s.
//   - Then run the drains in what is left of the 115 s budget.
//   - A quiet machine still exits at once.
//   (Live streaming sessions are still ended at once, as before.)
//
// `trackInFlightRequests` wraps the two routes in `index.ts`. It counts a
// request from the moment it enters until its handler has returned the
// Response. Both routes build a JSON body in full before they return, and fire
// their credit deduction (registered with `drainPendingDeductions`) and release
// their hold inside the handler, so once the count reaches zero every charge
// those requests made is already pending for the drain that follows.
//
// After `beginRequestShutdown`, a new request on these routes is refused with
// 503 and `Retry-After: 1` before it reads its body: work started now would
// only be cut by the exit. Fly normally stops routing to a stopping machine,
// so this is the rare request already on its way.

import type { Context, Next } from 'hono';
import { errorResponse } from '../lib/responses';

/** How long a shutdown waits for in-flight requests, measured from the signal. */
export const IN_FLIGHT_REQUEST_WAIT_MS = 100_000;

/**
 * The count drops when the handler RETURNS its Response; Bun still has to write
 * it to the socket. An exit in the same tick can cut the reply the wait was
 * for, so a wait that saw requests finish holds this much longer (never past
 * its deadline).
 */
export const RESPONSE_FLUSH_GRACE_MS = 500;

let inFlight = 0;
let shuttingDown = false;
/** Resolvers of `waitForInFlightRequests` calls, run when the count hits 0. */
let idleWaiters: Array<() => void> = [];

function settleIdleWaiters(): void {
  const waiters = idleWaiters;
  idleWaiters = [];
  for (const resolve of waiters) resolve();
}

export async function trackInFlightRequests(c: Context, next: Next): Promise<Response | void> {
  if (shuttingDown) {
    console.log('machine.shutdown_refused_request', { path: c.req.path });
    const response = errorResponse(
      503,
      'Service unavailable',
      'This server is restarting. Please try again.',
    );
    response.headers.set('Retry-After', '1');
    return response;
  }

  inFlight += 1;
  try {
    await next();
  } finally {
    inFlight -= 1;
    if (inFlight === 0) settleIdleWaiters();
  }
}

/** Latch: from now on the tracked routes refuse new requests. */
export function beginRequestShutdown(): void {
  shuttingDown = true;
}

export function inFlightRequestCount(): number {
  return inFlight;
}

/**
 * Resolve when no tracked request is in flight (plus RESPONSE_FLUSH_GRACE_MS),
 * or after `timeoutMs`, whichever comes first. Resolves to the number that
 * were in flight when it was called; 0 resolves at once.
 */
export async function waitForInFlightRequests(timeoutMs: number): Promise<number> {
  const pendingCount = inFlight;
  if (pendingCount === 0) return 0;

  const deadline = Date.now() + timeoutMs;
  let timer: ReturnType<typeof setTimeout> | undefined;
  const idle = new Promise<'idle'>((resolve) => { idleWaiters.push(() => resolve('idle')); });
  const timeout = new Promise<'timeout'>((resolve) => {
    timer = setTimeout(() => resolve('timeout'), Math.max(0, timeoutMs));
  });
  const outcome = await Promise.race([idle, timeout]);
  clearTimeout(timer);
  if (outcome === 'idle') {
    const graceMs = Math.min(RESPONSE_FLUSH_GRACE_MS, deadline - Date.now());
    if (graceMs > 0) await new Promise((resolve) => setTimeout(resolve, graceMs));
  }
  return pendingCount;
}

/** Test-only: bun runs every test file in one process, and the latch is module state. */
export function resetInFlightRequestsForTests(): void {
  inFlight = 0;
  shuttingDown = false;
  settleIdleWaiters();
}
