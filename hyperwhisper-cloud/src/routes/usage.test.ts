import { afterEach, describe, expect, mock, test } from 'bun:test';
import { Hono } from 'hono';

const originalFetch = globalThis.fetch;

const cacheWrites: Array<{ licenseKey: string; license: { isValid: boolean; credits: number; cachedAt: string } }> = [];
// Every key the route looked up in Redis, in order. The route accepts the
// credential under three query-param names, so this is how a test proves which
// one won and whether it was trimmed before the lookup.
const cacheReads: string[] = [];
let cachedLicense: { isValid: boolean; credits: number; cachedAt: string } | null = {
  isValid: true,
  credits: 12,
  cachedAt: 'cached',
};
let ipBlocked = false;

mock.module('../lib/redis', () => ({
  // Process-wide mock: without this export the suites that reach
  // lib/google-auth's static `import { redis }` fail to load. See
  // post-process.test.ts.
  redis: {},
  getCachedLicense: async (licenseKey: string) => {
    cacheReads.push(licenseKey);
    return cachedLicense;
  },
  cacheLicense: async (licenseKey: string, license: { isValid: boolean; credits: number; cachedAt: string }) => {
    cacheWrites.push({ licenseKey, license });
  },
  isIPBlocked: async () => ipBlocked,
}));

const { readFiniteCredits, usageRoute } = await import('./usage');
const { validateAuth } = await import('../middleware/auth');
const { CREDITS_PER_MINUTE } = await import('../lib/constants');

afterEach(() => {
  cacheWrites.length = 0;
  cacheReads.length = 0;
  cachedLicense = { isValid: true, credits: 12, cachedAt: 'cached' };
  ipBlocked = false;
  globalThis.fetch = originalFetch;
});

function buildApp(): Hono {
  const app = new Hono();
  app.get('/usage', usageRoute);
  return app;
}

// Any fetch is a failure unless a test opts into one. Several branches of the
// route are defined by NOT going to the network, so an un-asserted call has to
// be loud rather than silently satisfied by a permissive default.
function forbidFetch(): { calls: string[] } {
  const calls: string[] = [];
  globalThis.fetch = mock(async (input: RequestInfo | URL) => {
    calls.push(String(input));
    throw new Error(`Unexpected fetch: ${String(input)}`);
  }) as unknown as typeof fetch;
  return { calls };
}

// Route each licensing endpoint to a handler, recording every URL requested.
function withLicenseApi(handlers: {
  validate?: (body: Record<string, unknown>) => Response | Promise<Response>;
  credits?: (url: string) => Response | Promise<Response>;
}): { urls: string[] } {
  const urls: string[] = [];
  globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    urls.push(url);

    if (url.includes('/api/license/validate') && handlers.validate) {
      return handlers.validate(JSON.parse(String(init?.body)) as Record<string, unknown>);
    }
    if (url.includes('/api/license/credits') && handlers.credits) {
      return handlers.credits(url);
    }

    throw new Error(`Unexpected fetch: ${url}`);
  }) as unknown as typeof fetch;
  return { urls };
}

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

describe('usageRoute credential resolution', () => {
  test('a request with no credential is refused with 401 and never reaches the licensing API', async () => {
    const fetchGuard = forbidFetch();

    const response = await buildApp().request('/usage');
    const body = await response.json() as { error: string };

    expect(response.status).toBe(401);
    expect(body.error).toBe('License required');
    expect(fetchGuard.calls).toHaveLength(0);
    expect(cacheReads).toHaveLength(0);
  });

  test('an "identifier" param that is only whitespace is not a credential', async () => {
    forbidFetch();

    const response = await buildApp().request('/usage?identifier=%20%20%20');

    expect(response.status).toBe(401);
    expect(cacheReads).toHaveLength(0);
  });

  test('account_key wins over the legacy license_key when both are sent', async () => {
    forbidFetch();

    const response = await buildApp().request('/usage?account_key=canonical-key&license_key=legacy-key');

    expect(response.status).toBe(200);
    expect(cacheReads).toEqual(['canonical-key']);
  });

  test('license_key is honoured when account_key is absent', async () => {
    forbidFetch();

    const response = await buildApp().request('/usage?license_key=legacy-key');

    expect(response.status).toBe(200);
    expect(cacheReads).toEqual(['legacy-key']);
  });

  test('the "identifier" alias is accepted and trimmed before the lookup', async () => {
    forbidFetch();

    const response = await buildApp().request('/usage?identifier=%20spaced-key%20');

    expect(response.status).toBe(200);
    // The other two aliases are used verbatim; only `identifier` is trimmed.
    expect(cacheReads).toEqual(['spaced-key']);
  });

  test('a blocked IP is refused with 403 before the credential is even read', async () => {
    ipBlocked = true;
    const fetchGuard = forbidFetch();

    const response = await buildApp().request('/usage?account_key=valid-key', {
      headers: { 'Fly-Client-IP': '203.0.113.7' },
    });
    const body = await response.json() as { error: string };

    expect(response.status).toBe(403);
    expect(body.error).toBe('Access denied');
    expect(cacheReads).toHaveLength(0);
    expect(fetchGuard.calls).toHaveLength(0);
  });
});

