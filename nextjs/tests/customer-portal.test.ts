/**
 * The authenticated customer portal seam.
 *
 * Two source files, both money and entitlement code, both at 0% before this
 * file existed:
 *
 * - `server/api/trpc.ts` — builds the request context from Better Auth and
 *   decides, in two middlewares, who may call a procedure at all.
 * - `server/api/routers/customer.ts` — turns pooled credit grants into the
 *   balance, the minutes and the top-up history a customer reads, and mints
 *   the Stripe billing-portal link.
 *
 * Every test drives the REAL source through `tests/customer-portal-harness.ts`,
 * which replaces Better Auth, the database layer and the Stripe client at the
 * module boundary. No bypass and no test license key is added to the source.
 */
import assert from "node:assert/strict";
import test, { beforeEach } from "node:test";

import { TRPCError } from "@trpc/server";

import {
  accountKeyRow,
  behaviour,
  callCustomer,
  calls,
  grantRow,
  loadCustomerRouter,
  loadTRPC,
  onlyPortalSession,
  requestHeaders,
  resetHarness,
} from "./customer-portal-harness";

beforeEach(() => {
  resetHarness();
});

/** A signed-in context, built by the real `createTRPCContext`. */
async function contextFor(
  user: Record<string, unknown> | null,
  headers = requestHeaders(),
) {
  const { createTRPCContext } = await loadTRPC();
  behaviour.session = user ? { user } : null;
  return createTRPCContext({ headers });
}

function customerUser(overrides: Record<string, unknown> = {}) {
  return {
    id: "user_1",
    email: "Buyer@Example.com",
    name: "Buyer",
    emailVerified: true,
    createdAt: new Date("2026-01-01T00:00:00Z"),
    updatedAt: new Date("2026-01-01T00:00:00Z"),
    role: "user",
    ...overrides,
  };
}

// ---------------------------------------------------------------------------
// server/api/trpc.ts — the context
// ---------------------------------------------------------------------------

test("a request with no session gets a null user and no admin rights", async () => {
  const headers = requestHeaders({ "x-forwarded-for": "203.0.113.7" });
  const ctx = await contextFor(null, headers);

  assert.equal(ctx.user, null);
  assert.equal(ctx.isAdmin, false);
  // The middlewares and the routers read the IP and the rate-limit keys off
  // these headers, so the same object has to survive into the context.
  assert.equal(ctx.headers, headers);
  // Better Auth is asked with the request's own headers, not a fresh set.
  assert.deepEqual(calls.getSession, [headers]);
});

test("admin rights come from the session role, never from the request", async () => {
  const adminCtx = await contextFor(customerUser({ role: "admin" }));
  assert.equal(adminCtx.isAdmin, true);

  const plainCtx = await contextFor(customerUser({ role: "user" }));
  assert.equal(plainCtx.isAdmin, false);

  const noRoleCtx = await contextFor(customerUser({ role: undefined }));
  assert.equal(noRoleCtx.isAdmin, false);

  // A caller who spells "admin" into the request gets nothing for it.
  const spoofed = await contextFor(
    customerUser({ role: "user" }),
    requestHeaders({ "x-role": "admin", "x-admin": "true" }),
  );
  assert.equal(spoofed.isAdmin, false);
});

test("a role that merely contains admin is not the admin role", async () => {
  for (const role of ["administrator", "Admin", "admin ", "superadmin"]) {
    const ctx = await contextFor(customerUser({ role }));
    assert.equal(ctx.isAdmin, false, `role ${JSON.stringify(role)}`);
  }
});

// ---------------------------------------------------------------------------
// server/api/trpc.ts — the three procedure tiers
// ---------------------------------------------------------------------------

/**
 * A throwaway router over the REAL exported procedures. The admin middleware
 * has no other caller a test can reach without loading the whole admin
 * surface, and what is under test is the middleware, not the router.
 */
async function tierRouter() {
  const { createTRPCRouter, publicProcedure, protectedProcedure, adminProcedure } =
    await loadTRPC();
  return createTRPCRouter({
    open: publicProcedure.query(({ ctx }) => ({ user: ctx.user?.id ?? null })),
    signedIn: protectedProcedure.query(({ ctx }) => ({ user: ctx.user.id })),
    adminOnly: adminProcedure.query(({ ctx }) => ({ user: ctx.user.id })),
  });
}

