/**
 * #774: the download form's abuse guards and appcast fallback.
 *
 * The first block drives `createDownloadRecorder` and `createAppcastReader`
 * (`server/api/routers/download-appcast.ts`) with stubbed collaborators: the
 * rate limit, the disposable-domain block and its subdomain walk, the two
 * failures the mutation swallows on purpose, and the appcast fallback.
 *
 * The last block runs the REAL router through tRPC's `callTRPCProcedure`, with
 * only its modules replaced (`mock.module`), so the wiring in `download.ts`
 * (the real disposable list, `fetch`, the origin) is proved too.
 */
import assert from "node:assert/strict";
import test, { afterEach, beforeEach, describe, mock } from "node:test";

import { TRPCError, callTRPCProcedure, type AnyRouter } from "@trpc/server";

import {
  createAppcastReader,
  createDownloadRecorder,
  isDisposableDomain,
  type DownloadRecordSource,
} from "../server/api/routers/download-appcast";
import type { RateLimitOutcome } from "../lib/rate-limit-gate";

const ORIGIN = "https://download-seam.example.test";
const FIRST_DMG = "https://downloads.example.test/HyperWhisper-2.0.0.dmg";
const SECOND_DMG = "https://downloads.example.test/HyperWhisper-1.9.0.dmg";
const TWO_ITEM_APPCAST = `<?xml version="1.0"?>
<rss><channel>
<item><title>2.0.0</title><enclosure url="${FIRST_DMG}" length="1" type="application/octet-stream"/></item>
<item><title>1.9.0</title><enclosure url="${SECOND_DMG}" length="1" type="application/octet-stream"/></item>
</channel></rss>`;

interface Calls {
  limit: string[];
  upserts: { email: string; source: string }[];
  welcomeEmails: string[];
  fetched: string[];
}

type Answer = Response | Error;

function fakeFetch(calls: Calls, answer: () => Answer): typeof fetch {
  return (async (input: unknown) => {
    calls.fetched.push(String(input));
    const result = answer();
    if (result instanceof Error) throw result;
    return result;
  }) as typeof fetch;
}

/** A recorder over stubs; every override replaces one collaborator. */
function recorder(
  overrides: Partial<DownloadRecordSource> & {
    limited?: boolean;
    domains?: string[];
    appcast?: () => Answer;
  } = {},
) {
  const calls: Calls = { limit: [], upserts: [], welcomeEmails: [], fetched: [] };
  const { limited = false, domains = [], appcast, ...source } = overrides;
  const record = createDownloadRecorder({
    clientIP: (headers) => headers.get("x-real-ip") ?? "unknown",
    limit: async (clientIP): Promise<RateLimitOutcome> => {
      calls.limit.push(clientIP);
      return limited ? { limited: true, retryAfterSeconds: 60 } : { limited: false };
    },
    getDisposableDomains: async () => new Set(domains),
    upsertEmail: async (row) => {
      calls.upserts.push(row);
    },
    sendWelcomeEmail: async (data) => {
      calls.welcomeEmails.push(data.customerEmail);
      return { success: true };
    },
    latestDownloadUrl: createAppcastReader({
      fetch: fakeFetch(calls, appcast ?? (() => new Response(TWO_ITEM_APPCAST))),
    }),
    origin: ORIGIN,
    supportEmail: "support@example.test",
    ...source,
  });
  return { calls, record: (email: string) => record({ email, headers: new Headers({ "x-real-ip": "203.0.113.9" }) }) };
}

async function rejectsWith(work: Promise<unknown>, code: string, message: string) {
  await assert.rejects(work, (error: unknown) => {
    assert.ok(error instanceof TRPCError, `expected a TRPCError, got ${String(error)}`);
    assert.equal(error.code, code);
    assert.equal(error.message, message);
    return true;
  });
}

const realConsole = { log: console.log, error: console.error };
let errors: string[] = [];

