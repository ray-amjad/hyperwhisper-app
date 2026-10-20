// The credit-hold Lua script run for real, against a local redis-server, through
// the same `createUpstashCreditHoldStore` production uses (only the transport is
// swapped: Bun's RESP client instead of Upstash's REST one). The same scenarios
// run against the in-memory twin the route suites use, so the two cannot drift.
//
// Skipped where no `redis-server` binary is on PATH.

import { afterAll, beforeAll, beforeEach, describe, expect, test } from 'bun:test';
import {
  createUpstashCreditHoldStore,
  holdKeys,
  type CreditHoldStore,
  type EvalClient,
  type ReserveHoldRequest,
} from './credit-hold-store';
import { createMemoryCreditHoldStore } from './credit-hold-store-memory';
import { cacheLicense, type RedisStore } from './redis-core';

const redisServerPath = Bun.which('redis-server');
const PORT = 20_000 + Math.floor(Math.random() * 20_000);

let server: ReturnType<typeof Bun.spawn> | undefined;
let client: InstanceType<typeof Bun.RedisClient> | undefined;

function evalClient(): EvalClient {
  return {
    async eval<TArgs extends unknown[], TData = unknown>(script: string, keys: string[], args: TArgs): Promise<TData> {
      return await client!.send('EVAL', [script, String(keys.length), ...keys, ...args.map(String)]) as TData;
    },
  };
}

/** `redis-core`'s store over the same redis-server, serialising as Upstash does. */
function cacheStore(): RedisStore {
  return {
    async get<TData = unknown>(key: string): Promise<TData | null> {
      const raw = await client!.send('GET', [key]) as string | null;
      return raw === null ? null : JSON.parse(raw) as TData;
    },
    async set(key: string, value: unknown, opts: { ex: number }): Promise<unknown> {
      return await client!.send('SET', [key, JSON.stringify(value), 'EX', String(opts.ex)]);
    },
  };
}

const ACCOUNT = 'acct-lua';
const NOW = 1_700_000_000_000;

function request(overrides: Partial<ReserveHoldRequest>): ReserveHoldRequest {
  return {
    account: ACCOUNT,
    holdId: 'h1',
    minMilli: 1000,
    maxMilli: 1000,
    balanceMilli: 1000,
    nowMs: NOW,
    ttlMs: 60_000,
    ...overrides,
  };
}

interface StoreUnderTest {
  name: string;
  store: () => CreditHoldStore;
  reset: () => Promise<void>;
  setCachedBalance?: (credits: number) => Promise<void>;
}

let memory = createMemoryCreditHoldStore();
let memoryCachedBalance: number | undefined;

const stores: StoreUnderTest[] = [
  {
    name: 'in-memory twin',
    store: () => memory,
    reset: async () => {
      memoryCachedBalance = undefined;
      memory = createMemoryCreditHoldStore({ cachedBalanceMilli: () => memoryCachedBalance });
    },
    setCachedBalance: async (credits) => { memoryCachedBalance = Math.round(credits * 1000); },
  },
];

if (redisServerPath) {
  stores.push({
    name: 'Lua script on redis-server',
    store: () => createUpstashCreditHoldStore(evalClient),
    reset: async () => { await client!.send('FLUSHALL', []); },
    // The shape `cacheLicense` writes: Upstash serialises the object to JSON.
    setCachedBalance: async (credits) => {
      await client!.send('SET', [holdKeys(ACCOUNT).license, JSON.stringify({ isValid: true, credits, cachedAt: 'x' })]);
    },
  });
}

beforeAll(async () => {
  if (!redisServerPath) return;
  server = Bun.spawn([redisServerPath, '--port', String(PORT), '--save', '', '--appendonly', 'no'], {
    stdout: 'ignore',
    stderr: 'ignore',
  });
  client = new Bun.RedisClient(`redis://127.0.0.1:${PORT}`);
  for (let attempt = 0; attempt < 100; attempt += 1) {
    try {
      await client.send('PING', []);
      return;
    } catch {
      await Bun.sleep(50);
    }
  }
  throw new Error('redis-server did not start');
});

afterAll(() => {
  client?.close();
  server?.kill();
});

