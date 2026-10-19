// CREDIT HOLDS — PER-REQUEST AND PER-SESSION RESERVATIONS (issue #2008)
//
// `validateCredits` used to compare the estimate with a balance cached for up to
// an hour, and nothing was set aside, so N concurrent requests on one key each
// saw the whole balance and each were served. Now every billed request holds
// its estimate in Redis before it runs (`lib/credit-hold-store.ts` does the
// atomic part), and the hold is settled or released exactly once when the
// request ends.
//
// Who releases a hold:
//   - a billed request: `deductCredits`, AFTER the licence API answered and the
//     balance cache was rewritten, so the next reservation counts the lowered
//     balance instead of a freed hold against the old one;
//   - a request that bills nothing or fails: `withCreditHoldScope`, the
//     wrapper every billed HTTP route runs inside, when the handler returns or
//     throws (a client abort ends the handler too);
//   - a live session: `ws-streaming-shared.ts` at session end;
//   - none of those ran (a crashed machine, a lost release call): the hold's
//     TTL, swept by the next reservation on the key.
//
// REDIS UNAVAILABLE FAILS OPEN. A store error (or an unconfigured store) marks
// the hold `degraded`, logs `credits_hold.unavailable`, and the request runs on
// the pre-hold rules — the cached-balance compare and the clamp at the balance
// seen at auth — so an Upstash outage costs the concurrency guard, never every
// paying user's dictation. Recorded as an open question for Ray.

import { AsyncLocalStorage } from 'node:async_hooks';
import type { AuthContext } from './auth';
import { createUpstashCreditHoldStore, fromMilliCredits, toMilliCredits, type CreditHoldStore } from '../lib/credit-hold-store';
import { createRedisGetter } from '../lib/redis-client';
import { toRedactedLogLine } from '../lib/log-redaction';
import { roundToTenth } from '../lib/utils';

/** Backstop for a one-shot request's hold: past the slowest request (Chirp batch, ~5 min). */
export const REQUEST_HOLD_TTL_MS = 15 * 60_000;
/** Backstop for a live session's hold. Renewed on every top-up and by the session's ping. */
export const SESSION_HOLD_TTL_MS = 10 * 60_000;

// Its own lazily built client: `lib/redis.ts` is replaced process-wide by a
// dozen suites' `mock.module`, and its exports are the only surface those mocks
// provide. Same database, same env vars.
const productionStore = createUpstashCreditHoldStore(createRedisGetter());
let storeOverride: CreditHoldStore | null = null;

/** Test-only: install a store (`createMemoryCreditHoldStore`), or `null` for the real one. */
export function setCreditHoldStoreForTests(store: CreditHoldStore | null): void {
  storeOverride = store;
}

function currentStore(): CreditHoldStore {
  return storeOverride ?? productionStore;
}

// "Not configured" is a process-wide fact, so it is said once, not per request.
let notConfiguredLogged = false;

function logHoldUnavailable(operation: string, error: unknown, licenseKey: string): void {
  const message = error instanceof Error ? error.message : String(error);
  const notConfigured = message.includes('UPSTASH_REDIS_CLOUD_URL');
  if (notConfigured) {
    if (notConfiguredLogged) return;
    notConfiguredLogged = true;
  }
  console.error(JSON.stringify({
    event: 'credits_hold.unavailable',
    operation,
    reason: notConfigured ? 'not_configured' : 'store_error',
    failOpen: true,
    error: toRedactedLogLine(error, { licenseKey }),
  }));
}

export interface CreditsShortfall {
  /** Where the overspend was caught. */
  reason: 'settle_above_hold' | 'session_above_hold' | 'session_above_balance';
  endpoint?: unknown;
  /** What the request metered. */
  creditsUsed: number;
  /** What is actually charged. */
  creditsCharged: number;
  heldCredits?: number;
}

