/**
 * Account Key rotation, end to end: `customer.rotateAccountKey` reached through
 * the real tRPC route (`app/api/trpc/[trpc]/route.ts`), authenticated by a real
 * Better Auth session that the real license-key sign-in endpoint minted, run
 * against a real Postgres (PGlite with every migration in `nextjs/drizzle`, see
 * `credit-ledger-harness.ts`). The licence verdicts afterwards come from the
 * real `/api/license/validate` and `/api/account/validate` routes.
 *
 * Mocked are only the edges a test cannot reach: the email provider (Resend),
 * Stripe's client, and the Upstash limiters (replaced by counting stubs, so the
 * rotate limit can be driven). The cloud purge is the real client; `fetch` is
 * stubbed for the cloud hosts only, so the request it sends is asserted.
 *
 * Every key string here is minted by the real generator or built from parts at
 * runtime; none is a real key.
 */
import assert from "node:assert/strict";
import { after, before, beforeEach, describe, test } from "node:test";

import { sql } from "drizzle-orm";
import superjson from "superjson";

import { db, loadDbLayer, resetDatabase, seedGrant, seedUser } from "./credit-ledger-harness";
import { consoleSilencer, moduleMock, moduleUrl } from "./module-mock";
import * as schema from "../src/db/schema";

const ORIGIN = "http://localhost:3000";
const CLOUD = "https://cloud.example.test";
const INTERNAL_SECRET = ["rotation", "test", "internal", "secret"].join("-");
const POOL_SECRET = "p".repeat(40);

// `src/env/server.mjs` snapshots process.env on first import (#682).
process.env.SKIP_ENV_VALIDATION = "1";
process.env.BETTER_AUTH_URL = ORIGIN;
process.env.HYPERWHISPER_INTERNAL_SECRET = INTERNAL_SECRET;
process.env.HYPERWHISPER_CLOUD_URLS = CLOUD;
process.env.CREDIT_POOL_ID_SECRET = POOL_SECRET;

// ---------------------------------------------------------------------------
// Edges
// ---------------------------------------------------------------------------

interface SentEmail {
  to: string;
  subject: string;
  html: string;
  text: string;
}
const sentEmails: SentEmail[] = [];

moduleMock.module(moduleUrl("../lib/clients/resend.ts"), {
  namedExports: {
    resend: {
      emails: {
        send: async (payload: SentEmail) => {
          sentEmails.push(payload);
          return { data: { id: "email_1" }, error: null };
        },
      },
    },
    DEFAULT_FROM_EMAIL: "HyperWhisper <support@hyperwhisper.com>",
  },
});

/**
 * Work the rotation queues to run after its response (the notice email).
 * Next's `after()` needs its request scope, which a route called directly
 * does not have; `flushAfterResponse()` runs what was queued.
 */
const afterResponseTasks: Array<() => Promise<void>> = [];

moduleMock.module(moduleUrl("../lib/shared/after-response.ts"), {
  namedExports: {
    afterResponse: (task: () => Promise<void>) => {
      afterResponseTasks.push(task);
    },
  },
});

async function flushAfterResponse(): Promise<void> {
  for (const task of afterResponseTasks.splice(0)) await task();
}

moduleMock.module(moduleUrl("../lib/clients/stripe.ts"), {
  namedExports: { stripe: {} },
});

/** The rotate limiter: a fixed window per identifier, `rotateMax` per run. */
const rotateCalls = new Map<string, number>();
let rotateMax = 5;
const allow = { limit: async () => ({ success: true }) };

moduleMock.module(moduleUrl("../lib/rate-limit.ts"), {
  namedExports: {
    downloadEmailRateLimiter: allow,
    licenseValidateRateLimiter: allow,
    licenseCreditsRateLimiter: allow,
    latencyIngestRateLimiter: allow,
    checkoutRateLimiter: allow,
    accountKeyRotateRateLimiter: {
      limit: async (identifier: string) => {
        const n = (rotateCalls.get(identifier) ?? 0) + 1;
        rotateCalls.set(identifier, n);
        return { success: n <= rotateMax, reset: Date.now() + 60_000 };
      },
    },
  },
});

interface PurgeCall {
  url: string;
  secret: string | null;
  body: unknown;
  hasSignal: boolean;
}
const purgeCalls: PurgeCall[] = [];
const realFetch = globalThis.fetch;

// Every console line, so no-key-in-logs can be asserted on all of them.
const logLines: string[] = [];
const logging = consoleSilencer(["log", "warn", "error"], logLines);