async function callTier(path: string, ctx: unknown) {
  const { callTRPCProcedure } = await import("@trpc/server");
  const router = await tierRouter();
  return callTRPCProcedure({
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    router: router as any,
    path,
    getRawInput: async () => undefined,
    ctx,
    type: "query",
    signal: undefined,
    batchIndex: 0,
  });
}

test("publicProcedure runs for a signed-out caller", async () => {
  const ctx = await contextFor(null);
  assert.deepEqual(await callTier("open", ctx), { user: null });
});

test("protectedProcedure refuses a signed-out caller and admits a signed-in one", async () => {
  const anonymous = await contextFor(null);
  await assert.rejects(callTier("signedIn", anonymous), (error: unknown) => {
    assert.ok(error instanceof TRPCError);
    assert.equal(error.code, "UNAUTHORIZED");
    assert.equal(
      error.message,
      "You must be signed in to access this resource",
    );
    return true;
  });

  const signedIn = await contextFor(customerUser());
  assert.deepEqual(await callTier("signedIn", signedIn), { user: "user_1" });
});

test("adminProcedure separates signed-out from signed-in-but-not-admin", async () => {
  const anonymous = await contextFor(null);
  await assert.rejects(callTier("adminOnly", anonymous), (error: unknown) => {
    assert.ok(error instanceof TRPCError);
    assert.equal(error.code, "UNAUTHORIZED");
    return true;
  });

  const plain = await contextFor(customerUser({ role: "user" }));
  await assert.rejects(callTier("adminOnly", plain), (error: unknown) => {
    assert.ok(error instanceof TRPCError);
    // A signed-in non-admin must read FORBIDDEN, not UNAUTHORIZED: the client
    // sends the caller to sign in again on UNAUTHORIZED, which loops forever.
    assert.equal(error.code, "FORBIDDEN");
    assert.equal(error.message, "Admin access required");
    return true;
  });

  const admin = await contextFor(customerUser({ role: "admin" }));
  assert.deepEqual(await callTier("adminOnly", admin), { user: "user_1" });
});

test("the admin middleware does not trust isAdmin without a user", async () => {
  // A context hand-built with isAdmin true but no user is what a future bug in
  // createTRPCContext would produce. The middleware must still refuse.
  await assert.rejects(
    callTier("adminOnly", {
      user: null,
      isAdmin: true,
      headers: requestHeaders(),
    }),
    (error: unknown) => {
      assert.ok(error instanceof TRPCError);
      assert.equal(error.code, "UNAUTHORIZED");
      return true;
    },
  );
});

// ---------------------------------------------------------------------------
// server/api/routers/customer.ts — licensesWithCredits
// ---------------------------------------------------------------------------

test("two keys on one account report the pooled balance once", async () => {
  const ctx = await contextFor(customerUser());
  behaviour.accountKeys = [
    accountKeyRow({ id: "row_a", key: "HW-AAAA-0000-0001" }),
    accountKeyRow({ id: "row_b", key: "HW-BBBB-0000-0002" }),
  ];
  behaviour.balances.set("user_1", 1_000);

  const result = (await callCustomer("licensesWithCredits", ctx)) as {
    licenses: Array<{ id: string; credits: number; minutesRemaining: number }>;
    totalCredits: number;
    totalMinutesRemaining: number;
    creditsPerMinute: number;
  };

  // Credits are pooled per account. Each key shows the account balance, and
  // the account total is that balance ONCE — summing per key bills the
  // customer's own dashboard for credits they do not have.
  assert.deepEqual(
    result.licenses.map((l) => l.credits),
    [1_000, 1_000],
  );
  assert.equal(result.totalCredits, 1_000);
  assert.equal(result.creditsPerMinute, 1.67);
  assert.equal(result.totalMinutesRemaining, 598); // floor(1000 / 1.67)
  assert.deepEqual(
    result.licenses.map((l) => l.minutesRemaining),
    [598, 598],
  );
  // The balance read is asked once per DISTINCT owner, not once per key.
  assert.deepEqual(calls.getCreditBalancesForUsers, [["user_1"]]);
});

