// UPSTASH REDIS CLIENT
// Serverless Redis for IP blocking and license caching
// Works globally with Fly.io's anycast routing

import { Redis } from '@upstash/redis';
import { LICENSE_CACHE_TTL_SECONDS } from './constants';
import { isRecord } from './utils';

// Initialize Redis client (lazy initialization for testing without Redis)
let _redis: Redis | null = null;

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
  try {
    const blockKey = `ip_blocked:${ip}`;
    const blocked = await getRedis().get(blockKey);
    return blocked === 'true';
  } catch {
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

export async function getCachedLicense(licenseKey: string): Promise<CachedLicense | null> {
  try {
    const cached = await getRedis().get<CachedLicense>(`license:${licenseKey}`);
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

export async function cacheLicense(licenseKey: string, license: CachedLicense): Promise<void> {
  try {
    await getRedis().set(`license:${licenseKey}`, license, { ex: LICENSE_CACHE_TTL_SECONDS });
  } catch (error) {
    console.error('Failed to cache license:', error);
  }
}
