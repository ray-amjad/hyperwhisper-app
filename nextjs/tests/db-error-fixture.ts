/**
 * #1039: a REAL drizzle `DrizzleQueryError` (installed drizzle-orm) wrapping a
 * REAL pg `DatabaseError` (installed pg), shaped the way node-postgres throws
 * it. Its bound params carry a customer address and a full licence key, and so
 * does the pg error's own message and `detail` — every place a value can hide.
 *
 * `formatLogArgs` renders a console call the way Node does (`util.inspect` for
 * anything that is not a string), so a test sees the stack, `params`, `cause`
 * and `detail` that a production log line would carry.
 */
import { inspect } from "node:util";

import { DrizzleQueryError } from "drizzle-orm";
import { DatabaseError } from "pg";

export const LEAKY_EMAIL = "leaky.buyer@example.com";
export const LEAKY_KEY = "HW-LEAK-7Q2Z-9XK4";

export const LEAKY_SQL =
  'insert into "account_keys" ("key", "email", "stripe_session_id") values ($1, $2, $3) returning "id"';

export function leakyDbError(code = "23505"): DrizzleQueryError {
  const pg = new DatabaseError(
    code === "23505"
      ? 'duplicate key value violates unique constraint "account_keys_key_unique"'
      : `invalid input syntax for type uuid: "${LEAKY_KEY}" (${LEAKY_EMAIL})`,
    0,
    "error",
  );
  Object.assign(pg, {
    severity: "ERROR",
    code,
    detail: `Key (email)=(${LEAKY_EMAIL}) already exists.`,
    where: `unnamed portal parameter $1 = '${LEAKY_KEY}'`,
    schema: "public",
    table: "account_keys",
    constraint: "account_keys_key_unique",
  });
  return new DrizzleQueryError(LEAKY_SQL, [LEAKY_KEY, LEAKY_EMAIL, "cs_leak_1"], pg);
}

export function formatLogArgs(args: unknown[]): string {
  return args.map((a) => (typeof a === "string" ? a : inspect(a, { depth: 6 }))).join(" ");
}

/** Every line that carries the address or the key, in any case. */
export function leakyLines(lines: string[]): string[] {
  const email = LEAKY_EMAIL.toLowerCase();
  const key = LEAKY_KEY.toLowerCase();
  return lines.filter((line) => {
    const lower = line.toLowerCase();
    return lower.includes(email) || lower.includes(key);
  });
}
