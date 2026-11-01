/**
 * The Outrank blog webhook's two body-level rejections (issue #718).
 *
 * An unreadable body and a body that is not JSON both answer HTTP 400. The
 * website has no error reporter, so the `console.error` on each path is the
 * only trace a rejected delivery leaves. These tests pin three things per
 * path: the reply is unchanged, exactly one `[add-blog-post]` line is logged
 * with the content type, and neither the body nor the bearer value reaches the
 * log. The read path keeps the stream error whole. The parse path logs the
 * byte count and the error's name only: V8's SyntaxError message quotes the
 * start of the input, so the parse error itself must never be logged.
 *
 * The second suite pins the seven refusals before any article is read
 * (issue #3003): one `[add-blog-post] delivery rejected` warn line each.
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
  OUTRANK_BEARER,
  captureRouteErrors,
  dbCalls,
  dbHandlers,
  errorCalls,
  fakeEnv,
  loadBlogWebhookRoute,
  postBrokenStream,
  postRaw,
  resetHarness,
  restoreRouteErrors,
  revalidatedPaths,
  warnCalls,
} from "./blog-webhook-harness";

type Route = Awaited<ReturnType<typeof loadBlogWebhookRoute>>;
let route: Route;

/** Distinctive marketing copy, so a leak into the log is unmistakable. */
const BODY_MARKER = "Zanzibar-article-copy-7f3a";

function rendered(args: unknown[]): string {
  return args
    .map((a) => (typeof a === "string" ? a : inspect(a, { depth: 6 })))
    .join(" ");
}

const REJECTED = "[add-blog-post] delivery rejected";

/** Values a rejection line must never carry, beside the body marker. */
const secrets: string[] = [];

/** Every console.error and console.warn argument the route logged, rendered. */
function everyLoggedArgument(): string {
  return [...errorCalls, ...warnCalls].map(rendered).join("\n");
}

function assertNothingLeaked(): void {
  const logged = everyLoggedArgument();
  assert.ok(!logged.includes(BODY_MARKER), "body text must not be logged");
  assert.ok(
    !logged.includes(OUTRANK_BEARER),
    "bearer value must not be logged",
  );
  for (const secret of secrets) {
    assert.ok(!logged.includes(secret), `"${secret}" must not be logged`);
  }
}

/**
 * Exactly one console.warn, and it is the rejection line with this reason and
 * status. Returns its fields.
 */
function onlyRejection(
  reason: string,
  status: number,
): Record<string, unknown> {
  assert.equal(
    warnCalls.length,
    1,
    `expected exactly one console.warn, got ${warnCalls.length}`,
  );
  const [message, detail, ...rest] = warnCalls[0];
  assert.equal(message, REJECTED);
  assert.deepEqual(rest, []);
  const fields = detail as Record<string, unknown>;
  assert.equal(fields.reason, reason);
  assert.equal(fields.status, status);
  return fields;
}

function onlyErrorCall(): unknown[] {
  assert.equal(
    errorCalls.length,
    1,
    `expected exactly one console.error, got ${errorCalls.length}`,
  );
  return errorCalls[0];
}

