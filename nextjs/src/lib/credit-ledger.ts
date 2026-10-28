/**
 * Credit ledger: credit grants, spends, refunds and the cached balance.
 *
 * Split out of db-layer.ts (#1595), which re-exports every public name here,
 * so importers keep importing `@/src/lib/db-layer`.
 */

import { eq, and, desc, inArray, sql, type SQL } from "drizzle-orm";
import { db } from "@/src/db";
import { creditBalances, creditGrants, stripeProcessedEvents } from "@/src/db/schema";

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

export type CreditGrantSourceType =
  | "license_bundle"
  | "polar_bundle"
  | "internal_bundle"
  | "admin_license_bundle"
  | "stripe_credit_pack"
  | "admin_manual"
  | "legacy_unknown";

export interface StripeCreditGrantInsert {
  eventId: string;
  eventType: string;
  stripeObjectId: string;
  userId: string;
  creditAmount: number;
  sourceType?: CreditGrantSourceType;
  sourceId?: string;
}

export interface CreditGrantInsert {
  userId: string;
  amount: number;
  sourceType: CreditGrantSourceType;
  sourceId: string;
}

export interface CreditGrantRefund {
  sourceType: CreditGrantSourceType;
  sourceId: string;
  /**
   * The TOTAL credits of this grant refunded so far, across every refund of
   * its purchase (Stripe's `amount_refunded` is cumulative, so the caller
   * passes the running total, not this refund's share). Omitted means the
   * whole grant is refunded.
   */
  refundedCreditsTotal?: number;
}

export interface CreditGrantRefundResult {
  /**
   * "duplicate": the grant already records this refund total. "not_found":
   * no grant exists for the source yet (e.g. the refund beat the mint, #1798).
   */
  status: "processed" | "duplicate" | "not_found";
  /** Credits of value this call newly recorded as refunded. */
  refundedAmount: number;
  /** Credits this call took off the grant's spendable remainder. */
  removedAmount: number;
}

// ---------------------------------------------------------------------------
// Credit Balances and Grants
// ---------------------------------------------------------------------------

// A handle that can run queries: either the pooled `db` connection or a
// transaction handle. Used so balance reads can join the caller's transaction
// instead of opening a second connection (which sees a stale snapshot and ties
// up the pool while row locks are held).
export type DbExecutor = typeof db | Parameters<Parameters<typeof db.transaction>[0]>[0];

// A grant still backs spendable balance when it has no expiry or hasn't expired
// yet. This is the single source of that rule for the raw-SQL credit queries
// below (balance read, spend, clawback) — keep it here so expiry semantics live
// in one place. The Drizzle-builder variant (using creditGrants.expiresAt) is
// expressed inline where the query is built with the query builder.
const ACTIVE_GRANT_EXPIRY = sql`(expires_at IS NULL OR expires_at > now())`;

/**
 * Authoritative spendable balance: the SUM of remaining_amount across a
 * license's still-active grants. The credit_grants rows are the source of
 * truth; credit_balances is only a denormalized cache of this value. Reading
 * via the passed executor lets callers inside a FOR UPDATE transaction get a
 * consistent, locked view rather than a stale snapshot from the outer pool.
 */
async function getActiveGrantsTotal(
  executor: DbExecutor,
  userId: string
): Promise<number> {
  const result = await executor.execute<{ total: string | null }>(sql`
    SELECT COALESCE(SUM(remaining_amount), 0) AS total
    FROM credit_grants
    WHERE user_id = ${userId}
      AND status = 'active'
      AND remaining_amount > 0
      AND ${ACTIVE_GRANT_EXPIRY}
  `);
  return Number(result.rows[0]?.total ?? 0);
}

/**
 * Force the credit_balances cache to equal the authoritative grant total,
 * reconciling any drift between the two. Returns the reconciled balance. This
 * is the single place that heals divergence (e.g. a legacy balance row with no
 * matching grants, or a partially-applied transaction) so the cache can never
 * permanently show credits the grants can't back, or vice versa.
 */
