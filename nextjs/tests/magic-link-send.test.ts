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
 * `src/lib/auth.ts` is imported once per process, so every case shares that one
 * instance and switches the stub's answer through `behaviour.result`.
 */
import assert from "node:assert/strict";
import test, { after, mock } from "node:test";

import { sha256Hex } from "../lib/shared/sha256";

const RECIPIENT = "magic-link-probe@example.com";
const MAGIC_URL = "https://x.test/magic";

/**
 * The same recipient as a phone keyboard with auto-capitalisation and a trailing
 * space would post it. Nothing between the input and `sendMagicLink` normalises:
 * `SignInClient.tsx` stores the field verbatim, better-auth's magic-link route
 * forwards `ctx.body.email` unchanged, and `z.email()` does not lowercase.
 */
const MIXED_CASE_RECIPIENT = " Magic-Link-Probe@Example.COM ";

/**
 * KNOWN-ANSWER digests. Every hex below was computed outside this process and
 * outside `sha256Hex`, by two tools that agree:
 *
 *   $ printf '%s' 'magic-link-probe@example.com' | sha256sum
 *   $ printf '%s' 'magic-link-probe@example.com' | openssl dgst -sha256
 *
 * Nothing here derives an expectation by calling the helper under test. That is
 * the point: the first version of this file built `expectedHash` from
 * `sha256Hex(RECIPIENT)`, so both sides of the comparison moved together and
 * adding `value.trim().toLowerCase()` inside `sha256Hex` — exactly what its doc
 * comment forbids — kept the suite green.
 */
const HASH12 = {
  /** sha256(" Foo@Example.COM "), full digest — whitespace and case preserved. */
  paddedMixedFoo:
    "4b78f42241fe890c89ea150dc4ceb63c72ddcafb6b6e1677199d9e91a2d1b5ce",
  /** sha256("foo@example.com"), full digest. */
  normalisedFoo:
    "321ba197033e81286fedb719d60d4ed5cecaed170733cb4a92013811afc0e3b6",
  /** sha256("magic-link-probe@example.com").slice(0, 12) — what the log logs. */
  recipient: "a24e076e7409",
  /** sha256(" Magic-Link-Probe@Example.COM ").slice(0, 12) — must NOT appear. */
  recipientUnnormalised: "7ffd45c49905",
} as const;

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
      // A message that carries no address of its own, so the assertions below
      // are about the log FORMAT: the hash, the fields, and the fact that
      // `message` is there at all. The case where Resend's own text echoes the
      // recipient — which is what a real `validation_error` does — is the next
      // test, and that is where "no address in the log" is really proved.
      message: "The from address is not a verified domain",
    },
  };

  await assert.rejects(
    () => sendMagicLink({ email: RECIPIENT, url: MAGIC_URL }),
    (err: unknown) => {
      assert.ok(err instanceof Error, "rejects with an Error");
      assert.match(err.message, /Failed to send the magic-link email/);
      // better-auth's APIError, not a plain Error, and 503 rather than 500.
      // A plain Error falls past `isAPIError` in better-call's router and
      // becomes `new Response(null, { status: 500 })` — an EMPTY body, so
      // better-fetch parses it to `null`, `authError.message` is `undefined`
      // and SignInClient.tsx can only ever show its generic fallback. An
      // APIError is turned into `{"message": ...}` at its own status, so this
      // message is what the user actually reads. 503 is also the status
      // better-auth does not re-log, which is what keeps the record count at
      // the one asserted below.
      assert.equal(err.name, "APIError", "throws better-auth's APIError");
      assert.equal(
        (err as { statusCode?: unknown }).statusCode,
        503,
        "a provider that could not be reached is 503, not 500",
      );
      // The thrown message goes to the browser, so no address in it either.
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

  // Resend's `message` is the only field that usually says what actually
  // failed, and the implementation calls it load-bearing. Assert it, with the
  // `message=` key, or it can be dropped later in silence: `/sendMagicLink/`
  // matches the prefix, `/422/` matches `statusCode=422`, and a
  // `recipientHash=...\b` regex still matches at end-of-string.
  assert.ok(
    line.includes("message=The from address is not a verified domain"),
    `the log line must carry Resend's own message: ${line}`,
  );

  // The recipient is present only as a 12-char digest prefix, pinned as a
  // literal known answer (see HASH12) rather than recomputed from the helper.
  assert.match(line, new RegExp(`recipientHash=${HASH12.recipient}\\b`));
  assert.ok(
    !line.includes(RECIPIENT),
    `the log line must not contain the recipient address: ${line}`,
  );
  assert.ok(
    !line.includes("example.com"),
    `the log line must not contain the recipient domain: ${line}`,
  );
});

