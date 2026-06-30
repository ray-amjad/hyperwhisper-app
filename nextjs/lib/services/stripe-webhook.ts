import Stripe from "stripe";
import { stripe } from "@/lib/clients/stripe";
import { emailService } from "@/lib/services/email";
import { generateLicenseKey } from "@/lib/services/license-key";
import {
  findLicenseByKey,
  findLicenseByStripeSession,
  insertLicenseKey,
  getOrCreateUser,
  updateLicenseKey,
  grantCreditLot,
  grantCreditsForStripeEvent,
  refundCreditGrant,
  hasProcessedStripeObject,
} from "@/src/lib/db-layer";

/**
 * Stripe Webhook Handlers
 *
 * Service module for processing Stripe webhook events.
 * Handles license purchases and credit purchases.
 */

/**
 * Process a completed license purchase.
 *
 * CRITICAL: This function must be idempotent.
 * Stripe may send the same event multiple times.
 * We use upsert with stripe_session_id as the idempotency key.
 */
export async function handleLicensePurchase(
  session: Stripe.Checkout.Session
): Promise<void> {
  const customerEmail = session.customer_details?.email;
  const customerName =
    session.customer_details?.name ||
    customerEmail?.split("@")[0] ||
    "Customer";
  const stripeCustomerId = session.customer as string;

  if (!customerEmail) {
    throw new Error("No customer email in checkout session");
  }

  console.log(`Processing license purchase for ${customerEmail}`);

  // STEP 1: Check if we already processed this session (idempotency)
  const existingLicense = await findLicenseByStripeSession(session.id);

  if (existingLicense) {
    console.log(
      `License already exists for session ${session.id}, resending email...`
    );
    // Still send email in case it failed before
    await sendLicenseEmail(customerName, customerEmail, existingLicense.key);
    return;
  }

  // STEP 2: Generate unique license key with collision check
  let licenseKey: string;
  let attempts = 0;
  const maxAttempts = 10;

  do {
    licenseKey = generateLicenseKey();
    attempts++;

    // Check uniqueness in database
    const collision = await findLicenseByKey(licenseKey);

    if (!collision) break;

    console.warn(`License key collision detected, retrying... (${attempts})`);

    if (attempts >= maxAttempts) {
      throw new Error("Failed to generate unique license key after max attempts");
    }
  } while (true);

  console.log(`Generated license key: ${licenseKey.substring(0, 7)}...`);

  // STEP 3: Get or create user for the customer
  const user = await getOrCreateUser(customerEmail, {
    name: customerName,
    stripeCustomerId,
  });

  if (!user) {
    throw new Error(`Failed to create user for ${customerEmail}`);
  }

  console.log(`User ready for ${customerEmail}: ${user.id}`);

  // STEP 4: Store license in database
  let insertedLicense;
  try {
    insertedLicense = await insertLicenseKey({
      key: licenseKey,
      email: customerEmail.toLowerCase().trim(),
      userId: user.id,
      stripeCustomerId,
      stripeSessionId: session.id,
      status: "granted",
    });
  } catch (insertError: unknown) {
    // Check if it's a duplicate (race condition with webhook retry)
    if (
      insertError &&
      typeof insertError === "object" &&
      "code" in insertError &&
      (insertError as { code: string }).code === "23505"
    ) {
      console.log("License already inserted by concurrent request");
      return;
    }
    console.error("Failed to store license key:", insertError);
    throw insertError;
  }

  console.log(`License key stored in database for ${customerEmail}`);

  // STEP 4b: Grant initial credits
  if (insertedLicense) {
    try {
      await grantCreditLot({
        licenseKeyId: insertedLicense.id,
        amount: 5000,
        sourceType: "license_bundle",
        sourceId: session.id,
      });
      console.log(`Granted 5000 initial credits for license ${licenseKey.substring(0, 7)}...`);
    } catch (creditError) {
      console.error("Failed to create initial credit balance:", creditError);
      // Don't throw - license was created, credits can be added later
    }
  }

  // STEP 5: Send license email
  await sendLicenseEmail(customerName, customerEmail, licenseKey);
}

