// The credit-hold lifecycle (#2008) below the routes: the request scope, the
// settle hand-off in `deductCredits`, and the overspend write-off. The store is
// the in-memory twin of the Lua script; `../lib/redis` is mocked only because
// `credits.ts` writes the balance cache through it.

import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import type { AuthContext } from './auth';
import type { CreditHoldStore, ReserveHoldRequest, ReserveHoldResult } from '../lib/credit-hold-store';
import { createMemoryCreditHoldStore, type MemoryCreditHoldStore } from '../lib/credit-hold-store-memory';

const cacheWrites: number[] = [];
mock.module('../lib/redis', () => ({
  redis: {},
  isIPBlocked: async () => false,
  getCachedLicense: async () => null,
  cacheLicense: async (_key: string, license: { credits: number }) => { cacheWrites.push(license.credits); },
}));

const { deductCredits, drainPendingDeductions, validateCredits } = await import('./credits');
const {
  CreditHold,
  detachCreditHold,
  drainPendingHoldReleases,
  requestHoldFor,
  reserveSessionHold,
  setCreditHoldStoreForTests,
  withCreditHoldScope,
} = await import('./credit-holds');

const ACCOUNT = 'acct-holds';
const originalFetch = globalThis.fetch;

let store: MemoryCreditHoldStore;
let events: string[];
let shortfalls: Array<Record<string, unknown>>;
const originalWarn = console.warn;

function auth(credits: number): AuthContext {
  return { identifier: ACCOUNT, licenseKey: ACCOUNT, credits };
}

async function settle(): Promise<void> {
  await drainPendingDeductions(2000);
  await drainPendingHoldReleases(2000);
}

beforeEach(() => {
  store = createMemoryCreditHoldStore();
  setCreditHoldStoreForTests(store);
  events = [];
  shortfalls = [];
  cacheWrites.length = 0;
  console.warn = (...args: unknown[]) => {
    const first = args[0];
    if (typeof first === 'string' && first.includes('credits_shortfall')) {
      shortfalls.push(JSON.parse(first) as Record<string, unknown>);
      return;
    }
    originalWarn(...args);
  };
  globalThis.fetch = mock(async (_input: RequestInfo | URL, init?: RequestInit) => {
    const amount = (JSON.parse(String(init?.body)) as { amount: number }).amount;
    events.push(`charge:${amount}`);
    return Response.json({ credits_remaining: 0 });
  }) as unknown as typeof fetch;
});

afterEach(async () => {
  await settle();
  console.warn = originalWarn;
  setCreditHoldStoreForTests(null);
  globalThis.fetch = originalFetch;
});

describe('withCreditHoldScope', () => {
  test('releases the hold when the handler throws', async () => {
    await expect(withCreditHoldScope(async () => {
      expect((await validateCredits(auth(10), 4, 'ip')).ok).toBe(true);
      expect(store.outstandingMilli(ACCOUNT)).toBe(4000);
      throw new Error('handler blew up');
    })).rejects.toThrow('handler blew up');
    await settle();
    expect(store.outstandingMilli(ACCOUNT)).toBe(0);
  });

  test('releases the hold of a request that billed nothing', async () => {
    await withCreditHoldScope(async () => {
      await validateCredits(auth(10), 4, 'ip');
    });
    await settle();
    expect(store.outstandingMilli(ACCOUNT)).toBe(0);
  });

  test('a second check in one request resizes its hold instead of taking another', async () => {
    await withCreditHoldScope(async () => {
      const a = auth(10);
      await validateCredits(a, 2, 'ip');
      await validateCredits(a, 5, 'ip');
      expect(store.holds(ACCOUNT).size).toBe(1);
      expect(store.outstandingMilli(ACCOUNT)).toBe(5000);
    });
  });

  test('a floor check (`hold: false`) takes no hold', async () => {
    await withCreditHoldScope(async () => {
      expect((await validateCredits(auth(10), 2, 'ip', { hold: false })).ok).toBe(true);
      expect(store.outstandingMilli(ACCOUNT)).toBe(0);
    });
  });

  test('outside a scope, validateCredits checks the balance and holds nothing', async () => {
    expect((await validateCredits(auth(10), 4, 'ip')).ok).toBe(true);
    expect(store.calls).toEqual([]);
  });

  test('two keys on one credit pool hold against one balance, and release from the pool (#2026)', async () => {
    const pool = 'b'.repeat(64);
    const keyA: AuthContext = { identifier: 'key-a', licenseKey: 'key-a', credits: 10, creditPoolId: pool };
    const keyB: AuthContext = { identifier: 'key-b', licenseKey: 'key-b', credits: 10, creditPoolId: pool };
    await withCreditHoldScope(async () => {
      expect((await validateCredits(keyA, 6, 'ip')).ok).toBe(true);
      await withCreditHoldScope(async () => {
        const refused = await validateCredits(keyB, 6, 'ip');
        expect(refused.ok).toBe(false);
      });
      expect(store.outstandingMilli(`pool:${pool}`)).toBe(6000);
      expect(store.outstandingMilli('key-a')).toBe(0);
    });
    await settle();
    expect(store.outstandingMilli(`pool:${pool}`)).toBe(0);
    expect(store.calls.filter((call) => call.op === 'release').map((call) => call.account)).toEqual([`pool:${pool}`]);
  });

  test('a detached hold outlives the handler until its owner releases it', async () => {
    const a = auth(10);
    const detached = await withCreditHoldScope(async () => {
      await validateCredits(a, 4, 'ip');
      return detachCreditHold(a);
    });
    await settle();
    expect(store.outstandingMilli(ACCOUNT)).toBe(4000);
    await detached!.release();
    expect(store.outstandingMilli(ACCOUNT)).toBe(0);
  });
});