for (const target of stores) {
  describe(`credit hold store: ${target.name}`, () => {
    beforeEach(async () => {
      await target.reset();
    });

    test('grants a hold the balance covers', async () => {
      const result = await target.store().reserve(request({}));
      expect(result).toEqual({ granted: true, holdMilli: 1000, availableMilli: 1000 });
    });

    test('of many concurrent reservations on a balance that covers one, exactly one is granted', async () => {
      const results = await Promise.all(Array.from({ length: 20 }, (_, index) => (
        target.store().reserve(request({ holdId: `h${index}`, minMilli: 3000, maxMilli: 3000, balanceMilli: 4000 }))
      )));
      expect(results.filter((result) => result.granted)).toHaveLength(1);
      expect(results.filter((result) => !result.granted).every((result) => result.availableMilli === 1000)).toBe(true);
    });

    test('counts other holds, not its own, and resizes in place', async () => {
      const store = target.store();
      await store.reserve(request({ holdId: 'a', minMilli: 2000, maxMilli: 2000, balanceMilli: 5000 }));
      // Own hold resized from 2 to 5 would need 7 with the old hold counted.
      const grown = await store.reserve(request({ holdId: 'a', minMilli: 5000, maxMilli: 5000, balanceMilli: 5000 }));
      expect(grown).toEqual({ granted: true, holdMilli: 5000, availableMilli: 5000 });
      const other = await store.reserve(request({ holdId: 'b', minMilli: 100, maxMilli: 100, balanceMilli: 5000 }));
      expect(other).toEqual({ granted: false, holdMilli: 0, availableMilli: 0 });
    });

    test('a range grant takes what is left, floored to a tenth of a credit', async () => {
      const store = target.store();
      await store.reserve(request({ holdId: 'a', minMilli: 1250, maxMilli: 1250, balanceMilli: 3000 }));
      const partial = await store.reserve(request({ holdId: 'b', minMilli: 500, maxMilli: 5000, balanceMilli: 3000 }));
      expect(partial).toEqual({ granted: true, holdMilli: 1700, availableMilli: 1700 });
    });

    test('a refusal keeps the hold it already had', async () => {
      const store = target.store();
      await store.reserve(request({ holdId: 'a', minMilli: 2000, maxMilli: 2000, balanceMilli: 3000 }));
      const refused = await store.reserve(request({ holdId: 'a', minMilli: 4000, maxMilli: 4000, balanceMilli: 3000 }));
      expect(refused).toEqual({ granted: false, holdMilli: 2000, availableMilli: 3000 });
      const other = await store.reserve(request({ holdId: 'b', minMilli: 1100, maxMilli: 1100, balanceMilli: 3000 }));
      expect(other.granted).toBe(false);
    });

    test('release frees the credits, and is harmless twice', async () => {
      const store = target.store();
      await store.reserve(request({ holdId: 'a', minMilli: 1000, maxMilli: 1000 }));
      await store.release(ACCOUNT, 'a');
      await store.release(ACCOUNT, 'a');
      const next = await store.reserve(request({ holdId: 'b' }));
      expect(next.granted).toBe(true);
    });

    test('an expired hold is swept before the next reservation counts', async () => {
      const store = target.store();
      await store.reserve(request({ holdId: 'leaked', ttlMs: 1000 }));
      expect((await store.reserve(request({ holdId: 'b', nowMs: NOW + 999 }))).granted).toBe(false);
      expect((await store.reserve(request({ holdId: 'b', nowMs: NOW + 1000 }))).granted).toBe(true);
    });

    test('a cached balance below the one the caller authed with wins', async () => {
      await target.setCachedBalance!(0.5);
      const refused = await target.store().reserve(request({ minMilli: 1000, maxMilli: 1000, balanceMilli: 1000 }));
      expect(refused).toEqual({ granted: false, holdMilli: 0, availableMilli: 500 });
    });

    test('a cached balance above it does not raise it', async () => {
      await target.setCachedBalance!(50);
      const refused = await target.store().reserve(request({ minMilli: 2000, maxMilli: 2000, balanceMilli: 1000 }));
      expect(refused.granted).toBe(false);
      expect(refused.availableMilli).toBe(1000);
    });

    test('holds are per account', async () => {
      const store = target.store();
      await store.reserve(request({ holdId: 'a' }));
      expect((await store.reserve(request({ account: 'acct-other', holdId: 'b' }))).granted).toBe(true);
    });
  });
}

