import { afterEach, describe, expect, mock, test } from 'bun:test';
import { BYTES_PER_MINUTE_ESTIMATE, CREDITS_PER_MINUTE } from '../lib/constants';
import { creditsForCost } from '../lib/cost-calculator';
import type { AuthContext } from './auth';

const originalFetch = globalThis.fetch;

type CachedLicense = { isValid: boolean; credits: number; cachedAt: string };
const cacheWrites: Array<{ licenseKey: string; license: CachedLicense }> = [];

mock.module('../lib/redis', () => ({
  redis: { get: () => { throw new Error('redis client should not be constructed in this test'); } },
  isIPBlocked: async () => false,
  getCachedLicense: async () => null,
  cacheLicense: async (licenseKey: string, license: CachedLicense) => {
    cacheWrites.push({ licenseKey, license });
  },
}));

const {
  estimateAudioSecondsFromSize,
  estimateCreditsFromSize,
  validateCredits,
  deductCredits,
  drainPendingDeductions,
} = await import('./credits');

function auth(credits: number): AuthContext {
  return { identifier: 'lic_test', credits, licenseKey: 'lic_test' };
}

async function expectNoPendingDeductions(): Promise<void> {
  // The tracking cleanup runs from a separate finally chain after the returned deduction settles.
  await Promise.resolve();
  expect(await drainPendingDeductions(1000)).toBe(0);
}

afterEach(() => {
  cacheWrites.length = 0;
  globalThis.fetch = originalFetch;
});

describe('estimateAudioSecondsFromSize', () => {
  test('converts bytes to seconds using the encoded-bitrate estimate', () => {
    expect(estimateAudioSecondsFromSize(BYTES_PER_MINUTE_ESTIMATE)).toBe(60);
    expect(estimateAudioSecondsFromSize(BYTES_PER_MINUTE_ESTIMATE / 2)).toBe(30);
  });

  test('floors at the 10-second minimum for tiny or zero uploads', () => {
    expect(estimateAudioSecondsFromSize(0)).toBe(10);
    expect(estimateAudioSecondsFromSize(1_000)).toBe(10);
  });
});

describe('estimateCreditsFromSize', () => {
  test('applies the blended per-minute rate by default', () => {
    const estimate = estimateCreditsFromSize(BYTES_PER_MINUTE_ESTIMATE);
    expect(estimate).toBeCloseTo(CREDITS_PER_MINUTE, 5);
  });

  test('never returns below the 0.1 credit floor for a tiny upload', () => {
    expect(estimateCreditsFromSize(0)).toBeGreaterThanOrEqual(0.1);
  });

  test('when cost estimators are provided, bills off the most expensive fallback provider', () => {
    // Sized so estimateAudioSecondsFromSize resolves to exactly 100s (well above the 10s floor).
    const sizeBytes = (100 / 60) * BYTES_PER_MINUTE_ESTIMATE;
    const cheapEstimator = (durationSeconds: number) => 0.001 * durationSeconds;
    const expensiveEstimator = (durationSeconds: number) => 0.01 * durationSeconds;

    const estimate = estimateCreditsFromSize(sizeBytes, {
      costEstimators: [cheapEstimator, expensiveEstimator],
    });

    expect(estimate).toBe(creditsForCost(expensiveEstimator(100)));
  });

  test('ignores an empty costEstimators list and falls back to the blended rate', () => {
    const withEmptyList = estimateCreditsFromSize(BYTES_PER_MINUTE_ESTIMATE, { costEstimators: [] });
    const withNoOptions = estimateCreditsFromSize(BYTES_PER_MINUTE_ESTIMATE);
    expect(withEmptyList).toBe(withNoOptions);
  });
});

describe('validateCredits', () => {
  test('allows a request when the balance covers the estimate exactly', async () => {
    const result = await validateCredits(auth(5), 5, '1.2.3.4');
    expect(result.ok).toBe(true);
  });

  test('rejects a request when the balance is short, with a 402 and the shortfall in the body', async () => {
    const result = await validateCredits(auth(4.9), 5, '1.2.3.4');
    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.response.status).toBe(402);
      const body = await result.response.json() as { credits_remaining: number };
      expect(body.credits_remaining).toBe(4.9);
    }
  });

  test('rounds the balance to the nearest tenth before comparing, so 5.04 cannot cover a 5.05 estimate', async () => {
    const result = await validateCredits(auth(5.04), 5.05, '1.2.3.4');
    expect(result.ok).toBe(false);
  });
});