async function reconcileCreditBalance(
  tx: Parameters<Parameters<typeof db.transaction>[0]>[0],
  userId: string
): Promise<number> {
  const total = await getActiveGrantsTotal(tx, userId);
  await tx
    .insert(creditBalances)
    .values({ userId, balance: total.toString() })
    .onConflictDoUpdate({
      target: creditBalances.userId,
      set: {
        balance: total.toString(),
        updatedAt: new Date(),
      },
    });
  return total;
}

async function incrementCreditBalance(
  tx: Parameters<Parameters<typeof db.transaction>[0]>[0],
  userId: string,
  amount: number
): Promise<number> {
  const [row] = await tx
    .insert(creditBalances)
    .values({ userId, balance: amount.toString() })
    .onConflictDoUpdate({
      target: creditBalances.userId,
      set: {
        balance: sql`${creditBalances.balance} + ${amount}`,
        updatedAt: new Date(),
      },
    })
    .returning({ balance: creditBalances.balance });

  return Number(row.balance);
}

// Every credit grant expires one year after it is created (OpenRouter model):
// the ToS reserves the right to expire unused credits 365 days after purchase.
// Stamped here so it covers every insert path (paid packs, minted keys, admin
// grants); enforcement is the lazy `expires_at > now()` filter on reads/spends.
const CREDIT_GRANT_TTL_MS = 365 * 24 * 60 * 60 * 1000;

export async function grantCreditLotInTransaction(
  tx: Parameters<Parameters<typeof db.transaction>[0]>[0],
  data: CreditGrantInsert
): Promise<{ status: "processed" | "duplicate"; balance: number }> {
  const expiresAt = new Date(Date.now() + CREDIT_GRANT_TTL_MS);
  const [grantRow] = await tx
    .insert(creditGrants)
    .values({
      userId: data.userId,
      sourceType: data.sourceType,
      sourceId: data.sourceId,
      originalAmount: data.amount.toString(),
      remainingAmount: data.amount.toString(),
      refundedAmount: "0",
      status: "active",
      expiresAt,
    })
    .onConflictDoNothing({
      target: [creditGrants.sourceType, creditGrants.sourceId],
    })
    .returning({ id: creditGrants.id });

  if (!grantRow) {
    // Read inside the transaction so the balance reflects the locked, in-tx
    // view rather than a stale snapshot from a second pooled connection.
    const balance = await getActiveGrantsTotal(tx, data.userId);
    return { status: "duplicate", balance };
  }

  const balance = await incrementCreditBalance(tx, data.userId, data.amount);
  return { status: "processed", balance };
}

export async function grantCreditLot(
  data: CreditGrantInsert
): Promise<{ status: "processed" | "duplicate"; balance: number }> {
  return db.transaction((tx) => grantCreditLotInTransaction(tx, data));
}

export async function grantCreditsForStripeEvent(
  data: StripeCreditGrantInsert
): Promise<"processed" | "duplicate"> {
  const insertedEvent = await db.transaction(async (tx) => {
    const [eventRow] = await tx
      .insert(stripeProcessedEvents)
      .values({
        eventId: data.eventId,
        eventType: data.eventType,
        stripeObjectId: data.stripeObjectId,
      })
      .onConflictDoNothing({ target: stripeProcessedEvents.stripeObjectId })
      .returning({ eventId: stripeProcessedEvents.eventId });

    if (!eventRow) {
      return null;
    }

    await grantCreditLotInTransaction(tx, {
      userId: data.userId,
      amount: data.creditAmount,
      sourceType: data.sourceType ?? "stripe_credit_pack",
      sourceId: data.sourceId ?? data.stripeObjectId,
    });

    return eventRow;
  });

  return insertedEvent ? "processed" : "duplicate";
}

/**
 * True when a Stripe object (a Checkout Session id) already has its completed
 * event recorded in `stripe_processed_events`. The table is keyed by event id
 * with a unique index on `stripe_object_id`, and a credit purchase writes the
 * session id there in the same transaction as its grant (#1834).
 */
