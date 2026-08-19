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
 * Under `session.deferSessionRefresh` (see `src/lib/auth.ts`) that GET performs
 * no writes and comes back with `needsRefresh`, on which Better Auth's client
 * immediately re-requests the same endpoint with POST — the only method that
 * takes the write branch. Both hit a route handler, so the re-issued
 * `Set-Cookie` reaches the browser intact.
 *
 * This is now the ONLY thing that rolls the session. Server-side reads
 * (`server/api/trpc.ts`, `app/api/customer/profile/route.ts`, every Server
 * Component) all go through `getSessionFromCtx`, which hard-codes `method:
 * "GET"`, so none of them refresh anything. That is deliberate — it is what
 * makes it impossible for an RSC read to consume the refresh window and strand
 * the cookie — but it does mean deleting this component silently reverts the
 * portal to a hard 90-day deadline. It is mounted in the segment layout, so
 * the guarantee holds for every page in the segment, now and later, without
 * depending on any page happening to fire a client query.
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
