/**
 * #1135: opening an English-only page switched a visitor's saved language.
 *
 * `/en/latency`, `/en/choosing-a-model`, `/en/blog` and `/en/blog/<slug>` call
 * `notFound()` for every locale but `en`, so every locale links to them with a
 * raw `/en/...` href. next-intl's middleware synced NEXT_LOCALE to the path
 * locale on that visit, so a German visitor's next bare `/` went to `/en`.
 *
 * This file runs the REAL proxy default export with real NextRequest objects.
 * Controls: `/en/download` still syncs the cookie to `en`, and `/` with
 * NEXT_LOCALE=de still redirects to `/de`.
 */
import assert from "node:assert/strict";
import test from "node:test";

import { NextRequest } from "next/server";

import proxy from "../proxy";

const ORIGIN = "https://www.hyperwhisper.com";

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

for (const path of [
  "/en/latency",
  "/en/latency/",
  "/en/choosing-a-model",
  "/en/blog",
  "/en/blog/some-post",
]) {
  test(`${path} keeps the visitor's NEXT_LOCALE`, async () => {
    const response = await proxy(request(path));

    assert.deepEqual(localeCookies(response), []);
    assert.equal(response.headers.get("x-pathname"), path);
  });
}

test("an English-only page keeps a Set-Cookie that is not NEXT_LOCALE", async () => {
  // next-intl writes only NEXT_LOCALE today; prove the strip is that narrow
  // by running the helper's filter over a response that already holds both.
  const { NextResponse } = await import("next/server");
  const original = NextResponse.next;

  NextResponse.next = ((init?: Parameters<typeof original>[0]) => {
    const response = original(init);

    response.cookies.set("other", "kept");

    return response;
  }) as typeof original;
  try {
    const response = await proxy(request("/en/latency"));

    assert.deepEqual(localeCookies(response), []);
    assert.ok(
      response.headers.getSetCookie().some((c) => c.startsWith("other=kept")),
    );
  } finally {
    NextResponse.next = original;
  }
});

test("control: /en/download still syncs NEXT_LOCALE to en", async () => {
  const response = await proxy(request("/en/download"));

  assert.equal(localeCookies(response).length, 1);
  assert.match(localeCookies(response)[0], /^NEXT_LOCALE=en;/);
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
