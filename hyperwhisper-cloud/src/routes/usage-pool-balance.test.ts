// GET /usage on a pooled key reports the pool's balance when it is lower (#2922).
//
// One user owns keys A and B on one credit pool. A spend on A rewrites
// `license:A` and `credit_pool_balance:<pool>` (redis-core `cacheLicense`), but
// not `license:B`. The credit-hold script already caps B's holds by the pool
// entry, so /usage on B must not report the credits those holds refuse.
//
// The licence cache below is a Map. `cacheLicense` mirrors redis-core's: it
// writes `license:<key>` AND the pool entry. `redis.get()` hands the route a
// client that reads the same Map, which is the one read the fix makes.
// `mock.module` is process-wide in bun (see post-process.test.ts); this file's
// mock is the live one while its tests run, alone or in the full suite.
import { afterAll, afterEach, beforeEach, expect, mock, test } from 'bun:test';
import { Hono } from 'hono';

const store = new Map<string, unknown>();
let poolReadThrows = false;
const poolReads: string[] = [];

mock.module('../lib/redis', () => ({
  redis: {
    get: () => ({
      get: async (key: string) => {
        poolReads.push(key);
        if (poolReadThrows) throw new Error('upstash unavailable');
        return store.get(key) ?? null;
      },
    }),
  },
  isIPBlocked: async () => false,
  getCachedLicense: async (key: string) => store.get(`license:${key}`) ?? null,
  cacheLicense: async (key: string, lic: { credits: number; cachedAt: string; creditPoolId?: string }) => {
    store.set(`license:${key}`, lic);
    if (lic.creditPoolId) store.set(`credit_pool_balance:${lic.creditPoolId}`, { credits: lic.credits, cachedAt: lic.cachedAt });
  },
}));

const { usageRoute, poolCappedCredits } = await import('./usage');
const { deductCredits } = await import('../middleware/credits');

const originalFetch = globalThis.fetch;
const POOL = 'a'.repeat(64);

function failOnFetch(): void {
  globalThis.fetch = (async (input: unknown) => {
    throw new Error(`unexpected fetch ${String(input)}`);
  }) as unknown as typeof fetch;
}

async function usageFor(key: string): Promise<{ status: number; body: Record<string, unknown> }> {
  const app = new Hono();
  app.get('/usage', usageRoute);
  const res = await app.request(`/usage?account_key=${key}`);
  return { status: res.status, body: (await res.json()) as Record<string, unknown> };
}

function seedKey(key: string, credits: number, creditPoolId?: string): void {
  store.set(`license:${key}`, {
    isValid: true,
    credits,
    cachedAt: new Date().toISOString(),
    issuedAtMs: 1,
    ...(creditPoolId ? { creditPoolId } : {}),
  });
}

beforeEach(() => {
  store.clear();
  poolReads.length = 0;
  poolReadThrows = false;
  failOnFetch();
});

afterEach(() => {
  globalThis.fetch = originalFetch;
});

afterAll(() => {
  globalThis.fetch = originalFetch;
});

// The issue's repro, kept as written apart from the mock's read path.
test('/usage on key B reflects a spend on sibling key A of the same pool', async () => {
  const now = new Date().toISOString();
  seedKey('KEY-A', 50, POOL);
  seedKey('KEY-B', 50, POOL);
  store.set(`credit_pool_balance:${POOL}`, { credits: 50, cachedAt: now });

  // Key A spends 50 credits; the site answers the pool now has 0.
  globalThis.fetch = (async (input: unknown) => {
    const url = String(input);
    if (url.includes('/api/license/credits')) {
      return new Response(JSON.stringify({ success: true, credits_remaining: 0, credit_pool_id: POOL }), { status: 200 });
    }
    throw new Error(`unexpected fetch ${url}`);
  }) as unknown as typeof fetch;
  await deductCredits(
    { identifier: 'KEY-A', licenseKey: 'KEY-A', credits: 50, creditPoolId: POOL } as never,
    0.5,
    { endpoint: '/transcribe' },
    '1.1.1.1',
    { credits: 50 } as never,
  );
  expect((store.get(`credit_pool_balance:${POOL}`) as { credits: number }).credits).toBe(0);
  // B's own entry still says 50: the stale read the fix has to cap.
  expect((store.get('license:KEY-B') as { credits: number }).credits).toBe(50);

  failOnFetch();
  const { status, body } = await usageFor('KEY-B');
  expect(status).toBe(200);
  expect(body.credits_remaining).toBe(0);
  expect(body.minutes_remaining).toBe(0);
});

