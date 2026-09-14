import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import {
  BYTES_PER_MINUTE_ESTIMATE,
  CREDITS_PER_MINUTE,
  DEFAULT_API_BASE_URL,
} from '../lib/constants';
import { creditsForCost } from '../lib/cost-calculator';
import type { AuthContext } from './auth';

const originalFetch = globalThis.fetch;
const originalConsoleWarn = console.warn;
const originalLicenseApiUrl = process.env.NEXTJS_LICENSE_API_URL;

type CachedLicense = { isValid: boolean; credits: number; cachedAt: string };
const cacheWrites: Array<{ licenseKey: string; license: CachedLicense }> = [];
type CacheLicense = (licenseKey: string, license: CachedLicense) => Promise<void>;
const defaultCacheLicense: CacheLicense = async (licenseKey, license) => {
  cacheWrites.push({ licenseKey, license });
};
let cacheLicenseImplementation: CacheLicense = defaultCacheLicense;

mock.module('../lib/redis', () => ({
  redis: { get: () => { throw new Error('redis client should not be constructed in this test'); } },
  isIPBlocked: async () => false,
  getCachedLicense: async () => null,
  cacheLicense: (licenseKey: string, license: CachedLicense) => (
    cacheLicenseImplementation(licenseKey, license)
  ),
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

interface BillingRequest {
  url: string;
  init?: RequestInit;
}

function captureFetch(response: () => Response | Promise<Response>): BillingRequest[] {
  const requests: BillingRequest[] = [];
  globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), init });
    return response();
  }) as unknown as typeof fetch;
  return requests;
}

function captureWarnings(): unknown[][] {
  const warnings: unknown[][] = [];
  console.warn = mock((...args: unknown[]) => {
    warnings.push(args);
  }) as typeof console.warn;
  return warnings;
}

function expectBillingPost(
  requests: BillingRequest[],
  expectedAmount: number,
  expectedMetadata: Record<string, unknown>,
  expectedUrl = `${DEFAULT_API_BASE_URL}/api/license/credits`,
): void {
  expect(requests).toHaveLength(1);
  const request = requests[0];
  expect(request).toBeDefined();
  expect(request?.url).toBe(expectedUrl);
  expect(request?.init?.method).toBe('POST');
  expect(request?.init?.headers).toEqual({ 'Content-Type': 'application/json' });
  expect(request?.init?.signal).toBeInstanceOf(AbortSignal);
  expect(JSON.parse(String(request?.init?.body))).toEqual({
    license_key: 'lic_test',
    amount: expectedAmount,
    metadata: expectedMetadata,
  });
}

async function waitFor(condition: () => boolean | Promise<boolean>, timeoutMs = 1000): Promise<void> {
  const deadline = performance.now() + timeoutMs;
  while (!(await condition())) {
    if (performance.now() >= deadline) {
      throw new Error(`Condition was not met within ${timeoutMs}ms`);
    }
    await new Promise<void>((resolve) => setTimeout(resolve, 1));
  }
}

async function expectNoPendingDeductions(): Promise<void> {
  let pendingCount = -1;
  await waitFor(async () => {
    pendingCount = await drainPendingDeductions(0);
    return pendingCount === 0;
  });
  expect(pendingCount).toBe(0);
}

beforeEach(() => {
  delete process.env.NEXTJS_LICENSE_API_URL;
});

