// CREDIT HOLDS — ATOMIC RESERVATION STORE (issue #2008)
//
// A hold is credits set aside for one in-flight request or live session, so N
// concurrent requests on one key cannot each spend the whole balance. Holds
// live in the cloud's own Upstash database next to the `license:<key>` cache
// they are checked against:
//
//   credit_holds:<pool>         HASH  holdId -> held milli-credits
//   credit_holds_expiry:<pool>  ZSET  holdId -> expiry (epoch ms)
//
// `<pool>` is the credit pool the key spends from: `pool:<credit_pool_id>` when
// the licence API named one (credits belong to the USER, and one user can own
// several keys, #2026), else the Account Key itself.
//
// Every reservation is ONE Lua script, so the read of the outstanding holds and
// the write of the new one are atomic: two requests racing for the last credit
// cannot both see it free. The expiry set is the backstop for a hold whose
// release never ran (a crashed machine, a lost release call): the next
// reservation on that key sweeps every expired hold before it counts.
//
// Amounts are integer MILLI-credits, so the script never sums floats, and a
// partial grant is floored to a tenth of a credit, the unit every charge is
// rounded to (`creditsForCost`), so a settle can never land between the hold
// and the next tenth.
//
// This module never imports `./redis`, which a dozen suites replace with
// `mock.module` (see the note at the top of `./redis-core`); its client
// arrives through a factory, and `credit-hold-store-memory.ts` is the in-memory
// twin the route suites use.

/** Credits as integer milli-credits. */
export function toMilliCredits(credits: number): number {
  if (!Number.isFinite(credits) || credits <= 0) return 0;
  return Math.round(credits * 1000);
}

export function fromMilliCredits(milli: number): number {
  return Math.round(milli) / 1000;
}

export interface ReserveHoldRequest {
  /**
   * The credit pool the hold is counted against: every hold on one pool shares
   * one balance. `pool:<credit_pool_id>`, or the Account Key when there is none.
   */
  account: string;
  /**
   * The Account Key the request authenticated with, whose cached balance the
   * script also reads. Defaults to `account` (a pool that is a key).
   */
  licenseKey?: string;
  holdId: string;
  /**
   * The hold's NEW total, not an increment: the smallest total that is any use
   * to the caller, and the most it wants. A grant lands anywhere between them,
   * capped by what the balance has left after every OTHER live hold.
   */
  minMilli: number;
  maxMilli: number;
  /** The balance the caller authenticated with. The cached balance wins when lower. */
  balanceMilli: number;
  nowMs: number;
  /** How long the hold outlives this call if nothing releases or renews it. */
  ttlMs: number;
}

export interface ReserveHoldResult {
  granted: boolean;
  /** This hold's total after the call. Unchanged on a refusal. */
  holdMilli: number;
  /** What the balance had left for this hold (other holds taken off), floored to a tenth. */
  availableMilli: number;
}

export interface CreditHoldStore {
  reserve(request: ReserveHoldRequest): Promise<ReserveHoldResult>;
  release(account: string, holdId: string): Promise<void>;
}

/** The hold account of a credit pool: `pool:<credit_pool_id>`. */
export const CREDIT_POOL_ACCOUNT_PREFIX = 'pool:';

/**
 * Where `cacheLicense` keeps the latest balance the licence API reported for
 * ANY key of one credit pool (#2026). Same `{ credits, cachedAt }` JSON and TTL
 * as the `license:<key>` entry it is written next to.
 */
export function creditPoolBalanceKey(creditPoolId: string): string {
  return `credit_pool_balance:${creditPoolId}`;
}

/** The JSON `cacheLicense` keeps under `creditPoolBalanceKey`. */
export interface CreditPoolBalanceEntry {
  credits: number;
  cachedAt: string;
  /** Epoch ms just before the licence API request that reported `credits` was sent. */
  issuedAtMs?: number;
  /** Epoch ms when its reply was cached. */
  receivedAtMs?: number;
}

