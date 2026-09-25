// UPSTASH REDIS — I/O-FREE CORE
//
// The IP block lookup, the license-cache read and the license-cache write, with
// their one I/O edge — the Upstash client — passed in as a factory.
//
// `lib/redis.ts` stays the module every caller imports; it supplies the real,
// lazily built client and re-exports the same four values it always did. This
// split exists because TEN suites replace `../lib/redis` with `mock.module`,
// and bun's module registry is process-wide: a test that imported `./redis` to
// exercise the logic below would get another suite's stub whenever that suite
// loaded first, and would then assert nothing. Nothing mocks `./redis-core`, so
// a test here mocks nothing at all. Same split, and the same reason, as
// `lib/google-auth-core.ts`.

import { LICENSE_CACHE_TTL_SECONDS } from './constants';
import { isRecord } from './utils';

/** The two Upstash commands this module uses. */
export interface RedisStore {
  get<TData = unknown>(key: string): Promise<TData | null>;
  set(key: string, value: unknown, opts: { ex: number }): Promise<unknown>;
}

/**
 * Called per operation, never held. The real factory THROWS when the Upstash
 * env vars are absent, so every function below calls it inside its own `try` —
 * that keeps the long-standing "no Redis configured fails open" behaviour
 * (`isIPBlocked` false, `getCachedLicense` a miss, `cacheLicense` a no-op)
 * exactly as it was when they called `getRedis()` inline.
 */
export type RedisStoreFactory = () => RedisStore;

// ============================================================================
// IP BLOCKING
// ============================================================================

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
 * The shortest licence key the by-value pass will substitute. The real format
 * is 19 characters (`HW-XXXX-XXXX-XXXX-XXXX`), but the caller passes whatever
 * the request sent. An empty string would put a marker between every character
 * of the line, and a 1-3 character key would rewrite ordinary words. Shorter
 * keys are left to the key pass and the payload cut.
 */
const MIN_BY_VALUE_KEY_CHARS = 8;

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

/**
 * A Redis failure as ONE bounded log line, with the secrets taken out of it.
 *
 * Logging the raw error would ship credentials. `@upstash/redis` builds its
 * message as `` `${body.error}, command was: ${JSON.stringify(req.body)}` ``
 * on every non-ok HTTP response, and `req.body` is what we sent. That body is
 * NOT just this caller's command: `enableAutoPipelining` defaults to `true`
 * (`chunk-LLI2WIYN.mjs`, `Redis` constructor) and `lib/redis.ts` builds
 * `new Redis({ url, token })` without overriding it, so every command issued in
 * the same tick is co-batched into one request — an `ip_blocked:` key for a
 * DIFFERENT caller's IP, a `license:` key, and on a `cacheLicense` write the
 * cached licence object itself. The licence key is the bearer credential for
 * every request (`middleware/auth.ts`). So EVERY catch in this file logs
 * through this helper: `isIPBlocked` (#898 — its catch was silent before), and
 * `getCachedLicense` / `cacheLicense` (#921 — they used to log the raw Error,
 * measured at 20 stderr lines on one 401 with the IP, the licence key and the
 * cached licence value in the clear). Each caller hands over the secrets it
 * HOLDS in `byValue`: `isIPBlocked` its `ip`, the licence functions their
 * `licenseKey`.
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
 * 3. Cut the userinfo out of any `scheme://user:password@host` URL. The second
 *    grammar-shaped bound, and it names no secret either. Upstash gives an
 *    operator TWO connection strings for one database — a REST URL, and a
 *    `redis://default:<PASSWORD>@host:6379` URL that carries the password
 *    inline. Paste the second into `UPSTASH_REDIS_CLOUD_URL` and the real
 *    client throws `UrlError` from its constructor, INSIDE this try, with the
 *    whole URL quoted back in the message (measured, v1.36.1). No other pass
 *    reaches it: there is no `, command was:` suffix, no Redis key, it is not
 *    the client IP, and the line measures 199 characters so the cap does not
 *    fire — and the password sits at the FRONT, so the cap would not help.
 *    The host is deliberately kept, or an operator cannot see WHICH url is
 *    wrong.
 * 4. Redact `ip_blocked:` and `license:` values wherever else they appear, for
 *    a message that names a key outside the command payload.
 * 5. Redact the secrets the caller holds, by VALUE. This is the only pass that
 *    reaches a secret the message carries with no key and no payload around
 *    it. Each has its own gate and its own marker:
 *
 *    - `licenseKey` → `<redacted-license-key>`, e.g. an upstream 429 body
 *      `daily quota exceeded for account HW-…`. Skipped below
 *      `MIN_BY_VALUE_KEY_CHARS`, so an empty or tiny key cannot rewrite
 *      ordinary text.
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
 *    These run after 1-4 because a caller's value is arbitrary text: a key
 *    that happens to be a substring of a marker (`redacted`) could rewrite
 *    part of one. Running last, such a rewrite only swaps marker text for
 *    another marker, and only the cap follows it.
 * 6. Truncate to `MAX_FAILURE_LOG_CHARS`.
 *
 * Apart from pass 5 running after 1-4 (above), no two passes depend on each
 * other's order for CORRECTNESS. Every marker a pass can WRITE INTO the line —
 * `<redacted>`, `<redacted-ip>`, `<redacted-license-key>`,
 * `<redacted-credentials>` — holds no whitespace, `"`, `'`, `[`, `]`, `{`, `/`,
 * `?`, `#` or `@`, so it matches no other pass's character class in part: a later pass
 * can only re-match one whole (which is idempotent) and can never truncate
 * one. Pass 3 re-matching its OWN output is the case that needs `@` on that
 * list, and `redis://<redacted-credentials>@host` is a fixed point. Round 1's
 * `<redacted ip>` did have a space, which is what made the old order
 * load-bearing; the hyphen removed the hazard rather than documenting it.
 * Dropping any one of 2, 3, 4 or either half of 5 re-opens a leak the tests
 * pin, so none is redundant. (`<unloggable failure>` is the catch-path
 * return, not a marker: it replaces the whole line and never meets another
 * pass.)
 *
 * `getCachedLicense` and `cacheLicense` pass no `ip`, so their redaction is
 * passes 1-4, the licence-key half of 5, and 6. They keep their own message
 * prefixes, which existing Axiom queries match on — only the second argument
 * goes through here.
 * The licence key is REDACTED, not masked to its first/last 4 characters:
 * whether `README.md`'s masking claim is the contract is still open (#921).
 *
 * Returns a STRING, never the Error: a raw Error prints a multi-line stack,
 * and the line-oriented shipper splits that into several unrelated records.
 * NEVER throws — a logger that throws inside a catch would turn a fail-open
 * into a 500.
 */
