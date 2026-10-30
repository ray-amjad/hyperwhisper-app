import type {
  BetterAuthRateLimitOptions,
  BetterAuthRateLimitStorage,
} from "better-auth";

import {
  cloudflareLimiter,
  type RateLimitBinding,
} from "@/lib/cloudflare-rate-limit";
import { cloudflareBinding } from "@/lib/clients/cloudflare-binding";
import { countFailOpen, type RequestLimiter } from "@/lib/rate-limit-gate";

/**
 * SHARED STORAGE FOR BETTER AUTH'S RATE LIMITER (#2010)
 *
 * Better Auth's default rate-limit storage is an in-process `Map`. On
 * Cloudflare Workers every isolate has its own, so the sign-in limits were per
 * isolate, not per caller. This storage counts in Cloudflare Rate Limiting
 * bindings instead, which every isolate in a location shares.
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
 * The limit numbers are Better Auth's own rules, unchanged. A binding's limit
 * is fixed in `wrangler.jsonc`, so each rule Better Auth can hand in
 * (`{ window, max }`) has its own binding in `AUTH_RATE_LIMIT_BINDINGS`. The
 * key Better Auth hands in is `<ip>|<path>`.
 *
 * FAILS OPEN: a rule with no binding, no binding on `env` (local dev, tests),
 * or a binding that throws lets the request through and logs a warning.
 */

/**
 * Better Auth 1.7's rules, by `${window}s:${max}`, and the binding counting
 * each. tests/rate-limit.test.ts checks every one is in `wrangler.jsonc` with
 * the same numbers.
 * - 10 s / 100: the default for every other auth path.
 * - 10 s / 3: `/sign-in*`, `/sign-up*`, `/change-password*`, `/change-email*`.
 * - 60 s / 3: password reset and verification emails.
 * - 60 s / 5: the magic-link plugin and `/sign-in/license-key`.
 */
export const AUTH_RATE_LIMIT_BINDINGS: Readonly<Record<string, string>> = {
  "10s:100": "RATE_LIMIT_AUTH_10S_100",
  "10s:3": "RATE_LIMIT_AUTH_10S_3",
  "60s:3": "RATE_LIMIT_AUTH_60S_3",
  "60s:5": "RATE_LIMIT_AUTH_60S_5",
};

const ALLOW = { allowed: true, retryAfter: null } as const;

export function createCloudflareAuthRateLimitStorage(
  getBinding: (name: string) => RateLimitBinding | undefined = cloudflareBinding,
): BetterAuthRateLimitStorage {
  const limiters = new Map<string, RequestLimiter>();
  const unmappedLogged = new Set<string>();

  const limiterFor = (rule: {
    window: number;
    max: number;
  }): RequestLimiter | null => {
    const ruleId = `${rule.window}s:${rule.max}`;
    const existing = limiters.get(ruleId);

    if (existing) return existing;

    const binding = AUTH_RATE_LIMIT_BINDINGS[ruleId];

    if (!binding || (rule.window !== 10 && rule.window !== 60)) {
      if (!unmappedLogged.has(ruleId)) {
        unmappedLogged.add(ruleId);
        console.warn(
          `Better Auth rate limit: no binding for rule ${ruleId}, failing open`,
        );
      }

      return null;
    }
    const limiter = cloudflareLimiter(binding, rule.window, getBinding);

    limiters.set(ruleId, limiter);

    return limiter;
  };

  return {
    async consume(key, rule) {
      const limiter = limiterFor(rule);

      if (!limiter) return ALLOW;

      const outcome = await countFailOpen(
        () => limiter.limit(key),
        "Better Auth",
      );

      if (!outcome.limited) return ALLOW;

      return { allowed: false, retryAfter: outcome.retryAfterSeconds };
    },
  };
}

/**
 * The `rateLimit` block `auth.ts` passes to `betterAuth()`. Enabled in every
 * environment (Better Auth's default is production only); without the
 * bindings the storage lets everything through, so local dev is unaffected.
 */
export function authRateLimitOptions(
  storage: BetterAuthRateLimitStorage = createCloudflareAuthRateLimitStorage(),
): BetterAuthRateLimitOptions {
  return { enabled: true, customStorage: storage };
}
