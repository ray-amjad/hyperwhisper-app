/**
 * Bounded request bodies on the licence and credit-checkout routes (#3039).
 *
 * `license/activate`, `license/validate`, `license/credits` (POST) and
 * `checkout/credits` are public and anonymous, so like `license/deactivate`
 * (#2904) each reads its body through `readBoundedText` with a 16 KB cap. A
 * body over it gets 413 before any database or Stripe call: at once for a
 * declared `content-length` (the body is never pulled), and as soon as the
 * running byte count passes the cap for a stream with no `content-length`.
 *
 * Every oversize probe body here is VALID JSON padded with spaces to 17 KiB,
 * so a route that went back to `req.json()` would parse it and go on to the
 * database: the test then fails on the status AND on the recorded calls.
 *
 * Mocks: `license-routes-harness.ts` and `credit-routes-harness.ts` each mock
 * `db-layer` and `rate-limit`, and one process cannot mock a module twice, so
 * this file registers one stand-in per module covering all four routes.
 */
import { afterEach, beforeEach, describe, it } from "node:test";
import assert from "node:assert/strict";
import { NextRequest } from "next/server";

import { accountKeyRow } from "./account-key-fixture";
import { moduleMock, moduleUrl } from "./module-mock";

import type { AccountKeyRow } from "@/src/lib/db-layer";

/** Every database and Stripe call any of the four routes made, in order. */
const dbCalls: string[] = [];
const stripeCalls: string[] = [];
const rows = new Map<string, AccountKeyRow>();

moduleMock.module(moduleUrl("../src/lib/db-layer.ts"), {
  namedExports: {
    findAccountByKey: async (key: string) => {
      dbCalls.push("findAccountByKey");
      return rows.get(key) ?? null;
    },
    upsertDeviceValidation: async () => {
      dbCalls.push("upsertDeviceValidation");
    },
    getCreditBalance: async () => {
      dbCalls.push("getCreditBalance");
      return 4_200;
    },
    deductCreditBalance: async (_userId: string, amount: number) => {
      dbCalls.push("deductCreditBalance");
      return 4_200 - amount;
    },
    updateAccountKey: async () => {
      dbCalls.push("updateAccountKey");
    },
  },
});

moduleMock.module(moduleUrl("../lib/clients/stripe.ts"), {
  namedExports: {
    stripe: {
      customers: {
        list: async () => {
          stripeCalls.push("customers.list");
          return { data: [] };
        },
        create: async () => {
          stripeCalls.push("customers.create");
          return { id: "cus_created" };
        },
        retrieve: async (id: string) => {
          stripeCalls.push("customers.retrieve");
          return { id, email: "buyer@example.com" };
        },
      },
      checkout: {
        sessions: {
          create: async () => {
            stripeCalls.push("checkout.sessions.create");
            return { id: "cs_test_1", url: "https://checkout.stripe.test/c/1" };
          },
        },
      },
    },
  },
});

const allowAll = { limit: async () => ({ success: true, reset: Date.now() }) };

moduleMock.module(moduleUrl("../lib/rate-limit.ts"), {
  namedExports: {
    licenseValidateRateLimiter: allowAll,
    licenseCreditsRateLimiter: allowAll,
    checkoutRateLimiter: allowAll,
  },
});

const KEY = "HW-TEST-0000-0001";
const CAP = 16 * 1024;
const OVERSIZE = 17 * 1024;

interface RouteCase {
  name: string;
  path: string;
  load: () => Promise<{ POST: (req: NextRequest) => Promise<Response> }>;
  /** A small body that today gets the route's normal success reply. */
  body: Record<string, unknown>;
  /** Asserts the success reply for `body`. */
  expectOk: (json: Record<string, unknown>) => void;
}

const ROUTES: RouteCase[] = [
  {
    name: "POST /api/license/activate",
    path: "/api/license/activate",
    load: () => import("@/app/api/license/activate/route"),
    body: { license_key: KEY },
    expectOk: (json) => {
      assert.equal(json.valid, true);
      assert.equal(typeof json.activation_id, "string");
    },
  },
  {
    name: "POST /api/license/validate",
    path: "/api/license/validate",
    load: () => import("@/app/api/license/validate/route"),
    body: { license_key: KEY, device_id: "device-1", device_name: "Mac" },
    expectOk: (json) => assert.deepEqual(json, { valid: true }),
  },
  {
    name: "POST /api/license/credits",
    path: "/api/license/credits",
    load: () => import("@/app/api/license/credits/route"),
    body: { license_key: KEY, amount: 100 },
    expectOk: (json) => {
      assert.equal(json.credits_remaining, 4_100);
      assert.equal(json.credits_deducted, 100);
    },
  },
  {
    name: "POST /api/checkout/credits",
    path: "/api/checkout/credits",
    load: () => import("@/app/api/checkout/credits/route"),
    body: { amount: 5 },
    expectOk: (json) =>
      assert.deepEqual(json, { checkoutUrl: "https://checkout.stripe.test/c/1" }),
  },
];

