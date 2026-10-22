import { afterEach, beforeEach, describe, expect, mock, spyOn, test } from 'bun:test';
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

type CachedLicense = { isValid: boolean; credits: number; cachedAt: string; issuedAtMs?: number };
const cacheWrites: Array<{ licenseKey: string; license: CachedLicense }> = [];
// What `getCachedLicense` reads back, so a charge's cache write can be followed
// through to the next request's `validateAuth`.
const cachedLicenses = new Map<string, CachedLicense>();
type CacheLicense = (licenseKey: string, license: CachedLicense) => Promise<void>;
const defaultCacheLicense: CacheLicense = async (licenseKey, license) => {
  cacheWrites.push({ licenseKey, license });
  cachedLicenses.set(licenseKey, license);
};
let cacheLicenseImplementation: CacheLicense = defaultCacheLicense;

mock.module('../lib/redis', () => ({
  redis: { get: () => { throw new Error('redis client should not be constructed in this test'); } },
  isIPBlocked: async () => false,
  getCachedLicense: async (licenseKey: string) => cachedLicenses.get(licenseKey) ?? null,
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
const { getCachedLicense } = await import('../lib/redis');
const { validateAuth } = await import('./auth');

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
  cachedLicenses.clear();
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
  test('applies the default-route per-minute rate by default', () => {
    const estimate = estimateCreditsFromSize(BYTES_PER_MINUTE_ESTIMATE);
    // One minute at CREDITS_PER_MINUTE (1.67), rounded up to a tenth like every estimate.
    expect(estimate).toBe(Math.ceil(CREDITS_PER_MINUTE * 10) / 10);
    expect(estimate).toBe(1.7);
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

  test('ignores an empty costEstimators list and falls back to the default-route rate', () => {
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

  test('the cache rewrite after a charge is stamped with the time taken BEFORE the charge went out (#2026)', async () => {
    const pool = 'f'.repeat(64);
    const clock = spyOn(Date, 'now').mockReturnValue(1_000);
    try {
      captureFetch(() => {
        // The reply arrives later: a stamp taken after the fetch reads 9000.
        clock.mockReturnValue(9_000);
        return Response.json({ credits_remaining: 8, credit_pool_id: pool });
      });

      await deductCredits(auth(20), 0.05, { provider: 'test-provider' }, '1.2.3.4');

      expect(cacheWrites).toHaveLength(1);
      expect(cacheWrites[0]?.license).toMatchObject({ credits: 8, creditPoolId: pool, issuedAtMs: 1_000 });
    } finally {
      clock.mockRestore();
    }
    await expectNoPendingDeductions();
  });

  test('the cache rewrite after a charge caches the credit pool id the reply names (#2026)', async () => {
    const pool = 'f'.repeat(64);
    captureFetch(() => Response.json({ credits_remaining: 8, credit_pool_id: pool }));

    // A key whose cache predates the pool: the charge's reply supplies it.
    await deductCredits(auth(20), 0.05, { provider: 'test-provider' }, '1.2.3.4');

    expect(cacheWrites).toHaveLength(1);
    expect(cacheWrites[0]?.license).toMatchObject({ isValid: true, credits: 8, creditPoolId: pool });
    await expectNoPendingDeductions();
  });

  test('the reply\'s pool id replaces a stale one in the auth context (#2026)', async () => {
    const rotated = 'a'.repeat(64);
    captureFetch(() => Response.json({ credits_remaining: 8, credit_pool_id: rotated }));
    const stale: AuthContext = { ...auth(20), creditPoolId: 'f'.repeat(64) };

    await deductCredits(stale, 0.05, { provider: 'test-provider' }, '1.2.3.4');

    expect(cacheWrites[0]?.license).toMatchObject({ creditPoolId: rotated });
    await expectNoPendingDeductions();
  });

  test('a reply with no (or a malformed) pool id caches none, whatever auth carried (#2026)', async () => {
    for (const creditPoolId of [undefined, 'not a pool id!']) {
      cacheWrites.length = 0;
      captureFetch(() => Response.json({ credits_remaining: 8, credit_pool_id: creditPoolId }));
      const stale: AuthContext = { ...auth(20), creditPoolId: 'f'.repeat(64) };

      await deductCredits(stale, 0.05, { provider: 'test-provider' }, '1.2.3.4');

      expect(cacheWrites).toHaveLength(1);
      expect(cacheWrites[0]?.license).toMatchObject({ isValid: true, credits: 8 });
      expect(cacheWrites[0]?.license).not.toHaveProperty('creditPoolId');
    }
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

  const keyRefusals = ['License is revoked', 'License is refunded', 'License key not found'];

  for (const error of keyRefusals) {
    test(`a 400 "${error}" caches the key as invalid, so the next request is refused from the cache (#1899)`, async () => {
      cachedLicenses.set('lic_test', { isValid: true, credits: 20, cachedAt: new Date().toISOString() });
      const requests = captureFetch(() => Response.json({ error }, { status: 400 }));
      captureWarnings();
      const failures: unknown[] = [];

      const creditsUsed = await deductCredits(auth(20), 0.05, { provider: 'p' }, '1.2.3.4', {
        onFailure: (failure) => failures.push(failure),
      });

      expect(creditsUsed).toBe(creditsForCost(0.05));
      expectBillingPost(requests, creditsForCost(0.05), { provider: 'p' });
      // Still reported as a dropped charge (#820).
      expect(failures).toEqual([{ kind: 'http', status: 400 }]);
      expect(await getCachedLicense('lic_test')).toMatchObject({ isValid: false, credits: 0 });

      globalThis.fetch = mock(async () => {
        throw new Error('validateAuth must answer from the cache');
      }) as unknown as typeof fetch;
      const next = await validateAuth({ licenseKey: 'lic_test' });
      expect(next.ok).toBe(false);
      expect(next.diagnostics.outcome).toBe('cached_invalid');
      await expectNoPendingDeductions();
    });
  }

  // The per-key entry keeps whichever site call was ASKED last (#1899): the
  // ordering itself is `cacheLicense`'s (`WRITE_LICENSE_SCRIPT`, run on a real
  // redis-server in `lib/redis-core.test.ts`). What a charge owes it is the
  // stamp: the time the charge was SENT, not when its reply came back.
  for (const { label, respond } of [
    { label: 'a refusal', respond: () => Response.json({ error: 'License is revoked' }, { status: 400 }) },
    { label: 'a success', respond: () => Response.json({ credits_remaining: 12 }) },
  ]) {
    test(`${label} is cached stamped with when the charge was sent, not when its reply came back (#1899)`, async () => {
      let sentAtMs = 0;
      let repliedAtMs = 0;
      captureFetch(async () => {
        sentAtMs = Date.now();
        await Bun.sleep(25);
        repliedAtMs = Date.now();
        return respond();
      });
      captureWarnings();

      await deductCredits(auth(20), 0.05, { provider: 'p' }, '1.2.3.4');

      expect(cacheWrites).toHaveLength(1);
      const issuedAtMs = cacheWrites[0]?.license.issuedAtMs;
      expect(typeof issuedAtMs).toBe('number');
      expect(issuedAtMs!).toBeLessThanOrEqual(sentAtMs);
      expect(issuedAtMs!).toBeLessThan(repliedAtMs);
      await expectNoPendingDeductions();
    });
  }

  test('a success reply to a charge asked after an older refusal re-validates the key (#1899)', async () => {
    cachedLicenses.set('lic_test', { isValid: false, credits: 0, cachedAt: new Date().toISOString(), issuedAtMs: Date.now() - 60_000 });
    captureFetch(() => Response.json({ credits_remaining: 12 }));

    await deductCredits(auth(20), 0.05, { provider: 'p' }, '1.2.3.4');

    expect(await getCachedLicense('lic_test')).toMatchObject({ isValid: true, credits: 12 });
    await expectNoPendingDeductions();
  });

  const nonKeyFailures: Array<{ label: string; respond: () => Response }> = [
    { label: 'a 503', respond: () => Response.json({ error: 'License is revoked' }, { status: 503 }) },
    { label: 'a 429', respond: () => Response.json({ error: 'Too many requests' }, { status: 429 }) },
    { label: 'a 409 retry', respond: () => Response.json({ error: 'Failed to deduct credits. Please retry.' }, { status: 409 }) },
    { label: 'a 400 about the amount', respond: () => Response.json({ error: 'amount must be a finite positive number' }, { status: 400 }) },
    { label: 'a 400 with no error text', respond: () => new Response('nope', { status: 400 }) },
  ];

  for (const { label, respond } of nonKeyFailures) {
    test(`${label} leaves a valid cached entry unchanged (#1899)`, async () => {
      const cached = { isValid: true, credits: 20, cachedAt: new Date().toISOString() };
      cachedLicenses.set('lic_test', cached);
      captureFetch(respond);
      captureWarnings();

      await deductCredits(auth(20), 0.05, { provider: 'p' }, '1.2.3.4');

      expect(cacheWrites).toHaveLength(0);
      expect(await getCachedLicense('lic_test')).toEqual(cached);
      const next = await validateAuth({ licenseKey: 'lic_test' });
      expect(next.ok).toBe(true);
      expect(next.diagnostics.outcome).toBe('accepted');
      await expectNoPendingDeductions();
    });
  }

  const onFailureCases: Array<{ label: string; respond: () => Response; expected: unknown }> = [
    {
      label: 'an HTTP 503',
      respond: () => Response.json({ error: 'synthetic outage' }, { status: 503 }),
      expected: [[{ kind: 'http', status: 503 }, creditsForCost(0.05)]],
    },
    {
      label: 'a network rejection',
      respond: () => { throw new Error('synthetic connection reset'); },
      expected: [[{ kind: 'network', error: 'synthetic connection reset' }, creditsForCost(0.05)]],
    },
    { label: 'an accepted write', respond: () => Response.json({ credits_remaining: 3 }), expected: [] },
    {
      // The license API accepted the charge; only the balance read failed.
      label: 'an accepted write with a malformed body',
      respond: () => new Response('{malformed-json', { status: 200 }),
      expected: [],
    },
  ];

  for (const { label, respond, expected } of onFailureCases) {
    test(`deductCredits onFailure reports ${label} without changing the resolved credits`, async () => {
      const requests = captureFetch(respond);
      captureWarnings();
      const failures: unknown[] = [];

      const creditsUsed = await deductCredits(auth(20), 0.05, { provider: 'p' }, '1.2.3.4', {
        // The second argument is the charge that was not recorded (#820).
        onFailure: (failure, creditsNotRecorded) => failures.push([failure, creditsNotRecorded]),
      });

      expect(creditsUsed).toBe(creditsForCost(0.05));
      expectBillingPost(requests, creditsForCost(0.05), { provider: 'p' });
      expect(failures).toEqual(expected as unknown[]);
      await expectNoPendingDeductions();
    });
  }

  test('deductCredits ignores an onFailure callback that throws', async () => {
    captureFetch(() => Response.json({ error: 'synthetic outage' }, { status: 503 }));
    captureWarnings();

    const creditsUsed = await deductCredits(auth(20), 0.05, {}, '1.2.3.4', {
      onFailure: () => { throw new Error('reporter broke'); },
    });

    expect(creditsUsed).toBe(creditsForCost(0.05));
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
