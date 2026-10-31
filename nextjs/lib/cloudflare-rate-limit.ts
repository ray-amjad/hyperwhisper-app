import { cloudflareBinding } from "@/lib/clients/cloudflare-binding";
import type { RequestLimiter } from "@/lib/rate-limit-gate";

/**
 * A `RequestLimiter` over one Cloudflare Workers Rate Limiting binding
 * (`ratelimits` in wrangler.jsonc). Its own module, apart from the limiters in
 * `lib/rate-limit.ts`, because route tests replace that module wholesale and
 * the Better Auth storage builds limiters with this.
 */

/** The slice of a `ratelimits` binding a limiter needs. */
export interface RateLimitBinding {
  limit(options: { key: string }): Promise<{ success: boolean }>;
}

/** Thrown when the binding is absent, so `countFailOpen` lets the call through and warns. */
export class RateLimitBindingMissingError extends Error {
  constructor(binding: string) {
    super(`No ${binding} rate limit binding`);
    this.name = "RateLimitBindingMissingError";
  }
}

export function cloudflareLimiter(
  binding: string,
  periodSeconds: 10 | 60,
  getBinding: (name: string) => RateLimitBinding | undefined = cloudflareBinding,
): RequestLimiter {
  return {
    async limit(identifier) {
      const limiter = getBinding(binding);

      if (!limiter) throw new RateLimitBindingMissingError(binding);
      const { success } = await limiter.limit({ key: identifier });

      // The binding does not say when its window frees a slot; a full period
      // from now is the latest it can be.
      return { success, reset: Date.now() + periodSeconds * 1000 };
    },
  };
}
