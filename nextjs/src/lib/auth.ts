import { cache } from "react";
import { headers } from "next/headers";
import { betterAuth } from "better-auth";
import { drizzleAdapter } from "better-auth/adapters/drizzle";
import { magicLink } from "better-auth/plugins";
import { nextCookies } from "better-auth/next-js";
import { db } from "@/src/db";
import { resend, DEFAULT_FROM_EMAIL } from "@/lib/clients/resend";
import { sha256Hex } from "@/lib/shared/sha256";
import {
  MAGIC_LINK_EXPIRY_SECONDS,
  magicLinkEmailHtml,
  magicLinkEmailText,
} from "@/lib/templates/magic-link-email";
import { licenseKeyPlugin } from "./auth-license-key-plugin";

/**
 * Read `name` / `statusCode` / `message` off whatever Resend put in the `error`
 * slot, checking each field rather than asserting it.
 *
 * The SDK's `ErrorResponse` type does not reliably carry `statusCode`, so the
 * parameter is `unknown` and every field is narrowed. This mirrors
 * `resendErrorDetailsOf` in `lib/services/email.ts` on purpose instead of
 * importing it: that module imports `logSentEmail` from `@/src/lib/db-layer`,
 * which would pull the database into this file's import graph — and #736
 * explicitly keeps the magic-link path off the `sentEmails` / retry path.
 */
function resendErrorFieldsOf(error: unknown): {
  name: string;
  statusCode: string;
  message: string;
} {
  const fields = { name: "unknown", statusCode: "none", message: "" };

  if (typeof error !== "object" || error === null) return fields;

  if ("name" in error && typeof error.name === "string") {
    fields.name = error.name;
  }
  if ("statusCode" in error && typeof error.statusCode === "number") {
    fields.statusCode = String(error.statusCode);
  }
  if ("message" in error && typeof error.message === "string") {
    fields.message = error.message;
  }

  return fields;
}

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
        const result = await resend.emails.send({
          from: DEFAULT_FROM_EMAIL,
          to: email,
          subject: "Sign in to HyperWhisper",
          html: magicLinkEmailHtml({ url }),
          text: magicLinkEmailText({ url }),
        });

        // The Resend SDK resolves to { data, error } and does NOT throw on
        // API-level failures (bad domain, rate limit, suspended key, 5xx), so
        // the error object must be inspected explicitly. Without this, a
        // rejected send returned normally, Better Auth answered 200, and
        // SignInClient.tsx told the user to check an inbox that would never
        // receive a link — with no trace of the failure anywhere (#736).
        if (result.error) {
          const { name, statusCode, message } = resendErrorFieldsOf(
            result.error,
          );

          // ONE line, and the recipient appears only as a 12-char SHA-256
          // prefix: enough to correlate a user's report with this log, never
          // the address itself. `message` is Resend's own text and is included
          // because the name/statusCode pair alone rarely says what was wrong.
          console.error(
            `sendMagicLink failed: resend error name=${name} statusCode=${statusCode} recipientHash=${sha256Hex(
              email,
            ).slice(0, 12)} message=${message}`,
          );

          // A plain Error, not better-auth's APIError, and with NO address in
          // the message — Better Auth surfaces a thrown message toward the
          // client, and the point of the throw is only that the client stops
          // seeing a silent success.
          throw new Error("Failed to send the magic-link email");
        }
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