test('a pooled key with no pool entry reports its own credits', async () => {
  seedKey('KEY-B', 50, POOL);
  const { status, body } = await usageFor('KEY-B');
  expect(status).toBe(200);
  expect(body.credits_remaining).toBe(50);
  expect(poolReads).toEqual([`credit_pool_balance:${POOL}`]);
});

test('a pool entry higher than the key never raises it', async () => {
  seedKey('KEY-B', 20, POOL);
  store.set(`credit_pool_balance:${POOL}`, { credits: 80, cachedAt: new Date().toISOString() });
  const { body } = await usageFor('KEY-B');
  expect(body.credits_remaining).toBe(20);
});

test('a pool entry stored as a JSON string is still read', async () => {
  seedKey('KEY-B', 50, POOL);
  store.set(`credit_pool_balance:${POOL}`, JSON.stringify({ credits: 7.5, cachedAt: 'x' }));
  const { body } = await usageFor('KEY-B');
  expect(body.credits_remaining).toBe(7.5);
});

test('a Redis error on the pool read falls back to the key credits', async () => {
  seedKey('KEY-B', 50, POOL);
  store.set(`credit_pool_balance:${POOL}`, { credits: 0, cachedAt: new Date().toISOString() });
  poolReadThrows = true;
  const { status, body } = await usageFor('KEY-B');
  expect(status).toBe(200);
  expect(body.credits_remaining).toBe(50);
});

test('an unpooled key is not capped and makes no pool read', async () => {
  seedKey('KEY-C', 50);
  store.set(`credit_pool_balance:${POOL}`, { credits: 0, cachedAt: new Date().toISOString() });
  const { body } = await usageFor('KEY-C');
  expect(body.credits_remaining).toBe(50);
  expect(poolReads).toEqual([]);
});

// A force refresh is a fresh site read for this key (and `validateAuth(…, true)`
// skips the cache), so it is authoritative and is not capped by an older pool entry.
test('force_refresh reports the site balance without a pool read', async () => {
  seedKey('KEY-B', 50, POOL);
  store.set(`credit_pool_balance:${POOL}`, { credits: 0, cachedAt: new Date().toISOString() });
  globalThis.fetch = (async (input: unknown) => {
    const url = String(input);
    if (url.includes('/api/license/credits')) {
      return new Response(JSON.stringify({ credits: 30, credit_pool_id: POOL }), { status: 200 });
    }
    throw new Error(`unexpected fetch ${url}`);
  }) as unknown as typeof fetch;
  const app = new Hono();
  app.get('/usage', usageRoute);
  const res = await app.request('/usage?account_key=KEY-B&force_refresh=true');
  const body = (await res.json()) as Record<string, unknown>;
  expect(res.status).toBe(200);
  expect(body.credits_remaining).toBe(30);
  expect(poolReads).toEqual([]);
});

// The helper directly, through its store parameter: these cases do not depend
// on which suite's `../lib/redis` mock is live.
test('poolCappedCredits fails open on every unusable pool entry', async () => {
  const reader = (value: unknown) => () => ({ get: async () => value as never });
  expect(await poolCappedCredits(50, POOL, reader({ credits: 0, cachedAt: 'x' }))).toBe(0);
  expect(await poolCappedCredits(50, POOL, reader({ credits: 12.3, cachedAt: 'x' }))).toBe(12.3);
  expect(await poolCappedCredits(50, POOL, reader({ credits: 90, cachedAt: 'x' }))).toBe(50);
  expect(await poolCappedCredits(50, POOL, reader(null))).toBe(50);
  expect(await poolCappedCredits(50, POOL, reader('not json'))).toBe(50);
  expect(await poolCappedCredits(50, POOL, reader({ credits: '0' }))).toBe(50);
  expect(await poolCappedCredits(50, POOL, reader({ credits: Number.NaN }))).toBe(50);
  expect(await poolCappedCredits(50, POOL, reader({ credits: Infinity }))).toBe(50);
  expect(await poolCappedCredits(50, POOL, reader({}))).toBe(50);
  // Redis unconfigured: the real getter throws before any read.
  expect(await poolCappedCredits(50, POOL, () => { throw new Error('UPSTASH_REDIS_CLOUD_URL required'); })).toBe(50);
  // A getter that hands back no client (what a `redis: {}` mock amounts to).
  expect(await poolCappedCredits(50, POOL, () => undefined as never)).toBe(50);
});
