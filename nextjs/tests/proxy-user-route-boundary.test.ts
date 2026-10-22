/**
 * #2087: USER_ROUTE_REGEX had no segment boundary after `user`, so a
 * signed-out visit to /en/users or /en/user-guide (no such pages) was sent to
 * the sign-in wall instead of the 404 page. The sign-in and sign-out
 * exemptions had the same gap.
 *
 * Runs the REAL proxy default export with real NextRequest objects, signed
 * out unless a case adds the Better Auth session cookie. Controls: /user and
 * every /user/* page are still gated, and sign-in and sign-out are not.
 */
import assert from "node:assert/strict";
import test from "node:test";

import { NextRequest } from "next/server";

import proxy from "../proxy";

const ORIGIN = "https://www.hyperwhisper.com";
const SESSION = { cookie: "better-auth.session_token=token.sig" };

function request(path: string, headers: Record<string, string> = {}) {
  return new NextRequest(new URL(path, ORIGIN), {
    headers: { ...headers, "sec-fetch-dest": "document" },
  });
}

function signInRedirect(response: Response) {
  const header = response.headers.get("location");
  if (!header) return null;
  const url = new URL(header, ORIGIN);
  return url.pathname.endsWith("/user/sign-in")
    ? url.pathname + url.search
    : null;
}

for (const path of [
  "/en/users",
  "/en/user-guide",
  "/en/users/x",
  "/de/usersettings",
]) {
  test(`${path} is not gated behind sign-in`, async () => {
    const response = await proxy(request(path));

    assert.equal(
      signInRedirect(response),
      null,
      `${path} -> ${response.status} ${response.headers.get("location")}`,
    );
  });
}

for (const [path, signIn] of [
  ["/en/user", "/en/user/sign-in?returnTo=%2Fen%2Fuser"],
  ["/en/user/", "/en/user/sign-in?returnTo=%2Fen%2Fuser%2F"],
  ["/en/user/dashboard", "/en/user/sign-in?returnTo=%2Fen%2Fuser%2Fdashboard"],
  ["/de/user/devices", "/de/user/sign-in?returnTo=%2Fde%2Fuser%2Fdevices"],
  // Only starts like the sign-in page: no longer exempt from the gate.
  [
    "/en/user/sign-in-help",
    "/en/user/sign-in?returnTo=%2Fen%2Fuser%2Fsign-in-help",
  ],
  [
    "/en/user/auth/sign-outx",
    "/en/user/sign-in?returnTo=%2Fen%2Fuser%2Fauth%2Fsign-outx",
  ],
]) {
  test(`control: signed-out ${path} still goes to sign-in`, async () => {
    const response = await proxy(request(path));

    assert.equal(response.status, 307);
    assert.equal(signInRedirect(response), signIn);
  });
}

for (const path of [
  "/en/user/sign-in",
  "/en/user/sign-in?returnTo=%2Fen%2Fuser%2Fdashboard",
  "/en/user/auth/sign-out",
]) {
  test(`control: signed-out ${path} is not gated`, async () => {
    const response = await proxy(request(path));

    assert.equal(signInRedirect(response), null);
  });
}

test("control: /en/user/dashboard with a session cookie is not gated", async () => {
  const response = await proxy(request("/en/user/dashboard", SESSION));

  assert.equal(signInRedirect(response), null);
});
