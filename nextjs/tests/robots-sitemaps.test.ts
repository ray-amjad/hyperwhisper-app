import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

import { SITE_URL } from "../lib/site-metadata";

// Runs the REAL robots() and checks the root robots.txt names the docs
// sitemap. Crawlers read robots.txt only at the host root, so the
// Sitemap: line in Mintlify's /docs/robots.txt is never seen (#1884).

test("robots() declares the site sitemap and the docs sitemap", async () => {
  const { default: robots } = await import("../app/robots");
  const { sitemap } = robots();
  const sitemaps = Array.isArray(sitemap) ? sitemap : [sitemap];
  assert.deepEqual(sitemaps, [
    `${SITE_URL}/sitemap.xml`,
    `${SITE_URL}/docs/sitemap.xml`,
  ]);
});

test("the docs sitemap URL is served by the /docs rewrite", () => {
  const vercel = JSON.parse(
    readFileSync(new URL("../vercel.json", import.meta.url), "utf8"),
  ) as { rewrites?: { source: string; destination: string }[] };
  const docs = vercel.rewrites?.find((r) => r.source === "/docs/:path*");
  assert.ok(docs, "nextjs/vercel.json has no /docs/:path* rewrite");
  assert.match(docs.destination, /\/docs\/:path\*$/);
});
