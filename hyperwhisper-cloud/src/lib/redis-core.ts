// UPSTASH REDIS — I/O-FREE CORE
//
// The IP block lookup, the license-cache read and the license-cache write, with
// their one I/O edge — the Upstash client — passed in as a factory.
//
// `lib/redis.ts` stays the module every caller imports; it supplies the real,
// lazily built client and re-exports the same four values it always did. This
// split exists because TEN suites replace `../lib/redis` with `mock.module`,
// and bun's module registry is process-wide: a test that imported `./redis` to
// exercise the logic below would get another suite's stub whenever that suite
// loaded first, and would then assert nothing. Nothing mocks `./redis-core`, so
// a test here mocks nothing at all. Same split, and the same reason, as
// `lib/google-auth-core.ts`.

import { LICENSE_CACHE_TTL_SECONDS } from './constants';
import { isRecord } from './utils';

/** The two Upstash commands this module uses. */
export interface RedisStore {
  get<TData = unknown>(key: string): Promise<TData | null>;
  set(key: string, value: unknown, opts: { ex: number }): Promise<unknown>;
}

/**
 * Called per operation, never held. The real factory THROWS when the Upstash
 * env vars are absent, so every function below calls it inside its own `try` —
 * that keeps the long-standing "no Redis configured fails open" behaviour
 * (`isIPBlocked` false, `getCachedLicense` a miss, `cacheLicense` a no-op)
 * exactly as it was when they called `getRedis()` inline.
 */
export type RedisStoreFactory = () => RedisStore;

// ============================================================================
// IP BLOCKING
// ============================================================================

export async function isIPBlocked(store: RedisStoreFactory, ip: string): Promise<boolean> {
  try {
    const blockKey = `ip_blocked:${ip}`;
    const blocked = await store().get(blockKey);
    return blocked === 'true';
  } catch {
    return false;
  }
}

// ============================================================================
// LICENSE CACHE (1 hour TTL for valid + invalid)
// ============================================================================

export interface CachedLicense {
  isValid: boolean;
  credits: number;
  cachedAt: string;
}

function isCachedLicense(value: unknown): value is CachedLicense {
  if (!isRecord(value)) return false;
  return typeof value.isValid === 'boolean'
    && typeof value.credits === 'number'
    && typeof value.cachedAt === 'string';
}

export async function getCachedLicense(
  store: RedisStoreFactory,
  licenseKey: string
): Promise<CachedLicense | null> {
  try {
    const cached = await store().get<CachedLicense>(`license:${licenseKey}`);
    if (!cached) return null;

    // Validate the shape instead of asserting it. An entry written by an older
    // schema (or a truncated string) used to come back with `isValid`
    // undefined, which auth.ts reads as "license invalid" and locks a paying
    // user out for the full TTL. Treat anything unrecognised as a cache MISS
    // so the next request revalidates against the license API.
    const parsed: unknown = typeof cached === 'string' ? JSON.parse(cached) : cached;
    return isCachedLicense(parsed) ? parsed : null;
  } catch (error) {
    console.error('Failed to get cached license:', error);
    return null;
  }
}

export async function cacheLicense(
  store: RedisStoreFactory,
  licenseKey: string,
  license: CachedLicense
): Promise<void> {
  try {
    await store().set(`license:${licenseKey}`, license, { ex: LICENSE_CACHE_TTL_SECONDS });
  } catch (error) {
    console.error('Failed to cache license:', error);
  }
}
