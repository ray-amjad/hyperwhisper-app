import Stripe from "stripe";

/**
 * Stripe client for server-side operations.
 * Uses the secret key from environment variables.
 *
 * Usage:
 * ```tsx
 * import { stripe } from '@/lib/clients/stripe';
 * const customers = await stripe.customers.list();
 * ```
 */
export const stripe = new Stripe(process.env.STRIPE_SECRET_KEY!, {
  // @ts-expect-error - managed_payments_preview is in private preview
  apiVersion: "2025-12-15.clover; managed_payments_preview=v1",
  typescript: true,
});

// Validate that required environment variables are set
if (!process.env.STRIPE_SECRET_KEY) {
  console.warn(
    "STRIPE_SECRET_KEY not configured - Stripe operations will fail"
  );
}
