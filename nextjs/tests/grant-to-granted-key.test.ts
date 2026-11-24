/**
 * `grantCreditLotToGrantedKey` (src/lib/db-layer.ts), run against a real
 * Postgres (PGlite, `credit-ledger-harness.ts`) with the real migrations.
 *
 * The admin MCP grant tools go through it: the key's status is read FOR SHARE
 * inside the same transaction that writes the lot, so a key that is no longer
 * `granted` never receives credits, and nothing is written when it is refused.
 */
import { before, beforeEach, test } from "node:test";
import assert from "node:assert/strict";

import {
  db,
  grantsBySource,
  loadDbLayer,
  resetDatabase,
  seedUser,
} from "./credit-ledger-harness";
import * as schema from "../src/db/schema";

let L: Awaited<ReturnType<typeof loadDbLayer>>;

const KEY = "HW-TEST-0000-1111";

before(async () => {
  L = await loadDbLayer();
});

beforeEach(async () => {
  await resetDatabase();
  await seedUser("u1");
});

async function seedKey(status: string): Promise<void> {
  await db.insert(schema.accountKeys).values({
    key: KEY,
    email: "u1@example.test",
    userId: "u1",
    status,
  });
}

const grant = {
  amount: 2500,
  sourceType: "admin_manual" as const,
  sourceId: "mcp:abc",
};

test("a granted key gets the lot, and the same source id is a duplicate", async () => {
  await seedKey("granted");

  const first = await L.grantCreditLotToGrantedKey(KEY, grant);
  assert.equal(first.status, "processed");
  assert.ok("balance" in first);
  assert.equal(first.balance, 2500);
  assert.ok("license" in first);
  assert.equal(first.license.userId, "u1");

  const second = await L.grantCreditLotToGrantedKey(KEY, grant);
  assert.equal(second.status, "duplicate");

  const grants = await grantsBySource("u1");
  assert.equal(grants.size, 1);
  assert.equal(grants.get("mcp:abc")?.original, 2500);
});

for (const status of ["revoked", "refunded"]) {
  test(`a ${status} key is refused and nothing is written`, async () => {
    await seedKey(status);

    const result = await L.grantCreditLotToGrantedKey(KEY, grant);
    assert.equal(result.status, "license_not_granted");
    assert.ok("license" in result);
    assert.equal(result.license.status, status);
    assert.equal((await grantsBySource("u1")).size, 0);
  });
}

test("an unknown key is not found and nothing is written", async () => {
  const result = await L.grantCreditLotToGrantedKey("HW-TEST-0000-9999", grant);
  assert.equal(result.status, "license_not_found");
  assert.equal((await grantsBySource("u1")).size, 0);
});
