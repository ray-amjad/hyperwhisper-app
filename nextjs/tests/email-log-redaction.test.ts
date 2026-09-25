/**
 * #717: no customer email address reaches a server log line from the REAL
 * `lib/services/email.ts` or the REAL `server/api/routers/download.ts` — on the
 * success path, and on the Resend-error path, where Resend's `validation_error`
 * echoes the recipient ("alice@corp.com is not a valid recipient") and the
 * caller logs the text it gets back in `EmailResult.error`.
 *
 * Only the collaborators are replaced (`mock.module`): the Resend client, the
 * database layer, the rate limiter and Better Auth (which `server/api/trpc.ts`
 * imports). Every `console.*` line is captured while a case runs.
 */
import assert from "node:assert/strict";
import test, { afterEach, beforeEach, mock } from "node:test";

import { callTRPCProcedure, type AnyRouter } from "@trpc/server";

import { emailTag } from "../lib/shared/redact";

/** As Stripe or a form hands it over: mixed case, trailing space. */
const RAW = "Alice.Smith@Acme-Widgets.com ";
/** The download form's zod `email()` refuses whitespace, so no trailing space. */
const RAW_FORM = "Alice.Smith@Acme-Widgets.com";
const TAG = emailTag(RAW);
/** Any spelling of the address or its domain, in any case. */
const LEAK = /alice\.smith|acme-widgets\.com|@/i;

const behaviour = {
  result: { data: { id: "msg_1" }, error: null } as { data: unknown; error: unknown },
};
const sentEmailRows: Array<Record<string, unknown>> = [];

function moduleUrl(relative: string): string {
  return new URL(relative, import.meta.url).href;
}

/** `mock.module` is untyped under the pinned `@types/node@20`; see the harnesses. */
interface ModuleMocker {
  module(specifier: string, options: { namedExports: Record<string, unknown> }): void;
}
const moduleMock = mock as unknown as ModuleMocker;

