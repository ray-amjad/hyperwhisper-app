// #1377: 7 blog posts link to /<locale>/legal/privacy, /<locale>/docs and
// /<locale>/pricing, which do not exist. These tests read the real
// next.config.mjs (through the next-intl wrapper), run its redirects() output
// through Next's own loader, and match paths with the regex Next writes into
// routes-manifest.json (what Vercel serves from), the same way
// security-headers.test.ts does for #842.
import assert from "node:assert/strict";
import test from "node:test";

import type { NextConfig } from "next";

import loadCustomRoutes from "next/dist/lib/load-custom-routes";
import { buildCustomRoute as buildManifestRoute } from "next/dist/lib/build-custom-route";
import { buildCustomRoute as buildServerRoute } from "next/dist/server/lib/router-utils/filesystem";
import { prepareDestination } from "next/dist/shared/lib/router/utils/prepare-destination";

import { blogLinkRedirects, localeParam } from "../config/redirects.mjs";
import { localeCodes } from "../src/i18n/locale-codes.mjs";
import { locales } from "../src/i18n/locales";

type Redirect = Awaited<ReturnType<typeof loadCustomRoutes>>["redirects"][number];

const loadConfig = async () => {
  // The config imports the env validator unless this is set.
  process.env.SKIP_ENV_VALIDATION = "1";
  // A non-literal specifier keeps tsc out of next.config.mjs: its top-level
  // await fails the es5 target, and `next build` never type-checks it.
  const specifier = "../next.config.mjs";
  const mod = (await import(specifier)) as { default: NextConfig };
  return mod.default;
};

const loadRedirects = async () => {
  const config = await loadConfig();
  assert.equal(typeof config.redirects, "function", "redirects() is wired");
  // Next's own loader validates the entries and drops any it rejects.
  const { redirects } = await loadCustomRoutes(config);
  return redirects;
};

/**
 * Follows a request for `path` through the redirects the way `next dev` and
 * `next start` do: the first rule whose matcher accepts the path wins, its
 * params fill the destination through Next's prepareDestination, and the
 * result is fed back in until nothing matches. Each hop is also checked
 * against the regex Next writes into routes-manifest.json (what Vercel
 * matches with), so the two cannot disagree. Returns every hop.
 */
const follow = (redirects: Redirect[], path: string) => {
  const hops: Array<{ to: string; statusCode: number | undefined; internal: boolean }> = [];
  let current = path;
  for (let i = 0; i < 5; i++) {
    let next: (typeof hops)[number] | null = null;
    for (const r of redirects) {
      const params = buildServerRoute("redirect", r).match(current);
      // `next build` passes the same restricted paths when it writes the manifest.
      const manifest = buildManifestRoute("redirect", r, ["/_next"]);
      const manifestHit = new RegExp(manifest.regex).test(current);
      assert.equal(manifestHit, params !== false, `${r.source} manifest regex agrees on ${current}`);
      if (params === false) continue;
      const { newUrl } = prepareDestination({
        destination: r.destination,
        params,
        query: {},
        appendParamsToQuery: false,
      });
      next = {
        to: newUrl,
        statusCode: manifest.statusCode,
        internal: Boolean((r as { internal?: boolean }).internal),
      };
      break;
    }
    if (!next) return hops;
    hops.push(next);
    // A #fragment never reaches the server, so the next request is the path.
    current = next.to.split("#")[0];
  }
  assert.fail(`${path} redirects more than 5 times (loop)`);
};

/** The one non-internal hop a path takes, or null when none. */
const resolve = (redirects: Redirect[], path: string) => {
  const own = follow(redirects, path).filter((h) => !h.internal);
  assert.ok(own.length <= 1, `${path} takes at most one blog-link hop`);
  return own[0] ?? null;
};

