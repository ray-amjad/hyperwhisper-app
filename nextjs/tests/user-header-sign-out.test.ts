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
 * What is asserted is the MARKUP, not the props of the returned element — the
 * same rule `tests/root-layout-direction.test.ts` documents. A `role="alert"`
 * region React refused to serialise would still satisfy a prop assertion, and
 * what the user gets is the bytes.
 */
import assert from "node:assert/strict";
import test, { beforeEach, mock } from "node:test";
import { createElement, type ReactNode } from "react";
import { renderToStaticMarkup } from "react-dom/server";

import type { SignOutHandlerRequest } from "../src/lib/sign-out";

/**
 * `mock.module` is a real Node 22 API (behind `--experimental-test-module-mocks`,
 * which `npm test` passes) but this repo pins `@types/node@20`, which has no
 * declaration for it. Narrowed to the one method used here rather than bumping
 * the types in a test-only change — the same shape
 * `tests/root-layout-direction.test.ts` uses.
 */
interface ModuleMocker {
  module(
    specifier: string,
    options: { namedExports: Record<string, unknown> },
  ): void;
}

const moduleMock = mock as unknown as ModuleMocker;

/** Every `createSignOutHandler` call the component made, in order. */
const factoryCalls: SignOutHandlerRequest[] = [];

/**
 * The seam under test. The stub records the request and answers a handler,
 * which is all the component does with it. The handler's own behaviour — when
 * it navigates, when it clears the busy flag — is `sign-out-seam.test.ts`.
 */
moduleMock.module("../src/lib/sign-out", {
  namedExports: {
    createSignOutHandler: (request: SignOutHandlerRequest) => {
      factoryCalls.push(request);

      return async () => {};
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

/**
 * `next-intl`'s `createNavigation` builds a `Link` that reads the request
 * locale from React context, which a bare `renderToStaticMarkup` does not
 * provide. The stub is a plain anchor: the logo link is not what this file is
 * about, but it must render for the header to render at all.
 */
moduleMock.module("../src/i18n/navigation", {
  namedExports: {
    Link: ({ children, ...props }: { children?: ReactNode; href: string }) =>
      createElement("a", props, children),
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
async function renderUserHeader(locale: string): Promise<string> {
  const { default: UserHeader } = (await import(HEADER_PATH)) as {
    default: UserHeader;
  };

  return renderToStaticMarkup(
    createElement(UserHeader, {
      user: { email: "someone@example.com" },
      locale,
      isAdmin: false,
    }),
  );
}

beforeEach(() => {
  factoryCalls.length = 0;
});

test("the header routes its sign-out button through the shared handler", async () => {
  await renderUserHeader("fr");

  // The whole of #870: the component must not own this decision. If it goes
  // back to an inline `await authClient.signOut(); window.location.href = …`
  // the factory is never asked for and this is 0.
  assert.equal(factoryCalls.length, 1);
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

test("the header renders an enabled Sign Out button", async () => {
  const markup = await renderUserHeader("en");

  assert.match(markup, /<button[^>]*>Sign Out<\/button>/);
  assert.doesNotMatch(markup, /Signing Out/);
  // React serialises a true `disabled` as `disabled=""` and omits a false one.
  // Anchored on the `=` because the button's Tailwind classes contain the bare
  // word `disabled:` twice.
  assert.doesNotMatch(markup, /disabled="/);
});

test("the header announces nothing before a sign-out has failed", async () => {
  const markup = await renderUserHeader("en");

  // A live region that is present and empty on every page load is an assistive
  // technology annoyance and would also mean the error span renders with no
  // error. It appears only once `setError` has been called.
  assert.doesNotMatch(markup, /role="alert"/);
});
