/**
 * Which Better Auth HTTP routes the website leaves mounted.
 *
 * A license-key sign-in (`./auth-license-key-plugin.ts`) mints an ordinary
 * session cookie, the same shape as a magic-link session. Better Auth then
 * serves its whole built-in surface to that cookie: list and revoke sessions,
 * update the user, change the email, delete the user, rewrite the session.
 * The portal never calls those routes. Desktop, Linux and iOS clients never
 * call `/api/auth/*` at all — they use `/api/license/*` — so the only callers
 * are the website itself:
 *
 * - `POST /sign-in/magic-link` and `GET /magic-link/verify` — email sign-in
 *   (`SignInClient`, the account-key rotation card)
 * - `POST /sign-in/license-key` — account-key sign-in (`SignInClient`)
 * - `GET` and `POST /get-session` — `authClient.useSession()` and the deferred
 *   rolling refresh (`SessionRefresher`, `src/lib/auth.ts`)
 * - `POST /sign-out` — `authClient.signOut()` in the portal header
 *
 * `auth.api.getSession` and `auth.api.signOut` are in-process calls. They do
 * not pass through this HTTP gate.
 *
 * `disabledPaths` is an exact-match denylist, so it cannot name
 * `/callback/:id` or `/reset-password/:token`, and a later better-auth
 * release can add a route this list does not know. The allow-list plugin is
 * the default deny: anything not named above is 404, including those two.
 * The denylist is still set, so the built-in routes are unmounted by Better
 * Auth itself before its rate limiter and handlers run.
 *
 * Paths are the router's normalized form (no `/api/auth` prefix), matching
 * `disabledPaths` in better-auth 1.7.27.
 */

/** Routes the website's browser actually requests. */
export const ENABLED_AUTH_PATHS = [
  "/get-session",
  "/magic-link/verify",
  "/sign-in/license-key",
  "/sign-in/magic-link",
  "/sign-out",
] as const;

const ENABLED_AUTH_PATH_SET: ReadonlySet<string> = new Set(ENABLED_AUTH_PATHS);

/**
 * Built-in better-auth 1.7.27 routes this app does not call. Dynamic routes
 * are omitted; the allow-list plugin rejects them. `/set-password` is
 * server-only and has no HTTP route.
 */
export const DISABLED_AUTH_PATHS = [
  "/account-info",
  "/change-email",
  "/change-password",
  "/delete-user",
  "/delete-user/callback",
  "/error",
  "/get-access-token",
  "/link-social",
  "/list-accounts",
  "/list-sessions",
  "/ok",
  "/refresh-token",
  "/request-password-reset",
  "/reset-password",
  "/revoke-other-sessions",
  "/revoke-session",
  "/revoke-sessions",
  "/send-verification-email",
  "/sign-in/email",
  "/sign-in/social",
  "/sign-up/email",
  "/unlink-account",
  "/update-session",
  "/update-user",
  "/verify-email",
  "/verify-password",
] as const;

export function isEnabledAuthPath(path: string): boolean {
  return ENABLED_AUTH_PATH_SET.has(path);
}

/**
 * Strip Better Auth's base path the way `@better-auth/core`'s
 * `normalizePathname` does, so this allow-list and `disabledPaths` see the
 * same string. `basePath` is `new URL(ctx.baseURL).pathname` — the context
 * base URL already includes `/api/auth`.
 */
export function normalizeAuthPath(
  requestUrl: string,
  basePath: string,
): string {
  let pathname: string;

  try {
    pathname = new URL(requestUrl).pathname.replace(/\/+$/, "") || "/";
  } catch {
    return "/";
  }

  const normalizedBasePath = basePath.replace(/\/+$/, "");

  if (normalizedBasePath === "") return pathname;
  if (pathname === normalizedBasePath) return "/";
  if (pathname.startsWith(`${normalizedBasePath}/`)) {
    return pathname.slice(normalizedBasePath.length).replace(/\/+$/, "") || "/";
  }

  return pathname;
}

/**
 * Default-deny for the HTTP router. Runs after `disabledPaths` and the rate
 * limiter, so a path named in `DISABLED_AUTH_PATHS` never reaches here, and
 * a path that is only caught here (a dynamic route, a route added upstream)
 * still answers 404 before its handler.
 */
export function authPathAllowListPlugin() {
  return {
    id: "auth-path-allow-list",
    async onRequest(request: Request, ctx: { baseURL: string }) {
      let basePath: string;

      try {
        basePath = new URL(ctx.baseURL).pathname;
      } catch {
        return { response: new Response("Not Found", { status: 404 }) };
      }

      if (isEnabledAuthPath(normalizeAuthPath(request.url, basePath))) return;

      return { response: new Response("Not Found", { status: 404 }) };
    },
  };
}
