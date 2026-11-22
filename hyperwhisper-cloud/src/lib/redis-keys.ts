// CLOUD REDIS KEY NAMES
//
// Every key the transcription service writes to its Upstash database
// (`UPSTASH_REDIS_CLOUD_*`) is named here and nowhere else. The licence cache
// (`redis-core.ts`), the credit-hold Lua script (`credit-hold-store.ts`), the
// Google token cache (`google-auth.ts`) and the log redactor
// (`log-redaction.ts`) all read their names from this module, so the hold
// script reads the very entry `cacheLicense` writes, and a renamed or added
// key is redacted from logs without a second edit.
//
// Pure: no I/O, no imports. `log-redaction.ts` imports it and must stay
// importable by every suite.

/** `ip_blocked:<ip>`: set while an IP is blocked. */
export const IP_BLOCKED_KEY_PREFIX = 'ip_blocked:';
/** `license:<key>`: one licence key's cached verdict and balance. */
export const LICENSE_KEY_PREFIX = 'license:';
/** The cached Google OAuth access token. */
export const GOOGLE_TOKEN_CACHE_KEY = 'google_oauth_token';

export function ipBlockedKey(ip: string): string {
  return `${IP_BLOCKED_KEY_PREFIX}${ip}`;
}

export function licenseCacheKey(licenseKey: string): string {
  return `${LICENSE_KEY_PREFIX}${licenseKey}`;
}

export const CREDIT_POOL_BALANCE_KEY_PREFIX = 'credit_pool_balance:';
export const CREDIT_HOLDS_KEY_PREFIX = 'credit_holds:';
export const CREDIT_HOLDS_EXPIRY_KEY_PREFIX = 'credit_holds_expiry:';

/**
 * Where `cacheLicense` keeps the latest balance the licence API reported for
 * ANY key of one credit pool (#2026). Same `{ credits, cachedAt }` JSON and TTL
 * as the `license:<key>` entry it is written next to.
 */
export function creditPoolBalanceKey(creditPoolId: string): string {
  return `${CREDIT_POOL_BALANCE_KEY_PREFIX}${creditPoolId}`;
}

/** `credit_holds:<account>` HASH: holdId -> held milli-credits. */
export function creditHoldsKey(account: string): string {
  return `${CREDIT_HOLDS_KEY_PREFIX}${account}`;
}

/** `credit_holds_expiry:<account>` ZSET: holdId -> expiry (epoch ms). */
export function creditHoldsExpiryKey(account: string): string {
  return `${CREDIT_HOLDS_EXPIRY_KEY_PREFIX}${account}`;
}

/**
 * `ratelimit:<bucket>:<identifier>`: the `ip` bucket's identifier is the raw
 * client IP, so the whole suffix is caller-identifying.
 */
export const RATE_LIMIT_KEY_PREFIX = 'ratelimit:';

/**
 * Key prefixes whose suffix identifies a caller (an IP, a licence key or an
 * account). All cloud commands share one Upstash client, which pipelines
 * same-tick commands, and an Upstash error echoes the whole batch — so every
 * caller-keyed name can appear in any Redis failure log. Values are not
 * redacted here: the only caller-derived value written is the opaque
 * `creditPoolId` (an HMAC), never a licence key or an IP.
 */
export const IDENTIFYING_KEY_PREFIXES = [
  IP_BLOCKED_KEY_PREFIX,
  LICENSE_KEY_PREFIX,
  CREDIT_POOL_BALANCE_KEY_PREFIX,
  CREDIT_HOLDS_KEY_PREFIX,
  CREDIT_HOLDS_EXPIRY_KEY_PREFIX,
  RATE_LIMIT_KEY_PREFIX,
] as const;

/** The `@upstash/ratelimit` prefix for one rate-limit bucket's counters. */
export function rateLimitKeyPrefix(bucket: string): string {
  return `${RATE_LIMIT_KEY_PREFIX}${bucket}`;
}
