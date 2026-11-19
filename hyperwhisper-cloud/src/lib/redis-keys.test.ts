import { describe, expect, test } from 'bun:test';
import {
  creditHoldsExpiryKey,
  creditHoldsKey,
  creditPoolBalanceKey,
  GOOGLE_TOKEN_CACHE_KEY,
  ipBlockedKey,
  licenseCacheKey,
  rateLimitKeyPrefix,
} from './redis-keys';
import { holdKeys } from './credit-hold-store';
import { toRedactedLogLine } from './log-redaction';

describe('cloud Redis key names', () => {
  test('the stored key strings are unchanged', () => {
    expect(ipBlockedKey('1.2.3.4')).toBe('ip_blocked:1.2.3.4');
    expect(licenseCacheKey('K')).toBe('license:K');
    expect(creditPoolBalanceKey('p1')).toBe('credit_pool_balance:p1');
    expect(creditHoldsKey('A')).toBe('credit_holds:A');
    expect(creditHoldsExpiryKey('A')).toBe('credit_holds_expiry:A');
    expect(rateLimitKeyPrefix('transcribe')).toBe('ratelimit:transcribe');
    expect(GOOGLE_TOKEN_CACHE_KEY).toBe('google_oauth_token');
  });

  test('the hold script reads the entry cacheLicense writes', () => {
    expect(holdKeys('K').license).toBe(licenseCacheKey('K'));
  });

  test('log redaction covers the keys by their shared names', () => {
    const line = toRedactedLogLine(
      new Error(`${ipBlockedKey('9.9.9.9')} ${licenseCacheKey('SECRETKEY')} ${GOOGLE_TOKEN_CACHE_KEY}=abc`)
    );
    expect(line).not.toContain('9.9.9.9');
    expect(line).not.toContain('SECRETKEY');
    expect(line).not.toContain('=abc');
  });
});
