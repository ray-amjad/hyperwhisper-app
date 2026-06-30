/**
 * Checkout Router
 *
 * Creates Stripe checkout sessions for license purchases.
 * All procedures are public (anyone can initiate a checkout).
 *
 * PROCEDURES:
 * - licenseKey: Creates checkout for new license purchase
 *
 * STRIPE INTEGRATION:
 * Uses Stripe Checkout API to create secure payment sessions.
 * Returns checkout URL for client-side redirect.
 *
 * MIGRATION NOTE:
 * Previously used Polar for checkout. Now uses Stripe.
 * License key generation moved to webhook handler.
 */
import { TRPCError } from "@trpc/server";

import { createTRPCRouter, publicProcedure } from "../trpc";
import { stripe } from "@/lib/clients/stripe";

export const checkoutRouter = createTRPCRouter({
  /**
   * Create a Stripe checkout session for license key purchase.
   *
   * FLOW:
   * 1. Gets price ID from environment
   * 2. Creates Stripe Checkout Session with:
   *    - Customer creation enabled (email required)
   *    - Promotion codes allowed
   *    - Metadata for webhook identification
   * 3. Returns checkout URL for redirect
   *
   * @returns { checkoutUrl: string }
   */
  licenseKey: publicProcedure.query(async () => {
    try {
      const productId = process.env.STRIPE_LICENSE_PRODUCT_ID;
      const siteUrl =
        process.env.NEXT_PUBLIC_SITE_URL || "https://hyperwhisper.com";

      if (!productId) {
        console.error("STRIPE_LICENSE_PRODUCT_ID not configured");
        throw new TRPCError({
          code: "INTERNAL_SERVER_ERROR",
          message: "Checkout system not configured",
        });
      }

      // Fetch the product to get its default price
      const product = await stripe.products.retrieve(productId);

      if (!product.default_price) {
        console.error("Product has no default price:", productId);
        throw new TRPCError({
          code: "INTERNAL_SERVER_ERROR",
          message: "Product has no default price configured",
        });
      }

      const priceId =
        typeof product.default_price === "string"
          ? product.default_price
          : product.default_price.id;

      // Create Stripe Checkout Session
      const session = await stripe.checkout.sessions.create({
        mode: "payment",
        line_items: [
          {
            price: priceId,
            quantity: 1,
          },
        ],

        // Always create a Stripe customer during checkout (even for guests)
        // This ensures they can access the customer portal later
        // Always create a Stripe customer during checkout (even for guests)
        // This ensures they can access the customer portal later
        customer_creation: "always",

        // Enable promotion codes for discounts
        allow_promotion_codes: true,

        // Metadata for webhook processing
        metadata: {
          purchase_type: "license",
        },

        // Tax compliance: automatically calculate VAT/GST/Sales Tax
        automatic_tax: { enabled: true },
        billing_address_collection: "auto",

        // Automatically create and email invoice to customer
        invoice_creation: { enabled: true },

        // Success URL includes session_id for confirmation page
        success_url: `${siteUrl}/purchase-success?session_id={CHECKOUT_SESSION_ID}`,
        cancel_url: `${siteUrl}/pricing`,
      });

      // Return checkout URL to client
      if (!session.url) {
        throw new TRPCError({
          code: "INTERNAL_SERVER_ERROR",
          message: "No checkout URL returned from Stripe",
        });
      }

      return {
        checkoutUrl: session.url,
      };
    } catch (error) {
      console.error("Stripe checkout error:", error);

      if (error instanceof TRPCError) {
        throw error;
      }

      throw new TRPCError({
        code: "INTERNAL_SERVER_ERROR",
        message:
          error instanceof Error
            ? error.message
            : "Failed to create checkout session",
      });
    }
  }),
});
