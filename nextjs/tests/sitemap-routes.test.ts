import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import test from "node:test";

import { locales } from "../src/i18n/locales";

import { moduleMock } from "./module-mock";

// Runs the REAL sitemap() and checks every URL it emits against the route files
// on disk, so a hand-written path with no page (#735: /pricing, /about,
// /legal/privacy …) or a locale-gated page in the 40-locale loop goes red here.

// The blog loader imports "server-only" and the database; the posts are not
// under test here, only the page list.
moduleMock.module(new URL("../src/content/blog.ts", import.meta.url).href, {
  namedExports: { getAllBlogPosts: async () => [] },
});

const BASE = "https://www.hyperwhisper.com";
const appDir = new URL("../app/", import.meta.url);
const pageFile = (path: string) => new URL(`[locale]${path}/page.tsx`, appDir);
// The gate blog, choosing-a-model and latency use to 404 off English.
const englishGate = /locale\s*!==\s*["']en["']/;

async function load() {
  const { default: sitemap } = await import("../app/sitemap");
  const entries = await sitemap();
  const localized = new Set<string>();
  const englishOnly: string[] = [];
  const unprefixed: string[] = [];
  for (const { url, alternates } of entries) {
    assert.ok(url.startsWith(`${BASE}/`), url);
    const [, locale, ...rest] = url.slice(BASE.length).split("/");
    const path = rest.length ? `/${rest.join("/")}` : "";
    if (!(locales as readonly string[]).includes(locale)) unprefixed.push(`/${locale}${path}`);
    else if (alternates) localized.add(path);
    else if (!path.startsWith("/blog/")) englishOnly.push(`${locale}:${path}`);
  }
  return { entries, localized: Array.from(localized), englishOnly, unprefixed };
}

test("every localized path is a page that renders on every locale", async () => {
  const { entries, localized } = await load();
  assert.ok(localized.length > 0);
  for (const path of localized) {
    const file = pageFile(path);
    assert.ok(existsSync(file), `no app/[locale]${path}/page.tsx`);
    assert.doesNotMatch(readFileSync(file, "utf8"), englishGate, `${path} 404s off English`);
    for (const locale of locales) {
      assert.ok(entries.some((e) => e.url === `${BASE}/${locale}${path}`), `${locale}${path}`);
    }
  }
});

test("every English-only entry is /en and a page gated to English", async () => {
  const { englishOnly } = await load();
  assert.deepEqual(englishOnly, ["en:/blog", "en:/choosing-a-model", "en:/latency"]);
  for (const path of englishOnly.map((p) => p.slice(3))) {
    const file = pageFile(path);
    assert.ok(existsSync(file), `no app/[locale]${path}/page.tsx`);
    assert.match(readFileSync(file, "utf8"), englishGate, `${path} renders on every locale`);
  }
});

test("/docs is listed exactly once, with no locale prefix", async () => {
  const { entries, unprefixed } = await load();
  assert.deepEqual(unprefixed, ["/docs"]);
  assert.equal(entries.filter((e) => e.url.endsWith("/docs")).length, 1);
});

test("the dead #735 paths are gone", async () => {
  const { localized, englishOnly } = await load();
  const all = [...localized, ...englishOnly.map((p) => p.slice(3))];
  for (const dead of ["/pricing", "/about", "/legal/privacy", "/legal/terms"]) {
    assert.ok(!all.includes(dead), `${dead} is listed`);
  }
});

// #816: the apex redirects to www, so a URL the site declares canonical must
// name www, or a crawler gets a redirect for every page.
test("sitemap, hreflang and robots.txt all name the www host", async () => {
  const { entries } = await load();
  for (const { url, alternates } of entries) {
    assert.ok(url.startsWith(`${BASE}/`), url);
    for (const href of Object.values(alternates?.languages ?? {})) {
      assert.ok(String(href).startsWith(`${BASE}/`), String(href));
    }
  }
  const { default: robots } = await import("../app/robots");
  assert.equal(robots().sitemap, `${BASE}/sitemap.xml`);
});

test("no app or lib file hard-codes the redirecting apex as a site URL", () => {
  // Content links (privacy page, email footers, FAQ copy) only need to land
  // somewhere; the declared URLs come from SITE_URL in lib/site-metadata.ts.
  for (const file of [
    "../app/sitemap.ts",
    "../app/robots.ts",
    "../app/[locale]/layout.tsx",
    "../app/[locale]/blog/[slug]/page.tsx",
    "../lib/localized-page-metadata.ts",
  ]) {
    const source = readFileSync(new URL(file, import.meta.url), "utf8");
    assert.doesNotMatch(source, /https:\/\/hyperwhisper\.com/, file);
  }
});
