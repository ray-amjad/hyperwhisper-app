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
 *
 * The rule for every other input: an article main's route stored is stored
 * with the same column values. The parity block at the end pins that, and it
 * passes against main's route too, for every case main did not throw on.
 */
import assert from "node:assert/strict";
import { createRequire } from "node:module";
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

/**
 * node-postgres's own conversion of a bound parameter to the text it sends.
 * main's route handed raw JSON values to the driver, so this is the column
 * value main stored, and comparing through it compares stored rows.
 */
const { prepareValue } = createRequire(import.meta.url)("pg/lib/utils.js") as {
  prepareValue: (value: unknown) => unknown;
};

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

  test("a date that does not parse is a permanent skip", async () => {
    const { status, body } = await post([
      { ...validArticle("b"), published_at: undefined, created_at: {} },
      { ...validArticle("c"), published_at: "not a date" },
    ]);

    assert.equal(status, 200);
    // These reasons are main's, unchanged: parsePublishedAt echoes the value.
    assert.deepEqual(body.skippedDetails, [
      { index: 0, reason: "invalid created_at: [object Object]" },
      { index: 1, reason: "invalid published_at: not a date" },
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

  test("a malformed description is ignored, the others keep main's text", async () => {
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
    // main trimmed meta_description inside the upsert try, so 42 threw there.
    assert.equal(row.description, "Fallback summary");
    // main handed these to the driver as is, which wrote this text.
    assert.equal(row.imageUrl, '{"href":"x"}');
    assert.equal(row.imageAlt, "7");
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

  describe("parity with main's route for every input main stored", () => {
    /** The row as Postgres receives it: every value through the driver. */
    function storedRow(values: Record<string, unknown>) {
      return Object.fromEntries(
        Object.entries(values).map(([column, value]) => [
          column,
          value instanceof Date ? value.toISOString() : prepareValue(value),
        ]),
      );
    }

    const base = {
      title: "Parity Post",
      content_html: "<p>parity</p>",
    };

    const storedCases: Array<{
      name: string;
      article: Record<string, unknown>;
      expected: Record<string, unknown>;
    }> = [
      {
        name: "a numeric id",
        article: { ...base, id: 12345 },
        expected: { externalId: "12345" },
      },
      {
        name: "a boolean id",
        article: { ...base, id: true },
        expected: { externalId: "true" },
      },
      {
        name: "an object id",
        article: { ...base, id: { n: 1 } },
        expected: { externalId: '{"n":1}' },
      },
      {
        name: "an epoch published_at",
        article: { ...base, id: "p1", published_at: 1_700_000_000_000 },
        expected: { publishedAt: "2023-11-14T22:13:20.000Z" },
      },
      {
        name: "an epoch created_at",
        article: { ...base, id: "p2", created_at: 1_700_000_000_000 },
        expected: { publishedAt: "2023-11-14T22:13:20.000Z" },
      },
      {
        name: "a valid published_at with a malformed created_at",
        article: {
          ...base,
          id: "p3",
          published_at: "2026-09-01T10:00:00.000Z",
          created_at: { bad: true },
        },
        expected: { publishedAt: "2026-09-01T10:00:00.000Z" },
      },
      {
        name: "a numeric image_url and a numeric image_alt",
        article: { ...base, id: "p4", image_url: 5, image_alt: 0 },
        expected: { imageUrl: "5", imageAlt: "0" },
      },
      {
        name: "an array image_url",
        article: { ...base, id: "p5", image_url: ["a", 1, null, 'q"\\'] },
        expected: { imageUrl: '{"a","1",NULL,"q\\"\\\\"}' },
      },
      {
        name: "an empty image_alt",
        article: { ...base, id: "p6", image_alt: "" },
        expected: { imageAlt: "" },
      },
      {
        name: "a non-string description behind a valid meta_description",
        article: { ...base, id: "p7", meta_description: " Kept ", description: 3 },
        expected: { description: "Kept" },
      },
    ];

    for (const { name, article, expected } of storedCases) {
      test(`${name} is stored as main stored it`, async () => {
        fakeDatabase();
        const { status, body } = await post([article]);

        assert.equal(status, 200);
        assert.equal(body.processed, 1);
        const row = storedRow(insertedValues[0]);
        for (const [column, value] of Object.entries(expected)) {
          assert.deepEqual(row[column], value, column);
        }
      });
    }

    test("a falsy id is still skipped as missing", async () => {
      const { status, body } = await post([
        { ...base, id: 0 },
        { ...base, id: false },
        { ...base, id: "" },
      ]);

      assert.equal(status, 200);
      assert.equal(body.processed, 0);
      assert.deepEqual(
        body.skippedDetails.map((s: { reason: string }) => s.reason),
        Array(3).fill("missing id/title/content"),
      );
      assert.deepEqual(dbCalls, []);
    });

    test("the route hands the driver the text node-postgres would write", async () => {
      fakeDatabase();
      const values: unknown[] = [
        7,
        -0.5,
        1e21,
        true,
        false,
        "plain",
        { a: [1, { b: null }] },
        [],
        [[1, 2], ["x"]],
        ['back\\slash', 'quote"', { o: 1 }, true],
      ];
      const { body } = await post(
        values.map((imageUrl, i) => ({ ...base, id: `v${i}`, image_url: imageUrl })),
      );

      assert.equal(body.processed, values.length);
      values.forEach((value, i) => {
        assert.equal(insertedValues[i].imageUrl, prepareValue(value), String(i));
      });
    });
  });
});