moduleMock.module(moduleUrl("../lib/clients/resend.ts"), {
  namedExports: {
    resend: { emails: { send: async () => behaviour.result } },
    DEFAULT_FROM_EMAIL: "HyperWhisper <support@hyperwhisper.com>",
  },
});
moduleMock.module(moduleUrl("../src/lib/db-layer.ts"), {
  namedExports: {
    logSentEmail: async (row: Record<string, unknown>) => {
      sentEmailRows.push(row);
    },
    upsertEmail: async () => {},
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

// Variable specifiers, loaded after the mocks above so they bind to them.
const EMAIL_PATH = "../lib/services/email.ts";
const DOWNLOAD_PATH = "../server/api/routers/download.ts";

let lines: string[] = [];
const realConsole = { log: console.log, warn: console.warn, error: console.error };
const realFetch = globalThis.fetch;

beforeEach(() => {
  lines = [];
  sentEmailRows.length = 0;
  behaviour.result = { data: { id: "msg_1" }, error: null };
  const capture =
    (level: string) =>
    (...args: unknown[]): void => {
      // `String(err)` drops an Error's stack, so inspect-like output is kept too.
      lines.push(
        `${level} ${args.map((a) => (a instanceof Error ? `${String(a)} ${a.stack}` : String(a))).join(" ")}`,
      );
    };
  console.log = capture("log");
  console.warn = capture("warn");
  console.error = capture("error");
  // `recordDownload` fetches the appcast; answer "not found" with no network.
  globalThis.fetch = (async () => new Response("", { status: 404 })) as typeof fetch;
});

afterEach(() => {
  console.log = realConsole.log;
  console.warn = realConsole.warn;
  console.error = realConsole.error;
  globalThis.fetch = realFetch;
});

/** A real Resend `validation_error`, echoing the recipient in another case. */
function resendRejectsRecipient(): void {
  behaviour.result = {
    data: null,
    error: {
      name: "validation_error",
      statusCode: 422,
      message: "alice.smith@acme-widgets.com is not a valid recipient",
    },
  };
}

function assertNoAddressIn(logged: string[]): void {
  assert.ok(logged.length > 0, "the case logged something");
  assert.deepEqual(
    logged.filter((line) => LEAK.test(line)),
    [],
    "no log line carries the address or its domain",
  );
}

async function sendLicense() {
  const { emailService } = await import(EMAIL_PATH);
  return emailService.sendLicenseKey({
    customerName: "Alice",
    customerEmail: RAW,
    licenseKey: "HW-TEST-0001",
    productName: "HyperWhisper",
    supportEmail: "hi@support.hyperwhisper.com",
  });
}

test("email service: a successful send logs the tag, never the address", async () => {
  const result = await sendLicense();

  assert.equal(result.success, true);
  assertNoAddressIn(lines);
  assert.ok(lines.some((line) => line.includes(TAG)), "the tag is logged instead");
});

test("email service: a Resend error echoing the recipient is redacted in the log AND in EmailResult.error", async () => {
  resendRejectsRecipient();

  const result = await sendLicense();

  assert.equal(result.success, false);
  // The diagnosis survives; the address becomes its tag.
  assert.equal(result.error, `${TAG} is not a valid recipient`);
  assertNoAddressIn(lines);
  assert.ok(
    lines.some((line) => line.includes(`${TAG} is not a valid recipient`)),
    "the catch-block log keeps Resend's reason",
  );
  // The audit row still names the recipient (it is the row's own column).
  assert.equal(sentEmailRows[0]?.recipient, RAW);
  assert.equal(sentEmailRows[0]?.errorMessage, `${TAG} is not a valid recipient`);
});

async function recordDownload(email: string = RAW_FORM): Promise<unknown> {
  const { downloadRouter } = await import(DOWNLOAD_PATH);
  return callTRPCProcedure({
    router: downloadRouter as unknown as AnyRouter,
    path: "recordDownload",
    getRawInput: async () => ({ email }),
    ctx: { headers: new Headers({ "x-real-ip": "203.0.113.7" }), user: null, isAdmin: false },
    type: "mutation",
    signal: undefined,
    batchIndex: 0,
  });
}

test("download: a recorded download logs the tag, never the address", async () => {
  const result = (await recordDownload()) as { success: boolean };

  assert.equal(result.success, true);
  assertNoAddressIn(lines);
  assert.ok(lines.some((line) => line.includes(TAG)));
});

test("download: a welcome email Resend refuses is logged without the address", async () => {
  resendRejectsRecipient();

  const result = (await recordDownload()) as { success: boolean };

  assert.equal(result.success, true);
  assertNoAddressIn(lines);
  assert.ok(
    lines.some((line) =>
      line.startsWith(`error Welcome email failed to send: ${TAG} is not a valid recipient`),
    ),
    "download.ts logs EmailResult.error, redacted at the source",
  );
});

test("download: a 60 KB address Resend echoes back reaches no log line and no EmailResult.error (review r2)", async () => {
  // The public form's `z.string().email()` has no length bound. On the old
  // RegExp-based redaction this address threw `Regular expression too large`,
  // and that SyntaxError's text — the address, regex-escaped — was logged on
  // every retry and returned as `EmailResult.error`.
  const huge = `${"alice.smith".repeat(5500)}@acme-widgets.com`;
  behaviour.result = {
    data: null,
    error: { name: "validation_error", statusCode: 422, message: `${huge} is not a valid recipient` },
  };

  const started = performance.now();
  const result = (await recordDownload(huge)) as { success: boolean };

  assert.equal(result.success, true);
  assert.ok(performance.now() - started < 2000, "no retry backoff: the error stays non-retryable");
  assertNoAddressIn(lines);
  assert.ok(
    lines.some((line) =>
      line.startsWith("error Welcome email failed to send: [redacted] is not a valid recipient"),
    ),
    "the reason survives, fail-closed",
  );
  assert.ok(!lines.some((line) => line.includes("Invalid regular expression")));
});
