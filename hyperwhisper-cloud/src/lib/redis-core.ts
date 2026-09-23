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
 * every request (`middleware/auth.ts`). Before this log existed the catch was
 * silent, so this line is the only thing that could ship any of it.
 *
 * So the design is a BOUND first and redaction second — a deny-list that names
 * the secrets it knows about is what let the licence key through. In order:
 *
 * 1. Collapse ALL whitespace to single spaces. A `UpstashJSONParseError` is
 *    built from `res.text()` verbatim, so an intermediary's HTML 502 body
 *    arrives with real newlines and would split this record into ~8 in a
 *    line-oriented shipper — on exactly the outage class the log is for.
 * 2. Cut the command payload. Everything from `, command was:` to the end
 *    becomes `<redacted>`, whatever it held. This is the pass that bounds an
 *    UNKNOWN secret: a key, a value or a caller we never thought about is gone
 *    without being named. If Upstash rewords the suffix this degrades to the
 *    passes below — today's behaviour — rather than to a leak.
 * 3. Redact `ip_blocked:` and `license:` values wherever else they appear, for
 *    a message that names a key outside the command payload.
 * 4. Redact this request's own `ip` by VALUE, which is the only pass that
 *    reaches an address carried some other way entirely —
 *    `connect ETIMEDOUT 203.0.113.7:443` has no key and no command suffix.
 *    Gated on `looksLikeIPAddress` so the `'unknown'` sentinel is not
 *    substituted into English.
 * 5. Truncate to `MAX_FAILURE_LOG_CHARS`.
 *
 * No two passes depend on each other's order for CORRECTNESS — the markers
 * hold no whitespace, `"`, `'` or `]`, so none of them can be re-matched or
 * truncated by a later pass. The order above is for readability. Dropping any
 * one of 2, 3 or 4 re-opens a leak the tests pin, so none is redundant.
 *
 * `ip` is optional so #921 can reuse this for the `getCachedLicense` /
 * `cacheLicense` catches, which have no address to redact. Those two lines are
 * deliberately NOT routed through it here — that is #921's scope, not #898's.
 *
 * Returns a STRING, never the Error: a raw Error prints a multi-line stack,
 * and the line-oriented shipper splits that into several unrelated records.
 * NEVER throws — a logger that throws inside a catch would turn a fail-open
 * into a 500.
 */
function toRedactedLogLine(error: unknown, ip?: string): string {
  try {
    // `String(x)`, never `` `${x}` ``. MEASURED, because round 1's note had it
    // backwards: `String(aSymbol)` does NOT throw — it has an explicit Symbol
    // case and gives `Symbol(x)` — while `` `${aSymbol}` `` throws
    // `Cannot convert a symbol to a string`. `String()` DOES still throw on an
    // object with a null prototype, which is what the catch below is for.
    const raw = error instanceof Error ? `${error.name}: ${error.message}` : String(error);

    let line = raw.replace(/\s+/g, ' ').trim();
    line = line.replace(/, command was:[\s\S]*$/, ', command was: <redacted>');
    line = line
      .replace(/ip_blocked:[^"'\s\]]*/g, 'ip_blocked:<redacted>')
      .replace(/license:[^"'\s\]]*/g, 'license:<redacted>');
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
    console.error('IP block check failed — failing open:', toRedactedLogLine(error, ip));
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
    console.error('Failed to get cached license:', error);
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
    console.error('Failed to cache license:', error);
  }
}
