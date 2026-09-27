/**
 * `GET /api/download` is the button on the /download page, the two
 * "Windows x64" / "Windows ARM64" links under it, and the link in the
 * download email. It reads the site's own Sparkle appcast, picks the newest
 * installer for the visitor's platform, and redirects to it.
 *
 * A fault here is silent and costly: an ARM64 laptop gets the x64 installer,
 * a Mac visitor gets an old build, or an untrusted `arch` value reaches the
 * RegExp the route builds. So the tests below drive the real route with real
 * Requests and assert what the browser receives — the status and the
 * `Location` header — and what the route asked the appcast origin for.
 *
 * The only collaborator is the global `fetch`, which the route uses to read
 * the appcast. It is replaced per test. Two tests feed it the real
 * `public/appcast*.xml` files, so a feed layout the route cannot parse fails
 * here before it ships.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { afterEach, beforeEach, describe, test } from "node:test";

import { GET, dynamic } from "../app/api/download/route";

// Not the production host: a preview deploy must read its OWN appcast, so the
// route has to build the feed URL from the request, not from a fixed domain.
const ORIGIN = "https://preview-123.example.test";

const WINDOWS_ARM_UA =
  "Mozilla/5.0 (Windows NT 10.0; ARM64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";
const WINDOWS_X64_UA =
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";
const MAC_UA =
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15";

/** A Windows feed in the same order as the real one: newest ARM64, newest x64, then an older pair. */
const WINDOWS_FEED = `<?xml version='1.0' encoding='utf-8'?>
<rss xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle" version="2.0">
<channel>
<item>
<title>2.0.0 (ARM64)</title>
<sparkle:os>windows-arm64</sparkle:os>
<enclosure
url="https://builds.example.test/HW-2.0.0-arm64.exe"
type="application/octet-stream" />
</item>
<item>
<title>2.0.0 (x64)</title>
<sparkle:os>windows-x64</sparkle:os>
<enclosure
url="https://builds.example.test/HW-2.0.0-x64.exe"
type="application/octet-stream" />
</item>
<item>
<title>1.0.0 (ARM64)</title>
<sparkle:os>windows-arm64</sparkle:os>
<enclosure url="https://builds.example.test/HW-1.0.0-arm64.exe" />
</item>
<item>
<title>1.0.0 (x64)</title>
<sparkle:os>windows-x64</sparkle:os>
<enclosure url="https://builds.example.test/HW-1.0.0-x64.exe" />
</item>
</channel>
</rss>`;

const MAC_FEED = `<rss><channel>
<title>hyperwhisper</title>
<item>
<title>3.1.0</title>
<enclosure url="https://builds.example.test/hw-3.1.0.dmg" length="1" type="application/octet-stream"/>
</item>
<item>
<title>3.0.0</title>
<enclosure url="https://builds.example.test/hw-3.0.0.dmg" length="1" type="application/octet-stream"/>
</item>
</channel></rss>`;

type FetchCall = { url: string; init: RequestInit | undefined };

const realFetch = globalThis.fetch;
const realConsoleError = console.error;
let fetchCalls: FetchCall[] = [];
let errorLines: unknown[][] = [];

/** Answers every appcast fetch with `body` and `status`. */
function serveFeed(body: string, status = 200) {
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    fetchCalls.push({ url: String(input), init });
    return new Response(body, { status });
  }) as typeof fetch;
}

function serveFeeds(byPath: Record<string, string>) {
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    fetchCalls.push({ url: String(input), init });
    const path = new URL(String(input)).pathname;
    const body = byPath[path];
    return body === undefined
      ? new Response("not found", { status: 404 })
      : new Response(body, { status: 200 });
  }) as typeof fetch;
}

function download(query: string, userAgent?: string): Promise<Response> {
  const headers = new Headers();
  if (userAgent !== undefined) {
    headers.set("user-agent", userAgent);
  }
  return GET(new Request(`${ORIGIN}/api/download${query}`, { headers }));
}

function assertRedirectTo(response: Response, expected: string) {
  assert.equal(response.status, 307);
  assert.equal(response.headers.get("location"), expected);
}

async function assertFailure(response: Response) {
  assert.equal(response.status, 500);
  assert.equal(response.headers.get("location"), null);
  assert.deepEqual(await response.json(), {
    error: "Failed to get latest download URL",
  });
}

