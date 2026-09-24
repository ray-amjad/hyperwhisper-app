/**
 * The last link in the #737 chain: the `setError` the card hands
 * `createBuyCreditsHandler` really is the thing that paints the View's error
 * region.
 *
 * Three other files already pass and none of them can see this. The seam test
 * proves the handler calls whatever `onError` it was given. The View test
 * proves the View paints whatever `error` prop it was given. The wiring test
 * mocks the View away and asserts only `typeof request.setError === "function"`.
 * A card that handed the factory a `() => {}`, or handed the View a literal
 * `error={null}`, is green in all three — and the customer is back to a
 * spinner that stops and nothing else, which is #737 verbatim.
 *
 * So this file mounts the REAL View, with no mock on it, and drives the real
 * `setError`.
 *
 * HOW, with no DOM and no click. React 19's server renderer accepts a
 * RENDER-PHASE state update: `dispatchAction` in
 * `node_modules/react-dom/cjs/react-dom-server-legacy.node.development.js`
 * takes an update when `componentIdentity === currentlyRenderingComponent`,
 * sets `didScheduleRenderPhaseUpdate`, and `renderWithHooks` then re-invokes
 * the component (up to 25 times) until the state settles. So a `useState`
 * setter called synchronously DURING that component's own render does take
 * effect inside a single `renderToStaticMarkup`, and the markup returned is
 * the markup of the updated state. An update dispatched from anywhere else
 * during a server render is dropped in silence.
 *
 * The factory stub below is what dispatches it, because Phase 2 requires the
 * `createBuyCreditsHandler` call to sit in the card's RENDER BODY. That is not
 * only a testability preference: move the call into a `useEffect` or inline it
 * into `onClick` and the setter fires outside `currentlyRenderingComponent`,
 * React drops it, and this file goes red. This file is the enforcement.
 *
 * WHAT IT DOES NOT PROVE. The link is proven for a setter called during
 * render. Production calls it AFTER an awaited `fetch`, from a click handler,
 * and the repaint is a commit only a client renderer performs. Nothing here
 * can drive that: no jsdom, no testing-library, no `act()`, and #737 does not
 * add one. The uncovered slice is a real click → async `setError` → committed
 * re-render. It is recorded as a named gap in the pull request, not hidden
 * behind this file's green tick.
 */
