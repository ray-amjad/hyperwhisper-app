/**
 * Behaviour tests for `lib/services/stripe-webhook.ts` — the module that turns
 * a Stripe event into a license key, a credit grant or a revocation. It was at
 * 0% line coverage, and it is the only place in the website where money
 * becomes entitlement.
 *
 * Every collaborator is replaced at the module boundary (see
 * `stripe-webhook-harness.ts`). The module under test is the real one.
 */
import assert from "node:assert/strict";
import test, { after, before, beforeEach } from "node:test";

import type Stripe from "stripe";

import {
  accountKeyRow,
  behaviour,
  calls,
  loadWebhook,
  logLines,
  resetHarness,
  restoreWebhookLogging,
  silenceWebhookLogging,
} from "./stripe-webhook-harness";

type Webhook = Awaited<ReturnType<typeof loadWebhook>>;

let webhook: Webhook;
const handleLicensePurchase: Webhook["handleLicensePurchase"] = (...args) =>
  webhook.handleLicensePurchase(...args);
const handleCreditPurchase: Webhook["handleCreditPurchase"] = (...args) =>
  webhook.handleCreditPurchase(...args);
const handleChargeRefunded: Webhook["handleChargeRefunded"] = (...args) =>
  webhook.handleChargeRefunded(...args);

function checkoutSession(
  overrides: Record<string, unknown> = {},
): Stripe.Checkout.Session {
  return {
    id: "cs_1",
    customer: "cus_1",
    customer_details: { email: "Buyer@Example.com ", name: "Buyer" },
    payment_status: "paid",
    metadata: {},
    ...overrides,
  } as unknown as Stripe.Checkout.Session;
}

function charge(overrides: Record<string, unknown> = {}): Stripe.Charge {
  return {
    id: "ch_1",
    amount: 1_000,
    amount_refunded: 1_000,
    payment_intent: "pi_1",
    ...overrides,
  } as unknown as Stripe.Charge;
}

before(async () => {
  silenceWebhookLogging();
  webhook = await loadWebhook();
});
after(restoreWebhookLogging);
beforeEach(resetHarness);

// ---------------------------------------------------------------------------
// handleLicensePurchase
// ---------------------------------------------------------------------------

test("a license purchase with no customer email is rejected before any write", async () => {
  await assert.rejects(
    handleLicensePurchase(checkoutSession({ customer_details: { email: null, name: "Buyer" } })),
    { message: "No customer email in checkout session" },
  );

  assert.deepEqual(calls.insertAccountKey, []);
  assert.deepEqual(calls.grantCreditLot, []);
  assert.deepEqual(calls.emails, []);
});

test("a license purchase with no Stripe customer is rejected before any write", async () => {
  await assert.rejects(handleLicensePurchase(checkoutSession({ customer: null })), {
    message: "No Stripe customer in checkout session",
  });

  assert.deepEqual(calls.insertAccountKey, []);
  assert.deepEqual(calls.grantCreditLot, []);
});

test("a license purchase stores a normalised row and grants exactly 5000 bundled credits", async () => {
  behaviour.generatedKeys = ["HW-NEW-KEY-0001"];

  await handleLicensePurchase(checkoutSession());

  assert.deepEqual(calls.insertAccountKey, [
    {
      key: "HW-NEW-KEY-0001",
      // The session carried "Buyer@Example.com " — stored lowercased and trimmed.
      email: "buyer@example.com",
      userId: "user_1",
      stripeCustomerId: "cus_1",
      stripeSessionId: "cs_1",
      status: "granted",
    },
  ]);
  assert.deepEqual(calls.grantCreditLot, [
    {
      userId: "user_1",
      amount: 5_000,
      sourceType: "license_bundle",
      sourceId: "cs_1",
    },
  ]);
  assert.equal(calls.emails.length, 1);
  assert.equal(calls.emails[0].kind, "license");
  assert.equal(calls.emails[0].payload.licenseKey, "HW-NEW-KEY-0001");
  assert.equal(calls.emails[0].payload.customerEmail, "Buyer@Example.com ");
});

