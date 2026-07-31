import { afterEach, describe, expect, mock, test } from 'bun:test';

const originalFetch = globalThis.fetch;

type CachedLicense = { isValid: boolean; credits: number; cachedAt: string };

const cacheWrites: Array<{ licenseKey: string; license: CachedLicense }> = [];
let cachedLicenseValue: CachedLicense | null = null;

mock.module('../lib/redis', () => ({
  redis: { get: () => { throw new Error('redis client should not be constructed in this test'); } },
  isIPBlocked: async () => false,
  getCachedLicense: async (_licenseKey: string) => cachedLicenseValue,
  cacheLicense: async (licenseKey: string, license: CachedLicense) => {
    cacheWrites.push({ licenseKey, license });
  },
}));

const { validateAuth } = await import('./auth');

function neverFetch() {
  return mock(async () => {
    throw new Error('validateAuth should not have called fetch here');
  }) as unknown as typeof fetch;
}

afterEach(() => {
  cacheWrites.length = 0;
  cachedLicenseValue = null;
  globalThis.fetch = originalFetch;
});

describe('validateAuth', () => {
  test('rejects a request with no license key, without calling the API', async () => {
    globalThis.fetch = neverFetch();

    const result = await validateAuth({});

    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.response.status).toBe(401);
  });

  test('serves a valid cached license without calling the API', async () => {
    cachedLicenseValue = { isValid: true, credits: 25, cachedAt: 'x' };
    globalThis.fetch = neverFetch();

    const result = await validateAuth({ licenseKey: 'abc123' });

    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.value.credits).toBe(25);
      expect(result.value.licenseKey).toBe('abc123');
      expect(result.value.identifier).toBe('abc123');
    }
  });

  test('rejects a request with a cached invalid license, without calling the API', async () => {
    cachedLicenseValue = { isValid: false, credits: 0, cachedAt: 'x' };
    globalThis.fetch = neverFetch();

    const result = await validateAuth({ licenseKey: 'bad-key' });

    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.response.status).toBe(401);
  });

  test('forceRefresh bypasses a valid cache entry and re-validates via the API', async () => {
    cachedLicenseValue = { isValid: true, credits: 25, cachedAt: 'x' };
    globalThis.fetch = mock(async () => Response.json({ valid: true, credits: 99 })) as unknown as typeof fetch;

    const result = await validateAuth({ licenseKey: 'abc123' }, true);

    expect(result.ok).toBe(true);
    if (result.ok) expect(result.value.credits).toBe(99);
    expect(cacheWrites).toHaveLength(1);
    expect(cacheWrites[0]?.license).toMatchObject({ isValid: true, credits: 99 });
  });

  test('validates via the API on a cache miss and caches a valid verdict', async () => {
    globalThis.fetch = mock(async () => Response.json({ valid: true, credits: 7 })) as unknown as typeof fetch;

    const result = await validateAuth({ licenseKey: 'fresh-key' });

    expect(result.ok).toBe(true);
    if (result.ok) expect(result.value.credits).toBe(7);
    expect(cacheWrites).toHaveLength(1);
    expect(cacheWrites[0]?.license).toMatchObject({ isValid: true, credits: 7 });
  });

  test('caches a definitive 4xx invalid verdict from the API', async () => {
    globalThis.fetch = mock(async () =>
      Response.json({ valid: false, error: 'revoked' }, { status: 404 })
    ) as unknown as typeof fetch;

    const result = await validateAuth({ licenseKey: 'revoked-key' });

    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.response.status).toBe(401);
    expect(cacheWrites).toHaveLength(1);
    expect(cacheWrites[0]?.license.isValid).toBe(false);
  });

  test('fails closed on a transient 5xx without caching, so a retry hits the API again', async () => {
    globalThis.fetch = mock(async () => new Response('upstream down', { status: 503 })) as unknown as typeof fetch;

    const result = await validateAuth({ licenseKey: 'transient-key' });

    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.response.status).toBe(401);
    expect(cacheWrites).toHaveLength(0);
  });

  test('fails closed on a network error without caching the result', async () => {
    globalThis.fetch = mock(async () => {
      throw new Error('ECONNREFUSED');
    }) as unknown as typeof fetch;

    const result = await validateAuth({ licenseKey: 'unreachable-key' });

    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.response.status).toBe(401);
    expect(cacheWrites).toHaveLength(0);
  });

  test('treats a timeout the same as any other network failure: fails closed, does not cache', async () => {
    globalThis.fetch = mock(async () => {
      const error = new DOMException('The operation timed out.', 'TimeoutError');
      throw error;
    }) as unknown as typeof fetch;

    const result = await validateAuth({ licenseKey: 'slow-key' });

    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.response.status).toBe(401);
    expect(cacheWrites).toHaveLength(0);
  });

  test('treats malformed JSON from a 2xx response as an invalid license, and caches it', async () => {
    globalThis.fetch = mock(async () => new Response('not json', { status: 200 })) as unknown as typeof fetch;

    const result = await validateAuth({ licenseKey: 'malformed-key' });

    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.response.status).toBe(401);
    expect(cacheWrites).toHaveLength(1);
    expect(cacheWrites[0]?.license.isValid).toBe(false);
  });
});
