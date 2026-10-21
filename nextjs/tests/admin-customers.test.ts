/**
 * The admin Customers router: who may call it, and what each money or
 * entitlement mutation asks the database, Stripe and the mailer to do.
 *
 * See `admin-customers-harness.ts` for what is mocked and why. The router, the
 * tRPC middleware, the license-key generator, the refund flow and the spend
 * reader all run for real.
 */
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { afterEach, beforeEach, describe, test } from "node:test";

import { TRPCError } from "@trpc/server";

import {
  accountKeyRow,
  adminUser,
  behaviour,
  callCustomers,
  calls,
  charge,
  licenseRow,
  resetHarness,
} from "./admin-customers-harness";
import { formatLogArgs, leakyDbError, leakyLines } from "./db-error-fixture";
import { captureConsole, type ConsoleCapture } from "./module-mock";

const LICENSE_ID = "11111111-1111-4111-8111-111111111111";

async function rejects(
  promise: Promise<unknown>,
  code: string,
  message?: string | RegExp,
): Promise<void> {
  await assert.rejects(promise, (error: unknown) => {
    assert.ok(error instanceof TRPCError, `expected a TRPCError, got ${String(error)}`);
    assert.equal(error.code, code);
    if (typeof message === "string") assert.equal(error.message, message);
    else if (message) assert.match(error.message, message);
    return true;
  });
}

/** Every write the router can make. Empty means nothing changed. */
function writes() {
  return {
    insertAccountKey: calls.insertAccountKey.length,
    grantCreditLot: calls.grantCreditLot.length,
    refundCreditGrant: calls.refundCreditGrant.length,
    revokeAccountKey: calls.revokeAccountKey.length,
    updateCustomerEmail: calls.updateCustomerEmail.length,
    sendLicenseKey: calls.sendLicenseKey.length,
    createRefund: calls.createRefund.length,
  };
}

const NO_WRITES = {
  insertAccountKey: 0,
  grantCreditLot: 0,
  refundCreditGrant: 0,
  revokeAccountKey: 0,
  updateCustomerEmail: 0,
  sendLicenseKey: 0,
  createRefund: 0,
};

beforeEach(() => {
  resetHarness();
  behaviour.accountsById.set(LICENSE_ID, accountKeyRow({ id: LICENSE_ID }));
});

afterEach(() => {
  resetHarness();
});

describe("admin gate", () => {
  const mutations: Array<[string, unknown]> = [
    ["grant", { email: "someone@example.com" }],
    ["addCredits", { licenseKeyId: LICENSE_ID, amount: 100 }],
    ["refund", { licenseKeyId: LICENSE_ID, revokeLicense: true }],
    ["updateEmail", { userId: "user_1", newEmail: "moved@example.com" }],
  ];

  for (const [path, input] of mutations) {
    test(`${path} refuses a signed-out caller and writes nothing`, async () => {
      await rejects(callCustomers(path, "mutation", input, null), "UNAUTHORIZED");
      assert.deepEqual(writes(), NO_WRITES);
    });

    test(`${path} refuses a signed-in customer who is not an admin`, async () => {
      await rejects(
        callCustomers(path, "mutation", input, adminUser({ role: "user" })),
        "FORBIDDEN",
      );
      assert.deepEqual(writes(), NO_WRITES);
      assert.equal(calls.findAccountById.length, 0);
    });
  }

  test("list refuses a non-admin before it reads any customer", async () => {
    await rejects(
      callCustomers("list", "query", undefined, adminUser({ role: "user" })),
      "FORBIDDEN",
    );
    assert.equal(calls.customerPage.length, 0);
    assert.equal(calls.listCharges.length, 0);
  });
});

