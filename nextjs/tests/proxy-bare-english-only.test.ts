/**
 * #2056: /latency, /choosing-a-model and /blog exist only as /en/... (#1135:
 * every other locale calls notFound()). A bare visit went through next-intl's
 * language detection, so a German header or a NEXT_LOCALE=ja cookie sent it
 * to /de/latency or /ja/blog, a 404.
 *
 * A trailing slash is dropped (as next-intl drops it), and no redirect writes
 * NEXT_LOCALE: main wrote NEXT_LOCALE=en for a visitor with no language.
 *
 * Runs the REAL proxy default export with real NextRequest objects. Controls:
 * a bare /download still follows the visitor's language, and a path that only
 * starts like an English-only one is not redirected to /en.
 */
import assert from "node:assert/strict";
import test from "node:test";

import { NextRequest } from "next/server";

import proxy from "../proxy";

const ORIGIN = "https://www.hyperwhisper.com";
const VISITORS: [string, Record<string, string>][] = [
  ["a German Accept-Language", { "accept-language": "de-DE,de;q=0.9" }],
  ["a NEXT_LOCALE=ja cookie", { cookie: "NEXT_LOCALE=ja" }],
  ["an English visitor", { "accept-language": "en-US,en;q=0.9" }],
  ["no language at all", {}],
];
const BARE_PATHS: [string, string][] = [
  ["/latency", "/en/latency"],
  ["/latency/", "/en/latency"],
  ["/choosing-a-model", "/en/choosing-a-model"],
  ["/blog", "/en/blog"],
  ["/blog/some-post", "/en/blog/some-post"],
];

function request(path: string, headers: Record<string, string>) {
  return new NextRequest(new URL(path, ORIGIN), {
    headers: { ...headers, "sec-fetch-dest": "document" },
  });
}

function location(response: Response) {
  const header = response.headers.get("location");
  assert.ok(header, "no Location header");
  const url = new URL(header, ORIGIN);
  return url.pathname + url.search;
}

for (const [path, target] of BARE_PATHS) {
  for (const [who, headers] of VISITORS) {
    test(`bare ${path} with ${who} goes to ${target}`, async () => {
      const response = await proxy(request(path, headers));

      assert.equal(response.status, 307);
      assert.equal(location(response), target);
      assert.deepEqual(
        response.headers
          .getSetCookie()
          .filter((cookie) => cookie.startsWith("NEXT_LOCALE=")),
        [],
        "a redirect to an English-only page must not save a language",
      );
    });
  }
}

test("the query string survives the redirect", async () => {
  const response = await proxy(
    request("/blog/some-post?utm_source=x&a=1", { cookie: "NEXT_LOCALE=ja" }),
  );

  assert.equal(location(response), "/en/blog/some-post?utm_source=x&a=1");
});

test("control: bare /download still follows the German header", async () => {
  const response = await proxy(
    request("/download", { "accept-language": "de-DE,de;q=0.9" }),
  );

  assert.equal(response.status, 307);
  assert.equal(location(response), "/de/download");
});

test("control: /latency-report is not an English-only path", async () => {
  const response = await proxy(
    request("/latency-report", { cookie: "NEXT_LOCALE=ja" }),
  );

  assert.equal(location(response), "/ja/latency-report");
});

test("control: /de/latency is left to next-intl (still the #1135 404)", async () => {
  const response = await proxy(request("/de/latency", {}));

  assert.equal(response.headers.get("location"), null);
});
