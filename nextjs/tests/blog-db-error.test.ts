/**
 * #2072: a database error while the blog (re)renders must not read as "no
 * posts". At runtime it propagates, so Next keeps the last good ISR page of
 * /en/blog, each post and the sitemap; only `next build` swallows it, so a
 * deploy does not need Postgres. The same rule as src/content/latency.ts.
 *
 * Drives the real blog loader and the real sitemap(); "server-only" and the
 * database are mocked, because neither resolves under node --test.
 */
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { afterEach, before, beforeEach, describe, mock, test } from "node:test";

// `mock.module` exists in Node 22 behind --experimental-test-module-mocks, but
// @types/node@20 has no declaration for it; narrow to the one method used.
const moduleMock = mock as unknown as {
  module(specifier: string, options: { namedExports: Record<string, unknown> }): void;
};

let select: () => unknown;
moduleMock.module(new URL("../src/db/index.ts", import.meta.url).href, {
  namedExports: { db: { select: () => select() } },
});

// tsx loads src/content/blog.ts through require(), so the bare "server-only"
// specifier loads as an empty module for the rest of this file. Installed after
// mock.module, which wraps the loader it finds and would otherwise bypass this.
const nodeModule = createRequire(import.meta.url)("node:module") as {
  _load: (request: string, ...rest: unknown[]) => unknown;
};
const load = nodeModule._load;
nodeModule._load = function (request, ...rest) {
  if (request === "server-only") return {};
  return load.call(this, request, ...rest);
};

const dbDown = () => {
  throw new Error("ECONNREFUSED 127.0.0.1:5432");
};
const tableWith = (rows: unknown[]) => () => ({
  from: () => ({ orderBy: async () => rows }),
});

const row = {
  id: "row-1",
  externalId: "art-1",
  source: "outrank",
  locale: "en",
  slug: "a-real-post",
  title: "A real post",
  description: null,
  contentMarkdown: "",
  contentHtml: "<p>Body</p>",
  imageUrl: null,
  imageAlt: null,
  tags: [],
  publishedAt: new Date("2026-01-01T00:00:00.000Z"),
  createdAt: new Date("2026-01-01T00:00:00.000Z"),
  updatedAt: new Date("2026-01-01T00:00:00.000Z"),
};

type Blog = typeof import("../src/content/blog");
let blog: Blog;
let sitemap: typeof import("../app/sitemap").default;
const originalPhase = process.env.NEXT_PHASE;
const originalError = console.error;
let logged: unknown[][];

before(async () => {
  blog = await import("../src/content/blog");
  ({ default: sitemap } = await import("../app/sitemap"));
});
beforeEach(() => {
  logged = [];
  console.error = (...args: unknown[]) => {
    logged.push(args);
  };
});
afterEach(() => {
  console.error = originalError;
  if (originalPhase === undefined) delete process.env.NEXT_PHASE;
  else process.env.NEXT_PHASE = originalPhase;
});

const blogUrl = "https://hyperwhisper.com/en/blog/a-real-post";

describe("at runtime (a background revalidation)", () => {
  beforeEach(() => {
    delete process.env.NEXT_PHASE;
  });

  test("a database error rejects every blog read instead of reporting no posts", async () => {
    select = dbDown;
    await assert.rejects(blog.getBlogPosts(), /ECONNREFUSED/);
    await assert.rejects(blog.getBlogPost("a-real-post"), /ECONNREFUSED/);
    await assert.rejects(blog.getAllBlogPosts(), /ECONNREFUSED/);
  });

  test("a database error rejects the sitemap instead of dropping the posts", async () => {
    select = dbDown;
    await assert.rejects(sitemap(), /ECONNREFUSED/);
  });

  test("an empty table is not a failure: no posts, and an unknown slug is null", async () => {
    select = tableWith([]);
    assert.deepEqual(await blog.getBlogPosts(), []);
    assert.equal(await blog.getBlogPost("a-real-post"), null);
  });

  test("a healthy database lists the post, renders it and puts it in the sitemap", async () => {
    select = tableWith([row]);
    assert.deepEqual(
      (await blog.getBlogPosts()).map((post) => post.slug),
      ["a-real-post"],
    );
    assert.equal((await blog.getBlogPost("a-real-post"))?.html, "<p>Body</p>");
    assert.ok((await sitemap()).some((entry) => entry.url === blogUrl));
  });
});

describe("during next build", () => {
  beforeEach(() => {
    process.env.NEXT_PHASE = "phase-production-build";
  });

  test("a database error ships the blog empty and logs it, so the build does not fail", async () => {
    select = dbDown;
    assert.deepEqual(await blog.getBlogPosts(), []);
    assert.equal(await blog.getBlogPost("a-real-post"), null);
    const entries = await sitemap();
    assert.ok(entries.some((entry) => entry.url === "https://hyperwhisper.com/en/blog"));
    assert.ok(!entries.some((entry) => entry.url === blogUrl));
    assert.ok(logged.some((args) => String(args[0]).startsWith("[blog]")));
  });
});