/**
 * Overspend that got past the hold, written off rather than charged (the
 * balance never goes negative). One structured line, never the key, so "how
 * much do we eat?" is one Axiom query. The licence API logs the same event
 * when its floor at zero swallows part of a charge.
 */
export function logCreditsShortfall(shortfall: CreditsShortfall): void {
  console.warn(JSON.stringify({
    event: 'credits_shortfall',
    source: 'cloud',
    ...shortfall,
    shortfallCredits: roundToTenth(shortfall.creditsUsed - shortfall.creditsCharged),
  }));
}

export type HoldOutcome =
  | { kind: 'granted' }
  | { kind: 'refused'; availableCredits: number }
  | { kind: 'unavailable' };

// Every release in flight, so the shutdown drain can wait for them as it does
// for deductions.
const inFlightReleases = new Set<Promise<void>>();

export async function drainPendingHoldReleases(timeoutMs: number): Promise<number> {
  const pending = inFlightReleases.size;
  if (pending === 0) return 0;
  await Promise.race([
    Promise.allSettled([...inFlightReleases]),
    new Promise<void>((resolve) => setTimeout(resolve, timeoutMs)),
  ]);
  return pending;
}

// The hold a request or session took, by the auth context it was taken for.
// `validateAuth` builds a fresh context per request, so this is per request.
const holdsByAuth = new WeakMap<AuthContext, CreditHold>();

export class CreditHold {
  readonly id = crypto.randomUUID();
  /** What this hold has in Redis right now, in credits. */
  heldCredits = 0;
  /**
   * The store failed (or is not configured). The hold enforces nothing from
   * here on and callers fall back to the pre-hold rules. See the file header.
   */
  degraded = false;
  private state: 'active' | 'settling' | 'released' = 'active';
  private everGranted = false;
  // Store calls on one hold run in order, so a release can never overtake the
  // reservation it undoes and leave the hold behind until its TTL.
  private queue: Promise<unknown> = Promise.resolve();

  constructor(readonly auth: AuthContext, private readonly ttlMs: number) {
    holdsByAuth.set(auth, this);
  }

  /** Neither handed to a settle nor released. */
  get active(): boolean {
    return this.state === 'active';
  }

  get released(): boolean {
    return this.state === 'released';
  }

  private enqueue<T>(operation: () => Promise<T>): Promise<T> {
    const run = this.queue.then(operation, operation);
    this.queue = run.catch(() => {});
    return run;
  }

  /**
   * Set this hold's TOTAL to `maxCredits`, or to whatever the balance has left
   * after every other hold if that is less, as long as it is at least
   * `minCredits`. A refusal leaves the current total in place.
   */
  reserve(minCredits: number, maxCredits: number): Promise<HoldOutcome> {
    return this.enqueue(async (): Promise<HoldOutcome> => {
      if (this.state === 'released') return { kind: 'refused', availableCredits: 0 };
      if (this.degraded) return { kind: 'unavailable' };
      try {
        const result = await currentStore().reserve({
          account: this.auth.identifier,
          holdId: this.id,
          minMilli: toMilliCredits(minCredits),
          maxMilli: toMilliCredits(Math.max(minCredits, maxCredits)),
          // The same rounded balance `validateCredits` compares.
          balanceMilli: toMilliCredits(roundToTenth(this.auth.credits)),
          nowMs: Date.now(),
          ttlMs: this.ttlMs,
        });
        this.heldCredits = fromMilliCredits(result.holdMilli);
        if (result.granted) {
          this.everGranted = true;
          return { kind: 'granted' };
        }
        return { kind: 'refused', availableCredits: fromMilliCredits(result.availableMilli) };
      } catch (error) {
        this.degraded = true;
        logHoldUnavailable('reserve', error, this.auth.identifier);
        return { kind: 'unavailable' };
      }
    });
  }

  /** Renew the TTL at the current total (a live session's keep-alive). */
  renew(): Promise<HoldOutcome> {
    if (this.heldCredits <= 0) return Promise.resolve({ kind: 'unavailable' });
    return this.reserve(this.heldCredits, this.heldCredits);
  }