// Every link the issue found, plus the same families on other locales and the
// docs sub-path case.
const EXPECTED: Array<[string, string]> = [
  ["/en/legal/privacy", "/en/legal/privacy-policy"],
  ["/es/legal/privacy", "/es/legal/privacy-policy"],
  ["/zh-Hant/legal/privacy", "/zh-Hant/legal/privacy-policy"],
  ["/en/docs", "/docs"],
  ["/de/docs", "/docs"],
  ["/en/docs/quickstart", "/docs/quickstart"],
  ["/en/docs/a/b", "/docs/a/b"],
  ["/en/pricing", "/en#cloud"],
  ["/hi/pricing", "/hi#cloud"],
];

// Must never redirect: the real targets (no loop), top-level paths that look
// like the families, an unknown locale, and longer paths under the families.
const UNTOUCHED = [
  "/docs",
  "/docs/quickstart",
  "/en/legal/privacy-policy",
  "/en",
  "/en/legal/terms",
  "/legal/privacy",
  "/pricing",
  "/xx/legal/privacy",
  "/xx/docs",
  "/xx/pricing",
  "/api/docs",
  "/en/pricing/extra",
  "/en/legal/privacy/extra",
  "/en/docsx",
  "/en/pricingx",
];

test("the 3 rules are exactly the ones the issue proposes, all permanent", () => {
  assert.deepEqual(
    blogLinkRedirects.map(({ source, destination, permanent }) => [
      source,
      destination,
      permanent,
    ]),
    [
      [`/${localeParam}/legal/privacy`, "/:locale/legal/privacy-policy", true],
      [`/${localeParam}/docs/:path*`, "/docs/:path*", true],
      [`/${localeParam}/pricing`, "/:locale#cloud", true],
    ],
  );
});

test(":locale is built from the one locale list, every code and nothing else", () => {
  // locales.ts re-exports the same array, so the app and the redirects cannot drift.
  assert.equal(locales, localeCodes);
  assert.equal(locales.length, 40);
  const inner = /^:locale\((.*)\)$/.exec(localeParam)?.[1];
  assert.deepEqual(inner?.split("|"), [...locales]);
});

test("next.config.mjs (through withNextIntl) serves the 3 rules as 308s", async () => {
  const redirects = await loadRedirects();
  for (const rule of blogLinkRedirects) {
    const served = redirects.filter((r) => r.source === rule.source);
    assert.equal(served.length, 1, `${rule.source} is served once`);
    assert.equal(served[0].destination, rule.destination);
    assert.equal(served[0].permanent, true);
  }
});

test("each blog link family redirects to the real page", async () => {
  const redirects = await loadRedirects();
  for (const [from, to] of EXPECTED) {
    const hit = resolve(redirects, from);
    assert.ok(hit, `${from} should redirect`);
    assert.equal(hit.to, to, `${from} -> ${to}`);
    assert.equal(hit.statusCode, 308, `${from} is a 308`);
  }
});

test("a trailing slash still reaches the same target", async () => {
  const redirects = await loadRedirects();
  // With trailingSlash off, Next's own rule first 308s /x/ to /x, then the
  // blog-link rule applies.
  for (const [from, to] of [
    ["/en/legal/privacy/", "/en/legal/privacy-policy"],
    ["/en/docs/", "/docs"],
    ["/en/pricing/", "/en#cloud"],
  ]) {
    const hops = follow(redirects, from);
    assert.equal(hops.at(-1)?.to, to, `${from} ends at ${to}`);
  }
});

test("/docs, the real targets and other paths are never redirected", async () => {
  const redirects = await loadRedirects();
  for (const path of UNTOUCHED) {
    assert.equal(resolve(redirects, path), null, `${path} must not redirect`);
  }
});

test("no target is itself a redirect source (no loop)", async () => {
  const redirects = await loadRedirects();
  for (const [, to] of EXPECTED) {
    const pathOnly = to.split("#")[0];
    assert.deepEqual(follow(redirects, pathOnly), [], `${pathOnly} must not redirect again`);
  }
});
