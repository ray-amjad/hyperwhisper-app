/**
 * The credits card's MARKUP and its click wiring:
 * `components/customer/dashboard/CloudCreditsCardView.tsx` (#737).
 *
 * #737 is a checkout the server refused and nobody was told about. Proving the
 * fix needs two things a `renderToStaticMarkup` of the STATEFUL card cannot
 * give: a render in the failed state, and a button whose `onClick` is visible.
 * There is no jsdom, no testing-library and no `act()` in this repo, so the
 * only rendering surface is `renderToStaticMarkup`, it renders ONCE, and the
 * only state it reaches is the initial one — not busy and not failed. React
 * does not serialise an event handler either. That is exactly how the #881
 * round 1 suite stayed green with the button's `onClick` deleted.
 *
 * `CloudCreditsCardView` exists to close both holes. It holds NO hooks, so:
 *
 * - it can be CALLED as a plain function, and the element tree it returns still
 *   carries the real `onClick`, which this file invokes;
 * - `loadingTier` and `error` are PROPS, so the busy render and the failed
 *   render are reachable and every markup assertion below is POSITIVE.
 *
 * The wrapper that owns those props is `tests/cloud-credits-card-wiring.test.ts`.
 * That the wrapper's own `setError` really reaches the `error` prop is
 * `tests/cloud-credits-card-error-state.test.ts`. The decision the handler makes
 * is `tests/buy-credits-seam.test.ts`.
 */
import assert from "node:assert/strict";
import test from "node:test";
import {
  Children,
  createElement,
  isValidElement,
  type ReactElement,
  type ReactNode,
} from "react";
import { renderToStaticMarkup } from "react-dom/server";

import type {
  CloudCreditsCardViewProps,
  CloudCreditsTier,
} from "../components/customer/dashboard/CloudCreditsCardView";

// A VARIABLE specifier, and deferred behind `await import`: a literal `.tsx`
// specifier is a TS5097 error under this tsconfig. The other `.tsx` tests in
// this folder document the same rule. No `mock.module` call is needed here at
// all — the View imports nothing but `react`, which is what "leaf" buys.
const VIEW_PATH = "../components/customer/dashboard/CloudCreditsCardView.tsx";

type CloudCreditsCardView = (props: CloudCreditsCardViewProps) => ReactElement;

async function loadView(): Promise<CloudCreditsCardView> {
  const { default: CloudCreditsCardView } = (await import(VIEW_PATH)) as {
    default: CloudCreditsCardView;
  };

  return CloudCreditsCardView;
}

/** Every `onBuy(amount, tier)` a test's click produced, in order. */
type BuyCall = [number, CloudCreditsTier];

/**
 * The props of a dashboard's first paint: a licence key present (the buy block,
 * and with it the error region, lives inside `activeLicenseKey && (…)`), no
 * checkout in flight and nothing refused yet.
 */
function propsFor(
  overrides: Partial<CloudCreditsCardViewProps> = {},
): CloudCreditsCardViewProps {
  return {
    totalCredits: 12345,
    totalMinutesRemaining: 42,
    activeLicenseKey: "HW-TEST-0000-1111-2222",
    tiers: [
      { amount: 5, creditsLabel: "5,000 credits", minutesLabel: "~50 min" },
      { amount: 10, creditsLabel: "10,000 credits", minutesLabel: "~100 min" },
    ],
    labels: {
      title: "Cloud Credits",
      minutesRemaining: "42 minutes remaining",
      custom: "Custom",
      customSub: "Choose amount",
      topUp: "Top up",
    },
    loadingTier: null,
    error: null,
    showCustom: false,
    onToggleCustom: () => {},
    customAmount: "",
    onCustomAmountChange: () => {},
    customValid: false,
    minAmount: 5,
    maxAmount: 500,
    onBuy: () => {},
    ...overrides,
  };
}