describe("grant", () => {
  test("mints a granted key for the email, grants 5000 credits to that key, and mails it", async () => {
    const result = (await callCustomers("grant", "mutation", {
      email: "friend@example.com",
    })) as { email: string; licenseKey: string };

    assert.equal(result.email, "friend@example.com");
    assert.match(result.licenseKey, /^HW(-[A-Z0-9]{4}){4}$/);

    assert.deepEqual(calls.getOrCreateUser, [
      { email: "friend@example.com", metadata: { name: "friend" } },
    ]);
    assert.equal(calls.insertAccountKey.length, 1);
    assert.deepEqual(calls.insertAccountKey[0], {
      key: result.licenseKey,
      email: "friend@example.com",
      userId: "user_new",
      status: "granted",
    });
    assert.deepEqual(calls.grantCreditLot, [
      {
        userId: "user_new",
        amount: 5000,
        sourceType: "admin_license_bundle",
        sourceId: "22222222-2222-4222-8222-222222222222",
      },
    ]);
    assert.equal(calls.sendLicenseKey.length, 1);
    assert.equal(calls.sendLicenseKey[0].licenseKey, result.licenseKey);
    assert.equal(calls.sendLicenseKey[0].customerEmail, "friend@example.com");
    assert.equal(calls.sendLicenseKey[0].customerName, "friend");
  });

  test("skips a candidate key that already exists and inserts the first free one", async () => {
    behaviour.keyCollisions = 2;

    const result = (await callCustomers("grant", "mutation", {
      email: "friend@example.com",
    })) as { licenseKey: string };

    assert.equal(calls.findAccountByKey.length, 3);
    assert.equal(calls.findAccountByKey[2], result.licenseKey);
    assert.notEqual(calls.findAccountByKey[0], result.licenseKey);
    assert.equal(calls.insertAccountKey[0].key, result.licenseKey);
  });

  test("gives up after 5 colliding candidates and creates nothing", async () => {
    behaviour.keyCollisions = 5;

    await rejects(
      callCustomers("grant", "mutation", { email: "friend@example.com" }),
      "INTERNAL_SERVER_ERROR",
      "Failed to generate unique license key",
    );
    assert.equal(calls.findAccountByKey.length, 5);
    assert.equal(calls.getOrCreateUser.length, 0);
    assert.deepEqual(writes(), NO_WRITES);
  });

  test("stops before the key insert when the user cannot be created", async () => {
    behaviour.createdUser = null;

    await rejects(
      callCustomers("grant", "mutation", { email: "friend@example.com" }),
      "INTERNAL_SERVER_ERROR",
      "Failed to create user",
    );
    assert.deepEqual(writes(), NO_WRITES);
  });

  test("grants no credits and sends no email when the key insert fails", async () => {
    behaviour.insertedLicense = null;

    await rejects(
      callCustomers("grant", "mutation", { email: "friend@example.com" }),
      "INTERNAL_SERVER_ERROR",
      "Failed to insert license key",
    );
    assert.equal(calls.insertAccountKey.length, 1);
    assert.equal(calls.grantCreditLot.length, 0);
    assert.equal(calls.sendLicenseKey.length, 0);
  });

  test("rejects an input that is not an email before it generates a key", async () => {
    await rejects(
      callCustomers("grant", "mutation", { email: "not-an-email" }),
      "BAD_REQUEST",
    );
    assert.equal(calls.findAccountByKey.length, 0);
  });
});

describe("addCredits", () => {
  test("grants the amount to the license's OWNING user and reports both balances", async () => {
    behaviour.accountsById.set(
      LICENSE_ID,
      accountKeyRow({ id: LICENSE_ID, userId: "user_owner" }),
    );
    behaviour.creditBalance = 1_200;
    behaviour.grantBalance = 1_450;

    const result = await callCustomers("addCredits", "mutation", {
      licenseKeyId: LICENSE_ID,
      amount: 250,
    });

    assert.deepEqual(result, {
      licenseKeyId: LICENSE_ID,
      previousBalance: 1_200,
      addedAmount: 250,
      newBalance: 1_450,
    });
    assert.deepEqual(calls.getCreditBalance, ["user_owner"]);
    assert.equal(calls.grantCreditLot.length, 1);
    const grant = calls.grantCreditLot[0];
    assert.equal(grant.userId, "user_owner");
    assert.equal(grant.amount, 250);
    assert.equal(grant.sourceType, "admin_manual");
    assert.match(String(grant.sourceId), /^[0-9a-f-]{36}$/);
  });

  test("gives every manual grant its own source id, so two grants never dedupe into one", async () => {
    const input = { licenseKeyId: LICENSE_ID, amount: 10 };
    await callCustomers("addCredits", "mutation", input);
    await callCustomers("addCredits", "mutation", input);

    assert.equal(calls.grantCreditLot.length, 2);
    assert.notEqual(calls.grantCreditLot[0].sourceId, calls.grantCreditLot[1].sourceId);
  });

  test("answers NOT_FOUND for an unknown license and grants nothing", async () => {
    await rejects(
      callCustomers("addCredits", "mutation", {
        licenseKeyId: "33333333-3333-4333-8333-333333333333",
        amount: 10,
      }),
      "NOT_FOUND",
      "License key not found",
    );
    assert.equal(calls.grantCreditLot.length, 0);
  });

  for (const [label, amount] of [
    ["zero", 0],
    ["a negative amount", -50],
    ["more than 1,000,000", 1_000_001],
  ] as const) {
    test(`rejects ${label} before it reads the license`, async () => {
      await rejects(
        callCustomers("addCredits", "mutation", { licenseKeyId: LICENSE_ID, amount }),
        "BAD_REQUEST",
      );
      assert.equal(calls.findAccountById.length, 0);
      assert.equal(calls.grantCreditLot.length, 0);
    });
  }

  test("accepts exactly the 1,000,000 cap", async () => {
    await callCustomers("addCredits", "mutation", {
      licenseKeyId: LICENSE_ID,
      amount: 1_000_000,
    });
    assert.equal(calls.grantCreditLot[0].amount, 1_000_000);
  });
});

