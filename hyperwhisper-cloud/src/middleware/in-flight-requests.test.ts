// IN-FLIGHT REQUEST TRACKING (issue #2031)
//
// The shutdown wait in `index.ts` is only as good as this count: a request it
// misses is cut by the exit, and a request it never lets go of holds every
// deploy for the full 100 s. These tests drive the middleware through a real
// Hono app, the way `index.ts` mounts it, with handlers that stay in flight
// until the test lets them go.

import { afterEach, beforeEach, describe, expect, test } from 'bun:test';
import { Hono } from 'hono';
import {
  beginRequestShutdown,
  IN_FLIGHT_REQUEST_WAIT_MS,
  inFlightRequestCount,
  resetInFlightRequestsForTests,
  RESPONSE_FLUSH_GRACE_MS,
  trackInFlightRequests,
  waitForInFlightRequests,
  waitForResponseFlush,
} from './in-flight-requests';

const realLog = console.log;
let logEvents: string[] = [];

/** A handler that stays in flight until `release()` is called. */
function gate(): { promise: Promise<void>; release: () => void } {
  let release!: () => void;
  const promise = new Promise<void>((resolve) => { release = resolve; });
  return { promise, release };
}

let slow = gate();
let handlerRuns = 0;

function makeApp(): Hono {
  const app = new Hono();
  app.post('/transcribe', trackInFlightRequests, async (c) => {
    handlerRuns += 1;
    await slow.promise;
    return c.json({ text: 'hello' });
  });
  app.post('/boom', trackInFlightRequests, async () => {
    throw new Error('handler failed');
  });
  app.onError((_err, c) => c.json({ error: 'Internal server error' }, 500));
  return app;
}

function post(app: Hono, path: string): Promise<Response> {
  return Promise.resolve(app.fetch(new Request(`http://transcribe.test${path}`, { method: 'POST', body: 'audio' })));
}

