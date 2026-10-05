/**
 * The #967 wiring seam: `components/credits/CreditsPurchase.tsx` really feeds
 * `showEmailError` into the email field's error state.
 *
 * `tests/credits-email.test.ts` covers the RULE. It cannot see the three props
 * that put the rule on screen — `onBlur`, `isInvalid` and `errorMessage` on the
 * email `<Input>` — and deleting any one of them leaves it green. This file is
 * what goes red.
 *
 * HOW, with no DOM and no event. The HeroUI `Input` is stubbed so its props can
 * be read. To move the component's state, this file leans on the render-phase
 * update rule `tests/cloud-credits-card-error-state.test.ts` documents: a
 * `useState` setter called synchronously DURING that component's own render is
 * applied inside one `renderToStaticMarkup`, and React re-runs the component
 * until the state settles. The email field's `onChange` and `onBlur` closures
 * are only reachable during that render as the props object handed to the JSX
 * runtime, so `react/jsx-runtime` is wrapped (delegating to the real one) and
 * fires them the moment `CreditsPurchase` builds the email `<Input>` element.
 * The REAL `showEmailError` then decides `isInvalid` on the re-render.
 *
 * WHAT IT DOES NOT PROVE. The real HeroUI `Input` is stubbed, so that it paints
 * `errorMessage` when `isInvalid` is true, and that its DOM `blur` event calls
 * `onBlur`, are HeroUI's behaviour and are not exercised here. Nor is a real
 * user's type-then-tab sequence across two committed client renders: here the
 * `onChange` and `onBlur` setters land in the same render pass. What IS proven:
 * the component's own `onBlur` flips the `touched` state, and that state, the
 * typed value and the component's `EMAIL_RE` result all reach the `isInvalid`
 * prop through `showEmailError`, with `buyCredits.errorEmail` as the message.
 *
 * #968 ADDS the naming pins: the field's own `aria-label` is the visible label
 * text (HeroUI otherwise names a label-less Input by its placeholder), the
 * visible `<label>` points at the field's stable `id`, and `aria-describedby`
 * lists the help line, plus the error while it shows. HeroUI lets a caller's
 * `aria-describedby` replace its own, so the error carries an id of the
 * component's own. Whether the browser computes "Your email" from these props is
 * the verify round's job, not this file's.
 *
 * #1147 ADDS the custom-amount pins. The Custom tile's `onClick` is fired the
 * same render-phase way, so the number `<Input>` renders, and its
 * `aria-labelledby` must name the ids on the visible "Choose an amount" heading
 * and the Custom tile title. `tests/heroui-input-labelledby.test.ts` pins that
 * the real HeroUI Input passes that attribute to the <input> unchanged.
 */
import assert from "node:assert/strict";
import test, { beforeEach, mock } from "node:test";
import { pathToFileURL } from "node:url";
import { createElement, type ReactElement } from "react";
import * as realJsxRuntime from "react/jsx-runtime";
import { renderToStaticMarkup } from "react-dom/server";

/**
 * `mock.module` is a real Node 22 API (behind `--experimental-test-module-mocks`,
 * which `npm test` passes) but this repo pins `@types/node@20`, which has no
 * declaration for it. Same narrowed shape the other mock.module tests here use.
 */
interface ModuleMocker {
  module(
    specifier: string,
    options: { namedExports?: Record<string, unknown> },
  ): void;
}

const moduleMock = mock as unknown as ModuleMocker;

interface InputProps {
  type?: string;
  value?: string;
  onChange?: (event: { target: { value: string } }) => void;
  onBlur?: () => void;
  isInvalid?: boolean;
  errorMessage?: unknown;
  id?: string;
  placeholder?: string;
  "aria-label"?: string;
  "aria-describedby"?: string;
  "aria-labelledby"?: string;
}

/** The static markup of the last render (the stub Input paints nothing). */
let markup = "";

/** What the buyer "does" to the email field during the next render. */
let scenario: { typed?: string; blur?: boolean; custom?: boolean } = {};
/** Whether this render has already dispatched the scenario. */
let fired = false;
/** Whether this render has already clicked the Custom tile. */
let customFired = false;

