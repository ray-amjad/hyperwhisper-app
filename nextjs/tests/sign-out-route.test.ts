/**
 * `POST /[locale]/user/auth/sign-out`, driven as HTTP.
 *
 * The route revokes the Better Auth session, then answers with a redirect to
 * the locale's sign-in page that carries the session-clearing cookie. Before
 * #871 it built that redirect and never returned it, so the caller got a
 * server error AFTER the session was already revoked.
 *
 * Better Auth is replaced at the module boundary, before the route is loaded,
 * so the route binds to the fake. Do not import the route path statically.
 */
import assert from "node:assert/strict";
import { beforeEach, mock, test } from "node:test";

import { NextRequest } from "next/server";

/** See credit-routes-harness.ts: @types/node@20 has no `mock.module`. */
interface ModuleMocker {
  module(
    specifier: string,
    options: { namedExports: Record<string, unknown> },
  ): void;
}

const signOutCalls: { headers: Headers; asResponse: boolean }[] = [];
let betterAuthSetCookies: string[] = [];

(mock as unknown as ModuleMocker).module(
  new URL("../src/lib/auth.ts", import.meta.url).href,
  {
    namedExports: {
      auth: {
        api: {
          signOut: async (options: {
            headers: Headers;
            asResponse: boolean;
          }) => {
            signOutCalls.push(options);
            const headers = new Headers();
            // One Set-Cookie header per cookie, as Better Auth's
            // deleteSessionCookie sends them.
            for (const cookie of betterAuthSetCookies) {
              headers.append("set-cookie", cookie);
            }
            return new Response(null, { status: 200, headers });
          },
        },
      },
    },
  },
);

const loadRoute = () => import("@/app/[locale]/user/auth/sign-out/route");

function signOutRequest(locale: string) {
  const request = new NextRequest(
    `https://hyperwhisper.test/${locale}/user/auth/sign-out`,
    { method: "POST", headers: { cookie: "better-auth.session_token=abc" } },
  );
  return { request, context: { params: Promise.resolve({ locale }) } };
}

beforeEach(() => {
  signOutCalls.length = 0;
  betterAuthSetCookies = [];
});

test("redirects to the locale sign-in page with every Better Auth cookie", async () => {
  betterAuthSetCookies = [
    "better-auth.session_token=; Max-Age=0; Path=/; HttpOnly; Secure",
    "better-auth.session_data=; Max-Age=0; Path=/; HttpOnly; Secure",
  ];
  const { POST } = await loadRoute();
  const { request, context } = signOutRequest("fr");

  const response = await POST(request, context);

  assert.ok(response instanceof Response, "the handler must return a Response");
  assert.equal(response.status, 307);
  assert.equal(
    response.headers.get("location"),
    "https://hyperwhisper.test/fr/user/sign-in",
  );
  // Each cookie must reach the browser as its own Set-Cookie header; a
  // comma-joined single header clears only the first one.
  assert.deepEqual(response.headers.getSetCookie(), betterAuthSetCookies);
  assert.equal(signOutCalls.length, 1);
  assert.equal(signOutCalls[0].asResponse, true);
  assert.equal(
    signOutCalls[0].headers.get("cookie"),
    "better-auth.session_token=abc",
  );
});

test("falls back to clearing the session cookie itself", async () => {
  const { POST } = await loadRoute();
  const { request, context } = signOutRequest("en");

  const response = await POST(request, context);

  assert.equal(response.status, 307);
  assert.equal(
    response.headers.get("location"),
    "https://hyperwhisper.test/en/user/sign-in",
  );
  assert.equal(
    response.headers.get("set-cookie"),
    "better-auth.session_token=; Max-Age=0; Path=/; HttpOnly; SameSite=Lax",
  );
});
