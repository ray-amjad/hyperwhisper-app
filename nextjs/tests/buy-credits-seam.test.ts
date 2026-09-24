import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { fileURLToPath } from "node:url";

import {
  BUY_CREDITS_ENDPOINT,
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

test("a 500 with an empty body falls back to the translated copy", async () => {
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
function handlerHarness(
  fetchImpl: BuyCreditsFetch,
  licenseKey: string | null = LICENSE_KEY,
) {
  const busy: Array<BuyCreditsTier | null> = [];
  const errors: Array<string | null> = [];
  const navigated: string[] = [];
  const reported: ReportedCall[] = [];
  const requests: Array<{ input: string; init: BuyCreditsRequestInit }> = [];

  const handler = createBuyCreditsHandler({
    licenseKey,
    fetchImpl: (input, init) => {
      requests.push({ input, init });

      return fetchImpl(input, init);
    },
    navigate: (destination) => navigated.push(destination),
    reportError: (error, properties) => reported.push({ error, properties }),
    setBusy: (tier) => busy.push(tier),
    setError: (message) => errors.push(message),
    checkoutErrorMessage: CHECKOUT_ERROR,
    genericErrorMessage: GENERIC_ERROR,
  });

  async function run(amount = 5, tier?: BuyCreditsTier) {
    const quiet = console.error;

    console.error = () => {};
    try {
      await (tier === undefined ? handler(amount) : handler(amount, tier));
    } finally {
      console.error = quiet;
    }
  }

  return { busy, errors, navigated, reported, requests, run };
}

test("a successful checkout leaves the tier buttons disarmed", async () => {
  const { busy, errors, navigated, run } = handlerHarness(
    responds({
      ok: true,
      status: 200,
      json: () => ({ checkoutUrl: "https://checkout.stripe.com/c/pay/ok" }),
    }),
  );

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