test("a redelivered license purchase resends the existing key and writes nothing", async () => {
  behaviour.bySession.set("cs_1", accountKeyRow({ key: "HW-EXISTING-0001" }));

  await handleLicensePurchase(checkoutSession());

  assert.deepEqual(calls.insertAccountKey, []);
  assert.deepEqual(calls.grantCreditLot, []);
  assert.deepEqual(calls.getOrCreateUser, []);
  assert.equal(calls.emails.length, 1);
  assert.equal(calls.emails[0].payload.licenseKey, "HW-EXISTING-0001");
});

test("a license purchase retries a colliding key and gives up after 10 attempts", async () => {
  behaviour.generatedKeys = Array.from({ length: 12 }, (_unused, i) => `HW-DUP-${i}`);
  for (const key of behaviour.generatedKeys) behaviour.takenKeys.add(key);

  await assert.rejects(handleLicensePurchase(checkoutSession()), {
    message: "Failed to generate unique license key after max attempts",
  });

  assert.equal(calls.generateLicenseKey, 10);
  assert.equal(calls.findAccountByKey.length, 10);
  assert.deepEqual(calls.insertAccountKey, []);
});

test("a license purchase takes the first free key after a collision", async () => {
  behaviour.generatedKeys = ["HW-TAKEN-0001", "HW-FREE-0002"];
  behaviour.takenKeys.add("HW-TAKEN-0001");

  await handleLicensePurchase(checkoutSession());

  assert.equal(calls.generateLicenseKey, 2);
  assert.equal(calls.insertAccountKey[0].key, "HW-FREE-0002");
});

test("a license purchase fails loudly when the user cannot be created", async () => {
  behaviour.user = null;

  await assert.rejects(handleLicensePurchase(checkoutSession()), {
    // The address is logged as its #717 tag (sha256 of "buyer@example.com"),
    // never in the clear — the webhook route logs this message.
    message: "Failed to create user for 6a6c26195c36",
  });

  assert.deepEqual(calls.insertAccountKey, []);
});

test("a concurrent insert (23505) ends the license purchase without a second grant", async () => {
  behaviour.insertError = Object.assign(new Error("duplicate key"), { code: "23505" });

  await handleLicensePurchase(checkoutSession());

  assert.equal(calls.insertAccountKey.length, 1);
  assert.deepEqual(calls.grantCreditLot, []);
  assert.deepEqual(calls.emails, []);
});

test("a non-duplicate insert failure propagates out of the license purchase", async () => {
  behaviour.insertError = Object.assign(new Error("connection reset"), { code: "08006" });

  await assert.rejects(handleLicensePurchase(checkoutSession()), {
    message: "connection reset",
  });

  assert.deepEqual(calls.grantCreditLot, []);
  assert.deepEqual(calls.emails, []);
});

test("a failed bundled-credit grant still delivers the license email", async () => {
  behaviour.grantLotError = new Error("credit ledger unavailable");

  await handleLicensePurchase(checkoutSession());

  assert.equal(calls.grantCreditLot.length, 1);
  assert.equal(calls.emails.length, 1);
  assert.equal(calls.emails[0].kind, "license");
});

test("a failed license email does not fail the webhook", async () => {
  behaviour.emailSuccess = false;

  await handleLicensePurchase(checkoutSession());

  assert.equal(calls.insertAccountKey.length, 1);
  assert.equal(calls.emails.length, 1);
});

test("a license purchase reads the Stripe customer id out of an expanded customer object", async () => {
  await handleLicensePurchase(checkoutSession({ customer: { id: "cus_expanded" } }));

  assert.equal(calls.insertAccountKey[0].stripeCustomerId, "cus_expanded");
});

test("a license purchase with no customer name falls back to the email local part", async () => {
  await handleLicensePurchase(
    checkoutSession({ customer_details: { email: "solo@example.com", name: null } }),
  );

  assert.equal(calls.emails[0].payload.customerName, "solo");
});

// ---------------------------------------------------------------------------
// handleCreditPurchase — gates
// ---------------------------------------------------------------------------

