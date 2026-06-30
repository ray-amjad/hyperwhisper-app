// CREDIT VALIDATION + DEDUCTION
// Handles preflight credit checks and post-usage deduction

import type { AuthContext } from './auth';
import { BYTES_PER_MINUTE_ESTIMATE, CREDITS_PER_MINUTE, DEFAULT_API_BASE_URL, LICENSE_API_TIMEOUT_MS } from '../lib/constants';
import { roundToTenth, roundUpToTenth } from '../lib/utils';
import { creditsForCost } from '../lib/cost-calculator';
import { insufficientCreditsResponse } from '../lib/responses';
import { cacheLicense } from '../lib/redis';

// Minimum encoded bitrate expected from clients for conservative credit estimation.
const MIN_ESTIMATED_SECONDS = 10;

export type CreditsResult =
  | { ok: true }
  | { ok: false; response: Response };

export interface CreditEstimateOptions {
  costEstimators?: Array<(durationSeconds: number) => number>;
}

export function estimateAudioSecondsFromSize(sizeBytes: number): number {
  const estimatedMinutes = sizeBytes / BYTES_PER_MINUTE_ESTIMATE;
  return Math.max(MIN_ESTIMATED_SECONDS, estimatedMinutes * 60);
}

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

async function recordLicenseUsage(
  licenseKey: string,
  creditsUsed: number,
  metadata: Record<string, unknown>
): Promise<void> {
  const apiBase = (process.env.NEXTJS_LICENSE_API_URL || DEFAULT_API_BASE_URL).replace(/\/+$/, '');

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
      const errorData = await response.json().catch(() => ({}));
      console.warn('POST /api/license/credits failed', {
        status: response.status,
        error: (errorData as Record<string, unknown>).error || 'Unknown error',
        creditsUsed,
      });
      return;
    }

    const data = await response.json() as { credits_remaining?: number; credits_deducted?: number };
    if (typeof data.credits_remaining === 'number') {
      await cacheLicense(licenseKey, {
        isValid: true,
        credits: data.credits_remaining,
        cachedAt: new Date().toISOString(),
      });
    }
  } catch (error) {
    console.warn('POST /api/license/credits network error', {
      error: error instanceof Error ? error.message : String(error),
    });
  }
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
  clientIP: string
): Promise<number> {
  const deduction = performDeduction(auth, costUsd, metadata, clientIP);
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
  _clientIP: string
): Promise<number> {
  const creditsUsed = creditsForCost(costUsd);

  if (creditsUsed <= 0) {
    return 0;
  }

  await recordLicenseUsage(auth.identifier, creditsUsed, metadata);
  return creditsUsed;
}
