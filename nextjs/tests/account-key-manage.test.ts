/**
 * Issuing and deleting Account Keys from the dashboard, end to end:
 * `customer.issueAccountKey` and `customer.deleteAccountKey` reached through
 * the real tRPC route, authenticated by real Better Auth sessions (magic link
 * or license-key sign-in), against a real Postgres (PGlite with every
 * migration, see `credit-ledger-harness.ts`). Licence verdicts come from the
 * real `/api/license/validate` and `/api/account/validate` routes, and the
 * dashboard list from the real `customer.licensesWithCredits` query.
 *
 * Mocked are only the edges, as in `account-key-rotation.test.ts`: the email
 * provider, Stripe's client, the rate limiters (counting stubs), and `fetch`
 * for the cloud hosts only, so the cache purge a delete sends is asserted.
 *
 * Every key string here is minted by the real generator; none is a real key.
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
const INTERNAL_SECRET = ["manage", "test", "internal", "secret"].join("-");
const POOL_SECRET = "q".repeat(40);

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

moduleMock.module(moduleUrl("../lib/clients/email.ts"), {
  namedExports: {
    sendEmail: async (payload: SentEmail) => {
      sentEmails.push(payload);
      return { data: { id: "email_1" }, error: null };
    },
    DEFAULT_FROM_EMAIL: "HyperWhisper <support@hyperwhisper.com>",
  },
});

/** Work queued to run after the response (the notice emails). */
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

/** The issue/delete limiter: a fixed window per identifier, `manageMax` per run. */
const manageCalls = new Map<string, number>();
let manageMax = 20;
const allow = { limit: async () => ({ success: true, reset: Date.now() + 60_000 }) };

moduleMock.module(moduleUrl("../lib/rate-limit.ts"), {
  namedExports: {
    downloadEmailRateLimiter: allow,
    licenseValidateRateLimiter: allow,
    licenseCreditsRateLimiter: allow,
    latencyIngestRateLimiter: allow,
    checkoutRateLimiter: allow,
    accountKeyRotateRateLimiter: allow,
    accountKeyManageRateLimiter: {
      limit: async (identifier: string) => {
        const n = (manageCalls.get(identifier) ?? 0) + 1;
        manageCalls.set(identifier, n);
        return { success: n <= manageMax, reset: Date.now() + 60_000 };
      },
    },
  },
});

interface PurgeCall {
  url: string;
  secret: string | null;
  body: unknown;
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
let trpcGET: Handler;
let validatePOST: Handler;
let accountValidatePOST: Handler;
let L: Awaited<ReturnType<typeof loadDbLayer>>;

const AUTH_ROUTE = "../app/api/auth/[...all]/route.ts";
const TRPC_ROUTE = "../app/api/trpc/[trpc]/route.ts";
const VALIDATE_ROUTE = "../app/api/license/validate/route.ts";
const ACCOUNT_VALIDATE_ROUTE = "../app/api/account/validate/route.ts";

before(async () => {
  L = await loadDbLayer();
  authPOST = (await import(AUTH_ROUTE)).POST;
  authGET = (await import(AUTH_ROUTE)).GET;
  trpcPOST = (await import(TRPC_ROUTE)).POST;
  trpcGET = (await import(TRPC_ROUTE)).GET;
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
  manageCalls.clear();
  manageMax = 20;
  logging.silence();
});

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

interface Owner {
  userId: string;
  email: string;
  keyId: string;
  key: string;
}

/** A user with one granted key, billed to a Stripe customer and session. */
async function seedOwner(id: string, status = "granted"): Promise<Owner> {
  const userId = await seedUser(id);
  const { generateLicenseKey } = await import("../lib/services/license-key");
  const key = generateLicenseKey();
  const email = `${id}@example.test`;
  const [row] = await db
    .insert(schema.accountKeys)
    .values({
      key,
      userId,
      email,
      status,
      stripeCustomerId: `cus_${id}`,
      stripeSessionId: `cs_${id}`,
    })
    .returning({ id: schema.accountKeys.id });
  return { userId, email, keyId: row.id, key };
}

async function signIn(licenseKey: string): Promise<{ cookie: string | null; error: unknown }> {
  const response = await authPOST(
    new Request(`${ORIGIN}/api/auth/sign-in/license-key`, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        origin: ORIGIN,
        "user-agent": "manage-test",
      },
      body: JSON.stringify({ licenseKey }),
    }),
  );
  const setCookie = response.headers.getSetCookie?.() ?? [];
  const session = setCookie.find((c) => c.includes("session_token="));
  const body = (await response.json()) as { error?: unknown };
  return { cookie: session ? session.split(";")[0] : null, error: body.error };
}