describe('usageRoute cached path (no force_refresh)', () => {
  test('a cached valid license is answered from Redis with no licensing API call', async () => {
    cachedLicense = { isValid: true, credits: 12, cachedAt: 'cached' };
    const fetchGuard = forbidFetch();

    const response = await buildApp().request('/usage?account_key=valid-key');
    const body = await response.json() as { credits_remaining: number };

    expect(response.status).toBe(200);
    expect(body.credits_remaining).toBe(12);
    expect(fetchGuard.calls).toHaveLength(0);
    // Answering from cache must not re-write the entry it just read.
    expect(cacheWrites).toHaveLength(0);
  });

  test('a cached invalid verdict is refused with 401 without revalidating', async () => {
    cachedLicense = { isValid: false, credits: 0, cachedAt: 'cached-invalid' };
    const fetchGuard = forbidFetch();

    const response = await buildApp().request('/usage?account_key=revoked-key');
    const body = await response.json() as { error: string };

    expect(response.status).toBe(401);
    expect(body.error).toBe('Invalid license key');
    expect(fetchGuard.calls).toHaveLength(0);
  });

  test('a cache miss validates against the licensing API and caches the verdict', async () => {
    cachedLicense = null;
    const validateBodies: Array<Record<string, unknown>> = [];
    withLicenseApi({
      validate: (body) => {
        validateBodies.push(body);
        return Response.json({ valid: true, credits: 30 });
      },
    });

    const response = await buildApp().request('/usage?account_key=fresh-key');
    const body = await response.json() as { credits_remaining: number };

    expect(response.status).toBe(200);
    expect(body.credits_remaining).toBe(30);
    expect(validateBodies).toEqual([{ license_key: 'fresh-key', include_credits: true }]);
    expect(cacheWrites).toHaveLength(1);
    expect(cacheWrites[0]).toMatchObject({
      licenseKey: 'fresh-key',
      license: { isValid: true, credits: 30 },
    });
  });
});

describe('usageRoute balance reporting', () => {
  test('rounds the balance to a tenth BEFORE deriving minutes_remaining', async () => {
    // 6.29 credits is under one minute at 6.3 credits/min, but the route rounds
    // first — so the user is told they have 1 minute, not 0.
    cachedLicense = { isValid: true, credits: 6.29, cachedAt: 'cached' };
    forbidFetch();

    const response = await buildApp().request('/usage?account_key=valid-key');
    const body = await response.json() as { credits_remaining: number; minutes_remaining: number };

    expect(body.credits_remaining).toBe(6.3);
    expect(body.minutes_remaining).toBe(1);
  });

  test('reports the full balance payload for a licensed account', async () => {
    cachedLicense = { isValid: true, credits: 63, cachedAt: 'cached' };
    forbidFetch();

    const response = await buildApp().request('/usage?account_key=valid-key');
    const body = await response.json() as Record<string, unknown>;

    expect(body).toEqual({
      credits_remaining: 63,
      minutes_remaining: 10,
      credits_per_minute: CREDITS_PER_MINUTE,
      is_licensed: true,
      is_trial: false,
      is_anonymous: false,
    });
  });

  test('a partial minute is floored away, never rounded up', async () => {
    // 20 credits is 3.17 minutes at 6.3 credits/min — promising 4 would let a
    // client start a request it cannot pay for.
    cachedLicense = { isValid: true, credits: 20, cachedAt: 'cached' };
    forbidFetch();

    const response = await buildApp().request('/usage?account_key=valid-key');
    const body = await response.json() as { minutes_remaining: number };

    expect(body.minutes_remaining).toBe(3);
  });

  test('a valid licence with an exhausted balance still reports 200 and zero minutes', async () => {
    // The credit gate lives on the spending routes; /usage only reports.
    cachedLicense = { isValid: true, credits: 0, cachedAt: 'cached' };
    forbidFetch();

    const response = await buildApp().request('/usage?account_key=valid-key');
    const body = await response.json() as { credits_remaining: number; minutes_remaining: number; is_licensed: boolean };

    expect(response.status).toBe(200);
    expect(body.is_licensed).toBe(true);
    expect(body.credits_remaining).toBe(0);
    expect(body.minutes_remaining).toBe(0);
  });
});

