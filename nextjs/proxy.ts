import createMiddleware from "next-intl/middleware";
import { NextRequest, NextResponse } from "next/server";

import { routing } from "./src/i18n/routing";
import { defaultLocale, locales } from "./src/i18n/locales";
import { sanitizeReturnTo } from "./src/lib/license-key-redirect";

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
const USER_CUSTOMERS_REGEX = new RegExp(`^\\/(${localePattern})\\/user\\/customers`);

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
 * Get full Better Auth session via an in-process call to Better Auth.
 * Only used for admin routes that need the user's email.
 *
 * This calls `auth.api.getSession()` directly instead of making a
 * self-referential HTTP fetch back to `/api/auth/get-session` - the
 * previous implementation could silently treat a valid session as
 * "not logged in" whenever that self-fetch hit a network hiccup,
 * timeout, or non-2xx response.
 *
 * The `auth` module is imported lazily (dynamic `import()`) so its
 * transitive dependencies - a Drizzle DB connection pool, the Resend
 * email client, and full server-env validation - only get pulled into
 * the module graph for requests that actually reach `/user/customers`,
 * not every page-route request that passes through this proxy.
 *
 * Any exception thrown by `getSession` (e.g. an `APIError` from a
 * session-update race, a DB hiccup, or a corrupted session-cache
 * cookie) is caught and treated as "not logged in", restoring the
 * graceful-degradation behavior of the old self-fetch implementation
 * for genuine failures.
 */
async function getBetterAuthSession(request: NextRequest) {
  try {
    const { auth } = await import("./src/lib/auth");
    const session = await auth.api.getSession({ headers: request.headers });
    return session?.user ?? null;
  } catch {
    return null;
  }
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
  const isUserCustomers = USER_CUSTOMERS_REGEX.test(pathname);

  // =============================================================
  // USER ROUTES - Unified portal for customers and admins
  // =============================================================

  // For /user/customers, require admin access
  if (isUserCustomers) {
    const user = await getBetterAuthSession(request);

    // If not authenticated, redirect to user sign-in
    if (!user) {
      const locale = getPathLocale(pathname);
      const signInUrl = new URL(`/${locale}/user/sign-in`, request.url);
      signInUrl.searchParams.set("returnTo", pathname);
      return NextResponse.redirect(signInUrl);
    }

    // Must be admin to access customers page
    if (user.role !== "admin") {
      const locale = getPathLocale(pathname);
      return NextResponse.redirect(
        new URL(`/${locale}/user/dashboard`, request.url)
      );
    }

    // User is authenticated and is admin - run intl middleware
    const response = intlMiddleware(request) as NextResponse;
    response.headers.set("x-pathname", pathname);
    return response;
  }

  // For other user routes (except sign-in, sign-out), check authentication via cookie presence
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

  // For user sign-in page, check if already authenticated
  if (isUserSignIn) {
    if (hasSessionCookie(request)) {
      const locale = getPathLocale(pathname);
      const returnTo = request.nextUrl.searchParams.get("returnTo");
      const redirectUrl = sanitizeReturnTo(returnTo, `/${locale}/user/dashboard`);
      return NextResponse.redirect(new URL(redirectUrl, request.url));
    }

    const response = intlMiddleware(request) as NextResponse;
    response.headers.set("x-pathname", pathname);
    return response;
  }

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
