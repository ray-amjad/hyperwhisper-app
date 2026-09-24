import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { fileURLToPath } from "node:url";

import {
  BUY_CREDITS_ENDPOINT,
  BUY_CREDITS_HANDLER_STAGE,
  BUY_CREDITS_HANDOVER_STAGE,
  BUY_CREDITS_OPERATION,
  buyCreditsAndRedirect,
  createBuyCreditsHandler,
  type BuyCreditsFetch,
  type BuyCreditsRequestInit,
  type BuyCreditsTier,
} from "../src/lib/buy-credits";

/**
 * The seam `CloudCreditsCard` now calls. #737 is a handler that read
 * `data.checkoutUrl`, found nothing, and returned in silence: no message, no
 * report, no record of the lost sale. Every test below is therefore an
 * assertion about what the customer and the error tracker DO get on a path
 * that used to produce nothing at all.
 */

/** Stand-in for a real licence key. Never allowed into a report. */
const LICENSE_KEY = "HW-TEST-0000-1111-2222";

/** Stand-in for the translated `buyCredits.errorCheckout`. */
const CHECKOUT_ERROR = "Could not start checkout. Please try again.";

/** Stand-in for the translated `buyCredits.errorGeneric`. */
const GENERIC_ERROR = "Something went wrong. Please try again.";

interface ReportedCall {
  error: unknown;
  properties: Record<string, unknown>;
}

/**
 * Records every collaborator call so a test can assert on what did NOT
 * happen — a `navigate` that fired for a checkout the server refused is the
 * defect this file exists to hold shut.
 *
 * `console.error` is swapped for the duration of the run, the way
 * `sign-out-seam.test.ts:21-36` does it, so the suite output stays clean and
 * the developer-facing line is still assertable.
 */
function harness(fetchImpl: BuyCreditsFetch) {
  const navigated: string[] = [];
  const errors: string[] = [];
  const logged: unknown[][] = [];
  const reported: ReportedCall[] = [];
  const requests: Array<{ input: string; init: BuyCreditsRequestInit }> = [];

  const recordingFetch: BuyCreditsFetch = (input, init) => {
    requests.push({ input, init });

    return fetchImpl(input, init);
  };

  async function run(amount = 5): Promise<boolean> {
    const quiet = console.error;

    console.error = (...args: unknown[]) => {
      logged.push(args);
    };
    try {
      return await buyCreditsAndRedirect({
        licenseKey: LICENSE_KEY,
        amount,
        fetchImpl: recordingFetch,
        navigate: (destination) => navigated.push(destination),
        onError: (message) => errors.push(message),
        reportError: (error, properties) =>
          reported.push({ error, properties }),
        checkoutErrorMessage: CHECKOUT_ERROR,
        genericErrorMessage: GENERIC_ERROR,
      });
    } finally {
      console.error = quiet;
    }
  }

  return { errors, logged, navigated, reported, requests, run };
}

/** A resolved response, exactly as the route would answer it. */
function responds(body: {
  ok: boolean;
  status: number;
  json: () => unknown;
}): BuyCreditsFetch {
  return () => Promise.resolve(body);
}

test("a 400 shows the server's own message and never navigates", async () => {
  const { errors, logged, navigated, reported, run } = harness(
    responds({
      ok: false,
      status: 400,
      json: () => ({ error: "Amount too large" }),
    }),
  );

  const navigatedAtAll = await run();

  // #737's Done-when item 2: the location was not changed. The seam records
  // every `navigate` call, so this is stronger than reading one global.
  assert.deepEqual(navigated, []);
  assert.equal(navigatedAtAll, false);

  // #737's Done-when item 1, at the seam: the exact string the route sent is
  // what the customer is told. Unlike #870's sign-out copy, this text is
  // written by our own route for this customer, and the issue pins it.
  assert.deepEqual(errors, ["Amount too large"]);

  // The lost sale now leaves a record — the whole second half of the issue.
  assert.equal(reported.length, 1);
  assert.equal(logged.length, 1);
});

