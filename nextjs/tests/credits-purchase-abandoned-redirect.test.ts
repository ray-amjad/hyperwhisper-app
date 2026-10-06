/**
 * #948: the guest `/credits` form (`components/credits/CreditsPurchase.tsx`)
 * must not leave its checkout button dead for good after an abandoned Stripe
 * redirect.
 *
 * The form keeps `loading` set once it has assigned `window.location.href`,
 * because that only SCHEDULES the load and re-arming the button underneath it
 * invites a second checkout session. Before #948 nothing ever released it, so a
 * buyer who came Back from Stripe out of the bfcache found a disabled button
 * and a spinner until a reload. The form now hands the release to
 * `watchForAbandonedRedirect` — the same watcher the dashboard card uses —
 * which runs it on a bfcache restore and on nothing else.
 *
 * HOW, with no DOM and no click. The HeroUI `Input` and `Button` are stubbed so
 * their props can be read; the email is typed through the render-phase rule
 * `tests/credits-purchase-email-wiring.test.ts` documents, so the settled
 * render's `onPress` closes over a valid address. That `onPress` is then
 * called directly, against a fake `window` (a fake page with a `location` and
 * a `pageshow` listener list) and a stubbed `fetch`. React's `useState` is
 * wrapped so every setter call is recorded: the call happens after the static
 * render is gone, so the setter's ARGUMENTS are the only trace of what the
 * component did to `loading`. The loading setter is the one the click calls
 * with `true`.
 */
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import test, { beforeEach, mock } from "node:test";
import { pathToFileURL } from "node:url";
import * as realJsxRuntime from "react/jsx-runtime";

import type { RedirectPageShowEvent } from "../src/lib/abandoned-redirect";

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
const require = createRequire(import.meta.url);

const realReact = require("react") as typeof import("react");
// Captured first: the mock below can write its exports back onto this object.
const realUseState = realReact.useState;

/** Every state-setter call, by the setter's real identity. */
const setterCalls: Array<{ setter: unknown; value: unknown }> = [];

moduleMock.module("react", {
  defaultExport: realReact,
  namedExports: {
    ...realReact,
    useState: (initial: unknown) => {
      const [value, setter] = realUseState(initial);

      return [
        value,
        (next: unknown) => {
          setterCalls.push({ setter, value: next });
          setter(next as never);
        },
      ];
    },
  },
});

interface InputProps {
  type?: string;
  onChange?: (event: { target: { value: string } }) => void;
}

interface ButtonProps {
  children?: unknown;
  isLoading?: boolean;
  onPress?: () => unknown;
}

function StubInput(): null {
  return null;
}

/** The checkout button's props, from the latest (settled) render. */
let checkoutButton: ButtonProps | null = null;

function StubButton(props: ButtonProps): null {
  if (props.children === "buyCredits.ctaIdle") checkoutButton = props;

  return null;
}

// By the file the COMPONENT loads — see the note in
// `credits-purchase-email-wiring.test.ts` on `index.mjs` versus `index.js`.
moduleMock.module(pathToFileURL(require.resolve("@heroui/input")).href, {
  namedExports: { Input: StubInput },
});
moduleMock.module(pathToFileURL(require.resolve("@heroui/button")).href, {
  namedExports: { Button: StubButton },
});

/** Whether this render has already typed the address. */
let typed = false;

function typeEmail(type: unknown, props: unknown): void {
  if (typed || type !== StubInput) return;
  const input = props as InputProps;

  if (input.type !== "email") return;
  typed = true;
  input.onChange?.({ target: { value: "buyer@example.com" } });
}

type JsxFn = (type: unknown, props: unknown, key?: unknown) => unknown;
const realJsx = realJsxRuntime.jsx as unknown as JsxFn;
const realJsxs = realJsxRuntime.jsxs as unknown as JsxFn;

moduleMock.module("react/jsx-runtime", {
  namedExports: {
    Fragment: realJsxRuntime.Fragment,
    jsx: (type: unknown, props: unknown, key?: unknown) => {
      typeEmail(type, props);

      return realJsx(type, props, key);
    },
    jsxs: (type: unknown, props: unknown, key?: unknown) => {
      typeEmail(type, props);

      return realJsxs(type, props, key);
    },
  },
});

moduleMock.module("next-intl", {
  namedExports: {
    useTranslations:
      (namespace: string) =>
      (key: string): string =>
        `${namespace}.${key}`,
  },
});

const COMPONENT_PATH = "../components/credits/CreditsPurchase.tsx";
const CHECKOUT_URL = "https://checkout.stripe.com/c/pay/cs_test_948";

