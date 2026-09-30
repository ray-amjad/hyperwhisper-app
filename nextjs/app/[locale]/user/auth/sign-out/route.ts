import { NextRequest, NextResponse } from "next/server";
import { auth } from "@/src/lib/auth";

/**
 * User Sign Out
 *
 * Signs the user out via Better Auth and redirects to sign-in page.
 */
export async function POST(
  request: NextRequest,
  { params }: { params: Promise<{ locale: string }> }
): Promise<NextResponse> {
  const { locale } = await params;

  // Revoke session via Better Auth and capture set-cookie header
  const signOutResponse = await auth.api.signOut({
    headers: request.headers,
    asResponse: true,
  });

  const redirect = NextResponse.redirect(
    new URL(`/${locale}/user/sign-in`, request.url)
  );

  // Do not copy Better Auth's Set-Cookie headers: nextCookies() already put them
  // in Next's cookie store, which Next merges into this response. A hand copy
  // wins that merge and loses `Max-Age=0`, so the browser keeps the cookie.
  if (signOutResponse.headers.getSetCookie().length === 0) {
    // Fallback: manually clear the session cookie
    redirect.headers.set(
      "set-cookie",
      "better-auth.session_token=; Max-Age=0; Path=/; HttpOnly; SameSite=Lax"
    );
  }

  return redirect;
}