// ---------------------------------------------------------------------------
// Loading
// ---------------------------------------------------------------------------

type Handler = (request: Request) => Promise<Response>;
let authPOST: Handler;
let authGET: Handler;
let trpcPOST: Handler;
let validatePOST: Handler;
let accountValidatePOST: Handler;
let L: Awaited<ReturnType<typeof loadDbLayer>>;

// Variable specifiers, loaded after the mocks above (see auth-catch-all-route).
const AUTH_ROUTE = "../app/api/auth/[...all]/route.ts";
const TRPC_ROUTE = "../app/api/trpc/[trpc]/route.ts";
const VALIDATE_ROUTE = "../app/api/license/validate/route.ts";
const ACCOUNT_VALIDATE_ROUTE = "../app/api/account/validate/route.ts";

before(async () => {
  L = await loadDbLayer();
  authPOST = (await import(AUTH_ROUTE)).POST;
  authGET = (await import(AUTH_ROUTE)).GET;
  trpcPOST = (await import(TRPC_ROUTE)).POST;
  validatePOST = (await import(VALIDATE_ROUTE)).POST;
  accountValidatePOST = (await import(ACCOUNT_VALIDATE_ROUTE)).POST;

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input instanceof Request ? input.url : input);
    if (!url.startsWith(CLOUD)) return realFetch(input, init);
    const headers = new Headers(init?.headers);
    purgeCalls.push({
      url,
      secret: headers.get("x-internal-secret"),
      body: JSON.parse(String(init?.body)),
      hasSignal: init?.signal instanceof AbortSignal,
    });
    return new Response(JSON.stringify({ invalidated: true }), { status: 200 });
  }) as typeof fetch;
});

after(() => {
  globalThis.fetch = realFetch;
});

beforeEach(async () => {
  await resetDatabase();
  await db.execute(sql`TRUNCATE device_validations`);
  sentEmails.length = 0;
  purgeCalls.length = 0;
  afterResponseTasks.length = 0;
  logLines.length = 0;
  rotateCalls.clear();
  rotateMax = 5;
  logging.silence();
});

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

async function seedOwner(id: string): Promise<{ userId: string; keyId: string; key: string }> {
  const userId = await seedUser(id);
  const { generateLicenseKey } = await import("../lib/services/license-key");
  const key = generateLicenseKey();
  const [row] = await db
    .insert(schema.accountKeys)
    .values({
      key,
      userId,
      email: `${id}@example.test`,
      status: "granted",
      stripeCustomerId: `cus_${id}`,
      stripeSessionId: `cs_${id}`,
    })
    .returning({ id: schema.accountKeys.id });
  return { userId, keyId: row.id, key };
}

/**
 * Sign in through the real endpoint. Better Auth's router answers a refusal as
 * HTTP 200 with an `error` body, so a refusal is "no cookie, an error".
 */
async function signIn(
  licenseKey: string,
): Promise<{ cookie: string | null; error: unknown }> {
  const response = await authPOST(
    new Request(`${ORIGIN}/api/auth/sign-in/license-key`, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        origin: ORIGIN,
        "user-agent": "rotation-test",
      },
      body: JSON.stringify({ licenseKey }),
    }),
  );
  const setCookie = response.headers.getSetCookie?.() ?? [];
  const session = setCookie.find((c) => c.includes("session_token="));
  const body = (await response.json()) as { error?: unknown };
  return { cookie: session ? session.split(";")[0] : null, error: body.error };
}

/**
 * Sign in by magic link through the real endpoints: ask for the link, read it
 * out of the captured email, and follow it. The session this mints is the one
 * proven by the inbox, so it is the only kind that may rotate.
 */
