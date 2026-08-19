import { createAuthClient } from "better-auth/react";
import { magicLinkClient } from "better-auth/client/plugins";

/**
 * WORKAROUND for an upstream Better Auth bug (`better-auth@1.6.27`).
 *
 * Better Auth's own client issues the rolling-session refresh as a POST that
 * Better Auth's own server router then rejects with 415. Nothing in this repo
 * asks for that request; we only have to make it well-formed. Do not "clean
 * this up" — deleting it silently turns the 90-day rolling session back into a
 * hard 90-day deadline, with no error anywhere. The chain, all of it verifiable
 * in `node_modules`:
 *
 *  1. Under `session.deferSessionRefresh` (see `src/lib/auth.ts`), a GET
 *     `/get-session` does no writes and answers `needsRefresh: true`.
 *  2. `better-auth/dist/client/session-atom.mjs` reacts to that flag with
 *     `$fetch("/get-session", { method: "POST", signal })` — note there is no
 *     `body` and no headers.
 *  3. `@better-fetch/fetch` only infers a `content-type` from the body
 *     (`detectContentType`), so a request with no body is sent with no
 *     `Content-Type` at all.
 *  4. Better Auth configures its router with
 *     `allowedMediaTypes: ["application/json"]`
 *     (`better-auth/dist/api/index.mjs`), and `better-call/dist/utils.mjs`
 *     throws `415 UNSUPPORTED_MEDIA_TYPE` for a request that carries a body
 *     stream with no `Content-Type`. Next.js hands a POST route handler a
 *     (possibly empty) body stream, so this fires on every refresh.
 *  5. `session-atom.mjs` wraps that POST in a bare `try { … } catch {}`, so the
 *     415 is swallowed and the stale, un-refreshed GET result is kept. No
 *     console error, no rejected promise, no signal of any kind.
 *
 * Measured against a running production build, same cookie, one variable:
 *
 *     POST /api/auth/get-session  no Content-Type, no body        -> 415
 *                                 {"code":"UNSUPPORTED_MEDIA_TYPE"}
 *     POST /api/auth/get-session  Content-Type: application/json,
 *                                 no body                         -> 400
 *                                 {"message":"Invalid JSON in request body"}
 *     POST /api/auth/get-session  Content-Type: application/json,
 *                                 body `{}`                       -> 200
 *                                 + Set-Cookie … Max-Age=7776000
 *
 * A default `Content-Type` header on its own is therefore NOT enough — it only
 * moves the failure from 415 to 400, because `better-call` then calls
 * `request.json()` on an empty body and gets a `SyntaxError`. The refresh needs
 * a `Content-Type` *and* a parseable body, which is what `onRequest` supplies.
 *
 * The hook is deliberately narrow, and it expires by itself:
 *  - it ignores GET/HEAD, so the read path is untouched;
 *  - it only fills in a body that is absent. Every call that goes through
 *    Better Auth's client proxy (`signIn.magicLink`, `signIn.licenseKey`,
 *    `signOut`) is already given `body: {…}` by `dist/client/proxy.mjs` and so
 *    is skipped — which is exactly why those three work today and the refresh
 *    does not;
 *  - if a future Better Auth release sends a body with its own refresh POST,
 *    the hook stops matching and does nothing.
 */
export const authClient = createAuthClient({
  plugins: [magicLinkClient()],
  fetchOptions: {
    onRequest(context) {
      const method = context.method?.toUpperCase();

      if (method === "GET" || method === "HEAD") return;
      // Only a request better-fetch is about to send with no payload at all.
      // Anything carrying a real body (JSON, form data, a blob) is left alone.
      if (context.body != null) return;

      context.body = "{}";
      if (!context.headers.has("content-type")) {
        context.headers.set("content-type", "application/json");
      }
    },

    /**
     * Give the session refresh a voice.
     *
     * Better Auth swallows a failed refresh (point 5 above), which is precisely
     * how a completely dead rolling session got through two rounds of review
     * looking healthy. The failure modes that remain here are environmental —
     * a `BETTER_AUTH_URL` that does not match the serving origin fails the
     * POST's origin check with a 403 — and each would otherwise present as
     * "users get signed out eventually", months later, with nothing in the
     * browser console to point at. One warning is cheap, and it is the only
     * signal this path has.
     */
    onError(context) {
      if (context.request.method?.toUpperCase() === "GET") return;

      const url = String(context.request.url);

      if (!url.includes("/get-session")) return;

      // Deliberate: the only signal a failed rolling-session refresh has.
      // eslint-disable-next-line no-console
      console.warn(
        `[auth] Rolling session refresh failed: POST ${url} -> ` +
          `${context.response.status} ${context.error?.message ?? ""}. ` +
          `The session cookie was NOT extended. Check that BETTER_AUTH_URL ` +
          `matches the origin this page is served from.`,
      );
    },
  },
});
