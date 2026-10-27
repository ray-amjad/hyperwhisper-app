/**
 * `customer.rotateAccountKey`: replace one of the caller's Account Keys with a
 * fresh `HW-XXXX-XXXX-XXXX-XXXX` string.
 *
 * Kept out of `customer.ts` and loaded on first use, because it pulls in the
 * email client, the Upstash limiter and the cloud purge, which the router's
 * read procedures (and the suites that drive them) do not need.
 *
 * Order of work:
 *   0. the session must have been proven by email (magic link), not by the
 *      key being rotated (`src/lib/session-sign-in-method.ts`);
 *   1. per-user rate limit (fails open, like every site limiter);
 *   2. `rotateAccountKey()` in db-layer: one transaction, row locked, owner
 *      checked, only `granted` keys, optimistic `currentKey` check so two
 *      rotations at once cannot both win; device rows cleared and every other
 *      web session of the user signed out in the same transaction;
 *   3. after commit, best effort: purge the old string from the cloud's licence
 *      cache (awaited, bounded by its 3 s timeout), and, after the response
 *      is sent, email the owner a notice that names only the new key's prefix.
 *
 * No log line here carries a key.
 */
import { TRPCError } from "@trpc/server";

import { rotateAccountKey } from "@/src/lib/db-layer";
import { accountKeyRotateRateLimiter } from "@/lib/rate-limit";
import { countFailOpen } from "@/lib/rate-limit-gate";
import { emailService } from "@/lib/services/email";
import { afterResponse } from "@/lib/shared/after-response";
import { purgeCloudLicenseCache } from "@/lib/services/cloud-license-cache";
import { canRotateAccountKeys } from "@/src/lib/session-sign-in-method";

/** The copy for each refusal. Owner-facing; never echoes the key. */
export const ROTATE_KEY_ERRORS = {
  rateLimited: "Too many key rotations. Please try again later.",
  notFound: "Account Key not found",
  notGranted: "Only an active Account Key can be rotated",
  needsEmailSignIn:
    "Sign in with the email link to rotate an Account Key. A session opened with the key itself cannot rotate it.",
  stale:
    "This Account Key has already been rotated. Refresh to see the current key.",
} as const;

export interface RotateKeyCaller {
  user: { id: string; email?: string | null; name?: string | null };
  sessionId?: string | null;
  signInMethod?: string | null;
}

export interface RotateKeyInput {
  id: string;
  currentKey: string;
}

export async function rotateKeyForCaller(
  ctx: RotateKeyCaller,
  input: RotateKeyInput,
): Promise<{ id: string; key: string }> {
  const userEmail = ctx.user.email?.toLowerCase();

  if (!userEmail) {
    throw new TRPCError({
      code: "BAD_REQUEST",
      message: "User email not found",
    });
  }

  // Rotation is the remedy for a leaked key, so a session proven only by
  // that key (license-key sign-in) must not be able to rotate it: it would
  // keep the leaker's session, sweep the owner's and hand the leaker the new
  // key. Only a session proven by the account's inbox may. Checked before the
  // limiter, so a refused session does not spend the owner's budget.
  if (!canRotateAccountKeys(ctx.signInMethod)) {
    throw new TRPCError({
      code: "FORBIDDEN",
      message: ROTATE_KEY_ERRORS.needsEmailSignIn,
    });
  }

  // Per user, before any database work.
  const limit = await countFailOpen(
    () => accountKeyRotateRateLimiter.limit(ctx.user.id),
    "account key rotate",
  );

  if (limit.limited) {
    throw new TRPCError({
      code: "TOO_MANY_REQUESTS",
      message: ROTATE_KEY_ERRORS.rateLimited,
    });
  }

  const result = await rotateAccountKey({
    id: input.id,
    userId: ctx.user.id,
    email: userEmail,
    expectedKey: input.currentKey,
    keepSessionId: ctx.sessionId ?? undefined,
  });

  switch (result.status) {
    case "not_found":
      throw new TRPCError({
        code: "NOT_FOUND",
        message: ROTATE_KEY_ERRORS.notFound,
      });
    case "not_granted":
      throw new TRPCError({
        code: "PRECONDITION_FAILED",
        message: ROTATE_KEY_ERRORS.notGranted,
      });
    case "stale":
      throw new TRPCError({
        code: "CONFLICT",
        message: ROTATE_KEY_ERRORS.stale,
      });
    case "rotated":
      break;
  }

  const { license, previousKey, devicesCleared } = result;

  console.log(
    `Account Key rotated: license ${license.id}, ${devicesCleared} device row(s) cleared`,
  );

  // Committed: from here a failure cannot bring the old key back.
  //
  // The notice is sent after the response: it retries with no per-send
  // timeout, and the owner should see the new key without waiting on email.
  const ownerName = ctx.user.name?.trim() || "there";

  afterResponse(async () => {
    let sent = false;

    try {
      const result = await emailService.sendKeyRotated({
        customerName: ownerName,
        customerEmail: license.email,
        newLicenseKey: license.key,
        rotatedAt: new Date(),
        supportEmail: "hi@support.hyperwhisper.com",
      });

      sent = result.success;
    } catch {
      sent = false;
    }
    if (!sent) {
      console.error(`Key-rotated notice not sent for license ${license.id}`);
    }
  });

  // The purge stays in the request: it is what makes the cloud stop
  // accepting the old key before it expires from the cache, so "rotated"
  // is shown once it was tried. It is bounded (3 s) and never throws.
  await purgeCloudLicenseCache(previousKey);

  return { id: license.id, key: license.key };
}
