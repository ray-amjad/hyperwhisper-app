/**
 * The build has no database, so the prerendered renders of the pages that read
 * it are empty. scripts/cf-build.mjs drops them from .open-next/cache so the
 * deploy does not upload them to R2 as fresh, and the first request renders
 * them through Hyperdrive instead.
 */
import assert from "node:assert/strict";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { describe, test } from "node:test";

import { DB_RENDERED_ROUTES, dropDbRenderedCache } from "../scripts/cf-build.mjs";

function cacheWith(routes: string[]): { dir: string; file: (route: string) => string } {
  const dir = mkdtempSync(join(tmpdir(), "cf-cache-"));
  const file = (route: string) =>
    join(dir, "BUILDID", "route-cache", "APP_PAGE", "abc123", "$", `${route}.cache`);
  for (const route of routes) {
    mkdirSync(dirname(file(route)), { recursive: true });
    writeFileSync(file(route), "{}");
  }
  return { dir, file };
}

describe("dropDbRenderedCache", () => {
  test("drops the database pages and keeps the rest", () => {
    const keep = ["en", "en/older-versions", "de/latency", "de/blog", "robots.txt"];
    const drop = [...DB_RENDERED_ROUTES, "en/blog/hyperwhisper-vs-spokenly"];
    const { dir, file } = cacheWith([...keep, ...drop]);

    const removed = dropDbRenderedCache(dir);

    assert.deepEqual([...removed].sort(), [...drop].sort());
    for (const route of drop) assert.equal(existsSync(file(route)), false, route);
    for (const route of keep) assert.equal(existsSync(file(route)), true, route);
  });

  test("fails when an expected entry is missing (cache layout changed)", () => {
    const { dir } = cacheWith(["en", "en/latency"]);
    assert.throws(() => dropDbRenderedCache(dir), /no prerendered cache entry/);
  });
});

describe("database-backed routes revalidate", () => {
  test("the sitemap is not build-only", () => {
    const source = readFileSync(new URL("../app/sitemap.ts", import.meta.url), "utf8");
    assert.match(source, /^export const revalidate = \d+;$/m);
  });

  for (const page of ["blog", "latency", "choosing-a-model"]) {
    test(`/${page} revalidates`, () => {
      const source = readFileSync(
        new URL(`../app/[locale]/${page}/page.tsx`, import.meta.url),
        "utf8",
      );
      assert.match(source, /^export const revalidate = \d+;$/m);
    });
  }
});
