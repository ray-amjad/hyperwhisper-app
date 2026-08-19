import { redirect } from "next/navigation";

import SignInClient from "./SignInClient";

import { getPortalSession } from "@/src/lib/auth";
import { sanitizeReturnTo } from "@/src/lib/license-key-redirect";

/**
 * User Sign-In page.
 *
 * The only job of this Server Component is the "already signed in?" decision,
 * and it makes that decision against the DB-backed session. That is what makes
 * the portal's redirect loop structurally impossible.
 *
 * `proxy.ts` used to make this call from cookie *presence* alone, which turned
 * any "cookie survives, session row does not" state into `ERR_TOO_MANY_
 * REDIRECTS`: the proxy waved the request through to `/user/dashboard`, the
 * authenticated layout's real session read came back null and redirected to
 * `/user/sign-in`, and the proxy saw the same still-present cookie and sent it
 * straight back. That state is now reachable on purpose — `revokeAccountKey()`
 * deletes session rows, and a Server Component cannot emit the clearing
 * `Set-Cookie` that Better Auth generates (`next-js.mjs` swallows the write) —
 * so the loop had to be closed at the source rather than made unlikely.
 *
 * Both ends of the bounce now read the same authority. If the session is live,
 * both agree the user belongs in the portal; if it is gone, both agree the user
 * belongs here, and this page renders the form instead of bouncing. There is no
 * input for which they can disagree, so there is no cycle. `proxy.ts` keeps its
 * cookie-presence check only in the direction that cannot loop: signed-out
 * visitors out of `/user/*`.
 */
export default async function UserSignInPage({
  params,
  searchParams,
}: {
  params: Promise<{ locale: string }>;
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const { locale } = await params;
  const { returnTo } = await searchParams;

  const session = await getPortalSession();

  if (session?.user) {
    const fallback = `/${locale}/user/dashboard`;
    const target = sanitizeReturnTo(
      typeof returnTo === "string" ? returnTo : null,
      fallback,
    );
    // `sanitizeReturnTo` only blocks off-origin targets, so `?returnTo=` can
    // still name this very page. Redirecting there would re-enter this branch;
    // send it to the dashboard instead so the hop count is bounded at one.

    redirect(target.includes("/user/sign-in") ? fallback : target);
  }

  return <SignInClient />;
}
