/**
 * #769: the retry budget and the permanent-vs-transient rule of the REAL
 * `lib/services/email.ts` — the loop every licence-key, welcome and credit
 * email runs through.
 *
 * Each case builds its own `EmailService` with a scripted `send`, a recording
 * `logSentEmail` and a recording `sleep`, so the backoff is read as numbers
 * instead of waited out. The module-level collaborators are still replaced
 * (`mock.module`) only so the module can load: the email client reaches for
 * the Cloudflare binding, and the db layer opens the Drizzle pool.
 */
import assert from "node:assert/strict";
import test, { afterEach, beforeEach, mock } from "node:test";

import type { EmailDeps, EmailService as EmailServiceClass } from "../lib/services/email";
import { moduleMock, moduleUrl } from "./module-mock";

moduleMock.module(moduleUrl("../lib/clients/email.ts"), {
  namedExports: {
    sendEmail: async () => {
      throw new Error("the module-level email client must not be reached");
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
  error: { code: "E_VALIDATION_ERROR", message: "Invalid `to` field" },
};
const RATE_LIMITED: SendResult = {
  data: null,
  error: { code: "E_RATE_LIMIT_EXCEEDED", message: "Too many requests" },
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

test("a permanent failure (E_VALIDATION_ERROR) is sent once and never sleeps", async () => {
  const { service, sends, sleeps, rows } = await rig([PERMANENT, OK, OK]);

  const result = await sendLicense(service);

  assert.equal(sends.length, 1, "a permanent failure is not retried");
  assert.deepEqual(sleeps, [], "no backoff before giving up");
  assert.equal(result.success, false);
  assert.equal(result.error, "Invalid `to` field");
  assert.equal(rows.length, 1);
  assert.equal(rows[0]?.status, "failed");
});

test("E_RATE_LIMIT_EXCEEDED twice then success: 3 sends, backoff [250, 500], success", async () => {
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

test("a failure with no Cloudflare code is treated as transient and retried", async () => {
  const unknown: SendResult = {
    data: null,
    error: { code: "unknown", message: "Network connection lost" },
  };
  const { service, sends, sleeps } = await rig([unknown, OK]);

  const result = await sendLicense(service);

  assert.equal(sends.length, 2);
  assert.deepEqual(sleeps, [250]);
  assert.equal(result.success, true);
});

test("any other E_ code is permanent; E_INTERNAL_SERVER_ERROR is transient", async () => {
  for (const code of [
    "E_SENDER_NOT_VERIFIED",
    "E_RECIPIENT_SUPPRESSED",
    "E_DAILY_LIMIT_EXCEEDED",
    "E_DELIVERY_FAILED",
    "E_BINDING_MISSING",
  ]) {
    const permanent = await rig([{ data: null, error: { code, message: code } }]);
    assert.equal((await sendLicense(permanent.service)).success, false);
    assert.equal(permanent.sends.length, 1, `${code} is not retried`);
  }

  const outage = await rig([
    { data: null, error: { code: "E_INTERNAL_SERVER_ERROR", message: "Unavailable" } },
    OK,
  ]);
  assert.equal((await sendLicense(outage.service)).success, true);
  assert.equal(outage.sends.length, 2);
});

test("3 transient failures: success false, the LAST error, a failed row, no sleep after the last attempt", async () => {
  const { service, sends, sleeps, rows } = await rig([
    { data: null, error: { code: "E_INTERNAL_SERVER_ERROR", message: "first" } },
    { data: null, error: { code: "E_INTERNAL_SERVER_ERROR", message: "second" } },
    { data: null, error: { code: "E_INTERNAL_SERVER_ERROR", message: "third" } },
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
