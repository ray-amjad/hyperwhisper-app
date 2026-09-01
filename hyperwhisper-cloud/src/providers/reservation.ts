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
  /** The model requested for `provider`; siblings price at their own default. */
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
  assemblyai: (input) => (couldRouteThroughSync({
    medical: input.medical,
    language: input.language,
    estimatedSeconds: input.estimatedSeconds,
  })
    ? ASSEMBLYAI_SYNC_ESTIMATED_USD_PER_MINUTE
    : null),
};

/**
 * The most expensive USD/min this request could end up paying, across every
 * provider its fallback chain may reach and every routing tier those providers
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
  const rates = fallbackChainFor(input.provider).map((p) => estimatedUsdPerMinute(
    p,
    p === input.provider ? input.model : undefined,
    p === input.provider ? input.medical : false,
    input.hasInitialPrompt,
  ));
  // Only the REQUESTED provider can take its own premium route. A sibling is
  // reached by fallback, which hands it the plain request and its default model.
  const premium = PREMIUM_ROUTES[input.provider]?.(input) ?? null;
  if (premium !== null) {
    rates.push(premium);
  }
  return Math.max(...rates);
}
