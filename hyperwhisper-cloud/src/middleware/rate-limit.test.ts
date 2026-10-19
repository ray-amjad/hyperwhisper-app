// REQUEST RATE LIMITS (middleware/rate-limit.ts, #2010)
//
// Driven through the REAL app (`src/index.ts`'s `fetch`), so each test proves
// the limiter is wired onto the route, not just that the helper counts. The
// only seams are:
//
//   - the limiter store, swapped with `setRateLimitStoreForTests` for an
//     in-memory sliding window with the same bucket sizes as production;
//   - `../lib/redis`, so auth answers from a "cached" licence instead of the
//     licence API. The factory SPREADS the real module: bun's registry is
//     process-wide, and a factory that lists exports deletes the rest for every
//     later file (see index.test.ts).
//
// The cached balance is 0 credits. A request that clears auth and the key limit
// therefore stops at the credit check (402) or, for /usage, answers 200 — no
// provider and no licence-API call is ever made, so the N allowed requests are
// cheap and the N+1st is the only one that can be a 429.
//
// The last block runs the real @upstash/ratelimit against a fake Redis, to pin
// the fail-open behaviour on an Upstash error and on its own timeout.

import { afterAll, afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import type { Redis } from '@upstash/redis';
import * as realRedis from '../lib/redis';

let cachedCredits = 0;

mock.module('../lib/redis', () => ({
  ...realRedis,
  isIPBlocked: async () => false,
  getCachedLicense: async () => ({ isValid: true, credits: cachedCredits, cachedAt: 'cached' }),
  cacheLicense: async () => {},
}));

const { default: server } = await import('../index');
const {
  RATE_LIMITS,
  RATE_LIMIT_WINDOW_SECONDS,
  checkRateLimit,
  createUpstashRateLimitStore,
  keyIdentifier,
  setRateLimitStoreForTests,
} = await import('./rate-limit');
type RateLimitStore = import('./rate-limit').RateLimitStore;
type RateLimitBucket = import('./rate-limit').RateLimitBucket;

const WINDOW_MS = RATE_LIMIT_WINDOW_SECONDS * 1000;

/** An in-memory sliding window with production's bucket sizes and a settable clock. */
function createMemoryStore(clock: { now: number }) {
  const hits = new Map<string, number[]>();
  const calls: Array<{ bucket: RateLimitBucket; identifier: string }> = [];
  const store: RateLimitStore = {
    async limit(bucket, identifier) {
      calls.push({ bucket, identifier });
      const id = `${bucket}:${identifier}`;
      const live = (hits.get(id) ?? []).filter((at) => at > clock.now - WINDOW_MS);
      const success = live.length < RATE_LIMITS[bucket];
      if (success) live.push(clock.now);
      hits.set(id, live);
      return { success, reset: (live[0] ?? clock.now) + WINDOW_MS };
    },
    async peek(bucket, identifier) {
      const live = (hits.get(`${bucket}:${identifier}`) ?? []).filter((at) => at > clock.now - WINDOW_MS);
      return { remaining: RATE_LIMITS[bucket] - live.length, reset: (live[0] ?? clock.now) + WINDOW_MS };
    },
  };
  return { store, calls };
}

const originalFetch = globalThis.fetch;
const realLog = console.log;
const realWarn = console.warn;
const realError = console.error;
let warnings: string[] = [];
let logLines: string[] = [];
let clock: { now: number };
let memory: ReturnType<typeof createMemoryStore>;
const realDateNow = Date.now;

beforeEach(() => {
  cachedCredits = 0;
  clock = { now: 1_800_000_000_000 };
  Date.now = () => clock.now;
  memory = createMemoryStore(clock);
  setRateLimitStoreForTests(memory.store);
  warnings = [];
  logLines = [];
  console.log = (...args: unknown[]) => { logLines.push(args.map(String).join(' ')); };
  console.warn = (...args: unknown[]) => { warnings.push(args.map(String).join(' ')); };
  console.error = () => {};
  // No request here may reach a provider or the licence API.
  globalThis.fetch = (async (input: RequestInfo | URL) => {
    throw new Error(`Unexpected fetch: ${String(input)}`);
  }) as unknown as typeof fetch;
});

afterEach(() => {
  Date.now = realDateNow;
  setRateLimitStoreForTests(null);
  console.log = realLog;
  console.warn = realWarn;
  console.error = realError;
  globalThis.fetch = originalFetch;
});

afterAll(() => {
  setRateLimitStoreForTests(null);
});

let ipCounter = 0;
/** A fresh client IP per test, so the per-IP bucket never carries over. */
function freshIP(): string {
  ipCounter += 1;
  return `203.0.113.${ipCounter % 250}`;
}

let keyCounter = 0;
function freshKey(): string {
  keyCounter += 1;
  return `HW-RATE-LIMIT-FIXTURE-${keyCounter}`;
}

type Family = 'transcribe' | 'post-process' | 'assistant' | 'usage' | 'ws-deepgram' | 'ws-gemini';

/** One request that clears auth (licensed key, valid shape) on each route family. */
function licensedRequest(family: Family, key: string, ip: string): Request {
  const base = 'http://transcribe.test';
  const ipHeader = { 'Fly-Client-IP': ip };
  switch (family) {
    case 'transcribe':
      return new Request(`${base}/transcribe?account_key=${key}&language=en-US`, {
        method: 'POST',
        headers: { ...ipHeader, 'Content-Type': 'audio/wav', 'X-STT-Provider': 'deepgram', 'Content-Length': '2048' },
        body: new Uint8Array(2048),
      });
    case 'post-process': {
      const body = JSON.stringify({ text: 'hello world', prompt: 'fix it', account_key: key });
      return new Request(`${base}/post-process`, {
        method: 'POST',
        headers: { ...ipHeader, 'Content-Type': 'application/json', 'Content-Length': String(body.length) },
        body,
      });
    }
    case 'assistant': {
      const form = new FormData();
      form.append('messages', JSON.stringify([{ role: 'user', content: 'hi' }]));
      form.append('account_key', key);
      return new Request(`${base}/assistant`, {
        method: 'POST',
        headers: { ...ipHeader, 'Content-Length': '4096' },
        body: form,
      });
    }
    case 'usage':
      return new Request(`${base}/usage?account_key=${key}`, { headers: ipHeader });
    case 'ws-deepgram':
      return new Request(`${base}/ws/streaming-deepgram?account_key=${key}`, {
        headers: { ...ipHeader, Upgrade: 'websocket' },
      });
    case 'ws-gemini':
      return new Request(`${base}/ws/streaming-gemini-transcribe?account_key=${key}`, {
        headers: { ...ipHeader, Upgrade: 'websocket' },
      });
  }
}

/** A request that is refused BEFORE auth on each family: it still costs an IP slot. */
function anonymousRequest(family: Family, ip: string): Request {
  const base = 'http://transcribe.test';
  const headers = { 'Fly-Client-IP': ip };
  switch (family) {
    case 'transcribe':
      return new Request(`${base}/transcribe`, { method: 'POST', headers: { ...headers, 'Content-Type': 'text/plain' }, body: 'x' });
    case 'post-process':
      return new Request(`${base}/post-process`, { method: 'POST', headers: { ...headers, 'Content-Type': 'text/plain' }, body: 'x' });
    case 'assistant':
      return new Request(`${base}/assistant`, { method: 'POST', headers, body: 'x' });
    case 'usage':
      return new Request(`${base}/usage`, { headers });
    case 'ws-deepgram':
      return new Request(`${base}/ws/streaming-deepgram`, { headers });
    case 'ws-gemini':
      return new Request(`${base}/ws/streaming-gemini-transcribe`, { headers });
  }
}

const PATHS: Record<Family, string> = {
  transcribe: '/transcribe',
  'post-process': '/post-process',
  assistant: '/assistant',
  usage: '/usage',
  'ws-deepgram': '/ws/streaming-deepgram',
  'ws-gemini': '/ws/streaming-gemini-transcribe',
};

async function send(request: Request): Promise<Response> {
  return server.fetch(request) as Promise<Response>;
}

async function expectRateLimited(response: Response, maxRetryAfter = RATE_LIMIT_WINDOW_SECONDS): Promise<number> {
  expect(response.status).toBe(429);
  const retryAfter = Number(response.headers.get('Retry-After'));
  expect(Number.isInteger(retryAfter)).toBe(true);
  expect(retryAfter).toBeGreaterThanOrEqual(1);
  expect(retryAfter).toBeLessThanOrEqual(maxRetryAfter);
  expect(response.headers.get('content-type')).toContain('application/json');
  const body = await response.json() as { error: string; message: string; retry_after_seconds: number };
  expect(body.error).toBe('Too many requests');
  expect(typeof body.message).toBe('string');
  expect(body.retry_after_seconds).toBe(retryAfter);
  return retryAfter;
}

const ALL_FAMILIES: Family[] = ['transcribe', 'post-process', 'assistant', 'usage', 'ws-deepgram', 'ws-gemini'];
const HTTP_FAMILIES: Family[] = ['transcribe', 'post-process', 'assistant', 'usage'];
const WS_FAMILIES: Family[] = ['ws-deepgram', 'ws-gemini'];

describe('per-IP limit (120/min, before auth)', () => {
  test.each(ALL_FAMILIES)('%s: request 121 inside the window is a 429 with Retry-After', async (family) => {
    const ip = freshIP();
    for (let i = 0; i < RATE_LIMITS.ip; i += 1) {
      const response = await send(anonymousRequest(family, ip));
      expect(response.status).not.toBe(429);
    }

    const limited = await send(anonymousRequest(family, ip));
    await expectRateLimited(limited);
    // Before auth: the IP bucket is the only one this caller touched.
    expect(new Set(memory.calls.map((call) => call.bucket))).toEqual(new Set(['ip']));
    expect(logLines.some((line) => line.includes('"event":"rate_limit.rejected"') && line.includes(`"endpoint":"${PATHS[family]}"`))).toBe(true);
  });

  test('a different IP is not limited by the first one, and the window frees up', async () => {
    const ip = freshIP();
    for (let i = 0; i < RATE_LIMITS.ip; i += 1) await send(anonymousRequest('usage', ip));
    await expectRateLimited(await send(anonymousRequest('usage', ip)));

    expect((await send(anonymousRequest('usage', freshIP()))).status).toBe(401);

    clock.now += WINDOW_MS + 1;
    expect((await send(anonymousRequest('usage', ip))).status).toBe(401);
  });

  test('Retry-After counts down to when the oldest request leaves the window', async () => {
    const ip = freshIP();
    for (let i = 0; i < RATE_LIMITS.ip; i += 1) await send(anonymousRequest('usage', ip));
    clock.now += 45_000;
    expect(await expectRateLimited(await send(anonymousRequest('usage', ip)))).toBe(15);
  });

  test('the health, warmup and CORS preflight paths are never counted', async () => {
    await send(new Request('http://transcribe.test/health'));
    await send(new Request('http://transcribe.test/warmup'));
    await send(new Request('http://transcribe.test/transcribe', {
      method: 'OPTIONS',
      headers: { Origin: 'https://www.hyperwhisper.com', 'Access-Control-Request-Method': 'POST' },
    }));
    expect(memory.calls).toEqual([]);
  });
});

describe('per-key limit (60/min, after auth)', () => {
  test.each(HTTP_FAMILIES)('%s: request 61 on one key is a 429 with Retry-After, before any credit work', async (family) => {
    const key = freshKey();
    // Spread over many IPs so only the key bucket can be the one that fills.
    for (let i = 0; i < RATE_LIMITS.key; i += 1) {
      const response = await send(licensedRequest(family, key, freshIP()));
      expect(response.status).toBe(family === 'usage' ? 200 : 402);
    }

    const limited = await send(licensedRequest(family, key, freshIP()));
    await expectRateLimited(limited);

    // Counted under the key's hash, never the key itself.
    const keyCalls = memory.calls.filter((call) => call.bucket === 'key');
    expect(keyCalls).toHaveLength(RATE_LIMITS.key + 1);
    expect(keyCalls.every((call) => call.identifier === keyIdentifier(key))).toBe(true);
    expect(memory.calls.some((call) => call.identifier.includes(key))).toBe(false);

    // Another key is unaffected.
    expect((await send(licensedRequest(family, freshKey(), freshIP()))).status).toBe(family === 'usage' ? 200 : 402);
  });

  test('/usage?force_refresh=true: request 61 is refused before it calls the site', async () => {
    const key = freshKey();
    const siteCalls: string[] = [];
    globalThis.fetch = (async (input: RequestInfo | URL) => {
      siteCalls.push(String(input));
      return new Response(JSON.stringify({ credits: 5 }), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }) as unknown as typeof fetch;
    const refresh = () => send(new Request(`http://transcribe.test/usage?account_key=${key}&force_refresh=true`, {
      headers: { 'Fly-Client-IP': freshIP() },
    }));
    for (let i = 0; i < RATE_LIMITS.key; i += 1) {
      expect((await refresh()).status).toBe(200);
    }
    expect(siteCalls).toHaveLength(RATE_LIMITS.key);

    await expectRateLimited(await refresh());
    expect(siteCalls).toHaveLength(RATE_LIMITS.key);
  });

  test('a rejected key never gets a bucket', async () => {
    const response = await send(new Request('http://transcribe.test/usage', { headers: { 'Fly-Client-IP': freshIP() } }));
    expect(response.status).toBe(401);
    expect(memory.calls.filter((call) => call.bucket !== 'ip')).toEqual([]);
  });

  test('every route shares one key budget', async () => {
    const key = freshKey();
    for (let i = 0; i < RATE_LIMITS.key; i += 1) {
      await send(licensedRequest(HTTP_FAMILIES[i % HTTP_FAMILIES.length], key, freshIP()));
    }
    await expectRateLimited(await send(licensedRequest('ws-deepgram', key, freshIP())));
  });
});

describe('per-key /ws/streaming-* upgrade limit (10/min)', () => {
  test.each(WS_FAMILIES)('%s: upgrade 11 on one key is a 429 with Retry-After', async (family) => {
    const key = freshKey();
    for (let i = 0; i < RATE_LIMITS.ws_upgrade; i += 1) {
      const response = await send(licensedRequest(family, key, freshIP()));
      expect(response.status).toBe(402);
    }

    await expectRateLimited(await send(licensedRequest(family, key, freshIP())));
    // The refused upgrade was turned away by the read, before either bucket counted it.
    expect(memory.calls.filter((call) => call.bucket === 'ws_upgrade')).toHaveLength(RATE_LIMITS.ws_upgrade);
    const id = keyIdentifier(key);
    expect((await memory.store.peek!('key', id)).remaining).toBe(RATE_LIMITS.key - RATE_LIMITS.ws_upgrade);
  });

  test('an upgrade the key bucket refuses does not spend an upgrade slot', async () => {
    const key = freshKey();
    for (let i = 0; i < RATE_LIMITS.key; i += 1) {
      expect((await send(licensedRequest('usage', key, freshIP()))).status).toBe(200);
    }
    for (let i = 0; i < 3; i += 1) {
      await expectRateLimited(await send(licensedRequest('ws-deepgram', key, freshIP())));
    }
    expect((await memory.store.peek!('ws_upgrade', keyIdentifier(key))).remaining).toBe(RATE_LIMITS.ws_upgrade);
  });

  test('a store with no peek still counts the buckets in order', async () => {
    const counted = createMemoryStore(clock);
    setRateLimitStoreForTests({ limit: counted.store.limit });
    const key = freshKey();
    for (let i = 0; i < RATE_LIMITS.ws_upgrade; i += 1) {
      expect((await send(licensedRequest('ws-deepgram', key, freshIP()))).status).toBe(402);
    }
    await expectRateLimited(await send(licensedRequest('ws-deepgram', key, freshIP())));
  });

  test('both live routes share the upgrade budget, and HTTP requests on the key still pass', async () => {
    const key = freshKey();
    for (let i = 0; i < RATE_LIMITS.ws_upgrade; i += 1) {
      await send(licensedRequest(WS_FAMILIES[i % 2], key, freshIP()));
    }
    await expectRateLimited(await send(licensedRequest('ws-gemini', key, freshIP())));
    expect((await send(licensedRequest('usage', key, freshIP()))).status).toBe(200);
  });
});

describe('fail open', () => {
  test.each(ALL_FAMILIES)('%s: a store that throws serves the request and logs a warning', async (family) => {
    setRateLimitStoreForTests({
      async limit() { throw new Error('fetch failed: connect ECONNREFUSED'); },
    });
    const response = await send(licensedRequest(family, freshKey(), freshIP()));
    expect(response.status).toBe(family === 'usage' ? 200 : 402);
    expect(warnings.some((line) => line.includes('"event":"rate_limit.unavailable"') && line.includes('"failOpen":true'))).toBe(true);
  });

  test('an Upstash timeout verdict ({ success: true, reason: "timeout" }) serves and logs', async () => {
    setRateLimitStoreForTests({
      async limit() { return { success: true, reset: 0, reason: 'timeout' }; },
    });
    expect((await send(licensedRequest('usage', freshKey(), freshIP()))).status).toBe(200);
    expect(warnings.some((line) => line.includes('"reason":"timeout"'))).toBe(true);
  });

  test('a logged store error does not carry the account key', async () => {
    const key = freshKey();
    setRateLimitStoreForTests({
      async limit(_bucket, identifier) { throw new Error(`ERR command was: ["evalsha","x","${identifier}","${key}"]`); },
    });
    await send(licensedRequest('usage', key, freshIP()));
    expect(warnings.length).toBeGreaterThan(0);
    expect(warnings.join('\n')).not.toContain(key);
  });
});

describe('the production store (real @upstash/ratelimit)', () => {
  test('no Upstash env: the getter throws and the request is not limited', async () => {
    setRateLimitStoreForTests(createUpstashRateLimitStore(() => {
      throw new Error('UPSTASH_REDIS_CLOUD_URL and UPSTASH_REDIS_CLOUD_TOKEN are required');
    }));
    expect(await checkRateLimit('ip', '203.0.113.9')).toEqual({ limited: false });
  });

  test('Upstash erroring: the limiter rejects and the check fails open', async () => {
    const failing = new Proxy({}, {
      get: () => async () => { throw new Error('Upstash 502'); },
    }) as unknown as Redis;
    setRateLimitStoreForTests(createUpstashRateLimitStore(() => failing));
    expect(await checkRateLimit('key', 'abc')).toEqual({ limited: false });
    expect(warnings.some((line) => line.includes('"reason":"store_error"'))).toBe(true);
  });

  test('Upstash hanging: the library\'s own timeout lets the request through', async () => {
    Date.now = realDateNow;
    const hanging = new Proxy({}, {
      get: () => () => new Promise(() => {}),
    }) as unknown as Redis;
    setRateLimitStoreForTests(createUpstashRateLimitStore(() => hanging, 20));
    expect(await checkRateLimit('ws_upgrade', 'abc')).toEqual({ limited: false });
    expect(warnings.some((line) => line.includes('"reason":"timeout"'))).toBe(true);
  });
});
