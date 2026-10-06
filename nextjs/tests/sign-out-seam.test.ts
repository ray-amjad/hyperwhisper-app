import assert from "node:assert/strict";
import test from "node:test";

import {
  createSignOutHandler,
  SIGN_OUT_ERROR_MESSAGE,
  signOutAndRedirect,
  type SignOutResult,
} from "../src/lib/sign-out";
import {
  watchForAbandonedRedirect,
  type RedirectPageShowEvent,
  type RedirectWatchHost,
} from "../src/lib/abandoned-redirect";

/**
 * The seam `UserHeader` now calls. Records every collaborator call so a test
 * can assert on what did NOT happen — the whole of #870 is a `navigate` that
 * fired after a sign-out the server refused.
 */
function harness(signOut: () => Promise<SignOutResult>) {
  const navigated: string[] = [];
  const errors: string[] = [];
  const logged: unknown[][] = [];

  async function run(redirectTo = "/fr/user/sign-in") {
    const quiet = console.error;
    console.error = (...args: unknown[]) => {
      logged.push(args);
    };
    try {
      await signOutAndRedirect({
        signOut,
        navigate: (destination) => navigated.push(destination),
        onError: (message) => errors.push(message),
        redirectTo,
      });
    } finally {
      console.error = quiet;
    }
  }

  return { errors, logged, navigated, run };
}

test("a refused sign-out does not navigate and reports the failure", async () => {
  const { errors, logged, navigated, run } = harness(() =>
    Promise.resolve({
      data: null,
      error: {
        status: 500,
        statusText: "Internal Server Error",
        message: "connect ECONNREFUSED 10.0.0.4:5432",
      },
    }),
  );

  await run();

  // Better Auth RESOLVES this — it does not reject. Before #870 the caller
  // discarded it and redirected, leaving a live session behind.
  assert.deepEqual(navigated, []);

  // The server's own words NEVER reach the user. #870's Proposed fix pins this
  // exact string, and the upstream text is untranslated in a 30-locale app and
  // can carry a driver message like the one above.
  assert.deepEqual(errors, [SIGN_OUT_ERROR_MESSAGE]);

  // …but the developer still gets all three fields, in the one console line.
  assert.equal(logged.length, 1);
  const line = String(logged[0]?.[0]);
  assert.match(line, /500/);
  assert.match(line, /Internal Server Error/);
  assert.match(line, /connect ECONNREFUSED 10\.0\.0\.4:5432/);
});

test("a refused sign-out with an EMPTY message still shows the shared copy", async () => {
  // The hole the old `error.message ?? SIGN_OUT_ERROR_MESSAGE` left: `??` does
  // not fall back on `""`, and the render guard is a truthiness check, so an
  // empty message showed the user NOTHING AT ALL. This case fails the moment
  // the user-facing string is derived from the error again.
  const { errors, logged, navigated, run } = harness(() =>
    Promise.resolve({ data: null, error: { status: 403, message: "" } }),
  );

  await run();

  assert.deepEqual(navigated, []);
  assert.deepEqual(errors, [SIGN_OUT_ERROR_MESSAGE]);
  assert.equal(logged.length, 1);
});

test("a successful sign-out navigates exactly once to the locale path", async () => {
  const { errors, logged, navigated, run } = harness(() =>
    Promise.resolve({ data: { success: true }, error: null }),
  );

  await run();

  assert.deepEqual(navigated, ["/fr/user/sign-in"]);
  assert.deepEqual(errors, []);
  assert.deepEqual(logged, []);
});

test("a thrown sign-out does not navigate and reports the failure", async () => {
  const { errors, logged, navigated, run } = harness(() =>
    Promise.reject(new Error("network down")),
  );

  await run();

  assert.deepEqual(navigated, []);
  assert.deepEqual(errors, [SIGN_OUT_ERROR_MESSAGE]);
  assert.equal(logged.length, 1);
});

/**
 * The handler `UserHeader` builds in its render body. Records the busy flag and
 * the error region the way React state would receive them, so a test can assert
 * on the SEQUENCE — the defect in review round 1 was a `finally` that cleared
 * busy on the success path too.
 */
function handlerHarness(signOut: () => Promise<SignOutResult>) {
  const busy: boolean[] = [];
  const errors: (string | null)[] = [];
  const navigated: string[] = [];
  const page = fakePage();

  const handler = createSignOutHandler({
    signOut,
    navigate: (destination) => navigated.push(destination),
    setBusy: (value) => busy.push(value),
    setError: (message) => errors.push(message),
    redirectTo: "/fr/user/sign-in",
    // The REAL watcher, on a fake page — the same wiring `UserHeader` makes
    // with `window` (#948).
    onRedirectScheduled: (release) => {
      watchForAbandonedRedirect(page.host, release);
    },
  });

  async function run() {
    const quiet = console.error;
    console.error = () => {};
    try {
      await handler();
    } finally {
      console.error = quiet;
    }
  }

  return { busy, errors, navigated, page, run };
}

/**
 * A stand-in for the page, the shape `tests/abandoned-redirect.test.ts` uses:
 * it records the `pageshow` listeners so a test can fire one and assert on
 * what was torn down.
 */
