import type { Redis } from "@upstash/redis";
import type {
  BetterAuthRateLimitOptions,
  BetterAuthRateLimitStorage,
} from "better-auth";

import { Ratelimit } from "@upstash/ratelimit";

/**
 * SHARED STORAGE FOR BETTER AUTH'S RATE LIMITER (#2010)
 *
 * Better Auth's default rate-limit storage is an in-process `Map`. On Vercel
 * every function instance has its own, so the sign-in limits (3 / 10 s on
 * `/sign-in*`, 5 / 60 s on `/sign-in/license-key`) were per instance, not per
 * caller. This storage puts the counters in the site's Upstash database, so
 * every instance counts against one budget.
 *
 * Why `rateLimit.customStorage` and NOT top-level `secondaryStorage` (checked
 * against better-auth 1.7.7): setting `secondaryStorage` also moves SESSIONS
 * off Postgres — `session.storeSessionInDatabase` defaults to false, and
 * `internal-adapter.mjs` then reads and writes sessions in secondary storage
 * only. That would change sign-in, revocation and the admin role checks, which
 * this issue must not touch. `customStorage` is consulted by the rate limiter
 * alone (`api/rate-limiter/index.mjs`, `getRateLimitStorage`), and it overrides
 * `storage`.
 *
 * The limit numbers are Better Auth's own rules, unchanged: each rule arrives
 * here as `{ window, max }`, and each distinct rule gets its own Upstash
 * sliding window. The key Better Auth hands in is `<ip>|<path>`.
 *
 * FAILS OPEN: Upstash unconfigured (local dev, tests), throwing, or slower than
 * `AUTH_RATE_LIMIT_TIMEOUT_MS` lets the request through and logs a warning.
 * Sign-in must not depend on Upstash being up.
 */

/** How long one Upstash check may take before the request is served anyway. */
export const AUTH_RATE_LIMIT_TIMEOUT_MS = 1_000;

export type RedisSource = () => Promise<Redis | null>;

/**
 * The site's Upstash client, or `null` when its env is not set. Imported
 * lazily so a process that never rate-limits (or has no Upstash) does not
 * build a client that can only fail.
 */
export const siteRedis: RedisSource = async () => {
  if (
    !process.env.UPSTASH_REDIS_SITE_URL ||
    !process.env.UPSTASH_REDIS_SITE_TOKEN
  ) {
    return null;
  }
  const { default: redis } = await import("@/lib/clients/redis");

  return redis;
};

const ALLOW = { allowed: true, retryAfter: null } as const;

export function createUpstashAuthRateLimitStorage(
  getRedis: RedisSource,
  timeoutMs: number = AUTH_RATE_LIMIT_TIMEOUT_MS,
): BetterAuthRateLimitStorage {
  const limiters = new Map<string, Ratelimit>();
  let notConfiguredLogged = false;

  return {
    async consume(key, rule) {
      let limiter: Ratelimit | undefined;

      try {
        const ruleId = `${rule.window}s:${rule.max}`;

        limiter = limiters.get(ruleId);
        if (!limiter) {
          const redis = await getRedis();

          if (!redis) {
            if (!notConfiguredLogged) {
              notConfiguredLogged = true;
              console.warn(
                "Better Auth rate limit: Upstash is not configured, failing open",
              );
            }

            return ALLOW;
          }
          limiter = new Ratelimit({
            redis,
            limiter: Ratelimit.slidingWindow(rule.max, `${rule.window} s`),
            prefix: `ratelimit:better-auth:${ruleId}`,
            timeout: timeoutMs,
            analytics: false,
          });
          limiters.set(ruleId, limiter);
        }

        const { success, reset, reason } = await limiter.limit(key);

        if (reason === "timeout") {
          console.warn(
            "Better Auth rate limit: Upstash timed out, failing open",
          );
        }
        if (success) return ALLOW;

        return {
          allowed: false,
          retryAfter: Math.max(1, Math.ceil((reset - Date.now()) / 1000)),
        };
      } catch (error) {
        // The class name only: an Upstash error can quote the command, and
        // with it the caller's IP.
        const name = error instanceof Error ? error.name : typeof error;

        console.warn(
          `Better Auth rate limit: Upstash unavailable, failing open: ${name}`,
        );

        return ALLOW;
      }
    },
  };
}

/**
 * The `rateLimit` block `auth.ts` passes to `betterAuth()`. Enabled in every
 * environment (Better Auth's default is production only); without Upstash env
 * the storage lets everything through, so local dev is unaffected.
 */
export function authRateLimitOptions(
  storage: BetterAuthRateLimitStorage = createUpstashAuthRateLimitStorage(
    siteRedis,
  ),
): BetterAuthRateLimitOptions {
  return { enabled: true, customStorage: storage };
}
