/**
 * The magic-link send seam: `sendMagicLink` in `src/lib/auth.ts` (#736).
 *
 * The callback is an inline closure inside `betterAuth({ plugins: [magicLink(
 * ...)] })`, so it is reached the way Better Auth itself holds it — the plugin
 * retains the supplied options object, and
 * `auth.options.plugins.find((p) => p.id === "magic-link").options.sendMagicLink`
 * IS the closure under test. Nothing is re-implemented here.
 *
 * ONE module is mocked: `lib/clients/resend.ts`. That is the whole seam. It
 * also keeps `src/env/server.mjs` out of the graph (the real resend client is
 * its only route in from here), so no env var has to be set — not even
 * `SKIP_ENV_VALIDATION`. `@/src/db` is NOT mocked and does not need to be:
 * `drizzle()` tolerates an undefined connection URL at construction and
 * `drizzleAdapter` issues no query, and this test never makes one.
 *
 * `src/lib/auth.ts` is imported once per process, so both cases share that one
 * instance and switch the stub's answer through `behaviour.result`.
 */
import assert from "node:assert/strict";
import test, { after, mock } from "node:test";

import { sha256Hex } from "../lib/shared/sha256";

const RECIPIENT = "magic-link-probe@example.com";
const MAGIC_URL = "https://x.test/magic";

interface SendPayload {
  from: string;
  to: string;
  subject: string;
  html: string;
  text: string;
}

/** Every payload the callback handed to Resend, in call order. */
const sendCalls: SendPayload[] = [];

/** What the stubbed `resend.emails.send` resolves to. Each test sets it. */
const behaviour = {
  result: { data: null, error: null } as unknown,
};

/**
 * Everything the callback logged through `console.error`, in call order.
 *
 * Only `console.error` is recorded. `betterAuth()` emits one `console.warn`
 * ("Base URL is not set", because `BETTER_AUTH_URL` is unset in a test
 * process) and zero `console.error`, so the count below belongs to the
 * callback alone — but do not start recording `warn` here or it will not.
 */
const logLines: string[] = [];
const realConsoleError = console.error;

console.error = (...args: unknown[]): void => {
  logLines.push(args.map((a) => String(a)).join(" "));
};

after(() => {
  console.error = realConsoleError;
});

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

// Keyed on the REAL file, so the registry key matches even though `auth.ts`
// imports it as `@/lib/clients/resend` — the same trick
// `tests/license-routes-harness.ts` uses for `lib/rate-limit.ts`. Both named
// exports are needed: `auth.ts:8` imports `resend` AND `DEFAULT_FROM_EMAIL`.
moduleMock.module(moduleUrl("../lib/clients/resend.ts"), {
  namedExports: {
    resend: {
      emails: {
        send: async (payload: SendPayload): Promise<unknown> => {
          sendCalls.push(payload);
          return behaviour.result;
        },
      },
    },
    DEFAULT_FROM_EMAIL: "HyperWhisper <support@hyperwhisper.com>",
  },
});

// A VARIABLE specifier, and deferred: a static import would bind before the
// `mock.module` call above, and a literal `.ts` specifier is a TS5097 error
// under this tsconfig.
const AUTH_PATH = "../src/lib/auth.ts";

type SendMagicLink = (args: { email: string; url: string }) => Promise<void>;

/** The real inline closure, off the real plugin, as Better Auth holds it. */
async function loadSendMagicLink(): Promise<SendMagicLink> {
  const { auth } = await import(AUTH_PATH);
  const plugins = (auth as { options: { plugins: Array<{ id: string }> } })
    .options.plugins;
  const magicLinkPlugin = plugins.find((p) => p.id === "magic-link") as
    | { id: string; options?: { sendMagicLink?: SendMagicLink } }
    | undefined;

  assert.ok(magicLinkPlugin, "the magic-link plugin is registered");
  const send = magicLinkPlugin.options?.sendMagicLink;
  assert.equal(
    typeof send,
    "function",
    "the magic-link plugin retained the sendMagicLink callback",
  );

  return send as SendMagicLink;
}

test("a Resend error object rejects the send and logs it without the address", async () => {
  const sendMagicLink = await loadSendMagicLink();

  sendCalls.length = 0;
  logLines.length = 0;
  behaviour.result = {
    data: null,
    error: {
      name: "validation_error",
      statusCode: 422,
      // The message deliberately does NOT echo the recipient. A real Resend
      // validation_error often does, and the log line interpolates `message`,
      // so an address-bearing stub would fail the "no recipient in the log"
      // assertion below for a reason that has nothing to do with the fix.
      message: "The from address is not a verified domain",
    },
  };

  await assert.rejects(
    () => sendMagicLink({ email: RECIPIENT, url: MAGIC_URL }),
    (err: unknown) => {
      assert.ok(err instanceof Error, "rejects with an Error");
      assert.match(err.message, /Failed to send the magic-link email/);
      // Better Auth surfaces a thrown message toward the client, so the
      // address must not be in it either.
      assert.ok(
        !err.message.includes(RECIPIENT),
        `the thrown message must not contain the recipient: ${err.message}`,
      );
      return true;
    },
    "the callback rejects instead of resolving on a Resend error object",
  );

  // Exactly one line — no "attempting send", nothing extra on the throw path.
  assert.equal(
    logLines.length,
    1,
    `expected one log line, got ${logLines.length}`,
  );
  const line = logLines[0];
  assert.match(line, /sendMagicLink/);
  assert.match(line, /422/);
  assert.match(line, /validation_error/);

  // The recipient is present only as a 12-char digest prefix. Asserted through
  // the real helper so this can never drift from the implementation.
  const expectedHash = sha256Hex(RECIPIENT).slice(0, 12);
  assert.equal(expectedHash.length, 12);
  assert.match(line, new RegExp(`recipientHash=${expectedHash}\\b`));
  assert.ok(
    !line.includes(RECIPIENT),
    `the log line must not contain the recipient address: ${line}`,
  );
  assert.ok(
    !line.includes("example.com"),
    `the log line must not contain the recipient domain: ${line}`,
  );
});

test("a successful send resolves quietly with the real template payload", async () => {
  const sendMagicLink = await loadSendMagicLink();

  sendCalls.length = 0;
  logLines.length = 0;
  behaviour.result = { data: { id: "resend_msg_1" }, error: null };

  await sendMagicLink({ email: RECIPIENT, url: MAGIC_URL });

  assert.equal(logLines.length, 0, "the happy path logs nothing");
  assert.equal(sendCalls.length, 1);

  const payload = sendCalls[0];
  assert.equal(payload.from, "HyperWhisper <support@hyperwhisper.com>");
  assert.equal(payload.to, RECIPIENT);
  assert.equal(payload.subject, "Sign in to HyperWhisper");
  assert.match(payload.html, /https:\/\/x\.test\/magic/);
  assert.match(payload.text, /https:\/\/x\.test\/magic/);
});