describe('deductCredits / drainPendingDeductions', () => {
  test('drainPendingDeductions returns 0 immediately when nothing is in flight', async () => {
    const drained = await drainPendingDeductions(1000);
    expect(drained).toBe(0);
  });

  test('deductCredits records usage against the license API and resolves the charged credits', async () => {
    let recordedBody: Record<string, unknown> | null = null;
    globalThis.fetch = mock(async (_input: RequestInfo | URL, init?: RequestInit) => {
      recordedBody = JSON.parse(String(init?.body));
      return Response.json({ credits_remaining: 12.3, credits_deducted: 1.2 });
    }) as unknown as typeof fetch;

    const costUsd = 0.05;
    const creditsUsed = await deductCredits(auth(20), costUsd, { provider: 'test-provider' }, '1.2.3.4');

    expect(creditsUsed).toBe(creditsForCost(costUsd));
    expect(recordedBody).toMatchObject({
      license_key: 'lic_test',
      amount: creditsForCost(costUsd),
      metadata: { provider: 'test-provider' },
    });
    expect(cacheWrites).toHaveLength(1);
    expect(cacheWrites[0]?.license).toMatchObject({ isValid: true, credits: 12.3 });
  });

  for (const status of [429, 500]) {
    test(`deductCredits contains an HTTP ${status} failure and removes the settled deduction from in-flight tracking`, async () => {
      globalThis.fetch = mock(async () => Response.json(
        { error: 'synthetic upstream failure' },
        { status },
      )) as unknown as typeof fetch;

      const costUsd = 0.05;
      const creditsUsed = await deductCredits(auth(20), costUsd, {}, '1.2.3.4');

      expect(creditsUsed).toBe(creditsForCost(costUsd));
      expect(cacheWrites).toHaveLength(0);
      await expectNoPendingDeductions();
    });
  }

  test('deductCredits contains a network rejection and removes the settled deduction from in-flight tracking', async () => {
    globalThis.fetch = mock(async () => {
      throw new Error('synthetic connection reset');
    }) as unknown as typeof fetch;

    const costUsd = 0.05;
    const creditsUsed = await deductCredits(auth(20), costUsd, {}, '1.2.3.4');

    expect(creditsUsed).toBe(creditsForCost(costUsd));
    expect(cacheWrites).toHaveLength(0);
    await expectNoPendingDeductions();
  });

  const unusableBalances: Array<{ label: string; body: Record<string, unknown> }> = [
    { label: 'missing', body: {} },
    { label: 'non-numeric', body: { credits_remaining: '12.3' } },
  ];

  for (const { label, body } of unusableBalances) {
    test(`deductCredits records usage but does not replace the cached balance when credits_remaining is ${label}`, async () => {
      globalThis.fetch = mock(async () => Response.json(body)) as unknown as typeof fetch;

      const costUsd = 0.05;
      const creditsUsed = await deductCredits(auth(20), costUsd, {}, '1.2.3.4');

      expect(creditsUsed).toBe(creditsForCost(costUsd));
      expect(cacheWrites).toHaveLength(0);
      await expectNoPendingDeductions();
    });
  }

  test('drainPendingDeductions waits for an in-flight deduction to finish before returning', async () => {
    globalThis.fetch = mock(async () => Response.json({ credits_remaining: 8 })) as unknown as typeof fetch;

    // Fire-and-forget, like real call sites do (they don't await deductCredits on the response path).
    void deductCredits(auth(20), 0.05, {}, '1.2.3.4');

    const drained = await drainPendingDeductions(2000);

    expect(drained).toBe(1);
    // If drain had returned without awaiting the deduction, this write wouldn't exist yet.
    expect(cacheWrites).toHaveLength(1);
  });

  test('drainPendingDeductions times out on a pending request and drops it after settlement', async () => {
    let resolveFetch: (response: Response) => void = () => {};
    const pendingResponse = new Promise<Response>((resolve) => {
      resolveFetch = resolve;
    });
    globalThis.fetch = mock(async () => pendingResponse) as unknown as typeof fetch;

    const costUsd = 0.05;
    const deduction = deductCredits(auth(20), costUsd, {}, '1.2.3.4');

    expect(await drainPendingDeductions(20)).toBe(1);
    expect(cacheWrites).toHaveLength(0);

    resolveFetch(Response.json({ credits_remaining: 7.5 }));
    expect(await deduction).toBe(creditsForCost(costUsd));
    expect(cacheWrites).toHaveLength(1);
    expect(cacheWrites[0]?.license.credits).toBe(7.5);
    await expectNoPendingDeductions();
  });
});