test("keys owned by different users add their balances together", async () => {
  const ctx = await contextFor(customerUser());
  behaviour.accountKeys = [
    accountKeyRow({ id: "row_a", userId: "user_1" }),
    accountKeyRow({ id: "row_b", userId: "user_2" }),
    accountKeyRow({ id: "row_c", userId: "user_2" }),
  ];
  behaviour.balances.set("user_1", 300);
  behaviour.balances.set("user_2", 700);

  const result = (await callCustomer("licensesWithCredits", ctx)) as {
    licenses: Array<{ credits: number }>;
    totalCredits: number;
  };

  assert.deepEqual(
    result.licenses.map((l) => l.credits),
    [300, 700, 700],
  );
  assert.equal(result.totalCredits, 1_000);
  assert.deepEqual(calls.getCreditBalancesForUsers, [["user_1", "user_2"]]);
});

test("an owner with no grants reads zero credits, not undefined", async () => {
  const ctx = await contextFor(customerUser());
  behaviour.accountKeys = [accountKeyRow({ userId: "user_9" })];

  const result = (await callCustomer("licensesWithCredits", ctx)) as {
    licenses: Array<{ credits: number; minutesRemaining: number }>;
    totalCredits: number;
    totalMinutesRemaining: number;
  };

  assert.equal(result.licenses[0].credits, 0);
  assert.equal(result.licenses[0].minutesRemaining, 0);
  assert.equal(result.totalCredits, 0);
  assert.equal(result.totalMinutesRemaining, 0);
});

test("a license row is reported field by field, with an ISO creation time", async () => {
  const ctx = await contextFor(customerUser());
  behaviour.accountKeys = [
    accountKeyRow({
      id: "row_a",
      key: "HW-AAAA-0000-0001",
      status: "revoked",
      stripeCustomerId: "cus_visible",
      polarCustomerId: "polar_visible",
      createdAt: new Date("2026-03-04T05:06:07.000Z"),
    }),
  ];
  behaviour.balances.set("user_1", 42);

  const result = (await callCustomer("licensesWithCredits", ctx)) as {
    licenses: Array<Record<string, unknown>>;
  };

  assert.deepEqual(result.licenses[0], {
    id: "row_a",
    key: "HW-AAAA-0000-0001",
    status: "revoked",
    credits: 42,
    minutesRemaining: 25, // floor(42 / 1.67)
    createdAt: "2026-03-04T05:06:07.000Z",
    stripeCustomerId: "cus_visible",
    polarCustomerId: "polar_visible",
  });
});

test("an account with no keys skips the balance read entirely", async () => {
  const ctx = await contextFor(customerUser());
  behaviour.accountKeys = [];

  const result = await callCustomer("licensesWithCredits", ctx);

  assert.deepEqual(result, {
    licenses: [],
    creditsPerMinute: 1.67,
    totalCredits: 0,
    totalMinutesRemaining: 0,
  });
  assert.deepEqual(calls.getCreditBalancesForUsers, []);
});

test("the email is lowercased before the database is asked", async () => {
  const ctx = await contextFor(customerUser({ email: "Mixed.Case@Example.COM" }));
  behaviour.accountKeys = [];

  await callCustomer("licensesWithCredits", ctx);
  await callCustomer("credits", ctx);
  await callCustomer("creditHistory", ctx);
  await callCustomer("billingProviders", ctx);

  assert.deepEqual(calls.getAccountKeysByEmail, [
    "mixed.case@example.com",
    "mixed.case@example.com",
    "mixed.case@example.com",
    "mixed.case@example.com",
  ]);
});

// ---------------------------------------------------------------------------
// server/api/routers/customer.ts — credits
// ---------------------------------------------------------------------------