test("a credit purchase with no credit_amount metadata is rejected", async () => {
  await assert.rejects(handleCreditPurchase(checkoutSession(), "evt_1"), {
    message: "Invalid credit amount in metadata: undefined",
  });

  assert.deepEqual(calls.grantCreditsForStripeEvent, []);
});

test("a credit purchase with a non-positive credit_amount is rejected", async () => {
  await assert.rejects(
    handleCreditPurchase(checkoutSession({ metadata: { credit_amount: "-500" } }), "evt_1"),
    { message: "Invalid credit amount in metadata: -500" },
  );

  assert.deepEqual(calls.grantCreditsForStripeEvent, []);
});

test("an unpaid credit checkout grants nothing", async () => {
  await handleCreditPurchase(
    checkoutSession({ payment_status: "unpaid", metadata: { credit_amount: "500" } }),
    "evt_1",
  );

  assert.deepEqual(calls.grantCreditsForStripeEvent, []);
  assert.deepEqual(calls.emails, []);
});

// ---------------------------------------------------------------------------
// handleCreditPurchase — top-up path (metadata.license_key present)
// ---------------------------------------------------------------------------

test("a top-up grants the credits against the session and emails the new balance", async () => {
  behaviour.byEmail.set("owner@example.com", [
    accountKeyRow({ key: "HW-OWNED-0001", email: "owner@example.com", userId: "user_9" }),
  ]);
  behaviour.creditBalance = 7_500;

  await handleCreditPurchase(
    checkoutSession({
      metadata: { license_key: "HW-OWNED-0001", credit_amount: "2500" },
    }),
    "evt_top",
    "checkout.session.async_payment_succeeded",
  );

  assert.deepEqual(calls.grantCreditsForStripeEvent, [
    {
      eventId: "evt_top",
      eventType: "checkout.session.async_payment_succeeded",
      stripeObjectId: "cs_1",
      userId: "user_9",
      creditAmount: 2_500,
      sourceType: "stripe_credit_pack",
      sourceId: "cs_1",
    },
  ]);
  assert.equal(calls.emails.length, 1);
  assert.equal(calls.emails[0].kind, "topup");
  assert.equal(calls.emails[0].payload.newBalance, 7_500);
  assert.equal(calls.emails[0].payload.creditAmount, 2_500);
  assert.equal(calls.emails[0].payload.licenseKey, "HW-OWNED-0001");
});

test("a top-up against an unknown license key is rejected", async () => {
  await assert.rejects(
    handleCreditPurchase(
      checkoutSession({ metadata: { license_key: "HW-GHOST-0001", credit_amount: "500" } }),
      "evt_1",
    ),
    { message: "License not found: HW-GHOS..." },
  );

  assert.deepEqual(calls.grantCreditsForStripeEvent, []);
});

test("a top-up against a revoked license key is refused, not silently granted", async () => {
  behaviour.byEmail.set("owner@example.com", [
    accountKeyRow({ key: "HW-DEAD-0001", email: "owner@example.com", status: "revoked" }),
  ]);

  await assert.rejects(
    handleCreditPurchase(
      checkoutSession({ metadata: { license_key: "HW-DEAD-0001", credit_amount: "500" } }),
      "evt_1",
    ),
    { message: "Cannot grant credits to revoked license: HW-DEAD..." },
  );

  assert.deepEqual(calls.grantCreditsForStripeEvent, []);
  assert.deepEqual(calls.emails, []);
});

test("a redelivered top-up does not email a second receipt", async () => {
  behaviour.byEmail.set("owner@example.com", [
    accountKeyRow({ key: "HW-OWNED-0001", email: "owner@example.com" }),
  ]);
  behaviour.grantForEventResult = "duplicate";

  await handleCreditPurchase(
    checkoutSession({ metadata: { license_key: "HW-OWNED-0001", credit_amount: "500" } }),
    "evt_1",
  );

  assert.equal(calls.grantCreditsForStripeEvent.length, 1);
  assert.deepEqual(calls.emails, []);
  assert.deepEqual(calls.getCreditBalance, []);
});

// ---------------------------------------------------------------------------
// handleCreditPurchase — mint path (no metadata.license_key)
// ---------------------------------------------------------------------------

