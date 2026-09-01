import { describe, expect, test } from 'bun:test';
import { maxReservationUsdPerMinute, type ReservationRateInput } from './reservation';
import { estimatedUsdPerMinute, fallbackChainFor, ASSEMBLYAI_SYNC_ESTIMATED_USD_PER_MINUTE } from '../lib/stt-models';

/** A long-enough clip that no provider's short-clip routing tier applies, so a
 * case can isolate the plain catalog-rate signal. */
const LONG_CLIP_SECONDS = 600;
/** Short and non-medical with an explicit language — AssemblyAI sync's target case. */
const SHORT_CLIP_SECONDS = 6;

function input(overrides: Partial<ReservationRateInput> = {}): ReservationRateInput {
  return {
    provider: 'deepgram',
    medical: false,
    hasInitialPrompt: false,
    estimatedSeconds: LONG_CLIP_SECONDS,
    ...overrides,
  };
}

describe('maxReservationUsdPerMinute — chain pricing', () => {
  test('reserves the most expensive member of the fallback chain, not the requested provider', () => {
    // Deepgram ($0.0055/min) falls back to Groq then ElevenLabs ($0.00983/min).
    // A reservation sized at Deepgram's own rate would be short if the request
    // actually lands on ElevenLabs.
    const reserved = maxReservationUsdPerMinute(input({ provider: 'deepgram' }));
    const chainRates = fallbackChainFor('deepgram').map((p) => estimatedUsdPerMinute(p));
    expect(reserved).toBe(Math.max(...chainRates));
    expect(reserved).toBeGreaterThan(estimatedUsdPerMinute('deepgram'));
  });

  test('a self-only provider reserves exactly its own catalog rate', () => {
    expect(maxReservationUsdPerMinute(input({ provider: 'azure-mai' })))
      .toBe(estimatedUsdPerMinute('azure-mai'));
  });

  test('the requested model prices the primary; siblings price at their own default', () => {
    // universal-2 is cheaper than the universal-3-5-pro default, and AssemblyAI
    // is self-only, so the requested model is the whole answer here.
    const universal2 = maxReservationUsdPerMinute(input({ provider: 'assemblyai', model: 'universal-2' }));
    const universal3Pro = maxReservationUsdPerMinute(input({ provider: 'assemblyai', model: 'universal-3-5-pro' }));
    expect(universal2).toBeLessThan(universal3Pro);
    expect(universal2).toBe(estimatedUsdPerMinute('assemblyai', 'universal-2'));
  });

  test('the medical add-on applies to the primary only', () => {
    const plain = maxReservationUsdPerMinute(input({ provider: 'assemblyai', model: 'universal-2' }));
    const medical = maxReservationUsdPerMinute(input({ provider: 'assemblyai', model: 'universal-2', medical: true }));
    expect(medical).toBeGreaterThan(plain);
  });
});

describe('maxReservationUsdPerMinute — keyterm surcharge', () => {
  test('a sibling that meters keyterms is reserved for, even when the primary does not', () => {
    // Deepgram charges no surcharge, but its chain ends at ElevenLabs
    // (scribe_v2), which forwards the initial_prompt and bills +20%.
    const base = maxReservationUsdPerMinute(input({ provider: 'deepgram' }));
    const withPrompt = maxReservationUsdPerMinute(input({ provider: 'deepgram', hasInitialPrompt: true }));
    expect(withPrompt).toBeGreaterThan(base);
  });

  test('a chain whose members all ignore keyterms reserves the same with and without a prompt', () => {
    // The route used to name the metering providers itself
    // (`p === 'elevenlabs' || p === 'assemblyai'`). The flag now goes to every
    // member unconditionally, so this asserts the registry — not the caller —
    // is what scopes the add-on.
    for (const provider of ['mistral', 'azure-mai', 'google-chirp'] as const) {
      const base = maxReservationUsdPerMinute(input({ provider }));
      const withPrompt = maxReservationUsdPerMinute(input({ provider, hasInitialPrompt: true }));
      expect(withPrompt).toBe(base);
    }
  });

  test('keyterms are free on AssemblyAI universal-2 and billed on universal-3-5-pro', () => {
    const u2 = maxReservationUsdPerMinute(input({ provider: 'assemblyai', model: 'universal-2' }));
    const u2Prompt = maxReservationUsdPerMinute(input({ provider: 'assemblyai', model: 'universal-2', hasInitialPrompt: true }));
    expect(u2Prompt).toBe(u2);

    const pro = maxReservationUsdPerMinute(input({ provider: 'assemblyai', model: 'universal-3-5-pro' }));
    const proPrompt = maxReservationUsdPerMinute(input({ provider: 'assemblyai', model: 'universal-3-5-pro', hasInitialPrompt: true }));
    expect(proPrompt).toBeGreaterThan(pro);
  });
});

describe('maxReservationUsdPerMinute — AssemblyAI sync premium route', () => {
  const syncEligible = { provider: 'assemblyai' as const, estimatedSeconds: SHORT_CLIP_SECONDS, language: 'en' };

  test('a short, non-medical, explicit-language clip reserves the sync rate', () => {
    // Sync always runs universal-3-5-pro at its own published rate, above
    // either async tier. Reserving only the async catalog rate for the
    // requested model would let the request deduct more than it held.
    for (const model of ['universal-2', 'universal-3-5-pro'] as const) {
      const reserved = maxReservationUsdPerMinute(input({ ...syncEligible, model }));
      expect(reserved).toBe(ASSEMBLYAI_SYNC_ESTIMATED_USD_PER_MINUTE);
      expect(reserved).toBeGreaterThan(estimatedUsdPerMinute('assemblyai', model));
    }
  });

  test('a clip over the sync threshold reserves only the async catalog rate', () => {
    const reserved = maxReservationUsdPerMinute(input({
      ...syncEligible, model: 'universal-2', estimatedSeconds: 200,
    }));
    expect(reserved).toBe(estimatedUsdPerMinute('assemblyai', 'universal-2'));
    expect(reserved).toBeLessThan(ASSEMBLYAI_SYNC_ESTIMATED_USD_PER_MINUTE);
  });

  test('medical and auto/absent language both fall outside sync, so neither reserves the sync rate', () => {
    // Over-reserving here would wrongly reject a low-balance account for a
    // request that can only ever take the cheaper async path.
    const medical = maxReservationUsdPerMinute(input({ ...syncEligible, model: 'universal-2', medical: true }));
    expect(medical).toBeLessThan(ASSEMBLYAI_SYNC_ESTIMATED_USD_PER_MINUTE);

    for (const language of [undefined, 'auto', 'AUTO', '   ']) {
      const reserved = maxReservationUsdPerMinute(input({ ...syncEligible, model: 'universal-2', language }));
      expect(reserved).toBe(estimatedUsdPerMinute('assemblyai', 'universal-2'));
    }
  });

  test('a provider with no premium route is unaffected by a sync-shaped request', () => {
    // Only the REQUESTED provider can take its own premium route. Deepgram
    // reaching AssemblyAI is impossible (not in its chain), and a short clip
    // must not inflate anyone else's reservation.
    const short = maxReservationUsdPerMinute(input({ provider: 'deepgram', estimatedSeconds: SHORT_CLIP_SECONDS, language: 'en' }));
    const long = maxReservationUsdPerMinute(input({ provider: 'deepgram' }));
    expect(short).toBe(long);
  });
});
