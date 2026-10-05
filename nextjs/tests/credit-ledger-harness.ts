/**
 * Test harness for the credit ledger in `src/lib/db-layer.ts`: granting a
 * credit lot, the Stripe event dedup, spending oldest-first, the refund
 * clawback, and the balance reads that heal the `credit_balances` cache.
 *
 * That code is raw SQL with `FOR UPDATE`, `ON CONFLICT` and `now()`. A fake
 * query builder would only check that the SQL was written, not that it is
 * right. So the harness gives `src/db/index.ts` a real Postgres instead:
 * PGlite, an in-process WebAssembly build of Postgres. It needs no server,
 * so it runs in nextjs-ci as it does here. The schema comes from the real
 * migrations in `nextjs/drizzle`, applied by the real Drizzle migrator.
 *
 * The mock is installed when this module is evaluated, and `loadDbLayer()`
 * is the only way the tests reach the module. A test file that imports
 * `src/lib/db-layer.ts` itself would bind to the real `pg` pool, so do not.
 */
import { mock } from "node:test";

import { PGlite } from "@electric-sql/pglite";
import { sql } from "drizzle-orm";
import { drizzle } from "drizzle-orm/pglite";
import { migrate } from "drizzle-orm/pglite/migrator";

import * as schema from "../src/db/schema";

interface ModuleMocker {
  module(
    specifier: string,
    options: {
      namedExports?: Record<string, unknown>;
      defaultExport?: unknown;
    },
  ): void;
}

function moduleUrl(relative: string): string {
  return new URL(relative, import.meta.url).href;
}

const client = new PGlite();
export const db = drizzle(client, { schema });

(mock as unknown as ModuleMocker).module(moduleUrl("../src/db/index.ts"), {
  namedExports: { db },
});

let migrated: Promise<void> | null = null;

/** Apply every migration in `nextjs/drizzle` once per test file. */
export function migrateOnce(): Promise<void> {
  migrated ??= migrate(db, {
    migrationsFolder: new URL("../drizzle", import.meta.url).pathname,
  });
  return migrated;
}

/** Empty every table the ledger touches. The users go last (FK cascade). */
export async function resetDatabase(): Promise<void> {
  await migrateOnce();
  await db.execute(sql`
    TRUNCATE credit_grants, credit_balances, stripe_processed_events,
      account_keys, account, session, "user" CASCADE
  `);
}

/** Insert a bare user row and return its id. The ledger rows need one (FK). */
export async function seedUser(id: string): Promise<string> {
  await db.insert(schema.user).values({
    id,
    name: id,
    email: `${id}@example.test`,
    emailVerified: false,
  });
  return id;
}

export interface GrantRow {
  id: string;
  userId: string;
  sourceType: string;
  sourceId: string;
  original: number;
  remaining: number;
  refunded: number;
  status: string;
  expiresAt: Date | null;
}

/** Insert a grant row directly, for states the public API cannot make. */
export async function seedGrant(g: {
  userId: string;
  sourceType?: string;
  sourceId: string;
  amount: number;
  remaining?: number;
  status?: string;
  expiresAt?: Date | null;
  createdAt?: Date;
}): Promise<string> {
  const [row] = await db
    .insert(schema.creditGrants)
    .values({
      userId: g.userId,
      sourceType: g.sourceType ?? "admin_manual",
      sourceId: g.sourceId,
      originalAmount: g.amount.toString(),
      remainingAmount: (g.remaining ?? g.amount).toString(),
      status: g.status ?? "active",
      expiresAt: g.expiresAt === undefined ? null : g.expiresAt,
      ...(g.createdAt ? { createdAt: g.createdAt } : {}),
    })
    .returning({ id: schema.creditGrants.id });
  return row.id;
}

/** Every grant of a user, keyed by source id, as plain numbers. */
export async function grantsBySource(userId: string): Promise<Map<string, GrantRow>> {
  const rows = await db.query.creditGrants.findMany({
    where: (t, { eq }) => eq(t.userId, userId),
  });
  return new Map(
    rows.map((r) => [
      r.sourceId,
      {
        id: r.id,
        userId: r.userId,
        sourceType: r.sourceType,
        sourceId: r.sourceId,
        original: Number(r.originalAmount),
        remaining: Number(r.remainingAmount),
        refunded: Number(r.refundedAmount),
        status: r.status,
        expiresAt: r.expiresAt,
      },
    ]),
  );
}

/** The cached balance row, or null when the cache has no row. */
export async function cachedBalance(userId: string): Promise<number | null> {
  const row = await db.query.creditBalances.findFirst({
    where: (t, { eq }) => eq(t.userId, userId),
  });
  return row ? Number(row.balance) : null;
}

/** Write the cache directly, to model drift between it and the grants. */
export async function setCachedBalance(userId: string, balance: number): Promise<void> {
  await db
    .insert(schema.creditBalances)
    .values({ userId, balance: balance.toString() })
    .onConflictDoUpdate({
      target: schema.creditBalances.userId,
      set: { balance: balance.toString() },
    });
}

export async function stripeEventCount(): Promise<number> {
  const r = await db.execute<{ n: number }>(
    sql`SELECT count(*)::int AS n FROM stripe_processed_events`,
  );
  return Number(r.rows[0].n);
}

export async function loadDbLayer() {
  await migrateOnce();
  return import("../src/lib/db-layer");
}
