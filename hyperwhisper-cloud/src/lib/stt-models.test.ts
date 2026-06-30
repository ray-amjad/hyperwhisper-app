import { describe, expect, test } from 'bun:test';
import {
  estimatedUsdPerMinute,
  getProviderDef,
  isValidProviderId,
  resolveModel,
} from './stt-models';

describe('isValidProviderId', () => {
  test('accepts known providers (legacy + new)', () => {
    for (const id of ['deepgram', 'groq', 'openai', 'gemini', 'assemblyai', 'mistral', 'soniox']) {
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
});

describe('estimatedUsdPerMinute', () => {
  test('is model-specific within a provider', () => {
    const transcribe = estimatedUsdPerMinute('openai', 'gpt-4o-transcribe');
    const mini = estimatedUsdPerMinute('openai', 'gpt-4o-mini-transcribe');
    expect(mini).toBeLessThan(transcribe);
  });

  test('adds the medical add-on only for AssemblyAI', () => {
    const base = estimatedUsdPerMinute('assemblyai', 'universal-3-pro', false);
    const medical = estimatedUsdPerMinute('assemblyai', 'universal-3-pro', true);
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
