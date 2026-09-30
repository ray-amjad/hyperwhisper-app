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

  // Forward every session-clearing cookie from Better Auth's response, one
  // Set-Cookie header each. `get("set-cookie")` would comma-join them into a
  // single header, and a browser applies only the first cookie in it.
  const setCookies = signOutResponse.headers.getSetCookie();

  if (setCookies.length > 0) {
    for (const cookie of setCookies) {
      redirect.headers.append("set-cookie", cookie);
    }
  } else {
    // Fallback: manually clear the session cookie
    redirect.headers.set(
      "set-cookie",
      "better-auth.session_token=; Max-Age=0; Path=/; HttpOnly; SameSite=Lax"
    );
  }

  return redirect;
}
