// CREDIT VALIDATION + DEDUCTION
// Handles preflight credit checks and post-usage deduction

import type { AuthContext } from './auth';
import { CREDITS_PER_MINUTE, DEFAULT_API_BASE_URL, LICENSE_API_TIMEOUT_MS } from '../lib/constants';
import { estimateAudioSecondsFromSize } from '../lib/audio-duration';
import { isRecord, roundToTenth, roundUpToTenth } from '../lib/utils';
import { creditsForCost } from '../lib/cost-calculator';
import { insufficientCreditsResponse } from '../lib/responses';
import { cacheLicense } from '../lib/redis';

export type CreditsResult =
  | { ok: true }
  | { ok: false; response: Response };

export interface CreditEstimateOptions {
  costEstimators?: Array<(durationSeconds: number) => number>;
}

export { estimateAudioSecondsFromSize };

export function estimateCreditsFromSize(sizeBytes: number, options: CreditEstimateOptions = {}): number {
  const estimatedSeconds = estimateAudioSecondsFromSize(sizeBytes);

  if (options.costEstimators?.length) {
    const maxEstimatedCost = Math.max(
      ...options.costEstimators.map((estimateCost) => estimateCost(estimatedSeconds))
    );
    return Math.max(0.1, creditsForCost(maxEstimatedCost));
  }

  const estimatedCredits = (estimatedSeconds / 60) * CREDITS_PER_MINUTE;
  return Math.max(0.1, roundUpToTenth(estimatedCredits));
}

export async function validateCredits(
  auth: AuthContext,
  estimatedCredits: number,
  _clientIP: string
): Promise<CreditsResult> {
  const balance = roundToTenth(auth.credits);
  if (balance < estimatedCredits) {
    return { ok: false, response: insufficientCreditsResponse(balance, estimatedCredits) };
  }
  return { ok: true };
}

/**
 * Why a license-API usage write did not land. `recordLicenseUsage` never
 * throws, so this is the only way a caller learns that a charge was dropped.
 * It carries no license key and no request metadata.
 */
export type DeductionFailure =
  | { kind: 'http'; status: number }
  | { kind: 'network'; error: string };

export interface DeductCreditsOptions {
  /**
   * Called once, after the usage write, when the license API refused it or the
   * request failed. It does not change what `deductCredits` resolves to, and a
   * callback that throws is ignored.
   */
  onFailure?: (failure: DeductionFailure) => void;
}

async function recordLicenseUsage(
  licenseKey: string,
  creditsUsed: number,
  metadata: Record<string, unknown>
): Promise<DeductionFailure | undefined> {
  const apiBase = (process.env.NEXTJS_LICENSE_API_URL || DEFAULT_API_BASE_URL).replace(/\/+$/, '');
  // Set once the license API accepts the write. A later throw (a 2xx with a
  // malformed body, a failed cache write) means the charge landed, so it is
  // warned about below but not reported as a failure.
  let accepted = false;

  try {
    const response = await fetch(`${apiBase}/api/license/credits`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        license_key: licenseKey,
        amount: creditsUsed,
        metadata,
      }),
      signal: AbortSignal.timeout(LICENSE_API_TIMEOUT_MS),
    });

    if (!response.ok) {
      const errorData: unknown = await response.json().catch(() => ({}));
      console.warn('POST /api/license/credits failed', {
        status: response.status,
        error: (isRecord(errorData) ? errorData.error : undefined) || 'Unknown error',
        creditsUsed,
      });
      return { kind: 'http', status: response.status };
    }
    accepted = true;

    const data: unknown = await response.json();
    const creditsRemaining = isRecord(data) ? data.credits_remaining : undefined;
    if (typeof creditsRemaining === 'number') {
      await cacheLicense(licenseKey, {
        isValid: true,
        credits: creditsRemaining,
        cachedAt: new Date().toISOString(),
      });
    }
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    console.warn('POST /api/license/credits network error', {
      error: message,
    });
    return accepted ? undefined : { kind: 'network', error: message };
  }
  return undefined;
}

// In-flight deduction tracking for graceful shutdown.
// Call sites fire deductCredits() without awaiting (response latency), so a
// Fly machine recycle (SIGTERM on deploy/scale-down) between the response
// flush and the redis/license write would silently drop the charge. Every
// deduction registers here so the SIGTERM handler can drain before exit.
const inFlightDeductions = new Set<Promise<number>>();

export async function drainPendingDeductions(timeoutMs: number): Promise<number> {
  const pendingCount = inFlightDeductions.size;
  if (pendingCount === 0) {
    return 0;
  }

  const allSettled = Promise.allSettled([...inFlightDeductions]);
  const timeout = new Promise<void>((resolve) => setTimeout(resolve, timeoutMs));
  await Promise.race([allSettled, timeout]);
  return pendingCount;
}

export function deductCredits(
  auth: AuthContext,
  costUsd: number,
  metadata: Record<string, unknown>,
  clientIP: string,
  options: DeductCreditsOptions = {}
): Promise<number> {
  const deduction = performDeduction(auth, costUsd, metadata, clientIP, options);
  inFlightDeductions.add(deduction);
  deduction
    .catch(() => {}) // errors are logged inside performDeduction / by callers
    .finally(() => inFlightDeductions.delete(deduction));
  return deduction;
}

async function performDeduction(
  auth: AuthContext,
  costUsd: number,
  metadata: Record<string, unknown>,
  _clientIP: string,
  options: DeductCreditsOptions
): Promise<number> {
  const creditsUsed = creditsForCost(costUsd);

  if (creditsUsed <= 0) {
    return 0;
  }

  const failure = await recordLicenseUsage(auth.identifier, creditsUsed, metadata);
  if (failure && options.onFailure) {
    try {
      options.onFailure(failure);
    } catch {
      // A reporting callback must not turn a dropped charge into a rejection.
    }
  }
  return creditsUsed;
}
