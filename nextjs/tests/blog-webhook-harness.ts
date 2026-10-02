/**
 * Test harness for the Outrank blog webhook, `app/api/webhooks/add-blog-post`.
 *
 * The route reaches two collaborators at import time: the validated server
 * env (`src/env/server.mjs`) and the database (`src/db/index.ts`). Both are
 * replaced here with `mock.module`, so the tests run the REAL route module —
 * its auth gate, its body read, its JSON parse and its reply shapes — with no
 * database and no real Outrank credential.
 *
 * The mocks are installed when this module is evaluated, and
 * `loadBlogWebhookRoute` is the only way the tests reach the route. A test
 * file that imports the route path itself would bind to the real
 * collaborators, so do not.
 */
import { mock } from "node:test";

import { NextRequest } from "next/server";

/** Made-up bearer value. Only the fake env below holds it. */
export const OUTRANK_BEARER = "outrank-bearer-for-tests";

/**
 * The fake validated env. Signing stays unset, which is the normal direct
 * Outrank setup: the bearer check then runs before the body is read.
 */
export const fakeEnv: Record<string, string | undefined> = {
  OUTRANK_WEBHOOK_TOKEN: OUTRANK_BEARER,
  OUTRANK_WEBHOOK_SIGNING_SECRET: undefined,
};

/** Database calls the route made. The 400 paths must make none. */
export const dbCalls: string[] = [];

/** Every `console.error` call the route made, with its raw arguments. */
export const errorCalls: unknown[][] = [];

const realConsoleError = console.error;

export function captureRouteErrors(): void {
  console.error = (...args: unknown[]): void => {
    errorCalls.push(args);
  };
}

export function restoreRouteErrors(): void {
  console.error = realConsoleError;
}

export function resetHarness(): void {
  dbCalls.length = 0;
  errorCalls.length = 0;
}

function moduleUrl(relative: string): string {
  return new URL(relative, import.meta.url).href;
}

/**
 * `mock.module` is a real Node 22 API (behind `--experimental-test-module-mocks`,
 * which `npm test` passes) but this repo pins `@types/node@20`, which has no
 * declaration for it. Narrow the tracker to the one method used here rather
 * than bumping the types in a test-only change.
 */
interface ModuleMocker {
  module(
    specifier: string,
    options: { namedExports: Record<string, unknown> },
  ): void;
}

const moduleMock = mock as unknown as ModuleMocker;

function recordDb(method: string) {
  return () => {
    dbCalls.push(method);
    throw new Error(`unexpected db.${method} in a blog webhook test`);
  };
}

moduleMock.module(moduleUrl("../src/db/index.ts"), {
  namedExports: {
    db: { select: recordDb("select"), insert: recordDb("insert") },
  },
});

moduleMock.module(moduleUrl("../src/env/server.mjs"), {
  namedExports: { env: fakeEnv },
});

const URL_BASE = "https://hyperwhisper.test/api/webhooks/add-blog-post";

/** An authorized POST carrying `body` as its raw text. */
export function postRaw(
  body: string,
  contentType = "application/json",
): NextRequest {
  return new NextRequest(URL_BASE, {
    method: "POST",
    headers: {
      "content-type": contentType,
      authorization: `Bearer ${OUTRANK_BEARER}`,
    },
    body,
  });
}

/**
 * An authorized POST whose body stream fails part-way, so `req.text()`
 * rejects with `failure`. The chunk already sent is returned so a test can
 * prove it never reaches the log.
 */
export function postBrokenStream(
  sentChunk: string,
  failure: Error,
  contentType = "application/json",
): NextRequest {
  const stream = new ReadableStream<Uint8Array>({
    start(controller) {
      controller.enqueue(new TextEncoder().encode(sentChunk));
      controller.error(failure);
    },
  });
  return new NextRequest(URL_BASE, {
    method: "POST",
    headers: {
      "content-type": contentType,
      authorization: `Bearer ${OUTRANK_BEARER}`,
    },
    body: stream,
    duplex: "half",
  } as ConstructorParameters<typeof NextRequest>[1] & { duplex: "half" });
}

/** Loads the route AFTER the mocks above are installed, so it binds to them. */
export const loadBlogWebhookRoute = () =>
  import("@/app/api/webhooks/add-blog-post/route");
