import { sha256Hex } from "@/lib/shared/sha256";

/**
 * A 12-hex-character tag for an email address, safe to write to a server log.
 *
 * The address is trimmed and lower-cased first, then hashed, so the tag for a
 * given customer is the same whatever case Stripe or a form handed us — and it
 * equals the `recipientHash=` that `sendMagicLink` in `src/lib/auth.ts` logs for
 * the same address, so a purchase line and a sign-in line correlate (#717).
 *
 * `sha256Hex` itself stays a pure digest; the normalisation lives here.
 * Node-runtime server code only (`node:crypto`, via `sha256Hex`).
 */
export function emailTag(email: string): string {
  return sha256Hex(email.trim().toLowerCase()).slice(0, 12);
}

// ---------------------------------------------------------------------------
// Provider prose. Moved here unchanged from `src/lib/auth.ts` (#736) so the
// magic-link send and `lib/services/email.ts` share one redaction (#717).
// ---------------------------------------------------------------------------

/** Written in place of anything in a log line that could be an address. */
const REDACTED = "[redacted]";

/**
 * Anything holding an `@`, out to the surrounding whitespace — but never across
 * a `=`.
 *
 * Deliberately greedy on the right and imprecise. The job is not to parse an
 * address correctly, it is to make sure nothing address-shaped survives:
 * over-redacting a log line is harmless, under-redacting is the bug (#736
 * forbids the address).
 *
 * The left side stops at `=`, and that is not cosmetic. This runs over the
 * WHOLE assembled `key=value` line, so a `\S+` left side swallowed the KEY
 * whenever Resend's text STARTED with the address:
 *
 *   message=alice@corp.com is not a valid recipient
 *        -> [redacted] is not a valid recipient
 *
 * `message=` was gone, so an operator or a log parser grepping `message=` for
 * the reason a send failed found nothing, and `name=` was exposed to the same
 * loss whenever Resend's error name was address-shaped. The structured shape of
 * this line is the point of it; the redaction must not eat the structure.
 *
 * The left side is `*` rather than `+`, and the right side `\S*` rather than
 * `\S+`, so a bare `@` and an `@` with nothing before it are matched too. Every
 * `@` in the line is therefore inside some match, which is what makes "no `@`
 * survives" a property of the pattern instead of a property of the wordings we
 * happened to test.
 *
 * The cost of excluding `=`: an address whose LOCAL-PART contains a `=` (legal
 * per RFC 5322) leaves the text up to its last `=` behind, so `a=b@corp.com`
 * redacts to `a=[redacted]`. What survives is a fragment of a local part, which
 * this function already declines to redact on its own — see below.
 */
const ADDRESS_SHAPED = /[^\s=]*@\S*/g;

function escapeForRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/**
 * The fewest dot-separated labels a domain candidate may have before it is
 * allowed to be redacted out of provider prose.
 *
 * 2 is chosen deliberately. Every candidate with 2 or more labels contains a
 * `.`, so it cannot collide with an ordinary word in Resend's message. At 1
 * label the candidate is a bare TLD or hostname — `com`, `io`, `mail` — and
 * replacing that would shred the only field that usually says what actually
 * failed, which is a worse outcome than the leak it prevents.
 *
 * This floor deliberately does NOT try to find the registrable domain.
 * `alice@corp.co.uk` yields `corp.co.uk` AND `co.uk`, so a message naming
 * `co.uk` on its own is over-redacted. That is the harmless direction, and a
 * public-suffix list is a dependency this fix will not add.
 */
const MIN_DOMAIN_LABELS = 2;

/**
 * The recipient's own domain plus every parent of it at or above the label
 * floor, longest first.
 *
 * The parents are the fix for a subdomained recipient. Resend answers by naming
 * the domain it actually checked rather than the one that was submitted: for
 * `alice@mail.corp.com` the reply is "The corp.com domain is not verified.",
 * which carries no `@` for the pass above to catch and is not the literal
 * `mail.corp.com` an exact-match pass searches for. That bare-domain shape is
 * one of the two `redactAddresses` exists to handle, and it used to survive
 * verbatim.
 *
 * Longest first, because the alternation built from this list takes the first
 * branch that matches at a position — so the full domain wins and no `mail.`
 * is left stranded in front of a redaction.
 */
