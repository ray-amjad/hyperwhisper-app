/**
 * The two credit REST endpoints, driven as HTTP.
 *
 * `POST /api/checkout/credits` is the money-in path: it prices the purchase
 * and hands Stripe the line items and the metadata the webhook later reads. A
 * wrong number here charges the wrong amount, and a wrong `license_key` in the
 * metadata grants the credits to the wrong wallet.
 *
 * `GET/POST /api/license/credits` is the money-out path: HyperWhisper Cloud
 * reads the balance here and deducts against it on every transcription. A
 * missed status gate lets a revoked key keep spending.
 *
 * So the tests below assert what a caller actually reads — the status, the
 * reply body, and the exact arguments the route handed Stripe and the database
 * — not that a mock was called. Collaborators are replaced at the module
 * boundary in `credit-routes-harness.ts`; the route modules are the real ones.
 */
import assert from "node:assert/strict";
import { after, afterEach, before, beforeEach, describe, test } from "node:test";

import {
  accountKeyRow,
  behaviour,
  calls,
  getRequest,
  loadCheckoutCreditsRoute,
  loadLicenseCreditsRoute,
  onlySessionCreate,
  postRequest,
  resetHarness,
  restoreRouteLogging,
  silenceRouteLogging,
  storeRow,
} from "./credit-routes-harness";

const CHECKOUT_PATH = "/api/checkout/credits";
const CREDITS_PATH = "/api/license/credits";

/** A made-up key string. Only the fake database in the harness knows it. */
const GRANTED_KEY = "HW-TEST-0000-0001";

type JsonBody = Record<string, unknown>;

async function readJson(response: Response): Promise<JsonBody> {
  return (await response.json()) as JsonBody;
}

interface LineItem {
  price_data: {
    currency: string;
    unit_amount: number;
    product_data: { name: string; description: string; tax_code: string };
  };
  quantity: number;
}

function lineItems(session: Record<string, unknown>): LineItem[] {
  return session.line_items as LineItem[];
}

const realSiteUrl = process.env.NEXT_PUBLIC_SITE_URL;

before(() => {
  silenceRouteLogging();
});

after(() => {
  restoreRouteLogging();
});

beforeEach(() => {
  resetHarness();
  delete process.env.NEXT_PUBLIC_SITE_URL;
});

afterEach(() => {
  if (realSiteUrl === undefined) delete process.env.NEXT_PUBLIC_SITE_URL;
  else process.env.NEXT_PUBLIC_SITE_URL = realSiteUrl;
});

describe("POST /api/checkout/credits — amount validation", () => {
  test("refuses a non-numeric amount before it reaches Stripe", async () => {
    const { POST } = await loadCheckoutCreditsRoute();

    const response = await POST(postRequest(CHECKOUT_PATH, { amount: "25" }));
    const body = await readJson(response);

    assert.equal(response.status, 400);
    assert.equal(body.error, "amount must be a finite number");
    assert.deepEqual(calls.sessionCreate, []);
  });

  test("refuses a fractional dollar amount", async () => {
    const { POST } = await loadCheckoutCreditsRoute();

    const response = await POST(postRequest(CHECKOUT_PATH, { amount: 5.5 }));
    const body = await readJson(response);

    assert.equal(response.status, 400);
    assert.equal(body.error, "amount must be a whole number of dollars");
    assert.deepEqual(calls.sessionCreate, []);
  });

  test("refuses an amount below the $5 floor", async () => {
    const { POST } = await loadCheckoutCreditsRoute();

    const response = await POST(postRequest(CHECKOUT_PATH, { amount: 4 }));
    const body = await readJson(response);

    assert.equal(response.status, 400);
    assert.equal(body.error, "amount must be between 5 and 500 dollars");
    assert.deepEqual(calls.sessionCreate, []);
  });

  test("refuses an amount above the $500 ceiling", async () => {
    const { POST } = await loadCheckoutCreditsRoute();

    const response = await POST(postRequest(CHECKOUT_PATH, { amount: 501 }));
    const body = await readJson(response);

    assert.equal(response.status, 400);
    assert.equal(body.error, "amount must be between 5 and 500 dollars");
    assert.deepEqual(calls.sessionCreate, []);
  });

  test("accepts both ends of the allowed range", async () => {
    const { POST } = await loadCheckoutCreditsRoute();

    for (const amount of [5, 500]) {
      resetHarness();
      const response = await POST(postRequest(CHECKOUT_PATH, { amount }));

      assert.equal(response.status, 200);
      assert.equal(
        onlySessionCreate().metadata &&
          (onlySessionCreate().metadata as Record<string, string>).credit_amount,
        (amount * 1000).toString(),
      );
    }
  });

  test("answers 500 when the body is not JSON at all", async () => {
    const { POST } = await loadCheckoutCreditsRoute();

    const response = await POST(postRequest(CHECKOUT_PATH, "not json"));
    const body = await readJson(response);

    assert.equal(response.status, 500);
    assert.equal(body.error, "Failed to create checkout session");
    assert.deepEqual(calls.sessionCreate, []);
  });
});

