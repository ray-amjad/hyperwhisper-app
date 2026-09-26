// LOG REDACTION — ONE BOUNDED LINE FOR A FAILURE THAT MAY QUOTE A SECRET
//
// `toRedactedLogLine` turns a caught error into a single, capped log line with
// the secrets taken out of it. It started inside `lib/redis-core.ts` (#898,
// #921) and moved here for #1029, when the Google-token cache catches in
// `lib/google-auth-core.ts` needed it too: that module has nothing to do with
// the IP block or the licence cache, and should not pull them into its module
// graph to reach one string function. Both modules import this one as peers.
//
// Pure: no I/O, no imports. Nothing `mock.module`s it, so every suite that
// reaches it through a caller exercises the real thing.

/**
 * The longest redacted failure we put on one log line. An
 * `UpstashJSONParseError` carries up to 200 characters of a proxy's raw HTML
 * body (`chunk-LLI2WIYN.mjs`: `body.slice(0, 200) + '...'`), which would
 * otherwise be the whole record. The cap is a BOUND, not a redaction: it runs
 * last, after every pass below, so it can only remove text, never uncover it.
 */
const MAX_FAILURE_LOG_CHARS = 200;

/**
 * Where a SERIALIZED command or cached value starts: a JSON array or object
 * whose first member is a string — `["get",…`, `[["set",…`, `{"isValid":…`.
 * The quote may be backslash-escaped (the value as it sits inside the command,
 * `"{\"isValid\":…}"`) or HTML-escaped (`&quot;`, a proxy page echoing the
 * request). Everything from there to the end of the line is cut. See pass 2.
 */
