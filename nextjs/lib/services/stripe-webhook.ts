import Stripe from "stripe";
import { stripe } from "@/lib/clients/stripe";
import { emailService } from "@/lib/services/email";
import { generateLicenseKey } from "@/lib/services/license-key";
import {
  findAccountByKey,
  getAccountKeysByEmail,
  findAccountByStripeSession,
  insertAccountKey,
  getOrCreateUser,
  updateAccountKey,
  grantCreditLot,
  grantCreditsForStripeEvent,
  refundCreditGrant,
  getCreditBalance,
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
  const existingLicense = await findAccountByStripeSession(session.id);

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
    const collision = await findAccountByKey(licenseKey);

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
    insertedLicense = await insertAccountKey({
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
        userId: insertedLicense.userId,
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
 * Two paths, decided by whether the checkout carried a license key:
 * - TOP-UP (`metadata.license_key` present): add credits to the existing key.
 * - MINT (`metadata.license_key` absent): a guest bought credits with no key,
 *   so we mint one, grant the credits, and email the new key. This is now the
 *   only way to obtain a key (the standalone license product is retired).
 *
 * CRITICAL: idempotent. Stripe may deliver the same event multiple times.
 * Mint dedupes on license_keys.stripe_session_id; the grant dedupes on
 * stripe_processed_events(stripe_object_id = session.id).
 */
export async function handleCreditPurchase(
  session: Stripe.Checkout.Session,
  eventId: string,
  eventType = "checkout.session.completed"
): Promise<void> {
  const licenseKey = session.metadata?.license_key;
  const creditAmount = parseInt(session.metadata?.credit_amount || "0", 10);

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

  if (licenseKey) {
    await handleCreditTopUp(session, eventId, eventType, licenseKey, creditAmount);
  } else {
    await handleCreditMint(session, eventId, eventType, creditAmount);
  }
}

/**
 * Top-up path: grant credits to an existing license key and email a receipt.
 */
async function handleCreditTopUp(
  session: Stripe.Checkout.Session,
  eventId: string,
  eventType: string,
  licenseKey: string,
  creditAmount: number
): Promise<void> {
  console.log(
    `Processing credit top-up: ${creditAmount} credits for license ${licenseKey.substring(0, 7)}...`
  );

  const license = await findAccountByKey(licenseKey);

  if (!license) {
    throw new Error(`License not found: ${licenseKey.substring(0, 7)}...`);
  }

  // Refuse to credit a non-granted (e.g. revoked) license: those credits would be
  // unusable via /api/license/credits. Throw so the webhook returns non-2xx and the
  // event surfaces for manual review/refund instead of silently crediting a dead row.
  if (license.status !== "granted") {
    throw new Error(
      `Cannot grant credits to ${license.status} license: ${licenseKey.substring(0, 7)}...`
    );
  }

  // Idempotently record the checkout session and atomically grant credits.
  const grantResult = await grantCreditsForStripeEvent({
    eventId,
    eventType,
    stripeObjectId: session.id,
    userId: license.userId,
    creditAmount,
    // Explicit (matches the mint path); the refund clawback resolves grants by
    // (sourceType, sourceId), so keep both paths writing the same provenance.
    sourceType: "stripe_credit_pack",
    sourceId: session.id,
  });

  if (grantResult === "duplicate") {
    console.log(
      `Credit top-up already processed for session ${session.id}, skipping`
    );
    return;
  }

  console.log(`Credits added: ${creditAmount} for session ${session.id}`);

  // Email a receipt with the amount added and the new balance.
  const newBalance = await getCreditBalance(license.userId);
  const customerEmail = license.email;
  const customerName =
    session.customer_details?.name || customerEmail?.split("@")[0] || "Customer";

  const emailResult = await emailService.sendCreditTopUp({
    customerName,
    customerEmail,
    licenseKey,
    creditAmount,
    newBalance,
    productName: "HyperWhisper",
    supportEmail: "support@hyperwhisper.com",
  });

  if (!emailResult.success) {
    console.error(`Failed to send top-up email: ${emailResult.error}`);
  }
}

/**
 * Mint path: a guest bought credits with no key. Generate a key, grant the
 * credits onto it, and email the key + starting balance.
 *
 * Idempotency: license insert is guarded by the unique stripe_session_id index
 * (we look it up first, and catch a concurrent insert's 23505); the credit
 * grant is independently idempotent on session.id, so it is safe to (re)run on
 * every delivery — retries never double-grant and always converge to a key with
 * the credits attached.
 */
async function handleCreditMint(
  session: Stripe.Checkout.Session,
  eventId: string,
  eventType: string,
  creditAmount: number
): Promise<void> {
  const customerEmail = session.customer_details?.email;
  const customerName =
    session.customer_details?.name || customerEmail?.split("@")[0] || "Customer";
  const stripeCustomerId = (session.customer as string) || null;

  if (!customerEmail) {
    throw new Error("No customer email in credit checkout session");
  }

  console.log(`Processing credit purchase by ${customerEmail}`);

  // STEP 1: Resolve the license for this session (existing on retry, else mint).
  let license = await findAccountByStripeSession(session.id);

  // Pool by email: a guest who buys with an email that ALREADY owns a granted
  // key should top that key up, not get a second key with a split balance.
  // Only when the session hasn't already resolved a license (retry-safe), and
  // only for granted keys (a revoked key is dead — mint a fresh one instead).
  let pooledIntoExisting = false;
  if (!license) {
    // An email can own several keys (e.g. a revoked key plus a live one), so
    // scan all of them newest-first and pool into the most recent GRANTED key.
    // (findFirst-by-email could hand back the dead revoked row and wrongly mint
    // a second key with a split balance.)
    const existingKeys = await getAccountKeysByEmail(
      customerEmail.toLowerCase().trim()
    );
    const existing = existingKeys.find((k) => k.status === "granted");
    if (existing) {
      license = existing;
      pooledIntoExisting = true;
    }
  }

  if (!license) {
    // Generate a unique license key with collision check.
    let licenseKey = "";
    const maxAttempts = 10;
    for (let attempt = 1; ; attempt++) {
      licenseKey = generateLicenseKey();
      const collision = await findAccountByKey(licenseKey);
      if (!collision) break;
      console.warn(`License key collision detected, retrying... (${attempt})`);
      if (attempt >= maxAttempts) {
        throw new Error("Failed to generate unique license key after max attempts");
      }
    }

    const user = await getOrCreateUser(customerEmail, {
      name: customerName,
      ...(stripeCustomerId ? { stripeCustomerId } : {}),
    });
    if (!user) {
      throw new Error(`Failed to create user for ${customerEmail}`);
    }

    try {
      license = await insertAccountKey({
        key: licenseKey,
        email: customerEmail.toLowerCase().trim(),
        userId: user.id,
        stripeCustomerId,
        stripeSessionId: session.id,
        status: "granted",
      });
    } catch (insertError: unknown) {
      // Concurrent webhook delivery inserted the row first (unique
      // stripe_session_id): fall back to the existing row.
      if (
        insertError &&
        typeof insertError === "object" &&
        "code" in insertError &&
        (insertError as { code: string }).code === "23505"
      ) {
        console.log("License already inserted by concurrent request");
        license = await findAccountByStripeSession(session.id);
      } else {
        console.error("Failed to store license key:", insertError);
        throw insertError;
      }
    }
  }

  if (!license) {
    throw new Error(`Failed to resolve minted license for session ${session.id}`);
  }

  // STEP 2: Grant the purchased credits (no included bundle on credit purchases).
  // Idempotent on session.id, so safe to run on every delivery.
  const grantResult = await grantCreditsForStripeEvent({
    eventId,
    eventType,
    stripeObjectId: session.id,
    userId: license.userId,
    creditAmount,
    sourceType: "stripe_credit_pack",
    sourceId: session.id,
  });

  // Retry/duplicate delivery: credits already granted for this session — don't
  // re-send the email. (Pooling resolves the same license by email each retry,
  // so this guard is what keeps a re-delivery from emailing twice.)
  if (grantResult === "duplicate") {
    console.log(
      `Credit purchase already processed for session ${session.id}, skipping email`
    );
    return;
  }

  // STEP 3: Email. When we pooled into an existing key, send a top-up receipt
  // with the new balance (the buyer already has this key). A genuinely new key
  // gets the mint email with its starting balance.
  if (pooledIntoExisting) {
    console.log(
      `Pooled ${creditAmount} credits into existing key ${license.key.substring(0, 7)}... for ${customerEmail}`
    );

    const newBalance = await getCreditBalance(license.userId);
    const emailResult = await emailService.sendCreditTopUp({
      customerName,
      customerEmail,
      licenseKey: license.key,
      creditAmount,
      newBalance,
      productName: "HyperWhisper",
      supportEmail: "support@hyperwhisper.com",
    });

    if (!emailResult.success) {
      console.error(`Failed to send top-up email: ${emailResult.error}`);
    }
    return;
  }

  console.log(
    `Minted license ${license.key.substring(0, 7)}... with ${creditAmount} credits for ${customerEmail}`
  );

  const emailResult = await emailService.sendCreditMint({
    customerName,
    customerEmail,
    licenseKey: license.key,
    creditAmount,
    productName: "HyperWhisper",
    supportEmail: "support@hyperwhisper.com",
  });

  if (!emailResult.success) {
    console.error(`Failed to send mint email: ${emailResult.error}`);
  }
}

/**
 * Process a charge refund.
 *
 * - "license" purchases: revoke the associated license.
 * - "credits" purchases: deduct the granted credits from the balance.
 *
 * REFUND SCOPE (per purchase type):
 * - license: acts only on a FULL refund (amount_refunded === amount).
 * - credits: the 6% processing fee is a separate, non-refundable line item, so a
 *   policy-compliant credit refund refunds only the credit value — a *partial*
 *   Stripe refund. Acts once amount_refunded covers the credit portion
 *   (charge total minus the non-refundable fee).
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
  console.log(
    `Processing refund for charge ${charge.id} (${charge.amount_refunded}/${charge.amount})`
  );

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

  // STEP 4: Route by purchase type, applying the right "is this refund
  // actionable?" rule for each.
  if (purchaseType === "credits") {
    // The 6% fee is a separate, non-refundable line item, so a credit refund
    // refunds only the credit value — a partial Stripe refund. Act once the
    // refunded amount covers the credit portion (charge total minus the fee).
    const feeCents = parseInt(checkoutSession.metadata?.fee_cents || "0", 10);
    const creditPortion = charge.amount - feeCents;
    if (charge.amount_refunded < creditPortion) {
      console.log(
        `Refund ${charge.amount_refunded}/${charge.amount} does not cover the credit portion (${creditPortion}) for credits session ${checkoutSession.id}, skipping`
      );
      return;
    }
    await handleCreditRefund(charge, checkoutSession, eventId);
    return;
  }

  if (purchaseType !== "license") {
    console.log(
      `Refund for unknown purchase type (${purchaseType}), skipping`
    );
    return;
  }

  // License purchases: act only on a FULL refund.
  if (charge.amount_refunded !== charge.amount) {
    console.log(
      `Partial refund (${charge.amount_refunded}/${charge.amount}) for license session ${checkoutSession.id}, skipping`
    );
    return;
  }

  console.log(`Revoking license for checkout session ${checkoutSession.id}`);

  // STEP 5: Revoke the license in database
  const license = await findAccountByStripeSession(checkoutSession.id);

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
  await updateAccountKey(license.id, { status: "revoked" });

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
  // license_key is only present on top-ups; minted credit purchases carry none.
  // The clawback is keyed on the checkout session id (the grant's source_id),
  // so it works for both paths — license_key is informational only here.
  const licenseKey = checkoutSession.metadata?.license_key;
  const creditAmount = parseInt(
    checkoutSession.metadata?.credit_amount || "0",
    10
  );

  if (!creditAmount || creditAmount <= 0) {
    console.error(
      `Credit refund for session ${checkoutSession.id}: invalid metadata (credit_amount=${checkoutSession.metadata?.credit_amount}), skipping`
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

  const licenseHint = licenseKey ? ` (license ${licenseKey.substring(0, 7)}...)` : "";
  console.log(
    `Deducted ${result.refundedAmount} credits for refunded charge ${charge.id}${licenseHint}`
  );
}