beforeEach(() => {
  errors = [];
  console.log = () => {};
  console.error = (...args: unknown[]) => {
    errors.push(args.map(String).join(" "));
  };
});

afterEach(() => {
  console.log = realConsole.log;
  console.error = realConsole.error;
});

describe("download recorder (#774)", () => {
  test("a refused rate limit throws TOO_MANY_REQUESTS and writes no row and sends no email", async () => {
    const { calls, record } = recorder({ limited: true });

    await rejectsWith(
      record("visitor@example.org"),
      "TOO_MANY_REQUESTS",
      "Too many requests. Please try again later.",
    );
    assert.deepEqual(calls.limit, ["203.0.113.9"]);
    assert.deepEqual(calls.upserts, []);
    assert.deepEqual(calls.welcomeEmails, []);
    assert.deepEqual(calls.fetched, []);
  });

  test("a disposable domain throws BAD_REQUEST and writes no row and sends no email", async () => {
    const { calls, record } = recorder({ domains: ["mailinator.com"] });

    await rejectsWith(
      record("someone@mailinator.com"),
      "BAD_REQUEST",
      "Disposable email domains are not allowed. Please use a valid email so we can send your download link.",
    );
    assert.deepEqual(calls.upserts, []);
    assert.deepEqual(calls.welcomeEmails, []);
    assert.deepEqual(calls.fetched, []);
  });

  test("sub.mailinator.com is blocked when the list holds mailinator.com", async () => {
    const list = new Set(["mailinator.com"]);
    assert.equal(isDisposableDomain("sub.mailinator.com", list), true);
    assert.equal(isDisposableDomain("a.b.Sub.MAILINATOR.com", list), true);
    assert.equal(isDisposableDomain("notmailinator.com", list), false);

    const { calls, record } = recorder({ domains: ["mailinator.com"] });
    await assert.rejects(record("someone@sub.mailinator.com"), { code: "BAD_REQUEST" });
    assert.deepEqual(calls.upserts, []);
  });

  test("a list holding the bare TLD com does not block user@example.com", async () => {
    assert.equal(isDisposableDomain("example.com", new Set(["com"])), false);
    assert.equal(isDisposableDomain("a.b.example.com", new Set(["com"])), false);

    const { calls, record } = recorder({ domains: ["com"] });
    const result = await record("user@example.com");

    assert.equal(result.success, true);
    assert.deepEqual(calls.upserts, [{ email: "user@example.com", source: "hyperwhisper-download" }]);
    assert.deepEqual(calls.welcomeEmails, ["user@example.com"]);
  });

  test("an upsertEmail that throws still returns success and still sends the welcome email", async () => {
    const welcomeEmails: string[] = [];
    const { record } = recorder({
      upsertEmail: async () => {
        throw new Error("connection refused");
      },
      sendWelcomeEmail: async (data) => {
        welcomeEmails.push(data.customerEmail);
        return { success: true };
      },
    });

    const result = await record("visitor@example.org");

    assert.deepEqual(result, {
      success: true,
      message: "Email recorded successfully",
      downloadUrl: FIRST_DMG,
    });
    assert.deepEqual(welcomeEmails, ["visitor@example.org"]);
    assert.ok(errors.some((line) => line.startsWith("Error storing email:")), errors.join("\n"));
  });

  test("a sendWelcomeEmail that answers success: false still returns success", async () => {
    const { calls, record } = recorder({
      sendWelcomeEmail: async () => ({ success: false, error: "resend refused" }),
    });

    const result = await record("visitor@example.org");

    assert.equal(result.success, true);
    assert.equal(result.downloadUrl, FIRST_DMG);
    assert.equal(calls.upserts.length, 1);
    assert.ok(errors.includes("Welcome email failed to send: resend refused"), errors.join("\n"));
  });

  test("an appcast that 404s falls back to /api/download; a readable one gives the FIRST item's enclosure", async () => {
    const missing = recorder({ appcast: () => new Response("not found", { status: 404 }) });
    const fallback = await missing.record("visitor@example.org");
    assert.equal(fallback.downloadUrl, `${ORIGIN}/api/download`);
    assert.deepEqual(missing.calls.fetched, [`${ORIGIN}/appcast.xml`]);

    const readable = recorder();
    const latest = await readable.record("visitor@example.org");
    assert.equal(latest.downloadUrl, FIRST_DMG);
    assert.deepEqual(readable.calls.fetched, [`${ORIGIN}/appcast.xml`]);
  });
});