describe.skipIf(!redisServerPath)('credit hold keys on redis-server', () => {
  test('both hold keys carry a TTL past the longest hold, so an idle account leaves nothing behind', async () => {
    await client!.send('FLUSHALL', []);
    await createUpstashCreditHoldStore(evalClient).reserve(request({ ttlMs: 600_000 }));
    const keys = holdKeys(ACCOUNT);
    const holdsTtl = Number(await client!.send('TTL', [keys.holds]));
    const expiryTtl = Number(await client!.send('TTL', [keys.expiry]));
    expect(holdsTtl).toBeGreaterThan(600);
    expect(expiryTtl).toBeGreaterThan(600);
  });

  test('a pooled hold counts on the pool but reads the cached balance of the key that asked (#2026)', async () => {
    await client!.send('FLUSHALL', []);
    const store = createUpstashCreditHoldStore(evalClient);
    const pool = 'pool:' + 'a'.repeat(64);
    // Key A's cache is fresher (lower) than the balance it authed with.
    await client!.send('SET', [holdKeys(pool, 'key-a').license, JSON.stringify({ isValid: true, credits: 1.5, cachedAt: 'x' })]);

    const first = await store.reserve(request({ account: pool, licenseKey: 'key-a', holdId: 'a', minMilli: 1000, maxMilli: 1000, balanceMilli: 4000 }));
    expect(first).toEqual({ granted: true, holdMilli: 1000, availableMilli: 1500 });
    // Key B on the same pool sees key A's hold: 4 - 1 = 3 left, not 4.
    const second = await store.reserve(request({ account: pool, licenseKey: 'key-b', holdId: 'b', minMilli: 3500, maxMilli: 3500, balanceMilli: 4000 }));
    expect(second.granted).toBe(false);
    expect(second.availableMilli).toBe(3000);

    expect(await client!.send('HKEYS', [holdKeys(pool).holds])).toEqual(['a']);
    expect(Number(await client!.send('EXISTS', ['credit_holds:key-a']))).toBe(0);
    await store.release(pool, 'a');
    expect(Number(await client!.send('EXISTS', [holdKeys(pool).holds]))).toBe(0);
  });

  test('a pool balance below the key\'s own cached balance caps the grant (#2026)', async () => {
    await client!.send('FLUSHALL', []);
    const pool = 'pool:' + 'c'.repeat(64);
    const keys = holdKeys(pool, 'key-b');
    expect(keys.poolBalance).toBe('credit_pool_balance:' + 'c'.repeat(64));
    await client!.send('SET', [keys.license, JSON.stringify({ isValid: true, credits: 10, cachedAt: 'x' })]);
    await client!.send('SET', [keys.poolBalance, JSON.stringify({ credits: 6, cachedAt: 'y' })]);

    const store = createUpstashCreditHoldStore(evalClient);
    const refused = await store.reserve(request({ account: pool, licenseKey: 'key-b', holdId: 'b', minMilli: 7000, maxMilli: 7000, balanceMilli: 10_000 }));
    expect(refused).toEqual({ granted: false, holdMilli: 0, availableMilli: 6000 });
    const clamped = await store.reserve(request({ account: pool, licenseKey: 'key-b', holdId: 'b', minMilli: 1000, maxMilli: 7000, balanceMilli: 10_000 }));
    expect(clamped).toEqual({ granted: true, holdMilli: 6000, availableMilli: 6000 });
  });

  test('key A spends and settles, then key B on the same pool cannot reserve the spent credits (#2026)', async () => {
    await client!.send('FLUSHALL', []);
    const poolId = 'd'.repeat(64);
    const pool = 'pool:' + poolId;
    const store = createUpstashCreditHoldStore(evalClient);
    // Both keys authed while the user had 10 credits.
    await cacheLicense(cacheStore, 'key-a', { isValid: true, credits: 10, cachedAt: 't0', creditPoolId: poolId });
    await cacheLicense(cacheStore, 'key-b', { isValid: true, credits: 10, cachedAt: 't0', creditPoolId: poolId });

    // A holds 4, is charged 4, and the post-charge rewrite caches 6 for A.
    expect((await store.reserve(request({ account: pool, licenseKey: 'key-a', holdId: 'a', minMilli: 4000, maxMilli: 4000, balanceMilli: 10_000 }))).granted).toBe(true);
    await cacheLicense(cacheStore, 'key-a', { isValid: true, credits: 6, cachedAt: 't1', creditPoolId: poolId });
    await store.release(pool, 'a');

    // B's own entry still says 10, and so does the balance it authed with.
    const b = await store.reserve(request({ account: pool, licenseKey: 'key-b', holdId: 'b', minMilli: 7000, maxMilli: 7000, balanceMilli: 10_000 }));
    expect(b).toEqual({ granted: false, holdMilli: 0, availableMilli: 6000 });

    // A top-up seen on B lifts the pool again: the minimum does not strand it.
    await cacheLicense(cacheStore, 'key-b', { isValid: true, credits: 20, cachedAt: 't2', creditPoolId: poolId });
    const afterTopUp = await store.reserve(request({ account: pool, licenseKey: 'key-b', holdId: 'b', minMilli: 7000, maxMilli: 7000, balanceMilli: 20_000 }));
    expect(afterTopUp.granted).toBe(true);
  });

  test('an unpooled hold reads no pool entry', async () => {
    await client!.send('FLUSHALL', []);
    expect(holdKeys(ACCOUNT).poolBalance).toBe(holdKeys(ACCOUNT).license);
    const result = await createUpstashCreditHoldStore(evalClient).reserve(request({}));
    expect(result.granted).toBe(true);
  });

  test('an unparseable pool entry is ignored, not fatal', async () => {
    await client!.send('FLUSHALL', []);
    const pool = 'pool:' + 'e'.repeat(64);
    await client!.send('SET', [holdKeys(pool, 'key-a').poolBalance, 'not json']);
    const result = await createUpstashCreditHoldStore(evalClient).reserve(request({ account: pool, licenseKey: 'key-a' }));
    expect(result.granted).toBe(true);
  });

  test('an unparseable cache entry is ignored, not fatal', async () => {
    await client!.send('FLUSHALL', []);
    await client!.send('SET', [holdKeys(ACCOUNT).license, 'not json']);
    const result = await createUpstashCreditHoldStore(evalClient).reserve(request({}));
    expect(result.granted).toBe(true);
  });
});