export async function hasProcessedStripeObject(stripeObjectId: string): Promise<boolean> {
  const [row] = await db
    .select({ eventId: stripeProcessedEvents.eventId })
    .from(stripeProcessedEvents)
    .where(eq(stripeProcessedEvents.stripeObjectId, stripeObjectId))
    .limit(1);
  return Boolean(row);
}

/**
 * Record a refund against ONE grant and take the refunded credits off it.
 *
 * RULE (#1351, Ray 2026-10-07): a refund removes credits from the refunded
 * purchase's own grant only — never from another grant on the account — and
 * never more than that grant has left unrefunded. A buyer with pack A and pack
 * B who refunds pack A keeps every credit of pack B.
 *
 * DEDUPE: `refunded_amount` holds the running total of credits refunded on
 * this grant. The caller passes the new running total (Stripe's
 * `amount_refunded` is cumulative), and only `total - refunded_amount` is
 * removed. A repeated, re-delivered or out-of-order event is a no-op, and a
 * second partial refund removes only its own part.
 *
 * SPENT CREDITS: the policy refunds only unused credits. When the grant has
 * fewer credits left than the refund is worth, the grant goes to 0 and the
 * rest is NOT drawn from other grants; the full value is still recorded on
 * `refunded_amount` so the running total stays right. The caller sees the gap
 * as `refundedAmount - removedAmount` and logs it.
 */
export async function refundCreditGrant(
  data: CreditGrantRefund
): Promise<CreditGrantRefundResult> {
  return db.transaction(async (tx) => {
    // Resolve the refunded purchase by provenance. No remaining_amount filter:
    // a fully-spent grant still records its refund (and is a duplicate on the
    // next delivery). FOR UPDATE serialises this against a concurrent spend
    // and a concurrent delivery of the same refund.
    const target = await tx.execute<{
      id: string;
      user_id: string;
      original_amount: string;
      remaining_amount: string;
      refunded_amount: string;
      status: string;
    }>(sql`
      SELECT id, user_id, original_amount, remaining_amount, refunded_amount, status
      FROM credit_grants
      WHERE source_type = ${data.sourceType}
        AND source_id = ${data.sourceId}
      FOR UPDATE
    `);

    const grant = target.rows[0];
    if (!grant) {
      // Not a duplicate: nothing was refunded. The caller decides whether a
      // missing grant is normal or must be retried (#1798).
      return { status: "not_found", refundedAmount: 0, removedAmount: 0 };
    }

    const originalAmount = Number(grant.original_amount);
    const remainingAmount = Number(grant.remaining_amount);
    const alreadyRefunded = Number(grant.refunded_amount);

    // The running total this refund brings the grant to, capped at the grant.
    const refundedTotal = Math.min(
      originalAmount,
      Math.max(0, data.refundedCreditsTotal ?? originalAmount)
    );
    const refundNow = refundedTotal - alreadyRefunded;
    if (refundNow <= 0) {
      return { status: "duplicate", refundedAmount: 0, removedAmount: 0 };
    }

    // Take the refunded credits off THIS grant only, flooring at 0.
    const removedAmount = Math.min(remainingAmount, refundNow);
    const newRemaining = remainingAmount - removedAmount;

    await tx
      .update(creditGrants)
      .set({
        remainingAmount: newRemaining.toString(),
        refundedAmount: refundedTotal.toString(),
        // A grant with nothing left after a refund is closed as refunded. A
        // partly refunded grant with credits left keeps its status, so the
        // rest stays spendable.
        status: newRemaining <= 0 ? "refunded" : grant.status,
        updatedAt: new Date(),
      })
      .where(eq(creditGrants.id, grant.id));

    // Reconcile the cache to the authoritative grant total, healing any drift.
    await reconcileCreditBalance(tx, grant.user_id);
    return {
      status: "processed",
      refundedAmount: refundNow,
      removedAmount,
    };
  });
}

