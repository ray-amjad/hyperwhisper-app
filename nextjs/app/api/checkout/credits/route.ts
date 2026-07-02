import { NextRequest, NextResponse } from "next/server";

import { stripe } from "@/lib/clients/stripe";
import { findAccountByKey, updateAccountKey } from "@/src/lib/db-layer";
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
 * - email (optional): mint path only. Prefills the Stripe checkout email so the
 *   minted key is emailed to that address; ignored when topping up a key.
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
    const { licenseKey, amount, email } = body as {
      licenseKey?: unknown;
      amount?: unknown;
      email?: unknown;
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
    // Trim first: a whitespace-only key must NOT count as "present" (which would
    // skip the mint path and then fail the key lookup). Normalize once and use the
    // trimmed value everywhere below.
    const normalizedLicenseKey =
      typeof licenseKey === "string" && licenseKey.trim().length > 0
        ? licenseKey.trim()
        : undefined;
    const hasLicenseKey = normalizedLicenseKey !== undefined;

    // Mint path only: a guest-supplied email to prefill at checkout. Loose
    // format check — Stripe re-validates and the buyer can still edit it. The
    // webhook mints/emails the key off the email Stripe ultimately collects.
    const guestEmail =
      !hasLicenseKey &&
      typeof email === "string" &&
      /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email.trim())
        ? email.trim().toLowerCase()
        : undefined;

    const metadata: Record<string, string> = {
      purchase_type: "credits",
      credit_amount: creditAmount.toString(),
      fee_cents: feeCents.toString(),
    };

    // Resolve the Stripe customer for the top-up path so credits attach to the
    // same customer/email. The mint path lets Stripe collect the email itself.
    let stripeCustomerId: string | undefined;

    if (normalizedLicenseKey) {
      const license = await findAccountByKey(normalizedLicenseKey);

      if (!license) {
        console.error(
          "License lookup failed for key:",
          normalizedLicenseKey.substring(0, 7)
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

      metadata.license_key = normalizedLicenseKey;

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
            metadata: { license_key: normalizedLicenseKey },
          });
          stripeCustomerId = newCustomer.id;
        }

        await updateAccountKey(license.id, { stripeCustomerId });
      }
    }

    // Create Stripe Checkout Session
    // @ts-expect-error - managed_payments is in private preview
    const session = await stripe.checkout.sessions.create({
      mode: "payment",
      // Top-up: attach to the resolved customer. Mint: always create a customer
      // during checkout, prefilling the guest email when one was supplied.
      ...(stripeCustomerId
        ? { customer: stripeCustomerId }
        : {
            customer_creation: "always",
            ...(guestEmail ? { customer_email: guestEmail } : {}),
          }),
      line_items: [
        {
          price_data: {
            currency: "usd",
            product_data: {
              name: "HyperWhisper Cloud credits",
              description: `${creditAmount.toLocaleString()} credits`,
              // Managed Payments requires every line item's product to carry a
              // tax_code. txcd_10000000 = "General - Electronically Supplied
              // Services", which fits prepaid credits for a cloud STT service.
              tax_code: "txcd_10000000",
            },
            unit_amount: creditCents,
          },
          quantity: 1,
        },
        {
          price_data: {
            currency: "usd",
            product_data: {
              name: "Cloud processing fee (6%)",
              description: "Non-refundable HyperWhisper Cloud processing fees",
              // Same Managed Payments requirement: the fee line needs a tax_code.
              tax_code: "txcd_10000000",
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

      // Land on the public confirmation page (works for guests, who have no
      // account yet). It only personalizes copy from `credits` — entitlement is
      // always enforced server-side via the webhook-minted key.
      success_url: `${siteUrl}/purchase-success?session_id={CHECKOUT_SESSION_ID}&credits=${creditAmount}`,
      cancel_url: `${siteUrl}/credits`,
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
