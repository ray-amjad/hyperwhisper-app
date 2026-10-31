/**
 * Bounded request bodies (#2904).
 *
 * The Stripe webhook, `license/deactivate` and the Outrank blog webhook must
 * never buffer an unbounded body: each reads through `readBoundedText`, which
 * refuses a declared oversize `content-length` without reading and stops a
 * stream (413) as soon as its byte count passes the cap.
 *
 * Every probe body here is a counting `ReadableStream` with `highWaterMark: 0`,
 * so it is pulled only when someone reads it. That lets a test prove a body was
 * never touched, or was abandoned near the cap rather than drained.
 *
 * Mocks: the Stripe route harness installs the Stripe client, service and env
 * mocks. The blog route also needs the database and `next/cache` stand-ins
 * that `blog-webhook-harness.ts` installs, but that harness mocks the env
 * module too, and one process cannot mock a module twice, so the two blog
 * stand-ins are registered here and the blog token lives on the Stripe
 * harness's (mutable) fake env.
 */
import { after, afterEach, beforeEach, describe, it } from "node:test";
import assert from "node:assert/strict";
import { NextRequest, NextResponse } from "next/server";

import {
  behaviour,
  calls,
  checkoutEvent,
  fakeEnv,
  loadStripeWebhookRoute,
  resetHarness,
  restoreRouteLogging,
  silenceRouteLogging,
} from "./stripe-webhook-route-harness";
import { moduleMock, moduleUrl } from "./module-mock";
import {
  declaresLengthOver,
  readBoundedText,
} from "../src/lib/bounded-body";

const blogDbCalls: string[] = [];

moduleMock.module(moduleUrl("../src/db/index.ts"), {
  namedExports: {
    db: {
      select: () => {
        blogDbCalls.push("select");
        throw new Error("unexpected db.select in a bounded-body test");
      },
      insert: () => {
        blogDbCalls.push("insert");
        throw new Error("unexpected db.insert in a bounded-body test");
      },
    },
  },
});

moduleMock.module("next/cache", {
  namedExports: { revalidatePath: () => undefined },
});

const loadBlogRoute = () => import("@/app/api/webhooks/add-blog-post/route");
const loadDeactivateRoute = () =>
  import("@/app/api/license/deactivate/route");

const MB = 1024 * 1024;
const BLOG_TOKEN = ["blog", "bearer", "for", "bounded", "body"].join("-");
const STRIPE_SECRET = ["whsec", "bounded", "body", "test"].join("_");

interface Probe {
  stream: ReadableStream<Uint8Array>;
  /** Number of times the source was asked for a chunk. */
  pulls: number;
  /** Bytes handed out so far. */
  bytesSent: number;
  cancelled: boolean;
}

/**
 * A pull-only stream of `total` bytes in `chunkSize` pieces of ASCII "a".
 * `total: Infinity` never ends.
 */
function countingStream(total: number, chunkSize = 64 * 1024): Probe {
  const probe: Probe = {
    stream: undefined as unknown as ReadableStream<Uint8Array>,
    pulls: 0,
    bytesSent: 0,
    cancelled: false,
  };
  probe.stream = new ReadableStream<Uint8Array>(
    {
      pull(controller) {
        probe.pulls += 1;
        const left = total - probe.bytesSent;
        if (left <= 0) {
          controller.close();
          return;
        }
        const size = Math.min(chunkSize, left);
        probe.bytesSent += size;
        controller.enqueue(new Uint8Array(size).fill(0x61));
      },
      cancel() {
        probe.cancelled = true;
      },
    },
    { highWaterMark: 0 },
  );
  return probe;
}

/** A stream that records a pull and then throws: reading it is the failure. */
function untouchableStream(): Probe {
  const probe: Probe = {
    stream: undefined as unknown as ReadableStream<Uint8Array>,
    pulls: 0,
    bytesSent: 0,
    cancelled: false,
  };
  probe.stream = new ReadableStream<Uint8Array>(
    {
      pull() {
        probe.pulls += 1;
        throw new Error("the body was read");
      },
    },
    { highWaterMark: 0 },
  );
  return probe;
}