/** Renders the real view to the bytes a browser would receive. */
async function renderView(
  overrides: Partial<CloudCreditsCardViewProps> = {},
): Promise<string> {
  const CloudCreditsCardView = await loadView();

  return renderToStaticMarkup(
    createElement(CloudCreditsCardView, propsFor(overrides)),
  );
}

type ButtonElement = ReactElement<{
  onClick?: () => void;
  disabled?: boolean;
  children?: ReactNode;
}>;

/**
 * EVERY `<button>` in a returned element tree, in document order.
 *
 * `findButton`-returns-the-first (`user-header-view.test.ts:114`) is not enough
 * here: this card paints at least three buttons — two tier presets, the custom
 * toggle, and a fourth "Top up" once the custom box is open — and the
 * assertions below are about which of them is wired to what.
 *
 * It walks the tree the component RETURNS, not its markup, because a handler is
 * exactly what markup cannot show. `Children.toArray` drops the `false` arms of
 * the conditional regions and flattens the arrays; the recursion covers the
 * nesting and the fragments.
 */
function findButtons(node: ReactNode): ButtonElement[] {
  const found: ButtonElement[] = [];

  for (const child of Children.toArray(node)) {
    if (!isValidElement(child)) continue;

    if (child.type === "button") found.push(child as ButtonElement);

    found.push(
      ...findButtons((child.props as { children?: ReactNode }).children),
    );
  }

  return found;
}

test("a refused checkout is announced under the tier buttons", async () => {
  const markup = await renderView({ error: "Amount too large" });

  // #737's Done-when item 1, as literally as this repo can state it: the exact
  // string the route sent, on screen, in a live region. Reachable ONLY because
  // `error` is a prop — the stateful card's first render can never carry one.
  assert.match(
    markup,
    /<span[^>]*role="alert"[^>]*>Amount too large<\/span>/,
  );

  // Under the tier buttons, not above them: the region is the last thing in
  // the buy block, so the $10 preset is painted before it.
  assert.ok(
    markup.indexOf(">$10<") < markup.indexOf('role="alert"'),
    "the alert region is painted before the tier buttons",
  );

  // A refusal leaves the customer on this page, so the buttons come back.
  assert.doesNotMatch(markup, /<button[^>]*\sdisabled=""/);
});

test("the card announces nothing before a checkout has failed", async () => {
  // A live region that is present and empty on every page load is an assistive
  // technology annoyance, and it would also mean the span renders with no
  // message. Safe as a negative only because the test above proves the region
  // exists when there IS an error.
  const markup = await renderView({ error: null });

  assert.doesNotMatch(markup, /role="alert"/);
});

test("a card with no licence key paints no buy block and no alert", async () => {
  // There is no button to fail without a key, so the region must not appear —
  // and this is the guard the error region sits inside.
  const markup = await renderView({
    activeLicenseKey: null,
    error: "Amount too large",
  });

  assert.doesNotMatch(markup, /<button/);
  assert.doesNotMatch(markup, /role="alert"/);
  assert.doesNotMatch(markup, /Amount too large/);
  // The balance itself is outside the guard and still shows.
  assert.match(markup, /12,345/);
});