test("a 400 about the licence itself is shown verbatim too", async () => {
  // `route.ts:112-117` — the other 4xx wording, and the one #947 review round
  // 2 names alongside "Amount too large". Both are sentences written for this
  // customer about this request, so both are shown. This is the positive
  // control for the 4xx half of the status-class split: delete the split and
  // this test and the one above it both go red.
  const { errors, navigated, run } = harness(
    responds({
      ok: false,
      status: 400,
      json: () => ({ error: "License is revoked" }),
    }),
  );

  await run();

  assert.deepEqual(navigated, []);
  assert.deepEqual(errors, ["License is revoked"]);
});

/**
 * The body `app/api/checkout/credits/route.ts:218-227` really sends on a 500 —
 * every unhandled throw on that route, a Stripe outage and a DB fault alike,
 * answers with exactly this shape. The old fixture here was `{}`, a body the
 * route cannot produce, and it let the 5xx case pass while the code showed the
 * customer a raw `data.error` (#947 review round 2, finding 5).
 */
const ROUTE_500_BODY = {
  error: "Failed to create checkout session",
  details: "Stripe API error: connection refused to api.stripe.com",
};

test("a 500 shows the translated copy, not the route's own words", async () => {
  // #947 review round 2, finding 3. The string above is not a message to a
  // customer — it is an internal fault description, in English only, in front
  // of a 40-locale alert region, and its `details` carries the throw's own
  // message. A 4xx body is the opposite and is still shown verbatim (the test
  // above this one). The status class is what decides.
  const { errors, navigated, reported, run } = harness(
    responds({ ok: false, status: 500, json: () => ROUTE_500_BODY }),
  );

  await run();

  assert.deepEqual(navigated, []);
  assert.deepEqual(errors, [CHECKOUT_ERROR]);
  // Neither half of the route's body reached the screen.
  assert.equal(errors[0]?.includes("Failed to create checkout session"), false);
  assert.equal(errors[0]?.includes("connection refused"), false);
  // …and the developer still gets the status.
  assert.deepEqual(reported[0]?.properties, {
    operation: BUY_CREDITS_OPERATION,
    status: 500,
  });
});

test("a 503 from in front of the route shows the translated copy too", async () => {
  // The split is on the CLASS, not on the number 500. A proxy or a platform
  // 503 carries no `error` at all, and a future route 5xx with a different
  // body must not be paintable either.
  const { errors, reported, run } = harness(
    responds({
      ok: false,
      status: 503,
      json: () => ({ error: "upstream connect error" }),
    }),
  );

  await run();

  assert.deepEqual(errors, [CHECKOUT_ERROR]);
  assert.deepEqual(reported[0]?.properties, {
    operation: BUY_CREDITS_OPERATION,
    status: 503,
  });
});

test("a 500 with an empty body falls back to the translated copy", async () => {
  // Kept as its own case, separate from the real-body one above. The route
  // cannot send this, but a body that lost its `error` in transit, or a future
  // 5xx handler that sends none, must land in the same place — and this is the
  // only test that proves the fallback is not reached THROUGH the status class.
  const { errors, navigated, reported, run } = harness(
    responds({ ok: false, status: 500, json: () => ({}) }),
  );

  await run();

  assert.deepEqual(navigated, []);
  assert.deepEqual(errors, [CHECKOUT_ERROR]);
  assert.deepEqual(reported[0]?.properties, {
    operation: BUY_CREDITS_OPERATION,
    status: 500,
  });
});

test("a 400 with an EMPTY error string still shows the translated copy", async () => {
  // The trap `sign-out.ts:50-55` documents, ported: `""` is a string, so it
  // passes `typeof data.error === "string"`, and the card's alert region
  // renders only when the message is truthy. The customer would be shown an
  // empty box — the same "button that does nothing" #737 is about. The
  // sibling at `CreditsPurchase.tsx:106-110` still has this hole.
  const { errors, navigated, run } = harness(
    responds({ ok: false, status: 400, json: () => ({ error: "" }) }),
  );

  await run();

  assert.deepEqual(navigated, []);
  assert.deepEqual(errors, [CHECKOUT_ERROR]);
});

test("a body that is not JSON falls back to the copy, with the status kept", async () => {
  // A 500 from a proxy or an unhandled route throw is an HTML page, and
  // `json()` REJECTS on it. `CreditsPurchase.tsx:95` lets that rejection fall
  // into its network `catch`, which loses the status and blames the network.
  // Here it stays on the refusal branch.
  const { errors, navigated, reported, run } = harness(
    responds({
      ok: false,
      status: 502,
      json: () => {
        throw new SyntaxError("Unexpected token '<'");
      },
    }),
  );

  await run();

  assert.deepEqual(navigated, []);
  assert.deepEqual(errors, [CHECKOUT_ERROR]);
  // Not `{ operation }` alone — that is what the network path reports.
  assert.deepEqual(reported[0]?.properties, {
    operation: BUY_CREDITS_OPERATION,
    status: 502,
  });
});