test("a guest credit purchase mints a key, grants the credits and sends the mint email", async () => {
  behaviour.generatedKeys = ["HW-MINT-0001"];

  await handleCreditPurchase(
    checkoutSession({ metadata: { credit_amount: "1500" } }),
    "evt_mint",
  );

  assert.equal(calls.insertAccountKey.length, 1);
  assert.equal(calls.insertAccountKey[0].key, "HW-MINT-0001");
  assert.equal(calls.insertAccountKey[0].email, "buyer@example.com");
  assert.deepEqual(calls.grantCreditsForStripeEvent, [
    {
      eventId: "evt_mint",
      eventType: "checkout.session.completed",
      stripeObjectId: "cs_1",
      userId: "user_1",
      creditAmount: 1_500,
      sourceType: "stripe_credit_pack",
      sourceId: "cs_1",
    },
  ]);
  // A mint gets no license_bundle lot — the bundle belongs to a license purchase.
  assert.deepEqual(calls.grantCreditLot, []);
  assert.equal(calls.emails.length, 1);
  assert.equal(calls.emails[0].kind, "mint");
  assert.equal(calls.emails[0].payload.licenseKey, "HW-MINT-0001");
  // Threaded through as the session gave it (RFC 5321 local parts are case-sensitive).
  assert.deepEqual(calls.getOrCreateUser.map((call) => call.email), ["Buyer@Example.com "]);
  assert.equal(calls.emails[0].payload.customerEmail, "Buyer@Example.com ");
});

test("a guest credit purchase pools into the buyer's existing granted key", async () => {
  behaviour.byEmail.set("buyer@example.com", [
    accountKeyRow({ key: "HW-LIVE-0002", userId: "user_live", status: "granted" }),
  ]);
  behaviour.creditBalance = 9_000;

  await handleCreditPurchase(checkoutSession({ metadata: { credit_amount: "600" } }), "evt_1");

  // Pooling must not mint a second key with a split balance.
  assert.deepEqual(calls.insertAccountKey, []);
  assert.equal(calls.generateLicenseKey, 0);
  assert.equal(calls.grantCreditsForStripeEvent[0].userId, "user_live");
  assert.equal(calls.emails.length, 1);
  assert.equal(calls.emails[0].kind, "topup");
  assert.equal(calls.emails[0].payload.licenseKey, "HW-LIVE-0002");
  assert.equal(calls.emails[0].payload.newBalance, 9_000);
});

test("pooling skips a revoked key and takes the granted one listed after it", async () => {
  behaviour.byEmail.set("buyer@example.com", [
    accountKeyRow({ key: "HW-DEAD-0001", userId: "user_dead", status: "revoked" }),
    accountKeyRow({ key: "HW-LIVE-0002", userId: "user_live", status: "granted" }),
  ]);

  await handleCreditPurchase(checkoutSession({ metadata: { credit_amount: "600" } }), "evt_1");

  assert.equal(calls.grantCreditsForStripeEvent[0].userId, "user_live");
  assert.equal(calls.emails[0].payload.licenseKey, "HW-LIVE-0002");
});

test("a buyer whose only key is revoked gets a freshly minted key", async () => {
  behaviour.byEmail.set("buyer@example.com", [
    accountKeyRow({ key: "HW-DEAD-0001", userId: "user_dead", status: "revoked" }),
  ]);
  behaviour.generatedKeys = ["HW-MINT-0003"];

  await handleCreditPurchase(checkoutSession({ metadata: { credit_amount: "600" } }), "evt_1");

  assert.equal(calls.insertAccountKey.length, 1);
  assert.equal(calls.insertAccountKey[0].key, "HW-MINT-0003");
  assert.equal(calls.emails[0].kind, "mint");
});

test("a redelivered mint reuses the session's key and sends no second email", async () => {
  behaviour.bySession.set("cs_1", accountKeyRow({ key: "HW-MINT-0001", userId: "user_5" }));
  behaviour.grantForEventResult = "duplicate";

  await handleCreditPurchase(checkoutSession({ metadata: { credit_amount: "600" } }), "evt_1");

  assert.deepEqual(calls.insertAccountKey, []);
  assert.deepEqual(calls.getAccountKeysByEmail, []);
  assert.equal(calls.grantCreditsForStripeEvent[0].userId, "user_5");
  assert.deepEqual(calls.emails, []);
});

