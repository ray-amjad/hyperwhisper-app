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
 * These endpoints are unauthenticated and, on a database miss, fall back to a
 * live Polar API call (importLicenseFromPolar). Without a limiter, one cheap
 * unauthenticated POST maps 1:1 to one outbound Polar request, letting any
 * caller flood random keys to burn Polar quota / amplify load. The limit is
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
