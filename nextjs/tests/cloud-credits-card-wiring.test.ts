/**
 * The `CloudCreditsCard` wiring seam:
 * `components/customer/dashboard/CloudCreditsCard.tsx` (#737).
 *
 * `tests/buy-credits-seam.test.ts` covers the DECISION — that a refused
 * checkout tells the customer and reports the lost sale instead of navigating.
 * It does NOT cover the thing #737 actually regresses on: that the card still
 * routes its buy buttons through that decision. Restore the pre-fix card from
 * `origin/main`, which re-adds the inline `fetch` + `window.location.href` and
 * the silent `if` with no `else`, and every seam test stays green. This file is
 * what goes red.
 *
 * There is no DOM here and no click: `renderToStaticMarkup` is the only way a
 * component is reachable in this repo and it renders once with no events. So
 * the handler is built by `createBuyCreditsHandler` in the card's RENDER BODY,
 * and a static render is enough to prove the card asked for it and with what.
 * The presentational half is MOCKED here so the props crossing the seam can be
 * read — including `onBuy`, which this file INVOKES to prove the button is
 * joined to the handler the factory built. The markup those props produce is
 * `tests/cloud-credits-card-view.test.ts`.
 *
 * What this file cannot see is whether the wrapper's `setError` really moves
 * the View's `error` prop: the View is a stub here, and asserting
 * `typeof request.setError === "function"` passes just as happily for a
 * `() => {}`. That link is `tests/cloud-credits-card-error-state.test.ts`,
 * which mounts the REAL View for exactly that reason.
 */
