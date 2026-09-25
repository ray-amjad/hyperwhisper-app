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

import { emailTag, redactRecipient } from "../lib/shared/redact";
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

test("emailTag: is 12 lowercase hex chars and never contains the address", () => {
  for (const email of ["bob@x.com", " Bob@X.com ", "a@b.co", "first.last+tag@mail.example.com"]) {
    const tag = emailTag(email);
    assert.match(tag, /^[0-9a-f]{12}$/);
    assert.ok(!tag.includes("@"));
    assert.ok(!tag.toLowerCase().includes(email.trim().toLowerCase().split("@")[0]!));
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
