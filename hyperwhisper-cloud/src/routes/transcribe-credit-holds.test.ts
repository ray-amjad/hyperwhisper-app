// Issue #2008: concurrent requests on one key must not together spend more than
// its balance. Every request below goes through the real route — real auth, real
// `validateCredits`, real `deductCredits` — with only the edges faked: the
// licence cache (`../lib/redis`), the upstreams and licence API (`fetch`), and
// the hold store, an in-memory twin of the Lua script
// (`lib/credit-hold-store-memory.ts`, checked against a real redis-server in
// `lib/credit-hold-store.test.ts`).

import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { Hono } from 'hono';
import { createMemoryCreditHoldStore, type MemoryCreditHoldStore } from '../lib/credit-hold-store-memory';
import type { CreditHoldStore } from '../lib/credit-hold-store';

let cachedCredits = 1000;
mock.module('../lib/redis', () => ({
  // Full export surface: a partial redis mock is process-wide in bun and breaks
  // every suite that loads after this one.
  redis: {},
  isIPBlocked: async () => false,
  getCachedLicense: async () => ({ isValid: true, credits: cachedCredits, cachedAt: 'cached' }),
  cacheLicense: async () => {},
}));

const { transcribeRoute } = await import('./transcribe');
const { postProcessRoute } = await import('./post-process');
const { drainPendingDeductions } = await import('../middleware/credits');
const { drainPendingHoldReleases, setCreditHoldStoreForTests } = await import('../middleware/credit-holds');

const originalFetch = globalThis.fetch;
const originalMetaKey = process.env.META_MODEL_API_KEY;
const originalGroqKey = process.env.GROQ_API_KEY;

const ACCOUNT = 'test-license';
// One minute of canonical Meta Muse audio reserves (and bills) exactly 3 credits.
const ONE_REQUEST_CREDITS = 3;

function buildApp(): Hono {
  const app = new Hono();
  app.post('/transcribe', transcribeRoute);
  app.post('/post-process', postProcessRoute);
  return app;
}

function metaMinuteRequest(): Request {
  const seconds = 60;
  const dataBytes = 16_000 * 2 * seconds;
  const audio = new Uint8Array(44 + dataBytes);
  const view = new DataView(audio.buffer);
  for (const [offset, text] of [[0, 'RIFF'], [8, 'WAVE'], [12, 'fmt '], [36, 'data']] as const) {
    for (let index = 0; index < text.length; index++) view.setUint8(offset + index, text.charCodeAt(index));
  }
  view.setUint32(4, audio.byteLength - 8, true);
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, 1, true);
  view.setUint32(24, 16_000, true);
  view.setUint32(28, 16_000 * 2, true);
  view.setUint16(32, 2, true);
  view.setUint16(34, 16, true);
  view.setUint32(40, dataBytes, true);
  return new Request(`http://localhost/transcribe?account_key=${ACCOUNT}&language=en`, {
    method: 'POST',
    headers: {
      'Content-Type': 'audio/wav',
      'Content-Length': String(audio.byteLength),
      'X-STT-Provider': 'meta',
      'X-STT-Model': 'muse-voice-transcribe-1.0',
    },
    body: audio,
  });
}

interface Charge { license_key: string; amount: number }

let store: MemoryCreditHoldStore;
let charges: Charge[];
/** What the Meta upstream does with each call. Default: a slow, successful minute. */
let metaUpstream: () => Promise<Response>;
let upstreamCalls: number;

function slowMetaSuccess(): Promise<Response> {
  // Slow enough that every concurrent request is past its credit check while
  // the first one is still being transcribed.
  return new Promise((resolve) => setTimeout(() => {
    resolve(Response.json({ transcript: 'served', audioDurationMs: 60_000, turns: [] }));
  }, 25));
}

async function settle(): Promise<void> {
  await drainPendingDeductions(2000);
  await drainPendingHoldReleases(2000);
}

