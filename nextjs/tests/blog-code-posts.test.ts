/**
 * Posts written in the repo (src/content/code-posts) list, render and enter the
 * sitemap like a stored Outrank post, and a stored row with the same slug
 * replaces one. Drives the real blog loader and the real sitemap(); the
 * database and "server-only" are mocked, as in blog-db-error.test.ts.
 */
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { before, test } from "node:test";

import type { SQL } from "drizzle-orm";
import { PgDialect } from "drizzle-orm/pg-core";

import { moduleMock, moduleUrl } from "./module-mock";

// The list is select().from().where(locale).orderBy(); one post is
// select().from().where(locale and slug).limit(1). The stub renders the real
// where clause and answers with the rows it matches.
const dialect = new PgDialect();
let rows: Array<{ locale: string; slug: string }> = [];
moduleMock.module(moduleUrl("../src/db/index.ts"), {
  namedExports: {
    db: {
      select: () => ({
        from: () => ({
          where: (condition: SQL) => {
            const [locale, slug] = dialect.sqlToQuery(condition).params;
            const matching = rows.filter(
              (item) =>
                item.locale === locale && (slug === undefined || item.slug === slug),
            );
            return {
              orderBy: async () => matching,
              limit: async (count: number) => matching.slice(0, count),
            };
          },
        }),
      }),
    },
  },
});

const nodeModule = createRequire(import.meta.url)("node:module") as {
  _load: (request: string, ...rest: unknown[]) => unknown;
};
const load = nodeModule._load;
nodeModule._load = function (request, ...rest) {
  if (request === "server-only") return {};
  return load.call(this, request, ...rest);
};

const SLUG = "hyperwhisper-vs-spokenly";
const URL_ = `https://www.hyperwhisper.com/en/blog/${SLUG}`;
const THREE_WAY_SLUG = "hyperwhisper-vs-handy-vs-fluidvoice";
const THREE_WAY_URL = `https://www.hyperwhisper.com/en/blog/${THREE_WAY_SLUG}`;

let blog: typeof import("../src/content/blog");
let sitemap: typeof import("../app/sitemap").default;
before(async () => {
  blog = await import("../src/content/blog");
  ({ default: sitemap } = await import("../app/sitemap"));
});

const stored = (slug: string, date: string, html = "<p>Stored</p>") => ({
  id: slug,
  externalId: slug,
  source: "outrank",
  locale: "en",
  slug,
  title: slug,
  description: null,
  contentMarkdown: "",
  contentHtml: html,
  imageUrl: null,
  imageAlt: null,
  tags: [],
  publishedAt: new Date(date),
  createdAt: new Date(date),
  updatedAt: new Date(date),
});

test("the code comparisons are listed by date among stored posts", async () => {
  rows = [stored("newer", "2026-12-01"), stored("older", "2026-01-01")];
  assert.deepEqual(
    (await blog.getBlogPosts()).map((post) => post.slug),
    ["newer", THREE_WAY_SLUG, SLUG, "older"],
  );
});

test("the Spokenly comparison renders with its sources and a scrollable table", async () => {
  rows = [];
  const post = await blog.getBlogPost(SLUG);
  assert.ok(post);
  assert.match(post.frontMatter.title, /HyperWhisper vs Spokenly/);
  assert.match(post.html, /href="https:\/\/spokenly\.app\/pricing"/);
  assert.match(post.html, /October 23, 2026/);
  assert.match(post.html, /\$29/);
  // The same body rewrite a stored post gets (#919): tables scroll on mobile.
  assert.match(post.html, /role="region"[^>]*>\s*<table/);
});

test("the Spokenly comparison is in the sitemap once", async () => {
  rows = [];
  const entries = await sitemap();
  assert.equal(entries.filter((entry) => entry.url === URL_).length, 1);
});

test("a stored row with the same slug replaces the code post", async () => {
  rows = [stored(SLUG, "2026-11-01", "<p>From Outrank</p>")];
  const posts = await blog.getBlogPosts();
  assert.equal(posts.filter((post) => post.slug === SLUG).length, 1);
  assert.equal((await blog.getBlogPost(SLUG))?.html, "<p>From Outrank</p>");
});

test("the Handy and FluidVoice comparison renders with its sources and a scrollable table", async () => {
  rows = [];
  const post = await blog.getBlogPost(THREE_WAY_SLUG);
  assert.ok(post);
  assert.match(post.frontMatter.title, /HyperWhisper vs Handy vs FluidVoice/);
  assert.match(post.html, /href="https:\/\/github\.com\/cjpais\/Handy"/);
  assert.match(post.html, /href="https:\/\/github\.com\/altic-dev\/FluidVoice"/);
  assert.match(post.html, /October 24, 2026/);
  assert.match(post.html, /role="region"[^>]*>\s*<table/);
});

test("the Handy and FluidVoice comparison is in the sitemap once", async () => {
  rows = [];
  const entries = await sitemap();
  assert.equal(entries.filter((entry) => entry.url === THREE_WAY_URL).length, 1);
});
