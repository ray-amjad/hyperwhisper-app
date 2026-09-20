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
 * A SUBDOMAINED recipient. Resend answers one of these by naming the domain it
 * actually checked — the parent, `example.com` — not the `mail.example.com` that
 * was submitted, so `redactAddresses` has to redact the parents too.
 */
const SUBDOMAIN_RECIPIENT = "magic-link-probe@mail.example.com";

/**
 * A recipient with no `@` at all, which leaves `redactAddresses` with an empty
 * domain and therefore no domain candidates.
 *
 * NOT reachable through the route — `z.email()` rejects it (see
 * `PADDED_RECIPIENT`). It is here for the same defence-in-depth reason the
 * implementation guards the case: the guard it exercises is what stops an empty
 * alternation regex from shredding the whole log line.
 */
const NO_DOMAIN_RECIPIENT = "magic-link-probe";

/**
 * The same recipient as a phone keyboard with auto-capitalisation would post it.
 *
 * This one IS reachable. Nothing between the input and `sendMagicLink`
 * lowercases: `SignInClient.tsx` stores the field verbatim, better-auth's
 * magic-link route forwards `ctx.body.email` unchanged, and the `z.email()` in
 * its `signInMagicLinkBodySchema`
 * (`better-auth/dist/plugins/magic-link/index.mjs`) validates without
 * normalising — measured against the installed zod 4.6.2,
 * `z.email().safeParse("Magic-Link-Probe@Example.COM")` succeeds and returns the
 * string unchanged.
 */
const MIXED_CASE_RECIPIENT = "Magic-Link-Probe@Example.COM";

/**
 * The same address with surrounding whitespace. This is DEFENCE IN DEPTH, not a
 * scenario a user reaches.
 *
 * An earlier version of this file claimed a phone keyboard "would post" a padded
 * address. That is false: the same `z.email()` REJECTS padding — measured on the
 * installed zod 4.6.2, `" Magic-Link-Probe@Example.COM "`,
 * `"alice@example.com "` and `" alice@example.com"` all fail `safeParse`, so the
 * route answers 400 and the callback never runs.
 *
 * The `.trim()` in the implementation is kept anyway, because `sendMagicLink` is
 * a plain callback that any future caller can reach without that schema in
 * front of it, and a padded address would otherwise hash to a digest support
 * cannot reproduce. This test pins the trim; it does not claim a user gets here.
 */
const PADDED_RECIPIENT = " Magic-Link-Probe@Example.COM ";

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
  /** sha256("Magic-Link-Probe@Example.COM").slice(0, 12) — must NOT appear. */
  recipientUnlowercased: "737764855113",
  /** sha256("magic-link-probe@mail.example.com").slice(0, 12). */
  subdomainRecipient: "75e47db778cd",
  /** sha256("magic-link-probe").slice(0, 12) — the no-`@` recipient. */
  noDomainRecipient: "df764bd733cf",
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

const RATE_LIMIT_ERROR = {
  name: "rate_limit_exceeded",
  statusCode: 429,
  message: "Slow down",
};

test("the logged hash is of the LOWERCASED address, and the envelope is not rewritten", async () => {
  const sendMagicLink = await loadSendMagicLink();

  sendCalls.length = 0;
  logLines.length = 0;
  behaviour.result = { data: null, error: RATE_LIMIT_ERROR };

  // The reachable case: a phone keyboard's auto-capitalisation. `z.email()`
  // accepts this exact string and passes it through unchanged, so it really
  // does arrive at the callback.
  await assert.rejects(() =>
    sendMagicLink({ email: MIXED_CASE_RECIPIENT, url: MAGIC_URL }),
  );

  const line = logLines[0];

  // Support has to be able to reproduce the digest from the address the user
  // reports, so capitalisation must fall out before the hash or one incident
  // reads as two users.
  assert.match(line, new RegExp(`recipientHash=${HASH12.recipient}\\b`));
  assert.ok(
    !line.includes(HASH12.recipientUnlowercased),
    `the hash must not be of the as-typed, mixed-case address: ${line}`,
  );

  // Normalisation is for the hash and the redaction only. An email local-part
  // is case-sensitive per RFC 5321, so the envelope keeps the address as given.
  assert.equal(sendCalls.length, 1);
  assert.equal(sendCalls[0].to, MIXED_CASE_RECIPIENT);
});

