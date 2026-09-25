/**
 * `emailTag` in `lib/shared/redact.ts` (#717): the value every nextjs server log
 * line writes IN PLACE OF a customer's email address.
 *
 * It must be stable across case and surrounding whitespace (so two log lines for
 * one customer correlate, and so a tag equals the `recipientHash=` that
 * `sendMagicLink` logs for the same address), short, and must never carry the
 * address itself.
 */
import assert from "node:assert/strict";
import test from "node:test";

import { emailTag, redactAddresses, redactRecipient } from "../lib/shared/redact";
import { sha256Hex } from "../lib/shared/sha256";

test("emailTag: case and surrounding whitespace do not change the tag", () => {
  assert.equal(emailTag(" Bob@X.com "), emailTag("bob@x.com"));
  assert.equal(emailTag("BOB@X.COM"), emailTag("bob@x.com"));
});

test("emailTag: known answer — first 12 hex of SHA-256 of the normalised address", () => {
  // Literal, so a change to the digest or the slice length fails here.
  assert.equal(emailTag(" Bob@X.com "), "581d5f1930c8");
});

test("emailTag: equals the magic-link recipientHash for the same address", () => {
  const raw = " Bob@X.com ";
  // The exact expression `src/lib/auth.ts` logs as `recipientHash=`.
  assert.equal(emailTag(raw), sha256Hex(raw.trim().toLowerCase()).slice(0, 12));
});

test("emailTag: is 12 lowercase hex chars, never the address itself", () => {
  for (const email of ["bob@x.com", " Bob@X.com ", "a@b.co", "first.last+tag@mail.example.com"]) {
    const tag = emailTag(email);
    // 12 hex chars cannot hold an `@`, so no address (or domain) can pass this.
    assert.match(tag, /^[0-9a-f]{12}$/);
  }
});

test("emailTag: different addresses give different tags", () => {
  assert.notEqual(emailTag("bob@x.com"), emailTag("alice@x.com"));
});

test("redactRecipient: the recipient becomes its tag in any case; the reason survives", () => {
  // Stripe hands the address padded and mixed-case; Resend echoes it lower-cased.
  assert.equal(
    redactRecipient(
      "validation_error: buyer@example.com is not a valid recipient",
      "Buyer@Example.com ",
    ),
    `validation_error: ${emailTag("buyer@example.com")} is not a valid recipient`,
  );
});

test("redactRecipient: other addresses and the recipient's domain are redacted too", () => {
  assert.equal(
    redactRecipient("The EXAMPLE.com domain is not verified for bob@other.org", "a@example.com"),
    "The [redacted] domain is not verified for [redacted]",
  );
});

// ---------------------------------------------------------------------------
// Review r2 (#717): the recipient reaches `redactRecipient` from the PUBLIC
// `recordDownload` form, whose `z.string().email()` has no length bound, and
// the text is provider prose. Each case below is a size the reviewer measured
// against the old RegExp-based redaction: an 8,000-char domain ABORTED the
// process, a 60 KB local part THREW with the address in the message, and one
// 50,000-char token took ~2.2 s. Each must now return quickly, and return text
// with no address, no domain and no `@` in it.
// ---------------------------------------------------------------------------

/** Far above the ~3 ms these take now; far below the old 2.2 s token case. */
const BUDGET_MS = 250;

function timed<T>(run: () => T): { value: T; ms: number } {
  const started = performance.now();
  const value = run();
  return { value, ms: performance.now() - started };
}

function assertBoundedAndClean(
  text: string,
  recipient: string,
  forbidden: string[],
): string {
  const domain = recipient.trim().toLowerCase().split("@")[1] ?? "";
  for (const [label, run] of [
    ["redactRecipient", () => redactRecipient(text, recipient)],
    ["redactAddresses", () => redactAddresses(text, domain)],
  ] as const) {
    const { value, ms } = timed(run);
    assert.ok(ms < BUDGET_MS, `${label} took ${ms.toFixed(1)} ms`);
    assert.ok(!value.includes("@"), `${label} left an @`);
    for (const piece of forbidden) {
      assert.ok(!value.toLowerCase().includes(piece), `${label} left ${piece.slice(0, 40)}…`);
    }
  }
  return redactRecipient(text, recipient);
}

test("redactRecipient: an 8,000-char domain returns, fast, with no domain in it", () => {
  const recipient = `a@${"x.".repeat(4000)}com`;
  const out = assertBoundedAndClean(`${recipient} is not a valid recipient`, recipient, [
    "x.x",
    "x.com",
  ]);
  // Fails closed, but the reason Resend gave survives.
  assert.equal(out, "[redacted] is not a valid recipient");
});

test("redactRecipient: a 60 KB local part neither throws nor leaks, and keeps the reason", () => {
  const recipient = `${"alice.smith".repeat(5500)}@acme-widgets.com`;
  const out = assertBoundedAndClean(`${recipient} is not a valid recipient`, recipient, [
    "alice.smith",
    "acme-widgets.com",
  ]);
  assert.equal(out, "[redacted] is not a valid recipient");
});

test("redactRecipient: one 50,000-char token is linear, and an address after it is redacted", () => {
  const out = assertBoundedAndClean(
    `${"x".repeat(50_000)} message=bob@acme-widgets.com is not a valid recipient`,
    "bob@acme-widgets.com",
    ["bob@", "acme-widgets.com"],
  );
  assert.ok(out.includes("[truncated "), "text past the cap is truncated, after redaction");
});

test("redactRecipient: 1 MB of recipient and of text is still bounded and clean", () => {
  const recipient = `${"a".repeat(1_000_000)}@acme-widgets.com`;
  assertBoundedAndClean(`${recipient} is not a valid recipient`, recipient, [
    "aaaaaaaaaa@",
    "acme-widgets.com",
  ]);
  assertBoundedAndClean(`name=x ${"y.".repeat(500_000)}`, "bob@acme-widgets.com", ["y.y"]);
});

test("redactAddresses: at the size caps the PRECISE path runs, fast, on hostile shapes", () => {
  // A 253-char, 127-label domain (the most labels a 254-char address can have)
  // against 16 KiB of text that nearly matches it everywhere.
  const domain = `${"a.".repeat(126)}a`;
  const text = `${"a.".repeat(60)}b.`.repeat(134).slice(0, 16 * 1024);
  const { value, ms } = timed(() => redactAddresses(text, domain));
  assert.ok(ms < BUDGET_MS, `took ${ms.toFixed(1)} ms`);
  assert.ok(!value.includes("a.a"), "every candidate occurrence is redacted");
  // Under the caps this is the precise path, not the fail-closed one: `b` and
  // the dots around it are kept.
  assert.ok(value.includes("b."), value.slice(0, 80));
});

test("redactAddresses: the fail-closed form keeps the key= diagnosis of a structured line", () => {
  const line = `sendMagicLink failed: resend error name=validation_error statusCode=422 recipientHash=0123456789ab message=${"a".repeat(20_000)}@corp.com is not a valid recipient.`;
  assert.equal(
    redactAddresses(line, "corp.com"),
    "sendMagicLink failed: resend error name=validation_error statusCode=422 recipientHash=0123456789ab message=[redacted] is not a valid [redacted]",
  );
});