/** The first enclosure URL after the first `<sparkle:os>` with this value, read by plain string search. */
function firstEnclosureAfter(xml: string, marker: string): string {
  const at = xml.indexOf(marker);
  assert.ok(at >= 0, `the feed holds no ${marker}`);
  const urlAt = xml.indexOf('url="', at) + 'url="'.length;
  return xml.slice(urlAt, xml.indexOf('"', urlAt));
}

beforeEach(() => {
  fetchCalls = [];
  errorLines = [];
  console.error = (...args: unknown[]) => {
    errorLines.push(args);
  };
});

afterEach(() => {
  globalThis.fetch = realFetch;
  console.error = realConsoleError;
});

describe("GET /api/download — platform and feed", () => {
  test("is evaluated per request, so a new release is served at once", () => {
    assert.equal(dynamic, "force-dynamic");
  });

  test("a Mac visitor gets the newest item of appcast.xml on the request's own origin", async () => {
    serveFeed(MAC_FEED);

    const response = await download("?platform=mac", MAC_UA);

    assertRedirectTo(response, "https://builds.example.test/hw-3.1.0.dmg");
    assert.equal(fetchCalls.length, 1);
    assert.equal(fetchCalls[0].url, `${ORIGIN}/appcast.xml`);
    assert.equal(fetchCalls[0].init?.cache, "no-store");
    assert.ok(fetchCalls[0].init?.signal instanceof AbortSignal);
  });

  test("no platform, or an unknown one, means the Mac feed", async () => {
    serveFeed(MAC_FEED);

    for (const query of ["", "?platform=linux", "?platform=WINDOWS"]) {
      fetchCalls = [];
      const response = await download(query, WINDOWS_ARM_UA);
      assertRedirectTo(response, "https://builds.example.test/hw-3.1.0.dmg");
      assert.equal(fetchCalls[0].url, `${ORIGIN}/appcast.xml`, query);
    }
  });

  test("a Windows visitor reads appcast-windows.xml, not the Mac feed", async () => {
    serveFeeds({ "/appcast-windows.xml": WINDOWS_FEED, "/appcast.xml": MAC_FEED });

    const response = await download("?platform=windows", WINDOWS_X64_UA);

    assertRedirectTo(response, "https://builds.example.test/HW-2.0.0-x64.exe");
    assert.deepEqual(
      fetchCalls.map((call) => call.url),
      [`${ORIGIN}/appcast-windows.xml`],
    );
  });
});

describe("GET /api/download — Windows architecture", () => {
  beforeEach(() => serveFeed(WINDOWS_FEED));

  test("an ARM64 user agent gets the newest ARM64 installer", async () => {
    assertRedirectTo(
      await download("?platform=windows", WINDOWS_ARM_UA),
      "https://builds.example.test/HW-2.0.0-arm64.exe",
    );
  });

  test("an x64 user agent skips the ARM64 item listed first and gets the newest x64 installer", async () => {
    assertRedirectTo(
      await download("?platform=windows", WINDOWS_X64_UA),
      "https://builds.example.test/HW-2.0.0-x64.exe",
    );
  });

  test("a bare 'ARM' in the user agent counts as ARM64, in any case", async () => {
    assertRedirectTo(
      await download("?platform=windows", "Mozilla/5.0 (Windows NT 10.0; arm)"),
      "https://builds.example.test/HW-2.0.0-arm64.exe",
    );
  });

  test("no user agent at all defaults to x64", async () => {
    assertRedirectTo(
      await download("?platform=windows"),
      "https://builds.example.test/HW-2.0.0-x64.exe",
    );
  });

  test("an explicit arch overrides the user agent, both ways", async () => {
    assertRedirectTo(
      await download("?platform=windows&arch=x64", WINDOWS_ARM_UA),
      "https://builds.example.test/HW-2.0.0-x64.exe",
    );
    assertRedirectTo(
      await download("?platform=windows&arch=arm64", WINDOWS_X64_UA),
      "https://builds.example.test/HW-2.0.0-arm64.exe",
    );
  });

  test("an arch outside the allow-list is ignored, so it never reaches the RegExp", async () => {
    // Each of these would change what the built RegExp matches if the route
    // interpolated it: a wildcard, an alternation, and a different case.
    for (const arch of ["x64|arm64", ".*", "(a+)+$", "ARM64", "x86"]) {
      assertRedirectTo(
        await download(`?platform=windows&arch=${encodeURIComponent(arch)}`, WINDOWS_X64_UA),
        "https://builds.example.test/HW-2.0.0-x64.exe",
      );
    }
    assertRedirectTo(
      await download(`?platform=windows&arch=${encodeURIComponent(".*")}`, WINDOWS_ARM_UA),
      "https://builds.example.test/HW-2.0.0-arm64.exe",
    );
  });

  test("the arch value is ignored on the Mac feed", async () => {
    serveFeed(MAC_FEED);
    assertRedirectTo(
      await download("?arch=arm64", MAC_UA),
      "https://builds.example.test/hw-3.1.0.dmg",
    );
  });
});

