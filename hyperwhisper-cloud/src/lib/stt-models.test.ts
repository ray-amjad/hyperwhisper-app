import { describe, expect, test } from 'bun:test';
import {
  ALL_STT_PROVIDER_IDS,
  estimatedUsdPerMinute,
  fallbackChainFor,
  getProviderDef,
  isSelfOnly,
  isValidProviderId,
  resolveModel,
  type SttProviderId,
} from './stt-models';

describe('isValidProviderId', () => {
  test('accepts known providers (legacy + new)', () => {
    for (const id of ['deepgram', 'groq', 'openai', 'gemini', 'gemini-transcribe', 'assemblyai', 'mistral', 'soniox']) {
      expect(isValidProviderId(id)).toBe(true);
    }
  });

  test('rejects unknown / spoofed providers', () => {
    expect(isValidProviderId('totally-made-up')).toBe(false);
    expect(isValidProviderId('')).toBe(false);
    expect(isValidProviderId('DEEPGRAM')).toBe(false); // case-sensitive; route lower-cases first
  });
});

describe('resolveModel', () => {
  test('empty request resolves to the provider default', () => {
    const r = resolveModel('openai', undefined);
    expect(r.ok).toBe(true);
    if (r.ok) expect(r.model.id).toBe('gpt-4o-transcribe');

    const blank = resolveModel('openai', '   ');
    expect(blank.ok).toBe(true);
    if (blank.ok) expect(blank.model.id).toBe('gpt-4o-transcribe');
  });

  test('accepts a valid model for the provider', () => {
    const r = resolveModel('openai', 'whisper-1');
    expect(r.ok).toBe(true);
    if (r.ok) expect(r.model.id).toBe('whisper-1');
  });

  test('rejects a model that belongs to a different provider (fail-closed)', () => {
    const r = resolveModel('openai', 'nova-3-medical');
    expect(r.ok).toBe(false);
    if (!r.ok) {
      expect(r.validModels).toContain('gpt-4o-transcribe');
      expect(r.validModels).not.toContain('nova-3-medical');
    }
  });

  test('single-model providers resolve their one model from a blank request', () => {
    const grok = resolveModel('grok', undefined);
    expect(grok.ok).toBe(true);
    if (grok.ok) expect(grok.model.id).toBe('');
  });

  test('flags preview models', () => {
    const r = resolveModel('gemini', 'gemini-3.1-pro-preview');
    expect(r.ok).toBe(true);
    if (r.ok) expect(r.model.isPreview).toBe(true);
  });

  test('resolves the new OpenAI transcription models', () => {
    const transcribe = resolveModel('openai', 'gpt-transcribe');
    expect(transcribe.ok).toBe(true);
    if (transcribe.ok) expect(transcribe.model.id).toBe('gpt-transcribe');

    const live = resolveModel('openai', 'gpt-live-transcribe');
    expect(live.ok).toBe(true);
    if (live.ok) expect(live.model.id).toBe('gpt-live-transcribe');
  });

  test('AssemblyAI universal-3-5-pro resolves and is now the provider default', () => {
    const explicit = resolveModel('assemblyai', 'universal-3-5-pro');
    expect(explicit.ok).toBe(true);
    if (explicit.ok) expect(explicit.model.id).toBe('universal-3-5-pro');

    const defaulted = resolveModel('assemblyai', undefined);
    expect(defaulted.ok).toBe(true);
    if (defaulted.ok) expect(defaulted.model.id).toBe('universal-3-5-pro');

    // Retired ids remain accepted only as compatibility aliases.
    const legacy = resolveModel('assemblyai', 'universal-3-pro');
    expect(legacy.ok).toBe(true);
    if (legacy.ok) expect(legacy.model.id).toBe('universal-3-5-pro');

    const slam = resolveModel('assemblyai', 'slam-1');
    expect(slam.ok).toBe(true);
    if (slam.ok) expect(slam.model.id).toBe('universal-3-5-pro');
  });

  test('gemini-transcribe resolves both speech models and defaults to the pre-recorded one', () => {
    const defaulted = resolveModel('gemini-transcribe', undefined);
    expect(defaulted.ok).toBe(true);
    if (defaulted.ok) expect(defaulted.model.id).toBe('gemini-3.5-transcribe');

    const live = resolveModel('gemini-transcribe', 'gemini-3.5-transcribe-live');
    expect(live.ok).toBe(true);
    if (live.ok) expect(live.model.id).toBe('gemini-3.5-transcribe-live');

    // The LLM models belong to the `gemini` provider, not this one, and vice
    // versa — the two are different upstream endpoints (see TRAP 1 in
    // providers/gemini-transcribe.ts) and must not cross-resolve.
    expect(resolveModel('gemini-transcribe', 'gemini-2.5-flash').ok).toBe(false);
    expect(resolveModel('gemini', 'gemini-3.5-transcribe').ok).toBe(false);
  });

  test('gemini-transcribe advertises real vocabulary support, unlike the Gemini LLM entry', () => {
    // `custom_vocabulary` is a first-class field on /v1beta/interactions; the
    // LLM path can only ask nicely in the prompt.
    for (const id of ['gemini-3.5-transcribe', 'gemini-3.5-transcribe-live']) {
      const r = resolveModel('gemini-transcribe', id);
      expect(r.ok).toBe(true);
      if (r.ok) {
        expect(r.model.supportsVocabulary).toBe(true);
        expect(r.model.isPreview).toBe(true);
      }
    }
    const llm = resolveModel('gemini', 'gemini-2.5-flash');
    if (llm.ok) expect(llm.model.supportsVocabulary).toBe(false);
  });

  test('Soniox stt-async-v5 resolves and is now the provider default', () => {
    const explicit = resolveModel('soniox', 'stt-async-v5');
    expect(explicit.ok).toBe(true);
    if (explicit.ok) expect(explicit.model.id).toBe('stt-async-v5');

    const defaulted = resolveModel('soniox', undefined);
    expect(defaulted.ok).toBe(true);
    if (defaulted.ok) expect(defaulted.model.id).toBe('stt-async-v5');

    const legacy = resolveModel('soniox', 'stt-async-v4');
    expect(legacy.ok).toBe(true);
    if (legacy.ok) expect(legacy.model.id).toBe('stt-async-v5');
  });

  test('ElevenLabs scribe_v1 is retired and now unresolvable (fail-closed)', () => {
    const r = resolveModel('elevenlabs', 'scribe_v1');
    expect(r.ok).toBe(false);
    if (!r.ok) {
      expect(r.validModels).not.toContain('scribe_v1');
      expect(r.validModels).toContain('scribe_v2');
    }
  });
});