/**
 * Send the license key email to the customer.
 *
 * Uses the existing email service with retry logic.
 * Does not throw - logs errors but allows the webhook to succeed.
 */
async function sendLicenseEmail(
  customerName: string,
  customerEmail: string,
  licenseKey: string
): Promise<void> {
  const emailResult = await emailService.sendLicenseKey({
    customerName,
    customerEmail,
    licenseKey,
    productName: "HyperWhisper",
    downloadUrl: "https://www.hyperwhisper.com",
    supportEmail: "support@hyperwhisper.com",
  });

  if (!emailResult.success) {
    // Log but don't throw - license is created, email can be resent manually
    console.error(`Failed to send license email: ${emailResult.error}`);
  } else {
    console.log(`License email sent to ${customerEmail}`);
  }
}

/**
 * Process a completed credit purchase.
 *
 * FLOW:
 * 1. Look up license key to get license_key_id
 * 2. Fetch current balance from credit_balances (or 0 if no row)
 * 3. Upsert new balance
 */
export async function handleCreditPurchase(
  session: Stripe.Checkout.Session,
  eventId: string,
  eventType = "checkout.session.completed"
): Promise<void> {
  const stripeCustomerId = session.customer as string;
  const licenseKey = session.metadata?.license_key;
  const creditAmount = parseInt(session.metadata?.credit_amount || "0", 10);

  if (!stripeCustomerId) {
    throw new Error("No customer ID in checkout session");
  }

  if (!licenseKey) {
    throw new Error("No license key in checkout session metadata");
  }

  if (!creditAmount || creditAmount <= 0) {
    throw new Error(
      `Invalid credit amount in metadata: ${session.metadata?.credit_amount}`
    );
  }

  if (session.payment_status !== "paid") {
    console.log(
      `Skipping credit purchase for session ${session.id}: payment_status=${session.payment_status}`
    );
    return;
  }

  console.log(
    `Processing credit purchase: ${creditAmount} credits for license ${licenseKey.substring(0, 7)}...`
  );

  // STEP 1: Get license
  const license = await findLicenseByKey(licenseKey);

  if (!license) {
    throw new Error(`License not found: ${licenseKey.substring(0, 7)}...`);
  }

  if (await hasProcessedStripeObject(session.id)) {
    console.log(
      `Credit purchase already processed for session ${session.id}, skipping`
    );
    return;
  }

  // Refuse to credit a non-granted (e.g. revoked) license: those credits would be
  // unusable via /api/license/credits. Throw so the webhook returns non-2xx and the
  // event surfaces for manual review/refund instead of silently crediting a dead row.
  if (license.status !== "granted") {
    throw new Error(
      `Cannot grant credits to ${license.status} license: ${licenseKey.substring(0, 7)}...`
    );
  }

  // STEP 2: Idempotently record the checkout session and atomically grant credits.
  const grantResult = await grantCreditsForStripeEvent({
    eventId,
    eventType,
    stripeObjectId: session.id,
    licenseKeyId: license.id,
    creditAmount,
  });

  if (grantResult === "duplicate") {
    console.log(
      `Credit purchase already processed for session ${session.id}, skipping`
    );
    return;
  }

  console.log(`Credits added: ${creditAmount} for session ${session.id}`);
}

/**
 * Process a charge refund.
 *
 * - "license" purchases: revoke the associated license.
 * - "credits" purchases: deduct the granted credits from the balance.
 *
 * FULL REFUNDS ONLY:
 * Only acts when amount_refunded === amount (full refund).
 * Partial refunds are ignored - the license/credits remain valid.
 *
 * TRACE PATH:
 * Charge -> PaymentIntent -> Checkout Session -> license_keys.stripe_session_id
 *
 * IDEMPOTENCY:
 * License revocation is idempotent ("revoked" status). Credit deduction is
 * recorded in stripe_processed_events keyed by charge.id, so retried
 * charge.refunded events never double-deduct.
 */