test("an idle card arms every button and shows the tier copy", async () => {
  const markup = await renderView();

  assert.match(markup, />\$5</);
  assert.match(markup, />\$10</);
  assert.match(markup, />5,000 credits</);
  // React serialises a true `disabled` as `disabled=""` and omits a false one.
  // Anchored on the `=` because this card's Tailwind classes carry the bare
  // word `disabled:` on three separate buttons — a `/disabled/` match here
  // would be vacuous (`user-header-view.test.ts:171-174`).
  assert.doesNotMatch(markup, /disabled="/);
  assert.doesNotMatch(markup, /animate-spin/);
});

test("a checkout in flight disarms the tier buttons and spins the one clicked", async () => {
  const markup = await renderView({ loadingTier: 5 });

  // POSITIVE, and reachable only because `loadingTier` is a prop. The $5
  // button swaps its label for a spinner; the $10 one keeps its label and is
  // disarmed, so a second checkout cannot be started for the same top-up.
  assert.match(markup, /<button[^>]*\sdisabled=""[^>]*>\s*<div[^>]*animate-spin/);
  assert.doesNotMatch(markup, />\$5</);
  assert.match(markup, />\$10</);
});

test("every button is disarmed while any tier is in flight", async () => {
  // The markup above cannot say WHICH buttons carry the flag, because the
  // custom toggle has a `disabled` of its own — delete `disabled={isDisabled}`
  // from the tier buttons and the string `disabled=""` is still in the bytes.
  // The element tree can, so this is where that mutation dies.
  const CloudCreditsCardView = await loadView();
  const buttons = findButtons(
    CloudCreditsCardView(propsFor({ loadingTier: 5 })),
  );

  assert.equal(buttons.length, 3, "expected two tier buttons and the toggle");
  assert.deepEqual(
    buttons.map((button) => button.props.disabled),
    [true, true, true],
  );

  const idle = findButtons(CloudCreditsCardView(propsFor()));

  assert.deepEqual(
    idle.map((button) => button.props.disabled),
    [false, false, false],
  );
});

test("the $5 button buys five dollars of credit when it is clicked", async () => {
  const CloudCreditsCardView = await loadView();
  const bought: BuyCall[] = [];

  // Called as a plain function, not rendered: this view holds no hooks, so the
  // tree it returns is the real one and it still carries the real handler.
  const buttons = findButtons(
    CloudCreditsCardView(
      propsFor({ onBuy: (amount, tier) => bought.push([amount, tier]) }),
    ),
  );

  assert.equal(buttons.length, 3);

  // The whole of the #881 round 2 lesson. Deleting this `onClick` left that
  // suite at 5 pass / 0 fail, because React never serialises a handler into
  // static markup.
  assert.equal(
    typeof buttons[0].props.onClick,
    "function",
    "the $5 button has no onClick",
  );

  buttons[0].props.onClick?.();

  // The tier argument is the dollar amount for a preset — the spinner in the
  // test above is keyed on it.
  assert.deepEqual(bought, [[5, 5]]);

  buttons[1].props.onClick?.();

  assert.deepEqual(bought, [
    [5, 5],
    [10, 10],
  ]);
});

test("the top-up button buys the typed amount under the custom sentinel", async () => {
  const CloudCreditsCardView = await loadView();
  const bought: BuyCall[] = [];

  const buttons = findButtons(
    CloudCreditsCardView(
      propsFor({
        showCustom: true,
        customAmount: "37",
        customValid: true,
        onBuy: (amount, tier) => bought.push([amount, tier]),
      }),
    ),
  );

  assert.equal(buttons.length, 4, "the open custom box adds a fourth button");

  buttons[3].props.onClick?.();

  // A NUMBER, not the "37" string the input holds, and the `"custom"`
  // sentinel rather than 37 — the seam sends the number to the route and the
  // card keys the top-up spinner on the sentinel.
  assert.deepEqual(bought, [[37, "custom"]]);
});

test("nothing is bought by rendering alone", async () => {
  const CloudCreditsCardView = await loadView();
  const bought: BuyCall[] = [];

  const tree = CloudCreditsCardView(
    propsFor({
      showCustom: true,
      customAmount: "37",
      customValid: true,
      onBuy: (amount, tier) => bought.push([amount, tier]),
    }),
  );

  // A handler called in the render body would create a checkout session on
  // every paint of the dashboard.
  renderToStaticMarkup(tree);

  assert.deepEqual(bought, []);
});

test("an invalid custom amount disarms the top-up button", async () => {
  const CloudCreditsCardView = await loadView();
  const buttons = findButtons(
    CloudCreditsCardView(
      propsFor({ showCustom: true, customAmount: "1", customValid: false }),
    ),
  );

  assert.equal(buttons.length, 4);
  assert.equal(buttons[3].props.disabled, true);
});