describe('estimatedUsdPerMinute', () => {
  test('is model-specific within a provider', () => {
    const transcribe = estimatedUsdPerMinute('openai', 'gpt-4o-transcribe');
    const mini = estimatedUsdPerMinute('openai', 'gpt-4o-mini-transcribe');
    expect(mini).toBeLessThan(transcribe);
  });

  test('adds the medical add-on only for AssemblyAI', () => {
    const base = estimatedUsdPerMinute('assemblyai', 'universal-3-5-pro', false);
    const medical = estimatedUsdPerMinute('assemblyai', 'universal-3-5-pro', true);
    expect(medical).toBeGreaterThan(base);

    // A provider that doesn't meter medical ignores the flag.
    const deepgram = estimatedUsdPerMinute('deepgram', 'nova-3-medical', true);
    const deepgramPlain = estimatedUsdPerMinute('deepgram', 'nova-3-medical', false);
    expect(deepgram).toBe(deepgramPlain);
  });

  test('unknown model falls back to the provider first-model rate (no throw)', () => {
    expect(() => estimatedUsdPerMinute('openai', 'nonexistent-model')).not.toThrow();
    expect(estimatedUsdPerMinute('openai', 'nonexistent-model')).toBeGreaterThan(0);
  });
});

describe('getProviderDef', () => {
  test('new proxy providers are self-only; the cheap trio + grok are not', () => {
    expect(getProviderDef('openai').selfOnly).toBe(true);
    expect(getProviderDef('assemblyai').selfOnly).toBe(true);
    expect(getProviderDef('grok').selfOnly).toBe(false);
    expect(getProviderDef('deepgram').selfOnly).toBe(false);
  });

  test('async providers are flagged for the polling path', () => {
    expect(getProviderDef('assemblyai').async).toBe(true);
    expect(getProviderDef('soniox').async).toBe(true);
    expect(getProviderDef('openai').async).toBe(false);
  });
});

