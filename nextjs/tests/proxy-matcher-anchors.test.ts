// #1125: the matcher's unanchored `docs` (and `api`, `_next`) let
// /docsx skip the proxy and render the home page with 200. Compiled with Next's
// own build code, so this checks the regex Next really ships.
import assert from "node:assert/strict";
import test from "node:test";

import * as staticInfo from "next/dist/build/analysis/get-page-static-info";
import { getMiddlewareRouteMatcher as routeMatcher } from "next/dist/shared/lib/router/utils/middleware-route-matcher";

import { config } from "../proxy";

type Matchers = Parameters<typeof routeMatcher>[0];
// Exported at runtime, missing from Next's .d.ts.
const { getMiddlewareMatchers } = staticInfo as unknown as {
  getMiddlewareMatchers: (m: string[], nextConfig: object) => Matchers;
};
const matches = routeMatcher(getMiddlewareMatchers(config.matcher, {}));
const runsProxy = (path: string) => matches(path, {} as never, {});

const SKIPPED =
  "/docs /docs/ /docs/a /docs/a/b /api /api/x /api/request /models /models/x /_mintlify/x /mintlify-assets/a/b";
const INTERNAL = "/_next/static/x /favicon.ico";
const PROXIED =
  "/docsx /docs-foo /apix /api-docs /modelsx /_nextx /_mintlifyx /mintlify-assetsx";
const CONTROLS = "/ /en /en/docs";

test("/docs, /api, /models, /_next, the Mintlify paths and subpaths skip the proxy", () => {
  for (const path of `${SKIPPED} ${INTERNAL}`.split(" ")) {
    assert.equal(runsProxy(path), false, `${path} should skip the proxy`);
  }
});

test("paths that only start with an excluded name run the proxy", () => {
  for (const path of `${PROXIED} ${CONTROLS}`.split(" ")) {
    assert.equal(runsProxy(path), true, `${path} should run the proxy`);
  }
});
