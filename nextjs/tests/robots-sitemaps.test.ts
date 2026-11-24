import assert from "node:assert/strict";
import test from "node:test";

import { SITE_URL } from "../lib/site-metadata";

// Runs the REAL robots() and checks the root robots.txt names the docs
// sitemap. Crawlers read robots.txt only at the host root (#1884); the docs
// sitemap is app/docs/sitemap.ts, at the URL Mintlify served it at.

test("robots() declares the site sitemap and the docs sitemap", async () => {
  const { default: robots } = await import("../app/robots");
  const { sitemap } = robots();
  const sitemaps = Array.isArray(sitemap) ? sitemap : [sitemap];
  assert.deepEqual(sitemaps, [
    `${SITE_URL}/sitemap.xml`,
    `${SITE_URL}/docs/sitemap.xml`,
  ]);
});

test("the docs sitemap URL is a route of this app, not a rewrite", async () => {
  const { existsSync } = await import("node:fs");
  assert.ok(
    existsSync(new URL("../app/docs/sitemap.ts", import.meta.url)),
    "no app/docs/sitemap.ts behind /docs/sitemap.xml",
  );
  const { default: docsSitemap } = await import("../app/docs/sitemap");
  const urls = docsSitemap().map((e) => e.url);
  assert.ok(urls.includes(`${SITE_URL}/docs`));
  assert.ok(urls.includes(`${SITE_URL}/docs/models`));
  for (const url of urls) assert.ok(url.startsWith(`${SITE_URL}/docs`), url);
});
