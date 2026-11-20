/**
 * Two overlapping deliveries of ONE guest credit checkout, against the REAL
 * credit ledger (#2947).
 *
 * The mint path (no `metadata.license_key`) looks the session up, then pools
 * by email, then mints. Delivery B can miss the session lookup (A has not
 * inserted its key yet), then find A's freshly minted key by email AFTER the
 * insert. If B then wins `grantCreditsForStripeEvent`, A gets "duplicate" and
 * returns before any email. Before the fix B sent only the top-up receipt
 * (key shown as "HW-ABCD…"), so the buyer was never emailed the key they paid
 * for. The mint email is decided from the key row's `stripe_session_id` now.
 *
 * The real `lib/services/stripe-webhook.ts` drives the real
 * `src/lib/db-layer.ts` on PGlite with the real migrations (see
 * `credit-ledger-harness.ts`). Only Stripe, the email service and the key
 * generator are mocked; four db-layer calls are wrapped (not replaced) to gate
 * the interleaving.
 */
import assert from "node:assert/strict";
import { AsyncLocalStorage } from "node:async_hooks";
import { after, before, beforeEach, test } from "node:test";

import type Stripe from "stripe";

import { loadDbLayer, resetDatabase } from "./credit-ledger-harness";
import { moduleMock, moduleUrl } from "./module-mock";

type Delivery = "A" | "B";

const emails: Array<{ kind: "mint" | "topup"; licenseKey: string }> = [];
let nextKey = 0;

moduleMock.module(moduleUrl("../lib/clients/stripe.ts"), {
  namedExports: { stripe: {} },
});
moduleMock.module(moduleUrl("../lib/services/email.ts"), {
  namedExports: {
    emailService: {
      sendCreditMint: async (d: { licenseKey: string }) => {
        emails.push({ kind: "mint", licenseKey: d.licenseKey });
        return { success: true };
      },
      sendCreditTopUp: async (d: { licenseKey: string }) => {
        emails.push({ kind: "topup", licenseKey: d.licenseKey });
        return { success: true };
      },
    },
  },
});
moduleMock.module(moduleUrl("../lib/services/license-key.ts"), {
  namedExports: {
    generateLicenseKey: () =>
      `HW-RACE-0000-${String(++nextKey).padStart(4, "0")}`,
  },
});

function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((r) => (resolve = r));
  return { promise, resolve };
}

/** Which delivery the current async call chain belongs to; unset = no gating. */
const who = new AsyncLocalStorage<Delivery>();
let gates = {
  bLookedUpSession: deferred(),
  aInsertedKey: deferred(),
  bGranted: deferred(),
};

let handleCreditPurchase: (
  session: Stripe.Checkout.Session,
  eventId: string,
  eventType?: string,
) => Promise<void>;

let real: Awaited<ReturnType<typeof loadDbLayer>>;

const realLog = console.log;
before(async () => {
  real = await loadDbLayer();
  // Wrap the real db-layer, then load the webhook so it binds to the wrapper.
  const wrapped: Record<string, unknown> = { ...real };
  wrapped.findAccountByStripeSession = async (id: string) => {
    const row = await real.findAccountByStripeSession(id);
    if (who.getStore() === "B") gates.bLookedUpSession.resolve();
    return row;
  };
  wrapped.getAccountKeysByEmail = async (email: string) => {
    if (who.getStore() === "B") await gates.aInsertedKey.promise;
    return real.getAccountKeysByEmail(email);
  };
  wrapped.insertAccountKey = async (
    data: Parameters<typeof real.insertAccountKey>[0],
  ) => {
    if (who.getStore() === "A") await gates.bLookedUpSession.promise;
    const row = await real.insertAccountKey(data);
    if (who.getStore() === "A") gates.aInsertedKey.resolve();
    return row;
  };
  wrapped.grantCreditsForStripeEvent = async (
    data: Parameters<typeof real.grantCreditsForStripeEvent>[0],
  ) => {
    if (who.getStore() === "A") await gates.bGranted.promise;
    try {
      return await real.grantCreditsForStripeEvent(data);
    } finally {
      if (who.getStore() === "B") gates.bGranted.resolve();
    }
  };
  moduleMock.module(moduleUrl("../src/lib/db-layer.ts"), {
    namedExports: wrapped,
  });
  ({ handleCreditPurchase } = await import("@/lib/services/stripe-webhook"));
  console.log = () => {};
});
after(() => {
  console.log = realLog;
});
beforeEach(async () => {
  await resetDatabase();
  emails.length = 0;
  gates = {
    bLookedUpSession: deferred(),
    aInsertedKey: deferred(),
    bGranted: deferred(),
  };
});

function guestCheckout(id: string, email: string): Stripe.Checkout.Session {
  return {
    id,
    payment_status: "paid",
    customer: "cus_race_1",
    customer_details: { email, name: "Guest" },
    metadata: {
      purchase_type: "credits",
      credit_amount: "5000",
      fee_cents: "30",
    },
  } as unknown as Stripe.Checkout.Session;
}

test("a guest checkout delivered twice at once still gets the mint email with the full key (#2947)", async () => {
  const email = "race-guest@example.test";
  const session = guestCheckout("cs_test_mint_race", email);

  await Promise.all([
    who.run("A", () => handleCreditPurchase(session, "evt_A")),
    who.run("B", () => handleCreditPurchase(session, "evt_A")),
  ]);

  const keys = await real.getAccountKeysByEmail(email);
  assert.equal(keys.length, 1, "one key minted");
  assert.equal(keys[0].stripeSessionId, session.id);
  assert.equal(
    await real.getCreditBalance(keys[0].userId),
    5000,
    "credits granted once",
  );
  assert.deepEqual(
    emails,
    [{ kind: "mint", licenseKey: keys[0].key }],
    "the buyer of a new key must receive the mint email (the only email that carries the full key)",
  );
});

test("a second checkout by the same email still pools into the first key with a top-up receipt", async () => {
  const email = "repeat-guest@example.test";

  await handleCreditPurchase(guestCheckout("cs_test_first", email), "evt_1");
  await handleCreditPurchase(guestCheckout("cs_test_second", email), "evt_2");

  const keys = await real.getAccountKeysByEmail(email);
  assert.equal(keys.length, 1, "the second purchase pooled, it did not mint");
  assert.equal(await real.getCreditBalance(keys[0].userId), 10_000);
  assert.deepEqual(
    emails.map((e) => e.kind),
    ["mint", "topup"],
  );
});
