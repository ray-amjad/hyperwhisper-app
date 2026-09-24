/**
 * The busy flag's second owner: `src/lib/abandoned-redirect.ts` (#737, review
 * round 1, finding 3).
 *
 * `createBuyCreditsHandler` keeps `loadingTier` set once a checkout URL has
 * been handed to the page's navigation, because assigning `location.href` only
 * SCHEDULES a load and re-arming the buttons underneath it invites a second
 * checkout session for the same top-up. That intent is right and this file
 * does not argue with it. What it covers is the other side: a scheduled load
 * the customer abandons leaves the card dead, and something has to notice.
 *
 * There is no jsdom in this repo, so the page is injected and the test drives
 * a fake one. That is the same reason `buy-credits.ts` takes an injected
 * `fetchImpl` and an injected `navigate`, and it is why the watcher lives in
 * its own module rather than inside the component: a `useEffect` registering a
 * listener on the real page would be code `renderToStaticMarkup` never runs
 * and no test in this repo could reach.
 */
import assert from "node:assert/strict";
import test from "node:test";

import {
  ABANDONED_REDIRECT_MS,
  watchForAbandonedRedirect,
  type RedirectPageShowEvent,
  type RedirectWatchHost,
} from "../src/lib/abandoned-redirect";

/**
 * A stand-in for the page. Records everything the watcher did to it so a test
 * can assert on what was TORN DOWN as well as on what fired — a watcher that
 * released correctly but left its listener and its timer behind would leak one
 * of each per abandoned checkout.
 */
function fakePage() {
  const listeners: Array<(event: RedirectPageShowEvent) => void> = [];
  // A plain array rather than a `Map`: this tsconfig targets ES5 without
  // `downlevelIteration`, so spreading `map.values()` is a TS2802.
  const timers: Array<{ id: number; handler: () => void }> = [];
  const delays: number[] = [];
  const cleared: number[] = [];
  let nextId = 1;

  const host: RedirectWatchHost = {
    addEventListener: (_type, listener) => {
      listeners.push(listener);
    },
    removeEventListener: (_type, listener) => {
      const at = listeners.indexOf(listener);

      if (at >= 0) listeners.splice(at, 1);
    },
    setTimeout: (handler, timeout) => {
      const id = nextId;

      nextId += 1;
      timers.push({ id, handler });
      delays.push(timeout);

      return id;
    },
    clearTimeout: (id) => {
      cleared.push(id);

      const at = timers.findIndex((timer) => timer.id === id);

      if (at >= 0) timers.splice(at, 1);
    },
  };

  return {
    host,
    listeners,
    timers,
    delays,
    cleared,
    /** Fires `pageshow` at every listener still registered. */
    pageshow(persisted: boolean) {
      for (const listener of listeners.slice()) listener({ persisted });
    },
    /** Runs every timer still pending, as the clock reaching them would. */
    tick() {
      for (const timer of timers.slice()) timer.handler();
    },
  };
}

test("a scheduled redirect re-arms nothing on its own", async () => {
  const page = fakePage();
  const released: number[] = [];

  watchForAbandonedRedirect(page.host, () => released.push(1));

  // The control for every test below, and the half of the rule #881 round 1
  // bought: while the navigation may still commit, the buttons stay disarmed.
  assert.deepEqual(released, []);
  assert.equal(page.listeners.length, 1);
  assert.equal(page.timers.length, 1);
  // The documented delay, and not some other number invented at the call site.
  assert.deepEqual(page.delays, [ABANDONED_REDIRECT_MS]);
});

test("Back from Stripe out of the bfcache re-arms the card at once", async () => {
  const page = fakePage();
  const released: number[] = [];

  watchForAbandonedRedirect(page.host, () => released.push(1));
  // `persisted` true is a document that came back with its React state — and
  // so with its `loadingTier` — exactly as it was before the redirect. On
  // `main` the card's unconditional `finally` covered this; this branch would
  // otherwise leave all four buttons dead until a reload.
  page.pageshow(true);

  assert.deepEqual(released, [1]);
  // …and it tore itself down rather than waiting for the timer too.
  assert.equal(page.listeners.length, 0);
  assert.equal(page.cleared.length, 1);
});

test("an ordinary page load re-arms nothing", async () => {
  const page = fakePage();
  const released: number[] = [];

  watchForAbandonedRedirect(page.host, () => released.push(1));
  // `persisted` false is a FRESH document. Its `loadingTier` is already null,
  // there is nothing to release, and a watcher that acted on it would fire on
  // every ordinary navigation into the dashboard. Drop the `event.persisted`
  // test in the module and this is the assertion that dies.
  page.pageshow(false);

  assert.deepEqual(released, []);
  assert.equal(page.listeners.length, 1);
});

test("a navigation that never happened re-arms the card when the clock runs out", async () => {
  const page = fakePage();
  const released: number[] = [];

  watchForAbandonedRedirect(page.host, () => released.push(1));
  // Escape, or a Stripe host that never answers. Neither produces a single
  // page-lifecycle event — the document is exactly as it was — so the timer is
  // the only thing that can tell them from a load about to commit.
  page.tick();

  assert.deepEqual(released, [1]);
  assert.equal(page.listeners.length, 0);
});

test("the release runs once, whichever of the two arrives first", async () => {
  const page = fakePage();
  const released: number[] = [];

  watchForAbandonedRedirect(page.host, () => released.push(1));
  page.pageshow(true);
  // A bfcache restore freezes the timer rather than dropping it, so the
  // resumed clock really does arrive second in production. A second release is
  // a second `setLoadingTier(null)`, which could clear a flag belonging to a
  // checkout the customer started after coming back.
  //
  // Once-ness here is a property of the TEARDOWN, not of a `released` flag: a
  // flag was written first and the mutation harness proved it unreachable,
  // because both sources are disarmed before `release` is called. So this
  // asserts the teardown too, and the two assertions below are what rows `n`
  // and `r` kill.
  page.tick();
  page.pageshow(true);

  assert.deepEqual(released, [1]);
  assert.equal(page.listeners.length, 0);
  assert.deepEqual(page.cleared.length, 1);
});

test("two abandoned checkouts each get their own watch", async () => {
  const page = fakePage();
  const released: string[] = [];

  watchForAbandonedRedirect(page.host, () => released.push("first"));
  page.tick();

  watchForAbandonedRedirect(page.host, () => released.push("second"));
  page.tick();

  // A card is clicked more than once in a session, and each watch must clean
  // up after itself: a listener that was never spliced out and a timer that
  // was never cleared would accumulate one of each per abandoned checkout.
  assert.deepEqual(released, ["first", "second"]);
  assert.equal(page.listeners.length, 0);
  assert.equal(page.timers.length, 0);
});

test("the caller may shorten the delay, and the default is the constant", async () => {
  const page = fakePage();

  watchForAbandonedRedirect(page.host, () => {}, 250);

  assert.deepEqual(page.delays, [250]);
  // Stated here so the number the card really ships with is pinned by a test
  // and not only by a comment: 20 seconds is far past any commit-bound TTFB,
  // and short enough that a customer who pressed Escape is not stranded.
  assert.equal(ABANDONED_REDIRECT_MS, 20_000);
});
