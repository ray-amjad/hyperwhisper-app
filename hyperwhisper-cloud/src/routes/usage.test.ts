import { afterEach, describe, expect, mock, test } from 'bun:test';
import { Hono } from 'hono';

const originalFetch = globalThis.fetch;

const cacheWrites: Array<{ licenseKey: string; license: { isValid: boolean; credits: number; cachedAt: string } }> = [];

mock.module('../lib/redis', () => ({
  getCachedLicense: async () => ({ isValid: true, credits: 12, cachedAt: 'cached' }),
  cacheLicense: async (licenseKey: string, license: { isValid: boolean; credits: number; cachedAt: string }) => {
    cacheWrites.push({ licenseKey, license });
  },
  isIPBlocked: async () => false,
}));

const { readFiniteCredits, usageRoute } = await import('./usage');

afterEach(() => {
  cacheWrites.length = 0;
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

describe('usageRoute force refresh', () => {
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
