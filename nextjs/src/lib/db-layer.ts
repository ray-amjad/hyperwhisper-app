/**
 * Database Abstraction Layer
 *
 * All database operations go through Drizzle ORM (Neon/PlanetScale).
 */

import { eq, ne, and, or, asc, desc, gte, inArray, count, countDistinct, sql, ilike } from "drizzle-orm";
import { generateLicenseKey } from "@/lib/services/license-key";
import {
  ACCOUNT_KEY_DELETED_STATUS,
  MAX_LIVE_ACCOUNT_KEYS,
} from "@/lib/account-key-limits";
import { db } from "@/src/db";
import {
  accountKeys,
  deviceValidations,
  emails,
  sentEmails,
  user,
  account,
  session,
} from "@/src/db/schema";
import {
  getCreditBalancesForUsers,
  grantCreditLotInTransaction,
  type CreditGrantSourceType,
  type DbExecutor,
} from "./credit-ledger";

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

export interface AccountKeyInsert {
  key: string;
  email: string;
  userId: string;
  status?: string;
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

export async function insertAccountKey(
  data: AccountKeyInsert,
  executor: DbExecutor = db
): Promise<AccountKeyRow | null> {
  const [row] = await executor
    .insert(accountKeys)
    .values({
      key: data.key,
      email: data.email,
      userId: data.userId,
      status: data.status ?? "granted",
      stripeCustomerId: data.stripeCustomerId ?? null,
      stripeSessionId: data.stripeSessionId ?? null,
    })
    .returning();

  if (!row) {
    return null;
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

export interface RevokeWebAccessOptions {
  /**
   * The operator performing the change, when there is one. Their own sessions
   * are left alone — see below.
   */
  actingUserId?: string;
  /**
   * One session row to leave standing: the browser the user is acting from
   * right now. Used by `rotateAccountKey()`, where the holder of the account
   * is the one asking, so their own tab stays signed in while every other
   * session (including one a leaked key minted) is swept.
   */
  keepSessionId?: string;
  /**
   * Join an existing transaction. Pass this whenever the caller is already
   * writing the row that made the access invalid, so the two cannot diverge.
   */
  tx?: DbExecutor;
}

/**
 * THE primitive for "this user's web access is no longer valid — kill it".
 *
 * Every path that invalidates web access calls this one function. Do not add a
 * second mechanism; extend this one. Today's callers:
 *   - `revokeAccountKey()`     — a license key was revoked
 *   - `updateCustomerEmail()`  — the account moved to a different address
 *   - `rotateAccountKey()`     — the owner replaced a key (keeps their own tab)
 *   - `deleteAccountKey()`     — the owner deleted a key (keeps their own tab)
 *
 * Why session rows have to go at all: web sessions last 90 days and roll
 * forward on use, and a session minted by `/sign-in/license-key` outlives the
 * key that minted it. Whoever holds a leaked key would otherwise keep the
 * portal (email, every other key on the account, credit balances, the Stripe
 * billing portal link) for up to 90 days after the key is killed. Deleting the
 * rows makes revocation take effect on the very next request.
 *
 * Sessions are not attributable to the key that created them, so this drops ALL
 * of the user's sessions, not just license-key ones. A legitimate owner of
 * several keys is signed out of the web portal once and signs back in with a
 * magic link or a key they still hold; the alternative — skipping the cleanup
 * when the account has another valid key — leaves the hole open in exactly the
 * multi-key case an attacker benefits from most. Desktop apps are unaffected:
 * `/api/license/*` authenticates on the license key, never on a session.
 *
 * `actingUserId` exists because comped keys are routinely minted against an
 * admin's own email. An admin refunding one of those would delete their own
 * session mid-mutation and sign themselves out. The rule lives here, not at the
 * call site, so every future caller inherits it: the access-invalidating write
 * always happens; only the session sweep is skipped, and only for the one
 * account known to be legitimately in use right now.
 *
 * NOTE for whoever adds Redis `secondaryStorage` to Better Auth: this raw
 * DELETE is the place to update. Better Auth's
 * `internalAdapter.deleteUserSessions(userId)` would also purge secondary
 * storage, the `active-sessions-${userId}` index, and run delete hooks — today
 * those are all no-ops because sessions live only in Postgres, and using it
 * here would mean giving up the transaction (it cannot join a Drizzle `tx`),
 * which is the thing that actually made revocation reliable. Atomicity now,
 * secondary storage later; this comment is the handoff.
 */
export async function revokeWebAccess(
  userId: string,
  options: RevokeWebAccessOptions = {},
): Promise<void> {
  if (options.actingUserId === userId) return;

  const runner = options.tx ?? db;

  await runner
    .delete(session)
    .where(
      options.keepSessionId
        ? and(eq(session.userId, userId), ne(session.id, options.keepSessionId))
        : eq(session.userId, userId),
    );
}

/** Why `rotateAccountKey()` refused, or what it rotated. */
export type RotateAccountKeyResult =
  | {
      status: "rotated";
      license: AccountKeyRow;
      /** The key the row held before. Only for invalidating caches keyed by it. */
      previousKey: string;
      /** Device rows removed with the old key. */
      devicesCleared: number;
    }
  /** No such row, or it is not the caller's. The two are not told apart. */
  | { status: "not_found" }
  /** The row is revoked / refunded / disabled: only a granted key rotates. */
  | { status: "not_granted" }
  /**
   * The row no longer holds the key the caller saw: another rotation got there
   * first. The caller reloads and sees the current key.
   */
  | { status: "stale" };

/**
 * Replace an Account Key's value with a fresh one, in place.
 *
 * The row keeps its id, so everything that hangs off the id stays with the
 * owner: credits (pooled per user), credit grants, the Stripe customer and
 * session, the Polar ids, purchase history. Only the string changes, so the
 * old key stops matching `findAccountByKey` — and with it `/api/license/*`,
 * `/api/account/*` and the license-key sign-in — on the next request.
 *
 * In ONE transaction, under a row lock:
 *   1. ownership: the row's user AND email are the caller's (the dashboard
 *      lists keys by email; both move together in `updateCustomerEmail`);
 *   2. status: only a `granted` key rotates — a revoked or refunded key is
 *      already dead, and rotating it would hand out a fresh string for it;
 *   3. `expectedKey`: the caller names the key it is replacing. Two rotations
 *      at once serialise on the lock; the second finds the string changed and
 *      gets `stale`, so neither caller is left showing a key that no longer
 *      works;
 *   4. the device rows of the old key are cleared (a leaked key's machines
 *      drop off the admin's device list; a real device re-registers on its
 *      next validation with the new key);
 *   5. every web session of the user except `keepSessionId` is deleted
 *      (`revokeWebAccess`): a session minted by the old key through
 *      `/sign-in/license-key` must not outlive it, and would otherwise show
 *      its holder the NEW key on this very dashboard.
 *
 * Never logs a key.
 */
export async function rotateAccountKey(input: {
  id: string;
  userId: string;
  email: string;
  expectedKey: string;
  keepSessionId?: string;
}): Promise<RotateAccountKeyResult> {
  const email = input.email.toLowerCase().trim();

  return db.transaction(async (tx) => {
    const [row] = await tx
      .select()
      .from(accountKeys)
      .where(eq(accountKeys.id, input.id))
      .for("update");

    if (!row || row.userId !== input.userId || row.email !== email) {
      return { status: "not_found" } as const;
    }
    if (row.status !== "granted") return { status: "not_granted" } as const;
    if (row.key !== input.expectedKey.trim()) return { status: "stale" } as const;

    // The same generator and collision check as a mint; `idx_account_keys_key`
    // backs it. The row still holds the old key, so a repeat is a collision.
    const key = await generateUnusedAccountKey(tx);

    const [updated] = await tx
      .update(accountKeys)
      .set({ key })
      .where(eq(accountKeys.id, row.id))
      .returning();

    const cleared = await tx
      .delete(deviceValidations)
      .where(eq(deviceValidations.licenseKeyId, row.id))
      .returning({ id: deviceValidations.id });

    await revokeWebAccess(row.userId, { tx, keepSessionId: input.keepSessionId });

    return {
      status: "rotated",
      license: drizzleAccountKeyToRow(updated),
      previousKey: row.key,
      devicesCleared: cleared.length,
    } as const;
  });
}

// First key of the two-int advisory lock that serialises one user's Account
// Key issue and delete (the second is hashtext(user id)). Any constant works;
// it only keeps these locks apart from GRANT_LICENSE_LOCK_CLASS and any other.
const MANAGE_KEYS_LOCK_CLASS = 2634;

/**
 * Take the per-user lock that issue and delete share, inside `tx`. Released
 * at COMMIT/ROLLBACK. Row locks alone are not enough: under READ COMMITTED a
 * statement that waited on `FOR UPDATE` still does not see rows another
 * transaction inserted meanwhile, so two concurrent issues would both count
 * the same keys and pass the cap. After this lock, the next statement takes a
 * fresh snapshot and sees what the previous holder committed.
 */
async function lockUserKeys(tx: DbExecutor, userId: string): Promise<void> {
  await tx.execute(
    sql`SELECT pg_advisory_xact_lock(${MANAGE_KEYS_LOCK_CLASS}, hashtext(${userId}))`,
  );
}

/**
 * The caller's live (`granted`) keys, newest first, row-locked. "The caller's"
 * is the same rule rotation applies: the row's user AND email are the
 * caller's (the dashboard lists keys by email).
 */
async function lockLiveKeys(
  tx: DbExecutor,
  userId: string,
  email: string,
): Promise<Array<typeof accountKeys.$inferSelect>> {
  return tx
    .select()
    .from(accountKeys)
    .where(
      and(
        eq(accountKeys.userId, userId),
        eq(accountKeys.email, email),
        eq(accountKeys.status, "granted"),
      ),
    )
    .orderBy(desc(accountKeys.createdAt), asc(accountKeys.id))
    .for("update");
}

/** What `issueAccountKey()` did, or why it refused. */
export type IssueAccountKeyResult =
  | { status: "issued"; license: AccountKeyRow }
  /**
   * The caller holds no granted key. Issuing is for people who already have
   * one; it must never be a way to get a first key (that is a purchase or
   * the ACS claim).
   */
  | { status: "no_live_key" }
  /** The caller already holds `MAX_LIVE_ACCOUNT_KEYS` granted keys. */
  | { status: "cap_reached" };

/**
 * Give the caller one more Account Key on the same account.
 *
 * The new row belongs to the same user, so it shares everything that is kept
 * per user: the credit pool (balances and grants are by `user_id`), the
 * `credit_pool_id` the licence API derives from the user id, and the web
 * account. Nothing is granted with it: no credits, no bundle.
 *
 * Copied from the caller's newest live key: `stripe_customer_id` (the licence
 * API returns it, and top-up checkouts run under the key row's cached
 * customer, so the account keeps one Stripe customer). NOT copied:
 * `stripe_session_id` (unique, and it names the purchase that minted the
 * original row; refunds and the admin refund find that row by it), and the
 * Polar ids (`polar_license_key_id` is unique, and both name an imported
 * Polar licence, which this row is not).
 *
 * In ONE transaction under the per-user lock: the caller must hold at least
 * one granted key, and fewer than `MAX_LIVE_ACCOUNT_KEYS`.
 *
 * Never logs a key.
 */
export async function issueAccountKey(input: {
  userId: string;
  email: string;
}): Promise<IssueAccountKeyResult> {
  const email = input.email.toLowerCase().trim();

  return db.transaction(async (tx) => {
    await lockUserKeys(tx, input.userId);

    const live = await lockLiveKeys(tx, input.userId, email);

    if (live.length === 0) return { status: "no_live_key" } as const;
    if (live.length >= MAX_LIVE_ACCOUNT_KEYS) {
      return { status: "cap_reached" } as const;
    }

    const key = await generateUnusedAccountKey(tx);
    const stripeCustomerId =
      live.find((row) => row.stripeCustomerId)?.stripeCustomerId ?? null;

    const license = await insertAccountKey(
      {
        key,
        email,
        userId: input.userId,
        status: "granted",
        stripeCustomerId,
      },
      tx,
    );

    if (!license) throw new Error("Failed to insert issued Account Key");

    return { status: "issued", license } as const;
  });
}

/** What `deleteAccountKey()` did, or why it refused. */
export type DeleteAccountKeyResult =
  | {
      status: "deleted";
      /** The row as it was before the delete (status `granted`). */
      license: AccountKeyRow;
      /** Device rows removed with the key. */
      devicesCleared: number;
    }
  /** No such row, or it is not the caller's. The two are not told apart. */
  | { status: "not_found" }
  /** The row is already deleted, revoked or refunded: only a live key deletes. */
  | { status: "not_granted" }
  /** The row no longer holds the key the caller saw (rotated meanwhile). */
  | { status: "stale" }
  /** It is the caller's last live key. An account always keeps one. */
  | { status: "last_key" };

/**
 * Delete one of the caller's Account Keys: it stops working everywhere, like
 * the old key of a rotation, and leaves the dashboard.
 *
 * A status change (`ACCOUNT_KEY_DELETED_STATUS`), not a row delete: the row is
 * what purchase history, the admin refund (by `stripe_session_id`), a top-up
 * checkout opened on this key (by `account_key_id`) and credit grants
 * (`source_id` = row id for the internal bundle) point at. Every licence
 * reader already refuses a non-`granted` row, so the key is dead on
 * `/api/license/*`, `/api/account/*` and the license-key sign-in on the next
 * request; its string stays reserved by `idx_account_keys_key`, so it is
 * never handed out again.
 *
 * In ONE transaction under the per-user lock, then the row lock:
 *   1. ownership (user AND email), as rotation;
 *   2. only a `granted` key;
 *   3. `expectedKey`: the key the caller saw, so a key rotated in another tab
 *      is not deleted unseen;
 *   4. at least one OTHER granted key remains. The per-user lock makes this
 *      race-safe: two tabs deleting the last two keys serialise, and the
 *      second sees the first's delete and gets `last_key`;
 *   5. the key's device rows are cleared, and every web session of the user
 *      except `keepSessionId` is deleted (`revokeWebAccess`): a session minted
 *      with this key must not outlive it.
 *
 * Never logs a key.
 */
export async function deleteAccountKey(input: {
  id: string;
  userId: string;
  email: string;
  expectedKey: string;
  keepSessionId?: string;
}): Promise<DeleteAccountKeyResult> {
  const email = input.email.toLowerCase().trim();

  return db.transaction(async (tx) => {
    await lockUserKeys(tx, input.userId);

    const [row] = await tx
      .select()
      .from(accountKeys)
      .where(eq(accountKeys.id, input.id))
      .for("update");

    if (!row || row.userId !== input.userId || row.email !== email) {
      return { status: "not_found" } as const;
    }
    if (row.status !== "granted") return { status: "not_granted" } as const;
    if (row.key !== input.expectedKey.trim())
      return { status: "stale" } as const;

    const live = await lockLiveKeys(tx, input.userId, email);

    if (!live.some((other) => other.id !== row.id)) {
      return { status: "last_key" } as const;
    }

    await tx
      .update(accountKeys)
      .set({ status: ACCOUNT_KEY_DELETED_STATUS })
      .where(eq(accountKeys.id, row.id));

    const cleared = await tx
      .delete(deviceValidations)
      .where(eq(deviceValidations.licenseKeyId, row.id))
      .returning({ id: deviceValidations.id });

    await revokeWebAccess(row.userId, {
      tx,
      keepSessionId: input.keepSessionId,
    });

    return {
      status: "deleted",
      license: drizzleAccountKeyToRow(row),
      devicesCleared: cleared.length,
    } as const;
  });
}

/**
 * Revoke a license key and sign its owner out of the web portal, atomically.
 *
 * The two writes are one transaction on purpose. Ordered as separate
 * statements they were fail-open, and fail-open in the worst possible shape:
 * the status write armed `stripe-webhook.ts`'s `if (license.status ===
 * "revoked") return;` guard (that branch now also sweeps sessions via
 * `revokeWebAccess`), which sits ABOVE this call, so a failed session delete
 * could never be retried — the first half of the write permanently blocked
 * its own second half. The webhook route answered 200 at the time, so Stripe
 * never retried either, and the key ended up revoked with live 90-day
 * sessions and no path to clean up. Either both land or neither does.
 *
 * A key its owner deleted (`deleteAccountKey`) stays `deleted`: it is already
 * dead everywhere and its sessions were swept when it was deleted, so a later
 * admin Refund & Revoke on it changes nothing here (the refund itself still
 * happens in the caller). Relabelling it `revoked` would lose the owner's
 * delete from the audit trail and bring the row back onto the owner's
 * dashboard, which hides only `deleted` rows. The status test is in the
 * UPDATE, so a delete committing first is seen even under a race. Returns
 * whether this call revoked the key.
 */
export async function revokeAccountKey(
  id: string,
  userId: string,
  options: { actingUserId?: string } = {},
): Promise<{ revoked: boolean }> {
  return db.transaction(async (tx) => {
    const updated = await tx
      .update(accountKeys)
      .set({ status: "revoked" })
      .where(
        and(
          eq(accountKeys.id, id),
          ne(accountKeys.status, ACCOUNT_KEY_DELETED_STATUS),
        ),
      )
      .returning({ id: accountKeys.id });

    if (updated.length === 0) return { revoked: false };

    await revokeWebAccess(userId, { ...options, tx });

    return { revoked: true };
  });
}

/**
 * The key row holding this exact key string, or null.
 *
 * `lockForShare` makes it `SELECT … FOR SHARE`. That read waits for any
 * transaction holding the row's lock (a rotation's `FOR UPDATE`, a
 * revocation's `UPDATE`) to commit, then re-checks the row as committed: after
 * a rotation the old string matches nothing, and after a revocation the status
 * shows it. The license-key sign-in needs that after `createSession` (see
 * `auth-license-key-plugin.ts`); a plain read under READ COMMITTED would see
 * the row as it was before the uncommitted change.
 */
export async function findAccountByKey(
  key: string,
  executor: DbExecutor = db,
  options: { lockForShare?: boolean } = {},
): Promise<AccountKeyRow | null> {
  if (options.lockForShare) {
    const [locked] = await executor
      .select()
      .from(accountKeys)
      .where(eq(accountKeys.key, key.trim()))
      .limit(1)
      .for("share");
    return locked ? drizzleAccountKeyToRow(locked) : null;
  }
  const row = await executor.query.accountKeys.findFirst({
    where: eq(accountKeys.key, key.trim()),
  });
  return row ? drizzleAccountKeyToRow(row) : null;
}

/**
 * A fresh key string that no row holds yet: the generator plus a collision
 * check, five tries. `idx_account_keys_key` is the real guarantee; this keeps a
 * collision from failing the insert. Used by every mint and the rotation that
 * run in `db-layer` (the webhook's and the admin grant's loops are their own,
 * tested through their mocks).
 */
export async function generateUnusedAccountKey(
  executor: DbExecutor = db,
): Promise<string> {
  for (let i = 0; i < 5; i++) {
    const key = generateLicenseKey();
    if (!(await findAccountByKey(key, executor))) return key;
  }
  throw new Error("Failed to generate unique license key");
}

/**
 * Every key row billed to this Stripe customer, newest first. A top-up
 * checkout always runs under the key row's cached customer, so this finds the
 * row again when the key string it recorded has since been rotated.
 */
export async function findAccountsByStripeCustomer(
  stripeCustomerId: string,
): Promise<AccountKeyRow[]> {
  const rows = await db.query.accountKeys.findMany({
    where: eq(accountKeys.stripeCustomerId, stripeCustomerId),
    orderBy: [desc(accountKeys.createdAt)],
  });
  return rows.map(drizzleAccountKeyToRow);
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

export async function getAccountKeysByEmail(
  email: string,
  executor: DbExecutor = db
): Promise<AccountKeyRow[]> {
  const rows = await executor.query.accountKeys.findMany({
    where: eq(accountKeys.email, email.toLowerCase()),
    orderBy: [desc(accountKeys.createdAt)],
  });
  return rows.map(drizzleAccountKeyToRow);
}

/**
 * Distinct normalized emails that hold at least one *granted* Account Key (from
 * the internal bundle or a paid purchase). Powers the ACS admin backfill, which
 * reconciles its claim flag against emails that already have a key. Read-only.
 */
export async function getGrantedEmails(): Promise<string[]> {
  const rows = await db
    .selectDistinct({ email: accountKeys.email })
    .from(accountKeys)
    .where(eq(accountKeys.status, "granted"));
  return rows
    .map((r) => r.email.toLowerCase().trim())
    .filter((email) => email.length > 0);
}

// Credits granted with each internal-bundle Account Key mint. $5 at the
// 1000-credits-per-dollar rate. This is the ACS member perk; only brand-new
// emails receive it (an email that already has a granted key gets no top-up).
// The perk ran at $5, was bumped to $10 on 2026-07-02, and came back to $5 on
// 2026-08-13. ACS shows each member what they truly got, keyed off their claim
// date, so keep its `creditDollarsForClaim` eras in step with any change here.
const INTERNAL_BUNDLE_CREDITS = 5000;

// First key of the two-int advisory lock that serialises internal mints per
// email (the second is hashtext(email)). Any constant works; it only keeps
// these locks apart from any other advisory lock on the database.
const GRANT_LICENSE_LOCK_CLASS = 2481;

/**
 * Give an email its one granted license. If the email already holds a granted
 * key (internal or paid), that key is returned and NOTHING is minted or
 * granted. Otherwise mint a fresh internal-bundle license: generate a
 * collision-free key, ensure the user exists, insert the license as "granted",
 * and grant the standard 5,000-credit ($5) internal bundle.
 *
 * This is the single source of truth for the internal mint flow. Its only
 * caller is the grant-license endpoint (driven by the ACS claim flow), which
 * mints only when the email has no granted key. licenses-for-email is read-only
 * and never mints. Throws on any failure.
 *
 * CONCURRENCY (#2481): the caller's "no granted key yet" read is outside any
 * lock, so several calls for one email (a double-clicked claim, a retry, an
 * overlapping backfill) can all reach here. The whole mint runs in ONE
 * transaction that first takes a per-email advisory lock and then re-reads the
 * granted keys: the first caller mints, and every caller queued behind it
 * finds that key and gets it back with no new key and no second credit grant.
 * The lock also serialises the user insert, so a brand-new email no longer
 * trips `user_email_unique` for the losers. Every Worker isolate shares it,
 * because it lives in Postgres.
 */
export async function provisionAccountKeyForEmail(email: string): Promise<AccountKeyRow> {
  const normalizedEmail = email.toLowerCase().trim();

  return db.transaction(async (tx) => {
    // Released at COMMIT/ROLLBACK, so it is safe behind a transaction pooler.
    await tx.execute(
      sql`SELECT pg_advisory_xact_lock(${GRANT_LICENSE_LOCK_CLASS}, hashtext(${normalizedEmail}))`
    );

    // Newest first, the same rule the route's unlocked fast path applies.
    const granted = (await getAccountKeysByEmail(normalizedEmail, tx)).find(
      (l) => l.status === "granted"
    );
    if (granted) return granted;

    const key = await generateUnusedAccountKey(tx);

    const name = normalizedEmail.split("@")[0];
    const userResult = await getOrCreateUser(normalizedEmail, { name }, tx);
    if (!userResult) {
      throw new Error("Failed to create user");
    }

    const license = await insertAccountKey(
      {
        key,
        email: normalizedEmail,
        userId: userResult.id,
        status: "granted",
      },
      tx
    );
    if (!license) {
      throw new Error("Failed to insert license key");
    }

    await grantCreditLotInTransaction(tx, {
      userId: license.userId,
      amount: INTERNAL_BUNDLE_CREDITS,
      sourceType: "internal_bundle",
      sourceId: license.id,
    });

    return license;
  });
}

// ---------------------------------------------------------------------------
// Credit Balances and Grants: moved to ./credit-ledger (#1595), re-exported
// here so every importer keeps one module.
// ---------------------------------------------------------------------------

export {
  grantCreditLot,
  grantCreditsForStripeEvent,
  hasProcessedStripeObject,
  refundCreditGrant,
  spendCreditGrantsByProvenance,
  deductCreditBalance,
  getCreditBalance,
  getCreditBalancesForUsers,
  getPaidCreditGrantsForUsers,
} from "./credit-ledger";
export type {
  CreditGrantSourceType,
  StripeCreditGrantInsert,
  CreditGrantInsert,
  CreditGrantRefund,
  CreditGrantRefundResult,
  CreditGrantHistoryRow,
} from "./credit-ledger";

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


// Hard cap on the number of license rows returned *per userId* by
// getAccountKeysWithCreditsForUserIds. Defensive only: a page of customers is
// bounded to CUSTOMERS_PAGE_SIZE distinct userIds, but a single userId can
// carry an unbounded number of license rows (e.g. repeated admin grants —
// `grant` has no idempotency guard), so without a ceiling here one admin
// page-load could pull an unbounded result set for a single customer.
//
// This MUST be a per-user cap, not a flat cap on the whole batch: a flat
// LIMIT applied to a batch of ~100 userIds means one customer with a large
// license count can crowd out the row window entirely, silently dropping
// OTHER customers from the result (they'd get zero rows back and vanish from
// the page) while the "Showing X-Y of Z" count still claims the full total.
// 100 per customer is generous enough that no realistic customer's real
// license set should ever hit it, while guaranteeing every requested userId
// gets at least some rows back.
const ACCOUNT_KEYS_PER_USER_ROW_LIMIT = 100;

/**
 * Fetch every license (with credit balance) belonging to the given users,
 * newest-first. Used by the admin list so that a customer's full license set
 * is shown even when a search only matched a subset of their licenses.
 *
 * Capped at ACCOUNT_KEYS_PER_USER_ROW_LIMIT rows *per userId* (see constant
 * doc) via a `ROW_NUMBER() OVER (PARTITION BY user_id ORDER BY created_at
 * DESC)` window — every userId passed in gets its own bounded row budget, so
 * one customer's outsized license count can never starve another customer in
 * the same batch out of the result entirely.
 *
 * Display order for a page of customers is determined by the caller (the
 * admin router reorders by the canonical page order returned from
 * getAccountKeyCustomerPage) — this function's own ordering only needs to
 * keep each customer's individual licenses newest-first.
 */
export async function getAccountKeysWithCreditsForUserIds(
  userIds: string[]
): Promise<Array<AccountKeyRow & { credits: number }>> {
  if (userIds.length === 0) return [];

  const rowNumber = sql<number>`row_number() over (partition by ${accountKeys.userId} order by ${accountKeys.createdAt} desc)`;

  const ranked = db.$with("ranked_account_keys").as(
    db
      .select({
        id: accountKeys.id,
        key: accountKeys.key,
        email: accountKeys.email,
        userId: accountKeys.userId,
        status: accountKeys.status,
        polarLicenseKeyId: accountKeys.polarLicenseKeyId,
        polarCustomerId: accountKeys.polarCustomerId,
        stripeCustomerId: accountKeys.stripeCustomerId,
        stripeSessionId: accountKeys.stripeSessionId,
        createdAt: accountKeys.createdAt,
        rowNumber: rowNumber.as("row_number"),
      })
      .from(accountKeys)
      .where(inArray(accountKeys.userId, userIds))
  );

  const rows = await db
    .with(ranked)
    .select()
    .from(ranked)
    .where(sql`${ranked.rowNumber} <= ${ACCOUNT_KEYS_PER_USER_ROW_LIMIT}`)
    .orderBy(desc(ranked.createdAt));

  const licenses = rows.map(drizzleAccountKeyToRow);
  return attachPooledCredits(licenses);
}

/**
 * One page of distinct customers (grouped by user_id) from account_keys, plus
 * the total distinct-customer count — powers server-side pagination on the
 * admin Customers list. An optional email search filters the underlying
 * license rows before grouping (same `ilike` semantics as the old
 * searchAccountKeysByEmail), so a customer only appears in the page if at
 * least one of their licenses matches. `%`, `_` and `\` in the search match
 * themselves, not as wildcards (#1949).
 *
 * Ordered by each customer's most recent license (MAX(created_at) DESC),
 * matching the order the router's in-memory grouping already produces from
 * newest-first license rows, with `user_id` as a tiebreak for determinism.
 *
 * The requested `page` is clamped to the last valid page (given the count
 * from this same call) BEFORE the paginated data query runs, so a stale page
 * number (a search or mutation shrank the result set out from under it)
 * never produces an empty `userIds` page against a nonzero total. The
 * clamped page is returned alongside the data so the caller can echo back
 * what was actually fetched.
 */
export async function getAccountKeyCustomerPage({
  search,
  page,
  pageSize,
}: {
  search?: string;
  page: number;
  pageSize: number;
}): Promise<{ userIds: string[]; totalCustomers: number; page: number }> {
  const emailFilter = search
    ? ilike(accountKeys.email, `%${escapeLikePattern(search)}%`)
    : undefined;

  const countRows = await db
    .select({ total: countDistinct(accountKeys.userId) })
    .from(accountKeys)
    .where(emailFilter);

  const totalCustomers = Number(countRows[0]?.total ?? 0);
  const totalPages = Math.max(1, Math.ceil(totalCustomers / pageSize));
  const clampedPage = Math.min(page, totalPages);

  const pageRows = await db
    .select({
      userId: accountKeys.userId,
      maxCreatedAt: sql<Date>`max(${accountKeys.createdAt})`,
    })
    .from(accountKeys)
    .where(emailFilter)
    .groupBy(accountKeys.userId)
    .orderBy(sql`max(${accountKeys.createdAt}) desc`, accountKeys.userId)
    .limit(pageSize)
    .offset((clampedPage - 1) * pageSize);

  return {
    userIds: pageRows.map((r) => r.userId),
    totalCustomers,
    page: clampedPage,
  };
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

/** Rows per page of the admin per-key device list (#1661). */
export const DEVICES_PAGE_SIZE = 50;

/**
 * Escapes `\`, `%` and `_` so a user's text matches itself literally inside
 * a LIKE / ILIKE pattern (Postgres's default escape character is `\`).
 */
export function escapeLikePattern(text: string): string {
  return text.replace(/[\\%_]/g, (ch) => `\\${ch}`);
}

/**
 * One page of a key's devices, plus how many devices match in total (#1661).
 * A key can hold over a thousand devices, so the admin Devices page asks for
 * them 50 at a time instead of every row at once.
 *
 * - `sinceDays` keeps only devices validated inside that window.
 * - `search` matches the device name or the device id, case-insensitive, as
 *   literal text (LIKE wildcards in it are escaped).
 * - Ordered most recently validated first, then by device id, so a page
 *   boundary never shifts between two requests with tied timestamps.
 */
export async function getDevicesForLicense(
  licenseKeyId: string,
  sinceDays?: number,
  page: { limit?: number; offset?: number; search?: string } = {}
): Promise<{
  devices: Array<{
    deviceId: string;
    deviceName: string | null;
    createdAt: Date;
    lastValidatedAt: Date;
  }>;
  total: number;
}> {
  const conditions = [eq(deviceValidations.licenseKeyId, licenseKeyId)];
  if (sinceDays) {
    const since = new Date();
    since.setDate(since.getDate() - sinceDays);
    conditions.push(gte(deviceValidations.lastValidatedAt, since));
  }
  const search = page.search?.trim();
  if (search) {
    const pattern = `%${escapeLikePattern(search)}%`;
    const match = or(
      ilike(deviceValidations.deviceName, pattern),
      ilike(deviceValidations.deviceId, pattern)
    );
    if (match) conditions.push(match);
  }
  const where = and(...conditions);

  const [devices, countRows] = await Promise.all([
    db
      .select({
        deviceId: deviceValidations.deviceId,
        deviceName: deviceValidations.deviceName,
        createdAt: deviceValidations.createdAt,
        lastValidatedAt: deviceValidations.lastValidatedAt,
      })
      .from(deviceValidations)
      .where(where)
      .orderBy(
        desc(deviceValidations.lastValidatedAt),
        asc(deviceValidations.deviceId)
      )
      .limit(page.limit ?? DEVICES_PAGE_SIZE)
      .offset(page.offset ?? 0),
    db.select({ total: count() }).from(deviceValidations).where(where),
  ]);

  return { devices, total: Number(countRows[0]?.total ?? 0) };
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
}): Promise<void> {
  await db
    .insert(emails)
    .values({
      email: data.email,
      source: data.source ?? null,
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
  metadata?: { name?: string; stripeCustomerId?: string },
  executor: DbExecutor = db
): Promise<UserResult | null> {
  // Check if user exists
  const existing = await executor.query.user.findFirst({
    where: eq(user.email, email.toLowerCase().trim()),
  });
  if (existing) return { id: existing.id, email: existing.email };

  // Create via Better Auth's user table.
  // emailVerified stays false: this address comes from a Stripe checkout
  // or an admin/import grant, so the mailbox owner has never proven control of
  // it. The magic-link plugin flips emailVerified to true on the first
  // successful sign-in, which is the only point where ownership is verified.
  const userId = crypto.randomUUID();
  const [newUser] = await executor
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
  await executor.insert(account).values({
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
 *
 * This also revokes web access, in the same transaction. Moving an account to a
 * different address is an access change: sessions key on `userId` and the row is
 * updated in place, so without this the previous holder of the old address keeps
 * a live, still-rolling session on an account that is no longer theirs. The
 * function already resets `emailVerified` on exactly that reasoning; leaving the
 * sessions alone contradicted it. Pre-existing, but 90-day sessions raised the
 * blast radius from a week, and it goes through the same primitive as every
 * other revocation rather than growing a second mechanism.
 *
 * It also drops each key's cached `stripe_customer_id` (#2460). That Stripe
 * customer was created for the OLD address: reusing it bills the old address,
 * mails it the receipt, and opens its payment history in the billing portal.
 * The next top-up finds or creates the customer for the new address. Past
 * payments stay on the old customer; refunds key on the session, not on this.
 */
export async function updateCustomerEmail(
  userId: string,
  newEmail: string,
  options: { actingUserId?: string } = {},
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
      .set({ email, stripeCustomerId: null })
      .where(eq(accountKeys.userId, userId));

    await revokeWebAccess(userId, { ...options, tx });
  });
}
