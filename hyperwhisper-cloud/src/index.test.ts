// THE SERVICE ENTRY POINT (src/index.ts)
//
// Every route in this service already has a suite. The file that WIRES them —
// the CORS allow-list, the two unauthenticated Fly endpoints, the 405 fallback,
// the error handler that must not echo an upstream message, and the SIGTERM
// drain that decides whether a charge survives a deploy — had none. It was the
// only file in `src/` that `bun test src --coverage` never loaded at all.
//
// Three mechanics make it testable in-process:
//
// 1. `./lib/redis` is replaced with mock.module before `./index` is imported,
//    the same way every route suite here does it. `isIPBlocked` is the lever
//    the error-handler test pulls: /usage calls it before anything else and
//    does not catch it. The factory SPREADS the real module and overrides only
//    that one function — bun's registry is process-wide, so a factory that
//    lists exports deletes the rest for every file that loads after this one
//    (lib/gcs-storage.test.ts installs its google-auth override the same way).
//
// 2. `process.exit` is replaced for the shutdown tests. `gracefulShutdown`
//    ends in `process.exit(0)`, which would take the whole test run with it.
//
// 3. `process.emit('SIGTERM')` only runs the listeners — it does not signal the
//    process — so the real handler that index.ts registered is what runs.
//
// Ordering note: `shuttingDown` latches on the first signal, so the shutdown
// describe block is LAST and its two tests are in the order they appear.

import { afterAll, afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { websocket as honoBunWebsocket } from 'hono/bun';
import * as realRedis from './lib/redis';

// ---------------------------------------------------------------------------
// Redis: the I/O edge /usage reaches before anything else. Blocked is off by
// default; a test that wants the failure path sets `ipBlockedError`.
// ---------------------------------------------------------------------------
let ipBlockedError: Error | null = null;
let ipBlockChecks = 0;

mock.module('./lib/redis', () => ({
  ...realRedis,
  isIPBlocked: async () => {
    ipBlockChecks += 1;
    if (ipBlockedError) throw ipBlockedError;
    return false;
  },
}));

// PORT is read once, when the module is evaluated, so pin it before the import.
const originalPort = process.env.PORT;
delete process.env.PORT;

const server = (await import('./index')).default;
const { deductCredits } = await import('./middleware/credits');
const { reportLatencySamples } = await import('./lib/latency-report');

const originalFetch = globalThis.fetch;
const originalExit = process.exit;
const originalEnv = {
  FLY_REGION: process.env.FLY_REGION,
  FLY_APP_NAME: process.env.FLY_APP_NAME,
  HYPERWHISPER_INTERNAL_SECRET: process.env.HYPERWHISPER_INTERNAL_SECRET,
  NEXTJS_LICENSE_API_URL: process.env.NEXTJS_LICENSE_API_URL,
};

/** Every console.log/console.error event name the app emitted, in order. */
let logEvents: string[] = [];
let logPayloads: Array<{ event: string; args: unknown[] }> = [];
const realLog = console.log;
const realError = console.error;

beforeEach(() => {
  ipBlockedError = null;
  ipBlockChecks = 0;
  logEvents = [];
  logPayloads = [];
  console.log = (...args: unknown[]) => {
    logEvents.push(String(args[0]));
    logPayloads.push({ event: String(args[0]), args });
  };
  console.error = (...args: unknown[]) => {
    logEvents.push(String(args[0]));
    logPayloads.push({ event: String(args[0]), args });
  };
  // Any network call is a failure unless a test opts into one. /health and
  // /warmup are defined by NOT making one.
  globalThis.fetch = (async (input: RequestInfo | URL) => {
    throw new Error(`Unexpected fetch: ${String(input)}`);
  }) as unknown as typeof fetch;
});

afterEach(() => {
  console.log = realLog;
  console.error = realError;
  globalThis.fetch = originalFetch;
  for (const [key, value] of Object.entries(originalEnv)) {
    if (value === undefined) delete process.env[key];
    else process.env[key] = value;
  }
});

afterAll(() => {
  process.exit = originalExit;
  if (originalPort === undefined) delete process.env.PORT;
  else process.env.PORT = originalPort;
});

function send(path: string, init: RequestInit = {}): Promise<Response> {
  return server.fetch(new Request(`http://transcribe.test${path}`, init)) as Promise<Response>;
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function jsonOk(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'content-type': 'application/json' },
  });
}

