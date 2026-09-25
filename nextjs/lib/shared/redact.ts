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
// Provider prose. Moved here from `src/lib/auth.ts` (#736) so the magic-link
// send and `lib/services/email.ts` share one redaction (#717).
//
// No RegExp in this section is built from its input, and every pass is linear
// in the input (review r2, #717). The recipient reaches `redactRecipient` from
// the PUBLIC `recordDownload` form, whose `z.string().email()` has no length
// bound, and the text is provider prose; so a pattern built from either one,
// or a pattern that backtracks, is attacker-sized work. Measured on the old
// shape: an 8,000-char domain built a ~19 MB alternation and ABORTED the Node
// process (`RegExpCompiler` OOM, uncatchable); a 60 KB local part threw
// `Regular expression too large` with the address in the error text; and a
// 50,000-char token cost ~2.2 s in the `[^\s=]*@\S*` pattern alone.
// ---------------------------------------------------------------------------

/** Written in place of anything in a log line that could be an address. */
const REDACTED = "[redacted]";

/**
 * The longest text the precise redaction will scan. Longer text fails CLOSED
 * through `redactCoarse` — every token holding an `@` or a `.` redacted, then
 * the result truncated to this length.
 *
 * Every pass below is linear, so this bound is not what keeps the CPU down; it
 * is a second line of defence and a cap on how much provider prose one log line
 * can carry. A real Resend `message` is well under 1 KiB.
 */
const MAX_TEXT_LENGTH = 16 * 1024;

/**
 * The longest recipient the precise redaction accepts: RFC 5321's 254-char
 * limit on an address in a forward-path, and so on its domain too. No mail
 * server delivers to anything longer, so a longer "recipient" is hostile or
 * broken input, and the text it came with fails CLOSED through `redactCoarse`.
 */
const MAX_ADDRESS_LENGTH = 254;

/**
 * Anything holding an `@`, out to the surrounding whitespace — but never across
 * a `=`. Applied per whitespace-separated token by `redactAddressShaped`.
 *
 * Deliberately greedy on the right and imprecise. The job is not to parse an
 * address correctly, it is to make sure nothing address-shaped survives:
 * over-redacting a log line is harmless, under-redacting is the bug (#736
 * forbids the address).
 *
 * The left side stops at `=`, and that is not cosmetic. This runs over the
 * WHOLE assembled `key=value` line, so a left side that ran to the whitespace
 * swallowed the KEY whenever Resend's text STARTED with the address:
 *
 *   message=alice@corp.com is not a valid recipient
 *        -> [redacted] is not a valid recipient
 *
 * `message=` was gone, so an operator or a log parser grepping `message=` for
 * the reason a send failed found nothing, and `name=` was exposed to the same
 * loss whenever Resend's error name was address-shaped. The structured shape of
 * this line is the point of it; the redaction must not eat the structure.
 *
 * A bare `@` and an `@` with nothing before it are matched too. Every `@` in the
 * line is therefore inside some redaction, which is what makes "no `@`
 * survives" a property of the mechanism instead of a property of the wordings
 * we happened to test.
 *
 * The cost of excluding `=`: an address whose LOCAL-PART contains a `=` (legal
 * per RFC 5322) leaves the text up to its last `=` behind, so `a=b@corp.com`
 * redacts to `a=[redacted]`. What survives is a fragment of a local part, which
 * this function already declines to redact on its own — see below.
 *
 * This is exactly what the old `[^\s=]*@\S*` pattern (global) replaced, done as ONE linear
 * token scan: that pattern retried its `[^\s=]*` from every start position of a
 * long token, which is quadratic (review r2).
 */
function redactTokens(line: string, markers: readonly string[]): string {
  // `\S+` is a fixed pattern with no backtracking between tokens: linear.
  return line.replace(/\S+/g, (token) => {
    let first = -1;
    for (const marker of markers) {
      const at = token.indexOf(marker);
      if (at !== -1 && (first === -1 || at < first)) first = at;
    }
    if (first === -1) return token;
    // Keep the token up to its last `=` before the marker (the `key=`).
    const keep = token.lastIndexOf("=", first) + 1;
    return token.slice(0, keep) + REDACTED;
  });
}

function redactAddressShaped(line: string): string {
  return redactTokens(line, ["@"]);
}

/**
 * The fail-CLOSED form, for input too large or too odd to redact precisely.
 *
 * Redacts every token holding an `@` OR a `.` — every address, and every
 * domain of 2 or more labels, whatever the recipient was — and only THEN cuts
 * the result to `MAX_TEXT_LENGTH`. That order matters twice: the cut can only
 * land in text that is already clean, so it cannot leave a fragment of an
 * address behind; and an oversized address at the front of the text collapses
 * to `[redacted]` instead of pushing the reason after it past the cut
 * ("[redacted] is not a valid recipient" survives a 60 KB local part). The
 * `key=` tokens of a structured line carry no `.`, so `name=`, `statusCode=`
 * and `recipientHash=` survive too: the diagnosis is kept, no address is.
 *
 * One linear pass. It cannot throw on any string that exists; the `catch` is
 * for an allocation failure, and still returns something safe to log.
 */
function redactCoarse(text: string): string {
  try {
    const redacted = redactTokens(text, ["@", "."]);
    if (redacted.length <= MAX_TEXT_LENGTH) return redacted;
    return `${redacted.slice(0, MAX_TEXT_LENGTH)} [truncated ${redacted.length - MAX_TEXT_LENGTH} chars]`;
  } catch {
    return REDACTED;
  }
}