async function signInByEmail(email: string): Promise<string> {
  const before = sentEmails.length;
  const response = await authPOST(
    new Request(`${ORIGIN}/api/auth/sign-in/magic-link`, {
      method: "POST",
      headers: { "content-type": "application/json", origin: ORIGIN },
      body: JSON.stringify({ email, callbackURL: "/en/user/dashboard" }),
    }),
  );
  assert.equal(response.status, 200, "magic link requested");
  const mail = sentEmails[before];
  assert.ok(mail, "a magic-link email was sent");
  sentEmails.splice(before, 1);
  const link = /https?:\/\/\S+\/api\/auth\/magic-link\/verify\?[^\s"<]+/.exec(mail.text)?.[0];
  assert.ok(link, "the email carries the verify link");
  const verified = await authGET(new Request(link.replace(/&amp;/g, "&")));
  const cookie = (verified.headers.getSetCookie?.() ?? []).find((c) =>
    c.includes("session_token="),
  );
  assert.ok(cookie, `verify set a session cookie (status ${verified.status})`);
  return cookie.split(";")[0];
}

async function signInMethods(userId: string): Promise<Array<string | null>> {
  const rows = await db
    .select({ m: schema.session.signInMethod })
    .from(schema.session)
    .where(sql`${schema.session.userId} = ${userId}`);
  return rows.map((r) => r.m).sort();
}

interface TrpcReply {
  status: number;
  data?: { id: string; key: string };
  code?: string;
}

async function rotate(
  cookie: string | null,
  input: { id: string; currentKey: string },
): Promise<TrpcReply> {
  const response = await trpcPOST(
    new Request(`${ORIGIN}/api/trpc/customer.rotateAccountKey`, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        ...(cookie ? { cookie } : {}),
      },
      body: JSON.stringify(superjson.serialize(input)),
    }),
  );
  const body = (await response.json()) as {
    result?: { data: { json: { id: string; key: string } } };
    error?: { json: { data: { code: string } } };
  };
  return {
    status: response.status,
    data: body.result?.data.json,
    code: body.error?.json.data.code,
  };
}

async function validate(
  licenseKey: string,
  extra: Record<string, unknown> = {},
  handler: Handler = validatePOST,
  path = "/api/license/validate",
): Promise<{ status: number; body: Record<string, unknown> }> {
  const response = await handler(
    new Request(`https://hyperwhisper.test${path}`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ license_key: licenseKey, ...extra }),
    }),
  );
  return { status: response.status, body: (await response.json()) as Record<string, unknown> };
}

async function storedKey(keyId: string): Promise<string> {
  const row = await db.query.accountKeys.findFirst({ where: (t, { eq }) => eq(t.id, keyId) });
  assert.ok(row);
  return row.key;
}

async function deviceCount(keyId: string): Promise<number> {
  const rows = await db
    .select()
    .from(schema.deviceValidations)
    .where(sql`${schema.deviceValidations.licenseKeyId} = ${keyId}`);
  return rows.length;
}

async function sessionCount(userId: string): Promise<number> {
  const rows = await db
    .select()
    .from(schema.session)
    .where(sql`${schema.session.userId} = ${userId}`);
  return rows.length;
}

const KEY_FORMAT = /^HW-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$/;

// ---------------------------------------------------------------------------
// Cases
// ---------------------------------------------------------------------------

