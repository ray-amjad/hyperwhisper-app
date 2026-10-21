/**
 * #1834: a `charge.refunded` whose credit grant does not exist, through the
 * REAL route, the REAL webhook service and the REAL credit ledger (PGlite with
 * the real migrations, see `credit-ledger-harness.ts`). Only the Stripe client,
 * the email service and the key generator are mocked.
 *
 * Since #1798 a missing grant answers 500 so Stripe retries the refund until
 * the late mint lands. A grant that will NEVER exist must not: Stripe would
 * retry it for 3 days. Ray's rule (only facts the code can check, no time
 * window): the miss is permanent, answered 200 and logged, when
 *   (a) it is a top-up and its licence row is missing or not granted;
 *   (b) the session was created before the credit ledger (a legacy_unknown
 *       pack: nothing is taken back from that grant);
 *   (c) stripe_processed_events already has the session but no grant exists.
 * Every other missing grant still answers 500.
 */
import assert from "node:assert/strict";
import { after, before, beforeEach, describe, mock, test } from "node:test";

import { sql } from "drizzle-orm";
import { NextRequest } from "next/server";

import {
  cachedBalance,
  db,
  grantsBySource,
  loadDbLayer,
  resetDatabase,
  seedGrant,
  seedUser,
} from "./credit-ledger-harness";
import { formatLogArgs } from "./db-error-fixture";
import { moduleMock, moduleUrl } from "./module-mock";

/** Checkout Sessions Stripe would return, keyed by payment intent. */
const sessionsByIntent = new Map<string, unknown>();
/** The event the stand-in signature verifier hands the route. */
let verifiedEvent: Record<string, unknown> | null = null;

