// UPSTASH REDIS CLIENT
// Serverless Redis for IP blocking and license caching
// Works globally with Fly.io's anycast routing
//
// This module is the I/O edge only: it wires up the client. The logic the three
// functions below carry out lives in `./redis-core`, where the client arrives
// as a parameter — see the note at the top of that file for why a test cannot
// reach it through this module.

import * as core from './redis-core';
import { createRedisGetter } from './redis-client';

export type { CachedLicense, RedisStore, RedisStoreFactory } from './redis-core';

// Lazy, memoised getter for the CLOUD database. The env names, the missing-env
// error and the memo live in `./redis-client`, which nothing mocks, so its
// tests reach them; see the SITE / CLOUD warning there.
const getRedis = createRedisGetter();

// Export redis getter for lazy initialization
export const redis = {
  get: getRedis,
};

// ============================================================================
// IP BLOCKING + DAILY QUOTA (credits-based)
// ============================================================================

export async function isIPBlocked(ip: string): Promise<boolean> {
  return core.isIPBlocked(getRedis, ip);
}

// ============================================================================
// LICENSE CACHE (1 hour TTL for valid + invalid)
// ============================================================================

export async function getCachedLicense(licenseKey: string): Promise<core.CachedLicense | null> {
  return core.getCachedLicense(getRedis, licenseKey);
}

export async function cacheLicense(licenseKey: string, license: core.CachedLicense): Promise<void> {
  return core.cacheLicense(getRedis, licenseKey, license);
}