describe("customer.rotateAccountKey, end to end", () => {
  test("the owner rotates: old key dead everywhere, new key works, credits and plan stay", async () => {
    const owner = await seedOwner("owner");
    await seedGrant({ userId: owner.userId, sourceId: "grant_1", amount: 1234 });

    // Two devices on the old key, through the real validate route.
    for (const device_id of ["mac-1", "win-1"]) {
      const res = await validate(owner.key, { device_id });
      assert.equal(res.status, 200);
    }
    assert.equal(await deviceCount(owner.keyId), 2);

    const before = await validate(owner.key, { include_credits: true });
    assert.equal(before.body.valid, true);
    const creditsBefore = before.body.credits;
    const poolBefore = before.body.credit_pool_id;
    assert.ok(poolBefore, "credit_pool_id is issued when the secret is set");

    // Two web sessions: the dashboard tab that rotates (proven by email) and
    // another browser that signed in with the key.
    const here = { cookie: await signInByEmail("owner@example.test") };
    const elsewhere = await signIn(owner.key);
    assert.ok(elsewhere.cookie);
    assert.equal(await sessionCount(owner.userId), 2);
    assert.deepEqual(await signInMethods(owner.userId), ["account_key", "email"]);

    const reply = await rotate(here.cookie, { id: owner.keyId, currentKey: owner.key });
    assert.equal(reply.status, 200, `rotate answered ${reply.code}`);
    assert.ok(reply.data);
    const newKey = reply.data.key;
    // The notice is queued for after the response; the reply did not wait on it.
    assert.equal(sentEmails.length, 0, "no email before the response");
    assert.equal(afterResponseTasks.length, 1);
    await flushAfterResponse();
    assert.equal(reply.data.id, owner.keyId, "same row: purchase history and Stripe link stay");
    assert.match(newKey, KEY_FORMAT);
    assert.notEqual(newKey, owner.key);
    assert.equal(await storedKey(owner.keyId), newKey);

    // Old key: refused by both licence APIs and by sign-in.
    const oldValidate = await validate(owner.key);
    assert.equal(oldValidate.status, 400);
    assert.equal(oldValidate.body.valid, false);
    const oldAccount = await validate(owner.key, {}, accountValidatePOST, "/api/account/validate");
    assert.equal(oldAccount.status, 400);
    assert.equal(oldAccount.body.valid, false);
    const oldSignIn = await signIn(owner.key);
    assert.equal(oldSignIn.cookie, null);
    assert.equal(oldSignIn.error, "Invalid or inactive license key.");

    // New key: accepted, same credits, same pool (cloud holds follow the user).
    const after = await validate(newKey, { include_credits: true, device_id: "mac-1" });
    assert.equal(after.status, 200);
    assert.equal(after.body.valid, true);
    assert.equal(after.body.credits, creditsBefore);
    assert.equal(after.body.credit_pool_id, poolBefore);
    assert.equal(await L.getCreditBalance(owner.userId), 1234);
    const row = await db.query.accountKeys.findFirst({
      where: (t, { eq }) => eq(t.id, owner.keyId),
    });
    assert.equal(row?.status, "granted");
    assert.equal(row?.stripeCustomerId, "cus_owner");
    assert.equal(row?.stripeSessionId, "cs_owner");
    const newSignIn = await signIn(newKey);
    assert.ok(newSignIn.cookie);

    // Devices: the old rows were cleared; only the re-validated one is back.
    assert.equal(await deviceCount(owner.keyId), 1);

    // Sessions: the session minted from the OLD key is gone; the rotating tab
    // is kept (the new-key sign-in above is the only other one).
    const stillHere = await rotate(here.cookie, { id: owner.keyId, currentKey: "x" });
    assert.equal(stillHere.code, "CONFLICT", "the rotating session still authenticates");
    const gone = await rotate(elsewhere.cookie, { id: owner.keyId, currentKey: newKey });
    assert.equal(gone.code, "UNAUTHORIZED", "the old key's session was swept");

    // Cloud purge: the old string, to the configured cloud, with a timeout.
    assert.equal(purgeCalls.length, 1);
    assert.equal(purgeCalls[0].url, `${CLOUD}/internal/license-cache/invalidate`);
    assert.equal(purgeCalls[0].secret, INTERNAL_SECRET);
    assert.deepEqual(purgeCalls[0].body, { account_key: owner.key });
    assert.equal(purgeCalls[0].hasSignal, true);

    // Notice email: sent to the owner, never the full new or old key.
    assert.equal(sentEmails.length, 1);
    assert.equal(sentEmails[0].to, "owner@example.test");
    for (const part of [sentEmails[0].html, sentEmails[0].text, sentEmails[0].subject]) {
      assert.ok(!part.includes(newKey), "email carries no full new key");
      assert.ok(!part.includes(owner.key), "email carries no old key");
    }
    assert.ok(sentEmails[0].text.includes(newKey.substring(0, 7)));

    // No log line carries either key.
    assert.ok(logLines.some((l) => l.includes("Account Key rotated")));
    for (const line of logLines) {
      assert.ok(!line.includes(owner.key), `old key in log: ${line}`);
      assert.ok(!line.includes(newKey), `new key in log: ${line}`);
    }
  });

  test("a session minted with the key itself cannot rotate it (the leaked-key takeover)", async () => {
    const owner = await seedOwner("leaked");
    const ownerSession = await signInByEmail("leaked@example.test");
    // Someone who holds only the key string signs in with it.
    const leaker = await signIn(owner.key);
    assert.ok(leaker.cookie);

    const reply = await rotate(leaker.cookie, { id: owner.keyId, currentKey: owner.key });
    assert.equal(reply.status, 403);
    assert.equal(reply.code, "FORBIDDEN");
    assert.equal(await storedKey(owner.keyId), owner.key, "nothing rotated");
    assert.equal(await sessionCount(owner.userId), 2, "nobody was signed out");
    assert.equal(rotateCalls.size, 0, "the owner's rate-limit budget is untouched");
    assert.equal(purgeCalls.length, 0);
    assert.equal(sentEmails.length, 0);

    // The owner, proven by email, rotates; the leaker's session dies with the key.
    const rotated = await rotate(ownerSession, { id: owner.keyId, currentKey: owner.key });
    assert.equal(rotated.status, 200);
    const after = await rotate(leaker.cookie, { id: owner.keyId, currentKey: rotated.data!.key });
    assert.equal(after.code, "UNAUTHORIZED", "the leaker's session was swept");
    assert.equal(await sessionCount(owner.userId), 1);
    assert.deepEqual(await signInMethods(owner.userId), ["email"]);
  });

  test("a non-owner cannot rotate someone else's key", async () => {
    const owner = await seedOwner("victim");
    const other = await seedOwner("other");
    const session = { cookie: await signInByEmail("other@example.test") };

    const reply = await rotate(session.cookie, { id: owner.keyId, currentKey: owner.key });
    assert.equal(reply.status, 404);
    assert.equal(reply.code, "NOT_FOUND");
    assert.equal(await storedKey(owner.keyId), owner.key);
    assert.equal((await validate(owner.key)).status, 200);
    assert.equal(purgeCalls.length, 0);
    assert.equal(sentEmails.length, 0);
  });

  test("unauthenticated is refused before any work", async () => {
    const owner = await seedOwner("anon");
    const reply = await rotate(null, { id: owner.keyId, currentKey: owner.key });
    assert.equal(reply.status, 401);
    assert.equal(reply.code, "UNAUTHORIZED");
    assert.equal(await storedKey(owner.keyId), owner.key);
    assert.equal(rotateCalls.size, 0, "the limiter is not even counted");
  });

  test("the per-user rate limit applies", async () => {
    rotateMax = 2;
    const owner = await seedOwner("busy");
    const session = { cookie: await signInByEmail("busy@example.test") };

    let current = owner.key;
    for (let i = 0; i < 2; i++) {
      const reply = await rotate(session.cookie, { id: owner.keyId, currentKey: current });
      assert.equal(reply.status, 200);
      current = reply.data!.key;
    }
    const limited = await rotate(session.cookie, { id: owner.keyId, currentKey: current });
    assert.equal(limited.status, 429);
    assert.equal(limited.code, "TOO_MANY_REQUESTS");
    assert.equal(await storedKey(owner.keyId), current, "a limited call changes nothing");
    assert.deepEqual(Array.from(rotateCalls.keys()), [owner.userId], "keyed by user id");
  });

  test("a revoked key is refused", async () => {
    const owner = await seedOwner("revoked");
    const session = { cookie: await signInByEmail("revoked@example.test") };
    await db.execute(sql`UPDATE account_keys SET status = 'revoked' WHERE id = ${owner.keyId}`);
    // Revoking through SQL leaves the session; the procedure must still refuse.
    const reply = await rotate(session.cookie, { id: owner.keyId, currentKey: owner.key });
    assert.equal(reply.status, 412);
    assert.equal(reply.code, "PRECONDITION_FAILED");
    assert.equal(await storedKey(owner.keyId), owner.key);
  });

  test("two rotations at once: exactly one wins, the other is CONFLICT, no stale key", async () => {
    const owner = await seedOwner("racer");
    const session = { cookie: await signInByEmail("racer@example.test") };

    const [a, b] = await Promise.all([
      rotate(session.cookie, { id: owner.keyId, currentKey: owner.key }),
      rotate(session.cookie, { id: owner.keyId, currentKey: owner.key }),
    ]);
    const statuses = [a.status, b.status].sort();
    assert.deepEqual(statuses, [200, 409]);
    const winner = a.status === 200 ? a : b;
    assert.equal(await storedKey(owner.keyId), winner.data!.key);
    assert.equal((await validate(winner.data!.key)).status, 200);
    assert.equal((await validate(owner.key)).status, 400);
    assert.equal(purgeCalls.length, 1);
  });

  test("the rotation still lands when the cloud purge fails", async () => {
    const owner = await seedOwner("offline");
    const session = { cookie: await signInByEmail("offline@example.test") };
    const prev = globalThis.fetch;
    globalThis.fetch = (async () => {
      throw new TypeError("fetch failed");
    }) as typeof fetch;
    try {
      const reply = await rotate(session.cookie, { id: owner.keyId, currentKey: owner.key });
      assert.equal(reply.status, 200);
      assert.equal((await validate(owner.key)).status, 400);
    } finally {
      globalThis.fetch = prev;
    }
    assert.ok(logLines.some((l) => l.includes("Cloud licence cache purge failed")));
    for (const line of logLines) assert.ok(!line.includes(owner.key));
  });
});