describe("POST /api/checkout/credits — pricing", () => {
  test("prices credits at 1000 per dollar and the fee at 6%, as two line items", async () => {
    const { POST } = await loadCheckoutCreditsRoute();

    const response = await POST(postRequest(CHECKOUT_PATH, { amount: 25 }));
    const body = await readJson(response);
    const session = onlySessionCreate();
    const items = lineItems(session);

    assert.equal(response.status, 200);
    assert.equal(body.checkoutUrl, behaviour.sessionUrl);
    assert.equal(items.length, 2);

    // Credits line: $25 -> 2500 cents, and 25,000 credits.
    assert.equal(items[0].price_data.unit_amount, 2500);
    assert.equal(items[0].price_data.currency, "usd");
    assert.equal(items[0].price_data.product_data.description, "25,000 credits");
    assert.equal(items[0].quantity, 1);

    // Fee line: 6% of 2500 cents = 150 cents. It is revenue, never credits.
    assert.equal(items[1].price_data.unit_amount, 150);
    assert.equal(items[1].price_data.currency, "usd");
    assert.equal(items[1].quantity, 1);

    // The webhook grants off `credit_amount` and reconciles off `fee_cents`.
    const metadata = session.metadata as Record<string, string>;

    assert.equal(metadata.purchase_type, "credits");
    assert.equal(metadata.credit_amount, "25000");
    assert.equal(metadata.fee_cents, "150");
  });

  test("gives both line items a tax code, which Managed Payments requires", async () => {
    const { POST } = await loadCheckoutCreditsRoute();

    await POST(postRequest(CHECKOUT_PATH, { amount: 5 }));
    const session = onlySessionCreate();

    for (const item of lineItems(session)) {
      assert.equal(item.price_data.product_data.tax_code, "txcd_10000000");
    }
    assert.deepEqual(session.managed_payments, { enabled: true });
  });

  test("carries the credit count, not the dollar amount, into the success URL", async () => {
    process.env.NEXT_PUBLIC_SITE_URL = "https://staging.hyperwhisper.test";
    const { POST } = await loadCheckoutCreditsRoute();

    await POST(postRequest(CHECKOUT_PATH, { amount: 7 }));
    const session = onlySessionCreate();

    assert.equal(
      session.success_url,
      "https://staging.hyperwhisper.test/purchase-success?session_id={CHECKOUT_SESSION_ID}&credits=7000",
    );
    assert.equal(session.cancel_url, "https://staging.hyperwhisper.test/credits");
  });

  test("falls back to the production site URL when the env var is unset", async () => {
    const { POST } = await loadCheckoutCreditsRoute();

    await POST(postRequest(CHECKOUT_PATH, { amount: 10 }));
    const session = onlySessionCreate();

    assert.equal(
      session.success_url,
      "https://hyperwhisper.com/purchase-success?session_id={CHECKOUT_SESSION_ID}&credits=10000",
    );
    assert.equal(session.cancel_url, "https://hyperwhisper.com/credits");
  });
});

