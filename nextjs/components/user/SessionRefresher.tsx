"use client";

import { authClient } from "@/src/lib/auth-client";

/**
 * Session Refresher
 *
 * Renders nothing. Its only job is to guarantee that every hard page load in
 * the portal performs one session read from a place where `Set-Cookie` is
 * legal, so the rolling 90-day session cookie is re-issued and an active user
 * is never signed out.
 *
 * `authClient.useSession()` fetches `/api/auth/get-session` from the browser.
 * That is a route handler, so Better Auth's refreshed `Set-Cookie` reaches the
 * browser intact. Server Component reads cannot do this — Next.js forbids
 * writing cookies during an RSC render — which is why they go through
 * `getServerComponentSession()` and deliberately skip the refresh entirely.
 *
 * Why this component, when the tRPC handler also rolls the session: it does
 * (`server/api/trpc.ts` calls `auth.api.getSession` from a route handler), and
 * today all three pages under `(authenticated)` happen to fire an ungated
 * `useQuery` on mount. But that is a property of what each page needs to
 * *display*, not of authentication. Add a page with no client-side data, or
 * gate an existing query behind `enabled`, and the session silently stops
 * rolling — a failure nobody notices for 90 days. This component is mounted in
 * the segment layout, so the guarantee holds for every page in the segment,
 * now and later.
 *
 * Refetch behaviour: Better Auth's client does no interval polling by default
 * (`sessionOptions.refetchInterval` defaults to 0), but it *does* refetch on
 * window focus (rate-limited to once per 5s), on regaining network, and when
 * another tab broadcasts a session change. Those are left on deliberately —
 * each is a free extra chance to roll the cookie for a long-lived open tab.
 *
 * Do not delete this because it "renders nothing" — that is the point.
 */
export default function SessionRefresher() {
  authClient.useSession();

  return null;
}
