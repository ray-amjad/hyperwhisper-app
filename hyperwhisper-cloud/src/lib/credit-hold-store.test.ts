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

  test('an unparseable cache entry is ignored, not fatal', async () => {
    await client!.send('FLUSHALL', []);
    await client!.send('SET', [holdKeys(ACCOUNT).license, 'not json']);
    const result = await createUpstashCreditHoldStore(evalClient).reserve(request({}));
    expect(result.granted).toBe(true);
  });
});
