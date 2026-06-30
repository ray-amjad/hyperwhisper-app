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
) {
  const { locale } = await params;

  // Revoke session via Better Auth and capture set-cookie header
  const signOutResponse = await auth.api.signOut({
    headers: request.headers,
    asResponse: true,
  });

  const redirect = NextResponse.redirect(
    new URL(`/${locale}/user/sign-in`, request.url)
  );

  // Forward the session-clearing cookie from Better Auth's response
  const setCookie = signOutResponse.headers.get("set-cookie");
  if (setCookie) {
    redirect.headers.set("set-cookie", setCookie);
  } else {
    // Fallback: manually clear the session cookie
    redirect.headers.set(
      "set-cookie",
      "better-auth.session_token=; Max-Age=0; Path=/; HttpOnly; SameSite=Lax"
    );
  }
}
