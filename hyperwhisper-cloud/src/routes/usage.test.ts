import { afterEach, describe, expect, mock, test } from 'bun:test';
import { Hono } from 'hono';

const originalFetch = globalThis.fetch;

const cacheWrites: Array<{ licenseKey: string; license: { isValid: boolean; credits: number; cachedAt: string } }> = [];
let cachedLicense: { isValid: boolean; credits: number; cachedAt: string } | null = {
  isValid: true,
  credits: 12,
  cachedAt: 'cached',
};

mock.module('../lib/redis', () => ({
  getCachedLicense: async () => cachedLicense,
  cacheLicense: async (licenseKey: string, license: { isValid: boolean; credits: number; cachedAt: string }) => {
    cacheWrites.push({ licenseKey, license });
  },
  isIPBlocked: async () => false,
}));

const { readFiniteCredits, usageRoute } = await import('./usage');
const { validateAuth } = await import('../middleware/auth');

afterEach(() => {
  cacheWrites.length = 0;
  cachedLicense = { isValid: true, credits: 12, cachedAt: 'cached' };
  globalThis.fetch = originalFetch;
});

describe('readFiniteCredits', () => {
  test('accepts only finite numeric credits', () => {
    expect(readFiniteCredits({ credits: 12.5 })).toBe(12.5);
    expect(readFiniteCredits({ credits: null })).toBeNull();
    expect(readFiniteCredits({ credits: '12.5' })).toBeNull();
    expect(readFiniteCredits({ credits: Number.NaN })).toBeNull();
    expect(readFiniteCredits({ credits: Number.POSITIVE_INFINITY })).toBeNull();
    expect(readFiniteCredits({})).toBeNull();
  });
});

describe('validateAuth transient licensing responses', () => {
  test.each([429, 503])('does not cache HTTP %i as an invalid license verdict', async (status) => {
    globalThis.fetch = mock(async () =>
      Response.json({ valid: false }, { status })
    ) as unknown as typeof fetch;

    const result = await validateAuth({ licenseKey: 'test-license' }, true);

    expect(result.ok).toBe(false);
    expect(cacheWrites).toHaveLength(0);
  });
});

describe('usageRoute force refresh', () => {
  test('revalidates and replaces a cached invalid verdict', async () => {
    cachedLicense = { isValid: false, credits: 0, cachedAt: 'cached-invalid' };
    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/api/license/validate')) {
        return Response.json({ valid: true, credits: 42 });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const app = new Hono();
    app.get('/usage', usageRoute);

    const response = await app.request('/usage?license_key=test-license&force_refresh=true');
    const body = await response.json() as { credits_remaining: number; is_licensed: boolean };

    expect(response.status).toBe(200);
    expect(body.is_licensed).toBe(true);
    expect(body.credits_remaining).toBe(42);
    expect(cacheWrites).toHaveLength(1);
    expect(cacheWrites[0]).toMatchObject({
      licenseKey: 'test-license',
      license: { isValid: true, credits: 42 },
    });
  });

  test.each([429, 503])('does not cache transient HTTP %i as invalid', async (status) => {
    cachedLicense = { isValid: false, credits: 0, cachedAt: 'cached-invalid' };
    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/api/license/validate')) {
        return Response.json({ valid: false }, { status });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const app = new Hono();
    app.get('/usage', usageRoute);

    const response = await app.request('/usage?license_key=test-license&force_refresh=true');

    expect(response.status).toBe(401);
    expect(cacheWrites).toHaveLength(0);
  });

  test('does not cache malformed credits balance responses and falls back to validation', async () => {
    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/license/credits?')) {
        return Response.json({ credits: null });
      }

      if (url.includes('/api/license/validate')) {
        return Response.json({ valid: true, credits: 42 });
      }

      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const app = new Hono();
    app.get('/usage', usageRoute);

    const response = await app.request('/usage?license_key=test-license&force_refresh=true');
    const body = await response.json() as { credits_remaining: number; is_licensed: boolean };

    expect(response.status).toBe(200);
    expect(body.is_licensed).toBe(true);
    expect(body.credits_remaining).toBe(42);
    expect(cacheWrites).toHaveLength(1);
    expect(cacheWrites[0]?.license).toMatchObject({ isValid: true, credits: 42 });
  });
});
