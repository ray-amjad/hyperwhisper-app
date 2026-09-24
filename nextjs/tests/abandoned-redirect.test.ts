/**
 * The busy flag's second owner: `src/lib/abandoned-redirect.ts` (#737, review
 * round 1, finding 3; narrowed in #947 review round 2, finding 1).
 *
 * `createBuyCreditsHandler` keeps `loadingTier` set once a checkout URL has
 * been handed to the page's navigation, because assigning `location.href` only
 * SCHEDULES a load and re-arming the buttons underneath it invites a second
 * checkout session for the same top-up. That intent is right and this file
 * does not argue with it. What it covers is the other side: a scheduled load
 * the customer abandons leaves the card dead, and something has to notice.
 *
 * ONE signal does the noticing, and round 2 deleted the other. A 20-second
 * timeout used to run beside the listener so that Escape and an unreachable
 * Stripe host were covered too — and it fired on a slow-but-real redirect as
 * well, re-arming every control while the navigation was still in flight. The
 * tests for it are gone with it, and the two tests at the bottom of this file
 * are what say so: nothing but a bfcache restore releases the flag.
 *
 * There is no jsdom in this repo, so the page is injected and the test drives
 * a fake one. That is the same reason `buy-credits.ts` takes an injected
 * `fetchImpl` and an injected `navigate`, and it is why the watcher lives in
 * its own module rather than inside the component: a `useEffect` registering a
 * listener on the real page would be code `renderToStaticMarkup` never runs
 * and no test in this repo could reach.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { fileURLToPath } from "node:url";

import {
  watchForAbandonedRedirect,
  type RedirectPageShowEvent,
  type RedirectWatchHost,
} from "../src/lib/abandoned-redirect";

/**
 * A stand-in for the page. Records everything the watcher did to it so a test
 * can assert on what was TORN DOWN as well as on what fired — a watcher that
 * released correctly but left its listener behind would leak one per abandoned
 * checkout.
 *
 * It also carries a `setTimeout` and a `clearTimeout` the host type no longer
 * declares, so that the "arms no clock" test below is driven by something the
 * watcher COULD have called rather than by an absence the fake made certain.
 */
function fakePage() {
  const listeners: Array<(event: RedirectPageShowEvent) => void> = [];
  const timers: Array<{ id: number; handler: () => void }> = [];
  let nextId = 1;

  const host: RedirectWatchHost & {
    setTimeout: (handler: () => void, timeout: number) => number;
    clearTimeout: (id: number) => void;
  } = {
    addEventListener: (_type, listener) => {
      listeners.push(listener);
    },
    removeEventListener: (_type, listener) => {
      const at = listeners.indexOf(listener);

      if (at >= 0) listeners.splice(at, 1);
    },
    setTimeout: (handler) => {
      const id = nextId;

      nextId += 1;
      timers.push({ id, handler });

      return id;
    },
    clearTimeout: (id) => {
      const at = timers.findIndex((timer) => timer.id === id);

      if (at >= 0) timers.splice(at, 1);
    },
  };

  return {
    host,
    listeners,
    timers,
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
});

test("Back from Stripe out of the bfcache re-arms the card at once", async () => {
  const page = fakePage();
  const released: number[] = [];

  watchForAbandonedRedirect(page.host, () => released.push(1));
  // `persisted` true is a document that came back with its React state — and
  // so with its `loadingTier` — exactly as it was before the redirect. On
  // `main` the card's unconditional `finally` covered this; this branch would
  // otherwise leave all four buttons dead until a reload. It is also the ONLY
  // abandonment this watcher covers, by choice — see the note at the top.
  page.pageshow(true);

  assert.deepEqual(released, [1]);
  // …and it tore itself down.
  assert.equal(page.listeners.length, 0);
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

test("the release runs once, however many times the customer comes back", async () => {
  const page = fakePage();
  const released: number[] = [];

  watchForAbandonedRedirect(page.host, () => released.push(1));
  page.pageshow(true);
  // A second restore in the same session — Back, forward, Back again. A second
  // release is a second `setLoadingTier(null)`, which could clear a flag
  // belonging to a checkout the customer started after coming back.
  //
  // Once-ness here is a property of the TEARDOWN, not of a `released` flag: a
  // flag was written first and the mutation harness proved it unreachable,
  // because the listener is removed before `release` is called. So this
  // asserts the teardown too, and that is what row `n` kills.
  page.pageshow(true);

  assert.deepEqual(released, [1]);
  assert.equal(page.listeners.length, 0);
});

test("two abandoned checkouts each get their own watch", async () => {
  const page = fakePage();
  const released: string[] = [];

  watchForAbandonedRedirect(page.host, () => released.push("first"));
  page.pageshow(true);

  watchForAbandonedRedirect(page.host, () => released.push("second"));
  page.pageshow(true);

  // A card is clicked more than once in a session, and each watch must clean
  // up after itself: a listener that was never spliced out would accumulate
  // one per abandoned checkout, and the first watch's release would fire again
  // on the second restore.
  assert.deepEqual(released, ["first", "second"]);
  assert.equal(page.listeners.length, 0);
});

/**
 * #947 review round 2, finding 1. The two tests below are the deletion itself,
 * stated as assertions rather than as a comment — a timer put back here is the
 * double-session window put back, and neither an absence nor a prose note can
 * be trusted to hold that shut.
 */

test("the watcher arms no clock, so nothing fires while a redirect is in flight", async () => {
  const page = fakePage();
  const released: number[] = [];

  // The fake page HAS a working `setTimeout`, so this is the watcher declining
  // to use one and not the fake making it impossible.
  watchForAbandonedRedirect(page.host, () => released.push(1));

  assert.equal(page.timers.length, 0);

  // Every clock in the world running out changes nothing: assigning
  // `location.href` leaves this document live until the new response's first
  // byte, and a release on a timer cannot tell a slow redirect from an
  // abandoned one. On a slow connection the old 20-second timer re-armed all
  // four controls mid-navigation, and a second click created a second Stripe
  // session for the same top-up.
  page.tick();

  assert.deepEqual(released, []);
  assert.equal(page.listeners.length, 1);
});

test("the watcher's source holds no timer at all", async () => {
  // The source text is the assertion, the way `buy-credits-seam.test.ts:321`
  // holds the browser globals out of the seam. A timer added back would be
  // invisible to every test above — they drive the watcher through a host that
  // records only what it is asked for, and a watcher that reached for the
  // global `setTimeout` instead would pass all of them.
  const source = readFileSync(
    fileURLToPath(new URL("../src/lib/abandoned-redirect.ts", import.meta.url)),
    "utf8",
  );

  // A positive control first, so the assertions below cannot pass because the
  // file was read from the wrong path.
  assert.match(source, /watchForAbandonedRedirect/);

  // Comments are stripped before the match: the note at the top of the module
  // explains at length why the timer is gone, and it has to be able to say the
  // words. This is the one place in the repo where the prose and the code
  // would otherwise fight.
  const code = source
    .replace(/\/\*[\s\S]*?\*\//g, "")
    .replace(/^[ \t]*\/\/.*$/gm, "");

  assert.match(code, /watchForAbandonedRedirect/);
  assert.equal(/setTimeout/.test(code), false);
  assert.equal(/clearTimeout/.test(code), false);
  assert.equal(/setInterval/.test(code), false);
  assert.equal(/requestAnimationFrame/.test(code), false);
});
