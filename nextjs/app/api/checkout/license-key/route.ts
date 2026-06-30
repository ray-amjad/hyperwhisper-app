import { NextRequest, NextResponse } from "next/server";

import { stripe } from "@/lib/clients/stripe";

/**
 * Stripe Checkout API Route for License Purchases
 *
 * Creates a Stripe Checkout Session for HyperWhisper license purchases.
 *
 * FLOW:
 * 1. Create Stripe Checkout Session with:
 *    - License price from STRIPE_LICENSE_PRICE_ID env var
 *    - Customer creation enabled (email required)
 *    - Promotion codes allowed
 *    - Success/cancel URLs
 * 2. Return checkout URL for client redirect
 *
 * STRIPE METADATA:
 * - purchase_type: "license" (for webhook to identify)
 */
export async function GET(req: NextRequest) {
  try {
    const productId = process.env.STRIPE_LICENSE_PRODUCT_ID;
    const siteUrl =
      process.env.NEXT_PUBLIC_SITE_URL || "https://hyperwhisper.com";

    if (!productId) {
      console.error("STRIPE_LICENSE_PRODUCT_ID not configured");

      return NextResponse.json(
        { error: "Checkout system not configured" },
        { status: 500 }
      );
    }

    // Fetch the product to get its default price
    const product = await stripe.products.retrieve(productId);

    if (!product.default_price) {
      console.error("Product has no default price:", productId);
      return NextResponse.json(
        { error: "Product has no default price configured" },
        { status: 500 }
      );
    }

    const priceId =
      typeof product.default_price === "string"
        ? product.default_price
        : product.default_price.id;

    // Create Stripe Checkout Session
    // @ts-expect-error - managed_payments is in private preview
    const session = await stripe.checkout.sessions.create({
      mode: "payment",
      line_items: [
        {
          price: priceId,
          quantity: 1,
        },
      ],

      // CRITICAL: Create customer to capture email for license delivery
      customer_creation: "always",

      // Enable promotion codes for discounts
      allow_promotion_codes: true,

      // Metadata for webhook processing
      metadata: {
        purchase_type: "license",
      },

      // Managed Payments: Stripe handles tax, invoicing, and compliance
      managed_payments: { enabled: true },

      // Success URL includes session_id for confirmation page
      success_url: `${siteUrl}/purchase-success?session_id={CHECKOUT_SESSION_ID}`,
      cancel_url: `${siteUrl}/pricing`,
    });

    // Return checkout URL to client
    if (session.url) {
      return NextResponse.json({
        checkoutUrl: session.url,
      });
    } else {
      throw new Error("No checkout URL returned from Stripe");
    }
  } catch (error) {
    console.error("Stripe checkout error:", error);

    return NextResponse.json(
      {
        error:
          error instanceof Error
            ? error.message
            : "Failed to create checkout session",
      },
      { status: 500 }
    );
  }
}