function domainCandidatesOf(recipientDomain: string): string[] {
  const labels = recipientDomain.split(".");
  const candidates: string[] = [];

  for (let i = 0; labels.length - i >= MIN_DOMAIN_LABELS; i += 1) {
    candidates.push(labels.slice(i).join("."));
  }

  return candidates;
}

/**
 * Strip email addresses, and the recipient's own domain, out of a finished log
 * line.
 *
 * Applied to the WHOLE assembled line rather than to one field, so "the
 * recipient reaches this log only as a hash" is a property of the mechanism and
 * survives a later edit that interpolates one more provider-controlled value.
 *
 * Two passes, because Resend's text carries either shape:
 *   - a full address — a real `validation_error` echoes the rejected recipient
 *     ("... alice@corp.com is not a valid recipient");
 *   - a bare domain — "the corp.com domain is not verified".
 *
 * What this GUARANTEES, for any input:
 *   - no `@` survives anywhere in the returned line;
 *   - neither the recipient's domain nor any parent of it down to the label
 *     floor survives, in any case, whether or not it arrived attached to an `@`;
 *   - the `key=` tokens of this line's own format survive, so the line stays
 *     greppable.
 *
 * What it does NOT guarantee, and cannot: Resend owns the `message` text, so it
 * may name the recipient in a form no pattern here matches — the local part on
 * its own ("the user alice is blocked"), a percent-encoded address
 * (`alice%40corp.com`), an address written with spaces around its `@`, or some
 * other identifier entirely. **Complete redaction of provider prose is not
 * achievable from this side**, and this comment deliberately does not claim it.
 * The guarantees above are what the two passes actually deliver.
 *
 * The recipient's LOCAL-PART is deliberately NOT redacted on its own either. It
 * does not identify anybody without a domain, and a short or common one ("a",
 * "me", "info") would shred the surrounding message.
 */
export function redactAddresses(line: string, recipientDomain: string): string {
  const withoutAddresses = line.replace(ADDRESS_SHAPED, REDACTED);

  const candidates = domainCandidatesOf(recipientDomain);

  // No candidate at all: an empty domain (a recipient with no `@`) or a
  // single-label one. The early return is load-bearing, not tidiness — joining
  // an empty list gives an empty pattern, and a global empty pattern makes
  // `String.replace` insert the replacement between every character of the
  // line.
  if (candidates.length === 0) return withoutAddresses;

  return withoutAddresses.replace(
    new RegExp(candidates.map(escapeForRegExp).join("|"), "gi"),
    REDACTED,
  );
}

/**
 * `text` with the recipient's address written as its `emailTag`, and anything
 * else address-shaped (or the recipient's domain) written as `[redacted]`.
 *
 * For provider error text that is about to leave the process: a Resend
 * `validation_error` echoes the rejected recipient ("alice@corp.com is not a
 * valid recipient"). The recipient itself becomes its tag rather than
 * `[redacted]` so the line still correlates with the `Sending … to <tag>`
 * line; everything else in the text — the reason the send failed — is kept.
 *
 * The recipient is matched after `trim()` and case-insensitively, because
 * Stripe hands us the address as typed ("Buyer@Example.com ") and Resend may
 * echo it in another case. `redactAddresses` then runs over the result, so
 * its guarantees (no `@` survives, nor the recipient's domain) hold here too.
 */
export function redactRecipient(text: string, recipient: string): string {
  const address = recipient.trim();
  const tagged = address.includes("@")
    ? text.replace(new RegExp(escapeForRegExp(address), "gi"), emailTag(address))
    : text;

  return redactAddresses(tagged, address.toLowerCase().split("@")[1] ?? "");
}
