import { createAuthEndpoint } from "better-auth/api";
import { setSessionCookie } from "better-auth/cookies";
import { eq } from "drizzle-orm";
import { z } from "zod";

import { findAccountByKey } from "./db-layer";
import { sanitizeLicenseKeyRedirect } from "./license-key-redirect";

import { db } from "@/src/db";
import { user } from "@/src/db/schema/auth";

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
          return ctx.json(
            { error: "Invalid or inactive license key." },
            { status: 400 },
          );
        }

        if (!license.userId) {
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
          return ctx.json(
            { error: "Failed to create session." },
            { status: 500 },
          );
        }

        // Re-read the key now that the session row exists. Revocation is a
        // transaction that flips the status AND deletes the user's sessions
        // (`revokeWebAccess`), so a revocation landing between the check above
        // and `createSession` would sweep a session that did not exist yet and
        // hand out a fresh 90-day one for a dead key. Reading after the write
        // means the two orderings cannot both miss: either revocation sees this
        // row and deletes it, or this read sees the revoked status and we do.
        const current = await findAccountByKey(licenseKey);

        if (!current || current.status !== "granted") {
          await ctx.context.internalAdapter.deleteSession(session.token);

          return ctx.json(
            { error: "Invalid or inactive license key." },
            { status: 400 },
          );
        }

        await setSessionCookie(ctx, { session, user: foundUser });

        const ipAddress =
          ctx.headers?.get("x-forwarded-for")?.split(",")[0]?.trim() ??
          "unknown";
        const userAgent = ctx.headers?.get("user-agent") ?? "unknown";
        console.log(
          `[license-key-sign-in] user=${foundUser.id} license=${license.id} ip=${ipAddress} ua="${userAgent}"`,
        );

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