export async function handleChargeRefunded(
  charge: Stripe.Charge,
  eventId: string
): Promise<void> {
  // STEP 1: Check if this is a FULL refund
  if (charge.amount_refunded !== charge.amount) {
    console.log(
      `Partial refund detected (${charge.amount_refunded}/${charge.amount}), skipping refund handling`
    );
    return;
  }

  console.log(`Processing full refund for charge ${charge.id}`);

  // STEP 2: Get PaymentIntent ID from the charge
  const paymentIntentId =
    typeof charge.payment_intent === "string"
      ? charge.payment_intent
      : charge.payment_intent?.id;

  if (!paymentIntentId) {
    console.log(
      "No payment_intent on charge, skipping (likely not a Checkout purchase)"
    );
    return;
  }

  // STEP 3: Find the Checkout Session associated with this PaymentIntent
  const sessions = await stripe.checkout.sessions.list({
    payment_intent: paymentIntentId,
    limit: 1,
  });

  if (sessions.data.length === 0) {
    console.log(
      `No checkout session found for payment_intent ${paymentIntentId}`
    );
    return;
  }

  const checkoutSession = sessions.data[0];
  const purchaseType = checkoutSession.metadata?.purchase_type;

  // STEP 4: Route by purchase type
  if (purchaseType === "credits") {
    await handleCreditRefund(charge, checkoutSession, eventId);
    return;
  }

  if (purchaseType !== "license") {
    console.log(
      `Refund for unknown purchase type (${purchaseType}), skipping`
    );
    return;
  }

  console.log(`Revoking license for checkout session ${checkoutSession.id}`);

  // STEP 5: Revoke the license in database
  const license = await findLicenseByStripeSession(checkoutSession.id);

  if (!license) {
    console.error(`License not found for session ${checkoutSession.id}`);
    return;
  }

  await refundCreditGrant({
    sourceType: "license_bundle",
    sourceId: checkoutSession.id,
  });

  // Already revoked - idempotent, just log and return
  if (license.status === "revoked") {
    console.log(`License ${license.key.substring(0, 7)}... already revoked`);
    return;
  }

  // STEP 6: Update status to revoked
  await updateLicenseKey(license.id, { status: "revoked" });

  console.log(
    `License ${license.key.substring(0, 7)}... revoked due to full refund`
  );
}

/**
 * Reverse a refunded credit-pack purchase.
 *
 * Looks up the original credit grant from the checkout session metadata and
 * atomically deducts it (clamped at 0). Idempotent via stripe_processed_events
 * keyed by charge.id, so webhook retries never double-deduct.
 */
async function handleCreditRefund(
  charge: Stripe.Charge,
  checkoutSession: Stripe.Checkout.Session,
  eventId: string
): Promise<void> {
  const licenseKey = checkoutSession.metadata?.license_key;
  const creditAmount = parseInt(
    checkoutSession.metadata?.credit_amount || "0",
    10
  );

  if (!licenseKey || !creditAmount || creditAmount <= 0) {
    console.error(
      `Credit refund for session ${checkoutSession.id}: invalid metadata (license_key=${licenseKey}, credit_amount=${checkoutSession.metadata?.credit_amount}), skipping`
    );
    return;
  }

  const license = await findLicenseByKey(licenseKey);

  if (!license) {
    console.error(
      `Credit refund: license not found: ${licenseKey.substring(0, 7)}...`
    );
    return;
  }

  const result = await refundCreditGrant({
    sourceType: "stripe_credit_pack",
    sourceId: checkoutSession.id,
  });

  if (result.status === "duplicate") {
    console.log(
      `Credit refund already processed for charge ${charge.id}, skipping`
    );
    return;
  }

  console.log(
    `Deducted ${result.refundedAmount} credits for refunded charge ${charge.id} (license ${licenseKey.substring(0, 7)}...)`
  );
}