test("a guest credit purchase with no customer email is rejected", async () => {
  await assert.rejects(
    handleCreditPurchase(
      checkoutSession({
        customer_details: { email: null, name: null },
        metadata: { credit_amount: "600" },
      }),
      "evt_1",
    ),
    { message: "No customer email in credit checkout session" },
  );

  assert.deepEqual(calls.grantCreditsForStripeEvent, []);
});

test("a mint whose insert loses the 23505 race falls back to the row the winner wrote", async () => {
  behaviour.insertError = Object.assign(new Error("duplicate key"), { code: "23505" });
  let sessionLookups = 0;
  const winner = accountKeyRow({ key: "HW-WINNER-0001", userId: "user_winner" });
  behaviour.bySession = {
    get: () => (sessionLookups++ === 0 ? undefined : winner),
  } as unknown as Map<string, ReturnType<typeof accountKeyRow>>;

  await handleCreditPurchase(checkoutSession({ metadata: { credit_amount: "600" } }), "evt_1");

  assert.equal(calls.grantCreditsForStripeEvent[0].userId, "user_winner");
  assert.equal(calls.emails[0].payload.licenseKey, "HW-WINNER-0001");
});

test("a mint that cannot resolve a license after the 23505 race fails loudly", async () => {
  behaviour.insertError = Object.assign(new Error("duplicate key"), { code: "23505" });

  await assert.rejects(
    handleCreditPurchase(checkoutSession({ metadata: { credit_amount: "600" } }), "evt_1"),
    { message: "Failed to resolve minted license for session cs_1" },
  );

  assert.deepEqual(calls.grantCreditsForStripeEvent, []);
});

test("a mint propagates a non-duplicate insert failure", async () => {
  behaviour.insertError = Object.assign(new Error("disk full"), { code: "53100" });

  await assert.rejects(
    handleCreditPurchase(checkoutSession({ metadata: { credit_amount: "600" } }), "evt_1"),
    { message: "disk full" },
  );

  assert.deepEqual(calls.grantCreditsForStripeEvent, []);
});

test("a mint gives up after 10 colliding keys", async () => {
  behaviour.generatedKeys = Array.from({ length: 12 }, (_unused, i) => `HW-DUP-${i}`);
  for (const key of behaviour.generatedKeys) behaviour.takenKeys.add(key);

  await assert.rejects(
    handleCreditPurchase(checkoutSession({ metadata: { credit_amount: "600" } }), "evt_1"),
    { message: "Failed to generate unique license key after max attempts" },
  );

  assert.equal(calls.generateLicenseKey, 10);
  assert.deepEqual(calls.insertAccountKey, []);
});

test("a mint fails loudly when the user cannot be created", async () => {
  behaviour.user = null;

  await assert.rejects(
    handleCreditPurchase(checkoutSession({ metadata: { credit_amount: "600" } }), "evt_1"),
    // #717: the tag of "buyer@example.com", not the address.
    { message: "Failed to create user for 6a6c26195c36" },
  );

  // The tag is the same for any case or padding, so it cannot show that the raw
  // session address reached the user lookup untouched. This does.
  assert.deepEqual(
    calls.getOrCreateUser.map((call) => call.email),
    ["Buyer@Example.com "],
  );
  assert.deepEqual(calls.insertAccountKey, []);
});

// ---------------------------------------------------------------------------
// A receipt that the mail provider refuses must never undo a paid grant.
// ---------------------------------------------------------------------------

test("a failed top-up receipt does not fail the webhook", async () => {
  behaviour.byEmail.set("owner@example.com", [
    accountKeyRow({ key: "HW-OWNED-0001", email: "owner@example.com" }),
  ]);
  behaviour.emailSuccess = false;

  await handleCreditPurchase(
    checkoutSession({ metadata: { license_key: "HW-OWNED-0001", credit_amount: "500" } }),
    "evt_1",
  );

  assert.equal(calls.grantCreditsForStripeEvent.length, 1);
  assert.equal(calls.emails.length, 1);
});