test("a 200 with a checkoutUrl navigates exactly once and shows nothing", async () => {
  const { errors, logged, navigated, reported, run } = harness(
    responds({
      ok: true,
      status: 200,
      json: () => ({ checkoutUrl: "https://checkout.stripe.com/c/pay/ok" }),
    }),
  );

  const navigatedAtAll = await run();

  assert.deepEqual(navigated, ["https://checkout.stripe.com/c/pay/ok"]);
  assert.equal(navigatedAtAll, true);
  assert.deepEqual(errors, []);
  assert.deepEqual(reported, []);
  assert.deepEqual(logged, []);
});

test("a 200 with NO checkoutUrl does not navigate and now says so", async () => {
  // This is the exact response #737 opened on: the old handler's `if` had no
  // `else`, so the spinner stopped and the page was unchanged.
  const { errors, navigated, reported, run } = harness(
    responds({ ok: true, status: 200, json: () => ({ ok: true }) }),
  );

  await run();

  assert.deepEqual(navigated, []);
  assert.deepEqual(errors, [CHECKOUT_ERROR]);
  assert.deepEqual(reported[0]?.properties, {
    operation: BUY_CREDITS_OPERATION,
    status: 200,
  });
});

test("a REFUSED response that still carries a checkoutUrl is not followed", async () => {
  // The one test that can see `response.ok &&` disappear. Drop that flag and
  // every other case in this file still passes — a 400 with no `checkoutUrl`
  // never reaches the redirect either way. Only a refusal that HAS a URL
  // separates the two guards, and following it would send a paying customer
  // to a Stripe session the server has already rejected.
  const { errors, navigated, run } = harness(
    responds({
      ok: false,
      status: 400,
      json: () => ({
        checkoutUrl: "https://checkout.stripe.com/x",
        error: "Amount too large",
      }),
    }),
  );

  await run();

  assert.equal(navigated.length, 0);
  assert.deepEqual(errors, ["Amount too large"]);
});

test("a rejected request shows the generic copy and reports the throw", async () => {
  const thrown = new TypeError("Failed to fetch");
  const { errors, logged, navigated, reported, run } = harness(() =>
    Promise.reject(thrown),
  );

  await run();

  assert.deepEqual(navigated, []);
  assert.deepEqual(errors, [GENERIC_ERROR]);
  // The thrown value itself goes to the tracker, unwrapped.
  assert.equal(reported.length, 1);
  assert.equal(reported[0]?.error, thrown);
  // No `status` — there was no response to take one from.
  assert.deepEqual(reported[0]?.properties, {
    operation: BUY_CREDITS_OPERATION,
  });
  // The developer still gets the throw in the console, now with the
  // operation name #737 says the old line was missing.
  assert.equal(logged.length, 1);
  assert.match(String(logged[0]?.[0]), /buy_credits/);
});

test("the report names the operation and carries no licence key", async () => {
  const { reported, run } = harness(
    responds({
      ok: false,
      status: 400,
      json: () => ({ error: "Amount too large" }),
    }),
  );

  await run();

  const call = reported[0];

  assert.notEqual(call, undefined);
  assert.deepEqual(call?.properties, {
    operation: BUY_CREDITS_OPERATION,
    status: 400,
  });

  // The licence key is a credential, and this app's PostHog init has no
  // `before_send` and no property redaction (#739) — anything passed ships
  // verbatim. Serialising the whole payload catches a key nested anywhere,
  // including a `data` or a request body someone adds later.
  const payload = JSON.stringify({
    message:
      call?.error instanceof Error ? call.error.message : String(call?.error),
    properties: call?.properties,
  });

  assert.equal(payload.includes(LICENSE_KEY), false);
  // Not even a fragment of it.
  assert.equal(payload.includes("HW-TEST"), false);
  // …and the status the issue asks for IS there.
  assert.match(payload, /"status":400/);
});