describe('fallbackChainFor', () => {
  // These four are the historical chains the transcribe route used to hold in
  // its own FALLBACK_CHAINS map. Pinned here so moving the policy into the
  // registry cannot quietly reorder or drop a provider.
  test('the cheap trio and grok keep their historical cross-provider chains', () => {
    expect(fallbackChainFor('deepgram')).toEqual(['deepgram', 'groq', 'elevenlabs']);
    expect(fallbackChainFor('groq')).toEqual(['groq', 'deepgram', 'elevenlabs']);
    expect(fallbackChainFor('elevenlabs')).toEqual(['elevenlabs', 'deepgram', 'groq']);
    expect(fallbackChainFor('grok')).toEqual(['grok', 'deepgram', 'groq', 'elevenlabs']);
  });

  test('every other provider is a chain of one — no silent substitution', () => {
    for (const id of ['azure-mai', 'google-chirp', 'openai', 'gemini', 'gemini-transcribe', 'assemblyai', 'mistral', 'soniox'] as SttProviderId[]) {
      expect(fallbackChainFor(id)).toEqual([id]);
    }
  });

  test('every provider attempts itself first', () => {
    for (const id of ALL_STT_PROVIDER_IDS) {
      expect(fallbackChainFor(id)[0]).toBe(id);
    }
  });

  test('no chain repeats a provider — a repeat would bill the same upstream twice', () => {
    for (const id of ALL_STT_PROVIDER_IDS) {
      const chain = fallbackChainFor(id);
      expect(new Set(chain).size).toBe(chain.length);
    }
  });

  // The route filters its own copy (it drops ElevenLabs in a geo-blocked
  // region). That must not edit the registry for every later request the
  // machine serves.
  test('returns a fresh array the caller may mutate', () => {
    const first = fallbackChainFor('deepgram');
    first.pop();
    expect(fallbackChainFor('deepgram')).toEqual(['deepgram', 'groq', 'elevenlabs']);
  });
});

describe('isSelfOnly', () => {
  // Pinned as a set, not re-derived from the chains: asserting
  // `isSelfOnly(id) === fallbackChainFor(id).length === 1` would restate the
  // implementation and could not fail. This list is the policy itself — adding
  // a provider to a cross-provider chain, or shortening one, has to be a
  // deliberate edit here too.
  const SELF_ONLY: SttProviderId[] = [
    'azure-mai', 'google-chirp', 'openai', 'gemini', 'gemini-transcribe', 'assemblyai', 'mistral', 'soniox',
  ];

  test('exactly the single-upstream providers are self-only', () => {
    const actual = ALL_STT_PROVIDER_IDS.filter(isSelfOnly).sort();
    expect(actual).toEqual([...SELF_ONLY].sort());
  });

  test('the cheap trio and grok keep a sibling to fall back to', () => {
    for (const id of ['deepgram', 'groq', 'elevenlabs', 'grok'] as SttProviderId[]) {
      expect(isSelfOnly(id)).toBe(false);
    }
  });

  // `selfOnly` is derived, so a def whose flag is missing would read as
  // `undefined` — which is falsy, and would silently turn every self-only 502
  // into a 429. Assert the type, not just the truthiness.
  test('every provider def carries a real boolean, never undefined', () => {
    for (const id of ALL_STT_PROVIDER_IDS) {
      expect(typeof getProviderDef(id).selfOnly).toBe('boolean');
    }
  });
});