const SERIALIZED_PAYLOAD = /[[{](?:\s*\[)?\s*(?:\\*"|&quot;)[\s\S]*$/;

/**
 * The shortest licence key, or other by-value secret, the by-value pass will
 * substitute. The real licence format is 19 characters
 * (`HW-XXXX-XXXX-XXXX-XXXX`), but the caller passes whatever the request sent.
 * An empty string would put a marker between every character of the line, and
 * a 1-3 character value would rewrite ordinary words. Shorter values are left
 * to the key pass and the payload cut.
 */
const MIN_BY_VALUE_KEY_CHARS = 8;

/**
 * The value that follows the `google_oauth_token` key, in any shape the key can
 * reach a message: a command echoed as plain text (`set google_oauth_token
 * <TOKEN> ex 3540`), `key=value` / `key: value`, or a quoted pair whose
 * brackets were lost (`"google_oauth_token","<TOKEN>"`, backslash-escaped
 * `\"`, or HTML-escaped `&quot;`). Group 1 is the key and its separator,
 * kept; the value becomes `<redacted>`. A comma separates only a QUOTED pair,
 * so an Upstash `…google_oauth_token, command was: …` does not lose the word
 * `command`. The value stops at whitespace, a quote, `<`, `>`, `&`, `,`,
 * `]` or a backslash — none of which a Google access token contains — and
 * cannot start at `<`, so the pass is a fixed point on its own output. See
 * pass 4.
 */
const GOOGLE_TOKEN_KEY_VALUE =
  /(google_oauth_token(?:(?:\\*"|&quot;|') ?[,:=] ?(?:\\*"|&quot;|')?|[ :=]+))[^\s"'<>&,\]\\]+/g;

/**
 * A Google OAuth access token by its FORMAT: Google issues them as `ya29.`
 * followed by base64url text. This bounds the token wherever it appears —
 * after a key this module does not know, or in an encoding the key pass does
 * not parse (`%22`, `&#34;`) — for the read and delete catches, which hold
 * no token to hand over by value. The `ya29.` prefix is kept so an operator
 * can still see that a Google token was there. See pass 4.
 */
const GOOGLE_ACCESS_TOKEN = /ya29\.[A-Za-z0-9_.-]+/g;

/**
 * Is `value` an IP address rather than a word? `getClientIP` (`lib/request-id`)
 * returns the literal `'unknown'` for an off-edge 6PN peer and for a request
 * with neither `Fly-Client-IP` nor `X-Forwarded-For`, and substituting THAT as
 * a literal corrupts unrelated English in the message —
 * `ERR unknown command 'GET'` would log as `ERR <redacted-ip> command 'GET'`,
 * putting a redaction marker where an operator looks for the fault code.
 * Deliberately loose about which addresses are well-formed: a false negative
 * only skips a pass that the key pass and the payload cut already cover.
 */
function looksLikeIPAddress(value: string): boolean {
  if (/^\d{1,3}(?:\.\d{1,3}){3}$/.test(value)) return true;
  return value.includes(':') && /^[0-9a-f:.]+$/i.test(value);
}

/** The secrets a caller HOLDS and hands over for the by-value pass (pass 5). */
export interface RedactByValue {
  /** The client IP. Substituted only when it looks like an address. */
  readonly ip?: string;
  /** A licence key — the bearer credential for every request. */
  readonly licenseKey?: string;
  /**
   * Any other secret the caller holds, e.g. a minted access token. Each one
   * of at least `MIN_BY_VALUE_KEY_CHARS` characters becomes `<redacted-secret>`.
   */
  readonly secrets?: readonly string[];
}

/**
 * A failure as ONE bounded log line, with the secrets taken out of it.
 *
 * Logging the raw error would ship credentials. `@upstash/redis` builds its
 * message as `` `${body.error}, command was: ${JSON.stringify(req.body)}` ``
 * on every non-ok HTTP response, and `req.body` is what we sent. That body is
 * NOT just this caller's command: `enableAutoPipelining` defaults to `true`
 * (`chunk-LLI2WIYN.mjs`, `Redis` constructor) and `lib/redis.ts` builds
 * `new Redis({ url, token })` without overriding it, so every command issued in
 * the same tick is co-batched into one request — an `ip_blocked:` key for a
 * DIFFERENT caller's IP, a `license:` key, a `set google_oauth_token …` write,
 * and on a `cacheLicense` write the cached licence object itself. The licence
 * key is the bearer credential for every request (`middleware/auth.ts`). So
 * EVERY Upstash catch logs through this helper: `isIPBlocked` in
 * `lib/redis-core.ts` (#898 — its catch was silent before), `getCachedLicense`
 * / `cacheLicense` there too (#921 — they used to log the raw Error, measured
 * at 20 stderr lines on one 401 with the IP, the licence key and the cached
 * licence value in the clear), and the three Google-token cache catches in
 * `lib/google-auth-core.ts` (#1029 — a failed
 * `set google_oauth_token <ACCESS_TOKEN> ex …` quoted the live Google access
 * token, plus any co-batched `license:` key, back in its message).
 *
 * Each caller hands over the secrets it HOLDS in `byValue`: `isIPBlocked` its
 * `ip`, the licence functions their `licenseKey`, and the Google cache WRITE
 * its minted `access_token` in `secrets`. The Google cache read and delete
 * catches hold no token — the read failed before one arrived, and a delete
 * sends only the key — so they pass nothing, and passes 2-4 are their bound.
 * A co-batched `set google_oauth_token <TOKEN> …` can still reach THEIR
 * message, echoed as plain text by a proxy; pass 4's `google_oauth_token` key
 * pass and `ya29.` format pass are what bound it there.
 *
 * So the design is a BOUND first and redaction second — a deny-list that names
 * the secrets it knows about is what let the licence key through. In order:
 *
 * 1. Collapse ALL whitespace to single spaces. A `UpstashJSONParseError` is
 *    built from `res.text()` verbatim, so an intermediary's HTML 502 body
 *    arrives with real newlines and would split this record into ~8 in a
 *    line-oriented shipper — on exactly the outage class the log is for.
 * 2. Cut the serialized payload. Everything from the first JSON array or
 *    object that opens on a string (`SERIALIZED_PAYLOAD`) to the end becomes
 *    `<redacted>`, whatever it held. This is the pass that bounds an UNKNOWN
 *    secret: a key, a value or a caller we never thought about is gone without
 *    being named. It is anchored on the GRAMMAR of what we send, not on
 *    Upstash's wording, because the command reaches the message in more places
 *    than the `, command was:` suffix — an `UpstashJSONParseError` quoting a
 *    proxy page that echoes the request, or a JSON error whose `error` field
 *    quotes the body — and in those shapes a key-shaped pass stops at the first
 *    `"` and leaves the cached `{"isValid":…,"credits":…}` in the clear. The
 *    suffix text itself survives, so the Upstash shape still reads
 *    `…, command was: <redacted>`. Over-cutting only costs diagnostic text.
 *    It does NOT reach a command echoed as plain text (a proxy page reading
 *    `set google_oauth_token ya29.… ex 3540`): no bracket, no quote. That is
 *    what the `google_oauth_token` and `ya29.` parts of pass 4 are for, and
 *    pass 5 for a secret the caller holds.
 * 3. Cut the userinfo out of any `scheme://user:password@host` URL. The second
 *    grammar-shaped bound, and it names no secret either. Upstash gives an
 *    operator TWO connection strings for one database — a REST URL, and a
 *    `redis://default:<PASSWORD>@host:6379` URL that carries the password
 *    inline. Paste the second into `UPSTASH_REDIS_CLOUD_URL` and the real
 *    client throws `UrlError` from its constructor, INSIDE the caller's try,
 *    with the whole URL quoted back in the message (measured, v1.36.1). No
 *    other pass reaches it: there is no `, command was:` suffix, no Redis key,
 *    it is not the client IP, and the line measures 199 characters so the cap
 *    does not fire — and the password sits at the FRONT, so the cap would not
 *    help. The host is deliberately kept, or an operator cannot see WHICH url
 *    is wrong.
 * 4. Redact `ip_blocked:` and `license:` values wherever else they appear, for
 *    a message that names a key outside the command payload. Then the value
 *    after the `google_oauth_token` key (`GOOGLE_TOKEN_KEY_VALUE`) — this is
 *    what bounds a Google token in a co-batched write echoed as PLAIN TEXT
 *    (`502: set google_oauth_token ya29.… ex 3540`) in the read and delete
 *    catches, which hold no token for pass 5 — and last any `ya29.` Google
 *    access token by format (`GOOGLE_ACCESS_TOKEN`), for one that reaches the
 *    message with no key in front of it. The key pass runs first so the
 *    format pass cannot leave a half-marker for it to re-match.
 * 5. Redact the secrets the caller holds, by VALUE. This is the only pass that
 *    reaches a secret the message carries with no key and no payload around
 *    it. Each has its own gate and its own marker:
 *
 *    - `licenseKey` → `<redacted-license-key>`, e.g. an upstream 429 body
 *      `daily quota exceeded for account HW-…`. Skipped below
 *      `MIN_BY_VALUE_KEY_CHARS`, so an empty or tiny key cannot rewrite
 *      ordinary text.
 *    - each of `secrets` → `<redacted-secret>`, e.g. the minted Google
 *      access token the write catch holds, wherever the message carries it
 *      outside the key and `ya29.` shapes pass 4 knows. Same
 *      `MIN_BY_VALUE_KEY_CHARS` gate, same reason.
 *    - `ip` → `<redacted-ip>`, for an address carried some other way
 *      entirely — a DNS failure gives
 *      `TypeError: getaddrinfo ENOTFOUND 203.0.113.7.invalid`, which has no
 *      key and no command suffix. (That example is MEASURED on this runtime.
 *      The `connect ETIMEDOUT <addr>:443` shape this comment used to cite does
 *      NOT occur here: bun's fetch gives `Unable to connect. Is the computer
 *      able to access the url?` with the address only on a non-enumerable
 *      property, and Node/undici hides it on `error.cause`, which this helper
 *      never reads.) Gated on `looksLikeIPAddress` so the `'unknown'`
 *      sentinel is not substituted into English.
 *
 *    The licence key and each secret are searched for with their whitespace
 *    collapsed the way pass 1 collapsed the line (and trimmed), or a value
 *    holding a newline or a run of spaces would no longer match and would
 *    ship. The length gate applies to that collapsed needle — it is what is
 *    searched for — so padding cannot carry a short value past it.
 *
 *    These run after 1-4 because a caller's value is arbitrary text: a value
 *    that happens to be a substring of a marker (`redacted`) could rewrite
 *    part of one. Running last, such a rewrite only swaps marker text for
 *    another marker, and only the cap follows it.
 * 6. Truncate to `MAX_FAILURE_LOG_CHARS`.
 *
 * Apart from pass 5 running after 1-4 (above), no two passes depend on each
 * other's order for CORRECTNESS. Every marker a pass can WRITE INTO the line —
 * `<redacted>`, `<redacted-ip>`, `<redacted-license-key>`, `<redacted-secret>`,
 * `<redacted-credentials>`, and the `ya29.<redacted>` pass 4 forms — holds no
 * whitespace, `"`, `'`, `[`, `]`, `{`, `/`, `?`, `#` or `@`, so it matches no other pass's character class in part: a
 * later pass can only re-match one whole (which is idempotent) and can never
 * truncate one. Pass 3 re-matching its OWN output is the case that needs `@`
 * on that list, and `redis://<redacted-credentials>@host` is a fixed point.
 * Round 1's `<redacted ip>` did have a space, which is what made the old order
 * load-bearing; the hyphen removed the hazard rather than documenting it.
 * Dropping any one of 2, 3, 4 or any part of 5 re-opens a leak the tests pin,
 * so none is redundant. (`<unloggable failure>` is the catch-path return, not
 * a marker: it replaces the whole line and never meets another pass.)
 *
 * `getCachedLicense` and `cacheLicense` pass no `ip`, so their redaction is
 * passes 1-4, the licence-key part of 5, and 6. They keep their own message
 * prefixes, which existing Axiom queries match on — only the second argument
 * goes through here.
 * The licence key is REDACTED, not masked to its first/last 4 characters:
 * whether `README.md`'s masking claim is the contract is still open (#921).
 *
 * Returns a STRING, never the Error: a raw Error prints a multi-line stack,
 * and the line-oriented shipper splits that into several unrelated records.
 * The caller must keep it on ONE line too — a string argument or a
 * `JSON.stringify`'d record, never an object literal, which the runtime
 * pretty-prints across several lines.
 * NEVER throws — a logger that throws inside a catch would turn a fail-open
 * into a 500.
 */
export function toRedactedLogLine(error: unknown, byValue: RedactByValue = {}): string {
  try {
    // `String(x)`, never `` `${x}` ``. MEASURED, because round 1's note had it
    // backwards: `String(aSymbol)` does NOT throw — it has an explicit Symbol
    // case and gives `Symbol(x)` — while `` `${aSymbol}` `` throws
    // `Cannot convert a symbol to a string`. `String()` DOES still throw on an
    // object with a null prototype, which is what the catch below is for.
    const raw = error instanceof Error ? `${error.name}: ${error.message}` : String(error);

    let line = raw.replace(/\s+/g, ' ').trim();
    line = line.replace(SERIALIZED_PAYLOAD, '<redacted>');
    // The class stops at the characters RFC 3986 says end an authority, so the
    // userinfo it can eat is only ever real userinfo. Without `/` this eats a
    // module path — `file:///…/node_modules/@upstash/redis/nodejs.mjs` becomes
    // `file://<redacted-credentials>@upstash/redis/nodejs.mjs` — and without
    // `?` it eats a query string up to an `@` in a parameter value. Both are
    // ordinary diagnostic text, and neither is a credential.
    line = line.replace(
      /([a-z][a-z0-9+.-]*:\/\/)[^@\s"'/?#\]]*@/gi,
      '$1<redacted-credentials>@'
    );
    line = line
      .replace(/ip_blocked:[^"'\s\]]*/g, 'ip_blocked:<redacted>')
      .replace(/license:[^"'\s\]]*/g, 'license:<redacted>')
      .replace(GOOGLE_TOKEN_KEY_VALUE, '$1<redacted>')
      .replace(GOOGLE_ACCESS_TOKEN, 'ya29.<redacted>');
    const { ip, licenseKey, secrets = [] } = byValue;
    // Pass 1 collapsed the LINE's whitespace, so a secret holding a newline or
    // a run of spaces would no longer match as given. Collapse the needle the
    // same way, and apply the length gate to what is actually searched for.
    const redactByValue = (value: string, marker: string): void => {
      const needle = value.replace(/\s+/g, ' ').trim();
      if (needle.length >= MIN_BY_VALUE_KEY_CHARS) {
        line = line.replaceAll(needle, marker);
      }
    };
    if (licenseKey !== undefined) redactByValue(licenseKey, '<redacted-license-key>');
    for (const secret of secrets) redactByValue(secret, '<redacted-secret>');
    if (ip !== undefined && looksLikeIPAddress(ip)) {
      line = line.replaceAll(ip, '<redacted-ip>');
    }

    return line.length > MAX_FAILURE_LOG_CHARS
      ? `${line.slice(0, MAX_FAILURE_LOG_CHARS)}<truncated>`
      : line;
  } catch {
    return '<unloggable failure>';
  }
}
