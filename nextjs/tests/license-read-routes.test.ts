/**
 * Behaviour of the four Account Key read routes. See
 * `license-read-routes-harness.ts` for how the database and Better Auth are
 * replaced at the module boundary.
 *
 * What these tests hold in place:
 *
 * - a revoked key is never handed back out, and never suppresses the mint;
 * - a read route never mints, whatever the email;
 * - credits are POOLED per account, so two keys on one user report that one
 *   balance and `totalCredits` counts it ONCE;
 * - the `x-internal-secret` gate and the session gate both run before the
 *   database is touched.
 */
import assert from "node:assert/strict";
import test from "node:test";

import {
  INTERNAL_SECRET,
  accountKeyRow,
  behaviour,
  calls,
  customerGet,
  internalPost,
  loadCustomerProfileRoute,
  loadGrantLicenseRoute,
  loadGrantedEmailsRoute,
  loadLicensesForEmailRoute,
  resetHarness,
  restoreRouteLogging,
  silenceRouteLogging,
} from "./license-read-routes-harness";

const realSecret = process.env.HYPERWHISPER_INTERNAL_SECRET;

test.before(() => {
  process.env.HYPERWHISPER_INTERNAL_SECRET = INTERNAL_SECRET;
  silenceRouteLogging();
});

test.after(() => {
  if (realSecret === undefined) delete process.env.HYPERWHISPER_INTERNAL_SECRET;
  else process.env.HYPERWHISPER_INTERNAL_SECRET = realSecret;
  restoreRouteLogging();
});

test.beforeEach(() => {
  resetHarness();
});

async function readJson(response: Response): Promise<unknown> {
  return response.json();
}

// ---------------------------------------------------------------------------
// POST /api/internal/grant-license
// ---------------------------------------------------------------------------

test("grant-license hands back the granted key and does not mint", async () => {
  behaviour.keysByEmail = [
    accountKeyRow({ key: "HW-AAAA-0000-0001", status: "granted" }),
  ];
  const { POST } = await loadGrantLicenseRoute();

  const response = await POST(
    internalPost("/api/internal/grant-license", { email: "buyer@example.com" }),
  );

  assert.equal(response.status, 200);
  assert.deepEqual(await readJson(response), { licenseKey: "HW-AAAA-0000-0001" });
  assert.deepEqual(calls.provisionAccountKeyForEmail, []);
});

test("grant-license mints when every key on the email is revoked", async () => {
  // A revoked key is dead at /api/license/validate. Handing it back would
  // leave the caller with a key that cannot transcribe, and would skip the
  // credit grant that comes with a fresh mint.
  behaviour.keysByEmail = [
    accountKeyRow({ key: "HW-DEAD-0000-0002", status: "revoked" }),
    accountKeyRow({ key: "HW-DEAD-0000-0001", status: "refunded" }),
  ];
  behaviour.minted = accountKeyRow({ key: "HW-NEWK-0000-0003" });
  const { POST } = await loadGrantLicenseRoute();

  const response = await POST(
    internalPost("/api/internal/grant-license", { email: "buyer@example.com" }),
  );

  assert.deepEqual(await readJson(response), { licenseKey: "HW-NEWK-0000-0003" });
  assert.deepEqual(calls.provisionAccountKeyForEmail, ["buyer@example.com"]);
});

test("grant-license takes the newest granted key when a revoked one is newer", async () => {
  // `getAccountKeysByEmail` answers newest first, so the revoked row leads.
  behaviour.keysByEmail = [
    accountKeyRow({ key: "HW-DEAD-0000-0009", status: "revoked" }),
    accountKeyRow({ key: "HW-LIVE-0000-0002", status: "granted" }),
    accountKeyRow({ key: "HW-LIVE-0000-0001", status: "granted" }),
  ];
  const { POST } = await loadGrantLicenseRoute();

  const response = await POST(
    internalPost("/api/internal/grant-license", { email: "buyer@example.com" }),
  );

  assert.deepEqual(await readJson(response), { licenseKey: "HW-LIVE-0000-0002" });
  assert.deepEqual(calls.provisionAccountKeyForEmail, []);
});

test("grant-license normalizes the email before it reaches the database", async () => {
  behaviour.minted = accountKeyRow({ key: "HW-NEWK-0000-0004" });
  const { POST } = await loadGrantLicenseRoute();

  await POST(
    internalPost("/api/internal/grant-license", { email: "  Buyer@Example.COM " }),
  );

  assert.deepEqual(calls.getAccountKeysByEmail, ["buyer@example.com"]);
  assert.deepEqual(calls.provisionAccountKeyForEmail, ["buyer@example.com"]);
});

test("grant-license rejects a wrong secret without touching the database", async () => {
  const { POST } = await loadGrantLicenseRoute();

  const response = await POST(
    internalPost(
      "/api/internal/grant-license",
      { email: "buyer@example.com" },
      { "x-internal-secret": "not-the-secret" },
    ),
  );

  assert.equal(response.status, 401);
  assert.deepEqual(await readJson(response), { error: "Unauthorized" });
  assert.deepEqual(calls.getAccountKeysByEmail, []);
  assert.deepEqual(calls.provisionAccountKeyForEmail, []);
});

