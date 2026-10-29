import { cache } from "react";
import { headers } from "next/headers";
import { betterAuth, APIError } from "better-auth";
import { drizzleAdapter } from "better-auth/adapters/drizzle";
import { createAuthMiddleware, isAPIError } from "better-auth/api";
import { magicLink } from "better-auth/plugins";
import { nextCookies } from "better-auth/next-js";
import { db } from "@/src/db";
import { env } from "@/src/env/server.mjs";
import { resend, DEFAULT_FROM_EMAIL } from "@/lib/clients/resend";
import { sha256Hex } from "@/lib/shared/sha256";
import { redactAddresses } from "@/lib/shared/redact";
import {
  MAGIC_LINK_EXPIRY_SECONDS,
  magicLinkEmailHtml,
  magicLinkEmailText,
} from "@/lib/templates/magic-link-email";
import { licenseKeyPlugin } from "./auth-license-key-plugin";
import { signInMethodForPath } from "./session-sign-in-method";
import { authRateLimitOptions } from "./auth-rate-limit-storage";

/**
 * Read `name` / `statusCode` / `message` off whatever Resend put in the `error`
 * slot, checking each field rather than asserting it, and return them already
 * stringified so the one call site below needs no `??` noise and cannot
 * interpolate `undefined`.
 *
 * The SDK's `ErrorResponse` type does not reliably carry `statusCode`, so the
 * parameter is `unknown` and every field is narrowed.
 *
 * `statusCode` has three outcomes on purpose. Resend's own network-failure path
 * (`resend/dist/index.mjs`, the `catch` around `fetch`) sets
 * `{ name: "application_error", statusCode: null }`, so an explicit `null` means
 * "the request never reached Resend" while a missing or wrong-typed field means
 * "Resend sent something this code does not understand". Collapsing both to one
 * token would make an unreachable API indistinguishable from a malformed error.
 *
 * This deliberately duplicates `resendErrorDetailsOf` in
 * `lib/services/email.ts` rather than importing it. NOT because of the import
 * graph — `auth.ts` already imports `@/src/db` directly (line 7) and reaches
 * `./db-layer` transitively through `./auth-license-key-plugin`, so the
 * database is in this file's graph either way. The real reasons are that
 * `resendErrorDetailsOf` is not exported (`email.ts` exports only the
 * `emailService` singleton), that #736 explicitly keeps the magic-link path off
 * the `sentEmails` / retry path that module owns, and that the two have
 * diverged for their different jobs: `email.ts` returns optional fields that
 * `isRetryableResendError` branches on, this one returns display strings.
 * Extracting a shared narrower would mean editing that retry decision's inputs,
 * which is out of scope for #736 and has no direct test coverage (the only test
 * that reaches `lib/services/email.ts` replaces the whole module with a mock).
 */
function resendErrorFieldsOf(error: unknown): {
  name: string;
  statusCode: string;
  message: string;
} {
  const fields = { name: "unknown", statusCode: "unknown", message: "" };

  if (typeof error !== "object" || error === null) return fields;

  if ("name" in error && typeof error.name === "string") {
    fields.name = error.name;
  }
  if ("statusCode" in error) {
    if (typeof error.statusCode === "number") {
      fields.statusCode = String(error.statusCode);
    } else if (error.statusCode === null) {
      fields.statusCode = "null";
    }
  }
  if ("message" in error && typeof error.message === "string") {
    fields.message = error.message;
  }

  return fields;
}

const MAGIC_LINK_VERIFY_PATH = "/magic-link/verify";

/**
 * The per-request auth contexts in which the magic-link verify created the
 * user (#2638). `databaseHooks.user.create.after` below adds to it, and the
 * verify log line reads it as `newUser`.
 *
 * Why a marker, not a heuristic: better-auth's verify handler keeps its own
 * `isNewUser` local. Its only outward trace is the redirect to
 * `newUserCallbackURL`, which falls back to `callbackURL` and which
 * `SignInClient.tsx` never sends, so the Location cannot tell. Comparing
 * `user.createdAt` with the request time is a clock guess: measured on the
 * memory adapter, a new user's row was 6 ms older than its session and a
 * returning user's (signed in moments before) only 42 ms older. The
 * create hook and the after hook run against the SAME `ctx.context` object
 * (dispatch builds one per request, and the adapter's hooks read it from the
 * endpoint's async context), so membership is exact. A WeakSet, so a request
 * that dies before the after hook (a thrown non-`APIError`) leaks nothing.
 */