describe("POST /api/webhooks/add-blog-post body rejections", () => {
  before(async () => {
    route = await loadBlogWebhookRoute();
    captureRouteErrors();
  });
  after(() => restoreRouteErrors());
  beforeEach(() => resetHarness());
  afterEach(() => resetHarness());

  test("an unreadable body still answers 400 Invalid body and logs it", async () => {
    const failure = new Error("stream aborted mid-delivery");
    const response = await route.POST(
      postBrokenStream(
        `{"title":"${BODY_MARKER}`,
        failure,
        "application/json; charset=utf-8",
      ),
    );

    assert.equal(response.status, 400);
    assert.deepEqual(await response.json(), { error: "Invalid body" });

    const [message, detail] = onlyErrorCall();
    assert.equal(message, "[add-blog-post] could not read request body");
    const fields = detail as Record<string, unknown>;
    assert.equal(fields.contentType, "application/json; charset=utf-8");
    // The error object is passed whole, not flattened to its message.
    assert.ok(fields.err instanceof Error, "err is kept as an Error");
    assert.match(inspect(fields.err), /stream aborted mid-delivery/);

    const line = rendered(errorCalls[0]);
    assert.ok(!line.includes(BODY_MARKER), "body text must not be logged");
    assert.ok(
      !line.includes(OUTRANK_BEARER),
      "bearer value must not be logged",
    );
    assert.deepEqual(dbCalls, []);
  });

  test("a body that is not JSON still answers 400 Invalid JSON and logs it", async () => {
    // Short enough that V8 quotes all of it in the SyntaxError message
    // (`Unexpected token 'Z', "Zébu—q9" is not valid JSON`), and multi-byte so
    // the UTF-8 byte count differs from the UTF-16 length.
    const body = "Zébu—q9";
    assert.throws(
      () => JSON.parse(body),
      (e: unknown) => e instanceof SyntaxError && e.message.includes(body),
      "precondition: the parser message quotes this body in full",
    );
    const response = await route.POST(postRaw(body, "text/html"));

    assert.equal(response.status, 400);
    assert.deepEqual(await response.json(), { error: "Invalid JSON" });

    const [message, detail] = onlyErrorCall();
    assert.equal(message, "[add-blog-post] request body is not valid JSON");
    const fields = detail as Record<string, unknown>;
    assert.equal(fields.contentType, "text/html");
    // UTF-8 bytes, not UTF-16 code units: the dash and the accent are multi-byte.
    assert.equal(fields.bodyBytes, Buffer.byteLength(body, "utf8"));
    assert.notEqual(fields.bodyBytes, body.length);
    assert.equal(fields.errorName, "SyntaxError");
    assert.ok(!("err" in fields), "the parse error object must not be logged");

    // inspect() renders an Error's message, so a quoted body would show here.
    const line = rendered(errorCalls[0]);
    for (let len = 4; len <= body.length; len++) {
      for (let at = 0; at + len <= body.length; at++) {
        const piece = body.slice(at, at + len);
        assert.ok(
          !line.includes(piece),
          `body text must not be logged (found "${piece}")`,
        );
      }
    }
    assert.ok(
      !line.includes(OUTRANK_BEARER),
      "bearer value must not be logged",
    );
    assert.deepEqual(dbCalls, []);
  });

  test("an unauthorized delivery is refused before either #718 line, with one rejection line", async () => {
    const request = postRaw("not json");
    request.headers.set("authorization", "Bearer wrong");
    const response = await route.POST(request);

    assert.equal(response.status, 401);
    assert.deepEqual(errorCalls, []);
    const fields = onlyRejection("bearer_refused", 401);
    assert.equal(fields.hasAuthorizationHeader, true);
    assertNothingLeaked();
  });
});

/**
 * The seven refusals before any article is read (#3003). Each one keeps its
 * reply and writes exactly ONE `[add-blog-post] delivery rejected` warn line
 * with a fixed reason and safe fields only: never the authorization header,
 * the signature, the token, the body or any article text.
 */
