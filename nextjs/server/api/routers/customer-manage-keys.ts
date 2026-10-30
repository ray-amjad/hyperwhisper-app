/**
 * `customer.issueAccountKey` and `customer.deleteAccountKey`: a signed-in user
 * adds an Account Key to their own account, or deletes one of theirs.
 *
 * Kept out of `customer.ts` and loaded on first use, like
 * `customer-rotate-key.ts` (the template for both): it pulls in the email
 * client, the rate limiter and the cloud purge.
 *
 * Order of work, for both:
 *   0. the session must have been proven by email (magic link), not by an
 *      Account Key (`src/lib/session-sign-in-method.ts`). A leaked key must not
 *      be able to give its holder a second key the owner does not know about,
 *      or delete the owner's other keys;
 *   1. per-user rate limit, one budget for both (fails open, like every site
 *      limiter);
 *   2. the db-layer transaction (`issueAccountKey` / `deleteAccountKey`): the
 *      per-user lock, owner checked, only `granted` keys, the cap on issue and
 *      the last-key rule on delete;
 *   3. after commit, best effort: on delete, purge the key from the cloud's
 *      licence cache (awaited, bounded by its 3 s timeout); for both, email the
 *      owner a notice after the response is sent, naming only a 7-character
 *      prefix.
 *
 * Every outcome past the email check writes one JSON line (`logManage`), the
 * shape of `logRotate`, so a refusal leaves a server record. No log line here
 * carries a key.
 */
import { TRPCError } from "@trpc/server";

import { deleteAccountKey, issueAccountKey } from "@/src/lib/db-layer";
import { MAX_LIVE_ACCOUNT_KEYS } from "@/lib/account-key-limits";
import { accountKeyManageRateLimiter } from "@/lib/rate-limit";
import { countFailOpen } from "@/lib/rate-limit-gate";
import { emailService } from "@/lib/services/email";
import { afterResponse } from "@/lib/shared/after-response";
import { purgeCloudLicenseCache } from "@/lib/services/cloud-license-cache";
import { canRotateAccountKeys } from "@/src/lib/session-sign-in-method";

const SUPPORT_EMAIL = "hi@support.hyperwhisper.com";

/** The copy for each refusal. Owner-facing; never echoes a key. */
export const MANAGE_KEY_ERRORS = {
  rateLimited: "Too many Account Key changes. Please try again later.",
  needsEmailSignIn:
    "Sign in with the email link to add or delete an Account Key. A session opened with a key cannot.",
  noLiveKey: "Only an account with an active Account Key can add another one",
  capReached: `An account can have at most ${MAX_LIVE_ACCOUNT_KEYS} active Account Keys. Delete one first.`,
  notFound: "Account Key not found",
  lastKey:
    "This is your only active Account Key, so it can't be deleted. Rotate it instead if it leaked.",
  stale:
    "This Account Key has changed since the page loaded. Refresh to see the current key.",
} as const;

type ManageEvent = "account_key.issue" | "account_key.delete";

type ManageRejectReason =
  | "needs_email_sign_in"
  | "rate_limited"
  | "no_live_key"
  | "cap_reached"
  | "not_found"
  | "not_granted"
  | "stale"
  | "last_key";

/**
 * One JSON line per outcome, the shape of `logRotate`. `userId` and
 * `licenseId` are opaque row ids (for a delete, `licenseId` is the caller's
 * validated uuid; for an issue, the new row's id). Never logs a key, the
 * email or the name.
 */
function logManage(
  event: ManageEvent,
  outcome: "ok" | "rejected",
  fields: {
    reason?: ManageRejectReason;
    userId: string;
    licenseId?: string;
    devicesCleared?: number;
  },
): void {
  const line = JSON.stringify({
    event,
    outcome,
    ...(fields.reason ? { reason: fields.reason } : {}),
    userId: fields.userId,
    ...(fields.licenseId ? { licenseId: fields.licenseId } : {}),
    ...(fields.devicesCleared !== undefined
      ? { devicesCleared: fields.devicesCleared }
      : {}),
  });

  // The website's console output is its server log (Workers Logs), so this is
  // the sink, not leftover debugging.
  /* eslint-disable no-console */
  if (outcome === "ok") {
    console.log(line);
  } else {
    console.warn(line);
  }
  /* eslint-enable no-console */
}

export interface ManageKeysCaller {
  user: { id: string; email?: string | null; name?: string | null };
  sessionId?: string | null;
  signInMethod?: string | null;
}

/**
 * The checks every call makes before any database work: an email, an
 * email-proven session (before the limiter, so a refused session does not
 * spend the owner's budget), then the limiter. Returns the lowercased email.
 */
async function gate(
  event: ManageEvent,
  ctx: ManageKeysCaller,
  licenseId: string | undefined,
): Promise<string> {
  const userEmail = ctx.user.email?.toLowerCase();

  if (!userEmail) {
    throw new TRPCError({
      code: "BAD_REQUEST",
      message: "User email not found",
    });
  }

  if (!canRotateAccountKeys(ctx.signInMethod)) {
    logManage(event, "rejected", {
      reason: "needs_email_sign_in",
      userId: ctx.user.id,
      licenseId,
    });
    throw new TRPCError({
      code: "FORBIDDEN",
      message: MANAGE_KEY_ERRORS.needsEmailSignIn,
    });
  }

  const limit = await countFailOpen(
    () => accountKeyManageRateLimiter.limit(ctx.user.id),
    "account key manage",
  );

  if (limit.limited) {
    logManage(event, "rejected", {
      reason: "rate_limited",
      userId: ctx.user.id,
      licenseId,
    });
    throw new TRPCError({
      code: "TOO_MANY_REQUESTS",
      message: MANAGE_KEY_ERRORS.rateLimited,
    });
  }

  return userEmail;
}

