/**
 * #769: the retry budget and the permanent-vs-transient rule of the REAL
 * `lib/services/email.ts` — the loop every licence-key, welcome and credit
 * email runs through.
 *
 * Each case builds its own `EmailService` with a scripted `send`, a recording
 * `logSentEmail` and a recording `sleep`, so the backoff is read as numbers
 * instead of waited out. The module-level collaborators are still replaced
 * (`mock.module`) only so the module can load: the Resend client validates the
 * server env at import, and the db layer opens the Drizzle pool.
 */
import assert from "node:assert/strict";
import test, { afterEach, beforeEach, mock } from "node:test";

import type { EmailDeps, EmailService as EmailServiceClass } from "../lib/services/email";

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
    resend: {
      emails: {
        send: async () => {
          throw new Error("the module-level Resend client must not be reached");
        },
      },
    },
    DEFAULT_FROM_EMAIL: "HyperWhisper <support@hyperwhisper.com>",
  },
});
moduleMock.module(moduleUrl("../src/lib/db-layer.ts"), {
  namedExports: {
    logSentEmail: async () => {
      throw new Error("the module-level logSentEmail must not be reached");
    },
  },
});

// Variable specifier, loaded after the mocks above so it binds to them.
const EMAIL_PATH = "../lib/services/email.ts";

async function loadEmailService(): Promise<typeof EmailServiceClass> {
  const { EmailService } = (await import(EMAIL_PATH)) as {
    EmailService: typeof EmailServiceClass;
  };
  return EmailService;
}

type SendResult = Awaited<ReturnType<EmailDeps["send"]>>;
type SentEmailRow = Parameters<EmailDeps["logSentEmail"]>[0];

const OK: SendResult = { data: { id: "msg_123" }, error: null };
const PERMANENT: SendResult = {
  data: null,
  error: { name: "validation_error", statusCode: 422, message: "Invalid `to` field" },
};
const RATE_LIMITED: SendResult = {
  data: null,
  error: { name: "rate_limit_exceeded", statusCode: 429, message: "Too many requests" },
};

/** Builds a service whose `send` answers from `script`, one entry per attempt. */
async function rig(script: SendResult[], opts: { logThrows?: boolean } = {}) {
  const EmailService = await loadEmailService();
  const sends: unknown[] = [];
  const sleeps: number[] = [];
  const rows: SentEmailRow[] = [];
  const service = new EmailService({
    send: async (payload) => {
      sends.push(payload);
      const next = script[sends.length - 1];
      assert.ok(next, `send called more often than scripted (${sends.length})`);
      return next;
    },
    logSentEmail: async (row) => {
      rows.push(row);
      if (opts.logThrows) throw new Error("sent_emails insert failed");
    },
    sleep: async (ms) => {
      sleeps.push(ms);
    },
  });
  return { service, sends, sleeps, rows };
}

function sendLicense(service: EmailServiceClass) {
  return service.sendLicenseKey({
    customerName: "Buyer",
    customerEmail: "buyer@example.com",
    licenseKey: "TEST-0000-0000-0000",
    productName: "HyperWhisper",
    supportEmail: "support@hyperwhisper.com",
  });
}

// The loop logs every attempt; keep the test output readable.
beforeEach(() => {
  mock.method(console, "log", () => {});
  mock.method(console, "error", () => {});
});
afterEach(() => {
  mock.restoreAll();
});

test("a permanent failure (validation_error 422) is sent once and never sleeps", async () => {
  const { service, sends, sleeps, rows } = await rig([PERMANENT, OK, OK]);

  const result = await sendLicense(service);

  assert.equal(sends.length, 1, "a permanent failure is not retried");
  assert.deepEqual(sleeps, [], "no backoff before giving up");
  assert.equal(result.success, false);
  assert.equal(result.error, "Invalid `to` field");
  assert.equal(rows.length, 1);
  assert.equal(rows[0]?.status, "failed");
});

test("rate_limit_exceeded twice then success: 3 sends, backoff [250, 500], success", async () => {
  const { service, sends, sleeps, rows } = await rig([RATE_LIMITED, RATE_LIMITED, OK]);

  const result = await sendLicense(service);

  assert.equal(sends.length, 3);
  assert.deepEqual(sleeps, [250, 500]);
  assert.deepEqual(result, { success: true, data: { id: "msg_123" } });
  assert.deepEqual(
    rows.map((row) => row.status),
    ["sent"],
    "one audit row for the one logical email",
  );
});

