/**
 * #2069: the download router reads the site's own appcast with the same 3 s
 * limit as `GET /api/download` (`lib/shared/appcast.ts`).
 *
 * `recordDownload` stores the email and sends the welcome email BEFORE it reads
 * the appcast. With no limit, a hung appcast origin held the Download modal's
 * mutation open up to the function's max duration after both side effects had
 * already happened, so the visitor saw an error and submitted again, which sent
 * a second welcome email. With the limit it settles in about 3 s and falls back
 * to the `/api/download` link.
 *
 * The REAL router runs; only its collaborators are replaced (`mock.module`):
 * the database layer, the email service, the rate limiter and Better Auth
 * (which `server/api/trpc.ts` imports). `fetch` is a hung origin: its promise
 * only settles when the caller aborts it.
 */
import assert from "node:assert/strict";
import test, { afterEach, beforeEach, mock } from "node:test";

import { callTRPCProcedure, type AnyRouter } from "@trpc/server";

import { APPCAST_FETCH_TIMEOUT_MS } from "../lib/shared/appcast";

const sideEffects: string[] = [];

function moduleUrl(relative: string): string {
  return new URL(relative, import.meta.url).href;
}

/** `mock.module` is untyped under the pinned `@types/node@20`; see the harnesses. */
interface ModuleMocker {
  module(specifier: string, options: { namedExports: Record<string, unknown> }): void;
}
const moduleMock = mock as unknown as ModuleMocker;

moduleMock.module(moduleUrl("../src/lib/db-layer.ts"), {
  namedExports: {
    upsertEmail: async () => {
      sideEffects.push("upsertEmail");
    },
  },
});
moduleMock.module(moduleUrl("../lib/services/email.ts"), {
  namedExports: {
    emailService: {
      sendWelcomeEmail: async () => {
        sideEffects.push("welcome");
        return { success: true };
      },
    },
  },
});
moduleMock.module(moduleUrl("../lib/rate-limit.ts"), {
  namedExports: {
    downloadEmailRateLimiter: { limit: async () => ({ success: true }) },
  },
});
moduleMock.module(moduleUrl("../src/lib/auth.ts"), {
  namedExports: {
    auth: { api: { getSession: async () => null } },
    getPortalSession: async () => null,
  },
});

/** The validated env the download router reads its origin from (#682). */
const fakeEnv: Record<string, string | undefined> = {};
moduleMock.module(moduleUrl("../src/env/server.mjs"), {
  namedExports: { env: fakeEnv },
});

// A variable specifier, loaded after the mocks above so it binds to them.
const DOWNLOAD_PATH = "../server/api/routers/download.ts";

const ORIGIN = "https://appcast-hangs.example.test";
/** Settling later than this means the limit is not applied. */
const SETTLE_BUDGET_MS = APPCAST_FETCH_TIMEOUT_MS + 2000;

const realFetch = globalThis.fetch;
const realConsoleError = console.error;
let fetchedUrls: string[] = [];

beforeEach(() => {
  sideEffects.length = 0;
  fetchedUrls = [];
  fakeEnv.NEXT_PUBLIC_SITE_URL = ORIGIN;
  console.error = () => {};
  globalThis.fetch = ((input: unknown, init?: RequestInit) => {
    fetchedUrls.push(String(input));
    return new Promise((_resolve, reject) => {
      init?.signal?.addEventListener("abort", () => reject(init.signal?.reason));
    });
  }) as typeof fetch;
});

afterEach(() => {
  globalThis.fetch = realFetch;
  console.error = realConsoleError;
});

async function call(path: string, type: "query" | "mutation", input?: unknown): Promise<unknown> {
  const { downloadRouter } = await import(DOWNLOAD_PATH);
  return callTRPCProcedure({
    router: downloadRouter as unknown as AnyRouter,
    path,
    getRawInput: async () => input,
    ctx: { headers: new Headers({ "x-real-ip": "203.0.113.7" }), user: null, isAdmin: false },
    type,
    signal: undefined,
    batchIndex: 0,
  });
}

/** Resolves to the outcome, or to "still pending" once the budget runs out. */
async function settleWithin<T>(work: Promise<T>): Promise<{ outcome: string; value?: T; ms: number }> {
  const started = Date.now();
  let timer: ReturnType<typeof setTimeout> | undefined;
  const result = await Promise.race([
    work.then(
      (value) => ({ outcome: "resolved", value }),
      (error: unknown) => ({ outcome: `rejected: ${(error as Error)?.message}` }),
    ),
    new Promise<{ outcome: string }>((resolve) => {
      timer = setTimeout(() => resolve({ outcome: "still pending" }), SETTLE_BUDGET_MS);
    }),
  ]);
  clearTimeout(timer);
  return { ...result, ms: Date.now() - started };
}

test("recordDownload gives up on a hung appcast origin and falls back to /api/download", async () => {
  const { outcome, value, ms } = await settleWithin(
    call("recordDownload", "mutation", { email: "download-hang@example.com" }),
  );

  assert.equal(outcome, "resolved", `recordDownload ${outcome} after ${ms} ms`);
  assert.deepEqual(sideEffects, ["upsertEmail", "welcome"], "the email is stored and sent once");
  assert.deepEqual(fetchedUrls, [`${ORIGIN}/appcast.xml`]);
  assert.deepEqual(value, {
    success: true,
    message: "Email recorded successfully",
    downloadUrl: `${ORIGIN}/api/download`,
  });
  assert.ok(ms >= APPCAST_FETCH_TIMEOUT_MS - 100, `waited the full limit, not ${ms} ms`);
});

test("getLatestUrl answers an error, not a hang, when the appcast origin hangs", async () => {
  const { outcome, ms } = await settleWithin(call("getLatestUrl", "query"));

  assert.equal(outcome, "rejected: Failed to get latest download URL", `getLatestUrl ${outcome} after ${ms} ms`);
  assert.deepEqual(fetchedUrls, [`${ORIGIN}/appcast.xml`]);
});
