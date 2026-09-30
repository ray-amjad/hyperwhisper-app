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
}

/** What the buyer "does" to the email field during the next render. */
let scenario: { typed?: string; blur?: boolean } = {};
/** Whether this render has already dispatched the scenario. */
let fired = false;

/** Every email `<Input>` the stub rendered, in order. */
const emailInputs: InputProps[] = [];

function StubInput(props: InputProps): null {
  if (props.type === "email") emailInputs.push(props);

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
  if (fired || type !== StubInput) return;

  const input = props as InputProps;

  if (input.type !== "email") return;
  fired = true;
  if (scenario.typed !== undefined) {
    input.onChange?.({ target: { value: scenario.typed } });
  }
  if (scenario.blur) input.onBlur?.();
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

  renderToStaticMarkup(createElement(Component, { locale: "en" }));

  // The settle re-render happens before any child renders, so the stub sees
  // the email field exactly once per render, with the final state.
  assert.equal(emailInputs.length, 1, "expected one email Input render");

  return emailInputs[0] as InputProps;
}

beforeEach(() => {
  scenario = {};
  fired = false;
  emailInputs.length = 0;
});

test("the email field carries the errorEmail copy and a blur handler", async () => {
  const input = await renderEmailInput();

  assert.equal(input.errorMessage, "buyCredits.errorEmail");
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
  assert.equal(input.errorMessage, "buyCredits.errorEmail");
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
