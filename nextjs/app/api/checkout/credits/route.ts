import { NextRequest, NextResponse } from "next/server";

import { stripe } from "@/lib/clients/stripe";
import { findLicenseByKey, updateLicenseKey } from "@/src/lib/db-layer";

/**
 * Stripe Checkout API Route for Credit Purchases
 *
 * Creates a Stripe Checkout Session for HyperWhisper credit purchases.
 * Requires a valid license key to purchase credits.
 *
 * FLOW:
 * 1. Validate license key exists in database
 * 2. Get customer email from license key record
 * 3. Find or create Stripe customer
 * 4. Create Stripe Checkout Session with:
 *    - Default price fetched from Stripe product
 *    - Customer linked to license for meter tracking
 *    - Metadata with license key and credit amount
 * 5. Return checkout URL for client redirect
 *
 * STRIPE METADATA:
 * - purchase_type: "credits" (for webhook to identify)
 * - license_key: The license key for this purchase
 * - credit_amount: Amount of credits to add
 */

const CREDIT_TIERS = {
  5: { credits: 5000, envKey: "STRIPE_CREDITS_PRODUCT_5" },
  10: { credits: 10000, envKey: "STRIPE_CREDITS_PRODUCT_10" },
  20: { credits: 20000, envKey: "STRIPE_CREDITS_PRODUCT_20" },
} as const;

type TierAmount = keyof typeof CREDIT_TIERS;

export async function POST(req: NextRequest) {
  try {
    const body = await req.json();
    const { licenseKey, amount } = body;

    // Validate license key is provided
    if (!licenseKey || typeof licenseKey !== "string") {
      return NextResponse.json(
        { error: "License key is required" },
        { status: 400 }
      );
    }

    // Validate and default tier amount
    const tierAmount: TierAmount =
      amount === 5 || amount === 10 || amount === 20 ? amount : 5;
    const tier = CREDIT_TIERS[tierAmount];

    // Look up license in database
    const license = await findLicenseByKey(licenseKey);

    if (!license) {
      console.error("License lookup failed for key:", licenseKey.substring(0, 7));
      return NextResponse.json(
        { error: "Invalid license key" },
        { status: 400 }
      );
    }

    // Reject non-granted (e.g. revoked) licenses: credits added to them would be
    // unusable because /api/license/credits refuses to validate or deduct them.
    if (license.status !== "granted") {
      return NextResponse.json(
        { error: `License is ${license.status}` },
        { status: 400 }
      );
    }

    const email = license.email;
    if (!email) {
      return NextResponse.json(
        { error: "License has no email associated" },
        { status: 400 }
      );
    }

    const productId = process.env[tier.envKey];
    const siteUrl =
      process.env.NEXT_PUBLIC_SITE_URL || "https://hyperwhisper.com";

    if (!productId) {
      console.error(`${tier.envKey} not configured`);
      return NextResponse.json(
        { error: "Credits checkout not configured" },
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

    // Find or create Stripe customer by email
    let stripeCustomerId = license.stripeCustomerId;

    if (!stripeCustomerId) {
      // Check if customer already exists in Stripe
      const existingCustomers = await stripe.customers.list({
        email: email,
        limit: 1,
      });

      if (existingCustomers.data.length > 0) {
        stripeCustomerId = existingCustomers.data[0].id;
      } else {
        // Create new Stripe customer
        const newCustomer = await stripe.customers.create({
          email: email,
          metadata: {
            license_key: licenseKey,
          },
        });
        stripeCustomerId = newCustomer.id;
      }

      // Update license with Stripe customer ID
      await updateLicenseKey(license.id, { stripeCustomerId });
    }

    // Create Stripe Checkout Session
    // @ts-expect-error - managed_payments is in private preview
    const session = await stripe.checkout.sessions.create({
      mode: "payment",
      customer: stripeCustomerId,
      line_items: [
        {
          price: priceId,
          quantity: 1,
        },
      ],

      // Allow coupon/promotion codes
      allow_promotion_codes: true,

      // Metadata for webhook processing
      metadata: {
        purchase_type: "credits",
        license_key: licenseKey,
        credit_amount: tier.credits.toString(),
      },

      // Managed Payments: Stripe handles tax, invoicing, and compliance
      managed_payments: { enabled: true },

      // Redirect to user dashboard after checkout
      success_url: `${siteUrl}/user/dashboard?purchase=success`,
      cancel_url: `${siteUrl}/user/dashboard`,
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
    console.error("Credit checkout error:", error);

    return NextResponse.json(
      {
        error: "Failed to create checkout session",
        details: error instanceof Error ? error.message : "Unknown error",
      },
      { status: 500 }
    );
  }
}
