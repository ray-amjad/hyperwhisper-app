// REQUEST RATE LIMITS (issue #2010)
//
// Upstash sliding windows on the cloud's own Upstash database, in three buckets
// (Ray, 2026-10-19):
//
//   - `ip`         120 requests / 60 s per client IP, BEFORE auth. A Hono
//                  middleware (`ipRateLimit`) that `index.ts` puts in front of
//                  `/transcribe`, `/post-process`, `/assistant`, `/usage` and
//                  every `/ws/streaming-*` upgrade, so a flood costs no body
//                  parse and no licence lookup.
//   - `key`        60 requests / 60 s per account key, AFTER auth. Each of the
//                  five route families calls `keyRateLimitResponse` right after
//                  `validateAuth` accepts the key and before any credit hold, so
//                  a junk key never gets a bucket and a 429 holds no credits.
//   - `ws_upgrade` 10 `/ws/streaming-*` upgrades / 60 s per account key, on top
//                  of the `key` bucket (an upgrade counts against both).
//
// The IP comes from `getClientIP` — the same helper `isIPBlocked` uses — so an
// off-edge caller lands in the shared `unknown` bucket rather than choosing its
// own IP. The account key is never a Redis key: it is the bearer credential, so
// the identifier is its SHA-256 (see `keyIdentifier`).
//
// Over the limit: HTTP 429, a `Retry-After` header in whole seconds, and the
// service's usual `{ error, message, ... }` JSON body (`lib/responses.ts`).
//
// UPSTASH UNAVAILABLE FAILS OPEN, like `isIPBlocked`: an unconfigured store, a
// thrown Upstash error, or a call slower than `RATE_LIMIT_TIMEOUT_MS` serves the
// request and logs `rate_limit.unavailable`. @upstash/ratelimit itself resolves
// `{ success: true, reason: 'timeout' }` when its timeout fires; that is the
// fail-open we want, and it is logged too so a disabled limiter is visible.

import { createHash } from 'node:crypto';
import type { Context, Next } from 'hono';
import { Ratelimit } from '@upstash/ratelimit';
import type { Redis } from '@upstash/redis';
import { createRedisGetter } from '../lib/redis-client';
import { getClientIP } from '../lib/request-id';
import { errorResponse } from '../lib/responses';
import { toRedactedLogLine } from '../lib/log-redaction';

export const RATE_LIMIT_WINDOW_SECONDS = 60;

/** Requests allowed per `RATE_LIMIT_WINDOW_SECONDS`, per bucket. */
export const RATE_LIMITS = {
  ip: 120,
  key: 60,
  ws_upgrade: 10,
} as const;

export type RateLimitBucket = keyof typeof RATE_LIMITS;

/**
 * How long one Upstash check may take before the request is served anyway.
 * Upstash's own default is 5 s; a brownout should cost a dictation well under
 * that, and a request runs at most three checks (ip, ws_upgrade, key).
 */
export const RATE_LIMIT_TIMEOUT_MS = 1_000;

/** What a store answers for one check. `reset` is the window end, unix ms. */
export interface RateLimitVerdict {
  success: boolean;
  reset: number;
  /** Set by @upstash/ratelimit; `'timeout'` means it gave up and let the request through. */
  reason?: string;
}

/** The one operation the limiter needs. Throwing means "unavailable": fail open. */
export interface RateLimitStore {
  limit(bucket: RateLimitBucket, identifier: string): Promise<RateLimitVerdict>;
}

/**
 * The production store: one `Ratelimit` per bucket, built on first use so a
 * process (or a test) that never reaches a limited route needs no Upstash env.
 * The getter THROWS when the env is missing; `checkRateLimit` turns that into a
 * fail-open like any other store error.
 */
export function createUpstashRateLimitStore(
  getRedis: () => Redis,
  timeoutMs: number = RATE_LIMIT_TIMEOUT_MS,
): RateLimitStore {
  const limiters = new Map<RateLimitBucket, Ratelimit>();
  return {
    async limit(bucket, identifier) {
      let limiter = limiters.get(bucket);
      if (!limiter) {
        limiter = new Ratelimit({
          redis: getRedis(),
          limiter: Ratelimit.slidingWindow(RATE_LIMITS[bucket], `${RATE_LIMIT_WINDOW_SECONDS} s`),
          prefix: `ratelimit:${bucket}`,
          timeout: timeoutMs,
          analytics: false,
        });
        limiters.set(bucket, limiter);
      }
      const { success, reset, reason } = await limiter.limit(identifier);
      return { success, reset, reason };
    },
  };
}