import assert from "node:assert/strict";
import test, { beforeEach, mock } from "node:test";
import { createElement, type ReactElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";

import type { BuyCreditsHandlerRequest } from "../src/lib/buy-credits";

/**
 * `mock.module` is a real Node 22 API (behind `--experimental-test-module-mocks`,
 * which `npm test` passes) but this repo pins `@types/node@20`, which has no
 * declaration for it. Narrowed to the one option used here rather than bumping
 * the types in a test-only change.
 */
interface ModuleMocker {
  module(
    specifier: string,
    options: { namedExports?: Record<string, unknown> },
  ): void;
}

const moduleMock = mock as unknown as ModuleMocker;

/** The message the route sends for the amount #737 was filed about. */
const REFUSAL = "Amount too large";

/** Every `createBuyCreditsHandler` call the card made, in order. */
const factoryCalls: BuyCreditsHandlerRequest[] = [];

/**
 * Whether the refusal has already been dispatched for the render in progress.
 *
 * Without it the re-render calls `setError` again with the same string. React
 * bails out of a render-phase update whose state is `Object.is`-equal, so it
 * would settle rather than hang — but a stub that fires on every pass is a
 * stub one careless edit away from the 25-deep `Too many re-renders` throw,
 * and the flag also gives the second test below a render with NO dispatch.
 */
let armed = false;

moduleMock.module("../src/lib/buy-credits", {
  namedExports: {
    createBuyCreditsHandler: (request: BuyCreditsHandlerRequest) => {
      factoryCalls.push(request);

      if (armed) {
        armed = false;

        // Checked before it is called, so mutation `j` — dropping `setError`
        // from the object the card builds — fails HERE with a named message
        // rather than as a confusing `not a function` inside React.
        assert.equal(
          typeof request.setError,
          "function",
          "the card handed the factory no setError",
        );

        // The render-phase dispatch. This is the seam's `onError`, called
        // exactly as `buyCreditsAndRedirect` calls it on a 400.
        request.setError(REFUSAL);
      }

      return async () => {};
    },
  },
});

/** `t` echoes `namespace.key`, so no asserted string can come from a label. */
moduleMock.module("next-intl", {
  namedExports: {
    useTranslations:
      (namespace: string) =>
      (key: string): string =>
        `${namespace}.${key}`,
  },
});

moduleMock.module("posthog-js/react", {
  namedExports: { usePostHog: () => ({ captureException: () => {} }) },
});

// DELIBERATELY NOT MOCKED: the View. It is the whole point of this file, and it
// is safe to mount — `CloudCreditsCard.tsx` imports only `react`, `next-intl`,
// `posthog-js/react`, the credits validation helpers and the seam, so there is
// no HeroUI and no locale-aware `Link` needing a stub of its own.

// A VARIABLE specifier, and deferred: a static import would bind before the
// `mock.module` calls above, and a literal `.tsx` specifier is a TS5097 error
// under this tsconfig.
const CARD_PATH = "../components/customer/dashboard/CloudCreditsCard.tsx";

type CloudCreditsCard = (props: {
  totalCredits: number;
  totalMinutesRemaining: number;
  creditsPerMinute: number;
  activeLicenseKey: string | null;
}) => ReactElement;

/**
 * Renders the real card, with the real View inside it, as the dashboard does.
 *
 * `activeLicenseKey` is mandatory and never null here: the buy block, and with
 * it the error region, lives inside `activeLicenseKey && (…)`.
 */
async function renderCard(): Promise<string> {
  const { default: CloudCreditsCard } = (await import(CARD_PATH)) as {
    default: CloudCreditsCard;
  };

  return renderToStaticMarkup(
    createElement(CloudCreditsCard, {
      totalCredits: 12345,
      totalMinutesRemaining: 42,
      creditsPerMinute: 100,
      activeLicenseKey: "HW-TEST-0000-1111-2222",
    }),
  );
}

beforeEach(() => {
  factoryCalls.length = 0;
  armed = false;
});

test("the setError the card hands the factory paints the real error region", async () => {
  armed = true;

  const markup = await renderCard();

  assert.equal(factoryCalls.length >= 1, true, "the card never called the factory");
  // The factory was called, so the dispatch happened.
  assert.equal(armed, false, "the stub never reached the setError call");

  // #737's Done-when item 1, end to end through the components: the exact
  // string a refusing route sends, out of the card's own `useState`, through
  // the `error` prop, into the live region of the REAL View. Replace the
  // card's `error={error}` with `error={null}` and this is the only assertion
  // in the repo that notices.
  assert.match(markup, /<span[^>]*role="alert"[^>]*>Amount too large<\/span>/);

  // Not a label leaking through: every translated string in this render is an
  // echoed `namespace.key`, so `Amount too large` can only have come from the
  // setter.
  assert.doesNotMatch(markup, /errorCheckout/);
  assert.doesNotMatch(markup, /errorGeneric/);
});

test("the same render with no setError call paints no live region", async () => {
  // The control. Without it the test above could pass on markup the card
  // renders unconditionally, and the `setError` link would still be unproven.
  const markup = await renderCard();

  assert.equal(factoryCalls.length >= 1, true, "the card never called the factory");
  assert.doesNotMatch(markup, /role="alert"/);
  assert.doesNotMatch(markup, /Amount too large/);
  // …while the rest of the card is unmistakably there, so the negative above
  // cannot be passing on an empty string.
  assert.match(markup, />\$5</);
  assert.match(markup, />\$10</);
});

test("the factory call sits in the render body, where a setter can reach it", async () => {
  armed = true;

  await renderCard();

  // React accepts a state update during a server render ONLY from the
  // component currently rendering. That the dispatch above took effect is
  // itself the proof the call is in the render body — a `useEffect` never runs
  // in `renderToStaticMarkup`, and an `onClick` is never invoked. This
  // assertion states the rule the first test silently depends on.
  const request = factoryCalls[0];

  assert.ok(request, "the card never called the factory");
  assert.equal(typeof request.setError, "function");
  assert.equal(typeof request.setBusy, "function");
});