/** Every email `<Input>` the stub rendered, in order. */
const emailInputs: InputProps[] = [];
/** Every custom-amount (number) `<Input>` the stub rendered, in order. */
const numberInputs: InputProps[] = [];

function StubInput(props: InputProps): null {
  if (props.type === "email") emailInputs.push(props);
  if (props.type === "number") numberInputs.push(props);

  return null;
}

/**
 * By the file the COMPONENT loads. `@heroui/input` ships `dist/index.mjs` for
 * `import` and `dist/index.js` for `require`; this test resolves a bare
 * specifier to the first, the component (compiled to CommonJS by tsx) loads the
 * second, and a mock keyed on the bare name is silently never used.
 */
moduleMock.module(pathToFileURL(require.resolve("@heroui/input")).href, {
  namedExports: { Input: StubInput },
});

/**
 * The real JSX runtime, with one addition: when `CreditsPurchase` builds the
 * email `<Input>` element — which is inside its own render — fire the scenario
 * through that element's own `onChange` / `onBlur`. `fired` stops the settle
 * re-render from dispatching again.
 */
function fireScenario(type: unknown, props: unknown): void {
  clickCustomTile(type, props);
  if (fired || type !== StubInput) return;

  const input = props as InputProps;

  if (input.type !== "email") return;
  fired = true;
  if (scenario.typed !== undefined) {
    input.onChange?.({ target: { value: scenario.typed } });
  }
  if (scenario.blur) input.onBlur?.();
}

/** True when a JSX child is the element rendering `buyCredits.custom`. */
function rendersCustomTitle(child: unknown): boolean {
  const props = (child as { props?: { children?: unknown } } | null)?.props;

  return props?.children === "buyCredits.custom";
}

/** Clicks the Custom tile while it is built, so `isCustom` turns true. */
function clickCustomTile(type: unknown, props: unknown): void {
  if (!scenario.custom || customFired || type !== "button") return;
  const button = props as { children?: unknown; onClick?: () => void };
  const children = Array.isArray(button.children)
    ? button.children
    : [button.children];

  if (!children.some(rendersCustomTitle)) return;
  customFired = true;
  button.onClick?.();
}

type JsxFn = (type: unknown, props: unknown, key?: unknown) => unknown;
const realJsx = realJsxRuntime.jsx as unknown as JsxFn;
const realJsxs = realJsxRuntime.jsxs as unknown as JsxFn;

moduleMock.module("react/jsx-runtime", {
  namedExports: {
    Fragment: realJsxRuntime.Fragment,
    jsx: (type: unknown, props: unknown, key?: unknown) => {
      fireScenario(type, props);

      return realJsx(type, props, key);
    },
    jsxs: (type: unknown, props: unknown, key?: unknown) => {
      fireScenario(type, props);

      return realJsxs(type, props, key);
    },
  },
});

/** `t` echoes `namespace.key`, so the assertion names the namespace too. */
moduleMock.module("next-intl", {
  namedExports: {
    useTranslations:
      (namespace: string) =>
      (key: string): string =>
        `${namespace}.${key}`,
  },
});

// A VARIABLE specifier, and deferred: a static import would bind before the
// `mock.module` calls above, and a literal `.tsx` specifier is a TS5097 error.
const COMPONENT_PATH = "../components/credits/CreditsPurchase.tsx";

type CreditsPurchase = (props: { locale: string }) => ReactElement;

/** Renders the real form once and answers the email field's settled props. */
async function renderEmailInput(
  next: typeof scenario = {},
): Promise<InputProps> {
  scenario = next;
  const { default: Component } = (await import(COMPONENT_PATH)) as {
    default: CreditsPurchase;
  };

  markup = renderToStaticMarkup(createElement(Component, { locale: "en" }));

  // The settle re-render happens before any child renders, so the stub sees
  // the email field exactly once per render, with the final state.
  assert.equal(emailInputs.length, 1, "expected one email Input render");

  return emailInputs[0] as InputProps;
}

