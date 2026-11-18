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
  stripeCustomerId: null;
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
    getAccountKeysWithCreditsForUserIds: async (ids: string[]) =>
      store.licenses
        .filter((l) => ids.includes(l.userId))
        .map((l) => ({ ...l, credits: balanceOf(l.userId) })),
    getCreditBalance: async (userId: string) => balanceOf(userId),
    getCreditGrantsForUsers: async (ids: string[]) =>
      store.grants.filter((g) => ids.includes(g.userId)),
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
      stripeCustomerId: null,
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
      reason: "pre-open-source purchase",
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
    },
  ]);
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
