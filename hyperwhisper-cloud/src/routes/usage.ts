// USAGE ROUTE
// GET /usage - Query credit balance and rate limits

import type { Context } from 'hono';
import { CREDITS_PER_MINUTE, DEFAULT_API_BASE_URL, LICENSE_API_TIMEOUT_MS } from '../lib/constants';
import { authDiagnosticsForLog, validateAuth, type AuthDiagnostics } from '../middleware/auth';
import { generateRequestId, getClientIP } from '../lib/request-id';
import { logEvent } from '../lib/logging';
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

// A failed balance read carries a fixed category, never upstream text: the
// licensing API's error body and a fetch error message can both repeat the
// request URL, and the licence key is in that URL's query string.
type CreditsLookupError = 'http_error' | 'invalid_response' | 'network_error';

async function getCreditsBalance(
  licenseKey: string,
): Promise<{ credits: number; error?: CreditsLookupError; status?: number; errorName?: string }> {
  const apiBase = (process.env.NEXTJS_LICENSE_API_URL || DEFAULT_API_BASE_URL).replace(/\/+$/, '');

  try {
    const response = await fetch(`${apiBase}/api/license/credits?license_key=${encodeURIComponent(licenseKey)}`, {
      method: 'GET',
      signal: AbortSignal.timeout(LICENSE_API_TIMEOUT_MS),
    });

    if (!response.ok) {
      return { credits: 0, error: 'http_error', status: response.status };
    }

    const data = await response.json().catch(() => ({}));
    const credits = readFiniteCredits(data);

    if (credits === null) {
      return { credits: 0, error: 'invalid_response', status: response.status };
    }

    await cacheLicense(licenseKey, {
      isValid: true,
      credits,
      cachedAt: new Date().toISOString(),
    });

    return { credits };
  } catch (error) {
    // The class name only (TimeoutError, TypeError), never the message.
    const name = error instanceof Error ? error.name : '';
    return { credits: 0, error: 'network_error', errorName: /^\w{1,40}$/.test(name) ? name : 'unknown' };
  }
}

export async function usageRoute(c: Context) {
  const requestId = generateRequestId();
  const startTime = performance.now();
  const clientIP = getClientIP(c);

  if (await isIPBlocked(clientIP)) {
    logEvent(requestId, startTime, 'usage.request_rejected', { reason: 'ip_blocked' });
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
    // Set by every validateAuth call below. A rejection always comes from one,
    // so the invalid_license line can say WHY: a licensing-API timeout or 5xx
    // also fails closed, and without these fields it reads as a bad key.
    let authDiagnostics: AuthDiagnostics | undefined;

    if (forceRefresh) {
      const cached = await getCachedLicense(licenseKey);
      if (cached?.isValid) {
        const balanceResult = await getCreditsBalance(licenseKey);
        if (balanceResult.error) {
          logEvent(requestId, startTime, 'usage.credits_lookup_failed', {
            error: balanceResult.error,
            status: balanceResult.status,
            errorName: balanceResult.errorName,
            forceRefresh,
          });
          const validation = await validateAuth({ licenseKey }, true);
          isValid = validation.ok;
          authDiagnostics = validation.diagnostics;
          credits = validation.ok ? validation.value.credits : 0;
        } else {
          isValid = true;
          credits = balanceResult.credits;
        }
      } else {
        const validation = await validateAuth({ licenseKey }, true);
        isValid = validation.ok;
        authDiagnostics = validation.diagnostics;
        credits = validation.ok ? validation.value.credits : 0;
      }
    } else {
      const validation = await validateAuth({ licenseKey });
      isValid = validation.ok;
      authDiagnostics = validation.diagnostics;
      credits = validation.ok ? validation.value.credits : 0;
    }

    if (!isValid) {
      logEvent(requestId, startTime, 'usage.request_rejected', {
        reason: 'invalid_license',
        ...(authDiagnostics ? authDiagnosticsForLog(authDiagnostics) : {}),
      });
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

    logEvent(requestId, startTime, 'usage.request_ok', { credits_remaining: normalizedCredits });
    return jsonResponse(response);
  }

  logEvent(requestId, startTime, 'usage.request_rejected', { reason: 'missing_license' });
  return errorResponse(401, 'License required', 'You must provide a valid license_key. HyperWhisper Cloud requires a license key.');
}
