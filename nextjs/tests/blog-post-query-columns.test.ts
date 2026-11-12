/**
 * #2989: every blog render used to run `SELECT *` over blog_posts, so both
 * ~2 MB body columns came back on every list, sitemap and post read. The list
 * now reads only the summary columns of the English posts, and one post reads
 * its own row's HTML through the (locale, slug) unique index. content_markdown
 * is never read.
 *
 * The blog loader runs against a REAL drizzle node-postgres database whose
 * pool records each query instead of sending it, so these assertions read the
 * exact SQL text and parameters Postgres would receive, and drizzle's own row
 * mapping turns the canned rows back into objects.
 */
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { before, beforeEach, test } from "node:test";

import { drizzle } from "drizzle-orm/node-postgres";
import type { Pool } from "pg";

import { moduleMock, moduleUrl } from "./module-mock";

type StoredRow = Record<string, unknown> & { locale: string; slug: string };
type Query = { sql: string; params: unknown[] };

let queries: Query[] = [];
let stored: StoredRow[] = [];

/** The column names a query selects, in order: `select "a", "b" from ...`. */
function selectedColumns(sql: string): string[] {
  const list = /^select (.*?) from /.exec(sql)?.[1] ?? "";
  return Array.from(list.matchAll(/"([a-z_]+)"(?:,|$)/g), (match) => match[1]);
}

/**
 * Answers a query the way Postgres would for the canned rows: filters on the
 * locale and slug parameters, applies the limit, and returns each row as an
 * array in the selected column order (drizzle reads results in array mode).
 */
const recordingPool = {
  query: async (config: { text: string }, params: unknown[]) => {
    queries.push({ sql: config.text, params });
    const [locale, slug, limit] = params;
    let rows = stored.filter(
      (row) => row.locale === locale && (slug === undefined || row.slug === slug),
    );
    if (typeof limit === "number") rows = rows.slice(0, limit);
    const columns = selectedColumns(config.text);

    return { rows: rows.map((row) => columns.map((column) => row[column])) };
  },
} as unknown as Pool;

moduleMock.module(moduleUrl("../src/db/index.ts"), {
  namedExports: { db: drizzle(recordingPool) },
});

const nodeModule = createRequire(import.meta.url)("node:module") as {
  _load: (request: string, ...rest: unknown[]) => unknown;
};
const load = nodeModule._load;
nodeModule._load = function (request, ...rest) {
  if (request === "server-only") return {};
  return load.call(this, request, ...rest);
};

let blog: typeof import("../src/content/blog");
before(async () => {
  blog = await import("../src/content/blog");
});
beforeEach(() => {
  queries = [];
  stored = [];
});

/** A row as Postgres stores it, keyed by column name. */
function row(slug: string, overrides: Record<string, unknown> = {}): StoredRow {
  return {
    id: `id-${slug}`,
    external_id: `art-${slug}`,
    source: "outrank",
    locale: "en",
    slug,
    title: `Title of ${slug}`,
    description: "A description",
    content_markdown: "# MARKDOWN BODY",
    content_html: `<p>Body of ${slug}</p>`,
    image_url: "https://cdnimg.co/a/cover.png",
    image_alt: "Cover",
    tags: ["dictation"],
    published_at: "2026-01-01T00:00:00.000Z",
    created_at: "2026-01-01T00:00:00.000Z",
    updated_at: "2026-01-01T00:00:00.000Z",
    ...overrides,
  };
}

const SUMMARY_COLUMNS = [
  "locale",
  "slug",
  "title",
  "description",
  "image_url",
  "image_alt",
  "tags",
  "published_at",
];
const CODE_SLUG = "hyperwhisper-vs-spokenly";

test("the list reads only the summary columns of the English posts, newest first", async () => {
  stored = [row("a-real-post"), row("a-german-post", { locale: "de" })];
  const posts = await blog.getBlogPosts();

  assert.equal(queries.length, 1);
  const [{ sql, params }] = queries;
  assert.deepEqual(selectedColumns(sql), SUMMARY_COLUMNS);
  assert.doesNotMatch(sql, /content_markdown|content_html|\*/);
  assert.match(
    sql,
    /from "blog_posts" where "blog_posts"\."locale" = \$1 order by "blog_posts"\."published_at" desc$/,
  );
  assert.deepEqual(params, ["en"]);

  const post = posts.find((item) => item.slug === "a-real-post");
  assert.deepEqual(post, {
    locale: "en",
    slug: "a-real-post",
    frontMatter: {
      title: "Title of a-real-post",
      description: "A description",
      date: "2026-01-01T00:00:00.000Z",
      image: "https://cdnimg.co/a/cover.png",
      imageAlt: "Cover",
      tags: ["dictation"],
      slug: "a-real-post",
    },
  });
  assert.ok(!posts.some((item) => item.slug === "a-german-post"));
  assert.ok(posts.some((item) => item.slug === CODE_SLUG));
});

test("one post is one query for its locale and slug, limit 1, with the HTML but not the markdown", async () => {
  stored = [row("other-post"), row("a-real-post")];
  const post = await blog.getBlogPost("a-real-post");

  assert.equal(queries.length, 1);
  const [{ sql, params }] = queries;
  assert.deepEqual(selectedColumns(sql), [...SUMMARY_COLUMNS, "content_html"]);
  assert.doesNotMatch(sql, /content_markdown|\*/);
  assert.match(
    sql,
    /from "blog_posts" where \("blog_posts"\."locale" = \$1 and "blog_posts"\."slug" = \$2\) limit \$3$/,
  );
  assert.deepEqual(params, ["en", "a-real-post", 1]);

  assert.ok(post);
  assert.equal(post.slug, "a-real-post");
  assert.equal(post.frontMatter.title, "Title of a-real-post");
  assert.equal(post.html, "<p>Body of a-real-post</p>");
  assert.equal(post.content, undefined);
});

test("a slug with surrounding slashes is looked up normalised", async () => {
  stored = [row("a-real-post")];
  const post = await blog.getBlogPost("/a-real-post/");

  assert.deepEqual(queries[0].params, ["en", "a-real-post", 1]);
  assert.equal(post?.html, "<p>Body of a-real-post</p>");
});

test("no stored row falls back to the code post; an unknown slug is null", async () => {
  const codePost = await blog.getBlogPost(CODE_SLUG);
  assert.ok(codePost);
  assert.match(codePost.frontMatter.title, /HyperWhisper vs Spokenly/);
  assert.equal(await blog.getBlogPost("no-such-post"), null);
  assert.equal(queries.length, 2);
});

test("a stored row with a code post's slug wins", async () => {
  stored = [row(CODE_SLUG, { content_html: "<p>From Outrank</p>" })];
  assert.equal((await blog.getBlogPost(CODE_SLUG))?.html, "<p>From Outrank</p>");
});
