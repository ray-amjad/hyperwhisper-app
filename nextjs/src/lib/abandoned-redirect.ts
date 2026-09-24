/**
 * The second owner of the buy-credits busy flag (#737, review round 1;
 * narrowed in #947 review round 2, finding 1).
 *
 * `createBuyCreditsHandler` releases the flag on every exit that leaves the
 * customer on the dashboard. It deliberately does NOT release it on the one
 * exit that does not: a checkout URL handed to the page's navigation, which
 * only SCHEDULES a cross-origin load. Clearing it there would re-arm all four
 * buttons while the load is in flight and invite a second checkout session for
 * the same top-up — the regression `sign-out.ts:93-101` and #881 round 1 both
 * record.
 *
 * But a scheduled load is not a load that happens. The customer can come Back
 * from Stripe with this document restored out of the bfcache and its React
 * state intact: the page is still here, still interactive, and every tier
 * button, the custom toggle and the amount input are dead with no recovery but
 * a reload. That is what this watcher is for, and it releases on exactly ONE
 * signal:
 *
 *   a `pageshow` whose `persisted` flag is set — the bfcache restore, which is
 *   the only page-lifecycle event a returning customer produces with the old
 *   React state still mounted. A `pageshow` with `persisted` false is a fresh
 *   document whose `loadingTier` is already `null`, so it must be ignored or
 *   the watcher fires on every ordinary load.
 *
 * WHAT WAS HERE BEFORE, AND WHY IT IS GONE. Review round 1 also ran a 20-second
 * timeout beside the listener, so that the two abandonments which produce no
 * event at all — Escape, and a Stripe host that never answers — were covered
 * too. Round 2 removed it. Assigning `location.href` leaves this document live
 * and interactive until the new response's first byte, so on a connection
 * where `checkout.stripe.com` takes longer than the timeout to answer, the
 * timer fired and re-armed all four controls WHILE THE REAL REDIRECT WAS STILL
 * IN FLIGHT. Nothing cancels a scheduled navigation's timer when it commits,
 * and nothing in a browser tells this document the difference between a load
 * that is slow and a load that is never coming. A second click there POSTs
 * again and creates a second checkout session for the same top-up.
 *
 * So the timer was a stuck spinner traded for a money-path fault, which is the
 * wrong direction. What it covered now keeps the stuck spinner: a customer who
 * presses Escape, or whose Stripe host is unreachable, has a dead card until
 * they reload. That is the cost, it is stated in the PR body, and it is
 * strictly less bad than a duplicate session. Back-from-Stripe — the common
 * abandonment, and the only one with a real signal — is still covered here.
 *
 * Do not add a timer back. A release source that can fire while a navigation
 * can still commit needs a re-entrancy guard the seam does not have and cannot
 * easily get: `createBuyCreditsHandler` is called in the card's render body and
 * re-allocated on every render, so a closure flag would reset the moment the
 * release triggered a re-render.
 *
 * Every collaborator is INJECTED rather than reached for as a global, for the
 * same reason `buy-credits.ts` injects `fetch` and the navigation: this repo
 * has no jsdom, so a watcher that read the page's globals itself would be a
 * watcher no test could drive. `tests/abandoned-redirect.test.ts` drives the
 * whole of it through a fake host.
 */

/** The `pageshow` payload, narrowed to the one field that matters. */
export interface RedirectPageShowEvent {
  /**
   * True when this document came back out of the bfcache rather than being
   * built fresh — which is exactly the case in which its `loadingTier` is
   * still set from before the redirect.
   */
  persisted: boolean;
}

/**
 * The slice of the page the watcher uses. A real browser page satisfies it,
 * and so does the plain object literal in the test.
 *
 * It holds no `setTimeout` and no `clearTimeout` on purpose — see the note
 * above. Widening it back is the first half of putting the double-session
 * window back.
 */
export interface RedirectWatchHost {
  addEventListener: (
    type: "pageshow",
    listener: (event: RedirectPageShowEvent) => void,
  ) => void;
  removeEventListener: (
    type: "pageshow",
    listener: (event: RedirectPageShowEvent) => void,
  ) => void;
}

/**
 * Runs `release` if, and only if, this document came back out of the bfcache
 * after the scheduled navigation took it away. Runs it at most once. Returns
 * nothing: the card has no use for a handle, and the watcher tears itself down.
 */
export function watchForAbandonedRedirect(
  host: RedirectWatchHost,
  release: () => void,
): void {
  function finish(): void {
    // The listener is removed BEFORE `release` runs, and that order is what
    // makes the release at-most-once: a second bfcache restore in the same
    // session must not clear a flag belonging to a checkout the customer
    // started after coming back.
    //
    // A `released` boolean was written here first and then removed: the
    // mutation harness proved it unreachable, because no path can reach
    // `finish` after this line has run. The line IS the guard, and row `n` of
    // `tests/mutate-cloud-credits-card.mjs` is what holds it. Anyone adding a
    // SECOND release source has to re-measure that — and read the note above
    // first, because a second source is how the double-session window gets
    // back in.
    host.removeEventListener("pageshow", onPageShow);

    release();
  }

  const onPageShow = (event: RedirectPageShowEvent): void => {
    // `persisted` is the whole guard. A fresh document fires this too, and
    // acting on it would clear a flag belonging to a card that never set one.
    if (event.persisted) finish();
  };

  host.addEventListener("pageshow", onPageShow);
}
