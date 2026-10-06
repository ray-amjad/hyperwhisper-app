/**
 * Per-article validation in the Outrank blog webhook (issue #708).
 *
 * The route used to cast the untrusted `data.articles` elements to
 * `OutrankArticle[]` and call `.trim()` on whatever came in. A numeric title,
 * markdown, HTML or slug threw a TypeError outside the per-article try, so one
 * bad article rejected the whole delivery. These tests pin the fix: a
 * malformed article lands in `skippedDetails` as a permanent skip, makes no
 * database call, and does not stop a later valid article. They also pin that a
 * valid delivery's reply and row are unchanged, and that a database failure
 * still answers 500 so the sender retries.
 */
import assert from "node:assert/strict";
import { inspect } from "node:util";
import {
  after,
  afterEach,
  before,
  beforeEach,
  describe,
  test,
} from "node:test";

import {
  captureRouteErrors,
  dbCalls,
  dbHandlers,
  errorCalls,
  loadBlogWebhookRoute,
  postRaw,
  resetHarness,
  restoreRouteErrors,
  revalidatedPaths,
} from "./blog-webhook-harness";

type Route = Awaited<ReturnType<typeof loadBlogWebhookRoute>>;
let route: Route;

/** Distinctive article copy, so a leak into a log or a reply is unmistakable. */
const CONTENT_MARKER = "Quokka-article-copy-5d1e";

/** The values each `db.insert(...).values(...)` call received. */
let insertedValues: Array<Record<string, unknown>> = [];

/**
 * A database that resolves every slug as free and returns one row per upsert.
 * `failInsert` makes the upsert reject, the way a DB outage does.
 */
function fakeDatabase(options: { failInsert?: boolean } = {}): void {
  dbHandlers.select = () => ({
    from: () => ({
      where: () => ({ limit: async () => [] }),
    }),
  });
  dbHandlers.insert = () => ({
    values: (values: Record<string, unknown>) => {
      insertedValues.push(values);

      return {
        onConflictDoUpdate: () => ({
          returning: async () => {
            if (options.failInsert) {
              throw new Error("connection terminated unexpectedly");
            }

            return [
              {
                id: `row-${insertedValues.length}`,
                slug: values.slug,
              },
            ];
          },
        }),
      };
    },
  });
}

function delivery(articles: unknown[]): string {
  return JSON.stringify({ event_type: "publish_articles", data: { articles } });
}

function validArticle(id = "art-1", title = "Hello World") {
  return {
    id,
    title,
    content_markdown: `# Heading\n\n${CONTENT_MARKER} body`,
    meta_description: "  A short summary  ",
    image_url: "https://cdn.example/hero.png",
    image_alt: "Hero",
    published_at: "2026-09-01T10:00:00.000Z",
    tags: [" speech ", 7, "", "dictation"],
  };
}

async function post(articles: unknown[]) {
  const response = await route.POST(postRaw(delivery(articles)));
  return { status: response.status, body: await response.json() };
}

function assertNothingLeaked(): void {
  for (const call of errorCalls) {
    const line = call
      .map((a) => (typeof a === "string" ? a : inspect(a, { depth: 6 })))
      .join(" ");
    assert.ok(!line.includes(CONTENT_MARKER), "article content was logged");
  }
}