/**
 * True (in SQL) when `remaining` is stored as 0.00 or less. A spend subtracts
 * in floating point, so a charge spanning two grants can leave a residue like
 * 2.78e-17 that the numeric(20,2) column stores as 0.00. Deciding the status
 * on the stored value means a grant that holds 0.00 is never left 'active'
 * (#2620).
 */
function storesAsZero(remaining: number): SQL {
  return sql`${remaining.toString()}::numeric(20,2) <= 0`;
}

export async function spendCreditGrantsByProvenance(
  userId: string,
  amount: number
): Promise<{ balance: number; deductedAmount: number }> {
  return db.transaction(async (tx) => {
    const result = await tx.execute<{ id: string; remaining_amount: string }>(sql`
      SELECT id, remaining_amount
      FROM credit_grants
      WHERE user_id = ${userId}
        AND remaining_amount > 0
        AND status = 'active'
        AND ${ACTIVE_GRANT_EXPIRY}
      -- Spend OLDEST-FIRST: soonest-to-expire grants are consumed before grants
      -- that still have time on them (and never-expiring grants last), so a user
      -- naturally burns down credits before they lapse. This replaces the older
      -- provenance (paid-pack-first) order. A refund takes credits from the
      -- refunded purchase's own grant only (#1351), so what a pack has left
      -- here is what a refund of that pack can remove. Expired grants are
      -- excluded above and never spent.
      ORDER BY
        expires_at ASC,
        created_at ASC,
        id
      FOR UPDATE
    `);

    let remainingToDeduct = amount;
    let deductedAmount = 0;

    for (let index = 0; index < result.rows.length; index++) {
      if (remainingToDeduct <= 0) break;

      const grant = result.rows[index];
      const grantRemaining = Number(grant.remaining_amount);
      const deduction = Math.min(grantRemaining, remainingToDeduct);

      if (deduction <= 0) continue;

      const newRemaining = grantRemaining - deduction;
      await tx
        .update(creditGrants)
        .set({
          remainingAmount: newRemaining.toString(),
          status: sql`CASE WHEN ${storesAsZero(newRemaining)} THEN 'spent' ELSE 'active' END`,
          updatedAt: new Date(),
        })
        .where(eq(creditGrants.id, grant.id));

      remainingToDeduct -= deduction;
      deductedAmount += deduction;
    }

    // Reconcile the cached balance to the authoritative grant total in the same
    // transaction (and same locked view). This both (a) reads via `tx` instead
    // of a second pooled connection, and (b) self-heals any drift between the
    // credit_balances cache and the grant rows, so the cache can never report
    // spendable credits the grants can't actually back, or vice versa.
    const balance = await reconcileCreditBalance(tx, userId);

    return { balance, deductedAmount };
  });
}

/**
 * Atomically deduct credits from source-tracked grants, flooring at zero.
 *
 * Returns the new cached aggregate balance. The public API response shape is
 * preserved, but the underlying grant rows record which source was consumed.
 *
 * The floor at zero swallows any part of `amount` the grants cannot cover. That
 * overspend is written off, never carried as a negative balance, and logged as
 * one structured `credits_shortfall` line (#2008) so it can be counted. The
 * cloud logs the same event, with `source: "cloud"`, for overspend it catches
 * before it charges. No account identifier is logged.
 */
export async function deductCreditBalance(userId: string, amount: number): Promise<number> {
  const result = await spendCreditGrantsByProvenance(userId, amount);
  const shortfall = amount - result.deductedAmount;

  // Grant amounts are stored to the cent, so anything under one is float noise.
  if (shortfall > 0.005) {
    console.warn(
      JSON.stringify({
        event: "credits_shortfall",
        source: "license_api",
        requestedCredits: amount,
        deductedCredits: result.deductedAmount,
        shortfallCredits: Math.round(shortfall * 100) / 100,
      }),
    );
  }

  return result.balance;
}

