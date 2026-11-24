/**
 * #2460: a credit top-up after an admin email move must bill a Stripe
 * customer at the licence's CURRENT email, never the customer cached on the
 * key while it was at the old address.
 *
 * Drives the REAL `/api/checkout/credits` route through
 * `tests/credit-routes-harness.ts`.
 */
import { test, after } from "node:test";
import assert from "node:assert/strict";

import {
  accountKeyRow,
  behaviour,
  calls,
  loadCheckoutCreditsRoute,
  onlySessionCreate,
  postRequest,
  resetHarness,
  restoreRouteLogging,
  silenceRouteLogging,
  storeRow,
} from "./credit-routes-harness";

after(restoreRouteLogging);

const OLD_EMAIL = "hunter-nx-checkout-old@example.test";
const NEW_EMAIL = "hunter-nx-checkout-new@example.test";

async function topUp(licenseKey: string) {
  const { POST } = await loadCheckoutCreditsRoute();
  return POST(postRequest("/api/checkout/credits", { licenseKey, amount: 10 }));
}

test("top-up checkout bills the Stripe customer of the licence's CURRENT email", async () => {
  resetHarness();
  silenceRouteLogging();

  // cus_old was created (and cached on the key) while the account was at the
  // old address. An admin then moved the account (updateCustomerEmail):
  // account_keys.email is the new address, stripe_customer_id still cus_old.
  const stripeCustomerEmails: Record<string, string> = {
    cus_old: OLD_EMAIL,
    cus_created: NEW_EMAIL,
  };
  behaviour.customerEmails = stripeCustomerEmails;
  const license = storeRow(
    accountKeyRow({
      key: "HW-TEST-0000-0042",
      email: NEW_EMAIL,
      stripeCustomerId: "cus_old",
    }),
  );
  behaviour.existingCustomers = [];
  behaviour.createdCustomerId = "cus_created";

  const res = await topUp(license.key);
  assert.equal(res.status, 200);

  const session = onlySessionCreate();
  const billedEmail =
    typeof session.customer === "string"
      ? stripeCustomerEmails[session.customer]
      : (session.customer_email as string | undefined);

  // The receipt/invoice goes to the account's current owner.
  assert.equal(billedEmail, license.email);
  // The new address's customer was looked up, created, and cached in place
  // of the stale one, so the next top-up and the portal use it.
  assert.deepEqual(calls.customerList, [{ email: NEW_EMAIL, limit: 1 }]);
  assert.deepEqual(calls.updateAccountKey, [
    { id: license.id, updates: { stripeCustomerId: "cus_created" } },
  ]);
});

test("a stale cached customer gives way to an existing customer at the new email", async () => {
  resetHarness();
  silenceRouteLogging();
  behaviour.customerEmails = { cus_old: OLD_EMAIL };
  const license = storeRow(
    accountKeyRow({ email: NEW_EMAIL, stripeCustomerId: "cus_old" }),
  );
  behaviour.existingCustomers = [{ id: "cus_new_existing" }];

  assert.equal((await topUp(license.key)).status, 200);
  assert.equal(onlySessionCreate().customer, "cus_new_existing");
  assert.deepEqual(calls.customerCreate, []);
});

test("a deleted cached customer is replaced, not billed", async () => {
  resetHarness();
  silenceRouteLogging();
  behaviour.customerEmails = { cus_gone: null };
  const license = storeRow(
    accountKeyRow({ email: NEW_EMAIL, stripeCustomerId: "cus_gone" }),
  );

  assert.equal((await topUp(license.key)).status, 200);
  assert.equal(onlySessionCreate().customer, "cus_created");
});

test("a cached customer that still matches the email (any case) is reused as is", async () => {
  resetHarness();
  silenceRouteLogging();
  behaviour.customerEmails = { cus_same: "Hunter-NX-Checkout-New@Example.test" };
  const license = storeRow(
    accountKeyRow({ email: NEW_EMAIL, stripeCustomerId: "cus_same" }),
  );

  assert.equal((await topUp(license.key)).status, 200);
  assert.equal(onlySessionCreate().customer, "cus_same");
  assert.deepEqual(calls.customerRetrieve, ["cus_same"]);
  assert.deepEqual(calls.customerList, []);
  assert.deepEqual(calls.customerCreate, []);
  assert.deepEqual(calls.updateAccountKey, []);
});