test("a failed pooled receipt does not fail the webhook", async () => {
  behaviour.byEmail.set("buyer@example.com", [
    accountKeyRow({ key: "HW-LIVE-0002", userId: "user_live" }),
  ]);
  behaviour.emailSuccess = false;

  await handleCreditPurchase(checkoutSession({ metadata: { credit_amount: "600" } }), "evt_1");

  assert.equal(calls.grantCreditsForStripeEvent.length, 1);
  assert.equal(calls.emails[0].kind, "topup");
});

test("a failed mint email does not fail the webhook", async () => {
  behaviour.emailSuccess = false;

  await handleCreditPurchase(checkoutSession({ metadata: { credit_amount: "600" } }), "evt_1");

  assert.equal(calls.grantCreditsForStripeEvent.length, 1);
  assert.equal(calls.emails[0].kind, "mint");
});

// ---------------------------------------------------------------------------
// #717: no webhook log line carries the buyer's address
// ---------------------------------------------------------------------------

test("no webhook log line carries the buyer's address, on any purchase path", async () => {
  const from = logLines.length;

  // License purchase: success, then a refused license email.
  await handleLicensePurchase(checkoutSession());
  resetHarness();
  behaviour.emailSuccess = false;
  await handleLicensePurchase(checkoutSession());

  // Mint, then a pool into the minted key, each with a refused email.
  resetHarness();
  behaviour.emailSuccess = false;
  await handleCreditPurchase(checkoutSession({ metadata: { credit_amount: "600" } }), "evt_1");
  resetHarness();
  behaviour.byEmail.set("buyer@example.com", [accountKeyRow({ key: "HW-LIVE-0002" })]);
  behaviour.emailSuccess = false;
  await handleCreditPurchase(
    checkoutSession({ id: "cs_2", metadata: { credit_amount: "600" } }),
    "evt_2",
  );

  const lines = logLines.slice(from);
  assert.ok(lines.length > 0, "the handlers logged");
  assert.deepEqual(
    lines.filter((line) => /buyer@example\.com/i.test(line)),
    [],
  );
  // Positive control: the lines that used to carry the address carry its tag.
  assert.ok(lines.some((line) => line.includes("Processing license purchase for 6a6c26195c36")));
  assert.ok(lines.some((line) => line.includes("Processing credit purchase by 6a6c26195c36")));
});

// ---------------------------------------------------------------------------
// handleChargeRefunded
// ---------------------------------------------------------------------------

test("a charge with no payment intent is skipped before Stripe is queried", async () => {
  await handleChargeRefunded(charge({ payment_intent: null }));

  assert.deepEqual(calls.stripeSessionQueries, []);
  assert.deepEqual(calls.revokeAccountKey, []);
});

test("a charge with an expanded payment intent is traced by its id", async () => {
  await handleChargeRefunded(charge({ payment_intent: { id: "pi_expanded" } }));

  assert.deepEqual(calls.stripeSessionQueries, [
    { payment_intent: "pi_expanded", limit: 1 },
  ]);
});

test("a charge with no matching checkout session is skipped", async () => {
  await handleChargeRefunded(charge());

  assert.equal(calls.stripeSessionQueries.length, 1);
  assert.deepEqual(calls.refundCreditGrant, []);
  assert.deepEqual(calls.revokeAccountKey, []);
});

test("a refund for an unknown purchase type touches nothing", async () => {
  behaviour.stripeSessions = [{ id: "cs_1", metadata: { purchase_type: "merch" } }];

  await handleChargeRefunded(charge());

  assert.deepEqual(calls.refundCreditGrant, []);
  assert.deepEqual(calls.revokeAccountKey, []);
});

test("a partial refund on a license purchase does not revoke the key", async () => {
  behaviour.stripeSessions = [{ id: "cs_1", metadata: { purchase_type: "license" } }];

  await handleChargeRefunded(charge({ amount_refunded: 999 }));

  assert.deepEqual(calls.refundCreditGrant, []);
  assert.deepEqual(calls.revokeAccountKey, []);
  assert.deepEqual(calls.findAccountByStripeSession, []);
});

