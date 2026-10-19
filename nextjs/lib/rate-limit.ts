import { Ratelimit } from "@upstash/ratelimit";
import redis from "@/lib/clients/redis";

/**
 * Rate limiter for download email endpoint.
 * Limits: 10 requests per IP per hour using sliding window algorithm.
 */
export const downloadEmailRateLimiter = new Ratelimit({
  redis: redis,
  limiter: Ratelimit.slidingWindow(10, "1 h"),
  prefix: "ratelimit:download-email",
  analytics: true,
});

/**
 * Rate limiter for the public license validate/activate endpoints.
 *
 * These endpoints are unauthenticated, so any caller can flood random keys
 * and drive database lookups. The limit is
 * generous enough for legitimate clients (the macOS app re-validates
 * periodically and many users may share a NAT IP) while bounding abuse.
 *
 * Limits: 30 requests per IP per minute using sliding window algorithm.
 */
export const licenseValidateRateLimiter = new Ratelimit({
  redis: redis,
  limiter: Ratelimit.slidingWindow(30, "1 m"),
  prefix: "ratelimit:license-validate",
  analytics: true,
});

/**
 * Rate limiter for `GET /api/license/credits` (and its `/api/account/credits`
 * alias): a key -> balance lookup open to anyone, so the same key-guessing
 * exposure as validate/activate (#2010).
 *
 * Its OWN budget, not validate's: HyperWhisper Cloud calls this route (on
 * `/usage?force_refresh=true`) and falls back to `/api/license/validate` when
 * it fails, both from a Fly region's shared exit address. Sharing one budget
 * would let refreshes starve the cloud's validation of the same users.
 *
 * Limits: 300 requests per IP per minute using sliding window algorithm. Ten
 * times validate's ceiling, because one Fly region's refreshes for every user
 * arrive from one address; it still stops a single caller scraping keys.
 */
export const licenseCreditsRateLimiter = new Ratelimit({
  redis: redis,
  limiter: Ratelimit.slidingWindow(300, "1 m"),
  prefix: "ratelimit:license-credits",
  analytics: true,
});

/**
 * Rate limiter for the internal STT latency ingest.
 *
 * Keyed by Fly region, NOT by IP: every machine in one region shares an exit
 * address, so a per-IP limit would make the busiest region throttle itself.
 * This exists only as a runaway-loop backstop — a bug in the edge service that
 * reports in a hot loop should not be able to fill the table. The ceiling sits
 * far above real traffic, so a healthy region never sees it.
 *
 * Limits: 6000 batches per region per minute using sliding window algorithm.
 */
export const latencyIngestRateLimiter = new Ratelimit({
  redis: redis,
  limiter: Ratelimit.slidingWindow(6000, "1 m"),
  prefix: "ratelimit:latency-ingest",
  analytics: true,
});

/**
 * Rate limiter for the checkout endpoints (`/api/checkout`,
 * `/api/checkout/credits`).
 *
 * `/api/checkout/credits` creates a Stripe Checkout Session on every call (and
 * may create a Stripe customer), and both routes are open to guests, so the
 * caller's IP is the only identity there is. A buyer clicks through a handful
 * of times at most; the ceiling only bounds a script (#2010).
 *
 * Limits: 10 requests per IP per minute using sliding window algorithm.
 */
export const checkoutRateLimiter = new Ratelimit({
  redis: redis,
  limiter: Ratelimit.slidingWindow(10, "1 m"),
  prefix: "ratelimit:checkout",
  analytics: true,
});
