/**
 * Admin MCP endpoint (`app/api/mcp/admin/route.ts`).
 *
 * The route is a credits door for an agent gateway, so the tests pin: the
 * bearer gate (401 without, with a wrong, and with no configured token), the
 * MCP handshake and tool catalogue, the two grant bands and their hand-off to
 * each other, idempotency (a retried key grants once), and both lookups.
 *
 * Two modules are faked with `mock.module`: the validated env (a mutable
 * object, so a test can unset the token) and the database layer, which is an
 * in-memory ledger keyed the way `credit_grants` is: (sourceType, sourceId)
 * unique, so a repeated source id is a `duplicate` exactly as in Postgres.
 */
import assert from "node:assert/strict";
import { beforeEach, test } from "node:test";
import { NextRequest } from "next/server";

import { captureConsole, moduleMock, moduleUrl } from "./module-mock";

const TOKEN = ["test", "mcp", "token"].join("-").padEnd(40, "x");

const fakeEnv: Record<string, string | undefined> = {
  HYPERWHISPER_ADMIN_MCP_TOKEN: TOKEN,
};

moduleMock.module(moduleUrl("../src/env/server.mjs"), {
  namedExports: { env: fakeEnv },
});

interface License {
  id: string;
  key: string;
  email: string;
  userId: string;
  status: string;
  polarLicenseKeyId: null;
  polarCustomerId: null;
  stripeCustomerId: string | null;
  stripeSessionId: null;
  createdAt: Date;
}

interface Grant {
  id: string;
  userId: string;
  sourceType: string;
  sourceId: string;
  originalAmount: number;
  remainingAmount: number;
  refundedAmount: number;
  status: string;
  createdAt: Date;
  expiresAt: Date | null;
}

const store = {
  users: new Map<string, { id: string; email: string }>(),
  licenses: [] as License[],
  grants: [] as Grant[],
  grantCalls: 0,
  staleGranted: false,
};

function balanceOf(userId: string): number {
  return store.grants
    .filter((g) => g.userId === userId && g.status === "active")
    .reduce((sum, g) => sum + g.remainingAmount, 0);
}

moduleMock.module(moduleUrl("../src/lib/db-layer.ts"), {
  namedExports: {
    findAccountByKey: async (key: string) => {
      const found = store.licenses.find((l) => l.key === key.trim());

      // A stale read: what a lookup outside the grant transaction could see.
      return found && store.staleGranted ? { ...found, status: "granted" } : (found ?? null);
    },
    getAccountKeysByEmail: async (email: string) =>
      store.licenses.filter((l) => l.email === email.toLowerCase()),
    getUserByEmail: async (email: string) =>
      Array.from(store.users.values()).find((u) => u.email === email) ?? null,
    getUsersByIds: async (ids: string[]) =>
      new Map(ids.flatMap((id) => (store.users.has(id) ? [[id, store.users.get(id)]] : []))),
    // Like the real query: the newest ACCOUNT_KEYS_PER_USER_ROW_LIMIT keys
    // per account only.
    ACCOUNT_KEYS_PER_USER_ROW_LIMIT: 100,
    getAccountKeysWithCreditsForUserIds: async (ids: string[]) =>
      ids.flatMap((id) =>
        store.licenses
          .filter((l) => l.userId === id)
          .sort((a, b) => b.createdAt.getTime() - a.createdAt.getTime())
          .slice(0, 100)
          .map((l) => ({ ...l, credits: balanceOf(l.userId) })),
      ),
    // Like the real query: every key, NO per-account cap.
    getStripeCustomerIdsForUserIds: async (ids: string[]) => {
      const byUser = new Map<string, Set<string>>();

      for (const l of store.licenses) {
        if (!l.stripeCustomerId || !ids.includes(l.userId)) continue;
        const set = byUser.get(l.userId) ?? new Set<string>();

        set.add(l.stripeCustomerId);
        byUser.set(l.userId, set);
      }

      return byUser;
    },
    getCreditBalance: async (userId: string) => balanceOf(userId),
    // Like the real query: newest first, at most `limit` rows PER account.
    getCreditGrantsForUsers: async (ids: string[], limit: number) => {
      const truncatedUserIds = new Set<string>();
      const rows = ids.flatMap((id) => {
        const own = store.grants
          .filter((g) => g.userId === id)
          .sort((a, b) => b.createdAt.getTime() - a.createdAt.getTime());

        if (own.length > limit) truncatedUserIds.add(id);

        return own.slice(0, limit);
      });

      return { rows, truncatedUserIds };
    },
    findCreditGrantBySource: async (sourceType: string, sourceId: string) =>
      store.grants.find((g) => g.sourceType === sourceType && g.sourceId === sourceId) ?? null,
    // The real one reads the key FOR SHARE inside the grant's transaction.
    // Here the status is read from the store at grant time, which is the
    // point: a status read any earlier must not decide the grant.
    grantCreditLotToGrantedKey: async (
      key: string,
      data: { amount: number; sourceType: string; sourceId: string },
    ) => {
      store.grantCalls += 1;
      const license = store.licenses.find((l) => l.key === key.trim());

      if (!license) return { status: "license_not_found" };
      if (license.status !== "granted") {
        return { status: "license_not_granted", license: { ...license } };
      }
      const exists = store.grants.some(
        (g) => g.sourceType === data.sourceType && g.sourceId === data.sourceId,
      );

      if (exists) {
        return { status: "duplicate", balance: balanceOf(license.userId), license };
      }
      store.grants.push({
        id: `grant-${store.grants.length + 1}`,
        userId: license.userId,
        sourceType: data.sourceType,
        sourceId: data.sourceId,
        originalAmount: data.amount,
        remainingAmount: data.amount,
        refundedAmount: 0,
        status: "active",
        createdAt: new Date("2026-11-18T00:00:00Z"),
        expiresAt: null,
      });

      return { status: "processed", balance: balanceOf(license.userId), license };
    },
  },
});

