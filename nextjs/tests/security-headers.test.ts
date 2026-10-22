// #842: the site sent no anti-framing header, so a third-party page could
// frame the sign-in and credit purchase pages (clickjacking). These tests read
// the real next.config.mjs (through the next-intl wrapper) and run its
// headers() output through Next's own loader, then match paths with the regex
// Next writes into routes-manifest.json (what Vercel serves from).
import assert from "node:assert/strict";
import test from "node:test";

import type { NextConfig } from "next";

import loadCustomRoutes from "next/dist/lib/load-custom-routes";
import { buildCustomRoute } from "next/dist/lib/build-custom-route";

import {
  securityHeaderRoutes,
  securityHeaders,
} from "../config/security-headers.mjs";

const EXPECTED: Record<string, string> = {
  "content-security-policy": "frame-ancestors 'none'",
  "x-frame-options": "DENY",
  "x-content-type-options": "nosniff",
  "referrer-policy": "strict-origin-when-cross-origin",
  "permissions-policy": "camera=(), microphone=(), geolocation=()",
};

// The framed surfaces named in the issue, a public page, the bare root, an
// API route and a static file: every response must carry the headers.
const PATHS = [
  "/",
  "/en",
  "/en/user/sign-in",
  "/de/credits",
  "/en/customer",
  "/api/license/validate",
  "/favicon.ico",
];

const loadConfig = async () => {
  // The config imports the env validator unless this is set.
  process.env.SKIP_ENV_VALIDATION = "1";
  // A non-literal specifier keeps tsc out of next.config.mjs: its top-level
  // await fails the es5 target, and `next build` never type-checks it.
  const specifier = "../next.config.mjs";
  const mod = (await import(specifier)) as { default: NextConfig };
  return mod.default;
};

test("the header list is exactly the 5 headers the issue proposes", () => {
  const actual = Object.fromEntries(
    securityHeaders.map(({ key, value }) => [key.toLowerCase(), value]),
  );
  assert.deepEqual(actual, EXPECTED);
  assert.deepEqual(securityHeaderRoutes, [
    { source: "/:path*", headers: securityHeaders },
  ]);
});

test("the CSP sets frame-ancestors only, no script or default policy", () => {
  const csp = securityHeaders.find(
    (h) => h.key === "Content-Security-Policy",
  )?.value;
  assert.equal(csp, "frame-ancestors 'none'");
  assert.doesNotMatch(csp ?? "", /script-src|default-src/);
});

test("next.config.mjs (through withNextIntl) serves the headers on /:path*", async () => {
  const config = await loadConfig();
  assert.equal(typeof config.headers, "function", "headers() is wired");
  assert.equal(config.turbopack?.root !== undefined, true, "turbopack kept");

  // Next's own loader validates the entries and drops any it rejects.
  const { headers } = await loadCustomRoutes(config);
  const catchAll = headers.filter((h) => h.source === "/:path*");
  assert.equal(catchAll.length, 1, "one /:path* rule");
  const served = Object.fromEntries(
    catchAll[0].headers.map(({ key, value }) => [key.toLowerCase(), value]),
  );
  assert.deepEqual(served, EXPECTED);
});

test("the /:path* rule matches the sign-in, credits and every other path", async () => {
  const config = await loadConfig();
  const { headers } = await loadCustomRoutes(config);
  for (const path of PATHS) {
    const hit = headers.some(
      (h) =>
        new RegExp(buildCustomRoute("header", h).regex).test(path) &&
        h.headers.some((x) => x.key === "X-Frame-Options" && x.value === "DENY"),
    );
    assert.equal(hit, true, `${path} should get X-Frame-Options: DENY`);
  }
});
