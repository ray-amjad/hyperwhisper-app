import { createHmac } from "node:crypto";

/**
 * The opaque id of the credit pool an Account Key spends from (#2026).
 *
 * Credits belong to the USER, and one user can own several keys, so
 * HyperWhisper Cloud must count its in-flight credit holds per user, not per
 * key. It must not learn the raw user id, so it gets this instead:
 * HMAC-SHA256 of the user id under `CREDIT_POOL_ID_SECRET`, as 64 hex
 * characters. Same user, same id, on every key and every call; nothing about
 * the user can be read back from it.
 *
 * `null` when the secret is not set: the licence routes then leave
 * `credit_pool_id` out, and the cloud keys holds by the Account Key, as it did
 * before (Ray, 2026-10-19). Rotating the secret moves every user to a new pool
 * id; holds taken under the old id are not counted against the new one until
 * they expire (15 minutes at most), so rotate when traffic is low.
 */
export function creditPoolIdFor(
  userId: string,
  secret: string | undefined = process.env.CREDIT_POOL_ID_SECRET,
): string | null {
  if (!secret) {
    warnMissingSecretOnce();

    return null;
  }

  return createHmac("sha256", secret)
    .update(`credit-pool:v1:${userId}`)
    .digest("hex");
}

let warnedMissingSecret = false;

// Said once per process: without the secret the cloud's credit holds fall back
// to per-key, and one user's keys can together overspend their pool again.
function warnMissingSecretOnce(): void {
  if (warnedMissingSecret) return;
  warnedMissingSecret = true;
  console.warn(
    "CREDIT_POOL_ID_SECRET is not set: licence replies omit credit_pool_id and the cloud holds credits per Account Key (#2026)",
  );
}
