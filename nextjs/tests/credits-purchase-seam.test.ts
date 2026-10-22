/**
 * #949: the public `/credits` form (`components/credits/CreditsPurchase.tsx`)
 * no longer re-implements the checkout POST. It calls the shared seam in
 * `src/lib/buy-credits.ts` with an EMAIL identity, exactly as the dashboard's
 * `CloudCreditsCard` calls it with a licence key, so the three defects the
 * inline copy carried are gone with it:
 *
 *   1. a non-JSON body (a proxy's HTML 502) was reported as a network failure;
 *   2. an empty-string server `error` painted an empty alert region;
 *   3. a scheduled redirect left `loading` set with nothing owning it.
 *
 * HOW, with no DOM and no click. The HeroUI `Input` and `Button` are stubbed so
 * their props can be read. The buyer's address is typed through the email
 * field's own `onChange`, fired from a wrapped `react/jsx-runtime` while the
 * form renders (the render-phase update rule
 * `tests/cloud-credits-card-error-state.test.ts` documents). The CTA's own
 * `onPress` — the component's `handleCheckout`, with its validation and its
 * double-submit guard — is then called by hand, and it drives the REAL seam
 * through a recording wrapper around `createBuyCreditsHandler`, with the
 * global `fetch` stubbed.
 *
 * WHAT IT DOES NOT PROVE. The recorded `setBusy` / `setError` calls are what
 * the seam handed the component's setters after the click; React's server
 * renderer drops an update dispatched outside a render, so the repaint after
 * an awaited `fetch` is not observable here (the same named gap as the card).
 * That the component's `setError` and `setBusy` do reach the alert region and
 * the CTA's loading state is proven separately, with render-phase dispatches.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test, { afterEach, beforeEach } from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { createElement, type ReactElement, type ReactNode } from "react";
import * as realJsxRuntime from "react/jsx-runtime";
import { renderToStaticMarkup } from "react-dom/server";

import * as realSeam from "../src/lib/buy-credits";
import type {
  BuyCreditsHandlerRequest,
  BuyCreditsTier,
} from "../src/lib/buy-credits";

import { moduleMock } from "./module-mock";

/** The address the buyer types, with the stray spaces a real paste carries. */
const TYPED_EMAIL = "  buyer-949@example.com ";
const EMAIL = "buyer-949@example.com";

interface InputProps {
  type?: string;
  onChange?: (event: { target: { value: string } }) => void;
}

interface ButtonProps {
  isLoading?: boolean;
  isDisabled?: boolean;
  onPress?: () => unknown;
  children?: ReactNode;
}

/** Every CTA `<Button>` (the one with `isLoading`) the stub rendered. */
const ctaButtons: ButtonProps[] = [];

function StubInput(): null {
  return null;
}

function StubButton(props: ButtonProps): ReactElement {
  if (props.isLoading !== undefined) ctaButtons.push(props);

  return createElement(
    "button",
    { "data-loading": String(Boolean(props.isLoading)) },
    props.children,
  );
}

// By the file the COMPONENT loads, not the bare name — see
// `tests/credits-purchase-email-wiring.test.ts` for why.
moduleMock.module(pathToFileURL(require.resolve("@heroui/input")).href, {
  namedExports: { Input: StubInput },
});
moduleMock.module(pathToFileURL(require.resolve("@heroui/button")).href, {
  namedExports: { Button: StubButton },
});

/** What the buyer types into the email field during the next render. */
let typed: string | undefined;
let typedFired = false;

