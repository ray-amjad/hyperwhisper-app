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

/**
 * A Redis failure as ONE log line, with the client IP taken out of it.
 *
 * Logging the raw error would ship the caller's IP. `@upstash/redis` builds its
 * message as `` `${body.error}, command was: ${JSON.stringify(req.body)}` ``,
 * and `req.body` is the command we sent — `["get","ip_blocked:203.0.113.7"]`.
 * Its retry loop only retries a fetch that THREW, so every HTTP failure (a
 * rotated token's 401, a quota 429, any 5xx) lands in the catch below with the
 * key in the message. That is exactly the outage this log was added for, so
 * without redaction the line leaks an IP per request — and the client IP is
 * its own privacy finding on this service (#714). Nothing downstream redacts.
 *
 * Two passes, and the FIRST is the one that carries the guarantee:
 *
 * 1. By VALUE. The `ip` argument is the string the key was built from, so
 *    replacing that literal catches it wherever the message put it — after the
 *    key, in a socket error, in a future upstream that words things some other
 *    way. Matching the message SHAPE instead (splitting on `, command was:`)
 *    only holds while Upstash keeps that wording.
 * 2. By KEY. A backstop for an `ip_blocked:` value we were not handed, e.g. a
 *    pipelined command or a key built from a normalised address.
 *
 * Returns a STRING, never the Error: a raw Error prints a multi-line stack,
 * and the line-oriented shipper splits that into several unrelated records.
 */
function redactIPFromFailure(error: unknown, ip: string): string {
  const message = error instanceof Error ? `${error.name}: ${error.message}` : String(error);
  const withoutKeys = message.replace(/ip_blocked:[^"'\s\]]*/g, 'ip_blocked:<redacted>');
  // `replaceAll('')` would splice the replacement between every character.
  return ip.length > 0 ? withoutKeys.replaceAll(ip, '<redacted ip>') : withoutKeys;
}

export async function isIPBlocked(store: RedisStoreFactory, ip: string): Promise<boolean> {
  try {
    const blockKey = `ip_blocked:${ip}`;
    const blocked = await store().get(blockKey);
    return blocked === 'true';
  } catch (error) {
    // Keep failing open — a Redis outage must not lock every caller out. But
    // this is the only abuse gate the public endpoints have, so the fail-open
    // has to leave a record: stdout ships to Axiom, and without a line here a
    // disabled gate looks exactly like an hour with no blocked IPs. No IP in
    // the line — see `redactIPFromFailure`; the operation name and the
    // redacted error are enough to spot the outage.
    console.error('IP block check failed — failing open:', redactIPFromFailure(error, ip));
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
