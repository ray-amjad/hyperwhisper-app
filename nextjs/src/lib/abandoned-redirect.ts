/**
 * The second owner of the buy-credits busy flag (#737, review round 1).
 *
 * `createBuyCreditsHandler` releases the flag on every exit that leaves the
 * customer on the dashboard. It deliberately does NOT release it on the one
 * exit that does not: a checkout URL handed to the page's navigation, which
 * only SCHEDULES a cross-origin load. Clearing it there would re-arm all four
 * buttons while the load is in flight and invite a second checkout session for
 * the same top-up — the regression `sign-out.ts:93-101` and #881 round 1 both
 * record.
 *
 * But a scheduled load is not a load that happens. The customer can press
 * Escape, the Stripe host can be slow or unreachable, or they can come Back
 * from Stripe with this document restored out of the bfcache and its React
 * state intact. In all three the page is still here, still interactive, and
 * every tier button, the custom toggle and the amount input are dead with no
 * recovery but a reload. On `main` the card's unconditional
 * `finally { setLoadingTier(null) }` covered all three, at the cost of the
 * double-session window above.
 *
 * This watcher is what makes both true at once. It holds the release until one
 * of two things says the load is not going to take the document away:
 *
 * 1. a `pageshow` whose `persisted` flag is set — the bfcache restore, which
 *    is the ONLY page-lifecycle event a returning customer produces with the
 *    old React state still mounted. A `pageshow` with `persisted` false is a
 *    fresh document whose `loadingTier` is already `null`, so it must be
 *    ignored or the watcher fires on every ordinary load;
 * 2. a timeout, for the two cases that produce no event at all. Escape and an
 *    unreachable host leave the document exactly as it was, so nothing but the
 *    passage of time distinguishes them from a load that is about to commit.
 *
 * Whichever arrives first wins, once, and drops the other.
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
  setTimeout: (handler: () => void, timeout: number) => number;
  clearTimeout: (id: number) => void;
}

/**
 * How long a scheduled navigation is given before the card assumes it is not
 * happening.
 *
 * The number is a trade between two costs, and neither is zero. Too short and
 * a slow-but-real redirect re-arms the buttons underneath a customer whose
 * Stripe session is still loading, which is the double-session risk the
 * asymmetric busy flag exists to prevent. Too long and a customer who pressed
 * Escape sits in front of a dead card. 20 seconds is far past any TTFB a
 * commit-bound navigation takes, and it is an annoyance rather than a lost
 * sale on the other side.
 */
export const ABANDONED_REDIRECT_MS = 20_000;

/**
 * Runs `release` if, and only if, the scheduled navigation did not take this
 * document away. Runs it at most once. Returns nothing: the card has no use
 * for a handle, and the watcher tears itself down.
 */
export function watchForAbandonedRedirect(
  host: RedirectWatchHost,
  release: () => void,
  timeoutMs: number = ABANDONED_REDIRECT_MS,
): void {
  let timer: number | undefined;

  function finish(): void {
    // BOTH sources are disarmed BEFORE `release` runs, and that order is what
    // makes the release at-most-once. A bfcache restore is followed by the
    // resumed timer — the freeze suspends it rather than dropping it — so
    // without the `clearTimeout` the card would be re-armed a second time,
    // possibly clearing a flag set by a checkout started after coming back.
    //
    // A `released` boolean was written here first and then removed: the
    // mutation harness proved it unreachable, because no path can reach
    // `finish` after these two lines have run. The two lines are the guard,
    // and rows `n` and `r` of `tests/mutate-cloud-credits-card.mjs` are what
    // hold them. Anyone adding a THIRD source has to re-measure that.
    host.removeEventListener("pageshow", onPageShow);

    if (timer !== undefined) host.clearTimeout(timer);

    release();
  }

  const onPageShow = (event: RedirectPageShowEvent): void => {
    // `persisted` is the whole guard. A fresh document fires this too, and
    // acting on it would clear a flag belonging to a card that never set one.
    if (event.persisted) finish();
  };

  host.addEventListener("pageshow", onPageShow);
  // Scheduled LAST, so a host whose `setTimeout` throws still leaves the
  // listener registered rather than half a watcher.
  timer = host.setTimeout(finish, timeoutMs);
}
