/**
 * The `UserHeader` wiring seam: `components/user/UserHeader.tsx` (#870, #881).
 *
 * `tests/sign-out-seam.test.ts` covers the DECISION — that a refused sign-out
 * does not navigate. It does NOT cover the thing #870 actually regresses on:
 * that the component still routes its button through that decision. Review
 * round 1 on #881 proved the gap by mutation — restoring the pre-fix
 * `UserHeader.tsx` from `origin/main`, which re-adds the unconditional
 * `window.location.href = …` and deletes the error region, left the suite byte
 * for byte identical. This file closes that half.
 *
 * There is no DOM here and no click: `renderToStaticMarkup` is the only way a
 * component is reachable in this repo, and it renders once with no events. So
 * the handler is built by `createSignOutHandler` in the component's RENDER
 * BODY, and a static render is enough to prove the component asked for it and
 * with what. A regression to the old inline `await authClient.signOut();
 * window.location.href = …` stops calling the factory and fails the first test
 * below.
 *
 * Round 2 split the markup out into `UserHeaderView`, because a static render
 * can see neither an event handler nor a state this component's FIRST render
 * never reaches. Those assertions are `tests/user-header-view.test.ts` now.
 * What is left here is the wiring between the two halves, and this file mocks
 * the view so it can read the props the wrapper hands down — including the
 * `onSignOut` callback, which it INVOKES to prove the button is joined to the
 * handler the factory built. That chain is the whole of #870.
 */
import assert from "node:assert/strict";
import test, { beforeEach, mock } from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";

import type { SignOutHandlerRequest } from "../src/lib/sign-out";

/**
 * `mock.module` is a real Node 22 API (behind `--experimental-test-module-mocks`,
 * which `npm test` passes) but this repo pins `@types/node@20`, which has no
 * declaration for it. Narrowed to the two options used here rather than bumping
 * the types in a test-only change — the same shape
 * `tests/root-layout-direction.test.ts` uses.
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

/** Every `createSignOutHandler` call the component made, in order. */
const factoryCalls: SignOutHandlerRequest[] = [];

/** Every run of a handler the factory answered, in order. */
const handlerRuns: SignOutHandlerRequest[] = [];

/**
 * The seam under test. The stub records the request and answers a handler that
 * records its own runs, which is all the component does with it. The handler's
 * own behaviour — when it navigates, when it clears the busy flag — is
 * `sign-out-seam.test.ts`.
 */
moduleMock.module("../src/lib/sign-out", {
  namedExports: {
    createSignOutHandler: (request: SignOutHandlerRequest) => {
      factoryCalls.push(request);

      return async () => {
        handlerRuns.push(request);
      };
    },
  },
});

/**
 * Mocked to keep Better Auth's client out of the graph. It reaches for
 * `window` and for `BETTER_AUTH_URL` at import time; neither exists here, and
 * neither is part of what this file proves.
 */
moduleMock.module("../src/lib/auth-client", {
  namedExports: { authClient: { signOut: async () => ({ error: null }) } },
});

/** Every prop set the wrapper handed the view, in order. */
interface ViewProps {
  user: { email?: string | null };
  isAdmin: boolean;
  signingOut: boolean;
  signOutError: string | null;
  onSignOut: () => void;
}

const viewRenders: ViewProps[] = [];

/**
 * The presentational half, stubbed. It renders nothing: this file is about the
 * props crossing the seam, and the markup they produce is
 * `tests/user-header-view.test.ts`. Capturing them is the only way to reach
 * `onSignOut` at all — React never serialises a handler into static markup,
 * which is exactly how the round 1 version of this file passed with the
 * button's `onClick` deleted.
 */
moduleMock.module("../components/user/UserHeaderView.tsx", {
  defaultExport: (props: ViewProps) => {
    viewRenders.push(props);

    return null;
  },
});

// A VARIABLE specifier, and deferred: a static import would bind before the
// `mock.module` calls above, and a literal `.tsx` specifier is a TS5097 error
// under this tsconfig. Same rule the other mock.module tests in this folder
// document.
const HEADER_PATH = "../components/user/UserHeader.tsx";

type UserHeader = (props: {
  user: { email?: string | null };
  locale: string;
  isAdmin: boolean;
}) => React.ReactElement;

/** Renders the real `UserHeader` exactly as a signed-in page would. */
async function renderUserHeader(locale: string): Promise<void> {
  const { default: UserHeader } = (await import(HEADER_PATH)) as {
    default: UserHeader;
  };

  renderToStaticMarkup(
    createElement(UserHeader, {
      user: { email: "someone@example.com" },
      locale,
      isAdmin: false,
    }),
  );
}

beforeEach(() => {
  factoryCalls.length = 0;
  handlerRuns.length = 0;
  viewRenders.length = 0;
});

test("the header routes its sign-out button through the shared handler", async () => {
  await renderUserHeader("fr");

  // The whole of #870: the component must not own this decision. If it goes
  // back to an inline `await authClient.signOut(); window.location.href = …`
  // the factory is never asked for and this is 0.
  assert.equal(factoryCalls.length, 1);
});

test("the button is joined to the handler the factory built", async () => {
  await renderUserHeader("fr");

  const [view] = viewRenders;

  assert.ok(view, "the header rendered no view");
  assert.equal(typeof view.onSignOut, "function");

  // Nothing may fire from rendering alone.
  assert.deepEqual(handlerRuns, []);

  view.onSignOut();
  await Promise.resolve();

  // The end of the chain: the factory's handler, and no other, is what the
  // button runs. A component that built the handler and then wired its button
  // to something else would satisfy every other test in this file.
  assert.equal(handlerRuns.length, 1);
  assert.equal(handlerRuns[0], factoryCalls[0]);
});

test("the handler is built for the caller's own locale", async () => {
  for (const locale of ["fr", "ar", "zh-Hant"]) {
    factoryCalls.length = 0;

    await renderUserHeader(locale);

    assert.equal(factoryCalls.length, 1);
    assert.equal(factoryCalls[0].redirectTo, `/${locale}/user/sign-in`);
  }
});

test("the handler is given a busy setter and an error setter", async () => {
  await renderUserHeader("en");

  // Both are what makes the asymmetric busy flag reachable at all. A component
  // that stopped passing one would leave the factory unable to disarm the
  // button or to surface a refusal, and nothing else here would notice.
  const request = factoryCalls[0];

  assert.equal(typeof request.setBusy, "function");
  assert.equal(typeof request.setError, "function");
  assert.equal(typeof request.signOut, "function");
  assert.equal(typeof request.navigate, "function");
});

test("the view is handed the idle state and the user on a first paint", async () => {
  await renderUserHeader("en");

  const [view] = viewRenders;

  assert.ok(view, "the header rendered no view");
  // The two flags the wrapper owns. They start here, and the factory's
  // `setBusy` and `setError` are the only things that move them — which is why
  // a static render of the wrapper can never reach any other state, and why
  // the view is tested on its own.
  assert.equal(view.signingOut, false);
  assert.equal(view.signOutError, null);
  assert.equal(view.isAdmin, false);
  assert.equal(view.user.email, "someone@example.com");
});
