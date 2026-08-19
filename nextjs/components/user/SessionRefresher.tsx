"use client";

import { useEffect } from "react";

import { authClient } from "@/src/lib/auth-client";

/**
 * Session Refresher
 *
 * Renders nothing, and does two jobs — one per direction of the session's life.
 *
 * 1. ROLL IT FORWARD. Every hard page load in the portal must perform one
 *    session read from a place where `Set-Cookie` is legal, or the rolling
 *    90-day cookie is never re-issued and an active user is signed out on a
 *    hard deadline.
 *
 *    `authClient.useSession()` fetches `/api/auth/get-session` from the
 *    browser. Under `session.deferSessionRefresh` (see `src/lib/auth.ts`) that
 *    GET performs no writes and comes back with `needsRefresh`, on which
 *    Better Auth's client immediately re-requests the same endpoint with POST —
 *    the only method that takes the write branch. Both hit a route handler, so
 *    the re-issued `Set-Cookie` reaches the browser intact.
 *
 *    That POST only works because `src/lib/auth-client.ts` gives it a body and
 *    a `Content-Type`. Better Auth sends neither, and its own router rejects
 *    the result with 415 while its client swallows the error — read the comment
 *    there before changing either file.
 *
 *    This is now the ONLY thing that rolls the session. Server-side reads
 *    (`server/api/trpc.ts`, `app/api/customer/profile/route.ts`, every Server
 *    Component) all go through `getSessionFromCtx`, which hard-codes
 *    `method: "GET"`, so none of them refresh anything. That is deliberate — it
 *    is what makes it impossible for an RSC read to consume the refresh window
 *    and strand the cookie — but it does mean deleting this component silently
 *    reverts the portal to a hard 90-day deadline. It is mounted in the segment
 *    layout, so the guarantee holds for every page in the segment, now and
 *    later, without depending on any page happening to fire a client query.
 *
 * 2. NOTICE WHEN IT IS GONE. Sessions are now deleted server-side
 *    (`revokeAccountKey()`), and an already-rendered tab has no way to learn
 *    that: the page was server-rendered when the session was still live, and
 *    nothing re-renders it. An admin's open `/user/customers` would keep
 *    showing the sidebar and the grant / addCredits / refund controls after
 *    their access was revoked. The mutations themselves are safe — every one
 *    goes through `adminProcedure`, which re-reads the session from the DB —
 *    but the UI would be lying, and there is no global 401 handler on the tRPC
 *    client to catch it.
 *
 *    So this component acts on the result instead of discarding it. A settled,
 *    error-free `null` is the server saying the session no longer exists; the
 *    same response also carries Better Auth's clearing `Set-Cookie`, so by the
 *    time we navigate, the stale cookie is already gone. The navigation is a
 *    full document load precisely so the proxy and the server re-evaluate from
 *    scratch.
 *
 *    Note the guard is `!isPending && !error && !data`, not just `!data`.
 *    `data` is null while the first fetch is in flight, and Better Auth's
 *    client preserves the previous session on a transient fetch failure
 *    (`dist/client/session-atom.mjs`) — neither is a revocation, and treating
 *    them as one would sign people out on a flaky connection.
 *
 * Refetch behaviour: Better Auth's client does no interval polling by default
 * (`sessionOptions.refetchInterval` defaults to 0), but it *does* refetch on
 * window focus (rate-limited to once per 5s), on regaining network, and when
 * another tab broadcasts a session change. Those are left on deliberately —
 * each is a free extra chance to roll the cookie, and each is also when a
 * revoked tab gets noticed.
 *
 * Do not delete this because it "renders nothing" — that is the point.
 */
export default function SessionRefresher({ locale }: { locale: string }) {
  const { data, isPending, error } = authClient.useSession();

  const sessionIsGone = !isPending && !error && !data;

  useEffect(() => {
    if (!sessionIsGone) return;

    const returnTo = window.location.pathname + window.location.search;
    const signInUrl = `/${locale}/user/sign-in?returnTo=${encodeURIComponent(returnTo)}`;

    window.location.replace(signInUrl);
  }, [sessionIsGone, locale]);

  return null;
}
