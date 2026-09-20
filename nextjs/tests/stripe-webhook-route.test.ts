/**
 * `POST /api/webhooks/stripe`, driven as HTTP.
 *
 * This is the door money comes through. Stripe is the only caller, and the
 * route is the only thing between a raw HTTP body and a license grant or a
 * credit grant. It has four jobs, and each one is a way to lose money:
 *
 * 1. The HMAC gate. The body must be verified BYTE FOR BYTE, with the header
 *    Stripe sent and the configured secret. A re-serialized body, a missing
 *    header accepted, or a missing secret treated as "fine" turns the endpoint
 *    into an open grant faucet.
 * 2. The payment gate. `checkout.session.completed` arrives for unpaid
 *    sessions too. Granting on one of those hands out a license nobody paid
 *    for.
 * 3. The dispatch. "license" and "credits" go to different handlers, and the
 *    credit handler needs the EVENT id and type — that pair is its idempotency
 *    key, so a wrong argument double-grants on Stripe's retry.
 * 4. The status it answers with. Stripe retries a 5xx and never retries a 2xx.
 *    A handler fault that answers 200 silently drops a paid purchase; the
 *    refund path deliberately answers 200 so a permanent fault cannot start a
 *    retry storm.
 *
 * So the tests below assert what Stripe actually reads — the status and the
 * body — and the exact arguments the route handed each handler. They do not
 * assert that a mock was called and stop there. Collaborators are replaced at
 * the module boundary in `stripe-webhook-route-harness.ts`; the route module
 * is the real one, signature gate included.
 */
import assert from "node:assert/strict";
import { after, afterEach, before, beforeEach, describe, test } from "node:test";

import {
  behaviour,
  calls,
  checkoutEvent,
  loadStripeWebhookRoute,
  logLines,
  refundEvent,
  resetHarness,
  restoreRouteLogging,
  silenceRouteLogging,
  webhookRequest,
} from "./stripe-webhook-route-harness";

/** A made-up secret. Only the stand-in verifier in the harness ever sees it. */
const WEBHOOK_SECRET = "whsec_harness_only";

const realWebhookSecret = process.env.STRIPE_WEBHOOK_SECRET;

type JsonBody = Record<string, unknown>;

async function readJson(response: Response): Promise<JsonBody> {
  return (await response.json()) as JsonBody;
}

type PostHandler = (request: ReturnType<typeof webhookRequest>) => Promise<Response>;

let POST: PostHandler;

before(async () => {
  silenceRouteLogging();
  ({ POST } = (await loadStripeWebhookRoute()) as unknown as {
    POST: PostHandler;
  });
});

after(() => {
  restoreRouteLogging();
});

beforeEach(() => {
  resetHarness();
  logLines.length = 0;
  process.env.STRIPE_WEBHOOK_SECRET = WEBHOOK_SECRET;
});

afterEach(() => {
  if (realWebhookSecret === undefined) {
    delete process.env.STRIPE_WEBHOOK_SECRET;
  } else {
    process.env.STRIPE_WEBHOOK_SECRET = realWebhookSecret;
  }
});

/** Sends a signed request and returns the response. */
function signedPost(body: unknown): Promise<Response> {
  return POST(webhookRequest(body, { "stripe-signature": "t=1,v1=deadbeef" }));
}

/** No handler ran at all — the request was refused or deliberately skipped. */
function assertNoHandlerRan(): void {
  assert.equal(calls.licensePurchase.length, 0, "license handler ran");
  assert.equal(calls.creditPurchase.length, 0, "credit handler ran");
  assert.equal(calls.chargeRefunded.length, 0, "refund handler ran");
}