// Stripe, as the shared customer spend reader calls it. A customer id mapped
// to an Error makes its charge list fail.
const stripeCharges = new Map<string, Array<Record<string, unknown>> | Error>();
const stripeDisputes = new Map<string, string>();

moduleMock.module(moduleUrl("../lib/clients/stripe.ts"), {
  namedExports: {
    stripe: {
      charges: {
        list: (params: { customer: string }) => ({
          autoPagingToArray: async () => {
            const answer = stripeCharges.get(params.customer) ?? [];

            if (answer instanceof Error) throw answer;

            return answer;
          },
        }),
      },
      disputes: {
        list: async (params: { charge: string }) => {
          const status = stripeDisputes.get(params.charge);

          return { data: status ? [{ status }] : [] };
        },
      },
    },
    stripeRefundApi: {},
  },
});

let route: typeof import("../app/api/mcp/admin/route");

test.before(async () => {
  route = await import("../app/api/mcp/admin/route");
});

const ACTIVE_KEY = "HW-TEST-0000-1111";
const REFUNDED_KEY = "HW-TEST-0000-2222";

beforeEach(() => {
  fakeEnv.HYPERWHISPER_ADMIN_MCP_TOKEN = TOKEN;
  store.users.clear();
  store.users.set("user-1", { id: "user-1", email: "buyer@example.com" });
  store.users.set("user-2", { id: "user-2", email: "refunded@example.com" });
  store.licenses = [
    {
      id: "lic-1",
      key: ACTIVE_KEY,
      email: "buyer@example.com",
      userId: "user-1",
      status: "granted",
      polarLicenseKeyId: null,
      polarCustomerId: null,
      stripeCustomerId: "cus_test_1",
      stripeSessionId: null,
      createdAt: new Date("2025-01-02T00:00:00Z"),
    },
    {
      id: "lic-2",
      key: REFUNDED_KEY,
      email: "refunded@example.com",
      userId: "user-2",
      status: "refunded",
      polarLicenseKeyId: null,
      polarCustomerId: null,
      stripeCustomerId: null,
      stripeSessionId: null,
      createdAt: new Date("2025-02-03T00:00:00Z"),
    },
  ];
  store.grants = [
    {
      id: "grant-signup",
      userId: "user-1",
      sourceType: "license_bundle",
      sourceId: "lic-1",
      originalAmount: 5000,
      remainingAmount: 1200,
      refundedAmount: 0,
      status: "active",
      createdAt: new Date("2025-01-02T00:00:00Z"),
      expiresAt: null,
    },
  ];
  store.grantCalls = 0;
  store.staleGranted = false;
  stripeCharges.clear();
  stripeDisputes.clear();
});

