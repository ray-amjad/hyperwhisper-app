/**
 * `credit_pool_id` (#2026): the opaque per-user id HyperWhisper Cloud keys its
 * credit holds by. Stable per user, different across users, never the user id,
 * and absent when the secret is not configured.
 */
import assert from "node:assert/strict";
import { createHmac } from "node:crypto";
import test from "node:test";

import { serverSchema } from "@/src/env/schema.server.mjs";
import { creditPoolIdFor } from "@/src/lib/credit-pool-id";

const SECRET = "test-only-credit-pool-secret-0123456789abcdef";

test("is HMAC-SHA256 of the user id under the secret, as 64 hex characters", () => {
  const id = creditPoolIdFor("user_42", SECRET);
  assert.match(id ?? "", /^[0-9a-f]{64}$/);
  assert.equal(
    id,
    createHmac("sha256", SECRET).update("credit-pool:v1:user_42").digest("hex"),
  );
});

test("is the same for one user on every call and differs across users", () => {
  assert.equal(
    creditPoolIdFor("user_42", SECRET),
    creditPoolIdFor("user_42", SECRET),
  );
  assert.notEqual(
    creditPoolIdFor("user_42", SECRET),
    creditPoolIdFor("user_43", SECRET),
  );
});

test("never contains the user id, and changes with the secret", () => {
  const id = creditPoolIdFor("user_42", SECRET) ?? "";
  assert.ok(!id.includes("user_42"));
  assert.notEqual(id, creditPoolIdFor("user_42", `${SECRET}-rotated`));
});

test("is null when the secret is not set", () => {
  assert.equal(creditPoolIdFor("user_42", undefined), null);
  assert.equal(creditPoolIdFor("user_42", ""), null);
});

test("the env schema treats an empty secret as unset, as creditPoolIdFor does, and still refuses a short one", () => {
  const field = serverSchema.shape.CREDIT_POOL_ID_SECRET;
  assert.equal(field.safeParse(undefined).success, true);
  assert.equal(field.safeParse("").success, true);
  assert.equal(field.safeParse(SECRET).success, true);
  assert.equal(field.safeParse("x".repeat(32)).success, true);
  assert.equal(field.safeParse("x".repeat(31)).success, false);
  assert.equal(field.safeParse(" ").success, false);
});