test("credits pools the balance across a multi-key account", async () => {
  const ctx = await contextFor(customerUser());
  behaviour.accountKeys = [
    accountKeyRow({ id: "row_a", userId: "user_1" }),
    accountKeyRow({ id: "row_b", userId: "user_1" }),
    accountKeyRow({ id: "row_c", userId: "user_2" }),
  ];
  behaviour.balances.set("user_1", 2_000);
  behaviour.balances.set("user_2", 340);

  assert.deepEqual(await callCustomer("credits", ctx), {
    totalCredits: 2_340,
    minutesRemaining: 1_401, // floor(2340 / 1.67)
    creditsPerMinute: 1.67,
  });
  assert.deepEqual(calls.getCreditBalancesForUsers, [["user_1", "user_2"]]);
});

test("credits floors the minutes rather than rounding them up", async () => {
  const ctx = await contextFor(customerUser());
  behaviour.accountKeys = [accountKeyRow()];
  behaviour.balances.set("user_1", 1);

  // 1 / 1.67 is 0.59 of a minute. A customer with that much must not be told
  // they have a minute left.
  assert.deepEqual(await callCustomer("credits", ctx), {
    totalCredits: 1,
    minutesRemaining: 0,
    creditsPerMinute: 1.67,
  });
});

test("credits answers zero for an account with no keys", async () => {
  const ctx = await contextFor(customerUser());
  behaviour.accountKeys = [];

  assert.deepEqual(await callCustomer("credits", ctx), {
    totalCredits: 0,
    minutesRemaining: 0,
    creditsPerMinute: 1.67,
  });
  assert.deepEqual(calls.getCreditBalancesForUsers, []);
});

test("a signed-in user with no email is refused before any read", async () => {
  const ctx = await contextFor(customerUser({ email: undefined }));

  for (const path of ["licensesWithCredits", "credits", "creditHistory"]) {
    await assert.rejects(callCustomer(path, ctx), (error: unknown) => {
      assert.ok(error instanceof TRPCError);
      assert.equal(error.code, "BAD_REQUEST");
      assert.equal(error.message, "User email not found");
      return true;
    }, path);
  }

  await assert.rejects(
    callCustomer("stripePortalUrl", ctx, "mutation"),
    (error: unknown) => {
      assert.ok(error instanceof TRPCError);
      assert.equal(error.code, "BAD_REQUEST");
      return true;
    },
  );

  assert.deepEqual(calls.getAccountKeysByEmail, []);
});

test("every customer procedure refuses a signed-out caller", async () => {
  const anonymous = await contextFor(null);
  const paths: Array<[string, "query" | "mutation"]> = [
    ["licensesWithCredits", "query"],
    ["credits", "query"],
    ["creditHistory", "query"],
    ["billingProviders", "query"],
    ["stripePortalUrl", "mutation"],
  ];

  for (const [path, type] of paths) {
    await assert.rejects(callCustomer(path, anonymous, type), (error: unknown) => {
      assert.ok(error instanceof TRPCError);
      assert.equal(error.code, "UNAUTHORIZED");
      return true;
    }, path);
  }

  assert.deepEqual(calls.getAccountKeysByEmail, []);
  assert.deepEqual(calls.portalSessionCreate, []);
});

// ---------------------------------------------------------------------------
// server/api/routers/customer.ts — creditHistory
// ---------------------------------------------------------------------------