beforeEach(() => {
  cachedCredits = 1000;
  charges = [];
  upstreamCalls = 0;
  metaUpstream = slowMetaSuccess;
  store = createMemoryCreditHoldStore();
  setCreditHoldStoreForTests(store);
  process.env.META_MODEL_API_KEY = 'test-meta-key';
  globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    if (url === 'https://api.meta.ai/v1/asr/transcribe') {
      upstreamCalls += 1;
      return metaUpstream();
    }
    if (url.includes('/api/license/credits')) {
      const charge = JSON.parse(String(init?.body)) as Charge;
      charges.push(charge);
      return Response.json({ credits_remaining: Math.max(0, cachedCredits - charge.amount) });
    }
    throw new Error(`Unexpected fetch: ${url}`);
  }) as unknown as typeof fetch;
});

afterEach(async () => {
  await settle();
  setCreditHoldStoreForTests(null);
  globalThis.fetch = originalFetch;
  if (originalMetaKey === undefined) delete process.env.META_MODEL_API_KEY;
  else process.env.META_MODEL_API_KEY = originalMetaKey;
  if (originalGroqKey === undefined) delete process.env.GROQ_API_KEY;
  else process.env.GROQ_API_KEY = originalGroqKey;
});

describe('concurrent /transcribe on a balance that covers one request (#2008)', () => {
  test('exactly one is served; the rest get the insufficient-credits response', async () => {
    // Covers one 3-credit minute, not two.
    cachedCredits = 4;
    const app = buildApp();
    const N = 6;

    const responses = await Promise.all(
      Array.from({ length: N }, () => app.fetch(metaMinuteRequest())),
    );
    await settle();

    const served = responses.filter((response) => response.status === 200);
    const refused = responses.filter((response) => response.status === 402);
    expect(served).toHaveLength(1);
    expect(refused).toHaveLength(N - 1);
    for (const response of refused) {
      const body = await response.json() as { error: string; credits_remaining: number };
      expect(body.error).toBe('Insufficient credits');
      // What the balance had left once the served request's hold was counted.
      expect(body.credits_remaining).toBe(1);
    }
    // Only the served request reached the vendor, and only it was charged.
    expect(upstreamCalls).toBe(1);
    expect(charges).toEqual([expect.objectContaining({ license_key: ACCOUNT, amount: ONE_REQUEST_CREDITS })]);
    // And nothing is left held once it settled.
    expect(store.outstandingMilli(ACCOUNT)).toBe(0);
  });

  test('a balance that covers two serves two, never more', async () => {
    cachedCredits = 6;
    const app = buildApp();

    const responses = await Promise.all(
      Array.from({ length: 5 }, () => app.fetch(metaMinuteRequest())),
    );
    await settle();

    expect(responses.filter((response) => response.status === 200)).toHaveLength(2);
    expect(responses.filter((response) => response.status === 402)).toHaveLength(3);
    expect(charges.reduce((sum, charge) => sum + charge.amount, 0)).toBe(6);
    expect(store.outstandingMilli(ACCOUNT)).toBe(0);
  });

  test('the credits come back once a request ends, so the next one is served', async () => {
    cachedCredits = 4;
    const app = buildApp();

    expect((await app.fetch(metaMinuteRequest())).status).toBe(200);
    await settle();
    expect((await app.fetch(metaMinuteRequest())).status).toBe(200);
    await settle();

    expect(charges).toHaveLength(2);
    expect(store.outstandingMilli(ACCOUNT)).toBe(0);
  });
});

