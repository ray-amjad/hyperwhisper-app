/**
 * #2009: Outrank article HTML is rendered with dangerouslySetInnerHTML on
 * www.hyperwhisper.com, so it must never carry script. The webhook stores only
 * sanitised HTML, getBlogPost() sanitises a stored row again before it renders,
 * and a normal article's markup comes through unchanged.
 *
 * The webhook is driven through the real route (blog-webhook-harness mocks its
 * env and database). The read path is driven through the real getBlogPost():
 * "server-only" and the database are mocked here, because neither resolves
 * under node --test.
 */
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import { afterEach, before, beforeEach, describe, test } from "node:test";

import {
  dbHandlers,
  loadBlogWebhookRoute,
  postRaw,
  resetHarness,
} from "./blog-webhook-harness";
import { sanitizeBlogHtml } from "../src/content/sanitize-blog-html";
import { serializeJsonLd } from "../lib/security/json-ld";

// "server-only" is not a package of its own here (Next resolves it inside a
// build), and tsx loads src/content/blog.ts through require(), so that one
// bare specifier loads as an empty module for the rest of this file.
const nodeModule = createRequire(import.meta.url)("node:module") as {
  _load: (request: string, ...rest: unknown[]) => unknown;
};
const load = nodeModule._load;
nodeModule._load = function (request, ...rest) {
  if (request === "server-only") return {};
  return load.call(this, request, ...rest);
};

const HOSTILE_HTML = [
  "<h2>Hello</h2>",
  "<p>Before<script>alert(document.cookie)</script>after</p>",
  '<img src="https://cdnimg.co/a/x.png" alt="x" onerror="alert(1)">',
  '<p><a href="javascript:alert(1)">click</a> <a href=" JaVaScRiPt:alert(2)">two</a></p>',
  '<p style="position:fixed" onclick="alert(3)">styled</p>',
  "<style>body{display:none}</style>",
  '<a href="data:text/html,<script>alert(4)</script>">data</a>',
].join("\n");

/** What survives of HOSTILE_HTML: every listed tag, none of the script. */
const HOSTILE_SANITISED = [
  "<h2>Hello</h2>",
  "<p>Beforeafter</p>",
  '<img src="https://cdnimg.co/a/x.png" alt="x" />',
  "<p><a>click</a> <a>two</a></p>",
  "<p>styled</p>",
  "",
  "<a>data</a>",
].join("\n");

/** A normal article, written in the form the sanitiser outputs. */
const NORMAL_ARTICLE = [
  '<h1>Dictation on a Mac</h1>',
  '<h2 id="setup">Set up &amp; go</h2>',
  "<p>Some <strong>bold</strong>, <em>emphasis</em> and <code>code</code>.</p>",
  '<p><a href="https://www.hyperwhisper.com/download" target="_blank" rel="noopener noreferrer">Download</a>, <a href="/en/blog">the blog</a>, <a href="#setup">setup</a>, <a href="mailto:hi@example.com">mail</a>.</p>',
  "<ul><li>One</li><li>Two</li></ul><ol><li>First</li></ol>",
  "<blockquote><p>Quoted</p></blockquote>",
  "<pre><code>npm install</code></pre>",
  '<table><thead><tr><th scope="col">Model</th><th align="right">Speed</th></tr></thead><tbody><tr><td>Parakeet</td><td colspan="2" align="right">Fast</td></tr></tbody></table>',
  '<figure><img src="https://cdnimg.co/a/schedule.jpg" alt="A flow chart" width="800" height="450" /><figcaption>Caption</figcaption></figure>',
  '<iframe src="https://www.youtube.com/embed/dQw4w9WgXcQ" width="560" height="315" title="YouTube video player" allow="accelerometer; encrypted-media" allowfullscreen></iframe>',
  '<iframe src="https://player.vimeo.com/video/76979871" width="640" height="360"></iframe>',
  "<hr /><p>line<br />break</p>",
].join("\n");