describe("refund", () => {
  test("refunds the session's payment intent once per license and reverses the bundle grant", async () => {
    behaviour.paymentIntent = "pi_abc";

    const result = await callCustomers("refund", "mutation", {
      licenseKeyId: LICENSE_ID,
      revokeLicense: false,
    });

    assert.deepEqual(result, { success: true, revoked: false });
    assert.deepEqual(calls.retrieveSession, ["cs_1"]);
    assert.deepEqual(calls.createRefund, [
      { paymentIntent: "pi_abc", idempotencyKey: `admin-refund-${LICENSE_ID}` },
    ]);
    assert.deepEqual(calls.refundCreditGrant, [
      { sourceType: "license_bundle", sourceId: "cs_1" },
    ]);
    assert.equal(calls.revokeAccountKey.length, 0);
  });

  test("reads the id off an expanded payment intent object", async () => {
    behaviour.paymentIntent = { id: "pi_expanded" };

    await callCustomers("refund", "mutation", {
      licenseKeyId: LICENSE_ID,
      revokeLicense: false,
    });

    assert.equal(calls.createRefund[0].paymentIntent, "pi_expanded");
  });

  test("revokes the key for its owner, passing the acting admin so they are not signed out", async () => {
    behaviour.accountsById.set(
      LICENSE_ID,
      accountKeyRow({ id: LICENSE_ID, userId: "user_owner" }),
    );

    const result = await callCustomers("refund", "mutation", {
      licenseKeyId: LICENSE_ID,
      revokeLicense: true,
    });

    assert.deepEqual(result, { success: true, revoked: true });
    assert.deepEqual(calls.revokeAccountKey, [
      { id: LICENSE_ID, userId: "user_owner", options: { actingUserId: "admin_1" } },
    ]);
  });

  test("answers NOT_FOUND for an unknown license and touches no money", async () => {
    await rejects(
      callCustomers("refund", "mutation", {
        licenseKeyId: "33333333-3333-4333-8333-333333333333",
        revokeLicense: true,
      }),
      "NOT_FOUND",
    );
    assert.deepEqual(writes(), NO_WRITES);
    assert.equal(calls.retrieveSession.length, 0);
  });

  test("refuses a license with no Stripe session (a granted key) and touches no money", async () => {
    behaviour.accountsById.set(
      LICENSE_ID,
      accountKeyRow({ id: LICENSE_ID, stripeSessionId: null }),
    );

    await rejects(
      callCustomers("refund", "mutation", { licenseKeyId: LICENSE_ID, revokeLicense: true }),
      "BAD_REQUEST",
      "No Stripe session associated with this license",
    );
    assert.deepEqual(writes(), NO_WRITES);
    assert.equal(calls.retrieveSession.length, 0);
  });

  test("keeps the credits and the key when the session has no payment intent", async () => {
    behaviour.paymentIntent = null;

    await rejects(
      callCustomers("refund", "mutation", { licenseKeyId: LICENSE_ID, revokeLicense: true }),
      "BAD_REQUEST",
      "No payment intent found for this session",
    );
    assert.deepEqual(writes(), NO_WRITES);
  });

  test("retrieves the session and refunds on the pinned refund client", async () => {
    await callCustomers("refund", "mutation", {
      licenseKeyId: LICENSE_ID,
      revokeLicense: false,
    });
    // Both calls reach `stripeRefundApi` from lib/clients/stripe.ts (#755).
    // Its API version pin is proved on the wire in stripe-client.test.ts.
    assert.deepEqual(calls.retrieveSession, ["cs_1"]);
    assert.equal(calls.createRefund.length, 1);
  });
});

