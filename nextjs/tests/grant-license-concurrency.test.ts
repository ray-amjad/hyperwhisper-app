/**
 * Concurrent `POST /api/internal/grant-license` calls for one email (#2481).
 *
 * The route reads "no granted key yet" outside any lock, so a double-clicked
 * ACS claim, a retry or an overlapping backfill can send several calls past
 * that read at once. Each used to mint its own key and its own 5,000-credit
 * internal bundle. These tests drive the real route against the real
 * migrations (PGlite, see `credit-ledger-harness.ts`) and count what landed.
 */
import assert from "node:assert/strict";
import { beforeEach, test } from "node:test";

import { sql } from "drizzle-orm";
import { NextRequest } from "next/server";

import { db, migrateOnce, resetDatabase, seedUser } from "./credit-ledger-harness";
import { moduleMock, moduleUrl } from "./module-mock";

const SECRET = "grant-license-concurrency-secret";
moduleMock.module(moduleUrl("../src/env/server.mjs"), {
  namedExports: { env: { HYPERWHISPER_INTERNAL_SECRET: SECRET } },
});

const CALLS = 8;

beforeEach(async () => {
  await migrateOnce();
  await resetDatabase();
});

async function grantConcurrently(email: string) {
  const { POST } = await import("@/app/api/internal/grant-license/route");
  const post = () =>
    POST(
      new NextRequest("https://hw.test/api/internal/grant-license", {
        method: "POST",
        headers: { "content-type": "application/json", "x-internal-secret": SECRET },
        body: JSON.stringify({ email }),
      })
    );
  const responses = await Promise.all(Array.from({ length: CALLS }, post));
  const statuses = responses.map((r) => r.status);
  const keys = new Set<string>();
  for (const r of responses) {
    const body = (await r.json()) as { licenseKey?: string };
    if (body.licenseKey) keys.add(body.licenseKey);
  }
  return { statuses, keys };
}

async function grantedKeyCount(email: string): Promise<number> {
  const rows = await db.execute<{ n: number }>(
    sql`SELECT count(*)::int AS n FROM account_keys WHERE email = ${email} AND status = 'granted'`
  );
  return rows.rows[0].n;
}

async function internalBundleCredits(): Promise<number> {
  const rows = await db.execute<{ c: number }>(
    sql`SELECT coalesce(sum(original_amount), 0)::int AS c FROM credit_grants WHERE source_type = 'internal_bundle'`
  );
  return rows.rows[0].c;
}

test("concurrent grants for an email that has a user but no granted key mint one key", async () => {
  // Signed in once, or their old key was revoked: the user row exists.
  await seedUser("existing.buyer");
  const email = "existing.buyer@example.test";

  const { statuses, keys } = await grantConcurrently(email);

  assert.deepEqual(statuses, Array(CALLS).fill(200));
  assert.equal(await grantedKeyCount(email), 1);
  assert.equal(keys.size, 1, `callers were handed ${keys.size} different keys`);
  assert.equal(await internalBundleCredits(), 5000);
});

test("concurrent grants for a brand-new email create one user and every caller gets 200", async () => {
  const email = "brand.new@example.test";

  const { statuses, keys } = await grantConcurrently(email);

  assert.deepEqual(statuses, Array(CALLS).fill(200));
  assert.equal(await grantedKeyCount(email), 1);
  assert.equal(keys.size, 1);
  assert.equal(await internalBundleCredits(), 5000);
  const users = await db.execute<{ n: number }>(
    sql`SELECT count(*)::int AS n FROM "user" WHERE email = ${email}`
  );
  assert.equal(users.rows[0].n, 1);
});

test("an email whose only key is revoked still gets a fresh key and one bundle", async () => {
  const userId = await seedUser("revoked.buyer");
  const email = "revoked.buyer@example.test";
  await db.execute(sql`
    INSERT INTO account_keys (key, email, user_id, status)
    VALUES ('HW-REVOKED-OLD', ${email}, ${userId}, 'revoked')
  `);

  const { statuses, keys } = await grantConcurrently(email);

  assert.deepEqual(statuses, Array(CALLS).fill(200));
  assert.equal(keys.size, 1);
  assert.ok(!keys.has("HW-REVOKED-OLD"), "a revoked key must never be handed back");
  assert.equal(await grantedKeyCount(email), 1);
  assert.equal(await internalBundleCredits(), 5000);
});
