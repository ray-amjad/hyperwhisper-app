/**
 * SEO: every URL the Mintlify docs served keeps working after the move into
 * app/docs, at the same path (a page in lib/docs/nav.ts, so it is prerendered
 * by generateStaticParams) or as a 301 to a page that exists.
 * tests/fixtures/docs-old-urls.json is the list, taken from the live docs
 * sitemap before the move.
 *
 * The same list is checked over HTTP when DOCS_URLS_BASE is set, against a
 * running build (`next build && next start`, or `pnpm run preview`):
 *   DOCS_URLS_BASE=http://localhost:3000 npm test
 */
import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import test from "node:test";

import { docsRedirects } from "../config/docs.mjs";
import { DOCS_ENTRIES, findDocByPath } from "../lib/docs/nav";

type Fixture = {
  pages: string[];
  redirects: Record<string, string>;
  files: string[];
};

const fixture = JSON.parse(
  readFileSync(new URL("./fixtures/docs-old-urls.json", import.meta.url), "utf8"),
) as Fixture;

const app = (path: string) => new URL(`../app/${path}`, import.meta.url);

test("the fixture holds the whole old docs sitemap", () => {
  assert.equal(fixture.pages.length, 69);
  assert.ok(fixture.pages.includes("/docs"));
  assert.ok(fixture.pages.includes("/docs/models"), "/docs/models has search traffic");
  assert.equal(
    fixture.pages.filter((p) => /^\/docs\/api-reference\/[^/]+$/.test(p)).length,
    14,
    "one page per openapi.yaml operation",
  );
});

test("every old page is a docs page at the same path", () => {
  assert.ok(existsSync(app("docs/[[...slug]]/page.tsx")));
  for (const path of fixture.pages) {
    const entry = findDocByPath(path);

    assert.ok(entry, `${path} is not in lib/docs/nav.ts`);
    assert.equal(entry.href, path);
  }
});

test("every old redirect is a 301 to a docs page", () => {
  for (const [source, destination] of Object.entries(fixture.redirects)) {
    const rule = docsRedirects.find((r) => r.source === source);

    assert.ok(rule, `no redirect for ${source} in config/docs.mjs`);
    assert.equal(rule.destination, destination);
    assert.equal(rule.statusCode, 301, `${source} must be a 301`);
    assert.ok(findDocByPath(destination), `${destination} is not a docs page`);
    assert.equal(findDocByPath(source), undefined, `${source} is also a page`);
  }
});

test("every docs redirect lands on a docs page", () => {
  for (const { source, destination } of docsRedirects) {
    assert.ok(findDocByPath(destination), `${source} -> ${destination}`);
  }
});

test("Mintlify's other docs URLs are still served", () => {
  for (const path of fixture.files) {
    const served =
      existsSync(new URL(`../public${path}`, import.meta.url)) ||
      existsSync(app(`${path.slice(1)}/route.ts`)) ||
      (path === "/docs/sitemap.xml" && existsSync(app("docs/sitemap.ts"))) ||
      (path.endsWith(".md") &&
        Boolean(findDocByPath(path.slice(0, -3))) &&
        existsSync(app("docs-md/[[...slug]]/route.ts")));

    assert.ok(served, `${path} has no file or route`);
  }
});

test("no docs page is missing from the old list without a reason", () => {
  // A new page is fine; this only reports the count so a dropped page shows.
  const old = new Set(fixture.pages);
  const kept = DOCS_ENTRIES.filter((e) => old.has(e.href)).length;

  assert.equal(kept, fixture.pages.length);
});

const base = process.env.DOCS_URLS_BASE?.replace(/\/$/, "");

test("over HTTP: 200 at the same path, or a 301 to a 200", { skip: !base }, async () => {
  const get = (path: string) => fetch(`${base}${path}`, { redirect: "manual" });

  for (const path of [...fixture.pages, ...fixture.files]) {
    const res = await get(path);

    assert.equal(res.status, 200, `${path} -> ${res.status}`);
  }
  for (const [source, destination] of Object.entries(fixture.redirects)) {
    const res = await get(source);

    assert.equal(res.status, 301, `${source} -> ${res.status}`);
    assert.equal(new URL(res.headers.get("location") ?? "", base).pathname, destination);
    assert.equal((await get(destination)).status, 200);
  }
});
