import Stripe from "stripe";

import { env } from "@/src/env/server.mjs";

/**
 * Stripe client for server-side operations.
 * Uses the secret key from environment variables. This file is the ONE place
 * a Stripe client is constructed; `tests/stripe-client-boundary.test.ts`
 * fails the suite when another file builds one.
 *
 * Usage:
 * ```tsx
 * import { stripe } from '@/lib/clients/stripe';
 * const customers = await stripe.customers.list();
 * ```
 */
export const stripe = new Stripe(env.STRIPE_SECRET_KEY, {
  // @ts-expect-error - managed_payments_preview is in private preview
  apiVersion: "2025-12-15.clover; managed_payments_preview=v1",
  typescript: true,
});

/**
 * The admin refund flow's Stripe client (#755). It retrieves the checkout
 * session and creates the refund in `server/api/routers/admin/customers.ts`,
 * and stays pinned to the API version that flow was built and tested
 * against, not the newer one `stripe` tracks above. The pin is deliberate:
 * keep it, and do not fold this client into `stripe`, without re-testing
 * live refunds. New code uses `stripe`.
 */
export const stripeRefundApi = new Stripe(env.STRIPE_SECRET_KEY, {
  // @ts-expect-error - the SDK's types only admit the API version it ships
  // with; this flow stays pinned to the older one it was tested against.
  apiVersion: "2025-02-24.acacia",
});

// Validate that required environment variables are set
if (!env.STRIPE_SECRET_KEY) {
  console.warn(
    "STRIPE_SECRET_KEY not configured - Stripe operations will fail"
  );
}