describe("updateEmail", () => {
  test("moves the customer to the lowercased, trimmed email, passing the acting admin", async () => {
    behaviour.userById = { id: "user_1", email: "old@example.com" };

    const result = await callCustomers("updateEmail", "mutation", {
      userId: "user_1",
      newEmail: "New.Person@Example.com",
    });

    assert.deepEqual(result, { success: true, email: "new.person@example.com" });
    assert.deepEqual(calls.getUserByEmail, ["new.person@example.com"]);
    assert.deepEqual(calls.updateCustomerEmail, [
      {
        userId: "user_1",
        email: "new.person@example.com",
        options: { actingUserId: "admin_1" },
      },
    ]);
  });

  test("answers NOT_FOUND for an unknown customer and writes nothing", async () => {
    await rejects(
      callCustomers("updateEmail", "mutation", {
        userId: "user_missing",
        newEmail: "moved@example.com",
      }),
      "NOT_FOUND",
      "Customer not found",
    );
    assert.equal(calls.getUserByEmail.length, 0);
    assert.equal(calls.updateCustomerEmail.length, 0);
  });

  test("treats a case-only change as a no-op and writes nothing", async () => {
    behaviour.userById = { id: "user_1", email: "Same@Example.com" };

    const result = await callCustomers("updateEmail", "mutation", {
      userId: "user_1",
      newEmail: "same@example.com",
    });

    assert.deepEqual(result, { success: true, email: "same@example.com" });
    assert.equal(calls.getUserByEmail.length, 0);
    assert.equal(calls.updateCustomerEmail.length, 0);
  });

  test("refuses an email that belongs to a DIFFERENT account", async () => {
    behaviour.userById = { id: "user_1", email: "old@example.com" };
    behaviour.userByEmail = { id: "user_2", email: "taken@example.com" };

    await rejects(
      callCustomers("updateEmail", "mutation", {
        userId: "user_1",
        newEmail: "taken@example.com",
      }),
      "CONFLICT",
      "That email already belongs to another account",
    );
    assert.equal(calls.updateCustomerEmail.length, 0);
  });

  test("allows the email when the only holder is the same account", async () => {
    behaviour.userById = { id: "user_1", email: "old@example.com" };
    behaviour.userByEmail = { id: "user_1", email: "mine@example.com" };

    await callCustomers("updateEmail", "mutation", {
      userId: "user_1",
      newEmail: "mine@example.com",
    });
    assert.equal(calls.updateCustomerEmail.length, 1);
  });

  test("turns a unique-constraint race (23505) into CONFLICT", async () => {
    behaviour.userById = { id: "user_1", email: "old@example.com" };
    behaviour.updateEmailError = Object.assign(new Error("duplicate key"), {
      code: "23505",
    });

    await rejects(
      callCustomers("updateEmail", "mutation", {
        userId: "user_1",
        newEmail: "raced@example.com",
      }),
      "CONFLICT",
      "That email already belongs to another account",
    );
  });

  test("turns any other database failure into INTERNAL_SERVER_ERROR with its message", async () => {
    behaviour.userById = { id: "user_1", email: "old@example.com" };
    behaviour.updateEmailError = Object.assign(new Error("connection reset"), {
      code: "08006",
    });

    await rejects(
      callCustomers("updateEmail", "mutation", {
        userId: "user_1",
        newEmail: "moved@example.com",
      }),
      "INTERNAL_SERVER_ERROR",
      "connection reset",
    );
  });
});