describe("GET /api/download — failures answer 500 and never redirect", () => {
  test("the appcast origin answers non-2xx", async () => {
    serveFeed(MAC_FEED, 503);
    await assertFailure(await download("?platform=mac", MAC_UA));
  });

  test("the feed has no item for the requested architecture", async () => {
    serveFeed(WINDOWS_FEED.replaceAll("windows-arm64", "windows-x86"));
    await assertFailure(await download("?platform=windows&arch=arm64", WINDOWS_X64_UA));
  });

  test("the feed has no item at all", async () => {
    serveFeed("<rss><channel></channel></rss>");
    await assertFailure(await download("?platform=mac", MAC_UA));
    assert.deepEqual(errorLines, []);
  });

  test("an enclosure URL that does not parse is refused and logged", async () => {
    serveFeed(MAC_FEED.replace("https://builds.example.test/hw-3.1.0.dmg", "not a url"));
    await assertFailure(await download("?platform=mac", MAC_UA));
    assert.equal(errorLines.length, 1);
    assert.equal(errorLines[0][0], "Error parsing appcast:");
  });

  test("a network fault is caught and logged", async () => {
    globalThis.fetch = (async () => {
      throw new TypeError("fetch failed");
    }) as typeof fetch;
    await assertFailure(await download("?platform=windows", WINDOWS_X64_UA));
    assert.equal(errorLines.length, 1);
    assert.equal(errorLines[0][0], "Error parsing appcast:");
  });

  test("a hung appcast origin is abandoned after about 3 seconds", async () => {
    // The fake never answers; it only rejects when the route's own signal fires.
    globalThis.fetch = ((_input: RequestInfo | URL, init?: RequestInit) =>
      new Promise<Response>((_resolve, reject) => {
        const signal = init?.signal;
        assert.ok(signal, "the route must pass an abort signal");
        signal.addEventListener("abort", () => reject(signal.reason), { once: true });
      })) as typeof fetch;

    // AbortSignal.timeout uses an unref'd timer. Hold the event loop open, or
    // the runner sees nothing pending and cancels the test before it fires.
    const keepAlive = setInterval(() => {}, 1_000);
    const started = performance.now();
    let response: Response;
    try {
      response = await download("?platform=mac", MAC_UA);
    } finally {
      clearInterval(keepAlive);
    }
    const elapsed = performance.now() - started;

    await assertFailure(response);
    assert.ok(elapsed >= 2_900, `gave up after ${Math.round(elapsed)} ms`);
    assert.ok(elapsed < 4_500, `gave up after ${Math.round(elapsed)} ms`);
  });
});

describe("GET /api/download — the real appcast files", () => {
  const macFeed = readFileSync(new URL("../public/appcast.xml", import.meta.url), "utf8");
  const windowsFeed = readFileSync(
    new URL("../public/appcast-windows.xml", import.meta.url),
    "utf8",
  );

  beforeEach(() =>
    serveFeeds({ "/appcast.xml": macFeed, "/appcast-windows.xml": windowsFeed }),
  );

  test("the Mac feed resolves to its first item's .dmg", async () => {
    const expected = firstEnclosureAfter(macFeed, "<item>");
    assert.match(expected, /^https:\/\/builds\.hyperwhisper\.com\/.+\.dmg$/);

    assertRedirectTo(await download("?platform=mac", MAC_UA), expected);
  });

  test("the Windows feed resolves each architecture to its own newest installer", async () => {
    const arm = firstEnclosureAfter(windowsFeed, "<sparkle:os>windows-arm64</sparkle:os>");
    const x64 = firstEnclosureAfter(windowsFeed, "<sparkle:os>windows-x64</sparkle:os>");
    assert.match(arm, /-arm64-Setup\.exe$/);
    assert.match(x64, /-x64-Setup\.exe$/);

    assertRedirectTo(await download("?platform=windows", WINDOWS_ARM_UA), arm);
    assertRedirectTo(await download("?platform=windows", WINDOWS_X64_UA), x64);
    assertRedirectTo(await download("?platform=windows&arch=arm64", WINDOWS_X64_UA), arm);
  });
});