function assertNoScript(html: string): void {
  assert.doesNotMatch(html, /<script/i);
  assert.doesNotMatch(html, /<style/i);
  assert.doesNotMatch(html, /\son[a-z]+\s*=/i);
  assert.doesNotMatch(html, /javascript:/i);
  assert.doesNotMatch(html, /data:/i);
  assert.doesNotMatch(html, /style=/i);
}

describe("sanitizeBlogHtml", () => {
  test("strips script, on* handlers, style and javascript:/data: links", () => {
    const html = sanitizeBlogHtml(HOSTILE_HTML);
    assertNoScript(html);
    assert.equal(html, HOSTILE_SANITISED);
  });

  test("a normal article's markup comes out unchanged", () => {
    assert.equal(sanitizeBlogHtml(NORMAL_ARTICLE), NORMAL_ARTICLE);
  });

  test("an iframe survives only for the YouTube embed and Vimeo player prefixes", () => {
    for (const src of [
      "https://evil.example/embed/x",
      "https://www.youtube-nocookie.com/embed/x",
      "https://www.youtube.com/watch?v=x",
      "https://youtube.com/embed/x",
      "http://www.youtube.com/embed/x",
      "//www.youtube.com/embed/x",
      "https://www.youtube.com.evil.example/embed/x",
      "https://vimeo.com/76979871",
      "/embed/x",
      "javascript:alert(1)",
    ]) {
      assert.equal(
        sanitizeBlogHtml(`<p>a</p><iframe src="${src}">inner</iframe><p>b</p>`),
        "<p>a</p><p>b</p>",
        src,
      );
    }
    assert.equal(sanitizeBlogHtml("<iframe></iframe>"), "");
    assert.equal(
      sanitizeBlogHtml('<iframe src="https://www.youtube.com/embed/x" onload="alert(1)" srcdoc="<script>1</script>"></iframe>'),
      '<iframe src="https://www.youtube.com/embed/x"></iframe>',
    );
  });

  test("img srcset with a javascript: candidate is dropped, a clean one kept", () => {
    assert.equal(
      sanitizeBlogHtml('<img srcset="javascript:alert(1) 1x, https://cdnimg.co/a/b.png 2x" alt="b">'),
      '<img alt="b" />',
    );
    const clean =
      '<img srcset="https://cdnimg.co/a/a.png 1x, https://cdnimg.co/a/b.png 2x" alt="b" />';
    assert.equal(sanitizeBlogHtml(clean), clean);
  });
});

describe("POST /api/webhooks/add-blog-post stores sanitised HTML", () => {
  let route: Awaited<ReturnType<typeof loadBlogWebhookRoute>>;
  let inserted: Array<Record<string, unknown>>;
  let updated: Array<Record<string, unknown>>;

  before(async () => {
    route = await loadBlogWebhookRoute();
  });
  beforeEach(() => {
    resetHarness();
    inserted = [];
    updated = [];
    dbHandlers.select = () => ({
      from: () => ({ where: () => ({ limit: async () => [] }) }),
    });
    dbHandlers.insert = () => ({
      values: (values: Record<string, unknown>) => {
        inserted.push(values);
        return {
          onConflictDoUpdate: ({ set }: { set: Record<string, unknown> }) => {
            updated.push(set);
            return {
              returning: async () => [{ id: "row-1", slug: values.slug }],
            };
          },
        };
      },
    });
  });
  afterEach(() => resetHarness());

  async function publish(article: Record<string, unknown>) {
    const body = JSON.stringify({
      event_type: "publish_articles",
      data: { articles: [{ id: "art-2009", title: "XSS post", ...article }] },
    });
    const response = await route.POST(postRaw(body));
    assert.equal(response.status, 200);
    assert.equal((await response.json()).processed, 1);
    assert.equal(inserted.length, 1);
    assert.equal(updated.length, 1);
    return { insert: inserted[0].contentHtml, update: updated[0].contentHtml };
  }

  test("content_html with <script>, <img onerror> and a javascript: link is stored clean", async () => {
    const { insert, update } = await publish({ content_html: HOSTILE_HTML });
    for (const stored of [insert, update]) {
      assert.equal(typeof stored, "string");
      assertNoScript(stored as string);
      assert.equal(stored, HOSTILE_SANITISED);
    }
  });

  test("a normal article's content_html is stored unchanged", async () => {
    const { insert, update } = await publish({ content_html: NORMAL_ARTICLE });
    assert.equal(insert, NORMAL_ARTICLE);
    assert.equal(update, NORMAL_ARTICLE);
  });

  test("the markdown fallback is still stored as before", async () => {
    const { insert } = await publish({
      content_markdown: "# Title\n\n<script>alert(1)</script> one\ntwo",
    });
    assert.equal(
      insert,
      "<h1>Title</h1>\n<p>&lt;script&gt;alert(1)&lt;/script&gt; one<br />two</p>",
    );
  });
  test("content_html that sanitises to nothing falls back to the markdown", async () => {
    const { insert, update } = await publish({
      content_html: "<script>alert(1)</script><style>p{}</style>",
      content_markdown: "# Title\n\nBody",
    });
    for (const stored of [insert, update]) {
      assert.equal(stored, "<h1>Title</h1>\n<p>Body</p>");
    }
  });
});