test("creditHistory reports each paid grant with its expiry state", async () => {
  const ctx = await contextFor(customerUser());
  behaviour.accountKeys = [
    accountKeyRow({ id: "row_a", userId: "user_1" }),
    accountKeyRow({ id: "row_b", userId: "user_1" }),
  ];
  behaviour.grants = [
    grantRow({
      id: "grant_live",
      createdAt: new Date("2026-05-01T00:00:00.000Z"),
      expiresAt: new Date("2099-01-01T00:00:00.000Z"),
      originalAmount: 5_000,
      remainingAmount: 4_200,
      status: "active",
    }),
    grantRow({
      id: "grant_gone",
      createdAt: new Date("2024-05-01T00:00:00.000Z"),
      expiresAt: new Date("2024-06-01T00:00:00.000Z"),
      originalAmount: 1_000,
      remainingAmount: 250,
      status: "expired",
    }),
    grantRow({
      id: "grant_forever",
      createdAt: new Date("2026-06-01T00:00:00.000Z"),
      expiresAt: null,
      originalAmount: 100,
      remainingAmount: 100,
      status: "active",
    }),
  ];

  const result = (await callCustomer("creditHistory", ctx)) as {
    grants: Array<Record<string, unknown>>;
  };

  assert.deepEqual(result.grants, [
    {
      id: "grant_live",
      createdAt: "2026-05-01T00:00:00.000Z",
      expiresAt: "2099-01-01T00:00:00.000Z",
      originalAmount: 5_000,
      remainingAmount: 4_200,
      expired: false,
      status: "active",
    },
    {
      id: "grant_gone",
      createdAt: "2024-05-01T00:00:00.000Z",
      expiresAt: "2024-06-01T00:00:00.000Z",
      originalAmount: 1_000,
      remainingAmount: 250,
      expired: true,
      status: "expired",
    },
    {
      id: "grant_forever",
      createdAt: "2026-06-01T00:00:00.000Z",
      expiresAt: null,
      originalAmount: 100,
      remainingAmount: 100,
      expired: false,
      status: "active",
    },
  ]);
  // The history is read once, for the distinct owners of the account's keys.
  assert.deepEqual(calls.getPaidCreditGrantsForUsers, [["user_1"]]);
});

test("creditHistory asks for every distinct owner of the account's keys", async () => {
  const ctx = await contextFor(customerUser());
  behaviour.accountKeys = [
    accountKeyRow({ id: "row_a", userId: "user_1" }),
    accountKeyRow({ id: "row_b", userId: "user_2" }),
    accountKeyRow({ id: "row_c", userId: "user_1" }),
  ];
  behaviour.grants = [
    grantRow({ id: "g1", userId: "user_1" }),
    grantRow({ id: "g2", userId: "user_2" }),
  ];

  const result = (await callCustomer("creditHistory", ctx)) as {
    grants: Array<{ id: string }>;
  };

  assert.deepEqual(calls.getPaidCreditGrantsForUsers, [["user_1", "user_2"]]);
  assert.deepEqual(
    result.grants.map((g) => g.id),
    ["g1", "g2"],
  );
});

test("creditHistory skips the grant read for an account with no keys", async () => {
  const ctx = await contextFor(customerUser());
  behaviour.accountKeys = [];

  assert.deepEqual(await callCustomer("creditHistory", ctx), { grants: [] });
  assert.deepEqual(calls.getPaidCreditGrantsForUsers, []);
});

// ---------------------------------------------------------------------------
// server/api/routers/customer.ts — billingProviders
// ---------------------------------------------------------------------------

test("billingProviders flags each provider the account has history with", async () => {
  const ctx = await contextFor(customerUser());

  behaviour.accountKeys = [
    accountKeyRow({ stripeCustomerId: "cus_1", polarCustomerId: null }),
  ];
  assert.deepEqual(await callCustomer("billingProviders", ctx), {
    hasStripe: true,
    hasPolar: false,
  });

  behaviour.accountKeys = [
    accountKeyRow({ stripeCustomerId: null, polarCustomerId: "polar_1" }),
  ];
  assert.deepEqual(await callCustomer("billingProviders", ctx), {
    hasStripe: false,
    hasPolar: true,
  });

  behaviour.accountKeys = [
    accountKeyRow({ id: "row_a", stripeCustomerId: null, polarCustomerId: null }),
    accountKeyRow({ id: "row_b", stripeCustomerId: "cus_2", polarCustomerId: null }),
    accountKeyRow({ id: "row_c", stripeCustomerId: null, polarCustomerId: "polar_2" }),
  ];
  assert.deepEqual(await callCustomer("billingProviders", ctx), {
    hasStripe: true,
    hasPolar: true,
  });

  behaviour.accountKeys = [];
  assert.deepEqual(await callCustomer("billingProviders", ctx), {
    hasStripe: false,
    hasPolar: false,
  });
});

