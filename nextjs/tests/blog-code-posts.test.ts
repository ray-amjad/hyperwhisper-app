/**
 * Posts written in the repo (src/content/code-posts) list, render and enter the
 * sitemap like a stored Outrank post, and a code post with the same slug
 * replaces a stored row. Drives the real blog loader and the real sitemap();
 * the database and "server-only" are mocked, as in blog-db-error.test.ts.
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
const WINDOWS_SLUG = "best-dictation-software-for-windows";
const WINDOWS_URL = `https://www.hyperwhisper.com/en/blog/${WINDOWS_SLUG}`;
const FREE_SLUG = "free-speech-to-text-app";
const FREE_URL = `https://www.hyperwhisper.com/en/blog/${FREE_SLUG}`;
const UMBRELLA_SLUG = "best-dictation-software";
const UMBRELLA_URL = `https://www.hyperwhisper.com/en/blog/${UMBRELLA_SLUG}`;
const MAC_SLUG = "mac-voice-dictation-software";
const MAC_URL = `https://www.hyperwhisper.com/en/blog/${MAC_SLUG}`;
const WRITERS_SLUG = "best-dictation-software-for-writers";
const WRITERS_URL = `https://www.hyperwhisper.com/en/blog/${WRITERS_SLUG}`;
const GOOD_SLUG = "good-dictation-software";

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
    [
      "newer",
      UMBRELLA_SLUG,
      MAC_SLUG,
      WINDOWS_SLUG,
      WRITERS_SLUG,
      FREE_SLUG,
      THREE_WAY_SLUG,
      SLUG,
      "older",
    ],
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

test("a code post replaces a stored row with the same slug", async () => {
  rows = [stored(FREE_SLUG, "2026-05-13", "<p>From Outrank</p>")];
  const posts = await blog.getBlogPosts();
  assert.equal(posts.filter((post) => post.slug === FREE_SLUG).length, 1);
  const post = await blog.getBlogPost(FREE_SLUG);
  assert.ok(post);
  assert.match(post.frontMatter.title, /Free Speech to Text App/);
  assert.doesNotMatch(post.html, /From Outrank/);
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

test("the Windows dictation post answers Win+H vs Dragon and stays in the sitemap", async () => {
  rows = [stored(WINDOWS_SLUG, "2026-05-26", "<p>From Outrank</p>")];
  const post = await blog.getBlogPost(WINDOWS_SLUG);
  assert.ok(post);
  assert.equal(
    post.frontMatter.title,
    "Windows Dictation in 2026: Win+H vs Dragon",
  );
  assert.match(post.frontMatter.description, /Dragon/);
  assert.ok(post.frontMatter.title.length <= 60);
  assert.ok(post.frontMatter.description.length <= 155);
  assert.match(post.html, /Win<\/kbd>\+<kbd>H/);
  assert.match(post.html, /Voice Access \/ Speech Recognition vs Dragon/);
  assert.match(post.html, /replaced WSR with Voice Access/);
  assert.doesNotMatch(post.html, /Still present as the classic feature/);
  assert.match(post.html, /href="https:\/\/dragon\.nuance\.com\/en-us\/dragon-professional"/);
  assert.match(post.html, /href="\/en\/blog\/best-dictation-software"/);
  assert.match(post.html, /href="\/en\/blog\/mac-voice-dictation-software"/);
  assert.match(post.html, /href="\/en\/download"/);
  assert.match(post.html, /href="\/docs\/models"/);
  assert.match(post.html, /<h2>FAQ<\/h2>/);
  assert.match(post.html, /role="region"[^>]*>\s*<table/);
  assert.doesNotMatch(post.html, /\$39|5 min|99%/);
  assert.doesNotMatch(post.html, /From Outrank/);
  rows = [];
  const entries = await sitemap();
  assert.equal(entries.filter((entry) => entry.url === WINDOWS_URL).length, 1);
});

test("the free speech-to-text post targets voice-to-text queries and stays in the sitemap", async () => {
  rows = [];
  const post = await blog.getBlogPost(FREE_SLUG);
  assert.ok(post);
  assert.equal(
    post.frontMatter.title,
    "Free Speech to Text App in 2026: Voice to Text, Offline",
  );
  assert.ok(post.frontMatter.title.length <= 60);
  assert.ok(post.frontMatter.description.length <= 155);
  assert.match(post.frontMatter.description, /small business/i);
  assert.match(post.html, /voice to text/i);
  assert.match(post.html, /href="\/en\/download"/);
  assert.match(post.html, /href="\/docs\/models"/);
  assert.match(post.html, /href="\/en\/blog\/best-dictation-software-for-windows"/);
  assert.match(post.html, /href="\/en\/blog\/best-dictation-software"/);
  assert.match(post.html, /href="\/en\/blog\/mac-voice-dictation-software"/);
  assert.match(post.html, /<h2>FAQ<\/h2>/);
  assert.match(post.html, /role="region"[^>]*>\s*<table/);
  assert.doesNotMatch(post.html, /\$39|5 min|99%/);
  const entries = await sitemap();
  assert.equal(entries.filter((entry) => entry.url === FREE_URL).length, 1);
});

function assertSeoPost(
  post: NonNullable<Awaited<ReturnType<typeof blog.getBlogPost>>>,
) {
  assert.ok(post.frontMatter.title.length <= 60, post.frontMatter.title);
  assert.ok(
    post.frontMatter.description.length <= 155,
    post.frontMatter.description,
  );
  assert.match(post.html, /href="\/en\/download"/);
  assert.match(post.html, /<h2>FAQ<\/h2>/);
  assert.match(post.html, /role="region"[^>]*>\s*<table/);
  assert.doesNotMatch(post.html, /\$39|5 min|99%/);
}

test("the umbrella dictation post is the canonical head-term page", async () => {
  rows = [stored(UMBRELLA_SLUG, "2026-01-01", "<p>From Outrank</p>")];
  const post = await blog.getBlogPost(UMBRELLA_SLUG);
  assert.ok(post);
  assert.equal(post.frontMatter.title, "Best Dictation Software (2026)");
  assert.match(post.frontMatter.description, /small business/i);
  assertSeoPost(post);
  assert.match(post.html, /href="\/en\/blog\/mac-voice-dictation-software"/);
  assert.match(post.html, /href="\/en\/blog\/best-dictation-software-for-windows"/);
  assert.match(post.html, /href="\/en\/blog\/best-dictation-software-for-writers"/);
  assert.doesNotMatch(post.html, /From Outrank/);
  const entries = await sitemap();
  assert.equal(entries.filter((entry) => entry.url === UMBRELLA_URL).length, 1);
  assert.equal(
    entries.filter((entry) => entry.url.includes(GOOD_SLUG)).length,
    0,
  );
});

test("good-dictation-software is omitted from the list, sitemap, and getBlogPost", async () => {
  rows = [stored(GOOD_SLUG, "2026-06-24", "<p>From Outrank</p>")];
  assert.equal(
    (await blog.getBlogPosts()).filter((post) => post.slug === GOOD_SLUG).length,
    0,
  );
  assert.equal(await blog.getBlogPost(GOOD_SLUG), null);
  const entries = await sitemap();
  assert.equal(
    entries.filter(
      (entry) =>
        entry.url === `https://www.hyperwhisper.com/en/blog/${GOOD_SLUG}`,
    ).length,
    0,
  );
  assert.equal(entries.filter((entry) => entry.url === UMBRELLA_URL).length, 1);
});

test("the Mac dictation post is a comparison, not a how-to-only guide", async () => {
  rows = [stored(MAC_SLUG, "2026-05-15", "<p>From Outrank</p>")];
  const post = await blog.getBlogPost(MAC_SLUG);
  assert.ok(post);
  assert.equal(post.frontMatter.title, "Best Dictation App for Mac (2026)");
  assert.match(post.frontMatter.description, /dictation software for Mac/i);
  assertSeoPost(post);
  assert.match(post.html, /Apple Dictation vs third-party/);
  assert.match(post.html, /href="\/en\/blog\/best-dictation-software"/);
  assert.match(post.html, /href="\/en\/blog\/best-dictation-software-for-windows"/);
  assert.doesNotMatch(post.html, /From Outrank/);
  const entries = await sitemap();
  assert.equal(entries.filter((entry) => entry.url === MAC_URL).length, 1);
});

test("the writers post targets writers and points at the umbrella", async () => {
  rows = [stored(WRITERS_SLUG, "2026-06-08", "<p>From Outrank</p>")];
  const post = await blog.getBlogPost(WRITERS_SLUG);
  assert.ok(post);
  assert.equal(
    post.frontMatter.title,
    "Best Dictation Software for Writers (2026)",
  );
  assert.match(post.frontMatter.title, /writers/i);
  assert.match(post.frontMatter.description, /writers/i);
  assertSeoPost(post);
  assert.match(post.html, /href="\/en\/blog\/best-dictation-software"/);
  assert.doesNotMatch(post.html, /From Outrank/);
  const entries = await sitemap();
  assert.equal(entries.filter((entry) => entry.url === WRITERS_URL).length, 1);
});