test("the request is the wire contract the route expects", async () => {
  const { requests, run } = harness(
    responds({
      ok: true,
      status: 200,
      json: () => ({ checkoutUrl: "https://checkout.stripe.com/c/pay/ok" }),
    }),
  );

  await run(25);

  assert.equal(requests.length, 1);
  assert.equal(requests[0]?.input, BUY_CREDITS_ENDPOINT);
  assert.equal(requests[0]?.input, "/api/checkout/credits");
  assert.equal(requests[0]?.init.method, "POST");
  assert.deepEqual(requests[0]?.init.headers, {
    "Content-Type": "application/json",
  });
  // The route reads `licenseKey` and `amount`, and nothing else belongs here.
  assert.equal(
    requests[0]?.init.body,
    JSON.stringify({ licenseKey: LICENSE_KEY, amount: 25 }),
  );
});

test("the seam holds no browser global", async () => {
  // #737's Done-when item 2 asks that `location.href` was not changed. The
  // strongest form of that is a seam in which no navigation CAN be written:
  // the only way out is the injected `navigate`, which every test above
  // records. A raw assignment added back here would be invisible to all of
  // them, so the source text itself is the assertion.
  const source = readFileSync(
    fileURLToPath(new URL("../src/lib/buy-credits.ts", import.meta.url)),
    "utf8",
  );

  // A positive control first, so the two assertions below cannot both pass
  // because the file was read from the wrong path.
  assert.match(source, /buyCreditsAndRedirect/);
  assert.equal(/\bwindow\b/.test(source), false);
  assert.equal(source.includes("location.href ="), false);
});

/**
 * The handler `CloudCreditsCard` builds in its render body. Records the busy
 * tier and the error region the way React state would receive them, so a test
 * can assert on the SEQUENCE — the card's `finally { setLoadingTier(null) }`
 * cleared busy on the success path too.
 */
/**
 * The collaborators a test can replace with one that THROWS. Every one of
 * them is real: an ad-blocker stubs `posthog.captureException`, a sandboxed
 * frame refuses the navigation assignment. `CloudCreditsCard.tsx` calls the
 * handler as `void handleBuyCredits(…)` with no `.catch`, so any of these
 * escaping is an unhandled rejection at the customer AND a buy block that
 * never comes back (#947 review round 1, finding 1).
 */
interface HandlerOverrides {
  navigate?: (destination: string) => void;
  reportError?: (error: unknown, properties: Record<string, unknown>) => void;
  onRedirectScheduled?: (release: () => void) => void;
}

function handlerHarness(
  fetchImpl: BuyCreditsFetch,
  licenseKey: string | null = LICENSE_KEY,
  overrides: HandlerOverrides = {},
) {
  const busy: Array<BuyCreditsTier | null> = [];
  const errors: Array<string | null> = [];
  /**
   * The developer-facing `console.error` lines, RECORDED rather than dropped.
   * They used to be swallowed by `run`, which is why finding 2's wrong
   * sentence — a log line claiming the busy flag was "released anyway" on the
   * one path that releases nothing — could not be asserted on from here.
   */
  const logged: unknown[][] = [];
  const navigated: string[] = [];
  const reported: ReportedCall[] = [];
  const requests: Array<{ input: string; init: BuyCreditsRequestInit }> = [];
  /**
   * Every busy-flag release the handler handed to the abandoned-redirect
   * watcher. The card passes `watchForAbandonedRedirect` here; this records
   * the callbacks instead, so a test can hold one and fire it by hand.
   */
  const releases: Array<() => void> = [];

  const handler = createBuyCreditsHandler({
    licenseKey,
    fetchImpl: (input, init) => {
      requests.push({ input, init });

      return fetchImpl(input, init);
    },
    navigate:
      overrides.navigate ?? ((destination) => navigated.push(destination)),
    reportError:
      overrides.reportError ??
      ((error, properties) => reported.push({ error, properties })),
    setBusy: (tier) => busy.push(tier),
    setError: (message) => errors.push(message),
    onRedirectScheduled:
      overrides.onRedirectScheduled ?? ((release) => releases.push(release)),
    checkoutErrorMessage: CHECKOUT_ERROR,
    genericErrorMessage: GENERIC_ERROR,
  });

  async function run(amount = 5, tier?: BuyCreditsTier) {
    const quiet = console.error;

    console.error = (...args: unknown[]) => {
      logged.push(args);
    };
    try {
      await (tier === undefined ? handler(amount) : handler(amount, tier));
    } finally {
      console.error = quiet;
    }
  }

  return { busy, errors, logged, navigated, releases, reported, requests, run };
}