function typeEmail(type: unknown, props: unknown): void {
  if (typed === undefined || typedFired || type !== StubInput) return;
  const input = props as InputProps;

  if (input.type !== "email") return;
  typedFired = true;
  input.onChange?.({ target: { value: typed } });
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

/** `t` echoes `namespace.key`, so every translated string is recognisable. */
moduleMock.module("next-intl", {
  namedExports: {
    useTranslations:
      (namespace: string) =>
      (key: string): string =>
        `${namespace}.${key}`,
  },
});

/** Every `reportClientError` call the form made. */
const clientReports: Array<{
  operation: string;
  error: unknown;
  context: Record<string, string | number> | undefined;
}> = [];

moduleMock.module("../lib/shared/report-client-error", {
  namedExports: {
    reportClientError: (
      operation: string,
      error: unknown,
      context?: Record<string, string | number>,
    ) => {
      clientReports.push({ operation, error, context });

      return Promise.resolve();
    },
  },
});

/** Every abandoned-redirect watch the form armed. */
const watches: Array<{ host: unknown; release: () => void }> = [];

moduleMock.module("../src/lib/abandoned-redirect", {
  namedExports: {
    watchForAbandonedRedirect: (host: unknown, release: () => void) => {
      watches.push({ host, release });
    },
  },
});

/** Every `createBuyCreditsHandler` request the form built, in order. */
const factoryCalls: BuyCreditsHandlerRequest[] = [];
/** What the seam handed the form's `setBusy` / `setError`, in order. */
const busy: Array<BuyCreditsTier | null> = [];
const errors: Array<string | null> = [];
/** A one-shot render-phase dispatch into the form's own setters. */
let armed: ((request: BuyCreditsHandlerRequest) => void) | null = null;

/**
 * The REAL factory, with the form's own `setBusy` / `setError` wrapped so the
 * seam's calls into them are recorded and still delivered.
 */
moduleMock.module("../src/lib/buy-credits", {
  namedExports: {
    ...realSeam,
    createBuyCreditsHandler: (request: BuyCreditsHandlerRequest) => {
      factoryCalls.push(request);

      if (armed) {
        const dispatch = armed;

        armed = null;
        dispatch(request);
      }

      return realSeam.createBuyCreditsHandler({
        ...request,
        setBusy: (tier) => {
          busy.push(tier);
          request.setBusy(tier);
        },
        setError: (message) => {
          errors.push(message);
          request.setError(message);
        },
      } as BuyCreditsHandlerRequest);
    },
  },
});

/** Every request the form sent through the global `fetch`. */
const requests: Array<{ input: unknown; init: RequestInit | undefined }> = [];

interface FakeResponse {
  ok: boolean;
  status: number;
  json: () => Promise<unknown>;
}

const realFetch = globalThis.fetch;
const hadWindow = "window" in globalThis;
const fakeWindow = { location: { href: "about:blank" } };

function stubFetch(response: () => Promise<FakeResponse>): void {
  globalThis.fetch = ((input: unknown, init?: RequestInit) => {
    requests.push({ input, init });

    return response();
  }) as unknown as typeof fetch;
}

/** A JSON body exactly as `Response.json()` would resolve it. */
function answers(ok: boolean, status: number, body: unknown) {
  return () => Promise.resolve({ ok, status, json: async () => body });
}

const COMPONENT_PATH = "../components/credits/CreditsPurchase.tsx";

type CreditsPurchase = (props: { locale: string }) => ReactElement;

/** Renders the real form once and answers the markup. */
async function renderForm(email: string | undefined = TYPED_EMAIL) {
  typed = email;
  const { default: Component } = (await import(COMPONENT_PATH)) as {
    default: CreditsPurchase;
  };

  return renderToStaticMarkup(createElement(Component, { locale: "en" }));
}

/** The settled CTA's `onPress` — the component's own `handleCheckout`. */
function lastCta(): ButtonProps {
  const cta = ctaButtons.at(-1);

  assert.ok(cta, "the form rendered no CTA button");

  return cta;
}

/** Clicks the CTA, with the seam's console lines recorded, not printed. */
async function press(): Promise<unknown[][]> {
  const logged: unknown[][] = [];
  const quiet = console.error;

  console.error = (...args: unknown[]) => {
    logged.push(args);
  };
  try {
    await lastCta().onPress?.();
  } finally {
    console.error = quiet;
  }

  return logged;
}

beforeEach(() => {
  typed = undefined;
  typedFired = false;
  armed = null;
  ctaButtons.length = 0;
  factoryCalls.length = 0;
  busy.length = 0;
  errors.length = 0;
  requests.length = 0;
  clientReports.length = 0;
  watches.length = 0;
  fakeWindow.location.href = "about:blank";
  (globalThis as { window?: unknown }).window = fakeWindow;
});

afterEach(() => {
  globalThis.fetch = realFetch;
  if (!hadWindow) delete (globalThis as { window?: unknown }).window;
});

test("Done-when 1: the form has no checkout fetch of its own and calls the seam", () => {
  const source = readFileSync(
    fileURLToPath(new URL(COMPONENT_PATH, import.meta.url)),
    "utf8",
  );

  // Positive control: the right file was read.
  assert.match(source, /export default function CreditsPurchase/);
  assert.equal(source.includes('fetch("/api/checkout/credits"'), false);
  assert.equal(source.includes("fetch('/api/checkout/credits'"), false);
  assert.equal(source.includes(".json()"), false);
  assert.match(source, /createBuyCreditsHandler\(\{/);
  assert.match(source, /watchForAbandonedRedirect\(window, release\)/);
});

test("the form hands the seam the trimmed email identity and translated copy", async () => {
  await renderForm();

  const request = factoryCalls.at(-1);

  assert.ok(request, "the form never called the factory");
  assert.equal(request.email, EMAIL);
  assert.equal("licenseKey" in request, false);
  assert.equal(request.checkoutErrorMessage, "buyCredits.errorCheckout");
  assert.equal(request.genericErrorMessage, "buyCredits.errorGeneric");
  assert.equal(typeof request.setBusy, "function");
  assert.equal(typeof request.setError, "function");
  assert.equal(typeof request.onRedirectScheduled, "function");
});

test("a click posts exactly { email, amount } to the checkout route", async () => {
  stubFetch(answers(false, 400, { error: "Amount too large" }));
  await renderForm();

  await press();

  assert.equal(requests.length, 1);
  assert.equal(requests[0]?.input, "/api/checkout/credits");
  assert.equal(requests[0]?.init?.method, "POST");
  assert.equal(
    requests[0]?.init?.body,
    JSON.stringify({ email: EMAIL, amount: 5 }),
  );
  // A 4xx sentence from the route is still shown verbatim.
  assert.deepEqual(errors, [null, "Amount too large"]);
  assert.deepEqual(busy, [5, null]);
});

test("Done-when 2: a non-JSON body shows the checkout copy, not the network copy", async () => {
  stubFetch(() =>
    Promise.resolve({
      ok: false,
      status: 502,
      json: () => Promise.reject(new SyntaxError("Unexpected token '<'")),
    }),
  );
  await renderForm();

  await press();

  assert.deepEqual(errors, [null, "buyCredits.errorCheckout"]);
  assert.equal(errors.includes("buyCredits.errorGeneric"), false);
  // The button comes back: the buyer is still on the page.
  assert.deepEqual(busy, [5, null]);
  // …and the status is kept for the developer.
  assert.equal(clientReports.length, 1);
  assert.equal(clientReports[0]?.context?.status, 502);
});

test("Done-when 3: an empty-string server error shows translated copy, never an empty alert", async () => {
  stubFetch(answers(false, 400, { error: "" }));
  await renderForm();

  await press();

  assert.deepEqual(errors, [null, "buyCredits.errorCheckout"]);
  assert.equal(errors.includes(""), false);
});

test("a 5xx shows the translated copy, not the server's body", async () => {
  stubFetch(
    answers(false, 500, {
      error: "Failed to create checkout session",
      details: "Stripe API error: connection refused",
    }),
  );
  await renderForm();

  await press();

  assert.deepEqual(errors, [null, "buyCredits.errorCheckout"]);
});

test("a thrown request still shows the network copy", async () => {
  stubFetch(() => Promise.reject(new TypeError("Failed to fetch")));
  await renderForm();

  await press();

  assert.deepEqual(errors, [null, "buyCredits.errorGeneric"]);
  assert.deepEqual(busy, [5, null]);
});

test("the email never reaches a report or a console line", async () => {
  const exits = [
    answers(false, 400, { error: "Nope" }),
    answers(false, 500, { error: "Failed to create checkout session" }),
    answers(true, 200, { ok: true }),
    () => Promise.reject(new TypeError("Failed to fetch")),
  ];

  for (const exit of exits) {
    clientReports.length = 0;
    ctaButtons.length = 0;
    typedFired = false;
    stubFetch(exit);
    await renderForm();

    const logged = await press();

    // Positive control: every exit above does report.
    assert.equal(clientReports.length, 1);
    assert.equal(clientReports[0]?.operation, "credits.checkout");

    const surface = JSON.stringify({
      reports: clientReports.map((r) => ({
        operation: r.operation,
        message: r.error instanceof Error ? r.error.message : String(r.error),
        context: r.context,
      })),
      logs: logged.map((args) =>
        args.map((a) => (a instanceof Error ? a.message : String(a))),
      ),
    });

    assert.equal(surface.includes(EMAIL), false);
    assert.equal(surface.includes("example.com"), false);
  }
});

test("a successful checkout navigates and hands the loading release to the watcher", async () => {
  stubFetch(
    answers(true, 200, { checkoutUrl: "https://checkout.stripe.com/c/pay/ok" }),
  );
  await renderForm();

  await press();

  assert.equal(fakeWindow.location.href, "https://checkout.stripe.com/c/pay/ok");
  // `loading` is NOT cleared on the way out: the document is still live
  // while the redirect loads, and a second click would be a second session.
  assert.deepEqual(busy, [5]);
  assert.deepEqual(errors, [null]);
  // …but it is owned: the form armed the abandoned-redirect watcher on the
  // real `window`, and what that watcher fires releases `loading`.
  assert.equal(watches.length, 1);
  assert.equal(watches[0]?.host, fakeWindow);
  watches[0]?.release();
  assert.deepEqual(busy, [5, null]);
});

test("the seam's setBusy drives the CTA's loading state, and the guard holds", async () => {
  stubFetch(answers(true, 200, { checkoutUrl: "https://x.example/ok" }));
  armed = (request) => request.setBusy(5);

  const markup = await renderForm();

  assert.equal(armed, null, "the factory never dispatched");
  assert.equal(lastCta().isLoading, true);
  assert.equal(lastCta().isDisabled, true);
  assert.match(markup, /data-loading="true"[^>]*>buyCredits\.ctaLoading</);

  // The double-submit guard: a press while loading sends nothing.
  await press();
  assert.equal(requests.length, 0);
});

test("setBusy(null) brings the CTA back", async () => {
  armed = (request) => {
    request.setBusy(5);
    request.setBusy(null);
  };

  const markup = await renderForm();

  assert.equal(lastCta().isLoading, false);
  assert.match(markup, /data-loading="false"[^>]*>buyCredits\.ctaIdle</);
});

test("the seam's setError paints the form's alert region", async () => {
  armed = (request) => request.setError("Amount too large");

  const markup = await renderForm();

  assert.match(markup, /<p[^>]*role="alert"[^>]*>Amount too large<\/p>/);
});

test("an invalid email never reaches the seam's request", async () => {
  stubFetch(answers(true, 200, { checkoutUrl: "https://x.example/ok" }));
  await renderForm("me@gmail");

  await press();

  // The form's own validation runs first, as before #949.
  assert.equal(requests.length, 0);
  assert.deepEqual(busy, []);
});