test("defence in depth: a padded address the route would reject still hashes normalised", async () => {
  const sendMagicLink = await loadSendMagicLink();

  sendCalls.length = 0;
  logLines.length = 0;
  behaviour.result = { data: null, error: RATE_LIMIT_ERROR };

  // NOT a user-reachable input — `z.email()` rejects the padding and the route
  // answers 400 before this callback runs (see PADDED_RECIPIENT). This pins the
  // implementation's `.trim()` for a future caller that reaches `sendMagicLink`
  // without that schema in front of it, and nothing more than that.
  await assert.rejects(() =>
    sendMagicLink({ email: PADDED_RECIPIENT, url: MAGIC_URL }),
  );

  const line = logLines[0];

  assert.match(line, new RegExp(`recipientHash=${HASH12.recipient}\\b`));
  assert.ok(
    !line.includes(HASH12.recipientUnnormalised),
    `the hash must not be of the raw, padded address: ${line}`,
  );
  assert.equal(sendCalls[0].to, PADDED_RECIPIENT);
});

test("a message that STARTS with the address is redacted without eating the message= key", async () => {
  const sendMagicLink = await loadSendMagicLink();

  sendCalls.length = 0;
  logLines.length = 0;
  behaviour.result = {
    data: null,
    error: {
      name: "validation_error",
      statusCode: 422,
      // Resend's text begins with the address, which is the shape that broke
      // the first version of the redaction: `/\S+@\S+/g` matched leftwards
      // across the `=` and took `message=` with it, leaving a line no operator
      // and no log parser could grep for the reason a send failed.
      message: `${RECIPIENT} is not a valid recipient`,
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

  // Both halves, in one assertion: the key survived AND the redaction fired at
  // exactly the position the key introduces.
  assert.ok(
    line.includes("message=[redacted] is not a valid recipient"),
    `the message= key must survive the redaction of a leading address: ${line}`,
  );
  // The key on its own, stated separately, because that is what a log parser
  // greps for and what the regex used to destroy.
  assert.ok(
    line.includes("message="),
    `the structured message= key must still be present: ${line}`,
  );
  // And the whole point of the redaction still holds.
  assert.ok(
    !line.includes(RECIPIENT),
    `the recipient must not survive into the log line: ${line}`,
  );
  assert.ok(
    !line.includes("@"),
    `nothing address-shaped may survive into the log line: ${line}`,
  );
  // The other keys are unharmed too.
  assert.match(line, /name=validation_error/);
  assert.match(line, /statusCode=422/);
  assert.match(line, new RegExp(`recipientHash=${HASH12.recipient}\\b`));
});

test("a bare PARENT domain of a subdomained recipient is redacted, and short labels are not", async () => {
  const sendMagicLink = await loadSendMagicLink();

  sendCalls.length = 0;
  logLines.length = 0;
  behaviour.result = {
    data: null,
    error: {
      name: "validation_error",
      statusCode: 422,
      // No `@` anywhere, so the address pass cannot help: Resend names the
      // domain it actually checked, which is the PARENT of the submitted
      // `mail.example.com`. The word "recommended" is deliberate — it contains
      // the bare label `com`, so it also pins the label floor. If the floor
      // dropped to one label, `com` would be redacted out of ordinary provider
      // prose and this would read `re[redacted]mended`.
      message:
        "The example.com domain is not verified. Verifying a domain is recommended before you send.",
    },
  };

  await assert.rejects(() =>
    sendMagicLink({ email: SUBDOMAIN_RECIPIENT, url: MAGIC_URL }),
  );

  assert.equal(
    logLines.length,
    1,
    `expected one log line, got ${logLines.length}`,
  );
  const line = logLines[0];

  // The parent domain is gone — which also covers the submitted
  // `mail.example.com`, since that contains it.
  assert.ok(
    !line.includes("example.com"),
    `a bare parent domain of the recipient must not survive: ${line}`,
  );
  assert.ok(
    line.includes("The [redacted] domain is not verified"),
    `the prose around the redacted domain must survive: ${line}`,
  );

  // The label floor: an ordinary word that happens to contain a bare TLD is not
  // shredded.
  assert.ok(
    line.includes("recommended"),
    `a 1-label candidate must not be redacted out of provider prose: ${line}`,
  );

  assert.match(
    line,
    new RegExp(`recipientHash=${HASH12.subdomainRecipient}\\b`),
  );
});

test("statusCode=null is distinguished from a missing or wrong-typed statusCode", async () => {
  const sendMagicLink = await loadSendMagicLink();

  sendCalls.length = 0;
  logLines.length = 0;
  behaviour.result = {
    data: null,
    error: {
      // Resend's own network-failure path: the `catch` around `fetch` in
      // `resend/dist/index.mjs` sets exactly this shape, and the SDK types
      // `statusCode` as `number | null`.
      name: "application_error",
      statusCode: null,
      message: "Unable to fetch data. The request could not be resolved.",
    },
  };

  await assert.rejects(() =>
    sendMagicLink({ email: RECIPIENT, url: MAGIC_URL }),
  );

  const line = logLines[0];

  // "the request never left the box", not "Resend sent something this code does
  // not understand". Collapsing the two makes an unreachable provider
  // indistinguishable from a malformed error to whoever is on call.
  assert.ok(
    line.includes("statusCode=null"),
    `an explicit null statusCode must log as null: ${line}`,
  );
  assert.ok(
    !line.includes("statusCode=unknown"),
    `null must not collapse into the unknown default: ${line}`,
  );
  assert.match(line, /name=application_error/);
});

test("a non-object Resend error still rejects and logs with the fallback fields", async () => {
  const sendMagicLink = await loadSendMagicLink();

  sendCalls.length = 0;
  logLines.length = 0;
  // Not an object at all, so every `in` narrowing in `resendErrorFieldsOf`
  // would throw `Cannot use 'in' operator` without the typeof guard — taking
  // the log line and the APIError with it and rejecting with a TypeError
  // instead.
  behaviour.result = { data: null, error: "boom" };

  await assert.rejects(
    () => sendMagicLink({ email: RECIPIENT, url: MAGIC_URL }),
    (err: unknown) => {
      assert.ok(err instanceof Error);
      assert.equal(err.name, "APIError", "throws better-auth's APIError");
      return true;
    },
  );

  assert.equal(
    logLines.length,
    1,
    `expected one log line, got ${logLines.length}`,
  );
  const line = logLines[0];

  assert.ok(
    line.includes("name=unknown statusCode=unknown"),
    `both fallbacks must appear for a non-object error: ${line}`,
  );
  assert.ok(
    line.endsWith("message="),
    `the message fallback is the empty string: ${line}`,
  );
  assert.match(line, new RegExp(`recipientHash=${HASH12.recipient}\\b`));
});

test("a recipient with no domain leaves the log line intact", async () => {
  const sendMagicLink = await loadSendMagicLink();

  sendCalls.length = 0;
  logLines.length = 0;
  behaviour.result = {
    data: null,
    error: {
      name: "validation_error",
      statusCode: 422,
      message: "Invalid to field. The recipient is not an email address.",
    },
  };

  // Not reachable through the route (`z.email()` rejects it), but it is what
  // exercises the empty-candidate guard in `redactAddresses`. Without that
  // guard the domain pass builds an empty alternation, and a global empty
  // pattern makes `String.replace` insert the replacement between EVERY
  // character of the line — so the diagnostics are destroyed rather than
  // redacted.
  await assert.rejects(() =>
    sendMagicLink({ email: NO_DOMAIN_RECIPIENT, url: MAGIC_URL }),
  );

  const line = logLines[0];

  assert.match(
    line,
    /^sendMagicLink failed: resend error name=validation_error/,
  );
  assert.ok(
    line.includes(
      "message=Invalid to field. The recipient is not an email address.",
    ),
    `the whole message must survive verbatim: ${line}`,
  );
  assert.ok(
    !line.includes("[redacted]"),
    `there is nothing to redact here, so nothing may be redacted: ${line}`,
  );
  assert.match(
    line,
    new RegExp(`recipientHash=${HASH12.noDomainRecipient}\\b`),
  );
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
