import { createAuthEndpoint } from "better-auth/api";
import { setSessionCookie } from "better-auth/cookies";
import { eq } from "drizzle-orm";
import { z } from "zod";

import { findAccountByKey } from "./db-layer";
import { sanitizeLicenseKeyRedirect } from "./license-key-redirect";

import { sha256Hex } from "@/lib/shared/sha256";
import { db } from "@/src/db";
import { user } from "@/src/db/schema/auth";

type SignInOutcome = "ok" | "rejected" | "failed";

type SignInReason =
  | "key_not_found"
  | "key_inactive"
  | "key_no_user"
  | "user_missing"
  | "session_create_failed"
  | "revoked_mid_sign_in";

/**
 * One structured line per sign-in attempt, in the `{ event, outcome, reason }`
 * shape `server/api/routers/admin/customers.ts` logs. Better Auth answers every
 * refusal below as HTTP 200, so this line is the only record that tells a
 * refusal from a success.
 *
 * `licenseId` and `userId` are opaque row ids, passed only once the row was
 * found. The submitted key is NEVER logged, nor any part of it: an unknown key
 * is a guess and a known one is a credential. The client IP is logged as a
 * truncated SHA-256 (as `sendMagicLink` in `auth.ts` does for an address), so
 * repeated attempts from one client can be correlated without the raw IP.
 */
function logSignIn(
  outcome: SignInOutcome,
  fields: {
    reason?: SignInReason;
    licenseId?: string;
    userId?: string;
    headers: Headers | undefined;
  },
): void {
  const ip = fields.headers?.get("x-forwarded-for")?.split(",")[0]?.trim();
  const line = JSON.stringify({
    event: "auth.license_key_sign_in",
    outcome,
    ...(fields.reason ? { reason: fields.reason } : {}),
    ...(fields.licenseId ? { licenseId: fields.licenseId } : {}),
    ...(fields.userId ? { userId: fields.userId } : {}),
    ipHash: ip ? sha256Hex(ip).slice(0, 12) : null,
  });

  // The website's console output is its server log (Vercel), so this is the
  // sink, not leftover debugging.
  /* eslint-disable no-console */
  if (outcome === "ok") {
    console.log(line);
  } else if (outcome === "rejected") {
    console.warn(line);
  } else {
    console.error(line);
  }
  /* eslint-enable no-console */
}

export const licenseKeyPlugin = () => ({
  id: "license-key",
  endpoints: {
    signInLicenseKey: createAuthEndpoint(
      "/sign-in/license-key",
      {
        method: "POST",
        requireHeaders: true,
        body: z.object({
          licenseKey: z.string(),
          callbackURL: z.string().optional(),
        }),
      },
      async (ctx) => {
        const { licenseKey, callbackURL } = ctx.body;

        const license = await findAccountByKey(licenseKey);

        if (!license || license.status !== "granted") {
          logSignIn("rejected", {
            reason: license ? "key_inactive" : "key_not_found",
            licenseId: license?.id,
            headers: ctx.headers,
          });

          return ctx.json(
            { error: "Invalid or inactive license key." },
            { status: 400 },
          );
        }

        if (!license.userId) {
          logSignIn("rejected", {
            reason: "key_no_user",
            licenseId: license.id,
            headers: ctx.headers,
          });

          return ctx.json(
            {
              error:
                "No account found for this license key. Please contact support.",
            },
            { status: 400 },
          );
        }

        const [foundUser] = await db
          .select()
          .from(user)
          .where(eq(user.id, license.userId))
          .limit(1);

        if (!foundUser) {
          logSignIn("rejected", {
            reason: "user_missing",
            licenseId: license.id,
            userId: license.userId,
            headers: ctx.headers,
          });

          return ctx.json(
            {
              error:
                "No account found for this license key. Please contact support.",
            },
            { status: 400 },
          );
        }

        const session = await ctx.context.internalAdapter.createSession(
          foundUser.id,
        );

        if (!session) {
          logSignIn("failed", {
            reason: "session_create_failed",
            licenseId: license.id,
            userId: foundUser.id,
            headers: ctx.headers,
          });

          return ctx.json(
            { error: "Failed to create session." },
            { status: 500 },
          );
        }

        // Re-read the key now that the session row exists. Revocation and
        // rotation are transactions that lock the key row, change it, AND
        // delete the user's sessions (`revokeWebAccess`), so one landing
        // between the check above and `createSession` would sweep a session
        // that did not exist yet and leave a 90-day one for a dead key.
        //
        // The re-read is a locking read (`FOR SHARE`), so the two orderings
        // cannot both miss, even under READ COMMITTED:
        // - if the change's lock was taken before this read, the read waits for
        //   its COMMIT and then sees the revoked status, or no row under the
        //   rotated-away key, and we delete the session;
        // - otherwise the change has not locked the row yet, so its session
        //   delete runs later, after this session committed, and removes it.
        // A plain read could see the row as it was before an uncommitted
        // rotation while that rotation's delete missed this session.
        const current = await findAccountByKey(licenseKey, db, {
          lockForShare: true,
        });

        if (!current || current.status !== "granted") {
          await ctx.context.internalAdapter.deleteSession(session.token);

          logSignIn("rejected", {
            reason: "revoked_mid_sign_in",
            licenseId: license.id,
            userId: foundUser.id,
            headers: ctx.headers,
          });

          return ctx.json(
            { error: "Invalid or inactive license key." },
            { status: 400 },
          );
        }

        await setSessionCookie(ctx, { session, user: foundUser });

        logSignIn("ok", {
          licenseId: license.id,
          userId: foundUser.id,
          headers: ctx.headers,
        });

        return ctx.json({ redirect: sanitizeLicenseKeyRedirect(callbackURL) });
      },
    ),
  },
  rateLimit: [
    {
      pathMatcher(path: string) {
        return path === "/sign-in/license-key";
      },
      window: 60,
      max: 5,
    },
  ],
});
