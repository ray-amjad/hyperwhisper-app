// UPSTASH REDIS CLIENT
// Serverless Redis for IP blocking and license caching
// Works globally with Fly.io's anycast routing
//
// This module is the I/O edge only: it builds the client. The logic the three
// functions below carry out lives in `./redis-core`, where the client arrives
// as a parameter — see the note at the top of that file for why a test cannot
// reach it through this module.

import { Redis } from '@upstash/redis';
import * as core from './redis-core';

export type { CachedLicense, RedisStore, RedisStoreFactory } from './redis-core';

// Initialize Redis client (lazy initialization for testing without Redis)
let _redis: Redis | null = null;

// The transcription service's own Upstash database. The Next.js site has a
// separate one behind UPSTASH_REDIS_SITE_* (`nextjs/lib/clients/redis.ts`).
// Both go through the same @upstash/redis client against the same REST
// protocol, so a swapped value fails silently — as a cache answering the
// other service's keys, not as a connection error. The SITE / CLOUD segment
// is the only thing separating them; keep it accurate.
function getRedis(): Redis {
  if (!_redis) {
    const url = process.env.UPSTASH_REDIS_CLOUD_URL;
    const token = process.env.UPSTASH_REDIS_CLOUD_TOKEN;

    if (!url || !token) {
      throw new Error('UPSTASH_REDIS_CLOUD_URL and UPSTASH_REDIS_CLOUD_TOKEN are required');
    }

    _redis = new Redis({ url, token });
  }
  return _redis;
}

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
