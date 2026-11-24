/**
 * `getCreditGrantsForUsers` (src/lib/credit-ledger.ts), run against a real
 * Postgres (PGlite, `credit-ledger-harness.ts`) with the real migrations.
 *
 * The admin MCP `find_customer` lists grant history with it. The limit is PER
 * ACCOUNT, so an account with a long history never crowds another account's
 * history out of the reply, and the accounts that were cut are named.
 */
import { before, beforeEach, test } from "node:test";
import assert from "node:assert/strict";

import {
  loadDbLayer,
  resetDatabase,
  seedGrant,
  seedUser,
} from "./credit-ledger-harness";

let L: Awaited<ReturnType<typeof loadDbLayer>>;

before(async () => {
  L = await loadDbLayer();
});

beforeEach(async () => {
  await resetDatabase();
  await seedUser("busy");
  await seedUser("quiet");
});

test("the limit applies per account, newest first, and names the cut accounts", async () => {
  for (let i = 0; i < 5; i++) {
    await seedGrant({
      userId: "busy",
      sourceId: `busy-${i}`,
      amount: 100 + i,
      createdAt: new Date(Date.UTC(2026, 0, 1 + i)),
    });
  }
  await seedGrant({
    userId: "quiet",
    sourceId: "quiet-0",
    amount: 7,
    createdAt: new Date(Date.UTC(2025, 0, 1)),
  });

  const { rows, truncatedUserIds } = await L.getCreditGrantsForUsers(
    ["busy", "quiet"],
    2,
  );
  const busy = rows.filter((r) => r.userId === "busy");
  const quiet = rows.filter((r) => r.userId === "quiet");

  // The oldest grant of all still shows: busy's history did not crowd it out.
  assert.deepEqual(quiet.map((r) => r.sourceId), ["quiet-0"]);
  assert.deepEqual(busy.map((r) => r.sourceId), ["busy-4", "busy-3"]);
  assert.deepEqual(Array.from(truncatedUserIds), ["busy"]);
});

test("exactly the limit is not reported as truncated", async () => {
  await seedGrant({ userId: "busy", sourceId: "a", amount: 1 });
  await seedGrant({ userId: "busy", sourceId: "b", amount: 2 });

  const { rows, truncatedUserIds } = await L.getCreditGrantsForUsers(["busy"], 2);

  assert.equal(rows.length, 2);
  assert.equal(truncatedUserIds.size, 0);
});

test("no accounts means no query and no rows", async () => {
  const { rows, truncatedUserIds } = await L.getCreditGrantsForUsers([]);

  assert.deepEqual(rows, []);
  assert.equal(truncatedUserIds.size, 0);
});
