/**
 * `getStripeCustomerIdsForUserIds` (src/lib/db-layer.ts) against a real
 * Postgres (PGlite, `credit-ledger-harness.ts`) with the real migrations.
 *
 * Total spend sums these ids, on the admin Customers page and in the admin MCP
 * `find_customer`, so the query must have no per-account row cap: an old paid
 * key behind 100 newer keys still counts (#3056).
 */
import { before, beforeEach, test } from "node:test";
import assert from "node:assert/strict";

import { db, loadDbLayer, resetDatabase, seedUser } from "./credit-ledger-harness";
import * as schema from "../src/db/schema";

let L: Awaited<ReturnType<typeof loadDbLayer>>;

before(async () => {
  L = await loadDbLayer();
});

beforeEach(async () => {
  await resetDatabase();
  await seedUser("u1");
  await seedUser("u2");
});

async function seedKey(
  userId: string,
  n: number,
  stripeCustomerId: string | null,
  createdAt: Date,
): Promise<void> {
  await db.insert(schema.accountKeys).values({
    key: `HW-TEST-${userId.toUpperCase()}-${String(n).padStart(4, "0")}`,
    email: `${userId}@example.test`,
    userId,
    stripeCustomerId,
    createdAt,
  });
}

test("every distinct Stripe customer id per account, past the 100-key list cap", async () => {
  await seedKey("u1", 0, "cus_old", new Date(Date.UTC(2024, 0, 1)));
  for (let i = 1; i <= 100; i++) {
    await seedKey("u1", i, i % 2 ? "cus_new" : null, new Date(Date.UTC(2026, 0, 1, 0, i)));
  }
  await seedKey("u2", 0, "cus_u2", new Date(Date.UTC(2025, 0, 1)));

  // The capped list really does leave the old key out...
  const capped = await L.getAccountKeysWithCreditsForUserIds(["u1"]);
  assert.equal(capped.length, 100);
  assert.ok(!capped.some((l) => l.stripeCustomerId === "cus_old"));

  // ...and the id query still has it.
  const byUser = await L.getStripeCustomerIdsForUserIds(["u1", "u2"]);
  assert.deepEqual(Array.from(byUser.get("u1") ?? []).sort(), ["cus_new", "cus_old"]);
  assert.deepEqual(Array.from(byUser.get("u2") ?? []), ["cus_u2"]);
});

test("an account without Stripe ids is absent, and no ids means no rows", async () => {
  await seedKey("u1", 0, null, new Date());

  assert.equal((await L.getStripeCustomerIdsForUserIds(["u1"])).size, 0);
  assert.equal((await L.getStripeCustomerIdsForUserIds([])).size, 0);
});