// Its own lazily built client, like `middleware/credit-holds.ts`: `lib/redis.ts`
// is replaced process-wide by a dozen suites' `mock.module`. Same database,
// same env vars.
const productionStore = createUpstashRateLimitStore(createRedisGetter());
let storeOverride: RateLimitStore | null = null;

/** Test-only: install a store, or `null` for the real Upstash one. */
export function setRateLimitStoreForTests(store: RateLimitStore | null): void {
  storeOverride = store;
}

function currentStore(): RateLimitStore {
  return storeOverride ?? productionStore;
}

// "Not configured" is a process-wide fact, so it is said once, not per request.
let notConfiguredLogged = false;

function logUnavailable(bucket: RateLimitBucket, reason: 'timeout' | 'store_error', error?: unknown): void {
  const message = error instanceof Error ? error.message : String(error ?? '');
  const notConfigured = message.includes('UPSTASH_REDIS_CLOUD_URL');
  if (notConfigured) {
    if (notConfiguredLogged) return;
    notConfiguredLogged = true;
  }
  // The identifier (an IP or a key hash) is deliberately not logged; the error
  // goes through the shared redaction, which bounds any echoed command payload.
  console.warn(JSON.stringify({
    event: 'rate_limit.unavailable',
    bucket,
    reason: notConfigured ? 'not_configured' : reason,
    failOpen: true,
    ...(error === undefined ? {} : { error: toRedactedLogLine(error) }),
  }));
}

export type RateLimitDecision =
  | { limited: false }
  | { limited: true; retryAfterSeconds: number };

/** Seconds until the window frees a slot: at least 1, at most one window. */
function retryAfterSeconds(reset: number, now: number): number {
  if (!Number.isFinite(reset) || reset <= now) return 1;
  return Math.min(RATE_LIMIT_WINDOW_SECONDS, Math.max(1, Math.ceil((reset - now) / 1000)));
}

/** Count one request against `bucket`. Never throws: any store failure is "not limited". */
export async function checkRateLimit(bucket: RateLimitBucket, identifier: string): Promise<RateLimitDecision> {
  let verdict: RateLimitVerdict;
  try {
    verdict = await currentStore().limit(bucket, identifier);
  } catch (error) {
    logUnavailable(bucket, 'store_error', error);
    return { limited: false };
  }
  if (verdict.reason === 'timeout') {
    logUnavailable(bucket, 'timeout');
  }
  if (verdict.success) return { limited: false };
  return { limited: true, retryAfterSeconds: retryAfterSeconds(verdict.reset, Date.now()) };
}

/** The 429 every limited route returns. */
export function rateLimitedResponse(retryAfter: number): Response {
  const response = errorResponse(
    429,
    'Too many requests',
    `Rate limit exceeded. Try again in ${retryAfter} second${retryAfter === 1 ? '' : 's'}.`,
    { retry_after_seconds: retryAfter },
  );
  response.headers.set('Retry-After', String(retryAfter));
  return response;
}

function logRejected(endpoint: string, bucket: RateLimitBucket, retryAfter: number): void {
  // Never the IP or the key: the bucket and the path say enough.
  console.log(JSON.stringify({
    event: 'rate_limit.rejected',
    endpoint,
    bucket,
    retryAfterSeconds: retryAfter,
  }));
}

/**
 * The per-key identifier. The account key is the bearer credential for every
 * request, so it must not sit in Redis as a key name, where the Upstash console
 * and any echoed command would show it.
 */
export function keyIdentifier(licenseKey: string): string {
  return createHash('sha256').update(licenseKey).digest('hex');
}

/** Hono middleware: the per-IP bucket, before the route parses a body or looks up a key. */
export async function ipRateLimit(c: Context, next: Next): Promise<Response | void> {
  const decision = await checkRateLimit('ip', getClientIP(c));
  if (decision.limited) {
    logRejected(c.req.path, 'ip', decision.retryAfterSeconds);
    return rateLimitedResponse(decision.retryAfterSeconds);
  }
  await next();
}

/**
 * The per-key buckets, for a route to call once `validateAuth` has accepted the
 * key. Returns the 429 to send, or `null` to carry on. Buckets are checked in
 * order and stop at the first refusal, so pass the stricter one first.
 */
export async function keyRateLimitResponse(
  endpoint: string,
  licenseKey: string,
  buckets: readonly RateLimitBucket[] = ['key'],
): Promise<Response | null> {
  const identifier = keyIdentifier(licenseKey);
  for (const bucket of buckets) {
    const decision = await checkRateLimit(bucket, identifier);
    if (decision.limited) {
      logRejected(endpoint, bucket, decision.retryAfterSeconds);
      return rateLimitedResponse(decision.retryAfterSeconds);
    }
  }
  return null;
}