describe('settling a hold through deductCredits', () => {
  test('releases the hold only after the licence API answered and the cache was rewritten', async () => {
    const order: string[] = [];
    const tracking: CreditHoldStore = {
      reserve: (request) => store.reserve(request),
      release: async (account, holdId) => {
        order.push(`release(cache=${cacheWrites.length})`);
        await store.release(account, holdId);
      },
    };
    setCreditHoldStoreForTests(tracking);
    globalThis.fetch = mock(async () => {
      order.push('charge');
      return Response.json({ credits_remaining: 7 });
    }) as unknown as typeof fetch;

    await withCreditHoldScope(async () => {
      const a = auth(10);
      await validateCredits(a, 3, 'ip');
      void deductCredits(a, 0.003, { endpoint: '/transcribe' }, 'ip');
    });
    await settle();

    expect(order).toEqual(['charge', 'release(cache=1)']);
    expect(store.outstandingMilli(ACCOUNT)).toBe(0);
  });

  test('a charge above the hold is grown into the free balance and charged in full', async () => {
    await withCreditHoldScope(async () => {
      const a = auth(10);
      await validateCredits(a, 1, 'ip');
      // 2.5 credits.
      expect(await deductCredits(a, 0.0025, { endpoint: '/transcribe' }, 'ip')).toBe(2.5);
    });
    await settle();
    expect(events).toEqual(['charge:2.5']);
    expect(shortfalls).toEqual([]);
  });

  test('the part another request holds is written off and logged as credits_shortfall', async () => {
    await store.reserve({
      account: ACCOUNT, holdId: 'other', minMilli: 8000, maxMilli: 8000,
      balanceMilli: 10_000, nowMs: Date.now(), ttlMs: 60_000,
    });
    await withCreditHoldScope(async () => {
      const a = auth(10);
      await validateCredits(a, 1, 'ip');
      // 3 credits used against 1 held and 1 more free: 2 charged, 1 written off.
      expect(await deductCredits(a, 0.003, { endpoint: '/transcribe' }, 'ip')).toBe(2);
    });
    await settle();

    expect(events).toEqual(['charge:2']);
    expect(shortfalls).toEqual([{
      event: 'credits_shortfall',
      source: 'cloud',
      reason: 'settle_above_hold',
      endpoint: '/transcribe',
      creditsUsed: 3,
      creditsCharged: 2,
      heldCredits: 2,
      shortfallCredits: 1,
    }]);
    expect(JSON.stringify(shortfalls)).not.toContain(ACCOUNT);
    expect([...store.holds(ACCOUNT).keys()]).toEqual(['other']);
  });

  test('a refused licence write still releases the hold', async () => {
    globalThis.fetch = mock(async () => Response.json({ error: 'nope' }, { status: 500 })) as unknown as typeof fetch;
    await withCreditHoldScope(async () => {
      const a = auth(10);
      await validateCredits(a, 1, 'ip');
      void deductCredits(a, 0.001, { endpoint: '/transcribe' }, 'ip');
    });
    await settle();
    expect(store.outstandingMilli(ACCOUNT)).toBe(0);
  });
});

describe('CreditHold ordering', () => {
  test('a release issued while the reservation is in flight still frees it', async () => {
    let finishReserve: (() => void) | undefined;
    const slow: CreditHoldStore = {
      reserve: (request: ReserveHoldRequest) => new Promise<ReserveHoldResult>((resolve) => {
        finishReserve = () => { void store.reserve(request).then(resolve); };
      }),
      release: (account, holdId) => store.release(account, holdId),
    };
    setCreditHoldStoreForTests(slow);

    const hold = new CreditHold(auth(10), 60_000);
    const reserving = hold.reserve(1, 5);
    await Bun.sleep(0);
    // Release before the reservation lands.
    const releasing = hold.release();
    finishReserve!();
    expect((await reserving).kind).toBe('granted');
    await releasing;
    expect(store.outstandingMilli(ACCOUNT)).toBe(0);
    expect(store.calls.map((call) => call.op)).toEqual(['reserve', 'release']);
  });

  test('release is idempotent and only calls the store once', async () => {
    const { hold } = await reserveSessionHold(auth(10), 1, 5);
    await Promise.all([hold.release(), hold.release(), hold.release()]);
    expect(store.calls.filter((call) => call.op === 'release')).toHaveLength(1);
  });

  test('requestHoldFor is undefined outside a scope', () => {
    expect(requestHoldFor(auth(10))).toBeUndefined();
  });
});