describe("list", () => {
  type ListResult = {
    customers: Array<{
      userId: string;
      email: string;
      licenseCount: number;
      totalCredits: number;
      totalSpentCents: number | null;
      created: number;
      licenses: Array<{ id: string; key: string; credits: number }>;
    }>;
    totalCustomers: number;
    page: number;
    pageSize: number;
    totalPages: number;
  };

  const list = async (input?: unknown) =>
    (await callCustomers("list", "query", input)) as ListResult;

  test("trims the search, asks for 100 customers per page, and reports the clamped page", async () => {
    behaviour.customerPage = { userIds: [], totalCustomers: 250, page: 2 };

    const result = await list({ search: "  buyer  ", page: 7 });

    assert.deepEqual(calls.customerPage, [{ search: "buyer", page: 7, pageSize: 100 }]);
    assert.equal(result.page, 2);
    assert.equal(result.pageSize, 100);
    assert.equal(result.totalPages, 3);
    assert.equal(result.totalCustomers, 250);
  });

  test("reports 1 page for an empty result and starts at page 1 with no input", async () => {
    const result = await list();

    assert.deepEqual(calls.customerPage, [{ search: undefined, page: 1, pageSize: 100 }]);
    assert.equal(result.totalPages, 1);
    assert.deepEqual(result.customers, []);
  });

  test("groups keys per customer, reads the pooled balance ONCE, and keeps the earliest date", async () => {
    behaviour.customerPage = { userIds: ["user_1"], totalCustomers: 1, page: 1 };
    behaviour.users = new Map([["user_1", { id: "user_1", email: "Owner@Example.com" }]]);
    behaviour.licenses = [
      licenseRow({
        id: "k2",
        key: "HW-FAKE-0000-0002",
        credits: 3_000,
        stripeCustomerId: null,
        createdAt: new Date("2026-03-01T00:00:00Z"),
      }),
      licenseRow({
        id: "k1",
        key: "HW-FAKE-0000-0001",
        credits: 3_000,
        stripeCustomerId: null,
        createdAt: new Date("2026-01-01T00:00:00Z"),
      }),
    ];

    const [customer] = (await list()).customers;

    assert.equal(customer.licenseCount, 2);
    // Pooled per account: 3000, never 3000 x 2.
    assert.equal(customer.totalCredits, 3_000);
    assert.equal(customer.created, Date.parse("2026-01-01T00:00:00Z") / 1000);
    assert.equal(customer.email, "owner@example.com");
    assert.deepEqual(
      customer.licenses.map((l) => l.id),
      ["k2", "k1"],
    );
  });

  test("falls back to the license email when the user row is missing", async () => {
    behaviour.customerPage = { userIds: ["user_1"], totalCustomers: 1, page: 1 };
    behaviour.licenses = [licenseRow({ email: "Key.Email@Example.com", stripeCustomerId: null })];

    const [customer] = (await list()).customers;
    assert.equal(customer.email, "key.email@example.com");
  });

  test("orders customers by the page's user ids, not by the order keys came back", async () => {
    behaviour.customerPage = { userIds: ["user_b", "user_a"], totalCustomers: 2, page: 1 };
    behaviour.licenses = [
      licenseRow({ id: "ka", userId: "user_a", stripeCustomerId: null }),
      licenseRow({ id: "kb", userId: "user_b", stripeCustomerId: null }),
    ];

    const result = await list();
    assert.deepEqual(
      result.customers.map((c) => c.userId),
      ["user_b", "user_a"],
    );
    assert.deepEqual(calls.licensesForUserIds, [["user_b", "user_a"]]);
  });

  test("sums net spend across every distinct Stripe customer, once per id", async () => {
    behaviour.customerPage = { userIds: ["user_1"], totalCustomers: 1, page: 1 };
    behaviour.licenses = [
      licenseRow({ id: "k1", stripeCustomerId: "cus_a" }),
      licenseRow({ id: "k2", stripeCustomerId: "cus_a" }),
      licenseRow({ id: "k3", stripeCustomerId: "cus_b" }),
    ];
    behaviour.charges.set("cus_a", [charge({ amount: 2_000, amount_refunded: 500 })]);
    behaviour.charges.set("cus_b", [
      charge({ id: "ch_b1", amount: 1_000 }),
      charge({ id: "ch_b2", amount: 9_999, status: "failed" }),
      charge({ id: "ch_b3", amount: 700, disputed: true }),
      charge({ id: "ch_b4", amount: 300, disputed: true }),
    ]);
    behaviour.disputeStatus.set("ch_b3", "lost");
    behaviour.disputeStatus.set("ch_b4", "won");

    const [customer] = (await list()).customers;

    // 1500 (cus_a) + 1000 + 300 (won dispute) — the failed and lost charges do not count.
    assert.equal(customer.totalSpentCents, 2_800);
    assert.deepEqual([...calls.listCharges].sort(), ["cus_a", "cus_b"]);
  });

  test("reports spend as unknown (null) when ANY of the customer's Stripe ids fails", async () => {
    behaviour.customerPage = { userIds: ["user_1", "user_2"], totalCustomers: 2, page: 1 };
    behaviour.licenses = [
      licenseRow({ id: "k1", userId: "user_1", stripeCustomerId: "cus_ok" }),
      licenseRow({ id: "k2", userId: "user_1", stripeCustomerId: "cus_down" }),
      licenseRow({ id: "k3", userId: "user_2", stripeCustomerId: "cus_other" }),
    ];
    behaviour.charges.set("cus_ok", [charge({ amount: 5_000 })]);
    behaviour.charges.set("cus_down", new Error("stripe unavailable"));
    behaviour.charges.set("cus_other", [charge({ amount: 400 })]);

    const result = await list();
    const byId = new Map(result.customers.map((c) => [c.userId, c]));

    assert.equal(byId.get("user_1")?.totalSpentCents, null);
    // One customer's Stripe failure does not blank another customer's spend.
    assert.equal(byId.get("user_2")?.totalSpentCents, 400);
  });

  test("makes no Stripe call for a customer with no Stripe id and reports 0", async () => {
    behaviour.customerPage = { userIds: ["user_1"], totalCustomers: 1, page: 1 };
    behaviour.licenses = [licenseRow({ stripeCustomerId: null })];

    const [customer] = (await list()).customers;
    assert.equal(customer.totalSpentCents, 0);
    assert.equal(calls.listCharges.length, 0);
  });

  test("wraps a database failure as INTERNAL_SERVER_ERROR with its message", async () => {
    behaviour.customerPageError = new Error("pool exhausted");

    await rejects(list(), "INTERNAL_SERVER_ERROR", "pool exhausted");
  });

  test("rejects a page below 1 before it reads the database", async () => {
    await rejects(list({ page: 0 }), "BAD_REQUEST");
    assert.equal(calls.customerPage.length, 0);
  });
});