describe("POST /api/webhooks/stripe — the signature gate", () => {
  test("refuses a request with no stripe-signature header", async () => {
    const response = await POST(webhookRequest(checkoutEvent()));

    assert.equal(response.status, 400);
    assert.deepEqual(await readJson(response), { error: "No signature" });
    assert.equal(
      calls.constructEvent.length,
      0,
      "the verifier must not be reached without a signature",
    );
    assertNoHandlerRan();
  });

  test("refuses every request when STRIPE_WEBHOOK_SECRET is not configured", async () => {
    delete process.env.STRIPE_WEBHOOK_SECRET;
    behaviour.verifiedEvent = checkoutEvent({ purchaseType: "license" });

    const response = await signedPost(behaviour.verifiedEvent);

    // 500, not 400 and not 200: the deployment is broken, so Stripe must keep
    // retrying. A 2xx here would drop a real purchase for good.
    assert.equal(response.status, 500);
    assert.deepEqual(await readJson(response), {
      error: "Webhook secret not configured",
    });
    assert.equal(
      calls.constructEvent.length,
      0,
      "the verifier must not be reached with no secret",
    );
    assertNoHandlerRan();
  });

  test("refuses a body whose signature does not verify", async () => {
    behaviour.verifiedEvent = null;
    behaviour.verifyError = "No signatures found matching the expected signature";

    const response = await signedPost(
      checkoutEvent({ purchaseType: "license" }),
    );

    assert.equal(response.status, 400);
    assert.deepEqual(await readJson(response), {
      error: "Webhook signature verification failed",
    });
    assert.equal(calls.constructEvent.length, 1);
    assertNoHandlerRan();
  });

  test("verifies the raw body bytes, not a re-serialized copy", async () => {
    // Stripe signs the exact bytes it sent. This body is valid JSON that no
    // JSON.stringify round-trip reproduces: the key order differs from the
    // object literal, and the whitespace is its own. If the route parsed the
    // body and handed the verifier a re-encoded string, every real signature
    // would fail in production while the tests still passed.
    const rawBody =
      '{  "type" : "checkout.session.completed" ,\n  "id":"evt_raw"  }';
    behaviour.verifiedEvent = checkoutEvent({ purchaseType: "credits" });

    const response = await POST(
      webhookRequest(rawBody, { "stripe-signature": "t=99,v1=abc123" }),
    );

    assert.equal(response.status, 200);
    assert.equal(calls.constructEvent.length, 1);
    assert.deepEqual(calls.constructEvent[0], {
      body: rawBody,
      signature: "t=99,v1=abc123",
      secret: WEBHOOK_SECRET,
    });
  });

  test("dispatches on the VERIFIED event, never on the posted body", async () => {
    // The body claims a license purchase. The verifier returns a credits
    // event. Only the verified object may decide anything, so the credit
    // handler must run and the license handler must not.
    behaviour.verifiedEvent = checkoutEvent({
      purchaseType: "credits",
      sessionId: "cs_verified",
    });

    const response = await POST(
      webhookRequest(
        { type: "checkout.session.completed", metadata: { purchase_type: "license" } },
        { "stripe-signature": "t=1,v1=deadbeef" },
      ),
    );

    assert.equal(response.status, 200);
    assert.equal(calls.licensePurchase.length, 0);
    assert.equal(calls.creditPurchase.length, 1);
    assert.equal(
      (calls.creditPurchase[0].session as { id: string }).id,
      "cs_verified",
    );
  });
});

describe("POST /api/webhooks/stripe — the payment gate", () => {
  test("skips an unpaid checkout.session.completed without granting", async () => {
    behaviour.verifiedEvent = checkoutEvent({
      purchaseType: "license",
      paymentStatus: "unpaid",
    });

    const response = await signedPost(behaviour.verifiedEvent);

    // 200 so Stripe stops retrying: nothing is wrong, the money simply has not
    // arrived. The paid event follows later.
    assert.equal(response.status, 200);
    assert.deepEqual(await readJson(response), { received: true });
    assertNoHandlerRan();
  });

  test("skips an unpaid credits session too", async () => {
    behaviour.verifiedEvent = checkoutEvent({
      purchaseType: "credits",
      paymentStatus: "unpaid",
      sessionId: "cs_pending",
    });

    const response = await signedPost(behaviour.verifiedEvent);

    assert.equal(response.status, 200);
    assertNoHandlerRan();
  });
});