/**
 * Spendable credit balance for a single license.
 *
 * The credit_grants rows are the source of truth; credit_balances is only a
 * cache of their summed remaining_amount. To avoid ever reporting credits the
 * user cannot actually consume (or hiding credits they can), this returns the
 * authoritative grant total and lazily heals the cache when the two have
 * drifted. The cheap read path (cache hit that already matches) does no write.
 */
export async function getCreditBalance(userId: string): Promise<number> {
  const [cachedRow, grantsTotal] = await Promise.all([
    db.query.creditBalances.findFirst({
      where: eq(creditBalances.userId, userId),
    }),
    getActiveGrantsTotal(db, userId),
  ]);

  const cached = cachedRow ? Number(cachedRow.balance) : null;
  if (cached !== grantsTotal) {
    // Drift detected (e.g. a legacy balance row with no matching grants, or a
    // partially-applied write): heal the cache to the authoritative total.
    await db.transaction((tx) => reconcileCreditBalance(tx, userId));
  }

  return grantsTotal;
}

/**
 * Spendable credit balances for many accounts at once (dashboard / admin list).
 *
 * Like {@link getCreditBalance}, the credit_grants rows are the source of truth
 * and credit_balances is only a cache. This reads the authoritative grant total
 * (SUM of remaining_amount across each account's still-active grants) directly,
 * so an account whose cached balance has drifted above its true grant total never
 * shows phantom credits on the dashboard. Accounts with no active grants default
 * to 0. (The cache still self-heals on the next single-account ledger
 * operation; this read path simply never relies on it.)
 */
export async function getCreditBalancesForUsers(userIds: string[]): Promise<Map<string, number>> {
  const map = new Map<string, number>();
  if (userIds.length === 0) return map;
  // Default every requested id to 0 so accounts with no active grants are
  // explicitly present in the map.
  for (const id of userIds) {
    map.set(id, 0);
  }
  const rows = await db
    .select({
      userId: creditGrants.userId,
      total: sql<string>`COALESCE(SUM(${creditGrants.remainingAmount}), 0)`,
    })
    .from(creditGrants)
    .where(
      and(
        inArray(creditGrants.userId, userIds),
        eq(creditGrants.status, "active"),
        sql`${creditGrants.remainingAmount} > 0`,
        sql`(${creditGrants.expiresAt} IS NULL OR ${creditGrants.expiresAt} > now())`
      )
    )
    .groupBy(creditGrants.userId);
  for (const row of rows) {
    map.set(row.userId, Number(row.total ?? 0));
  }
  return map;
}

export interface CreditGrantHistoryRow {
  id: string;
  userId: string;
  createdAt: Date;
  expiresAt: Date | null;
  originalAmount: number;
  remainingAmount: number;
  status: string;
}

/**
 * Paid credit-pack grants for the given licenses, newest-first.
 *
 * Top-up history shows PAID packs only (source_type = 'stripe_credit_pack'),
 * not free/included bundles or admin/legacy grants. Expired rows are still
 * returned (their status/expiresAt let the UI show them as expired) — this is a
 * statement of purchases, distinct from the spendable-balance reads which
 * exclude expired grants.
 */
export async function getPaidCreditGrantsForUsers(
  userIds: string[]
): Promise<CreditGrantHistoryRow[]> {
  if (userIds.length === 0) return [];
  const rows = await db
    .select({
      id: creditGrants.id,
      userId: creditGrants.userId,
      createdAt: creditGrants.createdAt,
      expiresAt: creditGrants.expiresAt,
      originalAmount: creditGrants.originalAmount,
      remainingAmount: creditGrants.remainingAmount,
      status: creditGrants.status,
    })
    .from(creditGrants)
    .where(
      and(
        inArray(creditGrants.userId, userIds),
        eq(creditGrants.sourceType, "stripe_credit_pack")
      )
    )
    .orderBy(desc(creditGrants.createdAt));

  return rows.map((row) => ({
    id: row.id,
    userId: row.userId,
    createdAt: row.createdAt,
    expiresAt: row.expiresAt,
    originalAmount: Number(row.originalAmount),
    remainingAmount: Number(row.remainingAmount),
    status: row.status,
  }));
}