/** Fixed chunks, handed out one per pull. */
function chunkStream(chunks: Uint8Array[]): ReadableStream<Uint8Array> {
  let i = 0;
  return new ReadableStream<Uint8Array>(
    {
      pull(controller) {
        if (i < chunks.length) controller.enqueue(chunks[i++]);
        else controller.close();
      },
    },
    { highWaterMark: 0 },
  );
}

function streamRequest(
  url: string,
  body: ReadableStream<Uint8Array>,
  headers: Record<string, string> = {},
): NextRequest {
  return new NextRequest(url, {
    method: "POST",
    headers: { "content-type": "application/json", ...headers },
    body,
    duplex: "half",
  } as ConstructorParameters<typeof NextRequest>[1] & { duplex: "half" });
}

const STRIPE_URL = "https://hyperwhisper.test/api/webhooks/stripe";
const BLOG_URL = "https://hyperwhisper.test/api/webhooks/add-blog-post";
const DEACTIVATE_URL = "https://hyperwhisper.test/api/license/deactivate";

function plainRequest(body: BodyInit | null, headers: Record<string, string> = {}) {
  return new Request("https://hyperwhisper.test/x", {
    method: "POST",
    headers,
    body,
    duplex: "half",
  } as RequestInit & { duplex: "half" });
}

describe("readBoundedText", () => {
  it("refuses a declared content-length over the cap without reading", async () => {
    const probe = untouchableStream();
    const req = plainRequest(probe.stream, { "content-length": "101" });
    const result = await readBoundedText(req, 100);

    assert.ok(result instanceof NextResponse);
    assert.equal(result.status, 413);
    assert.deepEqual(await result.json(), { error: "Payload too large" });
    assert.equal(probe.pulls, 0);
  });

  it("accepts a body of exactly the cap, declared or streamed", async () => {
    const exact = "x".repeat(100);
    assert.equal(
      await readBoundedText(plainRequest(exact, { "content-length": "100" }), 100),
      exact,
    );

    const probe = countingStream(100, 7);
    assert.equal(await readBoundedText(plainRequest(probe.stream), 100), "a".repeat(100));
  });

  it("refuses one streamed byte over the cap", async () => {
    const probe = countingStream(101, 7);
    const result = await readBoundedText(plainRequest(probe.stream), 100);
    assert.ok(result instanceof NextResponse);
    assert.equal(result.status, 413);
  });

  it("counts bytes, not characters", async () => {
    // 40 euro signs: 40 characters, 120 UTF-8 bytes.
    const text = "€".repeat(40);
    const req = plainRequest(chunkStream([new TextEncoder().encode(text)]));
    const result = await readBoundedText(req, 100);
    assert.ok(result instanceof NextResponse, "120 bytes must not pass a 100-byte cap");
    assert.equal(result.status, 413);
  });

  it("decodes a multi-byte character split across chunks", async () => {
    const text = "aé€😀z";
    const bytes = new TextEncoder().encode(text);
    // One byte per chunk splits every multi-byte character.
    const chunks = Array.from(bytes, (b) => new Uint8Array([b]));
    const result = await readBoundedText(plainRequest(chunkStream(chunks)), 1024);
    assert.equal(result, text);
  });

  it("cancels the stream once the cap is passed, and stops pulling", async () => {
    const probe = countingStream(Infinity, 1024);
    const result = await readBoundedText(plainRequest(probe.stream), 10 * 1024);

    assert.ok(result instanceof NextResponse);
    assert.equal(result.status, 413);
    assert.equal(probe.cancelled, true);
    assert.ok(probe.bytesSent <= 11 * 1024, `pulled ${probe.bytesSent} bytes`);
  });

  it("ignores a malformed or negative content-length and still caps the stream", async () => {
    for (const declared of ["abc", "-5", "1e9", "12.5", ""]) {
      const req = plainRequest(new Uint8Array(0), {});
      req.headers.set("content-length", declared);
      assert.equal(declaresLengthOver(req, 100), false, `declared ${JSON.stringify(declared)}`);
    }

    const malformed = plainRequest("hello", {});
    malformed.headers.set("content-length", "-5");
    assert.equal(await readBoundedText(malformed, 100), "hello");

    const lying = plainRequest(countingStream(1000, 100).stream, {});
    lying.headers.set("content-length", "-1");
    const result = await readBoundedText(lying, 100);
    assert.ok(result instanceof NextResponse);
    assert.equal(result.status, 413);
  });

  it("returns an empty string for a request with no body", async () => {
    assert.equal(await readBoundedText(plainRequest(null), 100), "");
  });

  it("rejects when the stream fails part-way, as req.text() would", async () => {
    const failing = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(new Uint8Array([0x61]));
        controller.error(new Error("connection reset"));
      },
    });
    await assert.rejects(readBoundedText(plainRequest(failing), 100), /connection reset/);
  });
});