test("grant-license rejects a body with no email", async () => {
  const { POST } = await loadGrantLicenseRoute();

  const response = await POST(internalPost("/api/internal/grant-license", {}));

  assert.equal(response.status, 400);
  assert.deepEqual(await readJson(response), { error: "email is required" });
  assert.deepEqual(calls.provisionAccountKeyForEmail, []);
});

test("grant-license answers 500 when the mint fails", async () => {
  behaviour.mintError = new Error("unique key generation failed");
  const { POST } = await loadGrantLicenseRoute();

  const response = await POST(
    internalPost("/api/internal/grant-license", { email: "buyer@example.com" }),
  );

  assert.equal(response.status, 500);
  assert.deepEqual(await readJson(response), { error: "Internal server error" });
});

// ---------------------------------------------------------------------------
// POST /api/internal/granted-emails
// ---------------------------------------------------------------------------

test("granted-emails returns the distinct email list", async () => {
  behaviour.grantedEmails = ["one@example.com", "two@example.com"];
  const { POST } = await loadGrantedEmailsRoute();

  const response = await POST(internalPost("/api/internal/granted-emails", {}));

  assert.equal(response.status, 200);
  assert.deepEqual(await readJson(response), {
    emails: ["one@example.com", "two@example.com"],
  });
  assert.equal(calls.getGrantedEmails, 1);
});

test("granted-emails rejects a missing secret without reading the database", async () => {
  const { POST } = await loadGrantedEmailsRoute();

  const response = await POST(
    internalPost("/api/internal/granted-emails", {}, {}),
  );

  assert.equal(response.status, 401);
  assert.deepEqual(await readJson(response), { error: "Unauthorized" });
  assert.equal(calls.getGrantedEmails, 0);
});

test("granted-emails answers 500 when the read fails", async () => {
  behaviour.grantedEmailsError = new Error("connection reset");
  const { POST } = await loadGrantedEmailsRoute();

  const response = await POST(internalPost("/api/internal/granted-emails", {}));

  assert.equal(response.status, 500);
  assert.deepEqual(await readJson(response), { error: "Internal server error" });
});

// ---------------------------------------------------------------------------
// POST /api/internal/licenses-for-email
// ---------------------------------------------------------------------------

test("licenses-for-email hides revoked keys and never mints", async () => {
  behaviour.keysByEmail = [
    accountKeyRow({ key: "HW-LIVE-0000-0001", status: "granted" }),
    accountKeyRow({ key: "HW-DEAD-0000-0001", status: "revoked" }),
  ];
  behaviour.balances = new Map([["user_1", 1_500]]);
  const { POST } = await loadLicensesForEmailRoute();

  const response = await POST(
    internalPost("/api/internal/licenses-for-email", {
      email: "buyer@example.com",
    }),
  );

  assert.deepEqual(await readJson(response), {
    licenses: [
      {
        key: "HW-LIVE-0000-0001",
        status: "granted",
        createdAt: "2026-01-01T00:00:00.000Z",
        credits: 1_500,
      },
    ],
  });
  assert.deepEqual(calls.provisionAccountKeyForEmail, []);
});

test("licenses-for-email pools one balance across an account's two keys", async () => {
  // Both keys belong to user_1, whose account holds 800 credits. Each key
  // reports the account balance, and the balance lookup asks for the user id
  // once, not once per key.
  behaviour.keysByEmail = [
    accountKeyRow({ key: "HW-LIVE-0000-0002", userId: "user_1" }),
    accountKeyRow({ key: "HW-LIVE-0000-0001", userId: "user_1" }),
  ];
  behaviour.balances = new Map([["user_1", 800]]);
  const { POST } = await loadLicensesForEmailRoute();

  const response = await POST(
    internalPost("/api/internal/licenses-for-email", {
      email: "buyer@example.com",
    }),
  );

  const body = (await readJson(response)) as { licenses: { credits: number }[] };
  assert.deepEqual(
    body.licenses.map((l) => l.credits),
    [800, 800],
  );
  assert.deepEqual(calls.getCreditBalancesForUsers, [["user_1"]]);
});

test("licenses-for-email reports 0 for an account with no credit grants", async () => {
  behaviour.keysByEmail = [accountKeyRow({ userId: "user_9" })];
  behaviour.balances = new Map();
  const { POST } = await loadLicensesForEmailRoute();

  const response = await POST(
    internalPost("/api/internal/licenses-for-email", {
      email: "buyer@example.com",
    }),
  );

  const body = (await readJson(response)) as { licenses: { credits: number }[] };
  assert.deepEqual(body.licenses.map((l) => l.credits), [0]);
});

test("licenses-for-email answers an empty list for an unknown email", async () => {
  const { POST } = await loadLicensesForEmailRoute();

  const response = await POST(
    internalPost("/api/internal/licenses-for-email", {
      email: "stranger@example.com",
    }),
  );

  assert.equal(response.status, 200);
  assert.deepEqual(await readJson(response), { licenses: [] });
  assert.deepEqual(calls.provisionAccountKeyForEmail, []);
});

