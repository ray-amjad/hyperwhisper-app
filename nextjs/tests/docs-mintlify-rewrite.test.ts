/**
 * The Mintlify docs are next.config rewrites (config/mintlify-rewrites.mjs),
 * moved from vercel.json when the site moved to Cloudflare Workers.
 *
 * #1233: the Mintlify client prefetches its home link `/docs` as an RSC
 * request. Vercel renamed that to `/docs.rsc` before its rewrites ran, so a
 * `/docs.rsc` rule exists; on Workers the request arrives as `/docs`. Both
 * must reach Mintlify. Sources are compiled with Next's own path-to-regexp.
 */
import assert from "node:assert/strict";
import test from "node:test";

// @ts-expect-error -- Next ships this compiled copy with no .d.ts.
import * as compiledPathToRegexp from "next/dist/compiled/path-to-regexp";

import {
  MINTLIFY_ORIGIN,
  mintlifyRewrites,
} from "../config/mintlify-rewrites.mjs";

const { pathToRegexp } = compiledPathToRegexp as {
  pathToRegexp: (path: string) => RegExp;
};

function firstRewrite(pathname: string) {
  return mintlifyRewrites.find(({ source }) =>
    pathToRegexp(source).test(pathname),
  );
}

for (const page of ["/docs", "/docs/models", "/docs/cloud-credits"]) {
  test(`a GET of ${page} goes to Mintlify`, () => {
    const rewrite = firstRewrite(page);

    assert.ok(rewrite, `${page} matches no docs rewrite`);
    assert.ok(rewrite.destination.startsWith(`${MINTLIFY_ORIGIN}/docs`));
  });
}

test("/docs.rsc is sent to Mintlify's own /docs.rsc", () => {
  assert.equal(
    firstRewrite("/docs.rsc")?.destination,
    `${MINTLIFY_ORIGIN}/docs.rsc`,
  );
});

test("Mintlify's assets and search API reach Mintlify", () => {
  assert.equal(
    firstRewrite("/api/request")?.destination,
    `${MINTLIFY_ORIGIN}/_mintlify/api/request`,
  );
  assert.ok(firstRewrite("/_mintlify/static/x.js"));
  assert.ok(firstRewrite("/mintlify-assets/a/b.png"));
});

test("control: app pages match no docs rewrite", () => {
  for (const path of ["/en", "/docsx", "/en/docs", "/api/license/validate"]) {
    assert.equal(firstRewrite(path), undefined, path);
  }
});