describe("a drizzle error (#1039)", () => {
  let errorLines: string[] = [];
  let errorCapture: ConsoleCapture | null = null;

  beforeEach(() => {
    errorCapture = captureConsole(["error"]);
    errorLines = errorCapture.lines;
  });

  afterEach(() => {
    errorCapture?.restore();
    errorCapture = null;
  });

  test("updateEmail turns drizzle's 23505 on .cause into CONFLICT", async () => {
    behaviour.userById = { id: "user_1", email: "old@example.com" };
    behaviour.updateEmailError = leakyDbError("23505", "user_email_unique");

    await rejects(
      callCustomers("updateEmail", "mutation", {
        userId: "user_1",
        newEmail: "raced@example.com",
      }),
      "CONFLICT",
      "That email already belongs to another account",
    );
  });

  test("updateEmail logs the SQLSTATE and returns a fixed message, never the bound values", async () => {
    behaviour.userById = { id: "user_1", email: "old@example.com" };
    behaviour.updateEmailError = leakyDbError("22P02");

    await rejects(
      callCustomers("updateEmail", "mutation", {
        userId: "user_1",
        newEmail: "moved@example.com",
      }),
      "INTERNAL_SERVER_ERROR",
      "Failed to update email",
    );
    // The SQLSTATE diagnostic line, then the one `failed` outcome line (#2177).
    assert.equal(errorLines.length, 2);
    assert.match(errorLines[0], /22P02/);
    assert.match(errorLines[1], /"event":"admin\.customers\.updateEmail","outcome":"failed"/);
    assert.match(errorLines[1], /"dbCode":"22P02"/);
    assert.deepEqual(leakyLines(errorLines), []);
  });

  test("list logs the SQLSTATE and returns a fixed message, never the bound values", async () => {
    behaviour.customerPageError = leakyDbError("22P02");

    await rejects(
      callCustomers("list", "query", { page: 1 }),
      "INTERNAL_SERVER_ERROR",
      "Failed to fetch customers",
    );
    assert.equal(errorLines.length, 1);
    assert.match(errorLines[0], /22P02/);
    assert.deepEqual(leakyLines(errorLines), []);
  });
});