afterEach(async () => {
  await drainPendingDeductions(2000);
  cacheWrites.length = 0;
  cacheLicenseImplementation = defaultCacheLicense;
  globalThis.fetch = originalFetch;
  console.warn = originalConsoleWarn;
  if (originalLicenseApiUrl === undefined) {
    delete process.env.NEXTJS_LICENSE_API_URL;
  } else {
    process.env.NEXTJS_LICENSE_API_URL = originalLicenseApiUrl;
  }
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
    process.env.NEXTJS_LICENSE_API_URL = 'https://licenses.example.test///';
    const requests = captureFetch(() => (
      Response.json({ credits_remaining: 12.3, credits_deducted: 1.2 })
    ));

    const costUsd = 0.05;
    const metadata = { provider: 'test-provider' };
    const creditsUsed = await deductCredits(auth(20), costUsd, metadata, '1.2.3.4');

    expect(creditsUsed).toBe(creditsForCost(costUsd));
    expectBillingPost(
      requests,
      creditsForCost(costUsd),
      { provider: 'test-provider' },
      'https://licenses.example.test/api/license/credits',
    );
    expect(cacheWrites).toHaveLength(1);
    expect(cacheWrites[0]?.license).toMatchObject({ isValid: true, credits: 12.3 });
    await expectNoPendingDeductions();
  });

  test('deductCredits bills the current 0.1 credit minimum when provider cost is zero', async () => {
    const requests = captureFetch(() => Response.json({ credits_remaining: 19.9 }));
    const metadata = { provider: 'zero-cost-provider' };

    expect(creditsForCost(0)).toBe(0.1);

    const creditsUsed = await deductCredits(auth(20), 0, metadata, '1.2.3.4');

    expect(creditsUsed).toBe(0.1);
    expectBillingPost(requests, 0.1, { provider: 'zero-cost-provider' });
    expect(cacheWrites[0]?.license.credits).toBe(19.9);
    await expectNoPendingDeductions();
  });

  const responseErrorCases = [
    {
      label: 'a successful response has malformed JSON',
      response: () => (
        new Response('{malformed-json', {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      ),
      provider: 'malformed-success-provider',
      warning: 'POST /api/license/credits network error',
      warningDetails: { error: expect.stringMatching(/json|unexpected|expected.*(?:property|identifier)/i) },
    },
    {
      label: 'a 503 response has a non-JSON body',
      response: () => (
        new Response('synthetic service failure', {
          status: 503,
          headers: { 'Content-Type': 'text/plain' },
        })
      ),
      provider: 'non-json-failure-provider',
      warning: 'POST /api/license/credits failed',
      warningDetails: { status: 503, error: 'Unknown error', creditsUsed: creditsForCost(0.05) },
    },
  ];

  for (const { label, response, provider, warning, warningDetails } of responseErrorCases) {
    test(`deductCredits resolves without a cache write when ${label}`, async () => {
      const requests = captureFetch(response);
      const warnings = captureWarnings();
      const costUsd = 0.05;
      const metadata = { provider };

      const creditsUsed = await deductCredits(auth(20), costUsd, metadata, '1.2.3.4');

      expect(creditsUsed).toBe(creditsForCost(costUsd));
      expectBillingPost(requests, creditsForCost(costUsd), { provider });
      expect(cacheWrites).toHaveLength(0);
      expect(warnings).toHaveLength(1);
      expect(warnings[0]?.[0]).toBe(warning);
      expect(warnings[0]?.[1]).toEqual(warningDetails);
      await expectNoPendingDeductions();
    });
  }

  for (const status of [429, 500]) {
    test(`deductCredits sends the billing POST and removes it from in-flight tracking after an HTTP ${status} failure`, async () => {
      const requests = captureFetch(() => (
        Response.json({ error: 'synthetic upstream failure' }, { status })
      ));
      const warnings = captureWarnings();

      const costUsd = 0.05;
      const metadata = { provider: 'http-failure-provider' };
      await deductCredits(auth(20), costUsd, metadata, '1.2.3.4');

      expectBillingPost(requests, creditsForCost(costUsd), { provider: 'http-failure-provider' });
      expect(cacheWrites).toHaveLength(0);
      expect(warnings).toEqual([[
        'POST /api/license/credits failed',
        {
          status,
          error: 'synthetic upstream failure',
          creditsUsed: creditsForCost(costUsd),
        },
      ]]);
      await expectNoPendingDeductions();
    });
  }

  test('deductCredits sends the billing POST and removes it from in-flight tracking after a network rejection', async () => {
    const requests = captureFetch(() => {
      throw new Error('synthetic connection reset');
    });
    const warnings = captureWarnings();

    const costUsd = 0.05;
    const metadata = { provider: 'network-failure-provider' };
    await deductCredits(auth(20), costUsd, metadata, '1.2.3.4');

    expectBillingPost(requests, creditsForCost(costUsd), { provider: 'network-failure-provider' });
    expect(cacheWrites).toHaveLength(0);
    expect(warnings).toEqual([[
      'POST /api/license/credits network error',
      { error: 'synthetic connection reset' },
    ]]);
    await expectNoPendingDeductions();
  });

  const unusableBalances: Array<{ label: string; body: Record<string, unknown> }> = [
    { label: 'missing', body: {} },
    { label: 'non-numeric', body: { credits_remaining: '12.3' } },
  ];

  for (const { label, body } of unusableBalances) {
    test(`deductCredits records usage but does not replace the cached balance when credits_remaining is ${label}`, async () => {
      const requests = captureFetch(() => Response.json(body));

      const costUsd = 0.05;
      const metadata = { provider: `${label}-balance-provider` };
      const creditsUsed = await deductCredits(auth(20), costUsd, metadata, '1.2.3.4');

      expect(creditsUsed).toBe(creditsForCost(costUsd));
      expectBillingPost(requests, creditsForCost(costUsd), { provider: `${label}-balance-provider` });
      expect(cacheWrites).toHaveLength(0);
      await expectNoPendingDeductions();
    });
  }

  test('drainPendingDeductions waits for a pending Redis cache write before returning', async () => {
    captureFetch(() => Response.json({ credits_remaining: 8 }));
    let resolveCacheWrite!: () => void;
    const cacheWritePending = new Promise<void>((resolve) => {
      resolveCacheWrite = resolve;
    });
    let cacheWriteStarted = false;
    cacheLicenseImplementation = async (licenseKey, license) => {
      cacheWriteStarted = true;
      await cacheWritePending;
      cacheWrites.push({ licenseKey, license });
    };

    // Fire-and-forget, like real call sites do (they don't await deductCredits on the response path).
    const deduction = deductCredits(auth(20), 0.05, {}, '1.2.3.4');

    try {
      await waitFor(() => cacheWriteStarted);
      let drainSettled = false;
      const drain = drainPendingDeductions(2000).then((pendingCount) => {
        drainSettled = true;
        return pendingCount;
      });
      await new Promise<void>((resolve) => setTimeout(resolve, 0));

      expect(drainSettled).toBe(false);
      resolveCacheWrite();
      expect(await drain).toBe(1);
      expect(cacheWrites).toHaveLength(1);
    } finally {
      resolveCacheWrite();
      await Promise.allSettled([deduction]);
    }
  });

  test('drainPendingDeductions times out on a pending request and drops it after settlement', async () => {
    let resolveFetch!: (response: Response) => void;
    const pendingResponse = new Promise<Response>((resolve) => {
      resolveFetch = resolve;
    });
    const requests = captureFetch(() => pendingResponse);

    const costUsd = 0.05;
    const metadata = { provider: 'pending-provider' };
    const deduction = deductCredits(auth(20), costUsd, metadata, '1.2.3.4');

    try {
      await waitFor(() => requests.length === 1);
      const timeoutMs = 25;
      const startedAt = performance.now();
      const pendingCount = await drainPendingDeductions(timeoutMs);
      const elapsedMs = performance.now() - startedAt;

      expect(pendingCount).toBe(1);
      expect(elapsedMs).toBeGreaterThanOrEqual(timeoutMs - 1);
      expectBillingPost(requests, creditsForCost(costUsd), { provider: 'pending-provider' });
      expect(cacheWrites).toHaveLength(0);
    } finally {
      resolveFetch(Response.json({ credits_remaining: 7.5 }));
      await Promise.allSettled([deduction]);
    }

    expect(cacheWrites).toHaveLength(1);
    expect(cacheWrites[0]?.license.credits).toBe(7.5);
  });
});