describe("POST /api/checkout/credits — the mint path", () => {
  test("omits license_key from the metadata, so the webhook mints a key", async () => {
    const { POST } = await loadCheckoutCreditsRoute();

    const response = await POST(postRequest(CHECKOUT_PATH, { amount: 5 }));
    const session = onlySessionCreate();
    const metadata = session.metadata as Record<string, string>;

    assert.equal(response.status, 200);
    assert.equal("license_key" in metadata, false);
    assert.equal(session.customer_creation, "always");
    assert.equal("customer" in session, false);
    // A guest has no key to look up, so the database is never asked.
    assert.deepEqual(calls.findAccountByKey, []);
  });

  test("prefills a well-formed guest email, trimmed and lower-cased", async () => {
    const { POST } = await loadCheckoutCreditsRoute();

    await POST(
      postRequest(CHECKOUT_PATH, { amount: 5, email: "  Buyer@Example.COM " }),
    );

    assert.equal(onlySessionCreate().customer_email, "buyer@example.com");
  });

  test("drops an email that is not an address, rather than sending it to Stripe", async () => {
    const { POST } = await loadCheckoutCreditsRoute();

    await POST(postRequest(CHECKOUT_PATH, { amount: 5, email: "not-an-email" }));

    assert.equal("customer_email" in onlySessionCreate(), false);
  });

  test("treats a whitespace-only license key as absent, and still mints", async () => {
    const { POST } = await loadCheckoutCreditsRoute();

    const response = await POST(
      postRequest(CHECKOUT_PATH, {
        amount: 5,
        licenseKey: "   ",
        email: "guest@example.com",
      }),
    );
    const session = onlySessionCreate();
    const metadata = session.metadata as Record<string, string>;

    assert.equal(response.status, 200);
    assert.equal("license_key" in metadata, false);
    // The blank key must not become a lookup that then fails as "Invalid".
    assert.deepEqual(calls.findAccountByKey, []);
    assert.equal(session.customer_creation, "always");
    // A blank key must not suppress the guest email prefill either: the mint
    // path is fully in force, so the key Stripe's webhook mints is emailed.
    assert.equal(session.customer_email, "guest@example.com");
  });
});

describe("POST /api/checkout/credits — the top-up path", () => {
  test("refuses a key the database does not hold", async () => {
    const { POST } = await loadCheckoutCreditsRoute();

    const response = await POST(
      postRequest(CHECKOUT_PATH, { amount: 5, licenseKey: "HW-NOPE-0000-0000" }),
    );
    const body = await readJson(response);

    assert.equal(response.status, 400);
    assert.equal(body.error, "Invalid license key");
    assert.deepEqual(calls.sessionCreate, []);
  });

  test("refuses a revoked key, so credits never land on an unusable wallet", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY, status: "revoked" }));
    const { POST } = await loadCheckoutCreditsRoute();

    const response = await POST(
      postRequest(CHECKOUT_PATH, { amount: 5, licenseKey: GRANTED_KEY }),
    );
    const body = await readJson(response);

    assert.equal(response.status, 400);
    assert.equal(body.error, "License is revoked");
    assert.deepEqual(calls.sessionCreate, []);
    assert.deepEqual(calls.customerCreate, []);
  });

  test("refuses a key with no email, which has nowhere to send the receipt", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY, email: "" }));
    const { POST } = await loadCheckoutCreditsRoute();

    const response = await POST(
      postRequest(CHECKOUT_PATH, { amount: 5, licenseKey: GRANTED_KEY }),
    );
    const body = await readJson(response);

    assert.equal(response.status, 400);
    assert.equal(body.error, "License has no email associated");
    assert.deepEqual(calls.sessionCreate, []);
  });

  test("reuses the customer already cached on the license", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY, stripeCustomerId: "cus_cached" }));
    const { POST } = await loadCheckoutCreditsRoute();

    const response = await POST(
      postRequest(CHECKOUT_PATH, { amount: 5, licenseKey: GRANTED_KEY }),
    );
    const session = onlySessionCreate();

    assert.equal(response.status, 200);
    assert.equal(session.customer, "cus_cached");
    assert.equal("customer_creation" in session, false);
    // Nothing to look up and nothing to write back.
    assert.deepEqual(calls.customerList, []);
    assert.deepEqual(calls.customerCreate, []);
    assert.deepEqual(calls.updateAccountKey, []);
  });

  test("finds the customer by email when the license has none cached, and caches it", async () => {
    storeRow(
      accountKeyRow({
        key: GRANTED_KEY,
        email: "topup@example.com",
        stripeCustomerId: null,
      }),
    );
    behaviour.existingCustomers = [{ id: "cus_found" }];
    const { POST } = await loadCheckoutCreditsRoute();

    const response = await POST(
      postRequest(CHECKOUT_PATH, { amount: 5, licenseKey: GRANTED_KEY }),
    );

    assert.equal(response.status, 200);
    assert.deepEqual(calls.customerList, [
      { email: "topup@example.com", limit: 1 },
    ]);
    assert.deepEqual(calls.customerCreate, []);
    assert.deepEqual(calls.updateAccountKey, [
      { id: "key_row_1", updates: { stripeCustomerId: "cus_found" } },
    ]);
    assert.equal(onlySessionCreate().customer, "cus_found");
  });

  test("creates the customer when Stripe knows the email, and caches that", async () => {
    storeRow(
      accountKeyRow({
        key: GRANTED_KEY,
        email: "fresh@example.com",
        stripeCustomerId: null,
      }),
    );
    behaviour.existingCustomers = [];
    behaviour.createdCustomerId = "cus_new";
    const { POST } = await loadCheckoutCreditsRoute();

    const response = await POST(
      postRequest(CHECKOUT_PATH, { amount: 5, licenseKey: GRANTED_KEY }),
    );

    assert.equal(response.status, 200);
    assert.deepEqual(calls.customerCreate, [
      {
        email: "fresh@example.com",
        metadata: { license_key: GRANTED_KEY },
      },
    ]);
    assert.deepEqual(calls.updateAccountKey, [
      { id: "key_row_1", updates: { stripeCustomerId: "cus_new" } },
    ]);
    assert.equal(onlySessionCreate().customer, "cus_new");
  });

  test("trims the key before the lookup and before it reaches the metadata", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY }));
    const { POST } = await loadCheckoutCreditsRoute();

    const response = await POST(
      postRequest(CHECKOUT_PATH, {
        amount: 5,
        licenseKey: `  ${GRANTED_KEY}\n`,
      }),
    );
    const metadata = onlySessionCreate().metadata as Record<string, string>;

    assert.equal(response.status, 200);
    assert.deepEqual(calls.findAccountByKey, [GRANTED_KEY]);
    // The webhook matches this string against the stored key, so an untrimmed
    // one would credit nobody.
    assert.equal(metadata.license_key, GRANTED_KEY);
  });

  test("ignores a supplied email on a top-up, because the license owns the address", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY, stripeCustomerId: "cus_cached" }));
    const { POST } = await loadCheckoutCreditsRoute();

    await POST(
      postRequest(CHECKOUT_PATH, {
        amount: 5,
        licenseKey: GRANTED_KEY,
        email: "someone.else@example.com",
      }),
    );

    assert.equal("customer_email" in onlySessionCreate(), false);
    assert.equal(onlySessionCreate().customer, "cus_cached");
  });
});