describe("one log line per admin mutation outcome (#2177)", () => {
  const methods = ["log", "info", "warn", "error", "debug"] as const;
  type Method = (typeof methods)[number];
  const real = Object.fromEntries(methods.map((m) => [m, console[m]])) as Record<
    Method,
    (...args: unknown[]) => void
  >;
  let lines: Array<{ method: Method; text: string }> = [];

  beforeEach(() => {
    lines = [];
    for (const method of methods) {
      console[method] = (...args: unknown[]): void => {
        lines.push({ method, text: formatLogArgs(args) });
      };
    }
  });

  afterEach(() => {
    for (const method of methods) console[method] = real[method];
    // Whatever the case, no line may carry an address or a licence key.
    for (const { text } of lines) {
      assert.ok(!text.includes("@"), `a log line carries an address: ${text}`);
      assert.doesNotMatch(text, /HW(-[A-Z0-9]{4}){4}/);
    }
  });

  const hash = (email: string) =>
    createHash("sha256").update(email).digest("hex").slice(0, 12);

  /** The parsed outcome lines for one mutation, with the console method each used. */
  function outcomes(name: string): Array<Record<string, unknown> & { method: Method }> {
    return lines.flatMap(({ method, text }) => {
      if (!text.startsWith("{")) return [];
      const parsed = JSON.parse(text) as Record<string, unknown>;
      return parsed.event === `admin.customers.${name}` ? [{ ...parsed, method }] : [];
    });
  }

  function onlyOutcome(name: string): Record<string, unknown> & { method: Method } {
    const found = outcomes(name);
    assert.equal(found.length, 1, `expected one ${name} line, got ${JSON.stringify(lines)}`);
    return found[0];
  }

  test("grant logs ok with the admin, the new key's id and a hash of the email, never the key", async () => {
    await callCustomers("grant", "mutation", { email: "friend@example.com" });

    assert.deepEqual(onlyOutcome("grant"), {
      method: "log",
      event: "admin.customers.grant",
      outcome: "ok",
      adminId: "admin_1",
      emailHash: hash("friend@example.com"),
      userId: "user_new",
      licenseId: "22222222-2222-4222-8222-222222222222",
      creditsGranted: 5000,
    });
  });

  test("grant logs failed, and how far it got, when the key insert fails", async () => {
    behaviour.insertedLicense = null;

    await rejects(
      callCustomers("grant", "mutation", { email: "friend@example.com" }),
      "INTERNAL_SERVER_ERROR",
    );
    const line = onlyOutcome("grant");
    assert.equal(line.method, "error");
    assert.equal(line.outcome, "failed");
    assert.equal(line.reason, "INTERNAL_SERVER_ERROR");
    // The tRPC code is never passed off as a SQLSTATE.
    assert.equal(line.dbCode, undefined);
    assert.equal(line.userId, "user_new");
    assert.equal(line.licenseId, undefined);
  });

  test("addCredits logs ok with the admin, the amount and the lot's source id", async () => {
    behaviour.grantBalance = 7_000;

    await callCustomers("addCredits", "mutation", { licenseKeyId: LICENSE_ID, amount: 2_000 });

    assert.deepEqual(onlyOutcome("addCredits"), {
      method: "log",
      event: "admin.customers.addCredits",
      outcome: "ok",
      adminId: "admin_1",
      licenseKeyId: LICENSE_ID,
      amount: 2_000,
      userId: "user_1",
      // The id the lot was written with, so the lot can be traced to this admin.
      sourceId: calls.grantCreditLot[0].sourceId,
      newBalance: 7_000,
    });
  });

  test("addCredits logs rejected NOT_FOUND for an unknown license", async () => {
    const unknown = "33333333-3333-4333-8333-333333333333";
    await rejects(
      callCustomers("addCredits", "mutation", { licenseKeyId: unknown, amount: 100 }),
      "NOT_FOUND",
    );
    assert.deepEqual(onlyOutcome("addCredits"), {
      method: "warn",
      event: "admin.customers.addCredits",
      outcome: "rejected",
      reason: "NOT_FOUND",
      adminId: "admin_1",
      licenseKeyId: unknown,
      amount: 100,
    });
  });

  test("refund logs ok with the admin, the license, the Stripe session and the revoke", async () => {
    await callCustomers("refund", "mutation", { licenseKeyId: LICENSE_ID, revokeLicense: true });

    assert.deepEqual(onlyOutcome("refund"), {
      method: "log",
      event: "admin.customers.refund",
      outcome: "ok",
      adminId: "admin_1",
      licenseKeyId: LICENSE_ID,
      revokeRequested: true,
      userId: "user_1",
      stripeSessionId: "cs_1",
      paymentRefunded: true,
      creditsReversed: true,
      revoked: true,
    });
  });

  test("refund logs rejected NOT_FOUND for an unknown license", async () => {
    await rejects(
      callCustomers("refund", "mutation", {
        licenseKeyId: "33333333-3333-4333-8333-333333333333",
        revokeLicense: false,
      }),
      "NOT_FOUND",
    );
    const line = onlyOutcome("refund");
    assert.equal(line.outcome, "rejected");
    assert.equal(line.reason, "NOT_FOUND");
    assert.equal(line.adminId, "admin_1");
  });

  test("refund logs rejected BAD_REQUEST when Stripe has no payment intent", async () => {
    behaviour.paymentIntent = null;

    await rejects(
      callCustomers("refund", "mutation", { licenseKeyId: LICENSE_ID, revokeLicense: true }),
      "BAD_REQUEST",
    );
    const line = onlyOutcome("refund");
    assert.equal(line.outcome, "rejected");
    assert.equal(line.reason, "BAD_REQUEST");
    assert.equal(line.paymentRefunded, undefined);
  });

  test("refund logs failed AFTER the payment was refunded when the credit reversal throws", async () => {
    behaviour.refundCreditGrantError = leakyDbError("57P01");

    await assert.rejects(
      callCustomers("refund", "mutation", { licenseKeyId: LICENSE_ID, revokeLicense: true }),
    );
    const line = onlyOutcome("refund");
    assert.equal(line.method, "error");
    assert.equal(line.outcome, "failed");
    assert.equal(line.reason, "UNEXPECTED");
    assert.equal(line.dbCode, "57P01");
    assert.equal(line.paymentRefunded, true);
    assert.equal(line.creditsReversed, undefined);
    assert.equal(line.revoked, undefined);
  });

  test("updateEmail logs ok with hashes of the old and new address, never either", async () => {
    behaviour.userById = { id: "user_1", email: "Old@Example.com" };

    await callCustomers("updateEmail", "mutation", {
      userId: "user_1",
      newEmail: "New.Person@Example.com",
    });

    assert.deepEqual(onlyOutcome("updateEmail"), {
      method: "log",
      event: "admin.customers.updateEmail",
      outcome: "ok",
      adminId: "admin_1",
      userId: "user_1",
      newEmailHash: hash("new.person@example.com"),
      oldEmailHash: hash("old@example.com"),
      changed: true,
    });
  });

  test("updateEmail logs ok with changed: false for a no-op", async () => {
    behaviour.userById = { id: "user_1", email: "Same@Example.com" };

    await callCustomers("updateEmail", "mutation", {
      userId: "user_1",
      newEmail: "same@example.com",
    });
    const line = onlyOutcome("updateEmail");
    assert.equal(line.outcome, "ok");
    assert.equal(line.changed, false);
  });

  test("updateEmail logs rejected NOT_FOUND for an unknown customer", async () => {
    await rejects(
      callCustomers("updateEmail", "mutation", {
        userId: "user_missing",
        newEmail: "moved@example.com",
      }),
      "NOT_FOUND",
    );
    const line = onlyOutcome("updateEmail");
    assert.equal(line.outcome, "rejected");
    assert.equal(line.reason, "NOT_FOUND");
  });

  test("updateEmail logs rejected CONFLICT for an address another account holds", async () => {
    behaviour.userById = { id: "user_1", email: "old@example.com" };
    behaviour.userByEmail = { id: "user_2", email: "taken@example.com" };

    await rejects(
      callCustomers("updateEmail", "mutation", {
        userId: "user_1",
        newEmail: "taken@example.com",
      }),
      "CONFLICT",
    );
    const line = onlyOutcome("updateEmail");
    assert.equal(line.method, "warn");
    assert.equal(line.outcome, "rejected");
    assert.equal(line.reason, "CONFLICT");
  });

  test("updateEmail logs rejected CONFLICT on a unique-constraint race", async () => {
    behaviour.userById = { id: "user_1", email: "old@example.com" };
    behaviour.updateEmailError = Object.assign(new Error("duplicate key"), { code: "23505" });

    await rejects(
      callCustomers("updateEmail", "mutation", {
        userId: "user_1",
        newEmail: "raced@example.com",
      }),
      "CONFLICT",
    );
    assert.equal(onlyOutcome("updateEmail").reason, "CONFLICT");
  });

  test("a Stripe error's own code is not logged as a SQLSTATE", async () => {
    behaviour.paymentIntent = "pi_1";
    behaviour.refundCreditGrantError = Object.assign(new Error("Stripe said no"), {
      code: "charge_already_refunded",
    });

    await assert.rejects(
      callCustomers("refund", "mutation", { licenseKeyId: LICENSE_ID, revokeLicense: false }),
    );
    const line = onlyOutcome("refund");
    assert.equal(line.outcome, "failed");
    assert.equal(line.dbCode, undefined);
  });

  test("updateEmail logs one failed outcome line with the SQLSTATE for a database failure", async () => {
    behaviour.userById = { id: "user_1", email: "old@example.com" };
    behaviour.updateEmailError = leakyDbError("40001");

    await rejects(
      callCustomers("updateEmail", "mutation", {
        userId: "user_1",
        newEmail: "moved@example.com",
      }),
      "INTERNAL_SERVER_ERROR",
      "Failed to update email",
    );
    // Its diagnostic line (kind, query, constraint) stays beside the outcome line.
    assert.equal(lines.length, 2);
    assert.match(lines[0].text, /Update customer email error:[^]*40001/);
    const line = onlyOutcome("updateEmail");
    assert.equal(line.method, "error");
    assert.equal(line.reason, "INTERNAL_SERVER_ERROR");
    assert.equal(line.dbCode, "40001");
  });

  test("an admin-gate refusal writes no outcome line (nothing ran)", async () => {
    await rejects(
      callCustomers("refund", "mutation", { licenseKeyId: LICENSE_ID, revokeLicense: true }, null),
      "UNAUTHORIZED",
    );
    assert.deepEqual(outcomes("refund"), []);
  });
});