test("billingProviders answers false for an emailless user without a read", async () => {
  const ctx = await contextFor(customerUser({ email: undefined }));

  // This one procedure reports "no history" rather than throwing, because the
  // dashboard only uses it to decide which billing button to draw.
  assert.deepEqual(await callCustomer("billingProviders", ctx), {
    hasStripe: false,
    hasPolar: false,
  });
  assert.deepEqual(calls.getAccountKeysByEmail, []);
});

// ---------------------------------------------------------------------------
// server/api/routers/customer.ts — stripePortalUrl
// ---------------------------------------------------------------------------

test("stripePortalUrl opens the portal for the first key with a Stripe customer", async () => {
  const ctx = await contextFor(customerUser());
  behaviour.accountKeys = [
    accountKeyRow({ id: "row_a", stripeCustomerId: null }),
    accountKeyRow({ id: "row_b", stripeCustomerId: "cus_wanted" }),
    accountKeyRow({ id: "row_c", stripeCustomerId: "cus_later" }),
  ];
  behaviour.portalUrl = "https://billing.stripe.test/session/bps_wanted";

  const previous = process.env.NEXT_PUBLIC_SITE_URL;
  process.env.NEXT_PUBLIC_SITE_URL = "https://staging.hyperwhisper.test";
  try {
    const result = await callCustomer("stripePortalUrl", ctx, "mutation");
    assert.deepEqual(result, {
      url: "https://billing.stripe.test/session/bps_wanted",
    });
  } finally {
    if (previous === undefined) delete process.env.NEXT_PUBLIC_SITE_URL;
    else process.env.NEXT_PUBLIC_SITE_URL = previous;
  }

  assert.deepEqual(onlyPortalSession(), {
    customer: "cus_wanted",
    return_url: "https://staging.hyperwhisper.test/user",
  });
});

test("stripePortalUrl falls back to the production site for the return URL", async () => {
  const ctx = await contextFor(customerUser());
  behaviour.accountKeys = [accountKeyRow({ stripeCustomerId: "cus_1" })];

  const previous = process.env.NEXT_PUBLIC_SITE_URL;
  delete process.env.NEXT_PUBLIC_SITE_URL;
  try {
    await callCustomer("stripePortalUrl", ctx, "mutation");
  } finally {
    if (previous !== undefined) process.env.NEXT_PUBLIC_SITE_URL = previous;
  }

  assert.equal(onlyPortalSession().return_url, "https://hyperwhisper.com/user");
});

test("stripePortalUrl refuses when no key carries a Stripe customer", async () => {
  const ctx = await contextFor(customerUser());
  behaviour.accountKeys = [
    accountKeyRow({ id: "row_a", stripeCustomerId: null, polarCustomerId: "polar_1" }),
    accountKeyRow({ id: "row_b", stripeCustomerId: null }),
  ];

  await assert.rejects(
    callCustomer("stripePortalUrl", ctx, "mutation"),
    (error: unknown) => {
      assert.ok(error instanceof TRPCError);
      assert.equal(error.code, "NOT_FOUND");
      assert.equal(error.message, "No Stripe billing history found");
      return true;
    },
  );
  // Stripe is never asked for a session the account cannot have.
  assert.deepEqual(calls.portalSessionCreate, []);
});

test("stripePortalUrl refuses an account with no keys at all", async () => {
  const ctx = await contextFor(customerUser());
  behaviour.accountKeys = [];

  await assert.rejects(
    callCustomer("stripePortalUrl", ctx, "mutation"),
    (error: unknown) => {
      assert.ok(error instanceof TRPCError);
      assert.equal(error.code, "NOT_FOUND");
      return true;
    },
  );
  assert.deepEqual(calls.portalSessionCreate, []);
});

// ---------------------------------------------------------------------------
// The router's own shape
// ---------------------------------------------------------------------------

test("the customer router exposes exactly the portal procedures", async () => {
  const { customerRouter } = await loadCustomerRouter();
  const record = (
    customerRouter as unknown as { _def: { record: Record<string, unknown> } }
  )._def.record;

  assert.deepEqual(Object.keys(record).sort(), [
    "billingProviders",
    "creditHistory",
    "credits",
    "licensesWithCredits",
    "stripePortalUrl",
  ]);
});