interface Probe {
  stream: ReadableStream<Uint8Array>;
  /** Number of times the source was asked for a chunk. */
  pulls: number;
  /** Bytes handed out so far. */
  bytesSent: number;
  cancelled: boolean;
}

/** `body` as JSON, padded with trailing spaces to exactly `totalBytes`. */
function paddedJson(body: Record<string, unknown>, totalBytes: number): Uint8Array {
  const json = JSON.stringify(body);
  return new TextEncoder().encode(json + " ".repeat(totalBytes - json.length));
}

/** A pull-only stream of `bytes`, handed out `chunkSize` bytes per pull. */
function countingStream(bytes: Uint8Array, chunkSize = 1024): Probe {
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
        if (probe.bytesSent >= bytes.length) {
          controller.close();
          return;
        }
        const end = Math.min(probe.bytesSent + chunkSize, bytes.length);
        controller.enqueue(bytes.slice(probe.bytesSent, end));
        probe.bytesSent = end;
      },
      cancel() {
        probe.cancelled = true;
      },
    },
    { highWaterMark: 0 },
  );
  return probe;
}

function streamRequest(
  path: string,
  body: ReadableStream<Uint8Array>,
  headers: Record<string, string> = {},
): NextRequest {
  return new NextRequest(`https://hyperwhisper.test${path}`, {
    method: "POST",
    headers: { "content-type": "application/json", ...headers },
    body,
    duplex: "half",
  } as ConstructorParameters<typeof NextRequest>[1] & { duplex: "half" });
}

function postRequest(path: string, body: Record<string, unknown>): NextRequest {
  return new NextRequest(`https://hyperwhisper.test${path}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(body),
  });
}

const silenced = { error: console.error, warn: console.warn, log: console.log };

beforeEach(() => {
  dbCalls.length = 0;
  stripeCalls.length = 0;
  rows.clear();
  rows.set(KEY, accountKeyRow({ key: KEY }));
  console.error = () => undefined;
  console.warn = () => undefined;
  console.log = () => undefined;
});

afterEach(() => {
  console.error = silenced.error;
  console.warn = silenced.warn;
  console.log = silenced.log;
});

for (const route of ROUTES) {
  describe(`${route.name} body bounds (#3039)`, () => {
    it("answers 413 for a declared 17 KiB content-length without reading the body", async () => {
      const { POST } = await route.load();
      const probe = countingStream(paddedJson(route.body, OVERSIZE));
      const res = await POST(
        streamRequest(route.path, probe.stream, {
          "content-length": String(OVERSIZE),
        }),
      );

      assert.equal(res.status, 413);
      assert.deepEqual(await res.json(), { error: "Payload too large" });
      assert.equal(probe.pulls, 0, "the body was pulled");
      assert.deepEqual(dbCalls, []);
      assert.deepEqual(stripeCalls, []);
    });

    it("answers 413 for a 17 KiB streamed body with no content-length, and stops reading", async () => {
      const { POST } = await route.load();
      const probe = countingStream(paddedJson(route.body, OVERSIZE));
      const req = streamRequest(route.path, probe.stream);

      assert.equal(req.headers.get("content-length"), null);

      const res = await POST(req);

      assert.equal(res.status, 413);
      assert.deepEqual(await res.json(), { error: "Payload too large" });
      assert.equal(probe.cancelled, true, "the stream was not cancelled");
      assert.ok(
        probe.bytesSent <= CAP + 1024,
        `pulled ${probe.bytesSent} bytes past a ${CAP}-byte cap`,
      );
      assert.deepEqual(dbCalls, []);
      assert.deepEqual(stripeCalls, []);
    });

    it("still gives the normal reply for a small body", async () => {
      const { POST } = await route.load();
      const res = await POST(postRequest(route.path, route.body));

      assert.equal(res.status, 200);
      route.expectOk((await res.json()) as Record<string, unknown>);
    });

    it("accepts a body of exactly 16 KiB", async () => {
      const { POST } = await route.load();
      const probe = countingStream(paddedJson(route.body, CAP));
      const res = await POST(streamRequest(route.path, probe.stream));

      assert.equal(res.status, 200);
      route.expectOk((await res.json()) as Record<string, unknown>);
    });

    it("keeps the 400 'Invalid request body' for a body that is not JSON", async () => {
      const { POST } = await route.load();
      const probe = countingStream(new TextEncoder().encode("{not json"));
      const res = await POST(streamRequest(route.path, probe.stream));

      assert.equal(res.status, 400);
      assert.equal(
        ((await res.json()) as Record<string, unknown>).error,
        "Invalid request body",
      );
      assert.deepEqual(dbCalls, []);
      assert.deepEqual(stripeCalls, []);
    });
  });
}
