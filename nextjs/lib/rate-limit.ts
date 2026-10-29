import { cloudflareLimiter } from "@/lib/cloudflare-rate-limit";

/**
 * The site's rate limiters: Cloudflare Workers Rate Limiting bindings, one per
 * limit, declared under `ratelimits` in `wrangler.jsonc`. The numbers live
 * THERE (tests/rate-limit.test.ts pins them); each binding has its own
 * namespace, so the key is just the caller's identity.
 *
 * Counts are approximate and kept per Cloudflare location, and a window can
 * only be 10 or 60 seconds. They bound abuse; they are not exact quotas.
 */

/**
 * Download email endpoint, per IP. 2 per minute (was 10 an hour on Upstash;
 * Cloudflare windows stop at 60 seconds): room to fix a typo, not to spam.
 */
export const downloadEmailRateLimiter = cloudflareLimiter(
  "RATE_LIMIT_DOWNLOAD_EMAIL",
  60,
);

/**
 * The public license validate/activate endpoints, per IP.
 *
 * These endpoints are unauthenticated, so any caller can flood random keys
 * and drive database lookups. The limit is
 * generous enough for legitimate clients (the macOS app re-validates
 * periodically and many users may share a NAT IP) while bounding abuse.
 *
 * 30 per minute.
 */
export const licenseValidateRateLimiter = cloudflareLimiter(
  "RATE_LIMIT_LICENSE_VALIDATE",
  60,
);

/**
 * `GET /api/license/credits` (and its `/api/account/credits` alias), per IP:
 * a key -> balance lookup open to anyone, so the same key-guessing exposure
 * as validate/activate (#2010).
 *
 * Its OWN budget, not validate's: HyperWhisper Cloud calls this route (on
 * `/usage?force_refresh=true`) and falls back to `/api/license/validate` when
 * it fails, both from a Fly region's shared exit address. Sharing one budget
 * would let refreshes starve the cloud's validation of the same users.
 *
 * 300 per minute. Ten times validate's ceiling, because one Fly region's
 * refreshes for every user arrive from one address; it still stops a single
 * caller scraping keys.
 */
export const licenseCreditsRateLimiter = cloudflareLimiter(
  "RATE_LIMIT_LICENSE_CREDITS",
  60,
);

/**
 * The internal STT latency ingest.
 *
 * Keyed by Fly region, NOT by IP: every machine in one region shares an exit
 * address, so a per-IP limit would make the busiest region throttle itself.
 * This exists only as a runaway-loop backstop — a bug in the edge service that
 * reports in a hot loop should not be able to fill the table. The ceiling sits
 * far above real traffic, so a healthy region never sees it.
 *
 * 6000 batches per region per minute.
 */
export const latencyIngestRateLimiter = cloudflareLimiter(
  "RATE_LIMIT_LATENCY_INGEST",
  60,
);

/**
 * The checkout endpoints (`/api/checkout`, `/api/checkout/credits`), per IP.
 *
 * `/api/checkout/credits` creates a Stripe Checkout Session on every call (and
 * may create a Stripe customer), and both routes are open to guests, so the
 * caller's IP is the only identity there is. A buyer clicks through a handful
 * of times at most; the ceiling only bounds a script (#2010).
 *
 * 10 per minute.
 */
export const checkoutRateLimiter = cloudflareLimiter("RATE_LIMIT_CHECKOUT", 60);

/**
 * Rotating an Account Key from the dashboard (`customer.rotateAccountKey`).
 *
 * Keyed by the signed-in USER id, not the IP: the caller is authenticated, so
 * the account is the identity, and a shared office NAT must not lock out a
 * neighbour. A rotation signs the user's other browsers out, clears the key's
 * device list and sends an email, so a loop (a stuck button, a script holding a
 * stolen session) is bounded hard. A real owner rotates once and is done.
 *
 * 1 per user per minute (was 5 an hour on Upstash; Cloudflare windows stop at
 * 60 seconds).
 */
export const accountKeyRotateRateLimiter = cloudflareLimiter(
  "RATE_LIMIT_ACCOUNT_KEY_ROTATE",
  60,
);
