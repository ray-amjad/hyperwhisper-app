import { cache } from "react";
import { headers } from "next/headers";
import { betterAuth, APIError } from "better-auth";
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

/** Written in place of anything in a log line that could be an address. */
const REDACTED = "[redacted]";

/**
 * Anything holding an `@`, out to the surrounding whitespace — but never across
 * a `=`.
 *
 * Deliberately greedy on the right and imprecise. The job is not to parse an
 * address correctly, it is to make sure nothing address-shaped survives:
 * over-redacting a log line is harmless, under-redacting is the bug (#736
 * forbids the address).
 *
 * The left side stops at `=`, and that is not cosmetic. This runs over the
 * WHOLE assembled `key=value` line, so a `\S+` left side swallowed the KEY
 * whenever Resend's text STARTED with the address:
 *
 *   message=alice@corp.com is not a valid recipient
 *        -> [redacted] is not a valid recipient
 *
 * `message=` was gone, so an operator or a log parser grepping `message=` for
 * the reason a send failed found nothing, and `name=` was exposed to the same
 * loss whenever Resend's error name was address-shaped. The structured shape of
 * this line is the point of it; the redaction must not eat the structure.
 *
 * The left side is `*` rather than `+`, and the right side `\S*` rather than
 * `\S+`, so a bare `@` and an `@` with nothing before it are matched too. Every
 * `@` in the line is therefore inside some match, which is what makes "no `@`
 * survives" a property of the pattern instead of a property of the wordings we
 * happened to test.
 *
 * The cost of excluding `=`: an address whose LOCAL-PART contains a `=` (legal
 * per RFC 5322) leaves the text up to its last `=` behind, so `a=b@corp.com`
 * redacts to `a=[redacted]`. What survives is a fragment of a local part, which
 * this function already declines to redact on its own — see below.
 */
const ADDRESS_SHAPED = /[^\s=]*@\S*/g;

function escapeForRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/**
 * The fewest dot-separated labels a domain candidate may have before it is
 * allowed to be redacted out of provider prose.
 *
 * 2 is chosen deliberately. Every candidate with 2 or more labels contains a
 * `.`, so it cannot collide with an ordinary word in Resend's message. At 1
 * label the candidate is a bare TLD or hostname — `com`, `io`, `mail` — and
 * replacing that would shred the only field that usually says what actually
 * failed, which is a worse outcome than the leak it prevents.
 *
 * This floor deliberately does NOT try to find the registrable domain.
 * `alice@corp.co.uk` yields `corp.co.uk` AND `co.uk`, so a message naming
 * `co.uk` on its own is over-redacted. That is the harmless direction, and a
 * public-suffix list is a dependency this fix will not add.
 */
const MIN_DOMAIN_LABELS = 2;

/**
 * The recipient's own domain plus every parent of it at or above the label
 * floor, longest first.
 *
 * The parents are the fix for a subdomained recipient. Resend answers by naming
 * the domain it actually checked rather than the one that was submitted: for
 * `alice@mail.corp.com` the reply is "The corp.com domain is not verified.",
 * which carries no `@` for the pass above to catch and is not the literal
 * `mail.corp.com` an exact-match pass searches for. That bare-domain shape is
 * one of the two `redactAddresses` exists to handle, and it used to survive
 * verbatim.
 *
 * Longest first, because the alternation built from this list takes the first
 * branch that matches at a position — so the full domain wins and no `mail.`
 * is left stranded in front of a redaction.
 */
function domainCandidatesOf(recipientDomain: string): string[] {
  const labels = recipientDomain.split(".");
  const candidates: string[] = [];

  for (let i = 0; labels.length - i >= MIN_DOMAIN_LABELS; i += 1) {
    candidates.push(labels.slice(i).join("."));
  }

  return candidates;
}

/**
 * Strip email addresses, and the recipient's own domain, out of a finished log
 * line.
 *
 * Applied to the WHOLE assembled line rather than to one field, so "the
 * recipient reaches this log only as a hash" is a property of the mechanism and
 * survives a later edit that interpolates one more provider-controlled value.
 *
 * Two passes, because Resend's text carries either shape:
 *   - a full address — a real `validation_error` echoes the rejected recipient
 *     ("... alice@corp.com is not a valid recipient");
 *   - a bare domain — "the corp.com domain is not verified".
 *
 * What this GUARANTEES, for any input:
 *   - no `@` survives anywhere in the returned line;
 *   - neither the recipient's domain nor any parent of it down to the label
 *     floor survives, in any case, whether or not it arrived attached to an `@`;
 *   - the `key=` tokens of this line's own format survive, so the line stays
 *     greppable.
 *
 * What it does NOT guarantee, and cannot: Resend owns the `message` text, so it
 * may name the recipient in a form no pattern here matches — the local part on
 * its own ("the user alice is blocked"), a percent-encoded address
 * (`alice%40corp.com`), an address written with spaces around its `@`, or some
 * other identifier entirely. **Complete redaction of provider prose is not
 * achievable from this side**, and this comment deliberately does not claim it.
 * The guarantees above are what the two passes actually deliver.
 *
 * The recipient's LOCAL-PART is deliberately NOT redacted on its own either. It
 * does not identify anybody without a domain, and a short or common one ("a",
 * "me", "info") would shred the surrounding message.
 */
function redactAddresses(line: string, recipientDomain: string): string {
  const withoutAddresses = line.replace(ADDRESS_SHAPED, REDACTED);

  const candidates = domainCandidatesOf(recipientDomain);

  // No candidate at all: an empty domain (a recipient with no `@`) or a
  // single-label one. The early return is load-bearing, not tidiness — joining
  // an empty list gives an empty pattern, and a global empty pattern makes
  // `String.replace` insert the replacement between every character of the
  // line.
  if (candidates.length === 0) return withoutAddresses;

  return withoutAddresses.replace(
    new RegExp(candidates.map(escapeForRegExp).join("|"), "gi"),
    REDACTED,
  );
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
              `sendMagicLink failed: resend error name=${name} statusCode=${statusCode} recipientHash=${sha256Hex(
                normalisedRecipient,
              ).slice(0, 12)} message=${message}`,
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