/** A 200 carrying a checkout URL — the only response that navigates. */
const CHECKOUT_OK: BuyCreditsFetch = responds({
  ok: true,
  status: 200,
  json: () => ({ checkoutUrl: "https://checkout.stripe.com/c/pay/ok" }),
});

test("a successful checkout leaves the tier buttons disarmed", async () => {
  const { busy, errors, navigated, run } = handlerHarness(CHECKOUT_OK);

  await run(5);

  assert.deepEqual(navigated, ["https://checkout.stripe.com/c/pay/ok"]);
  // The heart of it. The assignment only SCHEDULES the navigation — this
  // document stays live and interactive for the whole redirect to Stripe. The
  // card's old unconditional `finally` re-armed all three buttons there, so a
  // second click created a second checkout session for the same top-up.
  assert.deepEqual(busy, [5]);
  assert.deepEqual(errors, [null]);
});

test("a refused checkout re-arms the buttons and shows the failure", async () => {
  const { busy, errors, navigated, run } = handlerHarness(
    responds({
      ok: false,
      status: 400,
      json: () => ({ error: "Amount too large" }),
    }),
  );

  await run(500);

  assert.deepEqual(navigated, []);
  // The customer is still on the dashboard, so the buttons HAVE to come back.
  assert.deepEqual(busy, [500, null]);
  assert.deepEqual(errors, [null, "Amount too large"]);
});

test("a thrown request re-arms the buttons and shows the generic copy", async () => {
  const { busy, errors, navigated, reported, run } = handlerHarness(() =>
    Promise.reject(new TypeError("Failed to fetch")),
  );

  await run(5);

  assert.deepEqual(navigated, []);
  assert.deepEqual(busy, [5, null]);
  assert.deepEqual(errors, [null, GENERIC_ERROR]);
  assert.equal(reported.length, 1);
});

test("the custom tier carries its sentinel, not the dollar amount", async () => {
  // The card's custom box calls `handleBuyCredits(Number(customAmount),
  // "custom")`, and the spinner it shows is keyed on that sentinel.
  const { busy, requests, run } = handlerHarness(
    responds({ ok: false, status: 400, json: () => ({ error: "Nope" }) }),
  );

  await run(37, "custom");

  assert.deepEqual(busy, ["custom", null]);
  assert.equal(
    requests[0]?.init.body,
    JSON.stringify({ licenseKey: LICENSE_KEY, amount: 37 }),
  );
});

test("a second click clears the previous failure first", async () => {
  let attempt = 0;
  const { busy, errors, navigated, run } = handlerHarness((input, init) => {
    attempt += 1;

    return Promise.resolve(
      attempt === 1
        ? {
            ok: false,
            status: 400,
            json: () => ({ error: "Amount too large" }),
          }
        : {
            ok: true,
            status: 200,
            json: () => ({
              checkoutUrl: `https://checkout.stripe.com/c/pay/${init.method}`,
            }),
          },
    );
  });

  await run(5);
  await run(5);

  // The stale "Amount too large" must not sit next to a checkout that worked.
  assert.deepEqual(errors, [null, "Amount too large", null]);
  assert.deepEqual(busy, [5, null, 5]);
  assert.deepEqual(navigated, ["https://checkout.stripe.com/c/pay/POST"]);
});

/**
 * #947 review round 1, findings 1 and 3. One rule, stated once:
 *
 *   THE BUSY FLAG HAS EXACTLY ONE OWNER ON EVERY EXIT, AND NO EXIT HAS NONE.
 *
 * Every test above covers an exit that stays on the dashboard, where the owner
 * is the handler itself. The four below cover the two exits that had no owner
 * at all: a scheduled navigation (the flag was dropped on the floor and only a
 * reload brought the card back) and a collaborator that throws (the flag was
 * dropped AND the handler rejected under a `void` call site).
 */