moduleMock.module(moduleUrl("../lib/clients/stripe.ts"), {
  namedExports: {
    stripe: {
      webhooks: {
        constructEvent: () => {
          if (!verifiedEvent) throw new Error("No signatures found");
          return verifiedEvent;
        },
      },
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
// The route reads its webhook secret from the validated env (#682).
moduleMock.module(moduleUrl("../src/env/server.mjs"), {
  namedExports: { env: { STRIPE_WEBHOOK_SECRET: "whsec_harness_only" } },
});
moduleMock.module(moduleUrl("../lib/services/email.ts"), {
  namedExports: { emailService: {} },
});
moduleMock.module(moduleUrl("../lib/services/license-key.ts"), {
  namedExports: { generateLicenseKey: () => "HW-TEST-0000-0000" },
});

type PostHandler = (request: NextRequest) => Promise<Response>;

let L: Awaited<ReturnType<typeof loadDbLayer>>;
let POST: PostHandler;

const logLines: string[] = [];
const realConsole = { log: console.log, warn: console.warn, error: console.error };

/** 2026-06-16T06:55:05.796Z, the journal `when` of 0006_fat_husk. */
const LEDGER_STARTED_S = Math.floor(1781592905796 / 1000);
const AFTER_LEDGER_S = LEDGER_STARTED_S + 86_400;

before(async () => {
  L = await loadDbLayer();
  ({ POST } = (await import("@/app/api/webhooks/stripe/route")) as unknown as {
    POST: PostHandler;
  });
  const capture =
    (level: string) =>
    (...args: unknown[]): void => {
      logLines.push(`${level} ${formatLogArgs(args)}`);
    };
  console.log = capture("log");
  console.warn = capture("warn");
  console.error = capture("error");
});
after(() => {
  console.log = realConsole.log;
  console.warn = realConsole.warn;
  console.error = realConsole.error;
});
beforeEach(async () => {
  await resetDatabase();
  sessionsByIntent.clear();
  verifiedEvent = null;
  logLines.length = 0;
  await seedUser("u1");
});

/** A paid 5,000-credit pack's Checkout Session, findable by its intent. */
function checkoutSession(p: {
  session: string;
  created?: number;
  licenseKey?: string;
}): string {
  const intent = `pi_${p.session}`;
  sessionsByIntent.set(intent, {
    id: p.session,
    created: p.created ?? AFTER_LEDGER_S,
    amount_total: 530,
    total_details: { amount_discount: 0, amount_shipping: 0, amount_tax: 0 },
    metadata: {
      purchase_type: "credits",
      credit_amount: "5000",
      fee_cents: "30",
      ...(p.licenseKey ? { license_key: p.licenseKey } : {}),
    },
  });
  return intent;
}

/** POST a signed charge.refunded for the whole $5.00 of credits. */
function postRefund(intent: string): Promise<Response> {
  verifiedEvent = {
    id: `evt_refund_${intent}`,
    type: "charge.refunded",
    data: {
      object: { id: `ch_${intent}`, amount: 530, amount_refunded: 500, payment_intent: intent },
    },
  };
  return POST(
    new NextRequest("https://hyperwhisper.test/api/webhooks/stripe", {
      method: "POST",
      headers: { "content-type": "application/json", "stripe-signature": "t=1,v1=deadbeef" },
      body: JSON.stringify(verifiedEvent),
    }),
  );
}

/** The user's grants and cached balance, to prove a refund changed nothing. */
async function ledgerState() {
  const grants = Array.from((await grantsBySource("u1")).values())
    .map((g) => ({
      sourceType: g.sourceType,
      sourceId: g.sourceId,
      remaining: g.remaining,
      refunded: g.refunded,
      status: g.status,
    }))
    .sort((a, b) => a.sourceId.localeCompare(b.sourceId));
  return { grants, cached: await cachedBalance("u1") };
}

async function seedKey(key: string, status: string): Promise<void> {
  await L.insertAccountKey({ key, email: "u1@example.test", userId: "u1", status });
}

/** Another pack the refund must never touch. */
async function seedOtherPack(): Promise<void> {
  await L.grantCreditsForStripeEvent({
    eventId: "evt_other",
    eventType: "checkout.session.completed",
    stripeObjectId: "cs_other",
    userId: "u1",
    creditAmount: 2000,
  });
}

function warnings(): string[] {
  return logLines.filter((line) => line.startsWith("warn "));
}

describe("charge.refunded with no grant: permanent cases answer 200 and log why (#1834)", () => {
  test("(a) a top-up whose licence row does not exist", async () => {
    await seedOtherPack();
    const intent = checkoutSession({ session: "cs_gone", licenseKey: "HW-GONE-0000-0000" });
    const before = await ledgerState();

    const response = await postRefund(intent);

    assert.equal(response.status, 200);
    assert.deepEqual(await ledgerState(), before);
    assert.equal(warnings().length, 1, logLines.join("\n"));
    assert.match(warnings()[0], /cs_gone.*took back no credits: top-up licence HW-GONE\.\.\. does not exist/);
  });

  test("(a) a top-up whose licence is revoked", async () => {
    await seedOtherPack();
    await seedKey("HW-DEAD-0000-0000", "revoked");
    const intent = checkoutSession({ session: "cs_dead", licenseKey: "HW-DEAD-0000-0000" });
    const before = await ledgerState();

    const response = await postRefund(intent);

    assert.equal(response.status, 200);
    assert.deepEqual(await ledgerState(), before);
    assert.equal(warnings().length, 1, logLines.join("\n"));
    assert.match(warnings()[0], /cs_dead.*top-up licence HW-DEAD\.\.\. is revoked/);
  });

  test("(b) a pack bought before the credit ledger takes nothing from the legacy_unknown grant", async () => {
    await seedGrant({ userId: "u1", sourceType: "legacy_unknown", sourceId: "u1", amount: 7000 });
    const intent = checkoutSession({ session: "cs_old", created: LEDGER_STARTED_S - 1 });
    const before = await ledgerState();

    const response = await postRefund(intent);

    assert.equal(response.status, 200);
    assert.deepEqual(await ledgerState(), before);
    const legacy = (await grantsBySource("u1")).get("u1");
    assert.equal(legacy?.remaining, 7000);
    assert.equal(legacy?.refunded, 0);
    assert.equal(warnings().length, 1, logLines.join("\n"));
    assert.match(warnings()[0], /cs_old.*bought before the credit ledger \(legacy_unknown grant\)/);
  });

  test("(c) a processed checkout session whose grant no longer exists", async () => {
    await seedOtherPack();
    // The completed event was processed (event row + grant in one
    // transaction); the grant was later removed.
    await L.grantCreditsForStripeEvent({
      eventId: "evt_cs_lost",
      eventType: "checkout.session.completed",
      stripeObjectId: "cs_lost",
      userId: "u1",
      creditAmount: 5000,
    });
    await db.execute(sql`DELETE FROM credit_grants WHERE source_id = 'cs_lost'`);
    const intent = checkoutSession({ session: "cs_lost" });
    const before = await ledgerState();

    const response = await postRefund(intent);

    assert.equal(response.status, 200);
    assert.deepEqual(await ledgerState(), before);
    assert.equal(warnings().length, 1, logLines.join("\n"));
    assert.match(warnings()[0], /cs_lost.*checkout event was already processed but no grant exists/);
  });

  test("(c) a mint that commits after the first lookup is refunded, not called permanent", async () => {
    await seedOtherPack();
    const intent = checkoutSession({ session: "cs_race" });
    // The refund path's only transaction is refundCreditGrant. Run it (it
    // finds no grant), then let checkout.session.completed commit the grant
    // and its processed-event row, before the processed-event check runs.
    const ledgerDb = db as unknown as { transaction: (...args: unknown[]) => Promise<unknown> };
    const realTransaction = ledgerDb.transaction.bind(db);
    let minted = false;
    const hook = mock.method(ledgerDb, "transaction", async (...args: unknown[]) => {
      const out = await realTransaction(...args);
      if (!minted) {
        minted = true;
        hook.mock.restore();
        await L.grantCreditsForStripeEvent({
          eventId: "evt_cs_race",
          eventType: "checkout.session.completed",
          stripeObjectId: "cs_race",
          userId: "u1",
          creditAmount: 5000,
        });
      }
      return out;
    });

    let response: Response;
    try {
      response = await postRefund(intent);
    } finally {
      hook.mock.restore();
    }

    assert.ok(minted, "the mint never ran between the lookup and the check");
    assert.equal(response.status, 200);
    const g = await grantsBySource("u1");
    assert.equal(g.get("cs_race")?.remaining, 0);
    assert.equal(g.get("cs_race")?.refunded, 5000);
    assert.equal(g.get("cs_other")?.remaining, 2000);
    assert.deepEqual(warnings(), []);
  });
});

describe("charge.refunded with no grant yet: still 500 so Stripe retries (#1798)", () => {
  test("a minted pack whose grant is late answers 500, then the retry after the mint takes the credits", async () => {
    await seedOtherPack();
    const intent = checkoutSession({ session: "cs_late" });
    const before = await ledgerState();

    const early = await postRefund(intent);

    assert.equal(early.status, 500);
    assert.deepEqual(await ledgerState(), before);
    assert.deepEqual(warnings(), []);

    // checkout.session.completed lands, then Stripe re-sends the refund.
    await L.grantCreditsForStripeEvent({
      eventId: "evt_cs_late",
      eventType: "checkout.session.completed",
      stripeObjectId: "cs_late",
      userId: "u1",
      creditAmount: 5000,
    });
    const retry = await postRefund(intent);

    assert.equal(retry.status, 200);
    const g = await grantsBySource("u1");
    assert.equal(g.get("cs_late")?.remaining, 0);
    assert.equal(g.get("cs_late")?.refunded, 5000);
    assert.equal(g.get("cs_other")?.remaining, 2000);
  });

  test("a top-up onto a granted licence whose grant is late answers 500", async () => {
    await seedKey("HW-LIVE-0000-0000", "granted");
    const intent = checkoutSession({ session: "cs_topup_late", licenseKey: "HW-LIVE-0000-0000" });

    const response = await postRefund(intent);

    assert.equal(response.status, 500);
    assert.deepEqual(warnings(), []);
  });

  test("a session created at the ledger cut-off is not pre-ledger", async () => {
    const intent = checkoutSession({
      session: "cs_edge",
      created: Math.ceil(1781592905796 / 1000),
    });

    const response = await postRefund(intent);

    assert.equal(response.status, 500);
  });

  test("another session's processed event does not make this one permanent", async () => {
    await seedOtherPack();
    const intent = checkoutSession({ session: "cs_unrelated" });

    const response = await postRefund(intent);

    assert.equal(response.status, 500);
  });
});
