// CREDIT VALIDATION + DEDUCTION
// Handles preflight credit checks and post-usage deduction

import { readCreditPoolId, type AuthContext } from './auth';
import { LICENSE_API_TIMEOUT_MS, licenseApiBaseUrl } from '../lib/constants';
import { isRecord, roundToTenth } from '../lib/utils';
import { creditsForCost } from '../lib/cost-calculator';
import { insufficientCreditsResponse } from '../lib/responses';
import { cacheLicense } from '../lib/redis';
import { logEvent } from '../lib/logging';
import { machineLabels } from '../lib/fly-machine';
import { backgroundWork } from '../lib/background-work';
import { logCreditsShortfall, requestHoldFor, takeHoldForSettle, type CreditHold } from './credit-holds';

export type CreditsResult =
  | { ok: true }
  | { ok: false; response: Response };

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
 * `{ hold: false }`. `{ provisional: true }` takes a provisional hold: one that
 * bounds how many requests a balance can have in flight before their cost is
 * known (#2483), but that no other request's firm hold counts, so concurrent
 * floors cannot starve each other's real reservation. The request's next
 * (firm) call replaces it. A holding call made outside `withCreditHoldScope` (a route
 * that forgot the wrapper) still fails open on the cached balance, but logs
 * `credits_hold.no_scope` so the missing guard is visible (#2043).
 */
export async function validateCredits(
  auth: AuthContext,
  estimatedCredits: number,
  options: { hold?: boolean; provisional?: boolean } = {}
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
  const outcome = await hold.reserve(estimatedCredits, estimatedCredits, { provisional: options.provisional === true });
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
 *
 * The network arm also says when and how the request died (#3015): a reset a
 * few ms after the fetch went out points at a stale pooled socket, one near
 * `LICENSE_API_TIMEOUT_MS` at the far end giving up mid-request.
 * - `chargeElapsedMs`: ms from just before the fetch to the throw. Not
 *   `elapsedMs`, which `logEvent` already writes as the time since the
 *   cloud request itself started.
 * - `errorCode`: see `networkErrorCode`.
 * - `region`: the Fly region, `local` off Fly (`machineLabels`).
 */
export type DeductionFailure =
  | { kind: 'http'; status: number }
  | { kind: 'network'; error: string; chargeElapsedMs: number; errorCode: string; region: string };

/** How deep `networkErrorCode` follows `cause`: undici nests one level, never a loop. */
const MAX_CAUSE_DEPTH = 5;
/** A code is a short token (`ECONNRESET`, `UND_ERR_SOCKET`); anything else is not logged. */
const ERROR_CODE_PATTERN = /^[A-Za-z0-9_.:-]{1,64}$/;

/**
 * A short code for why the licence API fetch threw. The first string `code` on
 * the error or its `cause` chain (Bun's fetch errors carry `code` themselves;
 * Node's undici puts it on `cause`), else the error's `name` (`TimeoutError`
 * from `AbortSignal.timeout`, `AbortError`, `TypeError`), else `unknown` for a
 * thrown non-Error. Never the message: that is `error`.
 */
export function networkErrorCode(error: unknown): string {
  let current: unknown = error;
  for (let depth = 0; depth < MAX_CAUSE_DEPTH && isRecord(current); depth += 1) {
    const code = current.code;
    if (typeof code === 'string' && ERROR_CODE_PATTERN.test(code)) return code;
    current = current.cause;
  }
  if (error instanceof Error && ERROR_CODE_PATTERN.test(error.name)) return error.name;
  return 'unknown';
}

export interface DeductCreditsOptions {
  /**
   * Called once, after the usage write, when the license API refused it or the
   * request failed, with the credits that write would have recorded. It does
   * not change what `deductCredits` resolves to, and a callback that throws is
   * ignored.
   */
  onFailure?: (failure: DeductionFailure, creditsUsed: number) => void;
  /**
   * Charge exactly these credits instead of `creditsForCost(costUsd)`: the
   * caller metered its own amount (a live session at its end).
   */
  exactCredits?: number;
  /**
   * The caller already covered the amount against its hold (`hold.cover`), so
   * the deduction charges it as given and does not try to cover it a second
   * time.
   */
  coveredByHold?: boolean;
}

/**
 * The `onFailure` for every charge site (#820). A dropped charge writes one
 * `credits.deduction_failed` line against the request: why it failed, the
 * credits that were not recorded and the caller's endpoint, never the license
 * key, the IP or the request content.
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
        : {
            reason: 'network_error',
            error: failure.error,
            chargeElapsedMs: failure.chargeElapsedMs,
            errorCode: failure.errorCode,
            region: failure.region,
          }),
      creditsUsed,
    });
  };
}

/**
 * The license API's 400s for a key it will not charge: `License key not found`
 * and `License is <status>` (any status but granted). Its other 400s (a bad
 * amount, a missing field) are about the request and say nothing of the key.
 */
function isLicenseRefusal(status: number, error: unknown): boolean {
  if (status !== 400 || typeof error !== 'string') return false;
  return error === 'License key not found' || error.startsWith('License is ');
}

async function recordLicenseUsage(
  licenseKey: string,
  creditsUsed: number,
  metadata: Record<string, unknown>
): Promise<DeductionFailure | undefined> {
  const apiBase = licenseApiBaseUrl();
  // Set once the license API accepts the write. A later throw (a 2xx with a
  // malformed body, a failed cache write) means the charge landed, so it is
  // warned about below but not reported as a failure.
  let accepted = false;
  // Before the try, so the catch can time any throw; reset just before the fetch.
  let sentAt = performance.now();

  try {
    // Taken BEFORE the request goes out: the licence cache orders replies by
    // when the site was asked, the pool balance (#2026, see
    // `WRITE_POOL_BALANCE_SCRIPT`) and the key's own entry (#1899, see
    // `WRITE_LICENSE_SCRIPT`) alike.
    const issuedAtMs = Date.now();
    sentAt = performance.now();
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
      const errorMessage = isRecord(errorData) ? errorData.error : undefined;
      console.warn('POST /api/license/credits failed', {
        status: response.status,
        error: errorMessage || 'Unknown error',
        creditsUsed,
      });
      if (isLicenseRefusal(response.status, errorMessage)) {
        // The site refused the KEY, not the write (revoked, refunded, not
        // found): the cached "valid, N credits" entry is now wrong, and left
        // alone it admits the key for the rest of its TTL (#1899). Cache the
        // refusal so the next request stops at `cached_invalid`. A 5xx, 429 or
        // network failure says nothing about the key and leaves the cache be.
        // No pool id: only the key's own entry changes.
        await cacheLicense(licenseKey, {
          isValid: false,
          credits: 0,
          cachedAt: new Date().toISOString(),
          issuedAtMs,
        });
      }
      return { kind: 'http', status: response.status };
    }
    accepted = true;

    const data: unknown = await response.json();
    const creditsRemaining = isRecord(data) ? data.credits_remaining : undefined;
    if (typeof creditsRemaining === 'number') {
      // The pool id comes from THIS reply, never from `auth`: the rewrite
      // renews the TTL, so carrying the auth context's id forward would keep a
      // missing one (a cache older than the pool) or a stale one (a rotated
      // secret) alive for as long as the key stays busy (#2026). None in the
      // reply: the site has no secret, so holds go per key.
      const creditPoolId = isRecord(data) ? readCreditPoolId(data.credit_pool_id) : undefined;
      await cacheLicense(licenseKey, {
        isValid: true,
        credits: creditsRemaining,
        cachedAt: new Date().toISOString(),
        ...(creditPoolId ? { creditPoolId } : {}),
        issuedAtMs,
      });
    }
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    console.warn('POST /api/license/credits network error', {
      error: message,
    });
    if (accepted) return undefined;
    return {
      kind: 'network',
      error: message,
      chargeElapsedMs: Math.round(performance.now() - sentAt),
      errorCode: networkErrorCode(error),
      region: machineLabels().region,
    };
  }
  return undefined;
}

// In-flight deduction tracking for graceful shutdown.
// Call sites await only the charged amount, never the licence write (response
// latency), so a Fly machine recycle (SIGTERM on deploy/scale-down) between the
// response flush and the redis/license write would silently drop the charge. Every
// deduction registers here so the SIGTERM handler can drain before exit.
const inFlightDeductions = backgroundWork('deductions');

export function drainPendingDeductions(timeoutMs: number): Promise<number> {
  return inFlightDeductions.drain(timeoutMs);
}

/**
 * One charge, in two steps. `credits` is the number charged: priced once
 * (`creditsForCost(costUsd)`, or the caller's `exactCredits`) and capped at what
 * the hold covers. It settles before the licence write goes out, and every
 * response that reports credits used reads it, so a client is never told about
 * credits that were not charged. The one exception is a stalled Redis: when
 * the cap is not known within SETTLE_REPORT_DEADLINE_MS, `credits` resolves to
 * the uncapped price instead (see `withReportDeadline`), which can be more than
 * the charge that later settles. `recorded` resolves to the charged number once
 * the write is done, landed or not.
 */
export interface Deduction {
  readonly credits: Promise<number>;
  readonly recorded: Promise<number>;
}

export function deductCredits(
  auth: AuthContext,
  costUsd: number,
  metadata: Record<string, unknown>,
  options: DeductCreditsOptions = {}
): Deduction {
  // Taken synchronously, so the request scope that ends right after this call
  // finds the hold already handed over and leaves its release to the charge.
  const hold = takeHoldForSettle(auth);
  const charged = settleAgainstHold(costUsd, metadata, options, hold);
  // Errors are logged inside performDeduction / by callers.
  const recorded = inFlightDeductions.track(performDeduction(auth, charged, metadata, options, hold));
  const uncapped = options.exactCredits ?? creditsForCost(costUsd);
  return { credits: withReportDeadline(charged, uncapped), recorded };
}

/**
 * How long a response waits to learn the capped charge. Settling above the
 * hold asks Redis to grow it, and the Upstash client has no request timeout of
 * its own: a stalled Redis must not hold a transcript the upstream already
 * produced (and billed). Past the deadline the response reports the uncapped
 * amount, as it did before the cap existed; the charge itself still settles.
 */
const SETTLE_REPORT_DEADLINE_MS = 2_000;

function withReportDeadline(charged: Promise<number>, uncapped: number): Promise<number> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const deadline = new Promise<number>((resolve) => {
    timer = setTimeout(() => {
      console.warn(JSON.stringify({ event: 'credits.settle_report_deadline', deadlineMs: SETTLE_REPORT_DEADLINE_MS }));
      resolve(uncapped);
    }, SETTLE_REPORT_DEADLINE_MS);
  });
  return Promise.race([charged, deadline]).finally(() => clearTimeout(timer));
}

/** What this deduction charges: the amount, capped at what the hold covers. */
async function settleAgainstHold(
  costUsd: number,
  metadata: Record<string, unknown>,
  options: DeductCreditsOptions,
  hold: CreditHold | undefined
): Promise<number> {
  const creditsUsed = options.exactCredits ?? creditsForCost(costUsd);
  if (!hold || options.coveredByHold) return creditsUsed;

  // A charge above the hold is charged as far as the balance (less every
  // other hold) still covers it; the rest is written off and logged, never
  // pushed onto a concurrent request's share of the balance.
  const covered = await hold.cover(creditsUsed);
  if (covered < creditsUsed) {
    logCreditsShortfall({
      reason: 'settle_above_hold',
      endpoint: metadata.endpoint,
      creditsUsed,
      creditsCharged: covered,
      heldCredits: hold.heldCredits,
    });
  }
  return covered;
}

async function performDeduction(
  auth: AuthContext,
  credits: Promise<number>,
  metadata: Record<string, unknown>,
  options: DeductCreditsOptions,
  hold: CreditHold | undefined
): Promise<number> {
  try {
    const creditsUsed = await credits;
    if (creditsUsed <= 0) {
      return 0;
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