/** Sign in by magic link through the real endpoints (an email-proven session). */
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

interface TrpcReply<T> {
  status: number;
  data?: T;
  code?: string;
}

async function mutate<T>(
  path: string,
  cookie: string | null,
  input?: unknown,
): Promise<TrpcReply<T>> {
  const response = await trpcPOST(
    new Request(`${ORIGIN}/api/trpc/${path}`, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        ...(cookie ? { cookie } : {}),
      },
      body: JSON.stringify(superjson.serialize(input)),
    }),
  );
  const body = (await response.json()) as {
    result?: { data: { json: T } };
    error?: { json: { data: { code: string } } };
  };
  return {
    status: response.status,
    data: body.result?.data.json,
    code: body.error?.json.data.code,
  };
}

const issue = (cookie: string | null) =>
  mutate<{ id: string; key: string; status: string; createdAt: string }>(
    "customer.issueAccountKey",
    cookie,
  );

const remove = (cookie: string | null, input: { id: string; currentKey: string }) =>
  mutate<{ id: string }>("customer.deleteAccountKey", cookie, input);

/** The dashboard's list, through the real query. */
async function dashboardKeys(cookie: string): Promise<Array<{ id: string; status: string }>> {
  const response = await trpcGET(
    new Request(`${ORIGIN}/api/trpc/customer.licensesWithCredits`, {
      headers: { cookie },
    }),
  );
  const body = (await response.json()) as {
    result: { data: { json: { licenses: Array<{ id: string; status: string }> } } };
  };
  assert.equal(response.status, 200);
  return body.result.data.json.licenses;
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

async function keyRow(keyId: string) {
  const row = await db.query.accountKeys.findFirst({ where: (t, { eq }) => eq(t.id, keyId) });
  assert.ok(row);
  return row;
}

async function liveKeyCount(userId: string): Promise<number> {
  const rows = await db
    .select()
    .from(schema.accountKeys)
    .where(sql`${schema.accountKeys.userId} = ${userId} AND ${schema.accountKeys.status} = 'granted'`);
  return rows.length;
}

async function deviceCount(keyId: string): Promise<number> {
  const rows = await db
    .select()
    .from(schema.deviceValidations)
    .where(sql`${schema.deviceValidations.licenseKeyId} = ${keyId}`);
  return rows.length;
}

async function grantCount(userId: string): Promise<number> {
  const rows = await db
    .select()
    .from(schema.creditGrants)
    .where(sql`${schema.creditGrants.userId} = ${userId}`);
  return rows.length;
}

/** The `account_key.<event>` lines captured from index `from` on, parsed, with their level. */
function manageLogs(event: "issue" | "delete", from = 0): Array<Record<string, unknown>> {
  return logLines
    .slice(from)
    .filter((l) => l.includes(`"event":"account_key.${event}"`))
    .map((l) => {
      const space = l.indexOf(" ");
      return { level: l.slice(0, space), ...JSON.parse(l.slice(space + 1)) };
    });
}

function assertNoKeyInLogs(keys: string[]): void {
  for (const line of logLines) {
    for (const key of keys) assert.ok(!line.includes(key), `key in log: ${line}`);
  }
}

const KEY_FORMAT = /^HW-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$/;

// ---------------------------------------------------------------------------
// Cases
// ---------------------------------------------------------------------------

describe("customer.issueAccountKey and customer.deleteAccountKey, end to end", () => {
  test("issue, then delete the old key: it dies everywhere, the new key keeps the same credits", async () => {
    const owner = await seedOwner("owner");
    await seedGrant({ userId: owner.userId, sourceId: "grant_1", amount: 1234 });
    const grantsBefore = await grantCount(owner.userId);

    const before = await validate(owner.key, { include_credits: true });
    assert.equal(before.body.valid, true);
    const poolBefore = before.body.credit_pool_id;
    assert.ok(poolBefore, "credit_pool_id is issued when the secret is set");

    const here = await signInByEmail(owner.email);

    // ISSUE ------------------------------------------------------------------
    const issued = await issue(here);
    assert.equal(issued.status, 200, `issue answered ${issued.code}`);
    assert.ok(issued.data);
    const newKey = issued.data.key;
    assert.match(newKey, KEY_FORMAT);
    assert.notEqual(newKey, owner.key);
    assert.equal(issued.data.status, "granted");
    assert.equal(sentEmails.length, 0, "no email before the response");
    assert.equal(afterResponseTasks.length, 1);
    await flushAfterResponse();

    // Same user, so the same pool and balance; the Stripe customer is copied,
    // the purchase's session id is not; nothing was granted with it.
    const newRow = await keyRow(issued.data.id);
    assert.equal(newRow.userId, owner.userId);
    assert.equal(newRow.email, owner.email);
    assert.equal(newRow.stripeCustomerId, `cus_owner`);
    assert.equal(newRow.stripeSessionId, null);
    assert.equal(newRow.polarLicenseKeyId, null);
    assert.equal(await grantCount(owner.userId), grantsBefore, "no credits granted with a key");
    assert.equal(await L.getCreditBalance(owner.userId), 1234);

    const viaNew = await validate(newKey, { include_credits: true });
    assert.equal(viaNew.status, 200);
    assert.equal(viaNew.body.credits, before.body.credits);
    assert.equal(viaNew.body.credit_pool_id, poolBefore);
    assert.equal(viaNew.body.stripe_customer_id, "cus_owner");
    assert.equal((await validate(owner.key)).status, 200, "the old key still works");

    assert.equal(sentEmails.length, 1);
    assert.equal(sentEmails[0].to, owner.email);
    for (const part of [sentEmails[0].html, sentEmails[0].text, sentEmails[0].subject]) {
      assert.ok(!part.includes(newKey), "issue email carries no full key");
    }
    assert.ok(sentEmails[0].text.includes(newKey.substring(0, 7)));
    sentEmails.length = 0;

    assert.deepEqual(
      (await dashboardKeys(here)).map((l) => l.id).sort(),
      [owner.keyId, issued.data.id].sort(),
    );

    // DELETE the original ---------------------------------------------------
    for (const device_id of ["mac-1", "win-1"]) {
      assert.equal((await validate(owner.key, { device_id })).status, 200);
    }
    assert.equal(await deviceCount(owner.keyId), 2);
    const elsewhere = await signIn(owner.key);
    assert.ok(elsewhere.cookie);

    const deleted = await remove(here, { id: owner.keyId, currentKey: owner.key });
    assert.equal(deleted.status, 200, `delete answered ${deleted.code}`);
    assert.equal(sentEmails.length, 0, "no email before the response");
    await flushAfterResponse();

    // The key is refused by both licence APIs and by sign-in.
    const dead = await validate(owner.key);
    assert.equal(dead.status, 400);
    assert.equal(dead.body.valid, false);
    assert.equal(dead.body.reason, "not_entitled");
    const deadAccount = await validate(owner.key, {}, accountValidatePOST, "/api/account/validate");
    assert.equal(deadAccount.status, 400);
    assert.equal(deadAccount.body.valid, false);
    const deadSignIn = await signIn(owner.key);
    assert.equal(deadSignIn.cookie, null);

    // Devices cleared; the key's web session swept; this tab kept.
    assert.equal(await deviceCount(owner.keyId), 0);
    assert.equal(
      (await remove(elsewhere.cookie, { id: issued.data.id, currentKey: newKey })).code,
      "UNAUTHORIZED",
      "the deleted key's session was swept",
    );

    // Soft delete: the row keeps its purchase and Stripe link.
    const deadRow = await keyRow(owner.keyId);
    assert.equal(deadRow.status, "deleted");
    assert.equal(deadRow.key, owner.key);
    assert.equal(deadRow.stripeCustomerId, "cus_owner");
    assert.equal(deadRow.stripeSessionId, "cs_owner");

    // Gone from the dashboard; the new key and the credits stay.
    assert.deepEqual(
      (await dashboardKeys(here)).map((l) => l.id),
      [issued.data.id],
    );
    const stillThere = await validate(newKey, { include_credits: true });
    assert.equal(stillThere.status, 200);
    assert.equal(stillThere.body.credits, before.body.credits);
    assert.equal(stillThere.body.credit_pool_id, poolBefore);
    assert.equal(await L.getCreditBalance(owner.userId), 1234);

    // Cloud purge of the deleted key.
    assert.equal(purgeCalls.length, 1);
    assert.equal(purgeCalls[0].url, `${CLOUD}/internal/license-cache/invalidate`);
    assert.equal(purgeCalls[0].secret, INTERNAL_SECRET);
    assert.deepEqual(purgeCalls[0].body, { account_key: owner.key });

    // Delete notice: a prefix only.
    assert.equal(sentEmails.length, 1);
    assert.equal(sentEmails[0].to, owner.email);
    for (const part of [sentEmails[0].html, sentEmails[0].text, sentEmails[0].subject]) {
      assert.ok(!part.includes(owner.key), "delete email carries no full key");
      assert.ok(!part.includes(newKey));
    }
    assert.ok(sentEmails[0].text.includes(owner.key.substring(0, 7)));

    assert.deepEqual(manageLogs("issue"), [
      {
        level: "log",
        event: "account_key.issue",
        outcome: "ok",
        userId: owner.userId,
        licenseId: issued.data.id,
      },
    ]);
    assert.deepEqual(manageLogs("delete"), [
      {
        level: "log",
        event: "account_key.delete",
        outcome: "ok",
        userId: owner.userId,
        licenseId: owner.keyId,
        devicesCleared: 2,
      },
    ]);
    assertNoKeyInLogs([owner.key, newKey]);
  });

  test("the last live key cannot be deleted", async () => {
    const owner = await seedOwner("single");
    const here = await signInByEmail(owner.email);

    const reply = await remove(here, { id: owner.keyId, currentKey: owner.key });
    assert.equal(reply.status, 412);
    assert.equal(reply.code, "PRECONDITION_FAILED");
    assert.equal((await keyRow(owner.keyId)).status, "granted");
    assert.equal((await validate(owner.key)).status, 200);
    assert.equal(purgeCalls.length, 0);
    await flushAfterResponse();
    assert.equal(sentEmails.length, 0);
    assert.deepEqual(manageLogs("delete"), [
      {
        level: "warn",
        event: "account_key.delete",
        outcome: "rejected",
        reason: "last_key",
        userId: owner.userId,
        licenseId: owner.keyId,
      },
    ]);
  });

  test("a revoked key does not count as live: the one granted key is still the last", async () => {
    const owner = await seedOwner("halfdead");
    const here = await signInByEmail(owner.email);
    const issued = await issue(here);
    assert.equal(issued.status, 200);
    await db.execute(sql`UPDATE account_keys SET status = 'revoked' WHERE id = ${owner.keyId}`);

    const reply = await remove(here, { id: issued.data!.id, currentKey: issued.data!.key });
    assert.equal(reply.code, "PRECONDITION_FAILED");
    // And the revoked one is not deletable either.
    const revoked = await remove(here, { id: owner.keyId, currentKey: owner.key });
    assert.equal(revoked.code, "NOT_FOUND");
    assert.equal((await keyRow(owner.keyId)).status, "revoked", "a revoked key stays revoked");
  });

  test("two tabs deleting the last two keys at once: exactly one wins, one key is left", async () => {
    const owner = await seedOwner("racer");
    const here = await signInByEmail(owner.email);
    const issued = await issue(here);
    assert.equal(issued.status, 200);

    const [a, b] = await Promise.all([
      remove(here, { id: owner.keyId, currentKey: owner.key }),
      remove(here, { id: issued.data!.id, currentKey: issued.data!.key }),
    ]);
    assert.deepEqual([a.status, b.status].sort(), [200, 412]);
    assert.equal(await liveKeyCount(owner.userId), 1);
    const survivor = a.status === 200 ? issued.data!.key : owner.key;
    assert.equal((await validate(survivor)).status, 200);
  });

  test("a user with no granted key cannot issue one (no free key)", async () => {
    // Signed in by email, but owns no key at all.
    const userId = await seedUser("nokey");
    const noKey = await signInByEmail("nokey@example.test");
    const reply = await issue(noKey);
    assert.equal(reply.status, 412);
    assert.equal(reply.code, "PRECONDITION_FAILED");
    assert.equal(await liveKeyCount(userId), 0);

    // Only a revoked key: still nothing.
    const revoked = await seedOwner("onlyrevoked", "revoked");
    const revokedSession = await signInByEmail(revoked.email);
    const second = await issue(revokedSession);
    assert.equal(second.code, "PRECONDITION_FAILED");
    assert.equal(await liveKeyCount(revoked.userId), 0);
    await flushAfterResponse();
    assert.equal(sentEmails.length, 0);
    assert.deepEqual(
      manageLogs("issue").map((l) => [l.level, l.outcome, l.reason]),
      [
        ["warn", "rejected", "no_live_key"],
        ["warn", "rejected", "no_live_key"],
      ],
    );
  });

  test("issuing stops at the cap of live keys", async () => {
    const { MAX_LIVE_ACCOUNT_KEYS } = await import("../lib/account-key-limits");
    const owner = await seedOwner("collector");
    const here = await signInByEmail(owner.email);

    for (let n = 1; n < MAX_LIVE_ACCOUNT_KEYS; n++) {
      assert.equal((await issue(here)).status, 200);
    }
    assert.equal(await liveKeyCount(owner.userId), MAX_LIVE_ACCOUNT_KEYS);

    const capped = await issue(here);
    assert.equal(capped.status, 409);
    assert.equal(capped.code, "CONFLICT");
    assert.equal(await liveKeyCount(owner.userId), MAX_LIVE_ACCOUNT_KEYS);

    // Deleting one frees a slot.
    const keys = await dashboardKeys(here);
    const victim = await keyRow(keys[0].id);
    assert.equal((await remove(here, { id: victim.id, currentKey: victim.key })).status, 200);
    assert.equal((await issue(here)).status, 200);
  });

  test("two issues at once never pass the cap", async () => {
    const { MAX_LIVE_ACCOUNT_KEYS } = await import("../lib/account-key-limits");
    const owner = await seedOwner("burst");
    const here = await signInByEmail(owner.email);
    for (let n = 2; n < MAX_LIVE_ACCOUNT_KEYS; n++) {
      assert.equal((await issue(here)).status, 200);
    }
    const replies = await Promise.all([issue(here), issue(here)]);
    assert.deepEqual(replies.map((r) => r.status).sort(), [200, 409]);
    assert.equal(await liveKeyCount(owner.userId), MAX_LIVE_ACCOUNT_KEYS);
  });

  test("a non-owner cannot delete someone else's key, and issuing only ever adds to the caller", async () => {
    const victim = await seedOwner("victim");
    const victimSpare = await signInByEmail(victim.email);
    const spare = await issue(victimSpare);
    assert.equal(spare.status, 200);

    const other = await seedOwner("other");
    const otherSession = await signInByEmail(other.email);

    const reply = await remove(otherSession, { id: victim.keyId, currentKey: victim.key });
    assert.equal(reply.status, 404);
    assert.equal(reply.code, "NOT_FOUND");
    assert.equal((await keyRow(victim.keyId)).status, "granted");
    assert.equal((await validate(victim.key)).status, 200);

    const issued = await issue(otherSession);
    assert.equal(issued.status, 200);
    assert.equal((await keyRow(issued.data!.id)).userId, other.userId);
    assert.equal(await liveKeyCount(victim.userId), 2);
    assert.equal(purgeCalls.length, 0);
  });

  test("a session opened with an Account Key can neither issue nor delete", async () => {
    const owner = await seedOwner("leaked");
    const ownerSession = await signInByEmail(owner.email);
    const spare = await issue(ownerSession);
    assert.equal(spare.status, 200);
    await flushAfterResponse();
    sentEmails.length = 0;
    manageCalls.clear();
    const from = logLines.length;

    const leaker = await signIn(owner.key);
    assert.ok(leaker.cookie);

    const issued = await issue(leaker.cookie);
    assert.equal(issued.status, 403);
    assert.equal(issued.code, "FORBIDDEN");
    const deleted = await remove(leaker.cookie, { id: spare.data!.id, currentKey: spare.data!.key });
    assert.equal(deleted.status, 403);
    assert.equal(deleted.code, "FORBIDDEN");

    assert.equal(await liveKeyCount(owner.userId), 2, "nothing issued or deleted");
    assert.equal(manageCalls.size, 0, "the owner's rate-limit budget is untouched");
    assert.equal(purgeCalls.length, 0);
    await flushAfterResponse();
    assert.equal(sentEmails.length, 0);
    assert.deepEqual(
      [...manageLogs("issue", from), ...manageLogs("delete", from)].map((l) => [
        l.event,
        l.reason,
      ]),
      [
        ["account_key.issue", "needs_email_sign_in"],
        ["account_key.delete", "needs_email_sign_in"],
      ],
    );
  });

  test("unauthenticated is refused before any work", async () => {
    const owner = await seedOwner("anon");
    assert.equal((await issue(null)).code, "UNAUTHORIZED");
    assert.equal(
      (await remove(null, { id: owner.keyId, currentKey: owner.key })).code,
      "UNAUTHORIZED",
    );
    assert.equal(manageCalls.size, 0);
    assert.equal(await liveKeyCount(owner.userId), 1);
  });

  test("a key rotated since the page loaded is not deleted unseen", async () => {
    const owner = await seedOwner("stale");
    const here = await signInByEmail(owner.email);
    assert.equal((await issue(here)).status, 200);

    const reply = await remove(here, { id: owner.keyId, currentKey: `${owner.key}-old` });
    assert.equal(reply.status, 409);
    assert.equal(reply.code, "CONFLICT");
    assert.equal((await keyRow(owner.keyId)).status, "granted");
  });

  test("a deleted key cannot be deleted again", async () => {
    const owner = await seedOwner("twice");
    const here = await signInByEmail(owner.email);
    assert.equal((await issue(here)).status, 200);
    assert.equal((await remove(here, { id: owner.keyId, currentKey: owner.key })).status, 200);

    const again = await remove(here, { id: owner.keyId, currentKey: owner.key });
    assert.equal(again.code, "NOT_FOUND");
    assert.equal(purgeCalls.length, 1, "purged once");
  });

  test("issue and delete share one per-user rate limit", async () => {
    manageMax = 2;
    const owner = await seedOwner("busy");
    const here = await signInByEmail(owner.email);

    const first = await issue(here);
    assert.equal(first.status, 200);
    assert.equal(
      (await remove(here, { id: first.data!.id, currentKey: first.data!.key })).status,
      200,
    );
    const limited = await issue(here);
    assert.equal(limited.status, 429);
    assert.equal(limited.code, "TOO_MANY_REQUESTS");
    assert.equal(await liveKeyCount(owner.userId), 1, "a limited call changes nothing");
    assert.deepEqual(Array.from(manageCalls.keys()), [owner.userId], "keyed by user id");
  });

  test("the delete still lands when the cloud purge fails", async () => {
    const owner = await seedOwner("offline");
    const here = await signInByEmail(owner.email);
    const spare = await issue(here);
    assert.equal(spare.status, 200);
    const prev = globalThis.fetch;
    globalThis.fetch = (async () => {
      throw new TypeError("fetch failed");
    }) as typeof fetch;
    try {
      const reply = await remove(here, { id: owner.keyId, currentKey: owner.key });
      assert.equal(reply.status, 200);
      assert.equal((await validate(owner.key)).status, 400);
    } finally {
      globalThis.fetch = prev;
    }
    assert.ok(logLines.some((l) => l.includes("Cloud licence cache purge failed")));
    assertNoKeyInLogs([owner.key, spare.data!.key]);
  });
});
