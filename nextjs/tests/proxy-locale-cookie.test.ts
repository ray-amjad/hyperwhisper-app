/**
 * #1135: opening an English-only page switched a visitor's saved language.
 *
 * `/en/latency`, `/en/choosing-a-model`, `/en/blog` and `/en/blog/<slug>` call
 * `notFound()` for every locale but `en`, so every locale links to them with a
 * raw `/en/...` href. next-intl's middleware synced NEXT_LOCALE to the path
 * locale on that visit, so a German visitor's next bare `/` went to `/en`.
 *
 * The proxy runs those paths through a next-intl middleware built with
 * `localeCookie: false`, so the cookie is never written: not in `Set-Cookie`,
 * not in NextResponse's cookie map, and not in `x-middleware-set-cookie` (which
 * the page render's `cookies()` reads).
 *
 * This file runs the REAL proxy default export with real NextRequest objects.
 * Controls: `/en/download` still syncs the cookie to `en`, and `/` with
 * NEXT_LOCALE=de still redirects to `/de`.
 */
import assert from "node:assert/strict";
import test from "node:test";

import createMiddleware from "next-intl/middleware";
import { NextRequest, type NextResponse } from "next/server";

import proxy from "../proxy";
import { routing } from "../src/i18n/routing";

const ORIGIN = "https://www.hyperwhisper.com";
const ENGLISH_ONLY_PATHS = [
  "/en/latency",
  "/en/latency/",
  "/en/choosing-a-model",
  "/en/blog",
  "/en/blog/some-post",
];

function request(path: string, cookie = "NEXT_LOCALE=de") {
  return new NextRequest(new URL(path, ORIGIN), {
    headers: { cookie, "sec-fetch-dest": "document" },
  });
}

function localeCookies(response: Response) {
  return response.headers
    .getSetCookie()
    .filter((cookie) => cookie.startsWith("NEXT_LOCALE="));
}

function assertNoLocaleCookieAnywhere(response: NextResponse) {
  assert.deepEqual(localeCookies(response), [], "Set-Cookie header");
  assert.deepEqual(
    response.cookies.getAll().filter((c) => c.name === "NEXT_LOCALE"),
    [],
    "ResponseCookies map",
  );
  assert.doesNotMatch(
    response.headers.get("x-middleware-set-cookie") ?? "",
    /NEXT_LOCALE=/,
    "x-middleware-set-cookie",
  );
}

// Every header except the cookie ones and the proxy's own x-pathname.
function comparableHeaders(response: Response) {
  const skip = new Set(["set-cookie", "x-middleware-set-cookie", "x-pathname"]);

  const kept: [string, string][] = [];

  response.headers.forEach((value, name) => {
    if (!skip.has(name)) kept.push([name, value]);
  });

  return kept;
}

for (const path of ENGLISH_ONLY_PATHS) {
  test(`${path} never writes NEXT_LOCALE`, async () => {
    const response = await proxy(request(path));

    assertNoLocaleCookieAnywhere(response);
    assert.equal(response.headers.get("x-pathname"), path);
  });

  test(`${path}: a later cookies.set does not bring NEXT_LOCALE back`, async () => {
    const response = await proxy(request(path));

    response.cookies.set("other", "1");

    assertNoLocaleCookieAnywhere(response);
    assert.ok(
      response.headers.getSetCookie().some((c) => c.startsWith("other=1")),
    );
  });

  test(`${path}: routing matches the stock next-intl middleware`, async () => {
    const stock = createMiddleware(routing)(request(path));
    const response = await proxy(request(path));

    assert.equal(response.status, stock.status);
    assert.deepEqual(comparableHeaders(response), comparableHeaders(stock));
  });
}

test("control: /en/download still syncs NEXT_LOCALE to en", async () => {
  const response = await proxy(request("/en/download"));

  assert.equal(localeCookies(response).length, 1);
  assert.match(localeCookies(response)[0], /^NEXT_LOCALE=en;/);
  assert.equal(response.cookies.get("NEXT_LOCALE")?.value, "en");
});

test("control: a path that only starts like one is not treated as English-only", async () => {
  const response = await proxy(request("/en/latency-report"));

  assert.match(localeCookies(response)[0] ?? "", /^NEXT_LOCALE=en;/);
});

test("control: / with NEXT_LOCALE=de still redirects to /de", async () => {
  const response = await proxy(request("/"));

  assert.equal(response.status, 307);
  assert.equal(new URL(response.headers.get("location")!).pathname, "/de");
});