describe("POST /api/webhooks/stripe — dispatch", () => {
  test("hands a paid license session to handleLicensePurchase", async () => {
    behaviour.verifiedEvent = checkoutEvent({
      purchaseType: "license",
      sessionId: "cs_license_1",
    });

    const response = await signedPost(behaviour.verifiedEvent);

    assert.equal(response.status, 200);
    assert.deepEqual(await readJson(response), { received: true });
    assert.equal(calls.licensePurchase.length, 1);
    assert.equal(
      (calls.licensePurchase[0] as { id: string }).id,
      "cs_license_1",
      "the handler must receive the session object, not the event",
    );
    assert.equal(calls.creditPurchase.length, 0);
    assert.equal(calls.chargeRefunded.length, 0);
  });

  test("hands a paid credits session the session AND the event identity", async () => {
    // `handleCreditPurchase` keys its idempotency record on the event id, so
    // these three arguments are what stops Stripe's retry from granting the
    // pack twice.
    behaviour.verifiedEvent = checkoutEvent({
      id: "evt_credits_9",
      purchaseType: "credits",
      sessionId: "cs_credits_9",
    });

    const response = await signedPost(behaviour.verifiedEvent);

    assert.equal(response.status, 200);
    assert.equal(calls.creditPurchase.length, 1);
    assert.equal(
      (calls.creditPurchase[0].session as { id: string }).id,
      "cs_credits_9",
    );
    assert.equal(calls.creditPurchase[0].eventId, "evt_credits_9");
    assert.equal(
      calls.creditPurchase[0].eventType,
      "checkout.session.completed",
    );
    assert.equal(calls.licensePurchase.length, 0);
  });

  test("treats checkout.session.async_payment_succeeded as a purchase", async () => {
    // A delayed payment method settles with this event, not with
    // `completed`. Ignoring it loses the purchase entirely.
    behaviour.verifiedEvent = checkoutEvent({
      id: "evt_async_1",
      type: "checkout.session.async_payment_succeeded",
      purchaseType: "credits",
      sessionId: "cs_async_1",
    });

    const response = await signedPost(behaviour.verifiedEvent);

    assert.equal(response.status, 200);
    assert.equal(calls.creditPurchase.length, 1);
    assert.equal(
      calls.creditPurchase[0].eventType,
      "checkout.session.async_payment_succeeded",
      "the event type is part of the idempotency record and must be passed through",
    );
  });

  test("grants nothing for an unknown purchase_type", async () => {
    behaviour.verifiedEvent = checkoutEvent({ purchaseType: "subscription" });

    const response = await signedPost(behaviour.verifiedEvent);

    assert.equal(response.status, 200);
    assert.deepEqual(await readJson(response), { received: true });
    assertNoHandlerRan();
  });

  test("grants nothing when the session carries no metadata at all", async () => {
    behaviour.verifiedEvent = checkoutEvent({ metadata: undefined });

    const response = await signedPost(behaviour.verifiedEvent);

    assert.equal(response.status, 200);
    assertNoHandlerRan();
  });

  test("grants nothing for an event type it does not handle", async () => {
    behaviour.verifiedEvent = {
      id: "evt_invoice_1",
      type: "invoice.paid",
      data: { object: { id: "in_1" } },
    };

    const response = await signedPost(behaviour.verifiedEvent);

    assert.equal(response.status, 200);
    assert.deepEqual(await readJson(response), { received: true });
    assertNoHandlerRan();
  });

  test("grants nothing on checkout.session.async_payment_failed", async () => {
    behaviour.verifiedEvent = checkoutEvent({
      type: "checkout.session.async_payment_failed",
      purchaseType: "credits",
      sessionId: "cs_failed_1",
      paymentStatus: "unpaid",
    });

    const response = await signedPost(behaviour.verifiedEvent);

    assert.equal(response.status, 200);
    assertNoHandlerRan();
    assert.ok(
      logLines.some((line) => line.includes("cs_failed_1")),
      "the failed session id must reach the log for manual review",
    );
  });
});

describe("POST /api/webhooks/stripe — handler faults", () => {
  test("answers 500 when the license handler throws, so Stripe retries", async () => {
    behaviour.verifiedEvent = checkoutEvent({ purchaseType: "license" });
    behaviour.licenseError = new Error("database is down");

    const response = await signedPost(behaviour.verifiedEvent);

    assert.equal(response.status, 500);
    assert.deepEqual(await readJson(response), {
      error: "Failed to process license purchase",
    });
    assert.equal(calls.licensePurchase.length, 1);
  });

  test("answers 500 when the credit handler throws, so Stripe retries", async () => {
    behaviour.verifiedEvent = checkoutEvent({ purchaseType: "credits" });
    behaviour.creditError = new Error("credit ledger write failed");

    const response = await signedPost(behaviour.verifiedEvent);

    assert.equal(response.status, 500);
    assert.deepEqual(await readJson(response), {
      error: "Failed to process credit purchase",
    });
    assert.equal(calls.creditPurchase.length, 1);
  });
});

describe("POST /api/webhooks/stripe — refunds", () => {
  test("hands a charge.refunded event the charge object", async () => {
    behaviour.verifiedEvent = refundEvent({ chargeId: "ch_refund_7" });

    const response = await signedPost(behaviour.verifiedEvent);

    assert.equal(response.status, 200);
    assert.deepEqual(await readJson(response), { received: true });
    assert.equal(calls.chargeRefunded.length, 1);
    assert.equal(
      (calls.chargeRefunded[0] as { id: string }).id,
      "ch_refund_7",
      "the handler must receive the charge, not the event",
    );
    assert.equal(calls.licensePurchase.length, 0);
  });

  test("still answers 200 when the refund handler throws", async () => {
    // Deliberate, and the opposite of the purchase paths: a refund fault is
    // usually permanent, and a 5xx would make Stripe retry it forever. The
    // route logs instead and leaves the case for manual review.
    behaviour.verifiedEvent = refundEvent({ chargeId: "ch_refund_8" });
    behaviour.refundError = new Error("license row already gone");

    const response = await signedPost(behaviour.verifiedEvent);

    assert.equal(response.status, 200);
    assert.deepEqual(await readJson(response), { received: true });
    assert.equal(calls.chargeRefunded.length, 1);
    assert.ok(
      logLines.some((line) =>
        line.includes("Error processing refund"),
      ),
      "a swallowed refund fault must still be logged",
    );
  });
});
