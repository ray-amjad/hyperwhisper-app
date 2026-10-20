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

export function holdKeys(account: string, licenseKey: string = account): { holds: string; expiry: string; license: string } {
  return {
    holds: `credit_holds:${account}`,
    expiry: `credit_holds_expiry:${account}`,
    // The SAME key `redis-core.ts` caches the balance under. The script reads it
    // so a settle that lowered the cached balance after this request authed is
    // still seen: auth can read the balance seconds before the reservation runs
    // (a long upload sits between them on /transcribe).
    // Per KEY even when the holds are per pool: each of a user's keys caches
    // the same pooled balance, and this request's key has the freshest.
    license: `license:${licenseKey}`,
  };
}

// KEYS: holds hash, expiry zset, license cache
// ARGV: holdId, minMilli, maxMilli, balanceMilli, nowMs, ttlMs
// Returns {granted 0|1, holdMilli, availableMilli}.
//
// `cjson` is read under pcall: if the runtime lacks it, or the cache entry is
// not the JSON `cacheLicense` writes, the caller's balance stands alone.
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

local raw = redis.call('GET', KEYS[3])
if raw then
  local ok, decoded = pcall(function() return cjson.decode(raw) end)
  if ok and type(decoded) == 'table' and type(decoded.credits) == 'number' then
    local cached = math.floor(decoded.credits * 1000 + 0.5)
    if cached < balance then balance = cached end
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
        [keys.holds, keys.expiry, keys.license],
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