describe("getBlogPost sanitises a row stored before the fix", () => {
  let rows: Array<Record<string, unknown>>;
  let getBlogPost: typeof import("../src/content/blog").getBlogPost;

  before(async () => {
    // The webhook harness already mocked ../src/db/index.ts; its select
    // handler is set per test here to answer the blog read query.
    ({ getBlogPost } = await import("../src/content/blog"));
  });
  beforeEach(() => {
    resetHarness();
    dbHandlers.select = () => ({
      from: () => ({ orderBy: async () => rows }),
    });
  });
  afterEach(() => resetHarness());

  function storedRow(contentHtml: string) {
    return {
      id: "row-1",
      externalId: "art-old",
      source: "outrank",
      locale: "en",
      slug: "old-post",
      title: "Old post",
      description: null,
      contentMarkdown: "",
      contentHtml,
      imageUrl: null,
      imageAlt: null,
      tags: [],
      publishedAt: new Date("2026-01-01T00:00:00.000Z"),
      createdAt: new Date("2026-01-01T00:00:00.000Z"),
      updatedAt: new Date("2026-01-01T00:00:00.000Z"),
    };
  }

  test("a stored <script>, <img onerror> and javascript: link do not render", async () => {
    rows = [storedRow(HOSTILE_HTML)];
    const post = await getBlogPost("old-post");
    assert.ok(post);
    assertNoScript(post.html);
    // Sanitised first, so the lazy-load rewrite (#919) still applies.
    assert.match(
      post.html,
      /<img src="https:\/\/cdnimg\.co\/a\/x\.png" alt="x" loading="lazy" decoding="async" \/>/,
    );
  });

  test("a normal stored article renders unchanged apart from the lazy images", async () => {
    rows = [storedRow(NORMAL_ARTICLE)];
    const post = await getBlogPost("old-post");
    assert.ok(post);
    assert.equal(
      post.html,
      NORMAL_ARTICLE.replace(
        'height="450" />',
        'height="450" loading="lazy" decoding="async" />',
      ),
    );
  });
});

test("JSON-LD escapes < so a title cannot close its script element", () => {
  const json = serializeJsonLd({ headline: "</script><script>alert(1)</script>" });
  assert.doesNotMatch(json, /</);
  assert.deepEqual(JSON.parse(json), {
    headline: "</script><script>alert(1)</script>",
  });
});

test("the blog post page serialises its JSON-LD through serializeJsonLd", async () => {
  const source = await readFile(
    new URL("../app/[locale]/blog/[slug]/page.tsx", import.meta.url),
    "utf8",
  );
  assert.match(source, /__html:\s*serializeJsonLd\(jsonLd\)/);
  assert.doesNotMatch(source, /__html:\s*JSON\.stringify/);
});