import assert from "node:assert/strict";
import test, { beforeEach, mock } from "node:test";
import { createElement, type ReactElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";

import type { CloudCreditsCardViewProps } from "../components/customer/dashboard/CloudCreditsCardView";
import type {
  BuyCreditsHandlerRequest,
  BuyCreditsTier,
} from "../src/lib/buy-credits";

/**
 * `mock.module` is a real Node 22 API (behind `--experimental-test-module-mocks`,
 * which `npm test` passes) but this repo pins `@types/node@20`, which has no
 * declaration for it. Narrowed to the two options used here rather than bumping
 * the types in a test-only change — the same shape
 * `tests/user-header-sign-out.test.ts` uses.
 */
interface ModuleMocker {
  module(
    specifier: string,
    options: {
      namedExports?: Record<string, unknown>;
      defaultExport?: unknown;
    },
  ): void;
}

const moduleMock = mock as unknown as ModuleMocker;

/** Every `createBuyCreditsHandler` call the card made, in order. */
const factoryCalls: BuyCreditsHandlerRequest[] = [];

/** Every run of a handler the factory answered, with the arguments it got. */
const handlerRuns: Array<{
  request: BuyCreditsHandlerRequest;
  amount: number;
  tier: BuyCreditsTier | undefined;
}> = [];

/**
 * The seam under test. The stub records the request and answers a handler that
 * records its own runs, which is all the card does with it. When the handler
 * navigates, what it shows and when it clears the busy flag is
 * `tests/buy-credits-seam.test.ts`.
 */
moduleMock.module("../src/lib/buy-credits", {
  namedExports: {
    createBuyCreditsHandler: (request: BuyCreditsHandlerRequest) => {
      factoryCalls.push(request);

      return async (amount: number, tier?: BuyCreditsTier) => {
        handlerRuns.push({ request, amount, tier });
      };
    },
  },
});

/**
 * `useTranslations(namespace)` answers a `t` that echoes `namespace.key`, so an
 * assertion below can name BOTH halves. #737 declines to add a new message key
 * and reuses the sibling `/credits` form's two: the failure copy has to come
 * out of `buyCredits`, not out of `cloudCreditsCard`, and only the namespace in
 * the echoed string can prove which one the card asked for.
 */
moduleMock.module("next-intl", {
  namedExports: {
    useTranslations:
      (namespace: string) =>
      (key: string): string =>
        `${namespace}.${key}`,
  },
});

/** Every `posthog.captureException` the card's `reportError` forwarded. */
const captured: Array<{ error: unknown; properties: unknown }> = [];

/**
 * PostHog, stubbed. The real `captureException` is a silent no-op with no key
 * configured and it never throws, so it is unobservable — which is why the
 * seam takes an injected `reportError` and why this asserts on the stub.
 */
moduleMock.module("posthog-js/react", {
  namedExports: {
    usePostHog: () => ({
      captureException: (error: unknown, properties: unknown) => {
        captured.push({ error, properties });
      },
    }),
  },
});

/** Every prop set the wrapper handed the View, in order. */
const viewRenders: CloudCreditsCardViewProps[] = [];

/**
 * The presentational half, stubbed. It renders nothing: this file is about the
 * props crossing the seam. Capturing them is the only way to reach `onBuy` at
 * all — React never serialises a handler into static markup, which is exactly
 * how the #881 round 1 suite passed with the button's `onClick` deleted.
 */
moduleMock.module("../components/customer/dashboard/CloudCreditsCardView.tsx", {
  defaultExport: (props: CloudCreditsCardViewProps) => {
    viewRenders.push(props);

    return null;
  },
});

// A VARIABLE specifier, and deferred: a static import would bind before the
// `mock.module` calls above, and a literal `.tsx` specifier is a TS5097 error
// under this tsconfig. Same rule the other mock.module tests here document.
const CARD_PATH = "../components/customer/dashboard/CloudCreditsCard.tsx";

const LICENSE_KEY = "HW-TEST-0000-1111-2222";

type CloudCreditsCard = (props: {
  totalCredits: number;
  totalMinutesRemaining: number;
  creditsPerMinute: number;
  activeLicenseKey: string | null;
}) => ReactElement;

/** Renders the real card exactly as the dashboard does. */
async function renderCard(
  activeLicenseKey: string | null = LICENSE_KEY,
  totalMinutesRemaining = 42,
): Promise<void> {
  const { default: CloudCreditsCard } = (await import(CARD_PATH)) as {
    default: CloudCreditsCard;
  };

  renderToStaticMarkup(
    createElement(CloudCreditsCard, {
      totalCredits: 12345,
      totalMinutesRemaining,
      creditsPerMinute: 100,
      activeLicenseKey,
    }),
  );
}

beforeEach(() => {
  factoryCalls.length = 0;
  handlerRuns.length = 0;
  viewRenders.length = 0;
  captured.length = 0;
});

test("the card routes its buy buttons through the shared handler", async () => {
  await renderCard();

  // The whole of #737's fix at this seam. The pre-fix card did the `fetch`,
  // the `isRecord` check and the `window.location.href` inline, so the factory
  // is never asked for and this is 0.
  assert.equal(factoryCalls.length, 1);
});

test("the handler is built with every collaborator the seam needs", async () => {
  await renderCard();

  const request = factoryCalls[0];

  assert.ok(request, "the card never called the factory");
  // The licence key goes through NULLABLE and unchecked: the factory owns the
  // no-licence refusal so no component can forget it.
  assert.equal(request.licenseKey, LICENSE_KEY);
  assert.equal(typeof request.fetchImpl, "function");
  assert.equal(typeof request.navigate, "function");
  assert.equal(typeof request.reportError, "function");
  // Both state setters. Drop either and the factory cannot disarm the buttons
  // or surface a refusal, which is the silence #737 is about.
  assert.equal(typeof request.setBusy, "function");
  assert.equal(typeof request.setError, "function");
  // The busy flag's SECOND owner (#947 round 1, finding 3). The factory keeps
  // the flag set once a redirect is scheduled and hands the release to this;
  // omit it and `loadingTier` is stuck for the life of the document whenever
  // the customer abandons the redirect.
  assert.equal(typeof request.onRedirectScheduled, "function");
});

test("a null licence key still reaches the factory, unchecked", async () => {
  await renderCard(null);

  // Not `factoryCalls.length === 0`: a card that re-grew its own
  // `if (!activeLicenseKey) return` would give the seam two owners for one
  // rule and two ways for them to disagree.
  assert.equal(factoryCalls.length, 1);
  assert.equal(factoryCalls[0]?.licenseKey, null);
});

test("the failure copy comes from the buyCredits namespace, not the card's", async () => {
  await renderCard();

  const request = factoryCalls[0];

  // #737 asked for a new translation key. This declines it and reuses the two
  // the sibling `/credits` form already has in all 40 files under `messages/`.
  // The namespace is the assertion: `cloudCreditsCard.errorCheckout` does not
  // exist in any of them and would paint the raw key at the customer.
  assert.equal(request?.checkoutErrorMessage, "buyCredits.errorCheckout");
  assert.equal(request?.genericErrorMessage, "buyCredits.errorGeneric");
});

test("the view's first paint is idle, unfailed and fully labelled", async () => {
  await renderCard();

  const [view] = viewRenders;

  assert.ok(view, "the card rendered no view");
  // The two flags the wrapper owns. They start here and the factory's
  // `setBusy`/`setError` are the only things that move them — which is why a
  // static render of this wrapper can reach no other state, and why the View
  // is tested on its own.
  assert.equal(view.loadingTier, null);
  assert.equal(view.error, null);
  assert.equal(view.activeLicenseKey, LICENSE_KEY);
  // Every string is pre-formatted here, because the View can call no hook.
  assert.equal(view.labels.title, "cloudCreditsCard.title");
  assert.equal(view.labels.topUp, "cloudCreditsCard.topUp");
  assert.deepEqual(
    view.tiers.map((tier) => tier.amount),
    [5, 10],
  );
  assert.equal(view.tiers[0]?.creditsLabel, "cloudCreditsCard.creditsCount");
  assert.equal(view.tiers[0]?.minutesLabel, "cloudCreditsCard.minutes");
  assert.equal(view.minAmount, 5);
  assert.equal(view.maxAmount, 500);
});

test("the buy buttons are joined to the handler the factory built", async () => {
  await renderCard();

  const [view] = viewRenders;

  assert.ok(view, "the card rendered no view");
  assert.equal(typeof view.onBuy, "function");

  // Nothing may fire from rendering alone — a checkout session per paint.
  // `.length`, not `deepEqual(handlerRuns, [])`: the strict `deepStrictEqual`
  // carries an `asserts actual is T` signature, so comparing against a bare
  // `[]` narrows the array to `never[]` for the rest of this function and every
  // assertion below it stops compiling.
  assert.equal(handlerRuns.length, 0);

  view.onBuy(5, 5);
  await Promise.resolve();

  // The end of the chain: the factory's handler, and no other, is what a
  // button runs. A card that built the handler and then wired `onBuy` to
  // something else would satisfy every other test in this file.
  assert.equal(handlerRuns.length, 1);
  assert.equal(handlerRuns[0]?.request, factoryCalls[0]);
  assert.equal(handlerRuns[0]?.amount, 5);
  assert.equal(handlerRuns[0]?.tier, 5);

  // The custom box's sentinel survives the crossing too; the top-up spinner is
  // keyed on it.
  view.onBuy(37, "custom");
  await Promise.resolve();

  assert.equal(handlerRuns.length, 2);
  assert.equal(handlerRuns[1]?.amount, 37);
  assert.equal(handlerRuns[1]?.tier, "custom");
});

test("a reported failure reaches posthog with no licence key on it", async () => {
  await renderCard();

  const request = factoryCalls[0];

  assert.ok(request, "the card never called the factory");

  // The properties are built in the seam, which is where
  // `buy-credits-seam.test.ts` proves the key is absent. What this asserts is
  // the card's half: the guarded hop into `captureException`, which the only
  // other `usePostHog` caller in this app (`purchase-success/page.tsx:37`)
  // also makes. Delete it and a lost sale is recorded nowhere again.
  const thrown = new Error("buy_credits failed: 400");

  request.reportError(thrown, { operation: "buy_credits", status: 400 });

  assert.equal(captured.length, 1);
  assert.equal(captured[0]?.error, thrown);
  assert.deepEqual(captured[0]?.properties, {
    operation: "buy_credits",
    status: 400,
  });
  // This app's PostHog init has no `before_send` and no redaction (#739), so
  // anything that reaches here ships verbatim.
  assert.equal(
    JSON.stringify(captured[0]).includes("HW-TEST"),
    false,
    "the licence key reached the error tracker",
  );
});

test("the card does not navigate by itself", async () => {
  await renderCard();

  const request = factoryCalls[0];

  // `navigate` is one of the card's two window references, and the seam asserts
  // on its own source text that it grew none. Calling it here would assign
  // `window.location.href` under a Node runtime with no `window`, so the throw
  // is the proof that the navigation really is the card's to make and not the
  // seam's.
  assert.throws(() => request?.navigate("https://checkout.stripe.com/c/pay/ok"));
});

test("the abandoned-redirect watch is the card's to arm, not the seam's", async () => {
  await renderCard();

  const request = factoryCalls[0];

  // The card's OTHER window reference, and the same proof. The watch is a
  // `pageshow` listener and a timer on the real page, which the seam may not
  // hold — so a `onRedirectScheduled` that did nothing at all, or one the seam
  // supplied itself, would both pass the `typeof` check above. Only a call
  // that reaches for the page and finds no `window` under Node can tell them
  // apart. What the watch then DOES is `tests/abandoned-redirect.test.ts`.
  //
  // The `typeof` first is not decoration: without it, a card that passed no
  // `onRedirectScheduled` at all makes `request?.onRedirectScheduled(…)` throw
  // a TypeError of its own and `assert.throws` passes for the wrong reason.
  assert.equal(typeof request?.onRedirectScheduled, "function");
  assert.throws(() => request?.onRedirectScheduled(() => {}));
});

test("a customer with no minutes left is handed no minutes line at all", async () => {
  // #947 round 1, finding 2. This used to be two decisions: the wrapper
  // interpolated the string unconditionally and the View separately re-tested
  // `totalMinutesRemaining > 0`. The label now carries the answer, so a caller
  // cannot paint `~0 minutes remaining` by forgetting the second test.
  await renderCard(LICENSE_KEY, 0);

  assert.equal(viewRenders[0]?.labels.minutesRemaining, null);
  // The balance itself is unconditional and still there, so the null above is
  // a decision and not a card that failed to render.
  assert.equal(viewRenders[0]?.totalCredits, 12345);
});

test("a customer with minutes left is handed the interpolated line", async () => {
  await renderCard(LICENSE_KEY, 42);

  // The positive control. `t` echoes `namespace.key` here, so this proves the
  // card asked for its own `cloudCreditsCard.minutesRemaining` and no other.
  assert.equal(
    viewRenders[0]?.labels.minutesRemaining,
    "cloudCreditsCard.minutesRemaining",
  );
});