describe("POST /api/checkout/credits — Stripe faults", () => {
  test("answers 500 with the reason when Stripe returns no checkout URL", async () => {
    behaviour.sessionUrl = null;
    const { POST } = await loadCheckoutCreditsRoute();

    const response = await POST(postRequest(CHECKOUT_PATH, { amount: 5 }));
    const body = await readJson(response);

    assert.equal(response.status, 500);
    assert.equal(body.error, "Failed to create checkout session");
    assert.equal(body.details, "No checkout URL returned from Stripe");
  });

  test("answers 500 with the Stripe message when the session call throws", async () => {
    behaviour.sessionError = new Error("card_declined at session create");
    const { POST } = await loadCheckoutCreditsRoute();

    const response = await POST(postRequest(CHECKOUT_PATH, { amount: 5 }));
    const body = await readJson(response);

    assert.equal(response.status, 500);
    assert.equal(body.error, "Failed to create checkout session");
    assert.equal(body.details, "card_declined at session create");
  });

  test("reports a non-Error throw as unknown, rather than crashing the route", async () => {
    behaviour.sessionError = "stripe exploded";
    const { POST } = await loadCheckoutCreditsRoute();

    const response = await POST(postRequest(CHECKOUT_PATH, { amount: 5 }));
    const body = await readJson(response);

    assert.equal(response.status, 500);
    assert.equal(body.details, "Unknown error");
  });
});