function fakePage() {
  const listeners: Array<(event: RedirectPageShowEvent) => void> = [];
  const host: RedirectWatchHost = {
    addEventListener: (_type, listener) => {
      listeners.push(listener);
    },
    removeEventListener: (_type, listener) => {
      const at = listeners.indexOf(listener);

      if (at >= 0) listeners.splice(at, 1);
    },
  };

  return {
    host,
    listeners,
    /** Fires `pageshow` at every listener still registered. */
    pageshow(persisted: boolean) {
      for (const listener of listeners.slice()) listener({ persisted });
    },
  };
}

test("a successful sign-out leaves the button disarmed while the page unloads", async () => {
  const { busy, errors, navigated, run } = handlerHarness(() =>
    Promise.resolve({ data: { success: true }, error: null }),
  );

  await run();

  assert.deepEqual(navigated, ["/fr/user/sign-in"]);
  // The heart of it. `navigate` sets `window.location.href`, which only
  // SCHEDULES the navigation — this document stays live and interactive for the
  // whole page load that follows. A `setBusy(false)` here re-enables the
  // button, flips the label back to "Sign Out", and lets a second sign-out be
  // clicked against a session that is already gone.
  assert.deepEqual(busy, [true]);
  assert.deepEqual(errors, [null]);
});

test("a refused sign-out re-arms the button and shows the failure", async () => {
  const { busy, errors, navigated, run } = handlerHarness(() =>
    Promise.resolve({
      data: null,
      error: { status: 500, message: "Internal Server Error" },
    }),
  );

  await run();

  assert.deepEqual(navigated, []);
  // The user is still on this page, so the button HAS to come back.
  assert.deepEqual(busy, [true, false]);
  // The shared copy, not the server's "Internal Server Error".
  assert.deepEqual(errors, [null, SIGN_OUT_ERROR_MESSAGE]);
});

test("a thrown sign-out re-arms the button and shows the failure", async () => {
  const { busy, errors, navigated, run } = handlerHarness(() =>
    Promise.reject(new Error("network down")),
  );

  await run();

  assert.deepEqual(navigated, []);
  assert.deepEqual(busy, [true, false]);
  assert.deepEqual(errors, [null, SIGN_OUT_ERROR_MESSAGE]);
});

test("the handler clears a previous failure before it retries", async () => {
  let attempt = 0;
  const { busy, errors, navigated, run } = handlerHarness(() => {
    attempt += 1;

    return attempt === 1
      ? Promise.resolve({ data: null, error: { status: 500 } })
      : Promise.resolve({ data: { success: true }, error: null });
  });

  await run();
  await run();

  // The stale "sign out failed" must not sit next to a sign-out that worked.
  assert.deepEqual(errors, [null, SIGN_OUT_ERROR_MESSAGE, null]);
  assert.deepEqual(busy, [true, false, true]);
  assert.deepEqual(navigated, ["/fr/user/sign-in"]);
});

/**
 * #948. The success exit leaves busy set (above) — but a scheduled navigation
 * can be abandoned. Before this, a user who signed out and came Back out of the
 * bfcache found a Sign Out button that stayed disabled until a reload.
 */

test("#948: Back out of the bfcache after a sign-out re-arms the button", async () => {
  const { busy, navigated, page, run } = handlerHarness(() =>
    Promise.resolve({ data: { success: true }, error: null }),
  );

  await run();

  assert.deepEqual(navigated, ["/fr/user/sign-in"]);
  // Still disarmed while the navigation may commit…
  assert.deepEqual(busy, [true]);
  assert.equal(page.listeners.length, 1);

  // …and released by the bfcache restore, the one signal a browser gives.
  page.pageshow(true);

  assert.deepEqual(busy, [true, false]);
  // The watch tore itself down: no listener left behind per sign-out.
  assert.equal(page.listeners.length, 0);
});

test("#948: an ordinary page load after a sign-out re-arms nothing", async () => {
  const { busy, page, run } = handlerHarness(() =>
    Promise.resolve({ data: { success: true }, error: null }),
  );

  await run();
  page.pageshow(false);

  assert.deepEqual(busy, [true]);
  assert.equal(page.listeners.length, 1);
});

test("#948: a refused sign-out arms no abandoned-redirect watch", async () => {
  const { busy, page, run } = handlerHarness(() =>
    Promise.resolve({ data: null, error: { status: 500 } }),
  );

  await run();

  // Released at once by the handler itself, so nothing else may own it.
  assert.deepEqual(busy, [true, false]);
  assert.equal(page.listeners.length, 0);
});

test("#948: a second sign-out after coming Back arms exactly one fresh watch", async () => {
  const { busy, navigated, page, run } = handlerHarness(() =>
    Promise.resolve({ data: { success: true }, error: null }),
  );

  await run();
  page.pageshow(true);
  await run();

  assert.deepEqual(navigated, ["/fr/user/sign-in", "/fr/user/sign-in"]);
  assert.deepEqual(busy, [true, false, true]);
  // One listener, the second click's: the first watch removed itself.
  assert.equal(page.listeners.length, 1);

  page.pageshow(true);

  assert.deepEqual(busy, [true, false, true, false]);
  assert.equal(page.listeners.length, 0);
});
