/**
 * Database Abstraction Layer
 *
 * All database operations go through Drizzle ORM (Neon/PlanetScale).
 */

import { eq, and, desc, gte, inArray, count, sql, ilike } from "drizzle-orm";
import { generateLicenseKey } from "@/lib/services/license-key";
import { db } from "@/src/db";
import {
  accountKeys,
  creditBalances,
  creditGrants,
  deviceValidations,
  emails,
  sentEmails,
  user,
  account,
  stripeProcessedEvents,
} from "@/src/db/schema";

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

export interface AccountKeyInsert {
  key: string;
  email: string;
  userId: string;
  status?: string;
  polarLicenseKeyId?: string | null;
  polarCustomerId?: string | null;
  stripeCustomerId?: string | null;
  stripeSessionId?: string | null;
}

export interface AccountKeyRow {
  id: string;
  key: string;
  email: string;
  userId: string;
  status: string;
  polarLicenseKeyId: string | null;
  polarCustomerId: string | null;
  stripeCustomerId: string | null;
  stripeSessionId: string | null;
  createdAt: Date;
}

export interface CreditBalanceRow {
  userId: string;
  balance: number;
}

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
}

export interface UserResult {
  id: string;
  email: string;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function drizzleAccountKeyToRow(row: typeof accountKeys.$inferSelect): AccountKeyRow {
  return {
    id: row.id,
    key: row.key,
    email: row.email,
    userId: row.userId,
    status: row.status,
    polarLicenseKeyId: row.polarLicenseKeyId,
    polarCustomerId: row.polarCustomerId,
    stripeCustomerId: row.stripeCustomerId,
    stripeSessionId: row.stripeSessionId,
    createdAt: row.createdAt,
  };
}

// ---------------------------------------------------------------------------
// License Keys
// ---------------------------------------------------------------------------

export async function insertAccountKey(data: AccountKeyInsert): Promise<AccountKeyRow | null> {
  const polarLicenseKeyId = data.polarLicenseKeyId ?? null;
  const insert = db
    .insert(accountKeys)
    .values({
      key: data.key,
      email: data.email,
      userId: data.userId,
      status: data.status ?? "granted",
      polarLicenseKeyId,
      polarCustomerId: data.polarCustomerId ?? null,
      stripeCustomerId: data.stripeCustomerId ?? null,
      stripeSessionId: data.stripeSessionId ?? null,
    });

  // When importing a Polar license, the unique index on polar_license_key_id
  // (idx_license_keys_polar_license_key_id) is the authoritative dedupe guard:
  // if a concurrent import already inserted a row for this Polar key, skip the
  // insert and return the existing row instead of failing or creating a
  // duplicate. The read-then-write check in importLicenseFromPolar handles the
  // common case; this makes the rare race deterministic.
  const [row] = polarLicenseKeyId
    ? await insert
        .onConflictDoNothing({ target: accountKeys.polarLicenseKeyId })
        .returning()
    : await insert.returning();

  if (!row) {
    return polarLicenseKeyId
      ? await findAccountByPolarLicenseKeyId(polarLicenseKeyId)
      : null;
  }
  return drizzleAccountKeyToRow(row);
}

export async function updateAccountKey(
  id: string,
  updates: Partial<Pick<AccountKeyInsert, "status" | "stripeCustomerId" | "email">>
): Promise<void> {
  const values: Record<string, unknown> = {};
  if (updates.status !== undefined) values.status = updates.status;
  if (updates.stripeCustomerId !== undefined) values.stripeCustomerId = updates.stripeCustomerId;
  if (updates.email !== undefined) values.email = updates.email;

  await db.update(accountKeys).set(values).where(eq(accountKeys.id, id));
}

export async function findAccountByKey(key: string): Promise<AccountKeyRow | null> {
  const row = await db.query.accountKeys.findFirst({
    where: eq(accountKeys.key, key.trim()),
  });
  return row ? drizzleAccountKeyToRow(row) : null;
}

export async function findAccountById(id: string): Promise<AccountKeyRow | null> {
  const row = await db.query.accountKeys.findFirst({
    where: eq(accountKeys.id, id),
  });
  return row ? drizzleAccountKeyToRow(row) : null;
}

export async function findAccountByStripeSession(sessionId: string): Promise<AccountKeyRow | null> {
  const row = await db.query.accountKeys.findFirst({
    where: eq(accountKeys.stripeSessionId, sessionId),
  });
  return row ? drizzleAccountKeyToRow(row) : null;
}

export async function findAccountByPolarLicenseKeyId(
  polarLicenseKeyId: string
): Promise<AccountKeyRow | null> {
  const row = await db.query.accountKeys.findFirst({
    where: eq(accountKeys.polarLicenseKeyId, polarLicenseKeyId),
  });
  return row ? drizzleAccountKeyToRow(row) : null;
}

export async function getAccountKeysByEmail(email: string): Promise<AccountKeyRow[]> {
  const rows = await db.query.accountKeys.findMany({
    where: eq(accountKeys.email, email.toLowerCase()),
    orderBy: [desc(accountKeys.createdAt)],
  });
  return rows.map(drizzleAccountKeyToRow);
}

export async function getAllAccountKeysForAdmin(limit = 1000): Promise<AccountKeyRow[]> {
  const rows = await db.query.accountKeys.findMany({
    orderBy: [desc(accountKeys.createdAt)],
    limit,
  });
  return rows.map(drizzleAccountKeyToRow);
}

export async function searchAccountKeysByEmail(
  email: string,
  limit = 1000
): Promise<Array<AccountKeyRow & { credits: number }>> {
  const rows = await db.query.accountKeys.findMany({
    where: ilike(accountKeys.email, `%${email}%`),
    orderBy: [desc(accountKeys.createdAt)],
    limit,
  });
  const licenses = rows.map(drizzleAccountKeyToRow);
  return attachPooledCredits(licenses);
}

/**
 * Mint a fresh internal-bundle license for an email: generate a collision-free
 * key, ensure the user exists, insert the license as "granted", and grant the
 * standard 5,000-credit internal bundle.
 *
 * This is the single source of truth for the internal mint flow shared by the
 * grant-license and licenses-for-email endpoints. Callers decide *whether* to
 * mint (e.g. only when no license exists); this just performs the mint and
 * returns the new row. Throws on any failure.
 */
export async function provisionAccountKeyForEmail(email: string): Promise<AccountKeyRow> {
  const normalizedEmail = email.toLowerCase().trim();

  // Generate a unique license key with collision check
  let key = "";
  for (let i = 0; i < 5; i++) {
    key = generateLicenseKey();
    const collision = await findAccountByKey(key);
    if (!collision) break;
    if (i === 4) {
      throw new Error("Failed to generate unique license key");
    }
  }

  const name = normalizedEmail.split("@")[0];
  const userResult = await getOrCreateUser(normalizedEmail, { name });
  if (!userResult) {
    throw new Error("Failed to create user");
  }

  const license = await insertAccountKey({
    key,
    email: normalizedEmail,
    userId: userResult.id,
    status: "granted",
  });
  if (!license) {
    throw new Error("Failed to insert license key");
  }

  await grantCreditLot({
    userId: license.userId,
    amount: 5000,
    sourceType: "internal_bundle",
    sourceId: license.id,
  });

  return license;
}

// ---------------------------------------------------------------------------
// Credit Balances and Grants
// ---------------------------------------------------------------------------

// A handle that can run queries: either the pooled `db` connection or a
// transaction handle. Used so balance reads can join the caller's transaction
// instead of opening a second connection (which sees a stale snapshot and ties
// up the pool while row locks are held).
type DbExecutor = typeof db | Parameters<Parameters<typeof db.transaction>[0]>[0];

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

async function grantCreditLotInTransaction(
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

export async function refundCreditGrant(
  data: CreditGrantRefund
): Promise<{ status: "processed" | "duplicate"; refundedAmount: number }> {
  return db.transaction(async (tx) => {
    // Resolve the refunded purchase by provenance. We deliberately do NOT filter
    // on remaining_amount > 0: a fully-consumed pack still has to be clawed back,
    // otherwise spending the pack first (so a refund can't hand back unused paid
    // credits — see spendCreditGrantsByProvenance / #872) would let a refund of a
    // fully-spent pack reclaim nothing, leaving the user with the paid usage for
    // free PLUS the cash refund (the inverse money-loss bug).
    const target = await tx.execute<{
      id: string;
      user_id: string;
      original_amount: string;
      refunded_amount: string;
    }>(sql`
      SELECT id, user_id, original_amount, refunded_amount
      FROM credit_grants
      WHERE source_type = ${data.sourceType}
        AND source_id = ${data.sourceId}
      FOR UPDATE
    `);

    const grant = target.rows[0];
    if (!grant) {
      return { status: "duplicate", refundedAmount: 0 };
    }

    // The amount this refund reclaims is the grant's full purchased value that
    // has not already been refunded. A refunded grant (refunded_amount already
    // covers original_amount) is a no-op / duplicate webhook delivery.
    const originalAmount = Number(grant.original_amount);
    const alreadyRefunded = Number(grant.refunded_amount);
    const clawback = originalAmount - alreadyRefunded;
    if (clawback <= 0) {
      return { status: "duplicate", refundedAmount: 0 };
    }

    // A full refund must remove the FULL purchased value from the license's
    // spendable balance, regardless of which grant happened to be spent for the
    // matching usage. Clamped at the license's current active total so a user who
    // already burned the credits doesn't go negative, but no value escapes:
    //   spend order + refund clawback are reconciled against the SAME running
    //   total, so neither bundle-first nor pack-first ordering can leak money.
    let toClawback = Math.min(
      clawback,
      await getActiveGrantsTotal(tx, grant.user_id)
    );

    // Draw the clawback down from the license's active, unexpired grants,
    // starting with the refunded grant itself, then the same oldest-first order
    // used when spending (soonest-to-expire first). The clawback total is
    // clamped at the license's current active balance, and both spend and
    // clawback reconcile against that same running total, so no ordering can
    // leak money. Expired grants are skipped: they no longer back any spendable
    // balance, so clawing them back would remove value that was never available.
    const drawdown = await tx.execute<{ id: string; remaining_amount: string }>(sql`
      SELECT id, remaining_amount
      FROM credit_grants
      WHERE user_id = ${grant.user_id}
        AND remaining_amount > 0
        AND status = 'active'
        AND ${ACTIVE_GRANT_EXPIRY}
      ORDER BY
        CASE WHEN id = ${grant.id} THEN 0 ELSE 1 END,
        expires_at ASC,
        created_at ASC,
        id
      FOR UPDATE
    `);

    for (const row of drawdown.rows) {
      if (toClawback <= 0) break;
      const rowRemaining = Number(row.remaining_amount);
      const deduction = Math.min(rowRemaining, toClawback);
      if (deduction <= 0) continue;
      const newRemaining = rowRemaining - deduction;
      await tx
        .update(creditGrants)
        .set({
          remainingAmount: newRemaining.toString(),
          status: newRemaining === 0 ? "spent" : "active",
          updatedAt: new Date(),
        })
        .where(eq(creditGrants.id, row.id));
      toClawback -= deduction;
    }

    // Mark the refunded grant as refunded and record the full reclaimed value on
    // it (even when the credits were physically drawn from other grants), so the
    // grant's lifetime is correct and a duplicate refund webhook is a no-op.
    await tx
      .update(creditGrants)
      .set({
        remainingAmount: "0",
        refundedAmount: (alreadyRefunded + clawback).toString(),
        status: "refunded",
        updatedAt: new Date(),
      })
      .where(eq(creditGrants.id, grant.id));

    // Reconcile the cache to the authoritative grant total, healing any drift.
    await reconcileCreditBalance(tx, grant.user_id);
    return {
      status: "processed",
      refundedAmount: clawback,
    };
  });
}

export async function hasProcessedStripeObject(
  stripeObjectId: string
): Promise<boolean> {
  const row = await db.query.stripeProcessedEvents.findFirst({
    where: eq(stripeProcessedEvents.stripeObjectId, stripeObjectId),
    columns: { eventId: true },
  });

  return Boolean(row);
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
      -- provenance (paid-pack-first) order; refund safety is now preserved a
      -- different way — refundCreditGrant clamps every clawback at the license's
      -- current active balance and reconciles against the same running total
      -- (#872 / spec §6), so neither spend nor clawback ordering can leak money
      -- regardless of which grant a given unit of usage drew from. Expired
      -- grants are excluded above and never spent.
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
          status: newRemaining === 0 ? "spent" : "active",
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
 */
export async function deductCreditBalance(userId: string, amount: number): Promise<number> {
  const result = await spendCreditGrantsByProvenance(userId, amount);
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

/**
 * Attach each license's pooled account balance. Credits are pooled per account,
 * so balances are resolved by the licenses' distinct owning users and every
 * license of an account reports that one shared balance. Returns [] for empty
 * input without issuing a query.
 */
async function attachPooledCredits<T extends { userId: string }>(
  licenses: T[]
): Promise<Array<T & { credits: number }>> {
  if (licenses.length === 0) return [];
  const balanceMap = await getCreditBalancesForUsers(
    Array.from(new Set(licenses.map((l) => l.userId)))
  );
  return licenses.map((license) => ({
    ...license,
    credits: balanceMap.get(license.userId) || 0,
  }));
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

export async function getAllAccountKeysWithCreditsForAdmin(limit = 1000): Promise<
  Array<AccountKeyRow & { credits: number }>
> {
  const licenses = await getAllAccountKeysForAdmin(limit);
  return attachPooledCredits(licenses);
}

/**
 * Fetch every license (with credit balance) belonging to the given users,
 * newest-first. Used by the admin list so that a customer's full license set
 * is shown even when a search only matched a subset of their licenses.
 */
export async function getAccountKeysWithCreditsForUserIds(
  userIds: string[]
): Promise<Array<AccountKeyRow & { credits: number }>> {
  if (userIds.length === 0) return [];
  const rows = await db.query.accountKeys.findMany({
    where: inArray(accountKeys.userId, userIds),
    orderBy: [desc(accountKeys.createdAt)],
  });
  const licenses = rows.map(drizzleAccountKeyToRow);
  return attachPooledCredits(licenses);
}

// ---------------------------------------------------------------------------
// Device Validations
// ---------------------------------------------------------------------------

export async function upsertDeviceValidation(
  licenseKeyId: string,
  deviceId: string,
  deviceName?: string
): Promise<void> {
  await db
    .insert(deviceValidations)
    .values({
      licenseKeyId,
      deviceId,
      deviceName: deviceName || null,
      lastValidatedAt: new Date(),
    })
    .onConflictDoUpdate({
      target: [deviceValidations.licenseKeyId, deviceValidations.deviceId],
      set: {
        deviceName: deviceName || null,
        lastValidatedAt: new Date(),
      },
    });
}

export async function getDevicesForLicense(
  licenseKeyId: string,
  sinceDays?: number
): Promise<
  Array<{
    deviceId: string;
    deviceName: string | null;
    createdAt: Date;
    lastValidatedAt: Date;
  }>
> {
  const conditions = [eq(deviceValidations.licenseKeyId, licenseKeyId)];
  if (sinceDays) {
    const since = new Date();
    since.setDate(since.getDate() - sinceDays);
    conditions.push(gte(deviceValidations.lastValidatedAt, since));
  }

  const rows = await db
    .select({
      deviceId: deviceValidations.deviceId,
      deviceName: deviceValidations.deviceName,
      createdAt: deviceValidations.createdAt,
      lastValidatedAt: deviceValidations.lastValidatedAt,
    })
    .from(deviceValidations)
    .where(and(...conditions))
    .orderBy(desc(deviceValidations.lastValidatedAt));

  return rows;
}

export async function getDeviceCountsPerLicense(sinceDays?: number): Promise<
  Array<{
    licenseKeyId: string;
    email: string;
    licenseKey: string;
    deviceCount: number;
  }>
> {
  const conditions = [];
  if (sinceDays) {
    const since = new Date();
    since.setDate(since.getDate() - sinceDays);
    conditions.push(gte(deviceValidations.lastValidatedAt, since));
  }

  const rows = await db
    .select({
      licenseKeyId: accountKeys.id,
      email: accountKeys.email,
      licenseKey: accountKeys.key,
      deviceCount: count(deviceValidations.id),
    })
    .from(deviceValidations)
    .innerJoin(accountKeys, eq(deviceValidations.licenseKeyId, accountKeys.id))
    .where(conditions.length > 0 ? and(...conditions) : undefined)
    .groupBy(accountKeys.id, accountKeys.email, accountKeys.key)
    .orderBy(sql`count(${deviceValidations.id}) desc`);

  return rows.map((r) => ({ ...r, deviceCount: Number(r.deviceCount) }));
}

// ---------------------------------------------------------------------------
// Emails
// ---------------------------------------------------------------------------

export async function upsertEmail(data: {
  email: string;
  source?: string | null;
  ipAddress?: string | null;
  userAgent?: string | null;
  country?: string | null;
}): Promise<void> {
  await db
    .insert(emails)
    .values({
      email: data.email,
      source: data.source ?? null,
      ipAddress: data.ipAddress ?? null,
      userAgent: data.userAgent ?? null,
      country: data.country ?? null,
    })
    .onConflictDoNothing({ target: emails.email });
}

// ---------------------------------------------------------------------------
// Sent emails (outbound audit log)
// ---------------------------------------------------------------------------

export async function logSentEmail(data: {
  recipient: string;
  emailType: string;
  subject?: string | null;
  providerMessageId?: string | null;
  status: "sent" | "failed";
  errorMessage?: string | null;
}): Promise<void> {
  await db.insert(sentEmails).values({
    recipient: data.recipient,
    emailType: data.emailType,
    subject: data.subject ?? null,
    providerMessageId: data.providerMessageId ?? null,
    status: data.status,
    errorMessage: data.errorMessage ?? null,
  });
}

// ---------------------------------------------------------------------------
// Users
// ---------------------------------------------------------------------------

export async function getOrCreateUser(
  email: string,
  metadata?: { name?: string; stripeCustomerId?: string; polarCustomerId?: string }
): Promise<UserResult | null> {
  // Check if user exists
  const existing = await db.query.user.findFirst({
    where: eq(user.email, email.toLowerCase().trim()),
  });
  if (existing) return { id: existing.id, email: existing.email };

  // Create via Better Auth's user table.
  // emailVerified stays false: this address comes from a Stripe/Polar checkout
  // or an admin/import grant, so the mailbox owner has never proven control of
  // it. The magic-link plugin flips emailVerified to true on the first
  // successful sign-in, which is the only point where ownership is verified.
  const userId = crypto.randomUUID();
  const [newUser] = await db
    .insert(user)
    .values({
      id: userId,
      name: metadata?.name ?? email.split("@")[0],
      email: email.toLowerCase().trim(),
      emailVerified: false,
    })
    .returning();
  if (!newUser) return null;

  // Also insert an account record so the user can sign in via magic link
  await db.insert(account).values({
    id: `magic-link-${userId}`,
    accountId: userId,
    providerId: "magic-link",
    userId: userId,
    createdAt: new Date(),
    updatedAt: new Date(),
  });

  return { id: newUser.id, email: newUser.email };
}

export async function getUserById(id: string): Promise<UserResult | null> {
  const existing = await db.query.user.findFirst({
    where: eq(user.id, id),
  });
  return existing ? { id: existing.id, email: existing.email } : null;
}

export async function getUserByEmail(email: string): Promise<UserResult | null> {
  const existing = await db.query.user.findFirst({
    where: eq(user.email, email.toLowerCase().trim()),
  });
  return existing ? { id: existing.id, email: existing.email } : null;
}

export async function getUsersByIds(ids: string[]): Promise<Map<string, UserResult>> {
  if (ids.length === 0) return new Map();
  const rows = await db.query.user.findMany({
    where: inArray(user.id, ids),
  });
  const map = new Map<string, UserResult>();
  for (const row of rows) {
    map.set(row.id, { id: row.id, email: row.email });
  }
  return map;
}

/**
 * Move a customer to a new email. Updates the user's canonical email AND every
 * license_keys.email row owned by that user, atomically, so a customer is never
 * split across two emails. The caller should pre-check uniqueness; the
 * user.email UNIQUE constraint is the final guard (a colliding write throws
 * Postgres error code 23505, which the caller maps to a conflict).
 */
export async function updateCustomerEmail(
  userId: string,
  newEmail: string
): Promise<void> {
  const email = newEmail.toLowerCase().trim();
  await db.transaction(async (tx) => {
    // Reset emailVerified: the new address has not been proven to belong to
    // the account holder (matches the false default for admin/import-created
    // users). The magic-link plugin re-verifies on the next sign-in.
    await tx
      .update(user)
      .set({ email, emailVerified: false, updatedAt: new Date() })
      .where(eq(user.id, userId));
    await tx
      .update(accountKeys)
      .set({ email })
      .where(eq(accountKeys.userId, userId));
  });
}