test("a scheduled redirect hands the busy release over instead of dropping it", async () => {
  const { busy, navigated, releases, run } = handlerHarness(CHECKOUT_OK);

  await run(5);

  assert.deepEqual(navigated, ["https://checkout.stripe.com/c/pay/ok"]);
  // Still disarmed on the way out — the half of the rule that must not change.
  assert.deepEqual(busy, [5]);
  // …but handed to somebody. Before this, the branch simply returned and
  // `loadingTier` stayed set for the life of the document: every tier button,
  // the custom toggle and the amount input dead, with no recovery but a
  // reload, on a customer who pressed Escape or came Back out of the bfcache.
  assert.equal(releases.length, 1);
  assert.equal(typeof releases[0], "function");

  // The card wires this to `watchForAbandonedRedirect`, whose own conditions
  // are `tests/abandoned-redirect.test.ts`. What it does when it fires is
  // this: the one thing the handler declined to do itself.
  releases[0]?.();

  assert.deepEqual(busy, [5, null]);
});

test("a refused checkout arms no abandoned-redirect watch", async () => {
  const { busy, releases, run } = handlerHarness(
    responds({
      ok: false,
      status: 400,
      json: () => ({ error: "Amount too large" }),
    }),
  );

  await run(500);

  // The customer never left, so the handler released the flag itself and there
  // is nothing to hand over. A watch armed here would sit on a live page for
  // twenty seconds and then clear a flag a LATER click had set.
  assert.deepEqual(busy, [500, null]);
  assert.equal(releases.length, 0);
});

test("a navigation that throws re-arms the card and is not blamed on the network", async () => {
  const blocked = new Error("The navigation was refused");
  const { busy, errors, releases, reported, run } = handlerHarness(
    CHECKOUT_OK,
    LICENSE_KEY,
    {
      navigate: () => {
        throw blocked;
      },
    },
  );

  // `CloudCreditsCard.tsx` calls this with `void` and no `.catch`, so a
  // rejection here is an unhandled rejection in the customer's console.
  await assert.doesNotReject(() => run(5));

  // A navigation that threw is a navigation that did not happen, so this is an
  // exit that stays on the dashboard and the flag comes back here.
  assert.deepEqual(busy, [5, null]);
  assert.equal(releases.length, 0);

  // Reported ONCE, and under its own stage. `buyCreditsAndRedirect` wraps the
  // REQUEST and nothing else on purpose: widening that `try` would catch this
  // same throw and send it down the network path, showing the customer
  // "Something went wrong" about a network that answered 200. So the customer
  // is told nothing new and the fetch classification is untouched.
  assert.equal(reported.length, 1);
  assert.equal(reported[0]?.error, blocked);
  assert.deepEqual(reported[0]?.properties, {
    operation: BUY_CREDITS_OPERATION,
    stage: BUY_CREDITS_HANDLER_STAGE,
  });
  // `[null]` alone, and that is the assertion: the network path's
  // `GENERIC_ERROR` never reached the customer, because the request never
  // failed. Only the `setError(null)` that every click opens with is here.
  assert.deepEqual(errors, [null]);
});