function post(body: unknown, auth: string | null = `Bearer ${TOKEN}`): NextRequest {
  const headers: Record<string, string> = {
    "content-type": "application/json",
    accept: "application/json, text/event-stream",
  };
  if (auth !== null) headers.authorization = auth;
  return new NextRequest("http://localhost/api/mcp/admin", {
    method: "POST",
    headers,
    body: typeof body === "string" ? body : JSON.stringify(body),
  });
}

let nextId = 1;
async function rpc(method: string, params?: Record<string, unknown>) {
  const res = await route.POST(post({ jsonrpc: "2.0", id: nextId++, method, params }));
  assert.equal(res.status, 200);
  return res.json();
}

async function callTool(name: string, args: Record<string, unknown>) {
  const capture = captureConsole(["log", "warn", "error"]);
  try {
    const body = await rpc("tools/call", { name, arguments: args });
    assert.equal(body.error, undefined, JSON.stringify(body.error));
    return { result: body.result, logs: capture.lines };
  } finally {
    capture.restore();
  }
}

function grantArgs(credits: number, idempotencyKey = `idem-key-${credits}-${nextId}`) {
  return { licenseKey: ACTIVE_KEY, credits, reason: "pre-open-source purchase", idempotencyKey };
}

// ---------------------------------------------------------------------------
// Auth
// ---------------------------------------------------------------------------

test("401 without a bearer token", async () => {
  const res = await route.POST(post({ jsonrpc: "2.0", id: 1, method: "ping" }, null));
  assert.equal(res.status, 401);
  assert.match(res.headers.get("www-authenticate") ?? "", /^Bearer/);
});

test("401 with a wrong bearer token", async () => {
  const res = await route.POST(
    post({ jsonrpc: "2.0", id: 1, method: "ping" }, `Bearer ${TOKEN}x`),
  );
  assert.equal(res.status, 401);
});

test("401 for the right token when the env var is unset or empty", async () => {
  for (const value of [undefined, ""]) {
    fakeEnv.HYPERWHISPER_ADMIN_MCP_TOKEN = value;
    const res = await route.POST(post({ jsonrpc: "2.0", id: 1, method: "ping" }));
    assert.equal(res.status, 401);
    // An empty bearer against an empty secret must not match either.
    const empty = await route.POST(post({ jsonrpc: "2.0", id: 1, method: "ping" }, "Bearer "));
    assert.equal(empty.status, 401);
  }
});

test("GET is 401 unauthenticated and 405 authenticated", async () => {
  const anon = await route.GET(new NextRequest("http://localhost/api/mcp/admin"));
  assert.equal(anon.status, 401);
  const authed = await route.GET(
    new NextRequest("http://localhost/api/mcp/admin", {
      headers: { authorization: `Bearer ${TOKEN}` },
    }),
  );
  assert.equal(authed.status, 405);
  assert.equal(authed.headers.get("allow"), "POST");
});

test("a grant attempt without the token writes nothing", async () => {
  const res = await route.POST(
    post(
      {
        jsonrpc: "2.0",
        id: 1,
        method: "tools/call",
        params: { name: "add_credits", arguments: grantArgs(100) },
      },
      "Bearer wrong",
    ),
  );
  assert.equal(res.status, 401);
  assert.equal(store.grantCalls, 0);
});

// ---------------------------------------------------------------------------
// Protocol
// ---------------------------------------------------------------------------

test("initialize echoes a supported protocol version and offers tools", async () => {
  const body = await rpc("initialize", {
    protocolVersion: "2025-06-18",
    capabilities: {},
    clientInfo: { name: "test", version: "1" },
  });
  assert.equal(body.jsonrpc, "2.0");
  assert.equal(body.result.protocolVersion, "2025-06-18");
  assert.deepEqual(body.result.capabilities, { tools: { listChanged: false } });
  assert.equal(body.result.serverInfo.name, "hyperwhisper-admin");
});

test("initialize offers the newest version for an unknown one", async () => {
  const body = await rpc("initialize", { protocolVersion: "1999-01-01" });
  assert.equal(body.result.protocolVersion, "2025-11-25");
});

test("a notification is 202 with no body", async () => {
  const res = await route.POST(
    post({ jsonrpc: "2.0", method: "notifications/initialized" }),
  );
  assert.equal(res.status, 202);
  assert.equal(await res.text(), "");
});

