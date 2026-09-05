// PREFLIGHT RESERVATION RATE
//
// One question, one answer: for a request that asked for this provider, what
// is the highest USD/min it could actually be billed at? The transcribe route
// asks this before it reads the body, multiplies by the byte-size duration
// estimate, and reserves that many credits.
//
// This module exists so the route does not have to know THREE provider
// internals it previously carried inline:
//   1. which providers meter a keyterm add-on at all (it held the literal
//      `p === 'elevenlabs' || p === 'assemblyai'`, a second copy of a rule
//      `estimatedUsdPerMinute` already scopes internally),
//   2. that AssemblyAI has a sync fast path priced above every async catalog
//      rate, and what that rate is,
//   3. AssemblyAI's sync eligibility gate — the route imported the adapter's
//      `hasExplicitLanguage` and `SYNC_ELIGIBLE_ESTIMATED_SECONDS` directly,
//      the last adapter import left in the route after dispatch.ts,
//      audio-limits.ts and geo-availability.ts took the others.
//
// Adding a provider with a premium routing tier is a row in the table below,
// not a new `if` arm in the route. A provider with no entry prices purely off
// the registry's catalog rates.
//
// A reservation must over-, never under-, reserve: an under-reservation lets a
// request deduct more credits than were held for it. So every gate here is the
// SAFE side of the adapter's real gate — see `couldRouteThroughSync`.
//
// Two limits this module does NOT fix, both carried over from the route:
//   - It prices the REGISTRY chain. The route may then drop a geo-blocked
//     member (`reachableFromRegion`), so the reservation can be higher than any
//     provider the request can actually reach. Over-, not under-, so it is the
//     safe direction — but it is why this answers "could be billed at", not
//     "will be attempted".
//   - The prompt-token reservation for token-billed providers still lives in
//     the route (`estimatePromptInputReservationUsd`) and is applied to the
//     primary only. That holds while every token-billed provider is self-only.
//     Give one a cross-provider chain and it must move in here.

import { couldRouteThroughSync } from './assemblyai';
import {
  estimatedUsdPerMinute,
  fallbackChainFor,
  ASSEMBLYAI_SYNC_ESTIMATED_USD_PER_MINUTE,
  type SttProviderId,
} from '../lib/stt-models';

/** Everything the reservation needs to know about a request, in caller terms. */
export interface ReservationRateInput {
  /** The provider the caller asked for. Its fallback chain is derived here. */
  provider: SttProviderId;
  /**
   * The model requested for `provider`; siblings price at their own default.
   *
   * Must already be canonical — the route rejects an unresolvable model with a
   * 400 before it gets here. An unrecognised value does NOT throw: it prices at
   * the provider's DEAREST catalog row (`dearestModel` in `stt-models.ts`), so
   * an id this module cannot resolve always over-reserves. Do not "simplify"
   * that back to `models[0]`: index 0 is whatever order the catalog happens to
   * list, it is the CHEAP row for azure-mai (v2 at 1.67 credits/min sits first,
   * 1.5 at 6.0 second), and pricing below the tier the caller named is the one
   * direction this module must never go.
   */
  model?: string;
  /** True when the request asked for the medical domain. */
  medical: boolean;
  /** True when the request carries an `initial_prompt` / vocabulary. */
  hasInitialPrompt: boolean;
  /** The caller-supplied language. Absent, blank or "auto" all mean "detect". */
  language?: string;
  /** The byte-size duration estimate the reservation is sized against. */
  estimatedSeconds: number;
}

/**
 * A rate a request could be billed at that no `models[]` entry carries, because
 * it comes from a routing decision the adapter makes rather than from a model
 * the caller can select. Returns `null` when this request cannot reach it.
 */
type PremiumRouteResolver = (input: ReservationRateInput) => number | null;

const PREMIUM_ROUTES: Partial<Record<SttProviderId, PremiumRouteResolver>> = {
  // AssemblyAI's sync fast path always runs universal-3-5-pro at its own
  // published rate, higher than either async catalog tier. A short clip is
  // exactly sync's target case, so a reservation that only ever priced the
  // requested async model could be deducted beyond what it held.
  assemblyai: (input) => (couldRouteThroughSync(input)
    ? ASSEMBLYAI_SYNC_ESTIMATED_USD_PER_MINUTE
    : null),
};

/**
 * The most expensive USD/min this request could end up paying, across every
 * provider in its registry fallback chain and every routing tier any of them
 * may route it through.
 *
 * Ask this rather than pricing a chain yourself. The keyterm surcharge is
 * billed by ANY chain member that meters it whenever an initial_prompt is
 * present — a Deepgram→ElevenLabs fallback still forwards the prompt and bills
 * the +20% — so `hasInitialPrompt` is passed to every member. Providers that do
 * not meter it ignore the flag, so this never over-reserves for, say, a
 * Deepgram-only success path.
 */
export function maxReservationUsdPerMinute(input: ReservationRateInput): number {
  const rates: number[] = [];
  for (const p of fallbackChainFor(input.provider)) {
    // The primary is priced at the requested model and domain. A sibling is
    // reached by fallback, which hands it that provider's default model and
    // drops the domain (see the route's attemptModel / attemptDomain).
    const isPrimary = p === input.provider;
    const model = isPrimary ? input.model : undefined;
    const medical = isPrimary ? input.medical : false;
    rates.push(estimatedUsdPerMinute(p, model, medical, input.hasInitialPrompt));

    // A premium route is priced for EVERY chain member, not just the primary:
    // fallback forwards the same audio, language and content type, so a sibling
    // reaches its own premium tier on exactly the same terms. Today only
    // AssemblyAI has one and it is self-only, so this changes no reservation —
    // it stops a one-line edit to a `fallbackChain` in the registry from
    // silently under-reserving.
    const premium = PREMIUM_ROUTES[p]?.({ ...input, model, medical }) ?? null;
    if (premium !== null) {
      rates.push(premium);
    }
  }
  return Math.max(...rates);
}
