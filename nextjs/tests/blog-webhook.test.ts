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
  errorCalls,
  loadBlogWebhookRoute,
  postBrokenStream,
  postRaw,
  resetHarness,
  restoreRouteErrors,
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

  test("an unauthorized delivery is still refused before either log line", async () => {
    const request = postRaw("not json");
    request.headers.set("authorization", "Bearer wrong");
    const response = await route.POST(request);

    assert.equal(response.status, 401);
    assert.deepEqual(errorCalls, []);
  });
});
