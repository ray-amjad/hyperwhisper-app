/**
 * #1233: every docs page view fired one failing `/docs?_rsc=…` request. The
 * Mintlify client prefetches its home link `/docs` as an RSC request
 * (`RSC: 1`). On Vercel, @vercel/next renames an RSC request's path to
 * `<path>.rsc` before the vercel.json rewrites run, so the request arrives as
 * `/docs.rsc`. `/docs/:path*` still catches `/docs/models.rsc`, but nothing
 * caught `/docs.rsc`: it fell through to app/[locale] (500, then 404 since
 * #1856) instead of Mintlify.
 *
 * This replays that routing against the REAL nextjs/vercel.json: the rename
 * regex is the one @vercel/next emits for an app-dir build, and the sources
 * are compiled with path-to-regexp, as Vercel compiles them.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

// @ts-expect-error -- Next ships this compiled copy with no .d.ts.
import * as compiledPathToRegexp from "next/dist/compiled/path-to-regexp";

const { pathToRegexp } = compiledPathToRegexp as {
  pathToRegexp: (path: string) => RegExp;
};

type Rewrite = { source: string; destination: string };

const MINTLIFY = "https://rayamjadltd.mintlify.dev";
const { rewrites } = JSON.parse(
  readFileSync(new URL("../vercel.json", import.meta.url), "utf8"),
) as { rewrites: Rewrite[] };

// @vercel/next: `^/((?!.+\.rsc).+?)(?:/)?$` -> `/$1.rsc` when `rsc: 1`.
function rscPath(pathname: string) {
  return pathname.replace(/^\/((?!.+\.rsc).+?)(?:\/)?$/, "/$1.rsc");
}

function firstRewrite(pathname: string) {
  return rewrites.find(({ source }) => pathToRegexp(source).test(pathname));
}

for (const page of ["/docs", "/docs/models", "/docs/cloud-credits"]) {
  test(`the RSC request for ${page} is rewritten to Mintlify`, () => {
    const path = rscPath(page);
    const rewrite = firstRewrite(path);

    assert.ok(rewrite, `${path} matches no vercel.json rewrite`);
    assert.ok(
      rewrite.destination.startsWith(`${MINTLIFY}/docs`),
      `${path} goes to ${rewrite.destination}`,
    );
  });

  test(`a plain GET of ${page} still goes to Mintlify`, () => {
    const rewrite = firstRewrite(page);

    assert.ok(rewrite, `${page} matches no vercel.json rewrite`);
    assert.ok(rewrite.destination.startsWith(`${MINTLIFY}/docs`));
  });
}

test("/docs.rsc is sent to Mintlify's own /docs.rsc", () => {
  assert.equal(firstRewrite("/docs.rsc")?.destination, `${MINTLIFY}/docs.rsc`);
});

test("control: an app page's RSC request matches no docs rewrite", () => {
  assert.equal(firstRewrite(rscPath("/en")), undefined);
  assert.equal(firstRewrite(rscPath("/docsx")), undefined);
});