// KEYS: pool balance key. ARGV: entry JSON, ttl seconds, credits, issuedAtMs
// ('' when the caller did not stamp it), receivedAtMs. Returns 1 if written.
//
// Writes are ordered by when the licence API was ASKED, not by when the reply
// got here: several keys of one pool (and concurrent requests on one key) talk
// to the site at once, and a slow reply to an older question must not replace
// a newer, lower balance, or a sibling key reserves credits already spent.
// Each entry is the site's balance at some instant between its request being
// issued and its reply arriving, so against the stored entry a reading is:
//   - newer when it was issued after the stored one's reply came back: written,
//     so a top-up still lifts the pool;
//   - older when its reply came back before the stored one was issued: dropped;
//   - otherwise the two were in flight together and either could be the later
//     balance (two concurrent charges): the LOWER one is kept. A concurrent
//     top-up can then read low until the next request, which is the safe side.
// An unstamped reading (a machine still on the old build mid-deploy) never
// replaces a stamped entry, so it cannot carry an older balance past a newer
// one; it still replaces an unstamped entry, the old last-write-wins. A stored
// entry with no stamp, or one that does not parse, is replaced.
// The stamps are each machine's own clock; Fly hosts are NTP-synced, so skew is
// milliseconds against site round trips of tens or hundreds.
export const WRITE_POOL_BALANCE_SCRIPT = `
local incomingIssued = tonumber(ARGV[4])
local incomingReceived = tonumber(ARGV[5])
local raw = redis.call('GET', KEYS[1])
if raw then
  local ok, stored = pcall(function() return cjson.decode(raw) end)
  if ok and type(stored) == 'table' and type(stored.issuedAtMs) == 'number' then
    if incomingIssued == nil then return 0 end
    local storedReceived = stored.receivedAtMs
    if type(storedReceived) ~= 'number' then storedReceived = stored.issuedAtMs end
    if incomingIssued <= storedReceived then
      if incomingReceived < stored.issuedAtMs then return 0 end
      if type(stored.credits) == 'number' and tonumber(ARGV[3]) >= stored.credits then return 0 end
    end
  end
end
redis.call('SET', KEYS[1], ARGV[1], 'EX', ARGV[2])
return 1
`;

export function holdKeys(
  account: string,
  licenseKey: string = account,
): { holds: string; expiry: string; license: string; poolBalance: string } {
  const license = `license:${licenseKey}`;
  return {
    holds: `credit_holds:${account}`,
    expiry: `credit_holds_expiry:${account}`,
    // The SAME key `redis-core.ts` caches the balance under. The script reads it
    // so a settle that lowered the cached balance after this request authed is
    // still seen: auth can read the balance seconds before the reservation runs
    // (a long upload sits between them on /transcribe).
    license,
    // A key's own cache is not enough on a pool: a settle on key A rewrites
    // only `license:A`, so key B's entry keeps the balance from before A spent.
    // The pool entry is the latest balance any of the user's keys was told, so
    // the script caps by it too. An unpooled hold has no pool entry; it reads
    // its own licence entry twice instead, which changes nothing.
    poolBalance: account.startsWith(CREDIT_POOL_ACCOUNT_PREFIX)
      ? creditPoolBalanceKey(account.slice(CREDIT_POOL_ACCOUNT_PREFIX.length))
      : license,
  };
}

