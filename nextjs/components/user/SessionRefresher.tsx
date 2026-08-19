"use client";

import { authClient } from "@/src/lib/auth-client";

/**
 * Session Refresher
 *
 * Renders nothing. It exists only to move one session read out of the server
 * render and into a route handler, so the rolling session cookie can actually
 * be re-issued.
 *
 * Every other session read in the portal is `auth.api.getSession()` inside a
 * Server Component, and Next.js forbids writing cookies during an RSC render —
 * Better Auth's rolling refresh silently fails there, so the session cookie
 * kept its original Max-Age and users were signed out on a hard deadline.
 *
 * `authClient.useSession()` fetches `/api/auth/get-session` from the browser
 * instead. That is a route handler, where `Set-Cookie` is legal, so the
 * refreshed cookie reaches the browser. One fetch per hard page load is plenty
 * against a one-day `updateAge`, so there is deliberately no polling.
 *
 * Do not delete this because it "renders nothing" — that is the point.
 */
export default function SessionRefresher() {
  authClient.useSession();

  return null;
}
