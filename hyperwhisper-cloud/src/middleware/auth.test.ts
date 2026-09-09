import { afterEach, describe, expect, mock, test } from 'bun:test';

const originalFetch = globalThis.fetch;
const originalLicenseApiUrl = process.env.NEXTJS_LICENSE_API_URL;

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

const { authDiagnosticsForLog, validateAuth } = await import('./auth');

function neverFetch() {
  return mock(async () => {
    throw new Error('validateAuth should not have called fetch here');
  }) as unknown as typeof fetch;
}

afterEach(() => {
  cacheWrites.length = 0;
  cachedLicenseValue = null;
  globalThis.fetch = originalFetch;
  if (originalLicenseApiUrl === undefined) {
    delete process.env.NEXTJS_LICENSE_API_URL;
  } else {
    process.env.NEXTJS_LICENSE_API_URL = originalLicenseApiUrl;
  }
});

describe('validateAuth', () => {
  test('rejects a request with no license key, without calling the API', async () => {
    globalThis.fetch = neverFetch();

    const result = await validateAuth({});

    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.response.status).toBe(401);
      expect(result.diagnostics).toMatchObject({
        source: 'missing',
        outcome: 'missing_key',
        cacheHit: false,
      });
    }
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
      expect(result.diagnostics).toMatchObject({
        source: 'cache',
        outcome: 'accepted',
        cacheHit: true,
      });
    }
  });

  test('rejects a request with a cached invalid license, without calling the API', async () => {
    cachedLicenseValue = { isValid: false, credits: 0, cachedAt: 'x' };
    globalThis.fetch = neverFetch();

    const result = await validateAuth({ licenseKey: 'bad-key' });

    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.response.status).toBe(401);
      expect(result.diagnostics).toMatchObject({
        source: 'cache',
        outcome: 'cached_invalid',
        cacheHit: true,
      });
    }
  });

  test('forceRefresh bypasses a valid cache entry and re-validates via the API', async () => {
    cachedLicenseValue = { isValid: true, credits: 25, cachedAt: 'x' };
    globalThis.fetch = mock(async () => Response.json({ valid: true, credits: 99 })) as unknown as typeof fetch;

    const result = await validateAuth({ licenseKey: 'abc123' }, true);

    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.value.credits).toBe(99);
      expect(result.diagnostics).toMatchObject({
        source: 'api',
        outcome: 'accepted',
        upstreamStatus: 200,
      });
    }
    expect(cacheWrites).toHaveLength(1);
    expect(cacheWrites[0]?.license).toMatchObject({ isValid: true, credits: 99 });
  });

  test('validates via the API on a cache miss and caches a valid verdict', async () => {
    globalThis.fetch = mock(async () => Response.json({ valid: true, credits: 7 })) as unknown as typeof fetch;

    const result = await validateAuth({ licenseKey: 'fresh-key' });

    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.value.credits).toBe(7);
      expect(result.diagnostics).toMatchObject({
        source: 'api',
        outcome: 'accepted',
        cacheHit: false,
        upstreamStatus: 200,
      });
    }
    expect(cacheWrites).toHaveLength(1);
    expect(cacheWrites[0]?.license).toMatchObject({ isValid: true, credits: 7 });
  });

  test('sends only the license validation contract to the configured API with a timeout signal', async () => {
    process.env.NEXTJS_LICENSE_API_URL = 'https://licenses.example.test///';
    let receivedUrl = '';
    let receivedInit: RequestInit | undefined;
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      receivedUrl = String(input);
      receivedInit = init;
      return Response.json({ valid: true, credits: 12 });
    }) as unknown as typeof fetch;

    const result = await validateAuth({ licenseKey: 'boundary-key' });

    expect(result.ok).toBe(true);
    expect(receivedUrl).toBe('https://licenses.example.test/api/license/validate');
    expect(receivedInit?.method).toBe('POST');
    expect(receivedInit?.headers).toEqual({ 'Content-Type': 'application/json' });
    expect(JSON.parse(String(receivedInit?.body))).toEqual({
      license_key: 'boundary-key',
      include_credits: true,
    });
    expect(receivedInit?.signal).toBeInstanceOf(AbortSignal);
    expect(receivedInit?.signal?.aborted).toBe(false);
  });

  test('caches a definitive 4xx invalid verdict from the API', async () => {
    globalThis.fetch = mock(async () =>
      Response.json({ valid: false, error: 'revoked' }, { status: 404 })
    ) as unknown as typeof fetch;

    const result = await validateAuth({ licenseKey: 'revoked-key' });

    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.response.status).toBe(401);
      expect(result.diagnostics).toMatchObject({
        source: 'api',
        outcome: 'api_invalid',
        upstreamStatus: 404,
      });
    }
    expect(cacheWrites).toHaveLength(1);
    expect(cacheWrites[0]?.license.isValid).toBe(false);
  });

  test('fails closed on a transient 5xx without caching, so a retry hits the API again', async () => {
    globalThis.fetch = mock(async () => new Response('upstream down', { status: 503 })) as unknown as typeof fetch;

    const result = await validateAuth({ licenseKey: 'transient-key' });

    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.response.status).toBe(401);
      expect(result.diagnostics).toMatchObject({
        source: 'api',
        outcome: 'api_transient_status',
        upstreamStatus: 503,
      });
    }
    expect(cacheWrites).toHaveLength(0);
  });

  test('fails closed on rate limiting without caching a false invalid verdict', async () => {
    globalThis.fetch = mock(async () =>
      Response.json({ valid: false }, { status: 429 })
    ) as unknown as typeof fetch;

    const result = await validateAuth({ licenseKey: 'rate-limited-key' });

    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.response.status).toBe(401);
      expect(result.diagnostics).toMatchObject({
        source: 'api',
        outcome: 'api_transient_status',
        upstreamStatus: 429,
      });
    }
    expect(cacheWrites).toHaveLength(0);
  });

  test('never accepts a valid-looking payload carried by a transient error response', async () => {
    globalThis.fetch = mock(async () =>
      Response.json({ valid: true, credits: 1_000_000 }, { status: 503 })
    ) as unknown as typeof fetch;

    const result = await validateAuth({ licenseKey: 'error-payload-key' });

    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.diagnostics.outcome).toBe('api_transient_status');
      expect(result.diagnostics.upstreamStatus).toBe(503);
    }
    expect(cacheWrites).toHaveLength(0);
  });

  test('classifies a malformed transient response by its status and leaves the cache untouched', async () => {
    globalThis.fetch = mock(async () =>
      new Response('<html>temporary failure</html>', { status: 502 })
    ) as unknown as typeof fetch;

    const result = await validateAuth({ licenseKey: 'bad-gateway-key' });

    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.diagnostics).toMatchObject({
        outcome: 'api_transient_status',
        upstreamStatus: 502,
      });
    }
    expect(cacheWrites).toHaveLength(0);
  });

  test('fails closed on a network error without caching the result', async () => {
    globalThis.fetch = mock(async () => {
      throw new TypeError('fetch failed', { cause: { code: 'ECONNREFUSED' } });
    }) as unknown as typeof fetch;

    const result = await validateAuth({ licenseKey: 'unreachable-key' });

    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.response.status).toBe(401);
      expect(result.diagnostics).toMatchObject({
        source: 'api',
        outcome: 'api_network_error',
        apiErrorCode: 'ECONNREFUSED',
        apiErrorType: 'type_error',
      });
    }
    expect(cacheWrites).toHaveLength(0);
  });

  test('treats a timeout the same as any other network failure: fails closed, does not cache', async () => {
    globalThis.fetch = mock(async () => {
      const error = new DOMException('The operation timed out.', 'TimeoutError');
      throw error;
    }) as unknown as typeof fetch;

    const result = await validateAuth({ licenseKey: 'slow-key' });

    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.response.status).toBe(401);
      expect(result.diagnostics).toMatchObject({
        source: 'api',
        outcome: 'api_timeout',
      });
    }
    expect(cacheWrites).toHaveLength(0);
  });

  test('treats malformed JSON from a 2xx response as an invalid license, and caches it', async () => {
    globalThis.fetch = mock(async () => new Response('not json', { status: 200 })) as unknown as typeof fetch;

    const result = await validateAuth({ licenseKey: 'malformed-key' });

    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.response.status).toBe(401);
      expect(result.diagnostics).toMatchObject({
        source: 'api',
        outcome: 'api_invalid_json',
        upstreamStatus: 200,
      });
    }
    expect(cacheWrites).toHaveLength(1);
    expect(cacheWrites[0]?.license.isValid).toBe(false);
  });

  test('classifies valid JSON with the wrong shape as invalid JSON', async () => {
    globalThis.fetch = mock(async () => Response.json('not an object')) as unknown as typeof fetch;

    const result = await validateAuth({ licenseKey: 'wrong-shape-key' });

    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.diagnostics).toMatchObject({
        source: 'api',
        outcome: 'api_invalid_json',
        upstreamStatus: 200,
      });
    }
  });

  test('defaults malformed credits to zero so an accepted license cannot gain a synthetic balance', async () => {
    globalThis.fetch = mock(async () =>
      Response.json({ valid: true, credits: 'unlimited' })
    ) as unknown as typeof fetch;

    const result = await validateAuth({ licenseKey: 'bad-credits-key' });

    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.value.credits).toBe(0);
    }
    expect(cacheWrites).toHaveLength(1);
    expect(cacheWrites[0]?.license).toMatchObject({ isValid: true, credits: 0 });
  });
});

describe('authDiagnosticsForLog', () => {
  test('maps diagnostics to stable log fields without adding credential data', () => {
    const logged = authDiagnosticsForLog({
      source: 'api',
      outcome: 'api_network_error',
      cacheHit: false,
      elapsedMs: 14,
      apiElapsedMs: 12,
      apiErrorCode: 'ECONNRESET',
      apiErrorType: 'type_error',
    });

    expect(logged).toEqual({
      authSource: 'api',
      authOutcome: 'api_network_error',
      authCacheHit: false,
      authElapsedMs: 14,
      authApiElapsedMs: 12,
      authApiErrorCode: 'ECONNRESET',
      authApiErrorType: 'type_error',
      authUpstreamStatus: undefined,
    });
    expect(Object.keys(logged).some((key) => /key|licen[cs]e/i.test(key))).toBe(false);
  });
});
