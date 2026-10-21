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
//
// Every catch below logs through `toRedactedLogLine` (`lib/log-redaction.ts`),
// which says why a raw Upstash error is a credential leak.

import { LICENSE_CACHE_TTL_SECONDS } from './constants';
import { WRITE_POOL_BALANCE_SCRIPT, creditPoolBalanceKey, type CreditPoolBalanceEntry } from './credit-hold-store';
import { toRedactedLogLine } from './log-redaction';
import { isRecord } from './utils';

/** The three Upstash commands this module uses. */
export interface RedisStore {
  get<TData = unknown>(key: string): Promise<TData | null>;
  set(key: string, value: unknown, opts: { ex: number }): Promise<unknown>;
  eval<TArgs extends unknown[], TData = unknown>(script: string, keys: string[], args: TArgs): Promise<TData>;
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

export async function isIPBlocked(store: RedisStoreFactory, ip: string): Promise<boolean> {
  try {
    const blockKey = `ip_blocked:${ip}`;
    const blocked = await store().get(blockKey);
    // The client JSON-parses every read, so an entry stored as bare `true`
    // (redis-cli, the Upstash console, or `set(key, 'true')`) reads back as
    // the boolean; only one stored with literal quotes reads as the string.
    return blocked === true || blocked === 'true';
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
  /**
   * The licence API's opaque `credit_pool_id` (#2026): the user the key's
   * credits belong to, which credit holds are counted per. Absent when the API
   * sent none; holds are then per key.
   */
  creditPoolId?: string;
  /**
   * Epoch ms taken just BEFORE the licence API request whose reply this is was
   * sent (#2026). Orders writes of the pool balance, which several requests
   * race to write (see `WRITE_POOL_BALANCE_SCRIPT`), and of this entry itself
   * (#1899, see `WRITE_LICENSE_SCRIPT`).
   */
  issuedAtMs?: number;
}

function isCachedLicense(value: unknown): value is CachedLicense {
  if (!isRecord(value)) return false;
  return typeof value.isValid === 'boolean'
    && typeof value.credits === 'number'
    && typeof value.cachedAt === 'string'
    && (value.creditPoolId === undefined || typeof value.creditPoolId === 'string');
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

// KEYS: license:<key>. ARGV: entry JSON, ttl seconds, issuedAtMs. Returns 1 if
// written.
//
// The entry from whichever licence API call was ASKED last wins, not whichever
// reply got here last (#1899). Validation, the usage read and every charge
// reply all write this one entry, and they race: a slow success to a charge
// sent before a revoke would otherwise rewrite the refusal as "valid" and admit
// the key for a whole TTL (and a request that never charges, such as silent
// audio, never corrects it). A slow refusal to a call sent before a re-grant is
// dropped the same way. A tie is written. A stored entry with no stamp, or one
// that does not parse, is replaced. One script, so no reply can land between
// the read and the write.
export const WRITE_LICENSE_SCRIPT = `
local incomingIssued = tonumber(ARGV[3])
local raw = redis.call('GET', KEYS[1])
if raw and incomingIssued then
  local ok, stored = pcall(function() return cjson.decode(raw) end)
  if ok and type(stored) == 'table' and type(stored.issuedAtMs) == 'number'
    and stored.issuedAtMs > incomingIssued then
    return 0
  end
end
redis.call('SET', KEYS[1], ARGV[1], 'EX', ARGV[2])
return 1
`;

export async function cacheLicense(
  store: RedisStoreFactory,
  licenseKey: string,
  license: CachedLicense,
  receivedAtMs: number = Date.now(),
): Promise<void> {
  try {
    const redis = store();
    // Every caller stamps its write; an unstamped one (a machine still on an
    // older build mid-deploy) keeps the plain last-write-wins `set` it had.
    const writes: Array<Promise<unknown>> = [
      license.issuedAtMs !== undefined
        ? redis.eval(
          WRITE_LICENSE_SCRIPT,
          [`license:${licenseKey}`],
          [JSON.stringify(license), String(LICENSE_CACHE_TTL_SECONDS), String(license.issuedAtMs)],
        )
        : redis.set(`license:${licenseKey}`, license, { ex: LICENSE_CACHE_TTL_SECONDS }),
    ];
    // A pooled key's balance is the USER's (#2026), so it is also the latest
    // word on every other key of that user. The credit-hold script caps a
    // pooled reservation by this entry too, or a key whose own cache predates
    // a sibling's spend would reserve the spent credits again. The write is a
    // compare-and-set on when the site was asked, not last-write-wins: a slow
    // reply to an older request must not replace a newer, lower balance.
    if (license.creditPoolId) {
      const entry: CreditPoolBalanceEntry = {
        credits: license.credits,
        cachedAt: license.cachedAt,
        ...(license.issuedAtMs !== undefined ? { issuedAtMs: license.issuedAtMs, receivedAtMs } : {}),
      };
      writes.push(redis.eval(
        WRITE_POOL_BALANCE_SCRIPT,
        [creditPoolBalanceKey(license.creditPoolId)],
        [
          JSON.stringify(entry),
          String(LICENSE_CACHE_TTL_SECONDS),
          String(license.credits),
          license.issuedAtMs !== undefined ? String(license.issuedAtMs) : '',
          String(receivedAtMs),
        ],
      ));
    }
    await Promise.all(writes);
  } catch (error) {
    console.error('Failed to cache license:', toRedactedLogLine(error, { licenseKey }));
  }
}