test("a redirect handover that throws leaves the card busy, and says so", async () => {
  // #947 review round 2, findings 2 and 4. `HandlerOverrides` has declared
  // `onRedirectScheduled` since round 1 and no test ever supplied it, while
  // its two siblings each had a throwing test — so this branch ran in no test
  // at all, and the console line on it claimed the busy flag was "released
  // anyway" when nothing released it.
  //
  // The reviewer's remedy was to call `setBusy(null)` here. DECLINED, and this
  // test is where the decision is pinned. Getting here means `navigate`
  // already assigned `location.href`: a cross-origin load to Stripe is
  // scheduled and most likely committing, and only the handover to the
  // page-lifecycle watcher failed. Re-arming the controls at that moment is
  // the double-session fault round 2 deleted a 20-second timer for doing more
  // mildly — at 0 ms it is the same defect, made worse. So the card stays busy
  // and a reload is the recovery, exactly as it is for Escape.
  const broken = new TypeError("window.addEventListener is not a function");
  const { busy, errors, navigated, releases, reported, run } = handlerHarness(
    CHECKOUT_OK,
    LICENSE_KEY,
    {
      onRedirectScheduled: () => {
        throw broken;
      },
    },
  );

  // `CloudCreditsCard.tsx` calls this with `void` and no `.catch`, so a
  // rejection here is an unhandled rejection in the customer's console.
  await assert.doesNotReject(() => run(5));

  // The checkout itself worked and the customer is on their way to Stripe.
  assert.deepEqual(navigated, ["https://checkout.stripe.com/c/pay/ok"]);
  // Deliberately `[5]` and NOT `[5, null]`. Change this line and read the
  // `catch` in `createBuyCreditsHandler` first — it is the only exit in the
  // file that releases nothing, and it is the reason mutation row `w` exists.
  assert.deepEqual(busy, [5]);
  // Nothing to fire by hand: the handover is what failed.
  assert.equal(releases.length, 0);
  // The customer is told nothing new — the request answered 200 and a session
  // really was created, so a failure message here would be a lie.
  assert.deepEqual(errors, [null]);

  // Silence is what made this a defect, so the exit reports itself, under its
  // OWN stage: a dashboard cannot infer this one from anything else, because
  // no other exit leaves a customer with a card that can never re-arm.
  assert.equal(reported.length, 1);
  assert.equal(reported[0]?.error, broken);
  assert.deepEqual(reported[0]?.properties, {
    operation: BUY_CREDITS_OPERATION,
    stage: BUY_CREDITS_HANDOVER_STAGE,
  });
  assert.notEqual(BUY_CREDITS_HANDOVER_STAGE, BUY_CREDITS_HANDLER_STAGE);
});

test("the handover fault's console line does not claim a release it skipped", async () => {
  // The other half of finding 2: "Fix the code AND the comment." The line the
  // developer reads used to say "The busy flag is released anyway so the card
  // is not left dead" on every path, this one included. It is a parameter now,
  // and this is what holds the two wordings apart — a fault whose own log line
  // contradicts the behaviour sends the next reader looking in the wrong file.
  const { busy, logged, run } = handlerHarness(CHECKOUT_OK, LICENSE_KEY, {
    onRedirectScheduled: () => {
      throw new TypeError("window.addEventListener is not a function");
    },
  });

  await run(5);

  assert.deepEqual(busy, [5]);
  assert.equal(logged.length, 1);

  const line = String(logged[0]?.[0]);

  assert.match(line, /buy_credits/);
  // The sentence that was false here.
  assert.equal(line.includes("released anyway"), false);
  // …and what it says instead: the true state of the card, and the recovery.
  assert.match(line, /nothing now owns the busy flag/);
  assert.match(line, /reload/);
});

test("an error tracker an ad-blocker broke does not leave the card dead", async () => {
  // The reported trigger. `posthog.captureException` stubbed by an extension
  // throws from inside the refusal branch's `reportError`, past the point
  // where `buyCreditsAndRedirect` can catch anything.
  let reportAttempts = 0;
  const { busy, errors, run } = handlerHarness(
    responds({
      ok: false,
      status: 400,
      json: () => ({ error: "Amount too large" }),
    }),
    LICENSE_KEY,
    {
      reportError: () => {
        reportAttempts += 1;

        throw new TypeError("posthog.captureException is not a function");
      },
    },
  );

  await assert.doesNotReject(() => run(5));

  // The customer still sees the route's own message — `onError` ran before
  // the reporter did — and the buttons still come back.
  assert.deepEqual(errors, [null, "Amount too large"]);
  assert.deepEqual(busy, [5, null]);
  // Twice: the refusal's own report, then one best-effort attempt to record
  // the reporter's failure. The second throw is swallowed, because the one
  // collaborator that cannot be trusted to report a fault is the reporter.
  assert.equal(reportAttempts, 2);
});

test("no licence key means no request, no spinner and no message", async () => {
  // `CloudCreditsCard.tsx:57` returned early on this. The guard lives in the
  // factory now, so the card cannot forget it — and it comes BEFORE
  // `setBusy`, because a spinner that starts for a checkout that was never
  // attempted is the same silence #737 is about.
  const { busy, errors, navigated, requests, run } = handlerHarness(
    responds({
      ok: true,
      status: 200,
      json: () => ({ checkoutUrl: "https://checkout.stripe.com/c/pay/ok" }),
    }),
    null,
  );

  await run(5);

  assert.deepEqual(requests, []);
  assert.deepEqual(busy, []);
  assert.deepEqual(errors, []);
  assert.deepEqual(navigated, []);
});