// ---------------------------------------------------------------------------
// CORS. A header missing from `allowHeaders` is not a CORS warning — the
// browser's preflight fails and the POST never leaves, so the whole request
// disappears before any route sees it.
// ---------------------------------------------------------------------------
describe('CORS preflight', () => {
  const REQUIRED_ALLOW_HEADERS = [
    'content-type',
    'x-stt-provider',
    'x-stt-model',
    'x-stt-domain',
    'x-latency-opt-out',
    'x-hyperwhisper-platform',
    'x-hyperwhisper-version',
  ];

  test('allows every custom header the clients send on /transcribe', async () => {
    const response = await send('/transcribe', {
      method: 'OPTIONS',
      headers: {
        Origin: 'https://www.hyperwhisper.com',
        'Access-Control-Request-Method': 'POST',
        'Access-Control-Request-Headers': 'x-stt-provider, x-stt-model',
      },
    });

    const allowed = (response.headers.get('Access-Control-Allow-Headers') ?? '')
      .split(',')
      .map((name) => name.trim().toLowerCase());

    for (const header of REQUIRED_ALLOW_HEADERS) {
      expect(allowed).toContain(header);
    }
  });

  test('advertises the origin and the methods the service actually serves', async () => {
    const response = await send('/transcribe', {
      method: 'OPTIONS',
      headers: {
        Origin: 'https://www.hyperwhisper.com',
        'Access-Control-Request-Method': 'POST',
      },
    });

    expect(response.headers.get('Access-Control-Allow-Origin')).toBe('*');
    const methods = (response.headers.get('Access-Control-Allow-Methods') ?? '')
      .split(',')
      .map((method) => method.trim().toUpperCase());
    expect(methods).toContain('GET');
    expect(methods).toContain('POST');
    expect(methods).toContain('OPTIONS');
  });

  test('answers a preflight without a body and without running the route', async () => {
    const response = await send('/transcribe', {
      method: 'OPTIONS',
      headers: {
        Origin: 'https://www.hyperwhisper.com',
        'Access-Control-Request-Method': 'POST',
      },
    });

    expect(response.status).toBe(204);
    expect(await response.text()).toBe('');
    expect(ipBlockChecks).toBe(0);
  });
});

// ---------------------------------------------------------------------------
// /health is what Fly polls. If it ever needed a credential or an upstream, a
// licensing outage would take every machine out of rotation.
// ---------------------------------------------------------------------------
describe('GET /health', () => {
  test('reports ok with the machine region and a parseable ISO timestamp', async () => {
    process.env.FLY_REGION = 'lhr';

    const response = await send('/health');
    const body = await response.json() as { status: string; region: string; timestamp: string };

    expect(response.status).toBe(200);
    expect(body.status).toBe('ok');
    expect(body.region).toBe('lhr');
    expect(body.timestamp).toMatch(/^\d{4}-\d{2}-\d{2}T[\d:.]+Z$/);
    expect(Math.abs(Date.parse(body.timestamp) - Date.now())).toBeLessThan(60_000);
  });

  test('says local when the machine is not on Fly', async () => {
    delete process.env.FLY_REGION;

    const body = await (await send('/health')).json() as { region: string };

    expect(body.region).toBe('local');
  });

  test('needs no licence key and touches neither Redis nor the licence API', async () => {
    const response = await send('/health');

    expect(response.status).toBe(200);
    expect(ipBlockChecks).toBe(0);
  });
});

// ---------------------------------------------------------------------------
// /warmup exists to open the TLS/HTTP2 connection on hotkey-down. A cached
// answer would satisfy the client without opening anything.
// ---------------------------------------------------------------------------
describe('GET /warmup', () => {
  test('returns an empty 204 that must not be cached', async () => {
    const response = await send('/warmup');

    expect(response.status).toBe(204);
    expect(response.headers.get('Cache-Control')).toBe('no-store');
    expect(await response.text()).toBe('');
  });

  test('needs no licence key and touches neither Redis nor the licence API', async () => {
    const response = await send('/warmup');

    expect(response.status).toBe(204);
    expect(ipBlockChecks).toBe(0);
  });
});

// ---------------------------------------------------------------------------
// The fallback. It is 405 and plain text on purpose: it matches what the
// Cloudflare Worker this service replaced returned, and the installed clients
// branch on that status.
// ---------------------------------------------------------------------------
describe('unmatched requests', () => {
  test.each([
    ['GET', '/nope'],
    ['POST', '/health'],
    ['GET', '/transcribe'],
    ['DELETE', '/transcribe'],
    ['POST', '/ws/streaming-deepgram'],
  ])('answers %s %s with a plain-text 405', async (method, path) => {
    const response = await send(path, { method });

    expect(response.status).toBe(405);
    expect(await response.text()).toBe('Method not allowed');
    expect(response.headers.get('content-type')).toContain('text/plain');
  });

  test('never reaches Redis or the licence API on an unmatched path', async () => {
    await send('/nope');

    expect(ipBlockChecks).toBe(0);
  });
});

