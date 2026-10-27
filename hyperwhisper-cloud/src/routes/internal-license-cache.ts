// POST /internal/license-cache/invalidate — the site's "this key is dead" call.
//
// The cloud caches each key's licence verdict for an hour (`license:<key>`,
// `lib/redis-core.ts`) in its OWN Upstash database, which the site cannot
// reach. When the owner rotates an Account Key on the dashboard, the site
// already refuses the old string, but a cached "valid" here would keep it
// transcribing for up to an hour. The site calls this right after the rotation
// commits, and the entry becomes a stamped refusal.
//
// Gated by the shared `HYPERWHISPER_INTERNAL_SECRET` in `x-internal-secret`,
// the same secret and header this service already sends the site
// (`lib/latency-report.ts`), compared in constant time. No secret configured
// means the route refuses everything (503): it never runs open.
//
// The worst a caller holding the secret can do is mark a key refused for an
// hour — the same verdict the site's own 400 would cache — and the next
// `/usage?force_refresh=true` or the TTL restores a key that is still valid.
//
// The body carries the raw key, so nothing here logs the body or the key.

import { createHash, timingSafeEqual } from 'node:crypto';
import type { Context } from 'hono';
import { invalidateCachedLicense } from '../lib/redis-core';
import { validateContentLength } from '../lib/request-limits';
import { createRedisGetter } from '../lib/redis-client';

export const INTERNAL_SECRET_HEADER = 'x-internal-secret';

/** Longer than any key the site mints (19 chars). */
const MAX_KEY_LENGTH = 128;

/**
 * The most body this route reads. `{"account_key":"<128 chars>"}` is ~150
 * bytes; the cap is checked on Content-Length BEFORE `c.req.json()` buffers
 * anything, and the header is required so chunked encoding cannot skip it
 * (`lib/request-limits.ts`, the `/post-process` gate).
 */
export const MAX_INVALIDATE_BODY_BYTES = 1024;

export interface LicenseCacheInvalidateDeps {
  /** Read per request, so a secret rotated in Infisical needs no restart. */
  secret: () => string | undefined;
  invalidate: (licenseKey: string) => Promise<boolean>;
}

/** Equal-length digests, so neither the length nor the content leaks timing. */
export function secretMatches(provided: string | undefined, expected: string): boolean {
  if (!provided) return false;
  const a = createHash('sha256').update(provided).digest();
  const b = createHash('sha256').update(expected).digest();
  return timingSafeEqual(a, b);
}

export function createLicenseCacheInvalidateRoute(deps: LicenseCacheInvalidateDeps) {
  return async (c: Context) => {
    const expected = deps.secret();
    if (!expected) {
      console.error(JSON.stringify({ event: 'license_cache.invalidate_unconfigured' }));
      return c.json({ error: 'Not configured' }, 503);
    }
    if (!secretMatches(c.req.header(INTERNAL_SECRET_HEADER), expected)) {
      return c.json({ error: 'Unauthorized' }, 401);
    }

    const size = validateContentLength(c.req.header('content-length'), MAX_INVALIDATE_BODY_BYTES);
    if (!size.ok) return size.response;

    let body: unknown;
    try {
      body = await c.req.json();
    } catch {
      return c.json({ error: 'Invalid request body' }, 400);
    }

    const raw = typeof body === 'object' && body !== null && 'account_key' in body
      ? (body as { account_key: unknown }).account_key
      : undefined;
    // The same spelling the cache is keyed on: trimmed, case kept
    // (`canonicalAccountKey` in middleware/auth.ts).
    const licenseKey = typeof raw === 'string' ? raw.trim() : '';
    if (!licenseKey || licenseKey.length > MAX_KEY_LENGTH) {
      return c.json({ error: 'account_key is required' }, 400);
    }

    const ok = await deps.invalidate(licenseKey);
    console.log(JSON.stringify({ event: 'license_cache.invalidated', ok }));
    if (!ok) return c.json({ error: 'Cache unavailable' }, 503);

    return c.json({ invalidated: true });
  };
}

// Its own lazy client over the same UPSTASH_REDIS_CLOUD_* pair, as the credit
// hold store has: `lib/redis` is replaced with `mock.module` by many suites, and
// importing a new export through it would break every one of their stubs.
const getRedis = createRedisGetter();

export const licenseCacheInvalidateRoute = createLicenseCacheInvalidateRoute({
  secret: () => process.env.HYPERWHISPER_INTERNAL_SECRET,
  invalidate: (licenseKey) => invalidateCachedLicense(getRedis, licenseKey),
});