// KEYS: holds hash, expiry zset, license cache, pool balance cache
// ARGV: holdId, minMilli, maxMilli, balanceMilli, nowMs, ttlMs
// Returns {granted 0|1, holdMilli, availableMilli}.
//
// The balance is the LOWEST of the caller's, the key's cached one and the
// pool's cached one. `cjson` is read under pcall: if the runtime lacks it, or a
// cache entry is not the JSON `cacheLicense` writes, that entry is skipped.
//
// The minimum does not strand a top-up: every write of a pooled key's cache
// rewrites the pool entry too (`WRITE_POOL_BALANCE_SCRIPT`), and a balance the
// site was asked for after the stored one came back replaces it, higher or
// lower. (A key's own stale entry still caps that
// key until it refreshes, exactly as before pools.)
export const RESERVE_HOLD_SCRIPT = `
local holds = KEYS[1]
local expiry = KEYS[2]
local holdId = ARGV[1]
local minMilli = tonumber(ARGV[2])
local maxMilli = tonumber(ARGV[3])
local balance = tonumber(ARGV[4])
local now = tonumber(ARGV[5])
local ttl = tonumber(ARGV[6])

local expired = redis.call('ZRANGEBYSCORE', expiry, '-inf', now)
for i = 1, #expired do
  redis.call('HDEL', holds, expired[i])
end
if #expired > 0 then
  redis.call('ZREMRANGEBYSCORE', expiry, '-inf', now)
end

for k = 3, 4 do
  local raw = redis.call('GET', KEYS[k])
  if raw then
    local ok, decoded = pcall(function() return cjson.decode(raw) end)
    if ok and type(decoded) == 'table' and type(decoded.credits) == 'number' then
      local cached = math.floor(decoded.credits * 1000 + 0.5)
      if cached < balance then balance = cached end
    end
  end
end

local own = 0
local others = 0
local entries = redis.call('HGETALL', holds)
for i = 1, #entries, 2 do
  local amount = tonumber(entries[i + 1]) or 0
  if entries[i] == holdId then own = amount else others = others + amount end
end

local available = balance - others
if available < 0 then available = 0 end
available = available - (available % 100)

local grant = maxMilli
if grant > available then grant = available end
if grant < minMilli or grant <= 0 then
  return {0, own, available}
end

redis.call('HSET', holds, holdId, grant)
redis.call('ZADD', expiry, now + ttl, holdId)
local keyTtl = math.ceil(ttl / 1000) + 60
if redis.call('TTL', holds) < keyTtl then redis.call('EXPIRE', holds, keyTtl) end
if redis.call('TTL', expiry) < keyTtl then redis.call('EXPIRE', expiry, keyTtl) end
return {1, grant, available}
`;

// KEYS: holds hash, expiry zset. ARGV: holdId.
export const RELEASE_HOLD_SCRIPT = `
redis.call('HDEL', KEYS[1], ARGV[1])
redis.call('ZREM', KEYS[2], ARGV[1])
return 1
`;

/** The one Upstash command this store uses. */
export interface EvalClient {
  eval<TArgs extends unknown[], TData = unknown>(script: string, keys: string[], args: TArgs): Promise<TData>;
}

function parseReserveReply(reply: unknown): ReserveHoldResult {
  if (!Array.isArray(reply) || reply.length < 3) {
    throw new Error('unexpected credit hold reply shape');
  }
  const [granted, holdMilli, availableMilli] = reply.map((value) => Number(value));
  if (![granted, holdMilli, availableMilli].every((value) => Number.isFinite(value))) {
    throw new Error('unexpected credit hold reply values');
  }
  return { granted: granted === 1, holdMilli: holdMilli!, availableMilli: availableMilli! };
}

/**
 * The production store. `client` is called per operation, inside the caller's
 * `try`, because the real factory throws when the Upstash env is absent.
 */
export function createUpstashCreditHoldStore(client: () => EvalClient): CreditHoldStore {
  return {
    async reserve(request) {
      const keys = holdKeys(request.account, request.licenseKey);
      const reply = await client().eval(
        RESERVE_HOLD_SCRIPT,
        [keys.holds, keys.expiry, keys.license, keys.poolBalance],
        [
          request.holdId,
          String(Math.round(request.minMilli)),
          String(Math.round(request.maxMilli)),
          String(Math.round(request.balanceMilli)),
          String(Math.round(request.nowMs)),
          String(Math.round(request.ttlMs)),
        ],
      );
      return parseReserveReply(reply);
    },
    async release(account, holdId) {
      const keys = holdKeys(account);
      await client().eval(RELEASE_HOLD_SCRIPT, [keys.holds, keys.expiry], [holdId]);
    },
  };
}