describe('usageRoute licence validation failures', () => {
  test('an authoritative valid:false verdict IS cached, unlike a transient 5xx', async () => {
    cachedLicense = null;
    withLicenseApi({ validate: () => Response.json({ valid: false }) });

    const response = await buildApp().request('/usage?account_key=revoked-key');

    expect(response.status).toBe(401);
    expect(cacheWrites).toHaveLength(1);
    expect(cacheWrites[0]?.license).toMatchObject({ isValid: false, credits: 0 });
  });

  test('valid:true with no credits field bills the account as zero rather than undefined', async () => {
    cachedLicense = null;
    withLicenseApi({ validate: () => Response.json({ valid: true }) });

    const response = await buildApp().request('/usage?account_key=fresh-key');
    const body = await response.json() as { credits_remaining: number };

    expect(response.status).toBe(200);
    expect(body.credits_remaining).toBe(0);
    expect(cacheWrites[0]?.license).toMatchObject({ isValid: true, credits: 0 });
  });

  test('a network failure reaching the licensing API fails closed and caches nothing', async () => {
    cachedLicense = null;
    globalThis.fetch = mock(async () => {
      throw new Error('connect ECONNREFUSED');
    }) as unknown as typeof fetch;

    const response = await buildApp().request('/usage?account_key=fresh-key');

    expect(response.status).toBe(401);
    expect(cacheWrites).toHaveLength(0);
  });

  test('a non-JSON validation body is treated as invalid, not as a crash', async () => {
    cachedLicense = null;
    withLicenseApi({ validate: () => new Response('<html>gateway</html>', { status: 200 }) });

    const response = await buildApp().request('/usage?account_key=fresh-key');

    expect(response.status).toBe(401);
  });
});

