// AUTHENTICATION HELPERS
// Validates license keys and device trial identifiers

import { DEFAULT_API_BASE_URL, LICENSE_API_TIMEOUT_MS } from '../lib/constants';
import { cacheLicense, getCachedLicense } from '../lib/redis';
import { invalidLicenseResponse, licenseRequiredResponse } from '../lib/responses';

export interface AuthContext {
  identifier: string; // license key
  credits: number;
  licenseKey: string;
}

export interface AuthInput {
  licenseKey?: string;
}

export type AuthResult =
  | { ok: true; value: AuthContext }
  | { ok: false; response: Response };

// Mask license key for logging (show first 4 and last 4 chars)
function maskLicenseKey(key: string): string {
  if (key.length <= 8) return '****';
  return `${key.slice(0, 4)}...${key.slice(-4)}`;
}

async function validateLicenseViaApi(licenseKey: string): Promise<{ isValid: boolean; credits: number }> {
  const apiBase = (process.env.NEXTJS_LICENSE_API_URL || DEFAULT_API_BASE_URL).replace(/\/+$/, '');
  const validateUrl = `${apiBase}/api/license/validate`;
  const maskedKey = maskLicenseKey(licenseKey);

  console.log(`[License] Validating ${maskedKey} via ${validateUrl}`);

  try {
    const response = await fetch(validateUrl, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        license_key: licenseKey,
        include_credits: true,
      }),
      signal: AbortSignal.timeout(LICENSE_API_TIMEOUT_MS),
    });

    const responseText = await response.text();
    let data: { valid?: boolean; credits?: number; error?: string } = {};

    try {
      data = JSON.parse(responseText);
    } catch {
      console.error(`[License] Invalid JSON response for ${maskedKey}: ${responseText.slice(0, 200)}`);
    }

    const isValid = data.valid === true;
    const credits = typeof data.credits === 'number' ? data.credits : 0;

    console.log(`[License] ${maskedKey}: status=${response.status}, valid=${isValid}, credits=${credits}${data.error ? `, error=${data.error}` : ''}`);

    // A 5xx (cold start, upstream timeout, internal error) is a transient
    // failure, not proof the license is invalid — caching it would lock a
    // paying user out for the full LICENSE_CACHE_TTL_SECONDS. Fail this
    // request closed but leave the cache untouched so the next request
    // retries the API. A 4xx, by contrast, is a definitive verdict from the
    // licensing API (revoked/not-found/malformed key → valid:false); those
    // MUST be cached so repeated requests with the same bad key don't hammer
    // the licensing API on every call.
    if (response.status >= 500) {
      console.warn(`[License] Transient ${response.status} response for ${maskedKey}; not caching`);
      return { isValid: false, credits: 0 };
    }

    await cacheLicense(licenseKey, {
      isValid,
      credits,
      cachedAt: new Date().toISOString(),
    });

    return { isValid, credits };
  } catch (error) {
    // Network/DNS/timeout failure — we could not reach the license API, so we
    // cannot conclude the license is invalid. Do NOT cache: caching invalid
    // here would lock a paying user out for the full TTL. Fail this request
    // closed; the next request retries against the API.
    if (error instanceof DOMException && (error.name === 'TimeoutError' || error.name === 'AbortError')) {
      console.error(`[License] Validation timed out for ${maskedKey} after ${LICENSE_API_TIMEOUT_MS}ms (transient, not cached)`);
    } else {
      console.error(`[License] Validation failed for ${maskedKey} (transient, not cached):`, error);
    }
    return { isValid: false, credits: 0 };
  }
}

export async function validateAuth(input: AuthInput, forceRefresh = false): Promise<AuthResult> {
  const { licenseKey } = input;

  // HyperWhisper Cloud is licensed-only: a valid license key (which carries the
  // credit balance) is required for every request. There is no anonymous/trial
  // path — without a key the request is rejected before any provider work.
  if (!licenseKey) {
    console.log('[Auth] No license_key provided');
    return { ok: false, response: licenseRequiredResponse() };
  }

  const maskedKey = maskLicenseKey(licenseKey);

  if (!forceRefresh) {
    const cached = await getCachedLicense(licenseKey);
    if (cached) {
      console.log(`[Auth] Cache HIT for ${maskedKey}: valid=${cached.isValid}, credits=${cached.credits}, cachedAt=${cached.cachedAt}`);
      if (!cached.isValid) {
        return { ok: false, response: invalidLicenseResponse() };
      }
      return {
        ok: true,
        value: {
          identifier: licenseKey,
          credits: cached.credits,
          licenseKey,
        },
      };
    }
    console.log(`[Auth] Cache MISS for ${maskedKey}, calling API...`);
  } else {
    console.log(`[Auth] Force refresh for ${maskedKey}, bypassing cache...`);
  }

  const validation = await validateLicenseViaApi(licenseKey);
  if (!validation.isValid) {
    console.log(`[Auth] License ${maskedKey} is INVALID`);
    return { ok: false, response: invalidLicenseResponse() };
  }

  console.log(`[Auth] License ${maskedKey} is VALID with ${validation.credits} credits`);
  return {
    ok: true,
    value: {
      identifier: licenseKey,
      credits: validation.credits,
      licenseKey,
    },
  };
}
