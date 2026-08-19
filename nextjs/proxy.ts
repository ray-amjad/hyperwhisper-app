import createMiddleware from "next-intl/middleware";
import { NextRequest, NextResponse } from "next/server";

import { routing } from "./src/i18n/routing";
import { defaultLocale, locales } from "./src/i18n/locales";

const intlMiddleware = createMiddleware(routing);
const localePattern = locales
  .map((locale) => locale.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"))
  .join("|");
const LOCALE_REGEX = new RegExp(`^\\/(${localePattern})(\\/|$)`);
const USER_ROUTE_REGEX = new RegExp(`^\\/(${localePattern})\\/user`);
const USER_SIGN_IN_REGEX = new RegExp(`^\\/(${localePattern})\\/user\\/sign-in`);
const USER_AUTH_SIGN_OUT_REGEX = new RegExp(
  `^\\/(${localePattern})\\/user\\/auth\\/sign-out`,
);

const getPathLocale = (pathname: string) => {
  const match = pathname.match(LOCALE_REGEX);
  return match?.[1] ?? defaultLocale;
};

/**
 * Check if Better Auth session cookie is present.
 * For middleware, we only check cookie presence (no HTTP round-trip).
 * Actual session validation happens in the API layer.
 *
 * Better Auth prefixes cookies with "__Secure-" in production when
 * baseURL starts with "https://", so we check both names.
 */
function hasSessionCookie(request: NextRequest): boolean {
  return !!(
    request.cookies.get("better-auth.session_token")?.value ||
    request.cookies.get("__Secure-better-auth.session_token")?.value
  );
}

/**
 * Proxy (formerly middleware) that handles:
 * 1. next-intl locale routing (adds locale prefix)
 * 2. Better Auth session checking
 * 3. User route protection (unified portal for customers and admins)
 *
 * Runtime note: unlike middleware.ts, Proxy files always run on the
 * Node.js runtime in Next.js 16 - there is no Edge option (a
 * `runtime`/`config.runtime` export here throws a build error). This is
 * expected framework behavior, not a bug: see
 * https://nextjs.org/docs/messages/middleware-to-proxy
 */
export default async function proxy(request: NextRequest) {
  const { pathname } = request.nextUrl;

  // Check if this is a user route (matches /<locale>/user/*)
  const isUserRoute = USER_ROUTE_REGEX.test(pathname);
  const isUserSignIn = USER_SIGN_IN_REGEX.test(pathname);
  const isUserAuthSignOut = USER_AUTH_SIGN_OUT_REGEX.test(pathname);

  // =============================================================
  // USER ROUTES - Unified portal for customers and admins
  // =============================================================

  // For user routes (except sign-in, sign-out), check authentication via
  // cookie presence. This includes /user/customers - its admin-only access
  // is enforced server-side (with a real, DB-backed session check) by
  // CustomersPage and adminProcedure, which redirect/reject non-admins.
  // The proxy only needs to gate signed-out visitors here; doing a full
  // session lookup at this layer too just duplicates that enforcement.
  if (
    isUserRoute &&
    !isUserSignIn &&
    !isUserAuthSignOut
  ) {
    if (!hasSessionCookie(request)) {
      const locale = getPathLocale(pathname);
      const signInUrl = new URL(`/${locale}/user/sign-in`, request.url);
      signInUrl.searchParams.set("returnTo", pathname);
      return NextResponse.redirect(signInUrl);
    }

    // User has session cookie - run intl middleware
    const response = intlMiddleware(request) as NextResponse;
    response.headers.set("x-pathname", pathname);
    return response;
  }

  // The sign-in page is deliberately NOT gated here. Bouncing an already-
  // authenticated visitor to the dashboard is the mirror image of the check
  // above, and doing both from cookie presence alone made the pair
  // self-contradictory: a request holding a cookie whose session row is gone
  // was waved into /user/*, redirected back out to /user/sign-in by the real
  // session check in the authenticated layout, and waved straight back in
  // again — ERR_TOO_MANY_REDIRECTS, with no way out but clearing cookies by
  // hand. `revokeAccountKey()` deletes session rows, so that state is now
  // reachable in normal operation.
  //
  // Only one of the two directions can safely run on cookie presence, and it
  // is the one above (a missing cookie is proof of no session; a present one
  // proves nothing). The "already signed in" decision therefore lives in
  // `app/[locale]/user/sign-in/page.tsx`, where a real DB-backed session read
  // is available — the same authority the authenticated layout uses, so the
  // two can no longer disagree. Do not reintroduce a cookie-presence redirect
  // here.

  // For all other routes, just run intl middleware
  const response = intlMiddleware(request) as NextResponse;
  response.headers.set("x-pathname", pathname);

  return response;
}

export const config = {
  // Match all pathnames except for:
  // - API routes (/api/*)
  // - Raw model inventory endpoint (/models)
  // - Next.js internal files (/_next/*)
  // - Static files with extensions (*.*)
  // - Documentation (/docs/*)
  matcher: ["/((?!api|models(?:/|$)|_next|_vercel|docs|.*\\..*).*)"],
};
