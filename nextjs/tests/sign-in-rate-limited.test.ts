/**
 * #2387: an Account Key sign-in that Better Auth's rate limiter turns away
 * says so, instead of "Sign-in failed. Please try again."
 *
 * The limiter (5 tries per 60 s per IP, `auth-license-key-plugin.ts`) answers
 * HTTP 429 with `{ message }` and an `X-Retry-After` header. The client read
 * only `data.error`, so the user was told to retry at once into a form that
 * could not succeed until the window passed.
 *
 * HOW. The failure here arrives AFTER an awaited `fetch`, so the render-phase
 * trick `tests/sign-in-error-announced.test.ts` uses (a synchronous throw
 * caught inside the render) cannot paint it. Instead this file renders twice:
 *
 * 1. The first render fires the Account Key form's own `onSubmit` (through a
 *    wrapped `react/jsx-runtime`), with `fetch` stubbed to resolve the 429.
 *    `useState` is wrapped too, so every setter the REAL handler calls after
 *    its awaits is recorded against the hook slot it belongs to.
 * 2. The second render replays those recorded values as the slots' initial
 *    state, which is what a client commit would show, and the markup is read.
 *
 * Slots are numbered by first appearance in render order, which React
 * guarantees is the same on every render of the same tree.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test, { beforeEach } from "node:test";
import { NextIntlClientProvider } from "next-intl";
import * as realReact from "react";
import * as realJsxRuntime from "react/jsx-runtime";
import { renderToStaticMarkup } from "react-dom/server";

import { moduleMock } from "./module-mock";

const enMessages = JSON.parse(
  readFileSync(new URL("../messages/en.json", import.meta.url), "utf8"),
) as { signIn: Record<string, string> } & Record<string, unknown>;

type SetState = (value: unknown) => void;

/** The first render fires the submit; the replay render does not. */
let firing = false;
let submitted = false;
let lastInputId: string | undefined;

/** Slot number of each setter, by first appearance. */
let slotOf = new Map<SetState, number>();
/** Calls to `useState` in the current render, for the replay render. */
let callIndex = 0;
/** The last value the handler gave each slot. */
let recorded = new Map<number, unknown>();
/** Values to start each slot with in the replay render. */
let replay: Map<number, unknown> | null = null;
/** Set once the handler has passed its first await. */
let afterAwait = false;

function useStateWrapped(initial: unknown): [unknown, SetState] {
  const index = callIndex++;
  const start = replay?.has(index) ? replay.get(index) : initial;
  const [value, setValue] = realReact.useState(start) as [unknown, SetState];

  if (!slotOf.has(setValue)) slotOf.set(setValue, slotOf.size);
  const slot = slotOf.get(setValue)!;

  return [
    value,
    (next: unknown) => {
      if (afterAwait) recorded.set(slot, next);
      setValue(next);
    },
  ];
}

moduleMock.module("react", {
  defaultExport: { ...realReact, useState: useStateWrapped },
  namedExports: { ...realReact, useState: useStateWrapped },
});

interface Props {
  id?: string;
  onSubmit?: () => void;
}

function fireSubmit(type: unknown, rawProps: unknown): void {
  if (!firing) return;
  const props = rawProps as Props;

  if (type === "input") lastInputId = props.id;
  if (!submitted && type === "form" && lastInputId === "license-key") {
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
      fireSubmit(type, props);

      return realJsx(type, props, key);
    },
    jsxs: (type: unknown, props: unknown, key?: unknown) => {
      fireSubmit(type, props);

      return realJsxs(type, props, key);
    },
  },
});

let pushed: unknown[] = [];

moduleMock.module("next/navigation", {
  namedExports: {
    useRouter: () => ({ push: (to: unknown) => pushed.push(to) }),
    useSearchParams: () => new URLSearchParams(),
    useParams: () => ({ locale: "en" }),
  },
});

// Keeps Better Auth's client (which reads `window` at import) out of the graph.
moduleMock.module("../src/lib/auth-client", {
  namedExports: { authClient: { signIn: { magicLink: () => ({}) } } },
});