test("ping answers an empty result", async () => {
  const body = await rpc("ping");
  assert.deepEqual(body.result, {});
});

test("an unknown method is -32601 and bad JSON is -32700", async () => {
  const body = await rpc("resources/list");
  assert.equal(body.error.code, -32601);

  const res = await route.POST(post("{not json"));
  assert.equal(res.status, 400);
  assert.equal((await res.json()).error.code, -32700);
});

test("an unknown tool is -32602", async () => {
  const body = await rpc("tools/call", { name: "drop_tables", arguments: {} });
  assert.equal(body.error.code, -32602);
});

test("a batch answers its requests in order and skips notifications", async () => {
  const res = await route.POST(
    post([
      { jsonrpc: "2.0", method: "notifications/initialized" },
      { jsonrpc: "2.0", id: "a", method: "ping" },
      { jsonrpc: "2.0", id: "b", method: "tools/list" },
    ]),
  );
  assert.equal(res.status, 200);
  const body = await res.json();
  assert.deepEqual(
    body.map((r: { id: string }) => r.id),
    ["a", "b"],
  );
});

test("tools/list returns the three tools with their annotations", async () => {
  const body = await rpc("tools/list");
  const tools = body.result.tools as Array<{
    name: string;
    description: string;
    inputSchema: { type: string; required?: string[]; properties: Record<string, { minimum?: number; maximum?: number }> };
    annotations: Record<string, boolean>;
  }>;
  assert.deepEqual(
    tools.map((t) => t.name),
    ["find_customer", "add_credits", "add_credits_large"],
  );
  const [find, add, large] = tools;
  assert.equal(find.annotations.readOnlyHint, true);
  assert.equal(add.annotations.readOnlyHint, false);
  assert.equal(add.annotations.destructiveHint, false);
  assert.equal(add.annotations.idempotentHint, true);
  assert.equal(large.annotations.destructiveHint, true);
  assert.equal(large.annotations.idempotentHint, true);
  assert.match(large.description, /approval/i);
  for (const t of tools) assert.equal(t.inputSchema.type, "object");
  assert.deepEqual(add.inputSchema.required, ["licenseKey", "credits", "reason", "idempotencyKey"]);
  assert.equal(add.inputSchema.properties.credits.minimum, 1);
  assert.equal(add.inputSchema.properties.credits.maximum, 30_000);
  assert.equal(large.inputSchema.properties.credits.minimum, 30_001);
  assert.equal(large.inputSchema.properties.credits.maximum, 1_000_000);
});

// ---------------------------------------------------------------------------
// add_credits bands
// ---------------------------------------------------------------------------

test("add_credits refuses 0 credits", async () => {
  const { result } = await callTool("add_credits", grantArgs(0));
  assert.equal(result.isError, true);
  assert.match(result.content[0].text, /at least 1/);
  assert.equal(store.grantCalls, 0);
});

test("add_credits grants exactly 30,000 and reports the new balance", async () => {
  const { result, logs } = await callTool("add_credits", grantArgs(30_000, "first-grant"));
  assert.equal(result.isError, undefined);
  assert.equal(result.structuredContent.status, "processed");
  assert.equal(result.structuredContent.creditsAdded, 30_000);
  assert.equal(result.structuredContent.newBalance, 31_200);

  const grant = store.grants.at(-1)!;
  assert.equal(grant.userId, "user-1");
  assert.equal(grant.sourceType, "admin_manual");
  assert.match(grant.sourceId, /^mcp:[0-9a-f]{64}$/);

  // One structured audit line, ids only: no key string, no email, no token.
  const line = logs.find((l) => l.includes("admin.mcp.add_credits"));
  assert.ok(line, logs.join("\n"));
  const parsed = JSON.parse(line.slice(line.indexOf("{")));
  assert.deepEqual(
    { ...parsed },
    {
      event: "admin.mcp.add_credits",
      outcome: "ok",
      tool: "add_credits",
      amount: 30_000,
      sourceId: grant.sourceId,
      reasonRedacted: "pre-open-source purchase",
      reasonLength: "pre-open-source purchase".length,
      licenseKeyId: "lic-1",
      userId: "user-1",
      newBalance: 31_200,
      status: "processed",
    },
  );
  const all = logs.join("\n");
  assert.ok(!all.includes(ACTIVE_KEY));
  assert.ok(!all.includes("buyer@example.com"));
  assert.ok(!all.includes(TOKEN));
});