test("a full refund on a license purchase claws back the bundle and revokes the key", async () => {
  behaviour.stripeSessions = [{ id: "cs_1", metadata: { purchase_type: "license" } }];
  behaviour.bySession.set(
    "cs_1",
    accountKeyRow({ id: "row_7", userId: "user_7", status: "granted" }),
  );

  await handleChargeRefunded(charge());

  assert.deepEqual(calls.refundCreditGrant, [
    { sourceType: "license_bundle", sourceId: "cs_1" },
  ]);
  assert.deepEqual(calls.revokeAccountKey, [{ id: "row_7", userId: "user_7" }]);
  assert.deepEqual(calls.revokeWebAccess, []);
});

test("a re-sent refund on an already revoked key sweeps its web sessions instead", async () => {
  behaviour.stripeSessions = [{ id: "cs_1", metadata: { purchase_type: "license" } }];
  behaviour.bySession.set(
    "cs_1",
    accountKeyRow({ id: "row_7", userId: "user_7", status: "revoked" }),
  );

  await handleChargeRefunded(charge());

  assert.deepEqual(calls.revokeWebAccess, ["user_7"]);
  assert.deepEqual(calls.revokeAccountKey, []);
});

test("a full refund whose license row is missing stops without a revocation", async () => {
  behaviour.stripeSessions = [{ id: "cs_1", metadata: { purchase_type: "license" } }];

  await handleChargeRefunded(charge());

  assert.deepEqual(calls.findAccountByStripeSession, ["cs_1"]);
  assert.deepEqual(calls.refundCreditGrant, []);
  assert.deepEqual(calls.revokeAccountKey, []);
});

test("a credit refund short of the credit portion is left alone", async () => {
  behaviour.stripeSessions = [
    {
      id: "cs_1",
      metadata: { purchase_type: "credits", fee_cents: "60", credit_amount: "1000" },
    },
  ];

  // Credit portion is 1000 - 60 = 940. 939 does not cover it.
  await handleChargeRefunded(charge({ amount_refunded: 939 }));

  assert.deepEqual(calls.refundCreditGrant, []);
});

test("a credit refund that covers the credit portion less the 6% fee claws the grant back", async () => {
  behaviour.stripeSessions = [
    {
      id: "cs_1",
      metadata: { purchase_type: "credits", fee_cents: "60", credit_amount: "1000" },
    },
  ];
  behaviour.refundResult = { status: "processed", refundedAmount: 1_000 };

  await handleChargeRefunded(charge({ amount_refunded: 940 }));

  assert.deepEqual(calls.refundCreditGrant, [
    { sourceType: "stripe_credit_pack", sourceId: "cs_1" },
  ]);
  assert.deepEqual(calls.revokeAccountKey, []);
});

test("a credit refund with no fee_cents metadata needs the whole charge refunded", async () => {
  behaviour.stripeSessions = [
    { id: "cs_1", metadata: { purchase_type: "credits", credit_amount: "1000" } },
  ];

  await handleChargeRefunded(charge({ amount_refunded: 999 }));
  assert.deepEqual(calls.refundCreditGrant, []);

  await handleChargeRefunded(charge({ amount_refunded: 1_000 }));
  assert.equal(calls.refundCreditGrant.length, 1);
});

test("a credit refund with unusable credit_amount metadata does not touch the ledger", async () => {
  behaviour.stripeSessions = [
    { id: "cs_1", metadata: { purchase_type: "credits", fee_cents: "0" } },
  ];

  await handleChargeRefunded(charge());

  assert.deepEqual(calls.refundCreditGrant, []);
});

test("a redelivered credit refund does not deduct twice", async () => {
  behaviour.stripeSessions = [
    {
      id: "cs_1",
      metadata: {
        purchase_type: "credits",
        fee_cents: "0",
        credit_amount: "1000",
        license_key: "HW-OWNED-0001",
      },
    },
  ];
  behaviour.refundResult = { status: "duplicate", refundedAmount: 0 };

  await handleChargeRefunded(charge());

  assert.equal(calls.refundCreditGrant.length, 1);
  assert.deepEqual(calls.revokeAccountKey, []);
});
