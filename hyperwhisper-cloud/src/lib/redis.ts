// UPSTASH REDIS CLIENT
// Serverless Redis for IP blocking and license caching
// Works globally with Fly.io's anycast routing

import { Redis } from '@upstash/redis';
import { LICENSE_CACHE_TTL_SECONDS } from './constants';
import { roundToTenth } from './utils';

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

export async function getIPDailyUsage(ip: string, dateKey: string): Promise<number> {
  const key = `ip_daily:${ip}:${dateKey}`;
  try {
    const raw = await getRedis().get(key);
    if (!raw) return 0;
    const parsed = typeof raw === 'string' ? Number.parseFloat(raw) : Number(raw);
    return Number.isFinite(parsed) ? roundToTenth(parsed) : 0;
  } catch {
    return 0;
  }
}

export async function setIPDailyUsage(ip: string, dateKey: string, credits: number, ttlSeconds: number): Promise<void> {
  const key = `ip_daily:${ip}:${dateKey}`;
  await getRedis().set(key, credits.toFixed(1), { ex: ttlSeconds });
}

// ============================================================================
// LICENSE CACHE (1 hour TTL for valid + invalid)
// ============================================================================

export interface CachedLicense {
  isValid: boolean;
  credits: number;
  cachedAt: string;
}

export async function getCachedLicense(licenseKey: string): Promise<CachedLicense | null> {
  try {
    const cached = await getRedis().get<CachedLicense>(`license:${licenseKey}`);
    if (!cached) return null;

    if (typeof cached === 'string') {
      return JSON.parse(cached) as CachedLicense;
    }

    return cached;
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