test("a failure with statusCode null is treated as transient and retried", async () => {
  const unknownStatus: SendResult = {
    data: null,
    error: { name: "some_new_error", statusCode: null, message: "Unknown" },
  };
  const { service, sends, sleeps } = await rig([unknownStatus, OK]);

  const result = await sendLicense(service);

  assert.equal(sends.length, 2);
  assert.deepEqual(sleeps, [250]);
  assert.equal(result.success, true);
});

test("a 4xx with no retryable name is permanent, a 5xx is transient", async () => {
  const forbidden = await rig([
    { data: null, error: { name: "invalid_api_key", statusCode: 403, message: "Forbidden" } },
  ]);
  assert.equal((await sendLicense(forbidden.service)).success, false);
  assert.equal(forbidden.sends.length, 1);

  const gateway = await rig([
    { data: null, error: { name: "bad_gateway", statusCode: 502, message: "Bad gateway" } },
    OK,
  ]);
  assert.equal((await sendLicense(gateway.service)).success, true);
  assert.equal(gateway.sends.length, 2);
});

test("3 transient failures: success false, the LAST error, a failed row, no sleep after the last attempt", async () => {
  const { service, sends, sleeps, rows } = await rig([
    { data: null, error: { name: "internal_server_error", statusCode: 500, message: "first" } },
    { data: null, error: { name: "internal_server_error", statusCode: 500, message: "second" } },
    { data: null, error: { name: "internal_server_error", statusCode: 500, message: "third" } },
  ]);

  const result = await sendLicense(service);

  assert.equal(sends.length, 3, "the budget is 3 attempts");
  assert.deepEqual(sleeps, [250, 500], "750 ms worst case, nothing after attempt 3");
  assert.deepEqual(result, { success: false, error: "third" });
  assert.equal(rows.length, 1);
  assert.equal(rows[0]?.status, "failed");
  assert.equal(rows[0]?.errorMessage, "third");
});

test("a thrown send (network error) is retried like a transient failure", async () => {
  const EmailService = await loadEmailService();
  const sends: number[] = [];
  const sleeps: number[] = [];
  const service = new EmailService({
    send: async () => {
      sends.push(sends.length);
      if (sends.length < 3) throw new TypeError("fetch failed");
      return OK;
    },
    logSentEmail: async () => {},
    sleep: async (ms) => {
      sleeps.push(ms);
    },
  });

  const result = await sendLicense(service);

  assert.equal(sends.length, 3);
  assert.deepEqual(sleeps, [250, 500]);
  assert.equal(result.success, true);
});

test("a success logs status sent with the provider message id", async () => {
  const { service, sends, rows } = await rig([OK]);

  await sendLicense(service);

  assert.deepEqual(rows, [
    {
      recipient: "buyer@example.com",
      emailType: "license",
      subject: "Your HyperWhisper License Key",
      providerMessageId: "msg_123",
      status: "sent",
    },
  ]);
  const [payload] = sends as Array<Record<string, unknown>>;
  assert.equal(payload?.to, "buyer@example.com");
  assert.equal(payload?.from, "HyperWhisper <support@hyperwhisper.com>");
  assert.equal(payload?.subject, "Your HyperWhisper License Key");
});

test("a success payload whose id is not a string logs providerMessageId null", async () => {
  const { service, rows } = await rig([{ data: { id: 42 }, error: null }]);

  const result = await sendLicense(service);

  assert.equal(result.success, true);
  assert.equal(rows[0]?.providerMessageId, null);
});

test("a throwing logSentEmail changes neither the success nor the failure result", async () => {
  const ok = await rig([OK], { logThrows: true });
  assert.deepEqual(await sendLicense(ok.service), {
    success: true,
    data: { id: "msg_123" },
  });

  const failed = await rig([PERMANENT], { logThrows: true });
  assert.deepEqual(await sendLicense(failed.service), {
    success: false,
    error: "Invalid `to` field",
  });
});