  /**
   * How much of `credits` this hold can cover at settle time: grows the hold
   * to `credits` if the balance still has room, and otherwise answers the most
   * it holds. A degraded hold covers everything (fail open).
   */
  async cover(credits: number): Promise<number> {
    if (this.degraded || credits <= this.heldCredits) return credits;
    const outcome = await this.reserve(this.heldCredits, credits);
    if (outcome.kind === 'unavailable') return credits;
    return Math.min(credits, this.heldCredits);
  }

  /** Hand the hold to a deduction, which releases it. `false` if someone already did. */
  beginSettle(): boolean {
    if (this.state !== 'active') return false;
    this.state = 'settling';
    return true;
  }

  /** Give the credits back. Idempotent. */
  release(): Promise<void> {
    if (this.state === 'released') return this.queue.then(() => {});
    this.state = 'released';
    if (holdsByAuth.get(this.auth) === this) holdsByAuth.delete(this.auth);
    const releasing = this.enqueue(async () => {
      if (!this.everGranted) return;
      try {
        await currentStore().release(this.auth.identifier, this.id);
        this.heldCredits = 0;
      } catch (error) {
        // The TTL frees it. Logged, because until then it narrows the balance.
        logHoldUnavailable('release', error, this.auth.identifier);
      }
    });
    inFlightReleases.add(releasing);
    void releasing.finally(() => inFlightReleases.delete(releasing));
    return releasing;
  }
}

// ---------------------------------------------------------------------------
// Request scope
// ---------------------------------------------------------------------------

const requestScope = new AsyncLocalStorage<Set<CreditHold>>();

/**
 * Run one billed HTTP request. Every hold `validateCredits` takes inside it is
 * released when `run` settles, unless a deduction took it over first (it then
 * releases it after the charge) or `detachCreditHold` handed it to work that
 * outlives the handler.
 */
export async function withCreditHoldScope<T>(run: () => Promise<T>): Promise<T> {
  const scope = new Set<CreditHold>();
  try {
    return await requestScope.run(scope, run);
  } finally {
    for (const hold of scope) {
      if (hold.active) void hold.release();
    }
  }
}

/**
 * The hold for this request's auth context, created on first use. `undefined`
 * outside `withCreditHoldScope`: such a caller holds nothing and is checked
 * against the cached balance only, as before.
 */
export function requestHoldFor(auth: AuthContext): CreditHold | undefined {
  const scope = requestScope.getStore();
  if (!scope) return undefined;
  const existing = holdsByAuth.get(auth);
  if (existing && existing.active && scope.has(existing)) return existing;
  const hold = new CreditHold(auth, REQUEST_HOLD_TTL_MS);
  scope.add(hold);
  return hold;
}

/**
 * Take this request's hold out of its scope, for work that outlives the handler
 * (the assistant's streamed answer). The caller must `release()` it.
 */
export function detachCreditHold(auth: AuthContext): CreditHold | undefined {
  const hold = holdsByAuth.get(auth);
  if (!hold) return undefined;
  requestScope.getStore()?.delete(hold);
  return hold;
}

/** The hold a deduction for `auth` settles, handed over; `undefined` if there is none. */
export function takeHoldForSettle(auth: AuthContext): CreditHold | undefined {
  const hold = holdsByAuth.get(auth);
  return hold?.beginSettle() ? hold : undefined;
}

/**
 * A live session's hold: at least `minCredits`, up to `wantCredits`. The hold
 * is returned even when degraded, so the session can still release it.
 */
export async function reserveSessionHold(
  auth: AuthContext,
  minCredits: number,
  wantCredits: number,
): Promise<{ hold: CreditHold; outcome: HoldOutcome }> {
  const hold = new CreditHold(auth, SESSION_HOLD_TTL_MS);
  const outcome = await hold.reserve(minCredits, wantCredits);
  return { hold, outcome };
}
