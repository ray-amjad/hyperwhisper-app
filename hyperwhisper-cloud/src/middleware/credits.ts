// CREDIT VALIDATION + DEDUCTION
// Handles preflight credit checks and post-usage deduction

import type { AuthContext } from './auth';
import { CREDITS_PER_MINUTE, DEFAULT_API_BASE_URL, LICENSE_API_TIMEOUT_MS } from '../lib/constants';
import { estimateAudioSecondsFromSize } from '../lib/audio-duration';
import { isRecord, roundToTenth, roundUpToTenth } from '../lib/utils';
import { creditsForCost } from '../lib/cost-calculator';
import { insufficientCreditsResponse } from '../lib/responses';
import { cacheLicense } from '../lib/redis';
import { logEvent } from '../lib/logging';
import { logCreditsShortfall, requestHoldFor, takeHoldForSettle, type CreditHold } from './credit-holds';

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

/**
 * The preflight: the estimate against the balance seen at auth, then (inside a
 * `withCreditHoldScope` request) an atomic hold of the estimate against that
 * balance minus every other in-flight hold on the key (#2008). A second call
 * for the same request resizes its hold rather than taking another.
 *
 * A refused hold answers the same 402 as a short balance, reporting what the
 * balance has left after the other holds — the credits this request could
 * actually have used. Redis unavailable fails open (see `credit-holds.ts`).
 *
 * Holding is the default. A caller that must not hold says so with
 * `{ hold: false }`; a holding call made outside `withCreditHoldScope` (a route
 * that forgot the wrapper) still fails open on the cached balance, but logs
 * `credits_hold.no_scope` so the missing guard is visible (#2043).
 */
export async function validateCredits(
  auth: AuthContext,
  estimatedCredits: number,
  _clientIP: string,
  options: { hold?: boolean } = {}
): Promise<CreditsResult> {
  const balance = roundToTenth(auth.credits);
  if (balance < estimatedCredits) {
    return { ok: false, response: insufficientCreditsResponse(balance, estimatedCredits) };
  }
  // `hold: false` is a cheap floor check ahead of the real estimate. It must not
  // hold: two requests each holding a floor can starve each other's full hold.
  if (options.hold === false) return { ok: true };
  const hold = requestHoldFor(auth);
  if (!hold) {
    // One line, never the key: the wiring slip, not the request.
    console.error(JSON.stringify({
      event: 'credits_hold.no_scope',
      failOpen: true,
      estimatedCredits,
    }));
    return { ok: true };
  }
  const outcome = await hold.reserve(estimatedCredits, estimatedCredits);
  if (outcome.kind === 'refused') {
    return {
      ok: false,
      response: insufficientCreditsResponse(roundToTenth(outcome.availableCredits), estimatedCredits),
    };
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
   * request failed, with the credits that write would have recorded. It does
   * not change what `deductCredits` resolves to, and a callback that throws is
   * ignored.
   */
  onFailure?: (failure: DeductionFailure, creditsUsed: number) => void;
  /**
   * Charge exactly these credits instead of `creditsForCost(costUsd)`. For a
   * caller that already settled the amount against its hold (a live session at
   * its end), so the deduction does not try to cover it a second time.
   */
  credits?: number;
}

/**
 * The `onFailure` for every charge site but `/assistant`, which keeps its own
 * event (#820). A dropped charge writes one `credits.deduction_failed` line
 * against the request: why it failed, the credits that were not recorded and
 * the caller's endpoint, never the license key, the IP or the request content.
 */
export function logDeductionFailure(
  requestId: string,
  startTime: number,
  endpoint: string
): NonNullable<DeductCreditsOptions['onFailure']> {
  return (failure, creditsUsed) => {
    logEvent(requestId, startTime, 'credits.deduction_failed', {
      endpoint,
      ...(failure.kind === 'http'
        ? { reason: 'http_error', status: failure.status }
        : { reason: 'network_error', error: failure.error }),
      creditsUsed,
    });
  };
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
  // Taken synchronously, so the request scope that ends right after this call
  // finds the hold already handed over and leaves its release to the charge.
  const hold = takeHoldForSettle(auth);
  const deduction = performDeduction(auth, costUsd, metadata, clientIP, options, hold);
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
  options: DeductCreditsOptions,
  hold: CreditHold | undefined
): Promise<number> {
  try {
    let creditsUsed = options.credits ?? creditsForCost(costUsd);

    if (creditsUsed <= 0) {
      return 0;
    }

    // A charge above the hold is charged as far as the balance (less every
    // other hold) still covers it; the rest is written off and logged, never
    // pushed onto a concurrent request's share of the balance.
    if (hold && options.credits === undefined) {
      const covered = await hold.cover(creditsUsed);
      if (covered < creditsUsed) {
        logCreditsShortfall({
          reason: 'settle_above_hold',
          endpoint: metadata.endpoint,
          creditsUsed,
          creditsCharged: covered,
          heldCredits: hold.heldCredits,
        });
        creditsUsed = covered;
      }
      if (creditsUsed <= 0) {
        return 0;
      }
    }

    const failure = await recordLicenseUsage(auth.identifier, creditsUsed, metadata);
    if (failure && options.onFailure) {
      try {
        options.onFailure(failure, creditsUsed);
      } catch {
        // A reporting callback must not turn a dropped charge into a rejection.
      }
    }
    return creditsUsed;
  } finally {
    // After the licence API answered and the cache holds the lowered balance:
    // released any earlier, a concurrent reservation could count this
    // request's credits a second time against the stale cached balance.
    if (hold) await hold.release();
  }
}
