/**
 * #760: a failed sign-in on `/[locale]/user/sign-in` is announced to a screen
 * reader, and the field at fault says so.
 *
 * `app/[locale]/user/sign-in/SignInClient.tsx` used to paint "Invalid or
 * inactive license key." into a plain `<div>`: no `role="alert"`, no
 * `aria-invalid` on `#license-key`, nothing joining the two. A screen reader
 * user pressed Sign In and heard nothing. This file renders the REAL component
 * in its failed state and reads those attributes off the served markup.
 *
 * HOW, with no DOM and no click. The render-phase update rule that
 * `tests/cloud-credits-card-error-state.test.ts` documents: a `useState` setter
 * called synchronously DURING the component's own render is applied inside one
 * `renderToStaticMarkup`. `react/jsx-runtime` is wrapped (delegating to the
 * real one) so that, when the component builds a `<form>`, that form's own
 * `onSubmit` is called. The submit handlers are `async`, but they run
 * synchronously up to their first `await`; `fetch` (Account Key) and
 * `authClient.signIn.magicLink` (Email) are stubbed to THROW synchronously, so
 * the handler's own `catch` calls the real `setLicenseError` / `setEmailError`
 * inside that same render. The tab switch to Email is the Email tab button's
 * own `onClick`, fired the same way.
 *
 * WHAT IT DOES NOT PROVE. Production sets the error after an awaited network
 * answer, from a real submit event, and the repaint is a client commit. That
 * path is not driven here. Nor is the error inside the `magicLinkSent` branch
 * (it is only reachable after an awaited send succeeds); that branch renders
 * the same `emailErrorBanner` variable, and has no `#email` input to mark.
 * `older-versions/page.tsx` is not covered either: its error comes from a
 * fetch in `useEffect`, which a server render never runs.
 */
import assert from "node:assert/strict";
import test, { beforeEach, mock } from "node:test";
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

/** Which failure the next render drives. `null` renders the untouched form. */
let scenario: "license-key" | "email" | null = null;
/** Steps already fired in this render, so the settle re-render is inert. */
let switchedTab = false;
let submitted = false;
/** The id of the last `<input>` built: a form's children are built before it. */
let lastInputId: string | undefined;

interface Props {
  id?: string;
  type?: string;
  children?: unknown;
  onClick?: () => void;
  onSubmit?: () => void;
}

function fireScenario(type: unknown, rawProps: unknown): void {
  if (scenario === null) return;
  const props = rawProps as Props;

  if (type === "input") lastInputId = props.id;

  if (
    scenario === "email" &&
    !switchedTab &&
    type === "button" &&
    props.children === "Email"
  ) {
    switchedTab = true;
    props.onClick?.();

    return;
  }

  // Only the form that wraps the scenario's own field: on the first render the
  // Account Key form is built even when the Email tab is about to be selected.
  if (!submitted && type === "form" && lastInputId === scenario) {
    submitted = true;
    props.onSubmit?.();
  }
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

moduleMock.module("next/navigation", {
  namedExports: {
    useRouter: () => ({ push: () => {} }),
    useSearchParams: () => new URLSearchParams(),
    useParams: () => ({ locale: "en" }),
  },
});

/**
 * Also keeps Better Auth's client out of the graph: it reaches for `window`
 * and `BETTER_AUTH_URL` at import time. `magicLink` throws synchronously so the
 * send handler's `catch` runs inside the render.
 */
moduleMock.module("../src/lib/auth-client", {
  namedExports: {
    authClient: {
      signIn: {
        magicLink: () => {
          throw new Error("offline");
        },
      },
    },
  },
});

// A VARIABLE specifier, and deferred: a static import would bind before the
// `mock.module` calls above, and a literal `.tsx` specifier is a TS5097 error.
const COMPONENT_PATH = "../app/[locale]/user/sign-in/SignInClient.tsx";

const UNEXPECTED = "An unexpected error occurred. Please try again.";

async function render(next: typeof scenario): Promise<string> {
  scenario = next;
  const { default: SignInClient } = (await import(COMPONENT_PATH)) as {
    default: () => ReactElement;
  };

  return renderToStaticMarkup(createElement(SignInClient));
}

/** The opening tag of the element with this id. */
function tagWithId(html: string, id: string): string {
  const match = new RegExp(`<[a-z]+ [^>]*id="${id}"[^>]*>`).exec(html);

  assert.ok(match, `no element with id="${id}" in the markup`);

  return match[0];
}

const realFetch = globalThis.fetch;

beforeEach(() => {
  scenario = null;
  switchedTab = false;
  submitted = false;
  lastInputId = undefined;
  globalThis.fetch = (() => {
    throw new Error("offline");
  }) as typeof fetch;
});

test.after(() => {
  globalThis.fetch = realFetch;
});

test("an untouched form marks nothing invalid and announces nothing", async () => {
  const html = await render(null);
  const input = tagWithId(html, "license-key");

  assert.doesNotMatch(input, /aria-invalid/);
  assert.doesNotMatch(input, /aria-describedby/);
  assert.doesNotMatch(html, /role="alert"/);
  assert.doesNotMatch(html, /id="license-key-error"/);
});

test("a failed Account Key sign-in is announced as an alert", async () => {
  const html = await render("license-key");

  assert.ok(html.includes(UNEXPECTED), "the failure never reached the markup");
  assert.match(
    html,
    /<div [^>]*role="alert"[^>]*><p [^>]*id="license-key-error"[^>]*>An unexpected error/,
  );
});

test("a failed Account Key sign-in marks #license-key invalid and described", async () => {
  const html = await render("license-key");
  const input = tagWithId(html, "license-key");

  assert.match(input, /aria-invalid="true"/);
  assert.match(input, /aria-describedby="license-key-error"/);
});

test("a failed magic-link send is announced as an alert", async () => {
  const html = await render("email");

  assert.ok(html.includes(UNEXPECTED), "the failure never reached the markup");
  assert.match(
    html,
    /<div [^>]*role="alert"[^>]*><p [^>]*id="email-error"[^>]*>An unexpected error/,
  );
});

test("a failed magic-link send marks #email invalid and described", async () => {
  const html = await render("email");
  const input = tagWithId(html, "email");

  assert.match(input, /aria-invalid="true"/);
  assert.match(input, /aria-describedby="email-error"/);
});