/**
 * Issue the caller one more Account Key on their account (same user, so the
 * same credits and credit pool). Refused unless the caller already holds a
 * granted key, and at the cap.
 *
 * Refusals: FORBIDDEN (not an email session), TOO_MANY_REQUESTS,
 * PRECONDITION_FAILED (no live key), CONFLICT (cap reached).
 */
export async function issueKeyForCaller(
  ctx: ManageKeysCaller,
): Promise<{ id: string; key: string; status: string; createdAt: string }> {
  const event = "account_key.issue";
  const email = await gate(event, ctx, undefined);

  const result = await issueAccountKey({ userId: ctx.user.id, email });

  switch (result.status) {
    case "no_live_key":
      logManage(event, "rejected", {
        reason: "no_live_key",
        userId: ctx.user.id,
      });
      throw new TRPCError({
        code: "PRECONDITION_FAILED",
        message: MANAGE_KEY_ERRORS.noLiveKey,
      });
    case "cap_reached":
      logManage(event, "rejected", {
        reason: "cap_reached",
        userId: ctx.user.id,
      });
      throw new TRPCError({
        code: "CONFLICT",
        message: MANAGE_KEY_ERRORS.capReached,
      });
    case "issued":
      break;
  }

  const { license } = result;

  logManage(event, "ok", { userId: ctx.user.id, licenseId: license.id });

  const ownerName = ctx.user.name?.trim() || "there";

  // After the response: it retries with no per-send timeout, and the owner
  // should see the new key without waiting on email.
  afterResponse(async () => {
    let sent = false;

    try {
      const sendResult = await emailService.sendKeyIssued({
        customerName: ownerName,
        customerEmail: license.email,
        newLicenseKey: license.key,
        issuedAt: new Date(),
        supportEmail: SUPPORT_EMAIL,
      });

      sent = sendResult.success;
    } catch {
      sent = false;
    }
    if (!sent) {
      console.error(`Key-issued notice not sent for license ${license.id}`);
    }
  });

  return {
    id: license.id,
    key: license.key,
    status: license.status,
    createdAt: license.createdAt.toISOString(),
  };
}

export interface DeleteKeyInput {
  id: string;
  currentKey: string;
}

/**
 * Delete one of the caller's Account Keys. It dies everywhere at once: the
 * licence API and the key sign-in refuse it, its devices are cleared, the
 * user's other web sessions are signed out, and the cloud's cached verdict is
 * purged. The caller's last live key is refused.
 *
 * Refusals: FORBIDDEN (not an email session), TOO_MANY_REQUESTS, NOT_FOUND
 * (unknown, not the caller's, or no longer live), CONFLICT (the key changed
 * since the page loaded), PRECONDITION_FAILED (the last live key).
 */
export async function deleteKeyForCaller(
  ctx: ManageKeysCaller,
  input: DeleteKeyInput,
): Promise<{ id: string }> {
  const event = "account_key.delete";
  const email = await gate(event, ctx, input.id);

  const result = await deleteAccountKey({
    id: input.id,
    userId: ctx.user.id,
    email,
    expectedKey: input.currentKey,
    keepSessionId: ctx.sessionId ?? undefined,
  });

  const reject = (
    reason: ManageRejectReason,
    code: TRPCError["code"],
    message: string,
  ): never => {
    logManage(event, "rejected", {
      reason,
      userId: ctx.user.id,
      licenseId: input.id,
    });
    throw new TRPCError({ code, message });
  };

  switch (result.status) {
    case "not_found":
      return reject("not_found", "NOT_FOUND", MANAGE_KEY_ERRORS.notFound);
    // Already deleted, revoked or refunded: not a key the owner can delete,
    // and to the dashboard it is as gone as an unknown id.
    case "not_granted":
      return reject("not_granted", "NOT_FOUND", MANAGE_KEY_ERRORS.notFound);
    case "stale":
      return reject("stale", "CONFLICT", MANAGE_KEY_ERRORS.stale);
    case "last_key":
      return reject(
        "last_key",
        "PRECONDITION_FAILED",
        MANAGE_KEY_ERRORS.lastKey,
      );
    case "deleted":
      break;
  }

  const { license, devicesCleared } = result;

  logManage(event, "ok", {
    userId: ctx.user.id,
    licenseId: license.id,
    devicesCleared,
  });

  // Committed: from here a failure cannot bring the key back.
  const ownerName = ctx.user.name?.trim() || "there";

  afterResponse(async () => {
    let sent = false;

    try {
      const sendResult = await emailService.sendKeyDeleted({
        customerName: ownerName,
        customerEmail: license.email,
        deletedLicenseKey: license.key,
        deletedAt: new Date(),
        supportEmail: SUPPORT_EMAIL,
      });

      sent = sendResult.success;
    } catch {
      sent = false;
    }
    if (!sent) {
      console.error(`Key-deleted notice not sent for license ${license.id}`);
    }
  });

  // In the request, as for rotation: it is what makes the cloud stop
  // accepting the key before its cached verdict expires. Bounded, never throws.
  await purgeCloudLicenseCache(license.key);

  return { id: license.id };
}