describe("GET /api/license/credits", () => {
  test("requires a license_key query parameter", async () => {
    const { GET } = await loadLicenseCreditsRoute();

    const response = await GET(getRequest(CREDITS_PATH));
    const body = await readJson(response);

    assert.equal(response.status, 400);
    assert.equal(body.error, "license_key is required");
    assert.deepEqual(calls.findAccountByKey, []);
  });

  test("treats an empty license_key as missing", async () => {
    const { GET } = await loadLicenseCreditsRoute();

    const response = await GET(getRequest(`${CREDITS_PATH}?license_key=`));
    const body = await readJson(response);

    assert.equal(response.status, 400);
    assert.equal(body.error, "license_key is required");
    assert.deepEqual(calls.findAccountByKey, []);
  });

  test("reports the balance and the Stripe customer for a granted key", async () => {
    storeRow(
      accountKeyRow({
        key: GRANTED_KEY,
        userId: "user_42",
        stripeCustomerId: "cus_42",
      }),
    );
    behaviour.creditBalance = 1_337;
    const { GET } = await loadLicenseCreditsRoute();

    const response = await GET(
      getRequest(`${CREDITS_PATH}?license_key=${GRANTED_KEY}`),
    );
    const body = await readJson(response);

    assert.equal(response.status, 200);
    assert.equal(body.credits, 1_337);
    assert.equal(body.stripe_customer_id, "cus_42");
    // The balance belongs to the user, not to the key.
    assert.deepEqual(calls.getCreditBalance, ["user_42"]);
  });

  test("refuses a key the database does not hold", async () => {
    const { GET } = await loadLicenseCreditsRoute();

    const response = await GET(
      getRequest(`${CREDITS_PATH}?license_key=HW-NOPE-0000-0000`),
    );
    const body = await readJson(response);

    assert.equal(response.status, 400);
    assert.equal(body.error, "License key not found");
    assert.deepEqual(calls.getCreditBalance, []);
  });

  test("refuses a revoked key without reading its balance", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY, status: "revoked" }));
    const { GET } = await loadLicenseCreditsRoute();

    const response = await GET(
      getRequest(`${CREDITS_PATH}?license_key=${GRANTED_KEY}`),
    );
    const body = await readJson(response);

    assert.equal(response.status, 400);
    assert.equal(body.error, "License is revoked");
    assert.deepEqual(calls.getCreditBalance, []);
  });

  test("trims the key before the lookup", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY }));
    const { GET } = await loadLicenseCreditsRoute();

    const response = await GET(
      getRequest(`${CREDITS_PATH}?license_key=${encodeURIComponent(`  ${GRANTED_KEY}  `)}`),
    );

    assert.equal(response.status, 200);
    assert.deepEqual(calls.findAccountByKey, [GRANTED_KEY]);
  });

  test("answers 500 when the balance read fails", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY }));
    behaviour.balanceError = new Error("connection terminated");
    const { GET } = await loadLicenseCreditsRoute();

    const response = await GET(
      getRequest(`${CREDITS_PATH}?license_key=${GRANTED_KEY}`),
    );
    const body = await readJson(response);

    assert.equal(response.status, 500);
    assert.equal(body.error, "Failed to get credit balance");
  });
});

