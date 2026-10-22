/**
 * The Stripe refund webhook end to end against the REAL credit ledger (#1351).
 *
 * `stripe-webhook.test.ts` mocks the database layer, so it proves what the
 * webhook asks for, not what the ledger does with it. Here the real
 * `lib/services/stripe-webhook.ts` drives the real `src/lib/db-layer.ts` on
 * PGlite with the real migrations (see `credit-ledger-harness.ts`). Only the
 * Stripe client, the email service and the key generator are mocked.
 *
 * The rule under test: a refund of a credit pack removes credits at the price
 * the buyer paid, from that pack's grant only, never from another pack, and
 * never more than the pack has left unrefunded.
 */
import assert from "node:assert/strict";
import { after, before, beforeEach, describe, mock, test } from "node:test";

import type Stripe from "stripe";

import {
  cachedBalance,
  grantsBySource,
  loadDbLayer,
  resetDatabase,
  seedUser,
} from "./credit-ledger-harness";

interface ModuleMocker {
  module(specifier: string, options: { namedExports: Record<string, unknown> }): void;
}

const moduleUrl = (relative: string) => new URL(relative, import.meta.url).href;

/** Checkout Sessions Stripe would return, keyed by payment intent. */
const sessionsByIntent = new Map<string, unknown>();

(mock as unknown as ModuleMocker).module(moduleUrl("../lib/clients/stripe.ts"), {
  namedExports: {
    stripe: {
      checkout: {
        sessions: {
          list: async (query: { payment_intent?: string }) => {
            const hit = sessionsByIntent.get(query.payment_intent ?? "");
            return { data: hit ? [hit] : [] };
          },
        },
      },
    },
  },
});
(mock as unknown as ModuleMocker).module(moduleUrl("../lib/services/email.ts"), {
  namedExports: { emailService: {} },
});
(mock as unknown as ModuleMocker).module(moduleUrl("../lib/services/license-key.ts"), {
  namedExports: { generateLicenseKey: () => "HW-TEST-0000-0000" },
});

let L: Awaited<ReturnType<typeof loadDbLayer>>;
let handleChargeRefunded: (charge: Stripe.Charge) => Promise<void>;

const realLog = console.log;
const realWarn = console.warn;

before(async () => {
  L = await loadDbLayer();
  ({ handleChargeRefunded } = await import("@/lib/services/stripe-webhook"));
  console.log = () => {};
  console.warn = () => {};
});
after(() => {
  console.log = realLog;
  console.warn = realWarn;
});
beforeEach(async () => {
  await resetDatabase();
  sessionsByIntent.clear();
  await seedUser("u1");
});

/**
 * Buy a credit pack the way checkout.session.completed records it, and make
 * its Checkout Session findable by the charge's payment intent.
 */
async function buyPack(p: {
  session: string;
  credits: number;
  feeCents: number;
  amountTotal: number;
  amountTax?: number;
}): Promise<string> {
  const intent = `pi_${p.session}`;
  sessionsByIntent.set(intent, {
    id: p.session,
    amount_total: p.amountTotal,
    total_details: { amount_discount: 0, amount_shipping: 0, amount_tax: p.amountTax ?? 0 },
    metadata: {
      purchase_type: "credits",
      credit_amount: String(p.credits),
      fee_cents: String(p.feeCents),
    },
  });
  await L.grantCreditsForStripeEvent({
    eventId: `evt_${p.session}`,
    eventType: "checkout.session.completed",
    stripeObjectId: p.session,
    userId: "u1",
    creditAmount: p.credits,
  });
  return intent;
}

function refund(intent: string, amount: number, amountRefunded: number): Promise<void> {
  return handleChargeRefunded({
    id: `ch_${intent}`,
    amount,
    amount_refunded: amountRefunded,
    payment_intent: intent,
  } as unknown as Stripe.Charge);
}

describe("charge.refunded on a credit pack, against the real ledger", () => {
  test("$4.50 of a $5.30 pack with 500 credits used removes the 4,500 unused credits", async () => {
    const intent = await buyPack({ session: "cs_1", credits: 5000, feeCents: 30, amountTotal: 530 });
    await L.spendCreditGrantsByProvenance("u1", 500);

    await refund(intent, 530, 450);

    const pack = (await grantsBySource("u1")).get("cs_1");
    assert.equal(pack?.remaining, 0);
    assert.equal(pack?.refunded, 4500);
    assert.equal(pack?.status, "refunded");
    assert.equal(await L.getCreditBalance("u1"), 0);
  });

  test("a re-delivered refund event removes nothing more", async () => {
    const intent = await buyPack({ session: "cs_1", credits: 5000, feeCents: 30, amountTotal: 530 });

    await refund(intent, 530, 200);
    await refund(intent, 530, 200);

    const pack = (await grantsBySource("u1")).get("cs_1");
    assert.equal(pack?.remaining, 3000);
    assert.equal(pack?.refunded, 2000);
    assert.equal(await cachedBalance("u1"), 3000);
  });

  test("two partial refunds of one pack remove each refund's credits once", async () => {
    const intent = await buyPack({ session: "cs_1", credits: 10000, feeCents: 60, amountTotal: 1060 });

    await refund(intent, 1060, 300);
    await refund(intent, 1060, 800);

    const pack = (await grantsBySource("u1")).get("cs_1");
    assert.equal(pack?.remaining, 2000);
    assert.equal(pack?.refunded, 8000);
  });

  test("refunding pack A (500 used) leaves every credit of pack B", async () => {
    const intentA = await buyPack({ session: "cs_A", credits: 5000, feeCents: 30, amountTotal: 530 });
    await L.spendCreditGrantsByProvenance("u1", 500);
    await buyPack({ session: "cs_B", credits: 10000, feeCents: 60, amountTotal: 1060 });

    // Support refunds pack A's whole credit value, $5.00, by mistake: 5,000
    // credits of value, but only 4,500 are left on pack A.
    await refund(intentA, 530, 500);

    const g = await grantsBySource("u1");
    assert.equal(g.get("cs_A")?.remaining, 0);
    assert.equal(g.get("cs_A")?.refunded, 5000);
    assert.equal(g.get("cs_B")?.remaining, 10000);
    assert.equal(g.get("cs_B")?.refunded, 0);
    assert.equal(g.get("cs_B")?.status, "active");
    assert.equal(await L.getCreditBalance("u1"), 10000);
  });

  test("a tax-exclusive pack refunds its credit value plus tax in full", async () => {
    // $5.00 + $0.30 fee + 10% tax = $5.83. The old rule skipped a $5.50 refund.
    const intent = await buyPack({ session: "cs_1", credits: 5000, feeCents: 30, amountTotal: 583, amountTax: 53 });

    await refund(intent, 583, 550);

    const pack = (await grantsBySource("u1")).get("cs_1");
    assert.equal(pack?.remaining, 0);
    assert.equal(pack?.refunded, 5000);
  });
});
