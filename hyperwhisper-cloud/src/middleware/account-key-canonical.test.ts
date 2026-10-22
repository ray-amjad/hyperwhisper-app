// #2461: the credit hold (#2008) and the licence cache were keyed on the RAW
// account key, while the site trims the key before its lookup. With no
// credit_pool_id (CREDIT_POOL_ID_SECRET unset, which the site allows), `KEY`
// and ` KEY` validated as the same account with the same balance, but each got
// its own hold account, so two concurrent requests each held the WHOLE balance.

import { afterAll, beforeEach, describe, expect, mock, test } from 'bun:test';

// Real-shaped licence cache, keyed exactly as redis-core does: `license:<key>`.
const cache = new Map<string, unknown>();
mock.module('../lib/redis', () => ({
  redis: {},
  isIPBlocked: async () => false,
  getCachedLicense: async (key: string) => (cache.get(`license:${key}`) as never) ?? null,
  cacheLicense: async (key: string, license: unknown) => { cache.set(`license:${key}`, license); },
}));

const { canonicalAccountKey, validateAuth } = await import('./auth');
const { validateCredits } = await import('./credits');
const { setCreditHoldStoreForTests, withCreditHoldScope, holdAccountFor } = await import('./credit-holds');
const { createMemoryCreditHoldStore } = await import('../lib/credit-hold-store-memory');

const REAL_KEY = 'HW-REPRO-KEY-0001';
const BALANCE = 10;
const originalFetch = globalThis.fetch;
let siteCalls: string[] = [];

beforeEach(() => {
  cache.clear();
  siteCalls = [];
  setCreditHoldStoreForTests(createMemoryCreditHoldStore());
  // The site: trims the key (nextjs/app/api/license/validate/route.ts), one account, 10 credits.
  globalThis.fetch = (async (_url: RequestInfo | URL, init?: RequestInit) => {
    const body = JSON.parse(String(init?.body)) as { license_key: string };
    siteCalls.push(body.license_key);
    if (body.license_key.trim() === REAL_KEY) return Response.json({ valid: true, credits: BALANCE });
    return Response.json({ valid: false }, { status: 400 });
  }) as typeof fetch;
});

afterAll(() => {
  globalThis.fetch = originalFetch;
  setCreditHoldStoreForTests(null);
});

/** Request A holds the full balance and stays in flight while request B asks for it too. */
async function concurrentFullBalance(keyA: string, keyB: string): Promise<{ a: boolean; b: boolean }> {
  const authA = await validateAuth({ licenseKey: keyA });
  const authB = await validateAuth({ licenseKey: keyB });
  if (!authA.ok || !authB.ok) throw new Error('auth failed');
  let release!: () => void;
  const gate = new Promise<void>((r) => { release = r; });
  let resultA: { ok: boolean } | undefined;
  const reqA = withCreditHoldScope(async () => {
    resultA = await validateCredits(authA.value, BALANCE, '1.2.3.4');
    await gate; // still in flight
  });
  await Bun.sleep(5);
  const resultB = await withCreditHoldScope(() => validateCredits(authB.value, BALANCE, '1.2.3.4'));
  release();
  await reqA;
  return { a: resultA?.ok ?? false, b: resultB.ok };
}

describe('a padded account key is the same account (#2461)', () => {
  test('the key sent with and without a leading space shares one hold budget', async () => {
    const result = await concurrentFullBalance(REAL_KEY, ` ${REAL_KEY}`);
    expect(result.a).toBe(true);
    expect(result.b).toBe(false); // 402: the 10 credits are already held by A
  });

  test('tab, newline and trailing padding share it too', async () => {
    for (const variant of [`\t${REAL_KEY}`, `${REAL_KEY}\n`, `  ${REAL_KEY}  `]) {
      setCreditHoldStoreForTests(createMemoryCreditHoldStore());
      expect(await concurrentFullBalance(REAL_KEY, variant)).toEqual({ a: true, b: false });
    }
  });

  test('control: the same key string twice is refused', async () => {
    expect(await concurrentFullBalance(REAL_KEY, REAL_KEY)).toEqual({ a: true, b: false });
  });

  test('auth carries the trimmed key, so the hold account and the licence cache use one spelling', async () => {
    const padded = await validateAuth({ licenseKey: ` ${REAL_KEY}\t` });
    if (!padded.ok) throw new Error('auth failed');
    expect(padded.value.identifier).toBe(REAL_KEY);
    expect(padded.value.licenseKey).toBe(REAL_KEY);
    expect(holdAccountFor(padded.value)).toBe(REAL_KEY);
    expect(siteCalls).toEqual([REAL_KEY]);
    expect([...cache.keys()]).toEqual([`license:${REAL_KEY}`]);

    // A second variant is a cache hit on the same entry, not a second site call.
    const other = await validateAuth({ licenseKey: `\n${REAL_KEY}` });
    expect(other.ok).toBe(true);
    expect(other.diagnostics.cacheHit).toBe(true);
    expect(siteCalls).toEqual([REAL_KEY]);
  });

  test('a refusal cached for one spelling is seen by every padded spelling', async () => {
    cache.set(`license:${REAL_KEY}`, { isValid: false, credits: 0, cachedAt: new Date().toISOString() });
    const padded = await validateAuth({ licenseKey: ` ${REAL_KEY}` });
    expect(padded.ok).toBe(false);
    expect(padded.diagnostics.outcome).toBe('cached_invalid');
    expect(siteCalls).toEqual([]);
  });

  test('a whitespace-only key is a missing key, and case is kept', async () => {
    const blank = await validateAuth({ licenseKey: ' \t ' });
    expect(blank.ok).toBe(false);
    expect(blank.diagnostics.outcome).toBe('missing_key');
    expect(siteCalls).toEqual([]);
    expect(canonicalAccountKey(' hw-Key ')).toBe('hw-Key');
    expect(canonicalAccountKey(null)).toBeUndefined();
  });

  test('a key that is not a string (an untyped JSON body) is a missing key, not a throw', async () => {
    for (const value of [12345, { key: REAL_KEY }, [REAL_KEY], true]) {
      const result = await validateAuth({ licenseKey: value as never });
      expect(result.ok).toBe(false);
      expect(result.diagnostics.outcome).toBe('missing_key');
    }
    expect(siteCalls).toEqual([]);
  });
});