test("add_credits refuses 30,001 and points to add_credits_large", async () => {
  const { result } = await callTool("add_credits", grantArgs(30_001));
  assert.equal(result.isError, true);
  assert.match(result.content[0].text, /add_credits_large/);
  assert.equal(store.grantCalls, 0);
});

test("add_credits_large refuses 30,000 or less and points back", async () => {
  for (const credits of [30_000, 1]) {
    const { result } = await callTool("add_credits_large", grantArgs(credits));
    assert.equal(result.isError, true);
    assert.match(result.content[0].text, /use add_credits\b/);
  }
  assert.equal(store.grantCalls, 0);
});

test("add_credits_large grants 30,001 up to 1,000,000 and refuses more", async () => {
  const ok = await callTool("add_credits_large", grantArgs(30_001));
  assert.equal(ok.result.structuredContent.status, "processed");
  const max = await callTool("add_credits_large", grantArgs(1_000_000));
  assert.equal(max.result.structuredContent.status, "processed");
  const over = await callTool("add_credits_large", grantArgs(1_000_001));
  assert.equal(over.result.isError, true);
});

test("validation failures are isError results, not 500s", async () => {
  for (const args of [
    { licenseKey: ACTIVE_KEY, credits: 10.5, reason: "x", idempotencyKey: "abcdefgh" },
    { licenseKey: ACTIVE_KEY, credits: 10, idempotencyKey: "abcdefgh" },
    { licenseKey: ACTIVE_KEY, credits: 10, reason: "x" },
    { licenseKey: ACTIVE_KEY, credits: "10", reason: "x", idempotencyKey: "abcdefgh" },
    { licenseKey: ACTIVE_KEY, credits: 10, reason: "x", idempotencyKey: "abcdefgh", extra: 1 },
  ]) {
    const { result } = await callTool("add_credits", args);
    assert.equal(result.isError, true, JSON.stringify(args));
    assert.match(result.content[0].text, /^Invalid arguments for add_credits/);
  }
  assert.equal(store.grantCalls, 0);
});

test("a refunded or unknown license key is refused", async () => {
  const refunded = await callTool("add_credits", { ...grantArgs(100), licenseKey: REFUNDED_KEY });
  assert.equal(refunded.result.isError, true);
  assert.match(refunded.result.content[0].text, /refunded/);

  const missing = await callTool("add_credits", { ...grantArgs(100), licenseKey: "HW-NOPE" });
  assert.equal(missing.result.isError, true);
  assert.match(missing.result.content[0].text, /not found/);
  assert.equal(store.grants.filter((g) => g.sourceType === "admin_manual").length, 0);
});

test("a key revoked after any earlier read is refused by the in-transaction check", async () => {
  // Every out-of-transaction lookup still says "granted"; only the read the
  // grant itself makes sees the revocation.
  store.licenses[0].status = "revoked";
  store.staleGranted = true;
  const { result, logs } = await callTool("add_credits", grantArgs(500));

  assert.equal(result.isError, true);
  assert.match(result.content[0].text, /License key is revoked/);
  assert.equal(store.grants.filter((g) => g.sourceType === "admin_manual").length, 0);
  assert.ok(logs.some((l) => l.includes('"rejection":"license_not_active"')), logs.join("\n"));
});

// ---------------------------------------------------------------------------
// Idempotency
// ---------------------------------------------------------------------------

test("the same idempotencyKey twice grants once and the retry is duplicate", async () => {
  const args = grantArgs(9_000, "refund-make-good-001");
  const first = await callTool("add_credits", args);
  const second = await callTool("add_credits", args);

  assert.equal(first.result.structuredContent.status, "processed");
  assert.equal(second.result.structuredContent.status, "duplicate");
  assert.equal(second.result.structuredContent.creditsAdded, 0);
  assert.equal(second.result.structuredContent.newBalance, 10_200);
  assert.equal(store.grants.filter((g) => g.sourceType === "admin_manual").length, 1);
});