describe("POST /api/webhooks/add-blog-post refused deliveries", () => {
  const WRONG_BEARER = ["wrong", "bearer", "3003"].join("-");
  const SIGNATURE = ["sig", "value", "3003"].join("-");
  const SIGNING_SECRET = ["signing", "secret", "3003"].join("-");

  before(async () => {
    route = await loadBlogWebhookRoute();
    captureRouteErrors();
    secrets.push(WRONG_BEARER, SIGNATURE, SIGNING_SECRET);
  });
  after(() => {
    restoreRouteErrors();
    secrets.length = 0;
  });
  beforeEach(() => resetHarness());
  afterEach(() => resetHarness());

  function article(id: string) {
    return {
      id,
      title: `${BODY_MARKER} title ${id}`,
      content_html: `<p>${BODY_MARKER} html</p>`,
    };
  }

  function delivery(payload: unknown): string {
    return JSON.stringify(payload);
  }

  function publish(articles: unknown): string {
    return delivery({ event_type: "publish_articles", data: { articles } });
  }

  async function refuse(
    request: Request,
    status: number,
    reply: unknown,
    reason: string,
  ): Promise<Record<string, unknown>> {
    const response = await route.POST(request as Parameters<Route["POST"]>[0]);

    assert.equal(response.status, status);
    assert.deepEqual(await response.json(), reply);
    assert.deepEqual(errorCalls, [], "no #718 line on this path");
    const fields = onlyRejection(reason, status);
    assertNothingLeaked();
    assert.deepEqual(dbCalls, []);
    return fields;
  }

  test("a declared oversize body: 413, reason declared_too_large", async () => {
    const request = postRaw(publish([article("a1")]));
    request.headers.set("content-length", String(5 * 1024 * 1024 + 1));
    const fields = await refuse(
      request,
      413,
      { error: "Payload too large" },
      "declared_too_large",
    );

    assert.equal(fields.contentLength, String(5 * 1024 * 1024 + 1));
    assert.equal(fields.contentType, "application/json");
    assert.equal(fields.signingEnabled, false);
    assert.equal(fields.hasAuthorizationHeader, true);
    assert.equal(fields.hasSignatureHeader, false);
  });

  test("a wrong bearer: 401, reason bearer_refused", async () => {
    const request = postRaw(publish([article("a1")]));
    request.headers.set("authorization", `Bearer ${WRONG_BEARER}`);
    const fields = await refuse(
      request,
      401,
      { error: "Invalid access token" },
      "bearer_refused",
    );

    assert.equal(fields.hasAuthorizationHeader, true);
    assert.equal(fields.signingEnabled, false);
  });

  test("no bearer at all: 401, reason bearer_refused", async () => {
    const request = postRaw(publish([article("a1")]));
    request.headers.delete("authorization");
    const fields = await refuse(
      request,
      401,
      { error: "Invalid access token" },
      "bearer_refused",
    );

    assert.equal(fields.hasAuthorizationHeader, false);
  });

  test("a streamed oversize body: 413, reason streamed_too_large", async () => {
    const body = publish([article("a1")]).padEnd(5 * 1024 * 1024 + 1, " ");
    const request = postRaw(body);
    assert.equal(
      request.headers.get("content-length"),
      null,
      "precondition: no declared length, so the streaming cap refuses it",
    );
    const fields = await refuse(
      request,
      413,
      { error: "Payload too large" },
      "streamed_too_large",
    );

    assert.equal(fields.contentLength, null);
  });

  test("signing on, bad signature and bad bearer: 401, reason signature_refused", async () => {
    fakeEnv.OUTRANK_WEBHOOK_SIGNING_SECRET = SIGNING_SECRET;
    const request = postRaw(publish([article("a1")]));
    request.headers.set("authorization", `Bearer ${WRONG_BEARER}`);
    request.headers.set("x-outrank-signature", SIGNATURE);
    const fields = await refuse(
      request,
      401,
      { error: "Invalid access token" },
      "signature_refused",
    );

    assert.equal(fields.signingEnabled, true);
    assert.equal(fields.hasSignatureHeader, true);
    assert.equal(fields.hasAuthorizationHeader, true);
  });

  test("an unsupported event_type: 400, reason unsupported_event_type, cut to 40 chars", async () => {
    const eventType = `publish_drafts_${"x".repeat(80)}`;
    const fields = await refuse(
      postRaw(
        delivery({
          event_type: eventType,
          data: { articles: [article("a1")] },
        }),
      ),
      400,
      { error: `Unsupported event_type: ${eventType}` },
      "unsupported_event_type",
    );

    assert.equal(fields.eventType, eventType.slice(0, 40));
  });

  test("a missing event_type: 400, logged as the reply names it", async () => {
    const fields = await refuse(
      postRaw(delivery({ data: { articles: [article("a1")] } })),
      400,
      { error: "Unsupported event_type: undefined" },
      "unsupported_event_type",
    );

    assert.equal(fields.eventType, "undefined");
  });

  test("an empty articles array: 400, reason no_articles, count 0", async () => {
    const fields = await refuse(
      postRaw(publish([])),
      400,
      { error: "No articles in payload" },
      "no_articles",
    );

    assert.equal(fields.articleCount, 0);
  });

  test("no articles array at all: 400, reason no_articles, count null", async () => {
    const fields = await refuse(
      postRaw(
        delivery({
          event_type: "publish_articles",
          data: { note: BODY_MARKER },
        }),
      ),
      400,
      { error: "No articles in payload" },
      "no_articles",
    );

    assert.equal(fields.articleCount, null);
  });

  test("more than 100 articles: 413, reason too_many_articles, count kept", async () => {
    const articles = Array.from({ length: 101 }, (_, i) => article(`a${i}`));
    const fields = await refuse(
      postRaw(publish(articles)),
      413,
      { error: "Too many articles: 101 (max 100)" },
      "too_many_articles",
    );

    assert.equal(fields.articleCount, 101);
  });

  test("a delivery that is stored logs no rejection line", async () => {
    dbHandlers.select = () => ({
      from: () => ({ where: () => ({ limit: async () => [] }) }),
    });
    dbHandlers.insert = () => ({
      values: () => ({
        onConflictDoUpdate: () => ({
          returning: async () => [{ id: "row-1", slug: "stored-post" }],
        }),
      }),
    });
    const response = await route.POST(postRaw(publish([article("a1")])));

    assert.equal(response.status, 200);
    assert.equal((await response.json()).processed, 1);
    assert.deepEqual(warnCalls, []);
    assert.deepEqual(errorCalls, []);
    assert.ok(revalidatedPaths.includes("/en/blog"));
  });
});
