/**
 * #1039: `describeDbError` and `dbErrorCode` against a REAL drizzle
 * `DrizzleQueryError` wrapping a REAL pg `DatabaseError`.
 */
import assert from "node:assert/strict";
import test from "node:test";

import { DrizzleQueryError } from "drizzle-orm";

import { dbErrorCode, dbErrorConstraint, describeDbError } from "../lib/shared/db-error";
import {
  LEAKY_EMAIL,
  LEAKY_KEY,
  LEAKY_SQL,
  SESSION_INDEX,
  formatLogArgs,
  leakyDbError,
  leakyLines,
} from "./db-error-fixture";

test("positive control: the raw drizzle error, logged as-is, leaks both values", () => {
  const line = formatLogArgs(["Failed to store license key:", leakyDbError()]);
  assert.ok(line.includes(LEAKY_EMAIL));
  assert.ok(line.includes(LEAKY_KEY));
});

test("describeDbError keeps kind, code, SQL and identifiers, and drops params, message, detail and stack", () => {
  const err = leakyDbError("22P02");
  const described = describeDbError(err);

  assert.deepEqual(described, {
    kind: "DrizzleQueryError",
    code: "22P02",
    query: LEAKY_SQL,
    constraint: SESSION_INDEX,
    table: "account_keys",
    column: undefined,
  });
  assert.deepEqual(leakyLines([formatLogArgs(["x", described])]), []);
});

test("describeDbError redacts a bare pg DatabaseError too", () => {
  const pg = leakyDbError("22P02").cause;
  const described = describeDbError(pg);

  assert.equal((described as { code: string }).code, "22P02");
  assert.equal((described as { kind: string }).kind, "DatabaseError");
  assert.deepEqual(leakyLines([formatLogArgs([described])]), []);
});

test("describeDbError passes a non-database error through unchanged", () => {
  const err = new Error("Stripe refused the session");
  assert.equal(describeDbError(err), err);
  assert.equal(describeDbError("text"), "text");
  assert.equal(describeDbError(null), null);
});

test("dbErrorCode reads drizzle's .cause.code, and an own .code first", () => {
  const drizzle = leakyDbError("23505");
  assert.equal("code" in drizzle, false, "drizzle 0.45 leaves no own code");
  assert.equal(dbErrorCode(drizzle), "23505");
  assert.equal(dbErrorCode(Object.assign(new Error("x"), { code: "08006" })), "08006");
  assert.equal(dbErrorCode(new Error("x")), undefined);
  assert.equal(dbErrorCode(undefined), undefined);
});

test("dbErrorConstraint reads drizzle's .cause.constraint, and an own .constraint first", () => {
  assert.equal(dbErrorConstraint(leakyDbError("23505")), SESSION_INDEX);
  assert.equal(dbErrorConstraint(leakyDbError("23505", "idx_account_keys_key")), "idx_account_keys_key");
  assert.equal(dbErrorConstraint(leakyDbError("23505", null)), undefined);
  assert.equal(dbErrorConstraint(Object.assign(new Error("x"), { constraint: "own" })), "own");
  assert.equal(dbErrorConstraint(new Error("x")), undefined);
  assert.equal(dbErrorConstraint(null), undefined);
});

// A drizzle error whose cause is NOT a pg server error (a dropped connection,
// a connect timeout) keeps the cause's message as `reason` — redacted.
test("describeDbError keeps a non-pg cause's message as a redacted reason", () => {
  const dropped = new DrizzleQueryError(
    LEAKY_SQL,
    [LEAKY_KEY, LEAKY_EMAIL],
    new Error("Connection terminated unexpectedly"),
  );
  const described = describeDbError(dropped) as Record<string, unknown>;
  assert.equal(described.reason, "Connection terminated unexpectedly");
  assert.equal(described.query, LEAKY_SQL);
  assert.deepEqual(leakyLines([formatLogArgs([described])]), []);

  const timeout = new DrizzleQueryError(
    LEAKY_SQL,
    [LEAKY_KEY],
    new Error(`timeout exceeded when trying to connect for ${LEAKY_EMAIL}`),
  );
  const t = describeDbError(timeout) as Record<string, unknown>;
  assert.equal(t.reason, "timeout exceeded when trying to connect for [redacted]");

  const long = new DrizzleQueryError(LEAKY_SQL, [], new Error("x".repeat(5000)));
  assert.equal((describeDbError(long) as { reason: string }).reason.length, 300);
});

test("describeDbError never keeps a pg server error's message as a reason", () => {
  const described = describeDbError(leakyDbError("22P02")) as Record<string, unknown>;
  assert.equal("reason" in described, false);
  const bare = describeDbError(leakyDbError("22P02").cause) as Record<string, unknown>;
  assert.equal("reason" in bare, false);
});