test("a reused idempotencyKey for a different amount is refused, not swallowed", async () => {
  await callTool("add_credits", grantArgs(9_000, "reused-key-0001"));
  const { result } = await callTool("add_credits", grantArgs(8_000, "reused-key-0001"));
  assert.equal(result.isError, true);
  assert.match(result.content[0].text, /already used for a different grant/);
  assert.equal(balanceOf("user-1"), 10_200);
});

// ---------------------------------------------------------------------------
// find_customer
// ---------------------------------------------------------------------------

test("find_customer by email returns licenses, balance and grants", async () => {
  const { result } = await callTool("find_customer", { email: "  Buyer@Example.com " });
  assert.equal(result.isError, undefined);
  const [customer] = result.structuredContent.customers;
  assert.equal(customer.userId, "user-1");
  assert.equal(customer.email, "buyer@example.com");
  assert.equal(customer.creditBalance, 1200);
  assert.deepEqual(customer.licenses, [
    {
      id: "lic-1",
      key: ACTIVE_KEY,
      status: "granted",
      email: "buyer@example.com",
      createdAt: "2025-01-02T00:00:00.000Z",
      matched: true,
      purchase: {
        stripeCustomerId: "cus_test_1",
        stripeCheckoutSessionId: null,
        polarCustomerId: null,
      },
    },
  ]);
  assert.equal(customer.creditGrantsTruncated, false);
  assert.equal(customer.licensesTruncated, undefined);
  assert.deepEqual(customer.creditGrants, [
    {
      sourceType: "license_bundle",
      originalAmount: 5000,
      remainingAmount: 1200,
      refundedAmount: 0,
      status: "active",
      createdAt: "2025-01-02T00:00:00.000Z",
      expiresAt: null,
    },
  ]);
  // The text content carries the same data for clients that ignore structuredContent.
  assert.deepEqual(JSON.parse(result.content[0].text), result.structuredContent);
});

test("find_customer by license key finds the owning account", async () => {
  const { result } = await callTool("find_customer", { licenseKey: REFUNDED_KEY });
  const [customer] = result.structuredContent.customers;
  assert.equal(customer.userId, "user-2");
  assert.equal(customer.licenses[0].status, "refunded");
});

test("find_customer with no match returns an empty list", async () => {
  const { result } = await callTool("find_customer", { email: "nobody@example.com" });
  assert.deepEqual(result.structuredContent.customers, []);
});

test("find_customer needs exactly one of email or licenseKey", async () => {
  for (const args of [{}, { email: "buyer@example.com", licenseKey: ACTIVE_KEY }]) {
    const { result } = await callTool("find_customer", args);
    assert.equal(result.isError, true);
    assert.match(result.content[0].text, /exactly one/);
  }
});

// ---------------------------------------------------------------------------
// Text content beside structuredContent (follow-up 1)
// ---------------------------------------------------------------------------

test("every tool result carries a text block on the wire, success and error", async () => {
  const calls: Array<[string, Record<string, unknown>]> = [
    ["find_customer", { email: "buyer@example.com" }],
    ["find_customer", { email: "nobody@example.com" }],
    ["add_credits", grantArgs(100, "text-block-0001")],
    ["add_credits", grantArgs(0)],
    ["find_customer", {}],
  ];

  for (const [name, args] of calls) {
    const capture = captureConsole(["log", "warn", "error"]);
    let body: { result: { content: Array<{ type: string; text: string }>; structuredContent?: unknown; isError?: boolean } };

    try {
      const res = await route.POST(
        post({ jsonrpc: "2.0", id: 1, method: "tools/call", params: { name, arguments: args } }),
      );

      body = JSON.parse(await res.text());
    } finally {
      capture.restore();
    }
    const { content, structuredContent, isError } = body.result;

    assert.equal(content.length, 1, `${name} ${JSON.stringify(args)}`);
    assert.equal(content[0].type, "text");
    assert.ok(content[0].text.length > 0);
    if (structuredContent !== undefined) {
      // The text block is the same data, serialised.
      assert.deepEqual(JSON.parse(content[0].text), structuredContent);
    } else {
      assert.equal(isError, true);
    }
  }
});

// ---------------------------------------------------------------------------
// Spend (follow-up 2)
// ---------------------------------------------------------------------------

