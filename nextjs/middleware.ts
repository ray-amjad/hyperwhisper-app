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
 * Get full Better Auth session via API call.
 * Only used for admin routes that need the user's email.
 */
async function getBetterAuthSession(request: NextRequest) {
  const sessionCookie =
    request.cookies.get("__Secure-better-auth.session_token") ||
    request.cookies.get("better-auth.session_token");
  if (!sessionCookie?.value) return null;

  try {
    const baseUrl = request.nextUrl.origin;
    const response = await fetch(`${baseUrl}/api/auth/get-session`, {
      headers: {
        cookie: request.headers.get("cookie") || "",
      },
    });

    if (!response.ok) return null;

    const data = await response.json();
    return data?.user ?? null;
  } catch {
    return null;
  }
}

/**
 * Middleware that handles:
 * 1. next-intl locale routing (adds locale prefix)
 * 2. Better Auth session checking
 * 3. User route protection (unified portal for customers and admins)
 */
export default async function middleware(request: NextRequest) {
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
