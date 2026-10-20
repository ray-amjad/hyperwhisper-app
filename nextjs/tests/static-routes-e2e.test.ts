/**
 * #843, end to end: the public pages are prerendered and cacheable, and the
 * account routes are not. It boots the REAL production build with `next start`
 * and reads what the server sends: the `x-nextjs-prerender` and
 * `cache-control` headers, the `<html lang dir>` and the hreflang map.
 *
 * Before the fix, a `headers()` call in the root layout (and one in the
 * [locale] layout's metadata, and one in each not-found boundary) made every
 * route but the 3 `force-static` ones a fresh, uncacheable server render.
 *
 * It needs a build, which `npm test` does not make, so with no `.next/BUILD_ID`
 * it skips. To run it:
 *
 *   NODE_OPTIONS=--max-old-space-size=3072 SKIP_ENV_VALIDATION=1 RESEND_API_KEY=re_x \
 *     STRIPE_SECRET_KEY=sk_test_x npx next build
 *   node --import tsx --test tests/static-routes-e2e.test.ts
 */
import assert from "node:assert/strict";
import { type ChildProcess, spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { createServer } from "node:net";
import path from "node:path";
import { after, before, test } from "node:test";

const ROOT = path.join(__dirname, "..");
const HAS_BUILD = existsSync(path.join(ROOT, ".next", "BUILD_ID"));
const skip = HAS_BUILD ? false : "no .next build: run `next build` first";

// The 8 marketing routes the issue names. /credits is left out: it reads
// searchParams, so it stays dynamic.
const MARKETING = [
  "",
  "/download",
  "/support",
  "/open-source",
  "/older-versions",
  "/legal/privacy-policy",
  "/legal/terms-of-service",
  "/legal/refund-policy",
];

const ACCOUNT = ["/user/sign-in", "/customer/sign-in", "/customer"];

let server: ChildProcess | undefined;
let base = "";

function freePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const probe = createServer();

    probe.once("error", reject);
    probe.listen(0, "127.0.0.1", () => {
      const address = probe.address();
      const port = typeof address === "object" && address ? address.port : 0;

      probe.close(() => resolve(port));
    });
  });
}

async function waitForServer(url: string): Promise<void> {
  const deadline = Date.now() + 60_000;

  while (Date.now() < deadline) {
    try {
      await fetch(url, { redirect: "manual" });

      return;
    } catch {
      await new Promise((resolve) => setTimeout(resolve, 250));
    }
  }
  throw new Error(`next start did not answer ${url} within 60 s`);
}

before(async () => {
  if (!HAS_BUILD) return;

  const port = await freePort();

  base = `http://127.0.0.1:${port}`;
  server = spawn(
    process.execPath,
    [
      path.join(ROOT, "node_modules", "next", "dist", "bin", "next"),
      "start",
      "-p",
      String(port),
      "-H",
      "127.0.0.1",
    ],
    {
      cwd: ROOT,
      // Dummy values: the pages read none of them, but some server modules
      // build their clients at import time.
      env: {
        ...process.env,
        SKIP_ENV_VALIDATION: "1",
        RESEND_API_KEY: process.env.RESEND_API_KEY || "re_e2e_dummy",
        STRIPE_SECRET_KEY: process.env.STRIPE_SECRET_KEY || "sk_test_e2e_dummy",
      },
      stdio: "ignore",
    },
  );
  await waitForServer(`${base}/en/support`);
});

after(() => {
  server?.kill();
});

async function get(route: string) {
  const response = await fetch(`${base}${route}`, { redirect: "manual" });

  return {
    status: response.status,
    prerender: response.headers.get("x-nextjs-prerender"),
    cacheControl: response.headers.get("cache-control") ?? "",
    body: await response.text(),
  };
}

for (const route of MARKETING) {
  test(`/en${route} is prerendered and publicly cacheable`, { skip }, async () => {
    const { status, prerender, cacheControl } = await get(`/en${route}`);

    assert.equal(status, 200);
    // Next joins the header when it is set twice ("1, 1").
    assert.match(prerender ?? "", /^1(, 1)*$/, `x-nextjs-prerender: ${prerender}`);
    assert.doesNotMatch(cacheControl, /private|no-store/, cacheControl);
  });
}

for (const route of ACCOUNT) {
  test(`/en${route} stays private, no-cache, no-store`, { skip }, async () => {
    const { prerender, cacheControl } = await get(`/en${route}`);

    assert.equal(prerender, null);
    assert.match(cacheControl, /private, no-cache, no-store/, cacheControl);
  });
}

test("a deep localized page states its own lang, canonical and 41 hreflang links", { skip }, async () => {
  // The hreflang map used to come from the proxy's x-pathname header in the
  // [locale] layout; the page states it now. Deep path on purpose (#816).
  const { status, body } = await get("/de/legal/refund-policy");
  const url = "https://www.hyperwhisper.com/de/legal/refund-policy";

  assert.equal(status, 200);
  assert.match(body, /<html[^>]*\blang="de"/);
  assert.match(body, /<html[^>]*\bdir="ltr"/);
  assert.ok(body.includes(`<link rel="canonical" href="${url}"/>`), "canonical");
  assert.ok(body.includes(`<meta property="og:url" content="${url}"/>`), "og:url");
  // The page's own title and description reach the head with its path fields.
  assert.match(body, /<title>Refund Policy \| HyperWhisper - HyperWhisper<\/title>/);
  assert.match(body, /<meta name="description" content="HyperWhisper refund policy for/);
  assert.match(body, /<meta property="og:image"/);
  // The client provider must hydrate with the same locale: Providers writes its
  // useLocale() back onto <html lang>, and an "en" here turned lang="de" into
  // lang="en" after hydration (#843).
  assert.ok(body.includes(String.raw`\"locale\":\"de\",\"messages\"`), "client locale de");

  const hreflangs = Array.from(
    body.matchAll(/<link rel="alternate" hrefLang="([^"]+)" href="([^"]+)"/g),
  );

  assert.equal(hreflangs.length, 41);
  for (const [, lang, href] of hreflangs) {
    const locale = lang === "x-default" ? "en" : lang;

    assert.equal(href, `https://www.hyperwhisper.com/${locale}/legal/refund-policy`);
  }
});

test("a right-to-left locale's home page is served dir=rtl", { skip }, async () => {
  const { status, body } = await get("/ar");

  assert.equal(status, 200);
  assert.match(body, /<html[^>]*\blang="ar"/);
  assert.match(body, /<html[^>]*\bdir="rtl"/);
  assert.match(body, /hrefLang="x-default" href="https:\/\/www.hyperwhisper.com\/en"/);
});

test("an unmatched URL is a server-rendered 404 in its locale", { skip }, async () => {
  // app/global-not-found.tsx: the full page, with chrome, not Next's empty
  // client-only error shell.
  const { status, body } = await get("/de/zzz");

  assert.equal(status, 404);
  assert.match(body, /<html[^>]*\blang="de"/);
  assert.match(body, /<nav/);
  assert.match(body, /<meta name="robots" content="noindex"\/>/);
});

test("a non-locale first segment is a 404, not a 500", { skip }, async () => {
  // The proxy skips dotted paths, so this reaches [locale] (#1853).
  const { status } = await get("/favicon.icox");

  assert.equal(status, 404);
});