describe('a hold is released on every way a request ends (#2008)', () => {
  test('the upstream fails: no charge, and the hold is released', async () => {
    metaUpstream = async () => new Response('upstream down', { status: 503 });

    const response = await buildApp().fetch(metaMinuteRequest());
    await settle();

    expect(response.status).toBeGreaterThanOrEqual(500);
    expect(charges).toEqual([]);
    expect(store.outstandingMilli(ACCOUNT)).toBe(0);
    expect(store.calls.filter((call) => call.op === 'release')).toHaveLength(1);
  });

  test('the upstream throws: no charge, and the hold is released', async () => {
    metaUpstream = async () => { throw new TypeError('fetch failed'); };

    const response = await buildApp().fetch(metaMinuteRequest());
    await settle();

    expect(response.status).toBeGreaterThanOrEqual(500);
    expect(charges).toEqual([]);
    expect(store.outstandingMilli(ACCOUNT)).toBe(0);
  });

  test('the licence API refuses the charge: the hold is still released', async () => {
    const baseFetch = globalThis.fetch;
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input).includes('/api/license/credits')) {
        return Response.json({ error: 'boom' }, { status: 500 });
      }
      return baseFetch(input, init);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(metaMinuteRequest());
    await settle();

    expect(response.status).toBe(200);
    expect(store.outstandingMilli(ACCOUNT)).toBe(0);
  });

  test('a request refused for credits leaves nothing held', async () => {
    cachedCredits = 4;
    // Another request already holds most of the balance.
    await store.reserve({
      account: ACCOUNT, holdId: 'other', minMilli: 2000, maxMilli: 2000,
      balanceMilli: 4000, nowMs: Date.now(), ttlMs: 60_000,
    });

    const response = await buildApp().fetch(metaMinuteRequest());
    await settle();

    expect(response.status).toBe(402);
    expect(upstreamCalls).toBe(0);
    expect([...store.holds(ACCOUNT).keys()]).toEqual(['other']);
  });

  test('an expired hold no longer counts against the balance', async () => {
    cachedCredits = 4;
    // A hold left behind by a request that never released it (a crashed machine).
    await store.reserve({
      account: ACCOUNT, holdId: 'leaked', minMilli: 3000, maxMilli: 3000,
      balanceMilli: 4000, nowMs: Date.now() - 60 * 60_000, ttlMs: 60_000,
    });

    const response = await buildApp().fetch(metaMinuteRequest());
    await settle();

    expect(response.status).toBe(200);
    expect(store.outstandingMilli(ACCOUNT)).toBe(0);
  });
});

describe('Redis unavailable fails open (#2008)', () => {
  test('a store error serves the request on the cached-balance rule and logs it', async () => {
    const failing: CreditHoldStore = {
      reserve: async () => { throw new Error('upstash: connection refused'); },
      release: async () => {},
    };
    setCreditHoldStoreForTests(failing);
    const errors: string[] = [];
    const originalError = console.error;
    console.error = (...args: unknown[]) => { errors.push(args.map(String).join(' ')); };
    try {
      const response = await buildApp().fetch(metaMinuteRequest());
      await settle();
      expect(response.status).toBe(200);
    } finally {
      console.error = originalError;
    }
    expect(charges).toHaveLength(1);
    const line = errors.find((entry) => entry.includes('credits_hold.unavailable'));
    expect(line).toBeDefined();
    expect(JSON.parse(line!)).toMatchObject({ operation: 'reserve', reason: 'store_error', failOpen: true });
    expect(line).not.toContain(ACCOUNT);
  });
});

describe('concurrent /post-process shares the same hold rule (#2008)', () => {
  test('a balance that covers one post-process serves one', async () => {
    process.env.GROQ_API_KEY = 'test-groq-key';
    // The post-process estimate is a flat 1 credit.
    cachedCredits = 1.5;
    let llmCalls = 0;
    const baseFetch = globalThis.fetch;
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input).includes('api.groq.com')) {
        llmCalls += 1;
        await new Promise((resolve) => setTimeout(resolve, 25));
        return Response.json({
          choices: [{ message: { content: '<clean>Cleaned text.</clean>' }, finish_reason: 'stop' }],
          usage: { prompt_tokens: 10, completion_tokens: 5 },
        });
      }
      return baseFetch(input, init);
    }) as unknown as typeof fetch;

    const app = buildApp();
    const body = JSON.stringify({ account_key: ACCOUNT, text: 'raw text', prompt: 'Clean it up.' });
    const responses = await Promise.all(Array.from({ length: 4 }, () => app.fetch(new Request('http://localhost/post-process', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Content-Length': String(Buffer.byteLength(body)),
        'X-LLM-Provider': 'groq',
      },
      body,
    }))));
    await settle();

    expect(responses.filter((response) => response.status === 402)).toHaveLength(3);
    expect(responses.filter((response) => response.status === 200)).toHaveLength(1);
    expect(llmCalls).toBe(1);
    expect(charges).toHaveLength(1);
    expect(store.outstandingMilli(ACCOUNT)).toBe(0);
  });
});