describe('usageRoute credits-balance refresh', () => {
  test('a valid cached licence refreshes from the balance endpoint without revalidating', async () => {
    cachedLicense = { isValid: true, credits: 12, cachedAt: 'cached' };
    const api = withLicenseApi({ credits: () => Response.json({ credits: 88.5 }) });

    const response = await buildApp().request('/usage?account_key=valid-key&force_refresh=true');
    const body = await response.json() as { credits_remaining: number };

    expect(response.status).toBe(200);
    expect(body.credits_remaining).toBe(88.5);
    expect(api.urls).toHaveLength(1);
    expect(api.urls[0]).toContain('/api/license/credits?');
    expect(cacheWrites[0]?.license).toMatchObject({ isValid: true, credits: 88.5 });
  });

  test('the licence key is URL-encoded into the balance query string', async () => {
    const licenseKey = 'lic/key:1';
    cachedLicense = { isValid: true, credits: 12, cachedAt: 'cached' };
    const api = withLicenseApi({ credits: () => Response.json({ credits: 5 }) });

    const response = await buildApp().request(
      `/usage?account_key=${encodeURIComponent(licenseKey)}&force_refresh=true`
    );

    expect(response.status).toBe(200);
    expect(cacheReads).toEqual([licenseKey]);
    expect(api.urls[0]).toContain(`license_key=${encodeURIComponent(licenseKey)}`);
    expect(api.urls[0]).not.toContain(licenseKey);
  });

  test('a non-2xx balance response falls back to full revalidation', async () => {
    cachedLicense = { isValid: true, credits: 12, cachedAt: 'cached' };
    const api = withLicenseApi({
      // The body still carries a number: the status alone must disqualify it,
      // otherwise an error payload could be read as a balance.
      credits: () => Response.json({ error: 'balance unavailable', credits: 999 }, { status: 500 }),
      validate: () => Response.json({ valid: true, credits: 7 }),
    });

    const response = await buildApp().request('/usage?account_key=valid-key&force_refresh=true');
    const body = await response.json() as { credits_remaining: number };

    expect(response.status).toBe(200);
    expect(body.credits_remaining).toBe(7);
    expect(api.urls.some((url) => url.includes('/api/license/validate'))).toBe(true);
    // The failed balance read must not have written a 0-credit entry.
    expect(cacheWrites).toHaveLength(1);
    expect(cacheWrites[0]?.license).toMatchObject({ isValid: true, credits: 7 });
  });

  test('a balance request that throws falls back to full revalidation', async () => {
    cachedLicense = { isValid: true, credits: 12, cachedAt: 'cached' };
    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/api/license/credits')) {
        throw new Error('socket hang up');
      }
      return Response.json({ valid: true, credits: 7 });
    }) as unknown as typeof fetch;

    const response = await buildApp().request('/usage?account_key=valid-key&force_refresh=true');
    const body = await response.json() as { credits_remaining: number };

    expect(response.status).toBe(200);
    expect(body.credits_remaining).toBe(7);
  });

  test('a cached INVALID verdict never reaches the balance endpoint', async () => {
    // The balance endpoint reports credits, not entitlement. Reading it for a
    // licence Redis already knows is invalid would report a live balance for a
    // revoked key.
    cachedLicense = { isValid: false, credits: 0, cachedAt: 'cached-invalid' };
    const api = withLicenseApi({
      credits: () => Response.json({ credits: 500 }),
      validate: () => Response.json({ valid: true, credits: 9 }),
    });

    const response = await buildApp().request('/usage?account_key=revoked-key&force_refresh=true');
    const body = await response.json() as { credits_remaining: number };

    expect(response.status).toBe(200);
    expect(body.credits_remaining).toBe(9);
    expect(api.urls.every((url) => !url.includes('/api/license/credits'))).toBe(true);
  });

  test('force_refresh with no cache entry skips the balance endpoint and revalidates', async () => {
    cachedLicense = null;
    const api = withLicenseApi({ validate: () => Response.json({ valid: true, credits: 15 }) });

    const response = await buildApp().request('/usage?account_key=fresh-key&force_refresh=true');

    expect(response.status).toBe(200);
    expect(api.urls.every((url) => !url.includes('/api/license/credits'))).toBe(true);
  });

  test('force_refresh is opt-in: any value other than "true" uses the cached balance', async () => {
    cachedLicense = { isValid: true, credits: 12, cachedAt: 'cached' };
    const fetchGuard = forbidFetch();

    const response = await buildApp().request('/usage?account_key=valid-key&force_refresh=1');

    expect(response.status).toBe(200);
    expect(fetchGuard.calls).toHaveLength(0);
  });
});

describe('usageRoute licensing API base URL', () => {
  const originalBase = process.env.NEXTJS_LICENSE_API_URL;

  afterEach(() => {
    if (originalBase === undefined) {
      delete process.env.NEXTJS_LICENSE_API_URL;
    } else {
      process.env.NEXTJS_LICENSE_API_URL = originalBase;
    }
  });

  test('trailing slashes on NEXTJS_LICENSE_API_URL do not produce a double-slash path', async () => {
    process.env.NEXTJS_LICENSE_API_URL = 'https://licensing.example.test///';
    cachedLicense = null;
    const api = withLicenseApi({ validate: () => Response.json({ valid: true, credits: 3 }) });

    const response = await buildApp().request('/usage?account_key=fresh-key');

    expect(response.status).toBe(200);
    expect(api.urls[0]).toBe('https://licensing.example.test/api/license/validate');
  });
});