// ---------------------------------------------------------------------------
// The error handler. An unhandled throw here carries env-var names, upstream
// provider bodies and licence keys; the client gets an id to quote instead.
// ---------------------------------------------------------------------------
describe('unhandled errors', () => {
  const LEAKY = 'UPSTASH_REDIS_CLOUD_URL and UPSTASH_REDIS_CLOUD_TOKEN are required';

  test('answers 500 with a generic message and an error id, never the raw cause', async () => {
    ipBlockedError = new Error(LEAKY);

    const response = await send('/usage?license_key=HW-ROUTINE-FIXTURE');
    const raw = await response.text();
    const body = JSON.parse(raw) as { error: string; message: string; error_id: string };

    expect(response.status).toBe(500);
    expect(body.error).toBe('Internal server error');
    expect(body.message).toBe('An unexpected error occurred. Please try again.');
    expect(body.error_id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);
    expect(raw).not.toContain('UPSTASH');
    expect(raw).not.toContain(LEAKY);
  });

  test('logs the full cause server-side under the same id the client was given', async () => {
    ipBlockedError = new Error(LEAKY);

    const body = await (await send('/usage?license_key=HW-ROUTINE-FIXTURE')).json() as { error_id: string };

    const logged = logPayloads.find((entry) => entry.event.startsWith('Unhandled error'));
    expect(logged).toBeDefined();
    expect(logged!.event).toContain(body.error_id);
    expect(logged!.args[1]).toBe(ipBlockedError);
  });

  test('gives each failure its own id so two reports are distinguishable', async () => {
    ipBlockedError = new Error(LEAKY);

    const first = await (await send('/usage?license_key=HW-ROUTINE-FIXTURE')).json() as { error_id: string };
    const second = await (await send('/usage?license_key=HW-ROUTINE-FIXTURE')).json() as { error_id: string };

    expect(first.error_id).not.toBe(second.error_id);
  });
});

// ---------------------------------------------------------------------------
// The Bun server export. `websocket` missing from this object does not fail a
// build — the two /ws/streaming-* routes just stop upgrading at runtime.
// ---------------------------------------------------------------------------
describe('the exported Bun server', () => {
  test('serves 8080 when Fly sets no PORT', () => {
    expect(server.port).toBe(8080);
  });

  test('exports hono/bun\'s websocket handler so the live routes can upgrade', () => {
    expect(server.websocket).toBe(honoBunWebsocket);
    expect(typeof server.fetch).toBe('function');
  });
});

// ---------------------------------------------------------------------------
// Graceful shutdown. Fly sends SIGTERM on every deploy and scale-down. A credit
// deduction is fired WITHOUT being awaited, after the response is flushed, so
// exiting without draining hands the user a transcript it never charged for.
//
// `shuttingDown` latches on the first signal, so these two tests run in order
// and must be the last in the file.
// ---------------------------------------------------------------------------
describe('graceful shutdown', () => {
  test('waits for an in-flight deduction and latency report before it exits', async () => {
    process.env.FLY_REGION = 'lhr';
    // reportLatencySamples publishes only from the production Fly app.
    process.env.FLY_APP_NAME = 'hyperwhisper-transcribe';
    process.env.HYPERWHISPER_INTERNAL_SECRET = 'not-a-real-secret';
    process.env.NEXTJS_LICENSE_API_URL = 'https://licence.test';

    // Both writes take 40 ms. An exit that does not drain fires in the same
    // tick as the signal, so both flags are still false when it lands.
    let deductionReachedApi = false;
    let reportReachedApi = false;
    globalThis.fetch = (async (input: RequestInfo | URL) => {
      const url = String(input);
      await delay(40);
      if (url.includes('/api/license/credits')) {
        deductionReachedApi = true;
        return jsonOk({});
      }
      if (url.includes('/api/internal/latency')) {
        reportReachedApi = true;
        return jsonOk({});
      }
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    void deductCredits(
      { identifier: 'HW-ROUTINE-FIXTURE', credits: 10, licenseKey: 'HW-ROUTINE-FIXTURE' },
      0.05,
      { requestId: 'shutdown-test' },
      '203.0.113.1',
    );
    reportLatencySamples([
      { provider: 'deepgram', model: 'nova-3', latencyMs: 900, ok: true, attempt: 1, audioSeconds: 5 },
    ]);

    const exited = new Promise<number>((resolve) => {
      process.exit = ((code?: number) => { resolve(code ?? 0); }) as never;
    });
    process.emit('SIGTERM' as never);
    const code = await exited;

    expect(deductionReachedApi).toBe(true);
    expect(reportReachedApi).toBe(true);
    expect(code).toBe(0);

    expect(logEvents).toContain('machine.shutdown');
    expect(logPayloads.find((entry) => entry.event === 'machine.shutdown')!.args[1])
      .toMatchObject({ signal: 'SIGTERM' });
    expect(logPayloads.find((entry) => entry.event === 'machine.shutdown_drained_deductions')!.args[1])
      .toEqual({ count: 1 });
    expect(logPayloads.find((entry) => entry.event === 'machine.shutdown_drained_latency_reports')!.args[1])
      .toEqual({ count: 1 });
  });

  test('ignores a second signal, so SIGINT after SIGTERM cannot exit mid-drain', async () => {
    let exitedAgain = false;
    process.exit = (() => { exitedAgain = true; }) as never;

    process.emit('SIGINT' as never);
    await delay(50);

    expect(exitedAgain).toBe(false);
    expect(logEvents).not.toContain('machine.shutdown');
  });
});