/** The error node's id and text. It is a node, so it can carry an id. */
function errorOf(input: InputProps): { id: string | undefined; text: string } {
  const html = renderToStaticMarkup(input.errorMessage as ReactElement);
  const id = /\sid="([^"]+)"/.exec(html)?.[1];

  return { id, text: html.replace(/<[^>]+>/g, "") };
}

/** The ids `aria-describedby` lists. */
function describedBy(input: InputProps): string[] {
  return (input["aria-describedby"] ?? "").split(/\s+/).filter(Boolean);
}

/** The id of the help line in the rendered markup. */
function helpId(): string | undefined {
  return /<p[^>]*\sid="([^"]+)"[^>]*>buyCredits\.emailHelp<\/p>/.exec(
    markup,
  )?.[1];
}

beforeEach(() => {
  markup = "";
  scenario = {};
  fired = false;
  emailInputs.length = 0;
  customFired = false;
  numberInputs.length = 0;
});

test("the email field carries the errorEmail copy and a blur handler", async () => {
  const input = await renderEmailInput();

  assert.equal(errorOf(input).text, "buyCredits.errorEmail");
  assert.equal(typeof input.onBlur, "function");
  // A fresh form is unfinished, not wrong.
  assert.equal(input.isInvalid, false);
});

test("a rejected address shows no error before the field is left", async () => {
  const input = await renderEmailInput({ typed: "me@gmail" });

  assert.equal(input.value, "me@gmail", "the typed value never landed");
  assert.equal(input.isInvalid, false);
});

test("a rejected address shows the error once the field is left", async () => {
  // #967: `me@gmail` left the checkout button dead with no reason given.
  const input = await renderEmailInput({ typed: "me@gmail", blur: true });

  assert.equal(input.value, "me@gmail", "the typed value never landed");
  assert.equal(input.isInvalid, true);
  assert.equal(errorOf(input).text, "buyCredits.errorEmail");
});

test("an accepted address shows nothing after the field is left", async () => {
  // The positive control for the test above: the component's own EMAIL_RE
  // result, not only the touched flag, reaches `isInvalid`.
  const input = await renderEmailInput({ typed: "me@gmail.com", blur: true });

  assert.equal(input.value, "me@gmail.com", "the typed value never landed");
  assert.equal(input.isInvalid, false);
});

test("an empty field shows nothing after the field is left", async () => {
  const input = await renderEmailInput({ blur: true });

  assert.equal(input.isInvalid, false);
});

test("#968: the field is named by its visible label, not the placeholder", async () => {
  const input = await renderEmailInput();

  assert.equal(input["aria-label"], "buyCredits.emailLabel");
  assert.equal(input.placeholder, "buyCredits.emailPlaceholder");
  assert.ok(input.id, "the email Input has no stable id");
  const label =
    /<label[^>]*\sfor="([^"]+)"[^>]*>buyCredits\.emailLabel<\/label>/.exec(
      markup,
    );

  assert.equal(
    label?.[1],
    input.id,
    "the visible label does not point at the field",
  );
});

test("#968: a fresh field is described by the help line only", async () => {
  const input = await renderEmailInput();
  const help = helpId();

  assert.ok(help, "the help line has no id");
  assert.deepEqual(describedBy(input), [help]);
});

test("#968: a rejected field is described by the help line and the error", async () => {
  const input = await renderEmailInput({ typed: "me@gmail", blur: true });
  const error = errorOf(input);

  assert.equal(input.isInvalid, true);
  assert.ok(error.id, "the error message has no id");
  assert.notEqual(error.id, input.id);
  assert.deepEqual(describedBy(input), [helpId(), error.id]);
});

test("#968: an accepted field drops the error from its description", async () => {
  const input = await renderEmailInput({ typed: "me@gmail.com", blur: true });

  assert.deepEqual(describedBy(input), [helpId()]);
});

/** Renders the form with the Custom tile chosen; answers the number Input. */
async function renderCustomInput(): Promise<InputProps> {
  await renderEmailInput({ custom: true });
  assert.equal(numberInputs.length, 1, "expected one custom-amount Input");

  return numberInputs[0] as InputProps;
}

