/**
 * `customer.rotateAccountKey`: replace one of the caller's Account Keys with a
 * fresh `HW-XXXX-XXXX-XXXX-XXXX` string.
 *
 * Kept out of `customer.ts` and loaded on first use, because it pulls in the
 * email client, the rate limiter and the cloud purge, which the router's
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
 * Every outcome past the email check writes one JSON line (`logRotate`), so a
 * refused rotation, above all a key-only session trying to rotate (a leaked
 * key), leaves a server record: the tRPC `onError` logs only 5xx.
 *
 * No log line here carries a key.
 */
import { TRPCError } from "@trpc/server";

import { rotateAccountKey } from "@/src/lib/db-layer";
import { accountKeyRotateRateLimiter } from "@/lib/rate-limit";
import { countFailOpen } from "@/lib/rate-limit-gate";
import { emailService } from "@/lib/services/email";
import { afterResponse } from "@/lib/shared/after-response";
import {
  cloudPurgeConfirmed,
  logCloudPurge,
  purgeCloudLicenseCache,
} from "@/lib/services/cloud-license-cache";
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

type RotateOutcome = "ok" | "rejected";

type RotateRejectReason =
  | "needs_email_sign_in"
  | "rate_limited"
  | "not_found"
  | "not_granted"
  | "stale";

/**
 * One JSON line per rotation outcome, the shape of `logSignIn` in
 * `src/lib/auth-license-key-plugin.ts`. `userId` and `licenseId` are opaque
 * row ids (`licenseId` is the caller's `input.id`, a validated uuid). Never
 * logs `currentKey`, the new key, the email or the name.
 */
function logRotate(
  outcome: RotateOutcome,
  fields: {
    reason?: RotateRejectReason;
    userId: string;
    licenseId?: string;
    devicesCleared?: number;
  },
): void {
  const line = JSON.stringify({
    event: "account_key.rotate",
    outcome,
    ...(fields.reason ? { reason: fields.reason } : {}),
    userId: fields.userId,
    ...(fields.licenseId ? { licenseId: fields.licenseId } : {}),
    ...(fields.devicesCleared !== undefined
      ? { devicesCleared: fields.devicesCleared }
      : {}),
  });

  // The website's console output is its server log (Workers Logs), so this is the
  // sink, not leftover debugging.
  /* eslint-disable no-console */
  if (outcome === "ok") {
    console.log(line);
  } else {
    console.warn(line);
  }
  /* eslint-enable no-console */
}

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
): Promise<{ id: string; key: string; cloudCleared: boolean }> {
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
    logRotate("rejected", {
      reason: "needs_email_sign_in",
      userId: ctx.user.id,
      licenseId: input.id,
    });
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
    logRotate("rejected", {
      reason: "rate_limited",
      userId: ctx.user.id,
      licenseId: input.id,
    });
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
      logRotate("rejected", {
        reason: "not_found",
        userId: ctx.user.id,
        licenseId: input.id,
      });
      throw new TRPCError({
        code: "NOT_FOUND",
        message: ROTATE_KEY_ERRORS.notFound,
      });
    case "not_granted":
      logRotate("rejected", {
        reason: "not_granted",
        userId: ctx.user.id,
        licenseId: input.id,
      });
      throw new TRPCError({
        code: "PRECONDITION_FAILED",
        message: ROTATE_KEY_ERRORS.notGranted,
      });
    case "stale":
      logRotate("rejected", {
        reason: "stale",
        userId: ctx.user.id,
        licenseId: input.id,
      });
      throw new TRPCError({
        code: "CONFLICT",
        message: ROTATE_KEY_ERRORS.stale,
      });
    case "rotated":
      break;
  }

  const { license, previousKey, devicesCleared } = result;

  logRotate("ok", {
    userId: ctx.user.id,
    licenseId: license.id,
    devicesCleared,
  });

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
  // When not every cloud app confirmed, the owner is told the old key may
  // keep working in HyperWhisper Cloud for up to an hour.
  const purge = await purgeCloudLicenseCache(previousKey);

  logCloudPurge("rotate", purge, {
    userId: ctx.user.id,
    licenseId: license.id,
  });

  return {
    id: license.id,
    key: license.key,
    cloudCleared: cloudPurgeConfirmed(purge),
  };
}