test("find_customer reports the Customers page's total spend and the charges behind it", async () => {
  stripeCharges.set("cus_test_1", [
    { id: "ch_license", status: "succeeded", disputed: false, amount: 1500, amount_refunded: 0, created: 1735776000, description: "HyperWhisper license" },
    { id: "ch_pack", status: "succeeded", disputed: false, amount: 1200, amount_refunded: 165, created: 1736000000, description: "Credit pack" },
    { id: "ch_failed", status: "failed", disputed: false, amount: 999, amount_refunded: 0, created: 1736100000 },
    { id: "ch_lost", status: "succeeded", disputed: true, amount: 500, amount_refunded: 0, created: 1736200000 },
  ]);
  stripeDisputes.set("ch_lost", "lost");

  const { result } = await callTool("find_customer", { licenseKey: ACTIVE_KEY });
  const [customer] = result.structuredContent.customers;

  assert.equal(customer.totalSpentCents, 2535);
  assert.equal(customer.totalSpentUsd, 25.35);
  assert.equal(customer.hasStripeCustomer, true);
  assert.match(customer.totalSpentNote, /credit-pack/);
  assert.deepEqual(
    customer.charges.map((c: { id: string; countedCents: number; excluded?: string }) => [c.id, c.countedCents, c.excluded ?? null]),
    [
      ["ch_license", 1500, null],
      ["ch_pack", 1035, null],
      ["ch_failed", 0, "not_succeeded"],
      ["ch_lost", 0, "dispute_not_won"],
    ],
  );
  assert.equal(customer.charges[0].stripeCustomerId, "cus_test_1");
  assert.equal(customer.charges[0].createdAt, "2025-01-02T00:00:00.000Z");
  assert.equal(customer.charges[0].description, "HyperWhisper license");
});

test("find_customer spend is null when Stripe fails, and 0 without a Stripe customer", async () => {
  stripeCharges.set("cus_test_1", new Error("stripe down"));
  const capture = captureConsole(["error"]);

  try {
    const failed = await callTool("find_customer", { licenseKey: ACTIVE_KEY });
    const [customer] = failed.result.structuredContent.customers;

    assert.equal(customer.totalSpentCents, null);
    assert.equal(customer.totalSpentUsd, null);
    assert.deepEqual(customer.spendUnavailableFor, ["cus_test_1"]);
  } finally {
    capture.restore();
  }

  const none = await callTool("find_customer", { licenseKey: REFUNDED_KEY });
  const [customer] = none.result.structuredContent.customers;

  assert.equal(customer.totalSpentCents, 0);
  assert.equal(customer.hasStripeCustomer, false);
  assert.deepEqual(customer.charges, []);
});

// ---------------------------------------------------------------------------
// Reason redaction (follow-up 3)
// ---------------------------------------------------------------------------

test("a grant logs a masked reason and its length, never the raw reason", async () => {
  const reason = `paid $25.35 as buyer@example.com for ${ACTIVE_KEY}, ref ${"a".repeat(30)}`;
  const { result, logs } = await callTool("add_credits", { ...grantArgs(20_350, "redact-reason-01"), reason });

  assert.equal(result.structuredContent.status, "processed");
  const line = logs.find((l) => l.includes("admin.mcp.add_credits"));

  assert.ok(line);
  const parsed = JSON.parse(line.slice(line.indexOf("{")));

  assert.equal(parsed.reason, undefined);
  assert.equal(parsed.reasonLength, reason.length);
  assert.equal(parsed.reasonRedacted, "paid $25.35 as [email] for [license-key], ref [token]");
  const all = logs.join("\n");

  assert.ok(!all.includes("buyer@example.com"));
  assert.ok(!all.includes(ACTIVE_KEY));
});

test("a refused grant also logs only the masked reason", async () => {
  const reason = "for buyer@example.com";
  const { logs } = await callTool("add_credits", { ...grantArgs(30_001), reason });

  assert.ok(logs.some((l) => l.includes('"rejection":"out_of_band"')));
  assert.ok(!logs.join("\n").includes("buyer@example.com"));
});

// ---------------------------------------------------------------------------
// Lookup limits (follow-up 4)
// ---------------------------------------------------------------------------