describe("POST /api/webhooks/add-blog-post per-article validation", () => {
  before(async () => {
    route = await loadBlogWebhookRoute();
    captureRouteErrors();
  });
  after(() => restoreRouteErrors());
  beforeEach(() => {
    resetHarness();
    insertedValues = [];
  });
  afterEach(() => resetHarness());

  test("a valid article is stored and answered exactly as before", async () => {
    fakeDatabase();
    const { status, body } = await post([validArticle()]);

    assert.equal(status, 200);
    assert.deepEqual(body, {
      message: "Webhook processed successfully",
      processed: 1,
      skipped: 0,
      articles: [{ id: "row-1", slug: "hello-world" }],
    });
    assert.equal(insertedValues.length, 1);
    assert.deepEqual(insertedValues[0], {
      externalId: "art-1",
      source: "outrank",
      locale: "en",
      slug: "hello-world",
      title: "Hello World",
      description: "A short summary",
      contentMarkdown: `# Heading\n\n${CONTENT_MARKER} body`,
      contentHtml: `<h1>Heading</h1>\n<p>${CONTENT_MARKER} body</p>`,
      imageUrl: "https://cdn.example/hero.png",
      imageAlt: "Hero",
      tags: ["speech", "dictation"],
      publishedAt: new Date("2026-09-01T10:00:00.000Z"),
    });
    assert.deepEqual(revalidatedPaths, [
      "/en/blog/hello-world",
      "/en/blog",
      "/sitemap.xml",
    ]);
    assert.deepEqual(errorCalls, []);
  });

  for (const field of [
    "title",
    "content_markdown",
    "content_html",
    "slug",
    "id",
  ] as const) {
    test(`a numeric ${field} is a permanent skip with no database call`, async () => {
      const { status, body } = await post([
        { ...validArticle(), [field]: 123 },
      ]);

      assert.equal(status, 200);
      assert.deepEqual(body, {
        message: "Webhook processed successfully",
        processed: 0,
        skipped: 1,
        articles: [],
        skippedDetails: [
          { index: 0, reason: `invalid ${field}: expected a string` },
        ],
      });
      assert.deepEqual(dbCalls, []);
      assertNothingLeaked();
    });
  }

  test("a non-string date is a permanent skip, like an unparsable one", async () => {
    const { status, body } = await post([
      { ...validArticle("a"), published_at: 1_700_000_000_000 },
      { ...validArticle("b"), published_at: undefined, created_at: {} },
      { ...validArticle("c"), published_at: "not a date" },
    ]);

    assert.equal(status, 200);
    assert.deepEqual(body.skippedDetails, [
      { index: 0, reason: "invalid published_at: expected a string" },
      { index: 1, reason: "invalid created_at: expected a string" },
      { index: 2, reason: "invalid published_at: not a date" },
    ]);
    assert.equal(body.processed, 0);
    assert.deepEqual(dbCalls, []);
  });

  test("null and primitive elements are permanent skips", async () => {
    const { status, body } = await post([null, 5, "article", true, [], {}]);

    assert.equal(status, 200);
    assert.deepEqual(body, {
      message: "Webhook processed successfully",
      processed: 0,
      skipped: 6,
      articles: [],
      skippedDetails: [
        { index: 0, reason: "article is not an object" },
        { index: 1, reason: "article is not an object" },
        { index: 2, reason: "article is not an object" },
        { index: 3, reason: "article is not an object" },
        { index: 4, reason: "article is not an object" },
        { index: 5, reason: "missing id/title/content" },
      ],
    });
    assert.deepEqual(dbCalls, []);
  });

  test("malformed optional fields are ignored, as an absent field is", async () => {
    fakeDatabase();
    const { status, body } = await post([
      {
        id: "art-2",
        title: "Optional Fields",
        content_html: "<p>kept</p>",
        meta_description: 42,
        description: " Fallback summary ",
        image_url: { href: "x" },
        image_alt: 7,
        tags: "not-a-list",
        url: 99,
      },
    ]);

    assert.equal(status, 200);
    assert.equal(body.processed, 1);
    assert.equal(insertedValues.length, 1);
    const row = insertedValues[0];
    assert.equal(row.description, "Fallback summary");
    assert.equal(row.imageUrl, null);
    assert.equal(row.imageAlt, "Optional Fields");
    assert.deepEqual(row.tags, []);
    assert.equal(row.contentHtml, "<p>kept</p>");
    assert.ok(!("publishedAt" in row), "no date supplied, no date written");
  });

  test("null fields still mean absent", async () => {
    fakeDatabase();
    const { status, body } = await post([
      {
        ...validArticle(),
        slug: null,
        published_at: null,
        created_at: "2026-08-01T00:00:00.000Z",
        meta_description: null,
        description: "From description",
        image_url: null,
        tags: null,
      },
    ]);

    assert.equal(status, 200);
    assert.equal(body.processed, 1);
    const row = insertedValues[0];
    assert.equal(row.slug, "hello-world");
    assert.deepEqual(row.publishedAt, new Date("2026-08-01T00:00:00.000Z"));
    assert.equal(row.description, "From description");
    assert.equal(row.imageUrl, null);
    assert.deepEqual(row.tags, []);
  });

  test("an invalid article does not stop a later valid one", async () => {
    fakeDatabase();
    const { status, body } = await post([
      { ...validArticle("bad"), title: 123 },
      null,
      validArticle("good", "Second Post"),
    ]);

    assert.equal(status, 200);
    assert.deepEqual(body, {
      message: "Webhook processed successfully",
      processed: 1,
      skipped: 2,
      articles: [{ id: "row-1", slug: "second-post" }],
      skippedDetails: [
        { index: 0, reason: "invalid title: expected a string" },
        { index: 1, reason: "article is not an object" },
      ],
    });
    // Only the valid article reached the database.
    assert.equal(insertedValues.length, 1);
    assert.equal(insertedValues[0].externalId, "good");
    assert.deepEqual(dbCalls, ["select", "insert"]);
    assertNothingLeaked();
  });

  test("a database failure still answers 500 so the sender retries", async () => {
    fakeDatabase({ failInsert: true });
    const { status, body } = await post([
      { ...validArticle("bad"), slug: 9 },
      validArticle("good"),
    ]);

    assert.equal(status, 500);
    assert.deepEqual(body, {
      message: "Webhook failed: no articles were persisted",
      processed: 0,
      skipped: 2,
      skippedDetails: [
        { index: 0, reason: "invalid slug: expected a string" },
        { index: 1, reason: "connection terminated unexpectedly" },
      ],
    });
    assert.equal(errorCalls.length, 1);
    assert.equal(errorCalls[0][0], "[add-blog-post] upsert failed");
    assert.equal(
      (errorCalls[0][1] as Record<string, unknown>).externalId,
      "good",
    );
    assertNothingLeaked();
  });

  test("only invalid articles never ask for a retry", async () => {
    const { status, body } = await post([
      { ...validArticle(), content_markdown: 1 },
      { ...validArticle(), content_html: false },
    ]);

    assert.equal(status, 200);
    assert.equal(body.processed, 0);
    assert.equal(body.skipped, 2);
    assert.deepEqual(dbCalls, []);
  });
});