const verifyContextsThatCreatedAUser = new WeakSet<object>();

type MagicLinkVerifyOutcome = "ok" | "rejected" | "error";

/**
 * Classify a finished `/magic-link/verify` request from what better-auth left
 * on its context. Every failure in the handler is `throw ctx.redirect(...)` to
 * the error callback with `?error=<code>`, which `dispatch.mjs` catches into
 * `ctx.context.returned` as an `APIError` carrying a `location` header, so the
 * code is read from there (it is also what `SignInClient.tsx` reads).
 *
 * - `INVALID_TOKEN` (used, expired or unknown link) is the user's link, not
 *   the server, so it is `rejected`.
 * - Any other `?error=` code (`failed_to_create_user`,
 *   `failed_to_create_session`, `user_not_found`, a `createUser` APIError
 *   code) is `error`.
 * - An `APIError` with no redirect is a refusal before the handler ran (the
 *   query schema, the callback-URL origin checks): `rejected` when 4xx,
 *   `error` otherwise, with the error's code as `reason`.
 * - No session and nothing recognisable: `error` / `unexpected`.
 */
function classifyMagicLinkVerify(
  returned: unknown,
  signedIn: boolean,
): { outcome: MagicLinkVerifyOutcome; reason?: string } {
  if (signedIn) return { outcome: "ok" };
  if (!isAPIError(returned)) return { outcome: "error", reason: "unexpected" };

  const location = new Headers(returned.headers).get("location");

  if (location) {
    // Only the `error` parameter is read. The Location itself is never logged:
    // it carries the caller's callback URL.
    const code = new URL(location, "http://localhost").searchParams.get(
      "error",
    );

    if (code === "INVALID_TOKEN") return { outcome: "rejected", reason: code };

    return { outcome: "error", reason: code ?? "unexpected" };
  }

  const code =
    typeof returned.body?.code === "string"
      ? returned.body.code
      : `http_${returned.statusCode}`;

  return {
    outcome: returned.statusCode < 500 ? "rejected" : "error",
    reason: code,
  };
}

/**
 * ONE structured line per `GET /api/auth/magic-link/verify` (#2638), in the
 * shape `logSignIn` (`./auth-license-key-plugin.ts`) writes for the Account
 * Key path, so either sign-in method answers "who signed in" and "why did my
 * link not work" from the logs. Every verify failure is a 302, not a thrown
 * error, so nothing else (Sentry, `onRequestError`) sees one.
 *
 * Logged: opaque ids, a reason code, and the client IP as a 12-character
 * SHA-256 prefix. NEVER the email address, the token, the verify URL, the
 * callback URL or the session token.
 */
