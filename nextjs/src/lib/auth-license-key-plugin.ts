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