/** Let the request reach its handler. */
function tick(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

beforeEach(() => {
  resetInFlightRequestsForTests();
  slow = gate();
  handlerRuns = 0;
  logEvents = [];
  console.log = (...args: unknown[]) => { logEvents.push(String(args[0])); };
});

afterEach(() => {
  console.log = realLog;
  slow.release();
  resetInFlightRequestsForTests();
});

describe('trackInFlightRequests', () => {
  test('the shutdown wait is 100 s, under the 115 s drain budget', () => {
    expect(IN_FLIGHT_REQUEST_WAIT_MS).toBe(100_000);
  });

  test('a quiet machine does not wait at all', async () => {
    const startedAt = Date.now();
    expect(await waitForInFlightRequests(IN_FLIGHT_REQUEST_WAIT_MS)).toBe(0);
    expect(Date.now() - startedAt).toBeLessThan(50);
  });

  test('the wait holds until an in-flight request has its response, then resolves', async () => {
    const app = makeApp();
    const response = post(app, '/transcribe');
    await tick();
    expect(inFlightRequestCount()).toBe(1);

    let waitDone = false;
    const waited = waitForInFlightRequests(IN_FLIGHT_REQUEST_WAIT_MS).then((count) => {
      waitDone = true;
      return count;
    });
    await new Promise((resolve) => setTimeout(resolve, 30));
    expect(waitDone).toBe(false);

    slow.release();
    expect(await waited).toBe(1);
    expect(inFlightRequestCount()).toBe(0);
    const res = await response;
    expect(res.status).toBe(200);
    expect(await res.json()).toEqual({ text: 'hello' });
  });

  test('after the signal, a new request is refused with 503 before its handler runs, and the in-flight one still finishes', async () => {
    const app = makeApp();
    const inFlight = post(app, '/transcribe');
    await tick();
    expect(handlerRuns).toBe(1);

    beginRequestShutdown();
    const refused = await post(app, '/transcribe');
    expect(refused.status).toBe(503);
    expect(refused.headers.get('Retry-After')).toBe('1');
    expect(await refused.json()).toEqual({
      error: 'Service unavailable',
      message: 'This server is restarting. Please try again.',
    });
    expect(handlerRuns).toBe(1);
    expect(inFlightRequestCount()).toBe(1);
    expect(logEvents).toContain('machine.shutdown_refused_request');

    slow.release();
    expect(await waitForInFlightRequests(IN_FLIGHT_REQUEST_WAIT_MS)).toBe(1);
    expect((await inFlight).status).toBe(200);
  });

  test('the wait gives up at its deadline while a request is still running', async () => {
    const app = makeApp();
    void post(app, '/transcribe');
    await tick();

    const startedAt = Date.now();
    expect(await waitForInFlightRequests(40)).toBe(1);
    const elapsed = Date.now() - startedAt;
    expect(elapsed).toBeGreaterThanOrEqual(35);
    expect(elapsed).toBeLessThan(500);
    expect(inFlightRequestCount()).toBe(1);
  });

  test('a deadline already past does not wait', async () => {
    const app = makeApp();
    void post(app, '/transcribe');
    await tick();

    const startedAt = Date.now();
    expect(await waitForInFlightRequests(-5_000)).toBe(1);
    expect(Date.now() - startedAt).toBeLessThan(50);
  });

  test('a handler that throws still leaves the count, so it cannot hold a deploy for 100 s', async () => {
    const app = makeApp();
    const res = await post(app, '/boom');
    expect(res.status).toBe(500);
    expect(inFlightRequestCount()).toBe(0);
  });

  test('one wait covers several requests and resolves only when the last one ends', async () => {
    const first = gate();
    const second = gate();
    const multi = new Hono();
    multi.post('/a', trackInFlightRequests, async (c) => { await first.promise; return c.text('a'); });
    multi.post('/b', trackInFlightRequests, async (c) => { await second.promise; return c.text('b'); });
    const a = post(multi, '/a');
    const b = post(multi, '/b');
    await tick();
    expect(inFlightRequestCount()).toBe(2);

    let waitDone = false;
    const waited = waitForInFlightRequests(IN_FLIGHT_REQUEST_WAIT_MS).then((count) => {
      waitDone = true;
      return count;
    });
    first.release();
    await a;
    await tick();
    expect(waitDone).toBe(false);

    second.release();
    await b;
    expect(await waited).toBe(2);
  });
});

describe('waitForResponseFlush', () => {
  test('no reply returned yet: the exit is not held', async () => {
    const startedAt = Date.now();
    await waitForResponseFlush(Date.now() + 10_000);
    expect(Date.now() - startedAt).toBeLessThan(50);
  });

  test('a reply just returned holds the exit for the flush grace', async () => {
    const app = makeApp();
    slow.release();
    expect((await post(app, '/transcribe')).status).toBe(200);

    const startedAt = Date.now();
    await waitForResponseFlush(Date.now() + 10_000);
    const elapsed = Date.now() - startedAt;
    expect(elapsed).toBeGreaterThanOrEqual(RESPONSE_FLUSH_GRACE_MS - 50);
    expect(elapsed).toBeLessThan(RESPONSE_FLUSH_GRACE_MS + 300);
  });

  test('a reply that failed in its handler still counts as a reply to flush', async () => {
    const app = makeApp();
    expect((await post(app, '/boom')).status).toBe(500);

    const startedAt = Date.now();
    await waitForResponseFlush(Date.now() + 10_000);
    expect(Date.now() - startedAt).toBeGreaterThanOrEqual(RESPONSE_FLUSH_GRACE_MS - 50);
  });

  test('the grace never runs past the deadline', async () => {
    const app = makeApp();
    slow.release();
    await post(app, '/transcribe');

    const startedAt = Date.now();
    await waitForResponseFlush(Date.now() + 40);
    expect(Date.now() - startedAt).toBeLessThan(200);
  });

  test('a reply older than the grace does not hold the exit', async () => {
    const app = makeApp();
    slow.release();
    await post(app, '/transcribe');
    await new Promise((resolve) => setTimeout(resolve, RESPONSE_FLUSH_GRACE_MS + 50));

    const startedAt = Date.now();
    await waitForResponseFlush(Date.now() + 10_000);
    expect(Date.now() - startedAt).toBeLessThan(50);
  });

  test('a refused request holds the exit too, so its 503 is written out', async () => {
    const app = makeApp();
    beginRequestShutdown();
    expect((await post(app, '/transcribe')).status).toBe(503);

    const startedAt = Date.now();
    await waitForResponseFlush(Date.now() + 10_000);
    expect(Date.now() - startedAt).toBeGreaterThanOrEqual(RESPONSE_FLUSH_GRACE_MS - 50);
  });
});
