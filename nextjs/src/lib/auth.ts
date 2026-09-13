import { cache } from "react";
import { headers } from "next/headers";
import { betterAuth } from "better-auth";
import { drizzleAdapter } from "better-auth/adapters/drizzle";
import { magicLink } from "better-auth/plugins";
import { nextCookies } from "better-auth/next-js";
import { db } from "@/src/db";
import { resend, DEFAULT_FROM_EMAIL } from "@/lib/clients/resend";
import {
  MAGIC_LINK_EXPIRY_SECONDS,
  magicLinkEmailHtml,
  magicLinkEmailText,
} from "@/lib/templates/magic-link-email";
import { licenseKeyPlugin } from "./auth-license-key-plugin";

export const auth = betterAuth({
  baseURL: process.env.BETTER_AUTH_URL,
  database: drizzleAdapter(db, { provider: "pg" }),
  // Explicit session config: Better Auth's defaults are a 7-day `expiresIn`
  // with a 1-day `updateAge`, which signed active users out after a hard week.
  // 90 days + a daily rolling refresh keeps an active user signed in
  // indefinitely — but only if the refresh happens somewhere a `Set-Cookie`
  // can actually be sent, and a Server Component is not such a place.
  //
  // `deferSessionRefresh` is how that is guaranteed structurally rather than
  // by convention. With it on (`node_modules/better-auth/dist/api/routes/
  // session.mjs`), `/get-session` on a **GET** takes an early return that does
  // no writes at all — no `internalAdapter.updateSession`, no
  // `setSessionCookie` — and instead returns a `needsRefresh: true` flag.
  // Server-side `auth.api.getSession()` always goes through `getSessionFromCtx`,
  // which hard-codes `method: "GET"`, so *no* server read — RSC, tRPC context,
  // or a REST route handler — can consume the once-a-day refresh window. There
  // is nothing left to forget: the hazard is gone, not merely documented.
  //
  // The browser completes the refresh. `authClient.useSession()` sees
  // `needsRefresh` and immediately re-requests `/get-session` with **POST**
  // (`dist/client/session-atom.mjs`), the only method that reaches the write
  // branch — and POST is rejected outright unless `deferSessionRefresh` is on.
  // That request hits `/api/auth/[...all]`, a route handler, so the re-issued
  // `Set-Cookie: ...; Max-Age=7776000` reaches the browser intact.
  //
  // That POST does NOT work out of the box. Better Auth sends it with no body
  // and therefore no `Content-Type`, and its own router answers 415 — silently,
  // because the client swallows the error. `src/lib/auth-client.ts` carries the
  // workaround and the full trace; without it this whole design degrades to a
  // hard 90-day deadline that looks healthy.
  // <SessionRefresher /> in the portal layout is what guarantees that POST
  // happens on every hard page load, for every page in the segment. Because
  // server reads no longer refresh at all, that component is now the *only*
  // thing that rolls the session — do not delete it.
  //
  // One deployment caveat: being a POST, the refresh goes through Better
  // Auth's origin check, so `BETTER_AUTH_URL` must match the origin the portal
  // is actually served from. A mismatch fails the POST silently (the client
  // swallows the error and keeps the stale session), and the symptom would be
  // "signed out after 90 days" again. Sign-in itself is also a POST, so a
  // mismatch breaks sign-in first and loudly — but check this before blaming
  // anything else.
  //
  // Deliberately NO `cookieCache`. It would serve the session — including the
  // custom `role` field — from a signed cookie for its whole maxAge without
  // touching the DB, so a demoted admin or a deleted session row would keep
  // passing every admin gate (`adminProcedure`, the `role !== "admin"` guards
  // in customers/devices) until the cache expired. Revocation has to stay
  // immediate; a per-request session SELECT is a cheap price for that.
  session: {
    expiresIn: 60 * 60 * 24 * 90, // 90 days
    updateAge: 60 * 60 * 24, // refresh at most once a day
    deferSessionRefresh: true,
  },
  user: {
    additionalFields: {
      role: {
        type: "string",
        required: false,
        defaultValue: "user",
        input: false,
      },
    },
  },
  plugins: [
    magicLink({
      // Stated, not inherited. This was the plugin's 300-second default while
      // the email promised 10 minutes, so a link could be dead before the
      // stated deadline. The email copy is derived from this same constant.
      expiresIn: MAGIC_LINK_EXPIRY_SECONDS,
      sendMagicLink: async ({ email, url }) => {
        await resend.emails.send({
          from: DEFAULT_FROM_EMAIL,
          to: email,
          subject: "Sign in to HyperWhisper",
          html: magicLinkEmailHtml({ url }),
          text: magicLinkEmailText({ url }),
        });
      },
    }),
    licenseKeyPlugin(),
    nextCookies(),
  ],
});

/**
 * Read the current session inside a portal Server Component, at most once per
 * request.
 *
 * This is a de-duplication convenience, NOT a correctness contract. The portal
 * layout and the page it wraps both need the session, and each `getSession()`
 * is a full session+user SELECT; `cache()` collapses them into one. Calling
 * `auth.api.getSession({ headers: await headers() })` directly is equally
 * correct — `deferSessionRefresh` (see above) already makes it impossible for
 * a server-side read to consume the rolling-refresh window — it just costs an
 * extra query. There is deliberately no "you must use this" rule here.
 */
export const getPortalSession = cache(async () =>
  auth.api.getSession({ headers: await headers() }),
);