/**
 * Lower-case `value` WITHOUT changing its length, so an index into the result is
 * an index into `value`. `toLowerCase` keeps the length for everything except a
 * handful of code points (`İ` becomes 2 code units); only then is it done per
 * code unit, leaving any such character as it was.
 */
function foldCase(value: string): string {
  const lowered = value.toLowerCase();
  if (lowered.length === value.length) return lowered;

  let folded = "";
  for (let i = 0; i < value.length; i += 1) {
    const unit = value[i].toLowerCase();
    folded += unit.length === 1 ? unit : value[i];
  }
  return folded;
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
 * `line` with the recipient's domain, and every parent of it at or above the
 * label floor, replaced by `[redacted]`, in any case.
 *
 * The parents are the fix for a subdomained recipient. Resend answers by naming
 * the domain it actually checked rather than the one that was submitted: for
 * `alice@mail.corp.com` the reply is "The corp.com domain is not verified.",
 * which carries no `@` for the token pass to catch and is not the literal
 * `mail.corp.com` an exact-match pass searches for.
 *
 * How, without a pattern built from the domain: every candidate (`mail.corp.com`,
 * `corp.com`) ENDS with the shortest one (`corp.com`), so every occurrence of a
 * candidate contains an occurrence of the shortest. Find each occurrence of the
 * shortest with `indexOf`, then extend it LEFT one label at a time while the
 * text before it spells the domain's next label and a `.`, so the full domain
 * wins and no `mail.` is left stranded in front of a redaction. The extension
 * never reaches back past the end of the previous redaction, so the work is
 * linear in the line, however many labels the domain has.
 *
 * Replacing every occurrence of the shortest candidate is what guarantees that
 * no candidate survives. (For a domain that repeats its own labels —
 * `b.a.b.a` — this can split one match the old alternation made into two
 * adjacent redactions. Both are redacted either way.)
 */
function redactDomain(line: string, recipientDomain: string): string {
  const labels = foldCase(recipientDomain).split(".");

  // No candidate at all: an empty domain (a recipient with no `@`) or a
  // single-label one. The early return is load-bearing, not tidiness — there
  // is nothing to search for, and an empty needle matches between every
  // character of the line.
  if (labels.length < MIN_DOMAIN_LABELS) return line;

  const shortest = labels.slice(-MIN_DOMAIN_LABELS).join(".");
  const folded = foldCase(line);

  let out = "";
  let done = 0;
  for (let at = folded.indexOf(shortest); at !== -1; at = folded.indexOf(shortest, done)) {
    let start = at;
    for (let label = labels.length - MIN_DOMAIN_LABELS - 1; label >= 0; label -= 1) {
      const piece = `${labels[label]}.`;
      const from = start - piece.length;
      if (from < done || !folded.startsWith(piece, from)) break;
      start = from;
    }
    out += line.slice(done, start) + REDACTED;
    done = at + shortest.length;
  }
  return out + line.slice(done);
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
 *   - it returns, and does not throw; its work is linear in the input;
 *   - no `@` survives anywhere in the returned line;
 *   - neither the recipient's domain nor any parent of it down to the label
 *     floor survives, in any case, whether or not it arrived attached to an `@`;
 *   - the `key=` tokens of this line's own format survive, so the line stays
 *     greppable.
 *
 * A line longer than `MAX_TEXT_LENGTH`, or a domain longer than
 * `MAX_ADDRESS_LENGTH`, fails CLOSED through `redactCoarse`: every token with an
 * `@` or a `.` redacted, and the result truncated. So does any unexpected throw.
 *
 * What it does NOT guarantee, and cannot: Resend owns the `message` text, so it
 * may name the recipient in a form nothing here matches — the local part on
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
  if (line.length > MAX_TEXT_LENGTH || recipientDomain.length > MAX_ADDRESS_LENGTH) {
    return redactCoarse(line);
  }
  try {
    return redactDomain(redactAddressShaped(line), recipientDomain);
  } catch {
    return redactCoarse(line);
  }
}

/**
 * `text` with every occurrence of `address` (compared case-insensitively)
 * written as `replacement`. An `indexOf` loop, not a RegExp: the address is
 * caller-controlled, and a pattern built from it is attacker-sized work.
 */
function replaceIgnoringCase(text: string, address: string, replacement: string): string {
  const needle = foldCase(address);
  if (needle === "") return text;
  const folded = foldCase(text);

  let out = "";
  let done = 0;
  for (let at = folded.indexOf(needle); at !== -1; at = folded.indexOf(needle, done)) {
    out += text.slice(done, at) + replacement;
    done = at + needle.length;
  }
  return out + text.slice(done);
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
 * its guarantees (it returns, linearly; no `@` survives, nor the recipient's
 * domain) hold here too.
 *
 * A recipient longer than `MAX_ADDRESS_LENGTH` or text longer than
 * `MAX_TEXT_LENGTH` fails CLOSED through `redactCoarse` — no tag, no address,
 * no domain. The caller keeps its own diagnosis (`ResendSendError` carries
 * Resend's `name` and `statusCode` beside this message).
 */
export function redactRecipient(text: string, recipient: string): string {
  const address = recipient.trim();
  if (text.length > MAX_TEXT_LENGTH || address.length > MAX_ADDRESS_LENGTH) {
    return redactCoarse(text);
  }
  try {
    const tagged = address.includes("@")
      ? replaceIgnoringCase(text, address, emailTag(address))
      : text;
    return redactAddresses(tagged, address.toLowerCase().split("@")[1] ?? "");
  } catch {
    return redactCoarse(text);
  }
}