function logMagicLinkVerify(ctx: {
  headers?: Headers;
  context: object & {
    newSession: { user: { id: string } } | null;
    returned?: unknown;
  };
}): void {
  const { newSession, returned } = ctx.context;
  const { outcome, reason } = classifyMagicLinkVerify(
    returned,
    newSession !== null,
  );
  // cf-connecting-ip is set by Cloudflare and cannot be forged; the
  // x-forwarded-for fallback only matters off Cloudflare (local dev, tests).
  const ip =
    ctx.headers?.get("cf-connecting-ip")?.trim() ||
    ctx.headers?.get("x-forwarded-for")?.split(",")[0]?.trim();
  const line = JSON.stringify({
    event: "auth.magic_link_verify",
    outcome,
    ...(reason ? { reason } : {}),
    ...(newSession
      ? {
          userId: newSession.user.id,
          newUser: verifyContextsThatCreatedAUser.has(ctx.context),
        }
      : {}),
    ipHash: ip ? sha256Hex(ip).slice(0, 12) : null,
  });

  // The website's console output is its server log (Workers Logs).
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

export const auth = betterAuth({
  baseURL: env.BETTER_AUTH_URL,
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
    // How the session was proven (`./session-sign-in-method.ts`). Server-set
    // only (`input: false`) and with no default, so the hook below is the one
    // writer. An Account Key rotation needs an "email" session.
    additionalFields: {
      signInMethod: {
        type: "string",
        required: false,
        input: false,
      },
    },
  },
  databaseHooks: {
    session: {
      create: {
        // Stamp the method from the endpoint creating the session: the magic
        // link verify or the license-key sign-in. Anything else stays null.
        before: async (session, ctx) => ({
          data: { ...session, signInMethod: signInMethodForPath(ctx?.path) },
        }),
      },
    },
    user: {
      create: {
        // Marks the request, for `newUser` on the verify log line below. See
        // `verifyContextsThatCreatedAUser`.
        after: async (_user, ctx) => {
          if (ctx?.path === MAGIC_LINK_VERIFY_PATH) {
            verifyContextsThatCreatedAUser.add(ctx.context);
          }
        },
      },
    },
  },
  hooks: {
    // One log line per magic-link verify (#2638). Logging only: it returns
    // nothing, so the response (redirect, cookies, error code) is untouched.
    //
    // The try/catch is load-bearing. `runAfterHooks` in better-auth's
    // `dispatch.mjs` turns an `APIError` thrown here INTO the response and
    // rethrows anything else as a 500, so a fault in the log line would undo a
    // sign-in that already succeeded. Nothing from `ctx` goes into the fallback.
    after: createAuthMiddleware(async (ctx) => {
      if (ctx.path !== MAGIC_LINK_VERIFY_PATH) return;
      try {
        logMagicLinkVerify(ctx);
      } catch {
        // eslint-disable-next-line no-console
        console.error(
          JSON.stringify({ event: "auth.magic_link_verify_log_failed" }),
        );
      }
    }),
  },
  // Better Auth's own rate-limit rules, counted in the site's Upstash so every
  // Worker isolate shares one budget (#2010). `customStorage`, deliberately
  // not `secondaryStorage`: the latter would also move sessions off Postgres.
  // Fails open when Upstash is down or unconfigured. See
  // `./auth-rate-limit-storage.ts`.
  rateLimit: authRateLimitOptions(),
  advanced: {
    ipAddress: {
      // Cloudflare sets cf-connecting-ip on every request and overwrites any
      // client value. Its x-forwarded-for APPENDS to a client-supplied chain,
      // so it must not come first. x-forwarded-for stays as the fallback for
      // local dev and the tests, where no Cloudflare edge is in front.
      ipAddressHeaders: ["cf-connecting-ip", "x-forwarded-for"],
    },
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
        // Normalised HERE, at the call site, and NOT inside `sha256Hex`: that
        // helper is documented as a pure digest and a known-answer test pins a
        // literal hex to keep it one.
        //
        // The digest has to be reproducible from the address a user reports over
        // support, and nothing upstream normalises. `SignInClient.tsx` posts the
        // field exactly as typed, better-auth's magic-link route forwards
        // `ctx.body.email` unchanged, and its `z.email()` validates without
        // lowercasing. So `Alice@Acme.com` off a phone keyboard would hash to a
        // different digest than the `alice@acme.com` the user reports, and a
        // lowercase retry would mint a second one — one incident reading as two
        // users. better-auth's own sibling plugins normalise at this same seam
        // (`plugins/email-otp/routes.mjs`, `plugins/admin/routes.mjs`).
        //
        // Only the hash and the redaction below use this. `to:` still gets the
        // address as given, because an email local-part is case-sensitive per
        // RFC 5321 and rewriting the envelope is not this fix's business.
        const normalisedRecipient = email.trim().toLowerCase();
        const recipientHash = sha256Hex(normalisedRecipient).slice(0, 12);

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

          // ONE line from this callback. The recipient appears in it only as a
          // 12-character SHA-256 prefix of the NORMALISED address — enough to
          // correlate a user's support report with this log.
          //
          // `message` is kept because the name/statusCode pair rarely says what
          // was actually wrong, but both it and `name` are provider-controlled
          // text, so the assembled line goes through `redactAddresses` before it
          // is logged. Resend's `validation_error` really does echo the rejected
          // recipient ("... alice@corp.com is not a valid recipient"), which
          // would otherwise put the address a few characters from the hash whose
          // whole purpose is to avoid it. Redaction is what makes "never the
          // address itself" true of the mechanism instead of true only of the
          // wordings we happened to test.
          //
          // Exactly ONE record comes from this callback. In PRODUCTION it is
          // also all the request emits: `betterAuth()` here configures neither
          // `logger` nor `onAPIError`, and the `APIError` thrown below becomes
          // the response before better-auth's and better-call's error handlers
          // exist to log it — see the measurement at the throw.
          //
          // In a NON-production build there is a SECOND record, it is not
          // redacted, and it lands before this one. The Resend SDK logs every
          // failed send itself: `console.error("[Resend API Error]:", { status,
          // error, path })` in `resend/dist/index.mjs` (`logError`), gated only
          // on `process.env.NODE_ENV !== "production"`. So under `next dev` a
          // `validation_error` whose message echoes the recipient prints the
          // address in the clear whatever this line does.
          //
          // That log is deliberately NOT suppressed here. It never fires in
          // production, it is pre-existing at all five `resend.emails.send` call
          // sites (four of them in `lib/services/email.ts`, untouched by #736),
          // and the SDK exposes no logger hook at that line — so quieting it
          // means wrapping or muting the client app-wide. That belongs to #717,
          // which owns plaintext addresses in logs across every call site.
          console.error(
            redactAddresses(
              `sendMagicLink failed: resend error name=${name} statusCode=${statusCode} recipientHash=${recipientHash} message=${message}`,
              normalisedRecipient.split("@")[1] ?? "",
            ),
          );

          // better-auth's `APIError`, not a plain `Error`. Measured against a
          // real `auth.handler` (memory adapter, this same `magicLink` plugin,
          // no `logger` and no `onAPIError`, as configured here):
          //
          //   plain Error  -> HTTP 500, EMPTY body,             2 extra console.error
          //   APIError 503 -> HTTP 503, {"message": "..."},     0 extra console.error
          //
          // The empty body is why the plain `Error` was wrong. It falls past
          // `isAPIError` in better-call's router (`better-call/dist/router.mjs`),
          // which answers `new Response(null, { status: 500 })`; better-fetch
          // parses that to `null`, so `authError.message` is `undefined` and
          // `SignInClient.tsx` could only ever show its generic fallback. On the
          // way out it also costs two stack dumps that never name
          // `sendMagicLink` — better-auth's router `onError` and better-call's
          // `# SERVER_ERROR:` — so an operator greping for one record per failed
          // send would find three.
          //
          // An `APIError` never reaches either. `better-auth/dist/api/dispatch.mjs`
          // catches it around the endpoint call and turns it straight into the
          // response, so the message below is genuinely what the browser reads.
          //
          // 503, not 500: the app itself is healthy, it could not reach its email
          // provider, and the sign-in page's "Resend Magic Link" button is the
          // retry that status invites.
          //
          // NO address and NO Resend text in the message — it goes to a browser.
          throw new APIError("SERVICE_UNAVAILABLE", {
            message: "Failed to send the magic-link email. Please try again.",
          });
        }

        // The success twin of the failure line above (#2638), so a "no email
        // arrived" report can be matched to an accepted send. Same 12-character
        // hash of the normalised address, never the address or the link.
        // eslint-disable-next-line no-console
        console.log(
          JSON.stringify({
            event: "auth.magic_link_send",
            outcome: "ok",
            recipientHash,
          }),
        );
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