/** A stand-in for `window`: a location and a `pageshow` listener list. */
function fakePage(options: { refuseListener?: boolean } = {}) {
  const listeners: Array<(event: RedirectPageShowEvent) => void> = [];

  const window = {
    location: { href: "https://hyperwhisper.com/en/credits" },
    addEventListener: (
      _type: string,
      listener: (event: RedirectPageShowEvent) => void,
    ) => {
      if (options.refuseListener) throw new Error("listener refused");
      listeners.push(listener);
    },
    removeEventListener: (
      _type: string,
      listener: (event: RedirectPageShowEvent) => void,
    ) => {
      const at = listeners.indexOf(listener);

      if (at >= 0) listeners.splice(at, 1);
    },
  };

  return {
    window,
    listeners,
    pageshow(persisted: boolean) {
      for (const listener of listeners.slice()) listener({ persisted });
    },
  };
}

type FetchAnswer = { ok: boolean; json: unknown };

/**
 * Renders the real form with a valid address typed, then presses its checkout
 * button against `page` and a `fetch` that answers `answer`. Answers the
 * setter calls the press made, and the console lines it wrote.
 */
async function pressCheckout(
  page: ReturnType<typeof fakePage>,
  answer: FetchAnswer,
): Promise<{ logged: unknown[][] }> {
  const { createElement } = await import("react");
  const { renderToStaticMarkup } = await import("react-dom/server");
  const { default: Component } = (await import(COMPONENT_PATH)) as {
    default: (props: { locale: string }) => null;
  };

  renderToStaticMarkup(createElement(Component, { locale: "en" }));
  assert.ok(checkoutButton?.onPress, "the checkout button has no onPress");
  assert.equal(checkoutButton.isLoading, false);

  // Only what the press does is recorded below.
  setterCalls.length = 0;

  const globals = globalThis as unknown as Record<string, unknown>;
  const realFetch = globals.fetch;
  const quiet = console.error;
  const logged: unknown[][] = [];

  globals.window = page.window;
  globals.fetch = async () => ({
    ok: answer.ok,
    json: async () => answer.json,
  });
  console.error = (...args: unknown[]) => {
    logged.push(args);
  };
  try {
    await checkoutButton.onPress();
  } finally {
    console.error = quiet;
    globals.fetch = realFetch;
  }

  return { logged };
}

/** The values the loading setter received — the setter the press set `true`. */
function loadingValues(): unknown[] {
  const armed = setterCalls.find((call) => call.value === true);

  assert.ok(armed, "the press never set loading");

  return setterCalls
    .filter((call) => call.setter === armed.setter)
    .map((call) => call.value);
}

beforeEach(() => {
  setterCalls.length = 0;
  checkoutButton = null;
  typed = false;
  delete (globalThis as Record<string, unknown>).window;
});

test("#948: a scheduled Stripe redirect keeps the button disarmed", async () => {
  const page = fakePage();

  await pressCheckout(page, { ok: true, json: { checkoutUrl: CHECKOUT_URL } });

  assert.equal(page.window.location.href, CHECKOUT_URL);
  // Still disarmed while the navigation may commit — a second press here is
  // a second checkout session.
  assert.deepEqual(loadingValues(), [true]);
  // …but the release is handed to the abandoned-redirect watch.
  assert.equal(page.listeners.length, 1);
});

test("#948: Back from Stripe out of the bfcache re-arms the checkout button", async () => {
  const page = fakePage();

  await pressCheckout(page, { ok: true, json: { checkoutUrl: CHECKOUT_URL } });
  page.pageshow(true);

  // Before #948 this stayed `[true]` for the life of the document.
  assert.deepEqual(loadingValues(), [true, false]);
  // The watch removed its own listener: nothing leaks per abandoned checkout.
  assert.equal(page.listeners.length, 0);
});

test("#948: an ordinary page load re-arms nothing", async () => {
  const page = fakePage();

  await pressCheckout(page, { ok: true, json: { checkoutUrl: CHECKOUT_URL } });
  page.pageshow(false);

  assert.deepEqual(loadingValues(), [true]);
  assert.equal(page.listeners.length, 1);
});

test("#948: a refused watch handover re-arms nothing and paints no error", async () => {
  const page = fakePage({ refuseListener: true });

  const { logged } = await pressCheckout(page, {
    ok: true,
    json: { checkoutUrl: CHECKOUT_URL },
  });

  // The redirect is already scheduled. Falling into the form's own `catch`
  // would re-arm the button under it and show `errorGeneric` over a page that
  // is leaving — the trade `buy-credits.ts` declines too (option A).
  assert.equal(page.window.location.href, CHECKOUT_URL);
  assert.deepEqual(loadingValues(), [true]);
  const errorWrites = setterCalls
    .map((call) => call.value)
    .filter((value) => typeof value === "string");

  assert.deepEqual(errorWrites, []);
  assert.equal(logged.length, 1);
});

test("#948: a refused checkout arms no abandoned-redirect watch", async () => {
  const page = fakePage();

  await pressCheckout(page, {
    ok: false,
    json: { error: "Amount too large" },
  });

  // The buyer never left, so the form releases at once and nothing else may
  // own the flag.
  assert.equal(page.window.location.href, "https://hyperwhisper.com/en/credits");
  assert.deepEqual(loadingValues(), [true, false]);
  assert.equal(page.listeners.length, 0);
});
