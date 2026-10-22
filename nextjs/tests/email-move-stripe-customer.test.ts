/**
 * #2460: an admin email move (`updateCustomerEmail`) drops each key's cached
 * Stripe customer, which was created for the OLD address, so the next top-up
 * and the billing portal never reach it. Runs the real function against the
 * real migrations on PGlite (`tests/credit-ledger-harness.ts`).
 */
import assert from "node:assert/strict";
import { beforeEach, test } from "node:test";

import { eq } from "drizzle-orm";

import * as schema from "../src/db/schema";

import {
  db,
  loadDbLayer,
  resetDatabase,
  seedUser,
} from "./credit-ledger-harness";

beforeEach(resetDatabase);

test("an email move clears the cached Stripe customer on every key of the account", async () => {
  const { updateCustomerEmail } = await loadDbLayer();
  const userId = await seedUser("user_email_move");
  const other = await seedUser("user_untouched");
  await db.insert(schema.accountKeys).values([
    {
      userId,
      key: "HW-TEST-MOVE-0001",
      email: "user_email_move@example.test",
      stripeCustomerId: "cus_old",
    },
    {
      userId,
      key: "HW-TEST-MOVE-0002",
      email: "user_email_move@example.test",
      status: "revoked",
      stripeCustomerId: "cus_old_2",
    },
    {
      userId: other,
      key: "HW-TEST-KEEP-0001",
      email: "user_untouched@example.test",
      stripeCustomerId: "cus_other",
    },
  ]);

  await updateCustomerEmail(userId, "New-Owner@Example.test");

  const moved = await db
    .select({
      email: schema.accountKeys.email,
      stripeCustomerId: schema.accountKeys.stripeCustomerId,
    })
    .from(schema.accountKeys)
    .where(eq(schema.accountKeys.userId, userId));
  assert.deepEqual(moved, [
    { email: "new-owner@example.test", stripeCustomerId: null },
    { email: "new-owner@example.test", stripeCustomerId: null },
  ]);

  // Another account's cached customer is left alone.
  const [untouched] = await db
    .select({ stripeCustomerId: schema.accountKeys.stripeCustomerId })
    .from(schema.accountKeys)
    .where(eq(schema.accountKeys.userId, other));
  assert.equal(untouched.stripeCustomerId, "cus_other");
});