describe("POST /api/webhooks/stripe body bounds", () => {
  beforeEach(() => {
    resetHarness();
    silenceRouteLogging();
    fakeEnv.STRIPE_WEBHOOK_SECRET = STRIPE_SECRET;
  });
  afterEach(() => restoreRouteLogging());

  it("answers 400 for no signature without pulling the body", async () => {
    const { POST } = await loadStripeWebhookRoute();
    const probe = untouchableStream();

    const res = await POST(streamRequest(STRIPE_URL, probe.stream));

    assert.equal(res.status, 400);
    assert.deepEqual(await res.json(), { error: "No signature" });
    assert.equal(probe.pulls, 0, "the body of an unsigned request was read");
    assert.equal(calls.constructEvent.length, 0);
  });

  it("answers 413 for a 2 MB body with a signature and no content-length", async () => {
    const { POST } = await loadStripeWebhookRoute();
    const probe = countingStream(2 * MB);
    const req = streamRequest(STRIPE_URL, probe.stream, {
      "stripe-signature": "t=1700000000,v1=abc123",
    });
    assert.equal(req.headers.get("content-length"), null);

    const res = await POST(req);

    assert.equal(res.status, 413);
    assert.equal(calls.constructEvent.length, 0, "an oversize body reached the verifier");
    // Abandoned just past the 1 MB cap, not drained to 2 MB.
    assert.ok(probe.bytesSent <= MB + 64 * 1024, `pulled ${probe.bytesSent} bytes`);
    assert.equal(probe.cancelled, true);
  });

  it("answers 413 for a declared content-length over 1 MB without reading", async () => {
    const { POST } = await loadStripeWebhookRoute();
    const probe = untouchableStream();
    const res = await POST(
      streamRequest(STRIPE_URL, probe.stream, {
        "stripe-signature": "t=1700000000,v1=abc123",
        "content-length": String(MB + 1),
      }),
    );
    assert.equal(res.status, 413);
    assert.equal(probe.pulls, 0);
    assert.equal(calls.constructEvent.length, 0);
  });

  it("still hands a body under the cap to the verifier byte-for-byte", async () => {
    const { POST } = await loadStripeWebhookRoute();
    behaviour.verifiedEvent = checkoutEvent({ purchaseType: "other" });
    const raw = JSON.stringify({ id: "evt_1", note: "naïve €" }) + " ".repeat(2000);
    const bytes = new TextEncoder().encode(raw);
    const chunks = [bytes.slice(0, 23), bytes.slice(23, 24), bytes.slice(24)];

    const res = await POST(
      streamRequest(STRIPE_URL, chunkStream(chunks), {
        "stripe-signature": "t=1700000000,v1=abc123",
      }),
    );

    assert.equal(res.status, 200);
    assert.equal(calls.constructEvent.length, 1);
    assert.equal(calls.constructEvent[0].body, raw);
  });
});

