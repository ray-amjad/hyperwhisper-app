// USAGE ROUTE
// GET /usage - Query credit balance and rate limits

import type { Context } from 'hono';
import { CREDITS_PER_MINUTE, DEFAULT_API_BASE_URL, LICENSE_API_TIMEOUT_MS } from '../lib/constants';
import { getClientIP } from '../lib/request-id';
import { getCachedLicense, cacheLicense } from '../lib/redis';
import { errorResponse, jsonResponse } from '../lib/responses';
import { isIPBlocked } from '../lib/redis';
import { roundToTenth } from '../lib/utils';

export function readFiniteCredits(data: unknown): number | null {
  if (
    typeof data === 'object'
    && data !== null
    && 'credits' in data
    && typeof data.credits === 'number'
    && Number.isFinite(data.credits)
  ) {
    return data.credits;
  }

  return null;
}

async function validateLicenseAndGetCredits(licenseKey: string, forceRefresh: boolean): Promise<{ isValid: boolean; credits: number }> {
  if (!forceRefresh) {
    const cached = await getCachedLicense(licenseKey);
    if (cached) {
      return { isValid: cached.isValid, credits: cached.credits };
    }
  }

  const apiBase = (process.env.NEXTJS_LICENSE_API_URL || DEFAULT_API_BASE_URL).replace(/\/+$/, '');

  try {
    const response = await fetch(`${apiBase}/api/license/validate`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ license_key: licenseKey, include_credits: true }),
      signal: AbortSignal.timeout(LICENSE_API_TIMEOUT_MS),
    });

    const data = await response.json().catch(() => ({})) as { valid?: boolean; credits?: number };
    const isValid = data.valid === true;
    const credits = readFiniteCredits(data) ?? 0;

    await cacheLicense(licenseKey, {
      isValid,
      credits,
      cachedAt: new Date().toISOString(),
    });

    return { isValid, credits };
  } catch {
    return { isValid: false, credits: 0 };
  }
}

async function getCreditsBalance(licenseKey: string): Promise<{ credits: number; error?: string }> {
  const apiBase = (process.env.NEXTJS_LICENSE_API_URL || DEFAULT_API_BASE_URL).replace(/\/+$/, '');

  try {
    const response = await fetch(`${apiBase}/api/license/credits?license_key=${encodeURIComponent(licenseKey)}`, {
      method: 'GET',
      signal: AbortSignal.timeout(LICENSE_API_TIMEOUT_MS),
    });

    if (!response.ok) {
      const errorData = await response.json().catch(() => ({})) as { error?: string };
      return { credits: 0, error: errorData.error || `HTTP ${response.status}` };
    }

    const data = await response.json().catch(() => ({}));
    const credits = readFiniteCredits(data);

    if (credits === null) {
      return { credits: 0, error: 'Invalid credits response' };
    }

    await cacheLicense(licenseKey, {
      isValid: true,
      credits,
      cachedAt: new Date().toISOString(),
    });

    return { credits };
  } catch (error) {
    return { credits: 0, error: error instanceof Error ? error.message : String(error) };
  }
}

export async function usageRoute(c: Context) {
  const clientIP = getClientIP(c);

  if (await isIPBlocked(clientIP)) {
    return errorResponse(403, 'Access denied', 'Your IP has been temporarily blocked due to abuse');
  }

  // `account_key` is the canonical param; `license_key` is the legacy alias that
  // installed native apps still send, so we accept either.
  const licenseKey =
    c.req.query('account_key') || c.req.query('license_key') || c.req.query('identifier')?.trim() || null;
  const forceRefresh = c.req.query('force_refresh') === 'true';

  if (licenseKey) {
    let isValid = false;
    let credits = 0;

    if (forceRefresh) {
      const cached = await getCachedLicense(licenseKey);
      if (cached?.isValid) {
        const balanceResult = await getCreditsBalance(licenseKey);
        if (balanceResult.error) {
          const validation = await validateLicenseAndGetCredits(licenseKey, true);
          isValid = validation.isValid;
          credits = validation.credits;
        } else {
          isValid = true;
          credits = balanceResult.credits;
        }
      } else {
        const validation = await validateLicenseAndGetCredits(licenseKey, true);
        isValid = validation.isValid;
        credits = validation.credits;
      }
    } else {
      const validation = await validateLicenseAndGetCredits(licenseKey, false);
      isValid = validation.isValid;
      credits = validation.credits;
    }

    if (!isValid) {
      return errorResponse(401, 'Invalid license key', 'The provided license key is invalid or expired');
    }

    const normalizedCredits = roundToTenth(credits);
    const minutesRemaining = Math.floor(normalizedCredits / CREDITS_PER_MINUTE);

    const response = {
      credits_remaining: normalizedCredits,
      minutes_remaining: minutesRemaining,
      credits_per_minute: CREDITS_PER_MINUTE,
      is_licensed: true,
      is_trial: false,
      is_anonymous: false,
    };

    return jsonResponse(response);
  }

  return errorResponse(401, 'License required', 'You must provide a valid license_key. HyperWhisper Cloud requires a license key.');
}