describe("POST /api/license/credits", () => {
  test("requires a license_key", async () => {
    const { POST } = await loadLicenseCreditsRoute();

    const response = await POST(postRequest(CREDITS_PATH, { amount: 10 }));
    const body = await readJson(response);

    assert.equal(response.status, 400);
    assert.equal(body.error, "license_key is required");
    assert.deepEqual(calls.deductCreditBalance, []);
  });

  test("refuses a non-string license_key", async () => {
    const { POST } = await loadLicenseCreditsRoute();

    const response = await POST(
      postRequest(CREDITS_PATH, { license_key: 12345, amount: 10 }),
    );
    const body = await readJson(response);

    assert.equal(response.status, 400);
    assert.equal(body.error, "license_key is required");
    assert.deepEqual(calls.findAccountByKey, []);
  });

  test("refuses a zero or negative amount before any lookup", async () => {
    const { POST } = await loadLicenseCreditsRoute();

    for (const amount of [0, -5]) {
      resetHarness();
      const response = await POST(
        postRequest(CREDITS_PATH, { license_key: GRANTED_KEY, amount }),
      );
      const body = await readJson(response);

      assert.equal(response.status, 400);
      assert.equal(body.error, "amount must be a finite positive number");
      assert.deepEqual(calls.findAccountByKey, []);
    }
  });

  test("refuses an amount finer than the 2-decimal column scale", async () => {
    const { POST } = await loadLicenseCreditsRoute();

    const response = await POST(
      postRequest(CREDITS_PATH, { license_key: GRANTED_KEY, amount: 0.005 }),
    );
    const body = await readJson(response);

    // Postgres would round 0.005 away and deduct nothing.
    assert.equal(response.status, 400);
    assert.equal(body.error, "amount must have at most 2 decimal places");
    assert.deepEqual(calls.deductCreditBalance, []);
  });

  test("refuses an amount above the per-call ceiling", async () => {
    const { POST } = await loadLicenseCreditsRoute();

    const response = await POST(
      postRequest(CREDITS_PATH, { license_key: GRANTED_KEY, amount: 1_000_001 }),
    );
    const body = await readJson(response);

    assert.equal(response.status, 400);
    assert.equal(body.error, "amount must be 1000000 or less");
    assert.deepEqual(calls.deductCreditBalance, []);
  });

  test("deducts from the user behind the key and reports the new balance", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY, userId: "user_42" }));
    behaviour.balanceAfterDeduct = 890.5;
    const { POST } = await loadLicenseCreditsRoute();

    const response = await POST(
      postRequest(CREDITS_PATH, { license_key: GRANTED_KEY, amount: 109.5 }),
    );
    const body = await readJson(response);

    assert.equal(response.status, 200);
    assert.equal(body.credits_remaining, 890.5);
    assert.equal(body.credits_deducted, 109.5);
    assert.deepEqual(calls.deductCreditBalance, [
      { userId: "user_42", amount: 109.5 },
    ]);
  });

  test("refuses a key the database does not hold", async () => {
    const { POST } = await loadLicenseCreditsRoute();

    const response = await POST(
      postRequest(CREDITS_PATH, {
        license_key: "HW-NOPE-0000-0000",
        amount: 10,
      }),
    );
    const body = await readJson(response);

    assert.equal(response.status, 400);
    assert.equal(body.error, "License key not found");
    assert.deepEqual(calls.deductCreditBalance, []);
  });

  test("refuses a revoked key, so a revoked wallet cannot keep spending", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY, status: "revoked" }));
    const { POST } = await loadLicenseCreditsRoute();

    const response = await POST(
      postRequest(CREDITS_PATH, { license_key: GRANTED_KEY, amount: 10 }),
    );
    const body = await readJson(response);

    assert.equal(response.status, 400);
    assert.equal(body.error, "License is revoked");
    assert.deepEqual(calls.deductCreditBalance, []);
  });

  test("trims the key before the lookup", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY }));
    const { POST } = await loadLicenseCreditsRoute();

    const response = await POST(
      postRequest(CREDITS_PATH, {
        license_key: `  ${GRANTED_KEY}  `,
        amount: 10,
      }),
    );

    assert.equal(response.status, 200);
    assert.deepEqual(calls.findAccountByKey, [GRANTED_KEY]);
  });

  test("answers 409 when the decrement itself fails, so the caller retries", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY }));
    behaviour.deductError = new Error("deadlock detected");
    const { POST } = await loadLicenseCreditsRoute();

    const response = await POST(
      postRequest(CREDITS_PATH, { license_key: GRANTED_KEY, amount: 10 }),
    );
    const body = await readJson(response);

    // 409 and not 500: a retry is the right move, and the caller distinguishes
    // the two.
    assert.equal(response.status, 409);
    assert.equal(body.error, "Failed to deduct credits. Please retry.");
  });

  test("answers 500 when the body is not JSON at all", async () => {
    const { POST } = await loadLicenseCreditsRoute();

    const response = await POST(postRequest(CREDITS_PATH, "not json"));
    const body = await readJson(response);

    assert.equal(response.status, 500);
    assert.equal(body.error, "Failed to deduct credits");
    assert.deepEqual(calls.deductCreditBalance, []);
  });

  test("answers 500 when the lookup itself throws", async () => {
    behaviour.lookupError = new Error("connection terminated");
    const { POST } = await loadLicenseCreditsRoute();

    const response = await POST(
      postRequest(CREDITS_PATH, { license_key: GRANTED_KEY, amount: 10 }),
    );
    const body = await readJson(response);

    assert.equal(response.status, 500);
    assert.equal(body.error, "Failed to deduct credits");
    assert.deepEqual(calls.deductCreditBalance, []);
  });
});