describe("POST /api/webhooks/add-blog-post body bounds", () => {
  beforeEach(() => {
    resetHarness();
    blogDbCalls.length = 0;
    fakeEnv.OUTRANK_WEBHOOK_TOKEN = BLOG_TOKEN;
  });
  after(() => {
    delete fakeEnv.OUTRANK_WEBHOOK_TOKEN;
  });

  it("answers 413 for a 6 MB chunked body with no content-length", async () => {
    const { POST } = await loadBlogRoute();
    const probe = countingStream(6 * MB);
    const req = streamRequest(BLOG_URL, probe.stream, {
      authorization: `Bearer ${BLOG_TOKEN}`,
    });
    assert.equal(req.headers.get("content-length"), null);

    const res = await POST(req);

    assert.equal(res.status, 413);
    assert.deepEqual(await res.json(), { error: "Payload too large" });
    assert.ok(probe.bytesSent <= 5 * MB + 64 * 1024, `pulled ${probe.bytesSent} bytes`);
    assert.equal(probe.cancelled, true);
    assert.deepEqual(blogDbCalls, []);
  });

  it("keeps the auth order: a declared oversize body is 413 before auth", async () => {
    const { POST } = await loadBlogRoute();
    const probe = untouchableStream();
    const res = await POST(
      streamRequest(BLOG_URL, probe.stream, {
        authorization: "Bearer wrong",
        "content-length": String(6 * MB),
      }),
    );
    assert.equal(res.status, 413);
    assert.equal(probe.pulls, 0);
  });

  it("keeps the auth order: a bad bearer is 401 before the body is read", async () => {
    const { POST } = await loadBlogRoute();
    const probe = untouchableStream();
    const res = await POST(
      streamRequest(BLOG_URL, probe.stream, { authorization: "Bearer wrong" }),
    );
    assert.equal(res.status, 401);
    assert.equal(probe.pulls, 0);
  });
});

describe("POST /api/license/deactivate body bounds", () => {
  const silenced = { error: console.error, warn: console.warn };
  beforeEach(() => {
    console.error = () => undefined;
    console.warn = () => undefined;
  });
  afterEach(() => {
    console.error = silenced.error;
    console.warn = silenced.warn;
  });

  function deactivateBody(totalBytes: number): string {
    const json = JSON.stringify({ license_key: "HW-TEST" });
    return json + " ".repeat(totalBytes - json.length);
  }

  it("answers 413 for a streamed body over 16 KB", async () => {
    const { POST } = await loadDeactivateRoute();
    const probe = countingStream(Infinity, 1024);
    const res = await POST(streamRequest(DEACTIVATE_URL, probe.stream));
    assert.equal(res.status, 413);
    assert.equal(probe.cancelled, true);
    assert.ok(probe.bytesSent <= 17 * 1024, `pulled ${probe.bytesSent} bytes`);
  });

  it("answers 413 for a valid JSON body one byte over 16 KB", async () => {
    const { POST } = await loadDeactivateRoute();
    const body = new TextEncoder().encode(deactivateBody(16 * 1024 + 1));
    const res = await POST(streamRequest(DEACTIVATE_URL, chunkStream([body])));
    assert.equal(res.status, 413);
  });

  it("accepts a valid JSON body of exactly 16 KB", async () => {
    const { POST } = await loadDeactivateRoute();
    const body = new TextEncoder().encode(deactivateBody(16 * 1024));
    const res = await POST(streamRequest(DEACTIVATE_URL, chunkStream([body])));
    assert.equal(res.status, 200);
    assert.equal((await res.json()).success, true);
  });

  it("keeps the 400 for a body that is not JSON", async () => {
    const { POST } = await loadDeactivateRoute();
    const body = new TextEncoder().encode("{not json");
    const res = await POST(streamRequest(DEACTIVATE_URL, chunkStream([body])));
    assert.equal(res.status, 400);
    assert.deepEqual(await res.json(), {
      success: false,
      error: "Invalid request body",
    });
  });
});
