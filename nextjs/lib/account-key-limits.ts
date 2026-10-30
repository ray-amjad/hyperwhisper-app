/**
 * Limits on the Account Keys a signed-in user manages from the dashboard.
 *
 * Shared by the server (`issueAccountKey` / `deleteAccountKey` in
 * `src/lib/db-layer.ts`) and the dashboard, which hides or disables the
 * controls with the same rules so the user is told why before a refusal.
 *
 * No imports: a client component imports this file.
 */

/**
 * The most live (`granted`) Account Keys one account may hold. Issuing a key
 * past it is refused. Keys are free handles on one credit pool, so the cap is
 * only there to bound abuse and keep the dashboard list short; a key per
 * device is not the model (one key works on every device).
 */
export const MAX_LIVE_ACCOUNT_KEYS = 5;

/**
 * The most Account Key rows one user may gain in any 24 hours before the
 * dashboard's "Add a new key" is refused. The live cap alone does not bound
 * rows: a delete keeps its row (`deleted`), so issue-then-delete cycles could
 * add rows (and two notice emails each) without end. Every row the user got
 * in the window counts, purchases included, so the bound needs no new column.
 */
export const MAX_ACCOUNT_KEYS_ISSUED_PER_DAY = 10;

/**
 * The `account_keys.status` a key the owner deleted from the dashboard gets.
 * Not `revoked`, which an admin or a refund sets: the admin pages tell the two
 * apart. Every licence reader refuses any status but `granted`.
 */
export const ACCOUNT_KEY_DELETED_STATUS = "deleted";