test("find_customer by an old key past the 100-key cap still returns that key", async () => {
  // The looked-up key is the OLDEST of 101 on the account, so the capped
  // license list leaves it out.
  for (let i = 0; i < 100; i++) {
    store.licenses.push({
      ...store.licenses[0],
      id: `lic-new-${i}`,
      key: `HW-TEST-NEW0-${String(i).padStart(4, "0")}`,
      createdAt: new Date(Date.UTC(2026, 0, 1, 0, i)),
    });
  }
  const { result } = await callTool("find_customer", { licenseKey: ACTIVE_KEY });
  const [customer] = result.structuredContent.customers;
  const matched = customer.licenses.filter((l: { matched: boolean }) => l.matched);

  assert.equal(customer.licenses.length, 101);
  assert.equal(matched.length, 1);
  assert.equal(matched[0].key, ACTIVE_KEY);
  assert.equal(customer.licensesTruncated, true);
  assert.match(customer.licensesNote, /newest 100/);
});

test("grant history is limited per account and says when it was cut", async () => {
  // Two accounts share an email match; user-1 has 205 grants, user-2 has 1.
  store.licenses[1].email = "buyer@example.com";
  for (let i = 0; i < 204; i++) {
    store.grants.push({
      ...store.grants[0],
      id: `bulk-${i}`,
      sourceType: "admin_manual",
      sourceId: `bulk-${i}`,
      createdAt: new Date(Date.UTC(2026, 0, 1, 0, 0, i)),
    });
  }
  store.grants.push({ ...store.grants[0], id: "u2-grant", userId: "user-2", sourceId: "u2" });

  const { result } = await callTool("find_customer", { email: "buyer@example.com" });
  const byId = new Map(
    result.structuredContent.customers.map((c: { userId: string }) => [c.userId, c]),
  ) as Map<string, { creditGrants: unknown[]; creditGrantsTruncated: boolean; creditGrantsNote?: string }>;

  assert.equal(byId.get("user-1")?.creditGrants.length, 200);
  assert.equal(byId.get("user-1")?.creditGrantsTruncated, true);
  assert.match(byId.get("user-1")?.creditGrantsNote ?? "", /newest 200/);
  assert.equal(byId.get("user-2")?.creditGrants.length, 1);
  assert.equal(byId.get("user-2")?.creditGrantsTruncated, false);
});

// ---------------------------------------------------------------------------
// Review round 1 on #3056
// ---------------------------------------------------------------------------

test("a license key right after a word character is still masked", async () => {
  const glued = ["_", "x", "9"].map((p) => `${p}${ACTIVE_KEY}`);
  const lower = ACTIVE_KEY.toLowerCase();

  for (const reason of [...glued.map((g) => `ref${g}`), `key:${lower}.`]) {
    const { logs } = await callTool("add_credits", { ...grantArgs(30_001), reason });
    const all = logs.join("\n");

    assert.ok(all.includes("[license-key]"), all);
    assert.ok(!all.toUpperCase().includes(ACTIVE_KEY), all);
  }
});

test("spend counts an old paid key's Stripe customer past the 100-key cap, by email and by key", async () => {
  // The paid key is the OLDEST of 101 on the account; the 100 newer keys
  // have no Stripe customer, so only the uncapped id query finds cus_old.
  store.licenses[0].stripeCustomerId = "cus_old";
  for (let i = 0; i < 100; i++) {
    store.licenses.push({
      ...store.licenses[0],
      id: `lic-new-${i}`,
      key: `HW-TEST-NEW0-${String(i).padStart(4, "0")}`,
      stripeCustomerId: null,
      createdAt: new Date(Date.UTC(2026, 0, 1, 0, i)),
    });
  }
  stripeCharges.set("cus_old", [
    { id: "ch_old", status: "succeeded", disputed: false, amount: 2535, amount_refunded: 0 },
  ]);

  const byEmail = await callTool("find_customer", { email: "buyer@example.com" });
  const byKey = await callTool("find_customer", { licenseKey: ACTIVE_KEY });
  const emailCustomer = byEmail.result.structuredContent.customers[0];
  const keyCustomer = byKey.result.structuredContent.customers[0];

  // The email lookup's capped license list leaves the old key out...
  assert.equal(
    emailCustomer.licenses.some((l: { key: string }) => l.key === ACTIVE_KEY),
    false,
  );
  // ...but its purchase still counts, and both lookups agree.
  assert.equal(emailCustomer.totalSpentCents, 2535);
  assert.equal(emailCustomer.hasStripeCustomer, true);
  assert.equal(keyCustomer.totalSpentCents, 2535);
});