const COMPONENT_PATH = "../app/[locale]/user/sign-in/SignInClient.tsx";

const GENERIC = "Sign-in failed. Please try again.";

const realFetch = globalThis.fetch;

let fetchCalls: string[] = [];

function stubFetch(status: number, body: unknown, headers: Record<string, string>) {
  globalThis.fetch = (async (input: RequestInfo | URL) => {
    fetchCalls.push(String(input));
    afterAwait = true;

    return new Response(JSON.stringify(body), {
      status,
      headers: { "Content-Type": "application/json", ...headers },
    });
  }) as typeof fetch;
}

function renderOnce(SignInClient: () => realReact.ReactElement): string {
  callIndex = 0;

  return renderToStaticMarkup(
    realReact.createElement(NextIntlClientProvider, {
      locale: "en",
      messages: enMessages,
      timeZone: "UTC",
      children: realReact.createElement(SignInClient),
    }),
  );
}

/** Submits the Account Key form once, lets the handler finish, and renders the result. */
async function submitAndRender(): Promise<string> {
  const { default: SignInClient } = (await import(COMPONENT_PATH)) as {
    default: () => realReact.ReactElement;
  };

  firing = true;
  renderOnce(SignInClient);
  firing = false;
  assert.ok(submitted, "the Account Key form was never submitted");

  // The handler awaits `fetch` and then `res.json()`: let it run to the end.
  for (let i = 0; i < 20; i++) await new Promise((r) => setImmediate(r));

  replay = recorded;
  return renderOnce(SignInClient);
}

/** The text of the Account Key alert. */
function alertText(html: string): string {
  const match =
    /<div [^>]*role="alert"[^>]*><p [^>]*id="license-key-error"[^>]*>([^<]*)<\/p>/.exec(
      html,
    );

  assert.ok(match, "no Account Key alert in the markup");

  return match[1].replace(/&#x27;/g, "'");
}

beforeEach(() => {
  submitted = false;
  lastInputId = undefined;
  slotOf = new Map();
  recorded = new Map();
  replay = null;
  afterAwait = false;
  pushed = [];
  fetchCalls = [];
});

test.after(() => {
  globalThis.fetch = realFetch;
});

const RATE_LIMITED = { message: "Too many requests. Please try again later." };

test("a 429 with X-Retry-After shows the too-many-attempts copy with the wait", async () => {
  stubFetch(429, RATE_LIMITED, { "X-Retry-After": "42" });
  const html = await submitAndRender();

  assert.deepEqual(fetchCalls, ["/api/auth/sign-in/license-key"]);
  const text = alertText(html);
  assert.equal(
    text,
    "Too many sign-in attempts. Please wait 42 seconds and try again.",
  );
  assert.notEqual(text, GENERIC);
  assert.match(
    /<input [^>]*id="license-key"[^>]*>/.exec(html)?.[0] ?? "",
    /aria-invalid="true"/,
  );
  assert.deepEqual(pushed, []);
});

test("a 429 with a one-second wait reads in the singular", async () => {
  stubFetch(429, RATE_LIMITED, { "X-Retry-After": "1" });

  assert.equal(
    alertText(await submitAndRender()),
    "Too many sign-in attempts. Please wait 1 second and try again.",
  );
});

test("a 429 without a usable X-Retry-After still says why, without a count", async () => {
  stubFetch(429, RATE_LIMITED, {});

  assert.equal(alertText(await submitAndRender()), enMessages.signIn.tooManyAttemptsLater);
});

test("the server's raw message is not shown for other statuses (control)", async () => {
  // A bad key still reaches the client as `{ error }`; anything else without
  // `error` keeps the generic copy, never the server's `message`.
  stubFetch(500, { message: "Internal details" }, {});

  assert.equal(alertText(await submitAndRender()), GENERIC);
});

test("a rejected key still shows the server's own error (control)", async () => {
  stubFetch(200, { error: "Invalid or inactive license key." }, {});

  assert.equal(alertText(await submitAndRender()), "Invalid or inactive license key.");
});