test("a Resend message that echoes the recipient still reaches the log with no address in it", async () => {
  const sendMagicLink = await loadSendMagicLink();

  sendCalls.length = 0;
  logLines.length = 0;
  behaviour.result = {
    data: null,
    error: {
      name: "validation_error",
      statusCode: 422,
      // This is the shape the OTHER test deliberately avoids, and it is the
      // shape a real Resend `validation_error` takes. `message` is
      // provider-controlled text, it is interpolated into the log line, and
      // Resend echoes the rejected recipient in it — so the address arrived a
      // few characters from the hash whose whole purpose is to avoid it, and
      // the "never the address itself" contract did not hold. The other test's
      // `!line.includes(RECIPIENT)` was vacuous evidence because its stub
      // message carries no address at all. This one is the real assertion.
      message: `Invalid \`to\` field. The email address ${RECIPIENT} is not a valid recipient, and example.com is not a verified domain.`,
    },
  };

  await assert.rejects(() =>
    sendMagicLink({ email: RECIPIENT, url: MAGIC_URL }),
  );

  assert.equal(
    logLines.length,
    1,
    `expected one log line, got ${logLines.length}`,
  );
  const line = logLines[0];

  // The address, the domain, and — as a structural invariant that survives any
  // future field being added to this line — any `@` at all.
  assert.ok(
    !line.includes(RECIPIENT),
    `the recipient must not survive into the log line: ${line}`,
  );
  assert.ok(
    !line.includes("example.com"),
    `the recipient domain must not survive, even standalone: ${line}`,
  );
  assert.ok(
    !line.includes("@"),
    `nothing address-shaped may survive into the log line: ${line}`,
  );
  assert.ok(
    line.includes("[redacted]"),
    `the redaction must actually have fired: ${line}`,
  );

  // Redaction must not cost the diagnostics. The hash, the status, the Resend
  // error name and the part of the message that is not an address all survive.
  assert.match(line, new RegExp(`recipientHash=${HASH12.recipient}\\b`));
  assert.match(line, /statusCode=422/);
  assert.match(line, /name=validation_error/);
  assert.ok(
    line.includes("is not a valid recipient"),
    `the non-address part of Resend's message must survive: ${line}`,
  );
});

test("the logged hash is of the NORMALISED address, and the envelope is not rewritten", async () => {
  const sendMagicLink = await loadSendMagicLink();

  sendCalls.length = 0;
  logLines.length = 0;
  behaviour.result = {
    data: null,
    error: {
      name: "rate_limit_exceeded",
      statusCode: 429,
      message: "Slow down",
    },
  };

  await assert.rejects(() =>
    sendMagicLink({ email: MIXED_CASE_RECIPIENT, url: MAGIC_URL }),
  );

  const line = logLines[0];

  // Support has to be able to reproduce the digest from the address the user
  // reports. Padding and capitalisation must therefore fall out before the
  // hash, or one incident reads as two users.
  assert.match(line, new RegExp(`recipientHash=${HASH12.recipient}\\b`));
  assert.ok(
    !line.includes(HASH12.recipientUnnormalised),
    `the hash must not be of the raw, unnormalised address: ${line}`,
  );

  // Normalisation is for the hash and the redaction only. An email local-part
  // is case-sensitive per RFC 5321, so the envelope keeps the address as given.
  assert.equal(sendCalls.length, 1);
  assert.equal(sendCalls[0].to, MIXED_CASE_RECIPIENT);
});

test("sha256Hex is a pure digest: no trim, no lowercase, no normalisation", () => {
  // Two known answers, neither computed from the helper. If a later tidy-up
  // adds `value.trim().toLowerCase()` inside `sha256Hex`, the first assertion
  // fails — which is the enforcement its doc comment promises and did not have.
  assert.equal(sha256Hex(" Foo@Example.COM "), HASH12.paddedMixedFoo);
  assert.equal(sha256Hex("foo@example.com"), HASH12.normalisedFoo);
  assert.notEqual(
    HASH12.paddedMixedFoo,
    HASH12.normalisedFoo,
    "the two inputs must have different digests or this proves nothing",
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