// ----------------------------------------------------------------------------
// The real router, wired to the real disposable list and the global `fetch`.
// ----------------------------------------------------------------------------

function moduleUrl(relative: string): string {
  return new URL(relative, import.meta.url).href;
}

/** `mock.module` is untyped under the pinned `@types/node@20`; see the harnesses. */
interface ModuleMocker {
  module(specifier: string, options: { namedExports: Record<string, unknown> }): void;
}
const moduleMock = mock as unknown as ModuleMocker;

const routerCalls = { upserts: 0, welcomeEmails: [] as string[] };

moduleMock.module(moduleUrl("../src/lib/db-layer.ts"), {
  namedExports: {
    upsertEmail: async () => {
      routerCalls.upserts += 1;
    },
  },
});
moduleMock.module(moduleUrl("../lib/services/email.ts"), {
  namedExports: {
    emailService: {
      sendWelcomeEmail: async (data: { customerEmail: string; supportEmail: string; downloadUrl: string }) => {
        routerCalls.welcomeEmails.push(`${data.customerEmail} ${data.supportEmail} ${data.downloadUrl}`);
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
moduleMock.module(moduleUrl("../src/env/server.mjs"), {
  namedExports: {
    env: { NEXT_PUBLIC_SITE_URL: ORIGIN, SUPPORT_EMAIL: "help@example.test" },
  },
});

// A variable specifier, loaded after the mocks above so it binds to them.
const DOWNLOAD_PATH = "../server/api/routers/download.ts";

async function callRouter(path: string, type: "query" | "mutation", input?: unknown) {
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

describe("download router wiring (#774)", () => {
  const realFetch = globalThis.fetch;
  let fetched: string[] = [];

  beforeEach(() => {
    routerCalls.upserts = 0;
    routerCalls.welcomeEmails = [];
    fetched = [];
    globalThis.fetch = (async (input: unknown) => {
      fetched.push(String(input));
      return new Response(TWO_ITEM_APPCAST);
    }) as typeof fetch;
  });

  afterEach(() => {
    globalThis.fetch = realFetch;
  });

  test("recordDownload blocks a subdomain of a listed disposable domain before any side effect", async () => {
    await assert.rejects(callRouter("recordDownload", "mutation", { email: "x@sub.mailinator.com" }), {
      code: "BAD_REQUEST",
    });
    assert.equal(routerCalls.upserts, 0);
    assert.deepEqual(routerCalls.welcomeEmails, []);
    assert.deepEqual(fetched, []);
  });

  test("recordDownload and getLatestUrl read the appcast at the env origin through the global fetch", async () => {
    const recorded = await callRouter("recordDownload", "mutation", { email: "visitor@example.org" });
    assert.deepEqual(recorded, {
      success: true,
      message: "Email recorded successfully",
      downloadUrl: FIRST_DMG,
    });
    assert.equal(routerCalls.upserts, 1);
    assert.deepEqual(routerCalls.welcomeEmails, [
      `visitor@example.org help@example.test ${ORIGIN}/download`,
    ]);

    assert.deepEqual(await callRouter("getLatestUrl", "query"), { downloadUrl: FIRST_DMG });
    assert.deepEqual(fetched, [`${ORIGIN}/appcast.xml`, `${ORIGIN}/appcast.xml`]);
  });
});
