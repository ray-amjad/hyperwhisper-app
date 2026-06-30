import { NextRequest, NextResponse } from "next/server";
import Stripe from "stripe";
import { stripe } from "@/lib/clients/stripe";
import {
  handleLicensePurchase,
  handleCreditPurchase,
  handleChargeRefunded,
} from "@/lib/services/stripe-webhook";

/**
 * Stripe Webhook Handler
 *
 * Handles Stripe webhook events for HyperWhisper purchases.
 * Primary event: checkout.session.completed
 *
 * SUPPORTED PURCHASE TYPES:
 * - "license": One-time license purchase
 * - "credits": Credit pack purchase (adds to Stripe Billing Meter)
 *
 * SECURITY:
 * - Verifies webhook signature using STRIPE_WEBHOOK_SECRET
 * - Uses raw body for signature verification (required by Stripe)
 */
export async function POST(req: NextRequest) {
  const body = await req.text();
  const signature = req.headers.get("stripe-signature");

  if (!signature) {
    console.error("Stripe webhook: No signature header");
    return NextResponse.json({ error: "No signature" }, { status: 400 });
  }

  const webhookSecret = process.env.STRIPE_WEBHOOK_SECRET;
  if (!webhookSecret) {
    console.error("Stripe webhook: STRIPE_WEBHOOK_SECRET not configured");
    return NextResponse.json(
      { error: "Webhook secret not configured" },
      { status: 500 }
    );
  }

  let event: Stripe.Event;

  // Verify webhook signature
  try {
    event = stripe.webhooks.constructEvent(body, signature, webhookSecret);
  } catch (err) {
    console.error(
      "Stripe webhook signature verification failed:",
      err instanceof Error ? err.message : err
    );
    return NextResponse.json(
      { error: "Webhook signature verification failed" },
      { status: 400 }
    );
  }

  console.log(`Stripe webhook received: ${event.type}`);

  // Handle checkout session lifecycle events
  if (
    event.type === "checkout.session.completed" ||
    event.type === "checkout.session.async_payment_succeeded"
  ) {
    const session = event.data.object as Stripe.Checkout.Session;
    const purchaseType = session.metadata?.purchase_type;

    if (session.payment_status !== "paid") {
      console.log(
        `Stripe webhook: ${event.type} for ${purchaseType} session ${session.id} is ${session.payment_status}, waiting for payment success`,
      );
      return NextResponse.json({ received: true });
    }

    // Route to appropriate handler based on purchase type
    if (purchaseType === "license") {
      try {
        await handleLicensePurchase(session);
      } catch (error) {
        console.error(
          "Stripe webhook: Error processing license purchase:",
          error
        );
        return NextResponse.json(
          { error: "Failed to process license purchase" },
          { status: 500 }
        );
      }
    } else if (purchaseType === "credits") {
      try {
        await handleCreditPurchase(session, event.id, event.type);
      } catch (error) {
        console.error(
          "Stripe webhook: Error processing credit purchase:",
          error
        );
        return NextResponse.json(
          { error: "Failed to process credit purchase" },
          { status: 500 }
        );
      }
    } else {
      console.log(
        `Stripe webhook: Unknown purchase type (${purchaseType}), skipping`
      );
    }
  }

  if (event.type === "checkout.session.async_payment_failed") {
    const session = event.data.object as Stripe.Checkout.Session;
    const purchaseType = session.metadata?.purchase_type;

    if (purchaseType === "credits") {
      console.log(
        `Stripe webhook: async credit payment failed for session ${session.id}`,
      );
    }
  }

  // Handle charge.refunded for license revocation / credit reversal
  if (event.type === "charge.refunded") {
    const charge = event.data.object as Stripe.Charge;
    try {
      await handleChargeRefunded(charge, event.id);
    } catch (error) {
      console.error("Stripe webhook: Error processing refund:", error);
      // Don't return error status - log for manual review instead
      // This prevents infinite retries for non-transient failures
    }
  }

  return NextResponse.json({ received: true });
}