/** The id on the element whose whole text is `key`, in the markup. */
function idOfText(tag: string, key: string): string | undefined {
  const re = new RegExp(
    `<${tag}[^>]*\\sid="([^"]+)"[^>]*>${key.replace(".", "\\.")}</${tag}>`,
  );

  return re.exec(markup)?.[1];
}

test("#1147: the custom-amount field is hidden until Custom is chosen", async () => {
  await renderEmailInput();

  assert.equal(numberInputs.length, 0);
});

test("#1147: the custom-amount field is named by the visible amount heading and Custom title", async () => {
  const input = await renderCustomInput();
  const amountId = idOfText("span", "buyCredits.amountLabel");
  const customId = idOfText("div", "buyCredits.custom");

  assert.ok(amountId, "the Choose an amount heading has no id");
  assert.ok(customId, "the Custom tile title has no id");
  assert.notEqual(amountId, customId);
  assert.deepEqual(
    (input["aria-labelledby"] ?? "").split(/\s+/).filter(Boolean),
    [amountId, customId],
  );
  // The name must not come from the placeholder: the component sets no
  // aria-label of its own (HeroUI's placeholder fallback loses to labelledby).
  assert.equal(input["aria-label"], undefined);
  assert.equal(input.placeholder, "buyCredits.customPlaceholder");
});

/**
 * #969 ADDS the amount-group pins. The $5 / $10 / Custom cards are a
 * single-choice group that showed the chosen card by border colour only. Each
 * card now carries `aria-pressed`, and the grid is a `role="group"` named by
 * the visible "Choose an amount" heading (`aria-labelledby`). The cards stay
 * toggle buttons, not `role="radio"` (that needs arrow-key roving focus).
 * Whether Chromium computes the group name and the [pressed] state is the
 * verify round's job.
 */

/** The opening tag of the element that holds the amount cards. */
function amountGroupTag(): string {
  const tag = /<div[^>]*\srole="group"[^>]*>/.exec(markup)?.[0];

  assert.ok(tag, "no role=group element in the markup");

  return tag;
}

/** Each amount card's text and `aria-pressed` value, in document order. */
function amountCards(): Array<{ text: string; pressed: string | null }> {
  const start = markup.indexOf(amountGroupTag());
  const buttons = Array.from(
    markup.slice(start).matchAll(/<button\b([^>]*)>([\s\S]*?)<\/button>/g),
  );

  return buttons.map((m) => ({
    text: m[2].replace(/<[^>]+>/g, ""),
    pressed: /\saria-pressed="([^"]+)"/.exec(m[1])?.[1] ?? null,
  }));
}

test("#969: the amount cards are a group named by the Choose an amount heading", async () => {
  await renderEmailInput();
  const amountId = idOfText("span", "buyCredits.amountLabel");
  const tag = amountGroupTag();

  assert.ok(amountId, "the Choose an amount heading has no id");
  assert.equal(/\saria-labelledby="([^"]+)"/.exec(tag)?.[1], amountId);
  assert.equal(
    Array.from(markup.matchAll(/\srole="group"/g)).length,
    1,
    "expected exactly one group",
  );
});

test("#969: on load only the $5 card is pressed", async () => {
  await renderEmailInput();
  const cards = amountCards();

  assert.equal(cards.length >= 3, true, "expected the $5, $10 and Custom cards");
  assert.deepEqual(
    cards.slice(0, 3).map((c) => c.pressed),
    ["true", "false", "false"],
  );
  assert.match(cards[0].text, /^\$5/);
  assert.match(cards[1].text, /^\$10/);
  assert.match(cards[2].text, /^buyCredits\.custom/);
});

test("#969: after a click on Custom only the Custom card is pressed", async () => {
  await renderEmailInput({ custom: true });
  const cards = amountCards();

  assert.deepEqual(
    cards.slice(0, 3).map((c) => c.pressed),
    ["false", "false", "true"],
  );
  assert.match(cards[2].text, /^buyCredits\.custom/);
});