function toRedactedLogLine(
  error: unknown,
  byValue: { readonly ip?: string; readonly licenseKey?: string } = {}
): string {
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
      .replace(/license:[^"'\s\]]*/g, 'license:<redacted>');
    const { ip, licenseKey } = byValue;
    if (licenseKey !== undefined && licenseKey.length >= MIN_BY_VALUE_KEY_CHARS) {
      line = line.replaceAll(licenseKey, '<redacted-license-key>');
    }
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

export async function isIPBlocked(store: RedisStoreFactory, ip: string): Promise<boolean> {
  try {
    const blockKey = `ip_blocked:${ip}`;
    const blocked = await store().get(blockKey);
    return blocked === 'true';
  } catch (error) {
    // Keep failing open — a Redis outage must not lock every caller out. But
    // this is the only abuse gate the public endpoints have, so the fail-open
    // has to leave a record: stdout ships to Axiom, and without a line here a
    // disabled gate looks exactly like an hour with no blocked IPs. Nothing
    // secret in the line — see `toRedactedLogLine`; the operation name and the
    // bounded error text are enough to spot the outage.
    console.error('IP block check failed — failing open:', toRedactedLogLine(error, { ip }));
    return false;
  }
}

// ============================================================================
// LICENSE CACHE (1 hour TTL for valid + invalid)
// ============================================================================

export interface CachedLicense {
  isValid: boolean;
  credits: number;
  cachedAt: string;
}

function isCachedLicense(value: unknown): value is CachedLicense {
  if (!isRecord(value)) return false;
  return typeof value.isValid === 'boolean'
    && typeof value.credits === 'number'
    && typeof value.cachedAt === 'string';
}

export async function getCachedLicense(
  store: RedisStoreFactory,
  licenseKey: string
): Promise<CachedLicense | null> {
  try {
    const cached = await store().get<CachedLicense>(`license:${licenseKey}`);
    if (!cached) return null;

    // Validate the shape instead of asserting it. An entry written by an older
    // schema (or a truncated string) used to come back with `isValid`
    // undefined, which auth.ts reads as "license invalid" and locks a paying
    // user out for the full TTL. Treat anything unrecognised as a cache MISS
    // so the next request revalidates against the license API.
    const parsed: unknown = typeof cached === 'string' ? JSON.parse(cached) : cached;
    return isCachedLicense(parsed) ? parsed : null;
  } catch (error) {
    console.error('Failed to get cached license:', toRedactedLogLine(error, { licenseKey }));
    return null;
  }
}

export async function cacheLicense(
  store: RedisStoreFactory,
  licenseKey: string,
  license: CachedLicense
): Promise<void> {
  try {
    await store().set(`license:${licenseKey}`, license, { ex: LICENSE_CACHE_TTL_SECONDS });
  } catch (error) {
    console.error('Failed to cache license:', toRedactedLogLine(error, { licenseKey }));
  }
}