test("licenses-for-email answers 500 when the read fails", async () => {
  behaviour.lookupError = new Error("connection reset");
  const { POST } = await loadLicensesForEmailRoute();

  const response = await POST(
    internalPost("/api/internal/licenses-for-email", {
      email: "buyer@example.com",
    }),
  );

  assert.equal(response.status, 500);
  assert.deepEqual(await readJson(response), { error: "Internal server error" });
});

// ---------------------------------------------------------------------------
// GET /api/customer/profile
// ---------------------------------------------------------------------------

test("profile rejects a signed-out caller without reading the database", async () => {
  behaviour.session = null;
  const { GET } = await loadCustomerProfileRoute();

  const response = await GET(customerGet());

  assert.equal(response.status, 401);
  assert.deepEqual(await readJson(response), { error: "Unauthorized" });
  assert.deepEqual(calls.getAccountKeysByEmail, []);
});

test("profile counts a pooled balance once across two keys", async () => {
  // The bug this holds shut: summing per license would report 1200 for an
  // account that holds 600.
  behaviour.session = { user: { id: "user_1", email: "Buyer@Example.com" } };
  behaviour.keysByEmail = [
    accountKeyRow({ id: "row_2", key: "HW-LIVE-0000-0002", userId: "user_1" }),
    accountKeyRow({ id: "row_1", key: "HW-LIVE-0000-0001", userId: "user_1" }),
  ];
  behaviour.balances = new Map([["user_1", 600]]);
  const { GET } = await loadCustomerProfileRoute();

  const response = await GET(customerGet());
  const body = (await readJson(response)) as {
    totalCredits: number;
    licenses: { credits: number }[];
  };

  assert.equal(body.totalCredits, 600);
  assert.deepEqual(body.licenses.map((l) => l.credits), [600, 600]);
});

test("profile adds up the balances of two distinct accounts", async () => {
  behaviour.session = { user: { id: "user_1", email: "buyer@example.com" } };
  behaviour.keysByEmail = [
    accountKeyRow({ id: "row_2", key: "HW-LIVE-0000-0002", userId: "user_2" }),
    accountKeyRow({ id: "row_1", key: "HW-LIVE-0000-0001", userId: "user_1" }),
  ];
  behaviour.balances = new Map([
    ["user_1", 100],
    ["user_2", 250],
  ]);
  const { GET } = await loadCustomerProfileRoute();

  const response = await GET(customerGet());
  const body = (await readJson(response)) as {
    totalCredits: number;
    licenses: { credits: number }[];
  };

  assert.equal(body.totalCredits, 350);
  assert.deepEqual(body.licenses.map((l) => l.credits), [250, 100]);
});

test("profile returns the whole license shape the account page reads", async () => {
  behaviour.session = { user: { id: "user_1", email: "buyer@example.com" } };
  behaviour.keysByEmail = [
    accountKeyRow({
      id: "row_1",
      key: "HW-LIVE-0000-0001",
      status: "granted",
      stripeCustomerId: "cus_made_up",
      polarCustomerId: null,
    }),
  ];
  behaviour.balances = new Map([["user_1", 42]]);
  const { GET } = await loadCustomerProfileRoute();

  const response = await GET(customerGet());

  assert.deepEqual(await readJson(response), {
    user: { id: "user_1", email: "buyer@example.com" },
    licenses: [
      {
        id: "row_1",
        key: "HW-LIVE-0000-0001",
        status: "granted",
        created_at: "2026-01-01T00:00:00.000Z",
        stripe_customer_id: "cus_made_up",
        polar_customer_id: null,
        credits: 42,
      },
    ],
    totalCredits: 42,
  });
});

test("profile lowercases the session email before the lookup", async () => {
  behaviour.session = { user: { id: "user_1", email: "Buyer@Example.COM" } };
  const { GET } = await loadCustomerProfileRoute();

  await GET(customerGet());

  assert.deepEqual(calls.getAccountKeysByEmail, ["buyer@example.com"]);
});

test("profile survives a session with no email", async () => {
  behaviour.session = { user: { id: "user_1", email: null } };
  const { GET } = await loadCustomerProfileRoute();

  const response = await GET(customerGet());
  const body = (await readJson(response)) as {
    licenses: unknown[];
    totalCredits: number;
  };

  assert.equal(response.status, 200);
  assert.deepEqual(calls.getAccountKeysByEmail, [""]);
  assert.deepEqual(body.licenses, []);
  assert.equal(body.totalCredits, 0);
});

test("profile answers 500 when the read fails", async () => {
  behaviour.session = { user: { id: "user_1", email: "buyer@example.com" } };
  behaviour.lookupError = new Error("connection reset");
  const { GET } = await loadCustomerProfileRoute();

  const response = await GET(customerGet());

  assert.equal(response.status, 500);
  assert.deepEqual(await readJson(response), { error: "Internal server error" });
});
