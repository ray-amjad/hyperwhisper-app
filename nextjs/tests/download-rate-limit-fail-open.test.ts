/**
 * #2136: the download form's email limiter fails open. When Upstash is
 * unreachable `recordDownload` still hands the visitor the download link; when
 * the limiter refuses, the TOO_MANY_REQUESTS reply is unchanged.
 *
 * The REAL `server/api/routers/download.ts` and `lib/rate-limit-gate.ts` run,
 * driven through tRPC's own `callTRPCProcedure`. Only the collaborators are
 * replaced (`mock.module`): the Upstash limiter, the database layer, the email
 * service and Better Auth (which `server/api/trpc.ts` imports).
 */
import assert from "node:assert/strict";
import test, { afterEach, beforeEach } from "node:test";

import { TRPCError, callTRPCProcedure, type AnyRouter } from "@trpc/server";

import { moduleMock, moduleUrl } from "./module-mock";

const behaviour = {
  /** Thrown by the limiter when set: Upstash unreachable. */
  limitError: null as unknown,
  /** What the limiter answers when it does not throw. */
  limitSuccess: true,
};
const calls = { limit: [] as string[], upserts: 0, welcomeEmails: 0 };

moduleMock.module(moduleUrl("../lib/rate-limit.ts"), {
  namedExports: {
    downloadEmailRateLimiter: {
      limit: async (identifier: string) => {
        calls.limit.push(identifier);
        if (behaviour.limitError) throw behaviour.limitError;
        return { success: behaviour.limitSuccess, reset: Date.now() + 30_000 };
      },
    },
  },
});
moduleMock.module(moduleUrl("../src/lib/db-layer.ts"), {
  namedExports: {
    upsertEmail: async () => {
      calls.upserts += 1;
    },
  },
});
moduleMock.module(moduleUrl("../lib/services/email.ts"), {
  namedExports: {
    emailService: {
      sendWelcomeEmail: async () => {
        calls.welcomeEmails += 1;
        return { success: true };
      },
    },
  },
});
moduleMock.module(moduleUrl("../src/lib/auth.ts"), {
  namedExports: {
    auth: { api: { getSession: async () => null } },
    getPortalSession: async () => null,
  },
});

// The download router reads NEXT_PUBLIC_SITE_URL and SUPPORT_EMAIL from the
// validated env (#682). Neither is set here, so it uses its own defaults.
moduleMock.module(moduleUrl("../src/env/server.mjs"), {
  namedExports: { env: {} },
});

// A variable specifier, loaded after the mocks above so it binds to them.
const DOWNLOAD_PATH = "../server/api/routers/download.ts";
const CLIENT_IP = "203.0.113.7";

let warnings: string[] = [];
const realConsole = { log: console.log, warn: console.warn, error: console.error };
const realFetch = globalThis.fetch;

beforeEach(() => {
  behaviour.limitError = null;
  behaviour.limitSuccess = true;
  calls.limit.length = 0;
  calls.upserts = 0;
  calls.welcomeEmails = 0;
  warnings = [];
  console.log = () => {};
  console.error = () => {};
  console.warn = (...args: unknown[]) => {
    warnings.push(args.map(String).join(" "));
  };
  // `recordDownload` fetches the appcast; answer "not found" with no network.
  globalThis.fetch = (async () => new Response("", { status: 404 })) as typeof fetch;
});

afterEach(() => {
  console.log = realConsole.log;
  console.warn = realConsole.warn;
  console.error = realConsole.error;
  globalThis.fetch = realFetch;
});

async function recordDownload(): Promise<unknown> {
  const { downloadRouter } = await import(DOWNLOAD_PATH);
  return callTRPCProcedure({
    router: downloadRouter as unknown as AnyRouter,
    path: "recordDownload",
    getRawInput: async () => ({ email: "visitor@example.org" }),
    ctx: { headers: new Headers({ "x-real-ip": CLIENT_IP }), user: null, isAdmin: false },
    type: "mutation",
    signal: undefined,
    batchIndex: 0,
  });
}

test("download: an unreachable limiter still records the download and returns the link (#2136)", async () => {
  behaviour.limitError = new TypeError(`fetch failed: upstash ${CLIENT_IP}`);

  const result = (await recordDownload()) as { success: boolean; downloadUrl: string };

  assert.equal(result.success, true);
  assert.match(result.downloadUrl, /\/api\/download$/);
  assert.deepEqual(calls.limit, [CLIENT_IP]);
  assert.equal(calls.upserts, 1, "the email is still stored");
  assert.equal(calls.welcomeEmails, 1, "the welcome email still goes out");
  assert.ok(
    warnings.includes("Rate limit unavailable for download email, failing open: TypeError"),
    warnings.join("\n"),
  );
  assert.ok(!warnings.some((line) => line.includes(CLIENT_IP)), "the error message is not logged");
});

test("download: a refusing limiter still answers TOO_MANY_REQUESTS and records nothing", async () => {
  behaviour.limitSuccess = false;

  await assert.rejects(recordDownload(), (error: unknown) => {
    assert.ok(error instanceof TRPCError);
    assert.equal(error.code, "TOO_MANY_REQUESTS");
    assert.equal(error.message, "Too many requests. Please try again later.");
    return true;
  });
  assert.equal(calls.upserts, 0);
  assert.equal(calls.welcomeEmails, 0);
});
