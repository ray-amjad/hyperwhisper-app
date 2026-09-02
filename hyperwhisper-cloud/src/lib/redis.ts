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

// This is the transcription service's Upstash pair, named WITHOUT the
// `_REST_` infix the Next.js site uses. That site reads
// UPSTASH_REDIS_REST_URL / UPSTASH_REDIS_REST_TOKEN in
// `nextjs/lib/clients/redis.ts`. Both go through the same @upstash/redis
// client over the same REST protocol, so a cross-wired value fails silently
// as a cache that answers the wrong service's keys rather than as a
// connection error. Keep the two name pairs distinct, and check which
// service you are in before you touch either one.
function getRedis(): Redis {
  if (!_redis) {
    const url = process.env.UPSTASH_REDIS_URL;
    const token = process.env.UPSTASH_REDIS_TOKEN;

    if (!url || !token) {
      throw new Error('UPSTASH_REDIS_URL and UPSTASH_REDIS_TOKEN are required');
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
