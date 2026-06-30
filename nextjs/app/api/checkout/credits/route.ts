import { NextRequest, NextResponse } from "next/server";

import { stripe } from "@/lib/clients/stripe";
import { findLicenseByKey, updateLicenseKey } from "@/src/lib/db-layer";
import {
  validateCreditPurchaseAmount,
  computeCreditPurchase,
} from "./validation";

/**
 * Stripe Checkout API Route for Credit Purchases
 *
 * Creates a Stripe Checkout Session for HyperWhisper credit purchases. The
 * license key IS the wallet, so this is the only way to obtain a key: a guest
 * can buy credits with no key and the webhook mints + emails one. Topping up an
 * existing key is the other path.
 *
 * REQUEST BODY:
 * - amount: whole dollars, 5..500 (1000 credits per $1)
 * - licenseKey (optional): when present, top up that key instead of minting
 *
 * PRICING (two non-bundled line items):
 * - Credits: amount * 100 cents -> amount * 1000 credits
 * - Processing fee (6%): round(amount * 100 * 0.06) cents — revenue, never
 *   granted as credits, and non-refundable.
 *
 * STRIPE METADATA (read by the webhook):
 * - purchase_type: "credits"
 * - credit_amount: credits to grant
 * - fee_cents: the processing fee charged
 * - license_key: present ONLY when topping up an existing key (absent => mint)
 */

export async function POST(req: NextRequest) {
  try {
    const body = await req.json();
    const { licenseKey, amount } = body as {
      licenseKey?: unknown;
      amount?: unknown;
    };

    // Validate amount: whole dollars within [MIN, MAX].
    const amountError = validateCreditPurchaseAmount(amount);
    if (amountError !== null) {
      return NextResponse.json({ error: amountError }, { status: 400 });
    }

    const { creditAmount, creditCents, feeCents } = computeCreditPurchase(
      amount as number
    );

    const siteUrl =
      process.env.NEXT_PUBLIC_SITE_URL || "https://hyperwhisper.com";

    // Optional license key: when supplied, this is a top-up of an existing
    // wallet; when absent, the webhook mints a brand-new key on payment.
    const hasLicenseKey = typeof licenseKey === "string" && licenseKey.length > 0;

    const metadata: Record<string, string> = {
      purchase_type: "credits",
      credit_amount: creditAmount.toString(),
      fee_cents: feeCents.toString(),
    };

    // Resolve the Stripe customer for the top-up path so credits attach to the
    // same customer/email. The mint path lets Stripe collect the email itself.
    let stripeCustomerId: string | undefined;

    if (hasLicenseKey) {
      const license = await findLicenseByKey(licenseKey);

      if (!license) {
        console.error(
          "License lookup failed for key:",
          licenseKey.substring(0, 7)
        );
        return NextResponse.json(
          { error: "Invalid license key" },
          { status: 400 }
        );
      }

      // Reject non-granted (e.g. revoked) licenses: credits added to them would
      // be unusable because /api/license/credits refuses to validate or deduct
      // them.
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

      metadata.license_key = licenseKey;

      // Find or create the Stripe customer by email and cache it on the license.
      stripeCustomerId = license.stripeCustomerId ?? undefined;
      if (!stripeCustomerId) {
        const existingCustomers = await stripe.customers.list({
          email,
          limit: 1,
        });

        if (existingCustomers.data.length > 0) {
          stripeCustomerId = existingCustomers.data[0].id;
        } else {
          const newCustomer = await stripe.customers.create({
            email,
            metadata: { license_key: licenseKey },
          });
          stripeCustomerId = newCustomer.id;
        }

        await updateLicenseKey(license.id, { stripeCustomerId });
      }
    }

    // Create Stripe Checkout Session
    // @ts-expect-error - managed_payments is in private preview
    const session = await stripe.checkout.sessions.create({
      mode: "payment",
      // Top-up: attach to the resolved customer. Mint: let Stripe collect the
      // email by always creating a customer during checkout.
      ...(stripeCustomerId
        ? { customer: stripeCustomerId }
        : { customer_creation: "always" }),
      line_items: [
        {
          price_data: {
            currency: "usd",
            product_data: {
              name: "HyperWhisper Cloud credits",
              description: `${creditAmount.toLocaleString()} credits`,
            },
            unit_amount: creditCents,
          },
          quantity: 1,
        },
        {
          price_data: {
            currency: "usd",
            product_data: {
              name: "Processing fee (6%)",
              description: "Non-refundable payment processing fee",
            },
            unit_amount: feeCents,
          },
          quantity: 1,
        },
      ],

      // Allow coupon/promotion codes
      allow_promotion_codes: true,

      // Metadata for webhook processing
      metadata,

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
