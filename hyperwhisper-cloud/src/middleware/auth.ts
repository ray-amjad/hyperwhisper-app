// AUTHENTICATION HELPERS
// Validates license keys and device trial identifiers

import { DEFAULT_API_BASE_URL, LICENSE_API_TIMEOUT_MS } from '../lib/constants';
import { cacheLicense, getCachedLicense } from '../lib/redis';
import { invalidLicenseResponse, licenseRequiredResponse } from '../lib/responses';
import { isRecord } from '../lib/utils';

export interface AuthContext {
  identifier: string; // license key
  credits: number;
  licenseKey: string;
}

export interface AuthInput {
  licenseKey?: string;
}

export type AuthOutcome =
  | 'accepted'
  | 'missing_key'
  | 'cached_invalid'
  | 'api_invalid'
  | 'api_invalid_json'
  | 'api_transient_status'
  | 'api_timeout'
  | 'api_network_error';

export interface AuthDiagnostics {
  source: 'missing' | 'cache' | 'api';
  outcome: AuthOutcome;
  cacheHit: boolean;
  elapsedMs: number;
  apiElapsedMs?: number;
  apiErrorCode?: string;
  apiErrorType?: 'dom_exception' | 'type_error' | 'error' | 'unknown';
  upstreamStatus?: number;
}

export function authDiagnosticsForLog(diagnostics: AuthDiagnostics): Record<string, unknown> {
  return {
    authSource: diagnostics.source,
    authOutcome: diagnostics.outcome,
    authCacheHit: diagnostics.cacheHit,
    authElapsedMs: diagnostics.elapsedMs,
    authApiElapsedMs: diagnostics.apiElapsedMs,
    authApiErrorCode: diagnostics.apiErrorCode,
    authApiErrorType: diagnostics.apiErrorType,
    authUpstreamStatus: diagnostics.upstreamStatus,
  };
}

export type AuthResult =
  | { ok: true; value: AuthContext; diagnostics: AuthDiagnostics }
  | { ok: false; response: Response; diagnostics: AuthDiagnostics };

interface ApiValidationResult {
  isValid: boolean;
  credits: number;
  outcome: AuthOutcome;
  elapsedMs: number;
  errorCode?: string;
  errorType?: AuthDiagnostics['apiErrorType'];
  upstreamStatus?: number;
}

function stableErrorCode(error: unknown): string | undefined {
  if (!isRecord(error) || !isRecord(error.cause) || typeof error.cause.code !== 'string') {
    return undefined;
  }
  return /^[A-Z0-9_]{1,40}$/.test(error.cause.code) ? error.cause.code : undefined;
}

function errorType(error: unknown): AuthDiagnostics['apiErrorType'] {
  if (error instanceof DOMException) return 'dom_exception';
  if (error instanceof TypeError) return 'type_error';
  if (error instanceof Error) return 'error';
  return 'unknown';
}

async function validateLicenseViaApi(licenseKey: string): Promise<ApiValidationResult> {
  const startedAt = performance.now();
  const apiBase = (process.env.NEXTJS_LICENSE_API_URL || DEFAULT_API_BASE_URL).replace(/\/+$/, '');
  const validateUrl = `${apiBase}/api/license/validate`;

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
    let data: { valid?: boolean; credits?: number } = {};

    let parsedResponse = true;
    try {
      const parsed: unknown = JSON.parse(responseText);
      if (isRecord(parsed)) {
        data = {
          valid: typeof parsed.valid === 'boolean' ? parsed.valid : undefined,
          credits: typeof parsed.credits === 'number' ? parsed.credits : undefined,
        };
      } else {
        parsedResponse = false;
      }
    } catch {
      // Do not log the response body. It can contain account data or an
      // upstream error that repeats a credential.
      parsedResponse = false;
    }

    const isValid = data.valid === true;
    const credits = typeof data.credits === 'number' ? data.credits : 0;
    const apiElapsedMs = Math.round(performance.now() - startedAt);

    // A 429 or 5xx (rate limit, cold start, upstream timeout, internal error)
    // is a transient failure, not proof the license is invalid — caching it
    // would lock a paying user out for the full LICENSE_CACHE_TTL_SECONDS.
    // Fail this request closed but leave the cache untouched so the next
    // request retries the API. Other 4xx responses are definitive verdicts
    // (revoked/not-found/malformed key → valid:false) and remain cacheable.
    if (response.status === 429 || response.status >= 500) {
      return {
        isValid: false,
        credits: 0,
        outcome: 'api_transient_status',
        elapsedMs: apiElapsedMs,
        upstreamStatus: response.status,
      };
    }

    await cacheLicense(licenseKey, {
      isValid,
      credits,
      cachedAt: new Date().toISOString(),
    });

    return {
      isValid,
      credits,
      outcome: isValid ? 'accepted' : parsedResponse ? 'api_invalid' : 'api_invalid_json',
      elapsedMs: apiElapsedMs,
      upstreamStatus: response.status,
    };
  } catch (error) {
    // Network/DNS/timeout failure — we could not reach the license API, so we
    // cannot conclude the license is invalid. Do NOT cache: caching invalid
    // here would lock a paying user out for the full TTL. Fail this request
    // closed; the next request retries against the API.
    const timedOut = error instanceof DOMException
      && (error.name === 'TimeoutError' || error.name === 'AbortError');
    return {
      isValid: false,
      credits: 0,
      outcome: timedOut ? 'api_timeout' : 'api_network_error',
      elapsedMs: Math.round(performance.now() - startedAt),
      errorCode: stableErrorCode(error),
      errorType: errorType(error),
    };
  }
}

export async function validateAuth(input: AuthInput, forceRefresh = false): Promise<AuthResult> {
  const startedAt = performance.now();
  const { licenseKey } = input;

  // HyperWhisper Cloud is licensed-only: a valid license key (which carries the
  // credit balance) is required for every request. There is no anonymous/trial
  // path — without a key the request is rejected before any provider work.
  if (!licenseKey) {
    return {
      ok: false,
      response: licenseRequiredResponse(),
      diagnostics: {
        source: 'missing',
        outcome: 'missing_key',
        cacheHit: false,
        elapsedMs: Math.round(performance.now() - startedAt),
      },
    };
  }

  if (!forceRefresh) {
    const cached = await getCachedLicense(licenseKey);
    if (cached) {
      if (!cached.isValid) {
        return {
          ok: false,
          response: invalidLicenseResponse(),
          diagnostics: {
            source: 'cache',
            outcome: 'cached_invalid',
            cacheHit: true,
            elapsedMs: Math.round(performance.now() - startedAt),
          },
        };
      }
      return {
        ok: true,
        value: {
          identifier: licenseKey,
          credits: cached.credits,
          licenseKey,
        },
        diagnostics: {
          source: 'cache',
          outcome: 'accepted',
          cacheHit: true,
          elapsedMs: Math.round(performance.now() - startedAt),
        },
      };
    }
  }

  const validation = await validateLicenseViaApi(licenseKey);
  const diagnostics: AuthDiagnostics = {
    source: 'api',
    outcome: validation.outcome,
    cacheHit: false,
    elapsedMs: Math.round(performance.now() - startedAt),
    apiElapsedMs: validation.elapsedMs,
    apiErrorCode: validation.errorCode,
    apiErrorType: validation.errorType,
    upstreamStatus: validation.upstreamStatus,
  };
  if (!validation.isValid) {
    return { ok: false, response: invalidLicenseResponse(), diagnostics };
  }

  return {
    ok: true,
    value: {
      identifier: licenseKey,
      credits: validation.credits,
      licenseKey,
    },
    diagnostics,
  };
}
