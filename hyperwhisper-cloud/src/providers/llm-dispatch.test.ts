import { describe, expect, test } from 'bun:test';

import {
  DEFAULT_LLM_PROVIDER,
  defaultModelFor,
  extractLLMProvider,
  fallbackProviderFor,
  resolveLLMModel,
  servedLLMName,
  LLM_PROVIDER_NAMES,
  type LLMProvider,
} from '../lib/llm-provider';
import {
  computeGeminiChatCost,
  computeMistralChatCost,
  computeOpenAIChatCost,
  type GroqUsage,
} from '../lib/cost-calculator';

const ALL_PROVIDERS: LLMProvider[] = ['cerebras', 'groq', 'anthropic', 'grok', 'openai', 'gemini', 'mistral'];

function requestWith(headers: Record<string, string>): Request {
  return new Request('https://example.com/post-process', { headers });
}

describe('extractLLMProvider', () => {
  test('maps each valid header value to its provider', () => {
    for (const provider of ALL_PROVIDERS) {
      expect(extractLLMProvider(requestWith({ 'x-llm-provider': provider }))).toBe(provider);
    }
  });

  test('is case-insensitive and trims', () => {
    expect(extractLLMProvider(requestWith({ 'x-llm-provider': '  OpenAI ' }))).toBe('openai');
  });

  test('falls back to the default provider for unknown/missing header', () => {
    expect(extractLLMProvider(requestWith({ 'x-llm-provider': 'bogus' }))).toBe(DEFAULT_LLM_PROVIDER);
    expect(extractLLMProvider(requestWith({}))).toBe(DEFAULT_LLM_PROVIDER);
  });
});

describe('resolveLLMModel', () => {
  test('echoes a valid allowlisted model id', () => {
    expect(resolveLLMModel('openai', requestWith({ 'x-llm-model': 'gpt-5-nano' }))).toBe('gpt-5-nano');
    expect(resolveLLMModel('gemini', requestWith({ 'x-llm-model': 'gemini-2.5-flash-lite' }))).toBe('gemini-2.5-flash-lite');
    expect(resolveLLMModel('mistral', requestWith({ 'x-llm-model': 'open-mistral-nemo' }))).toBe('open-mistral-nemo');
  });

  test('returns the provider default for missing or invalid model', () => {
    for (const provider of ALL_PROVIDERS) {
      expect(resolveLLMModel(provider, requestWith({}))).toBe(defaultModelFor(provider));
      expect(resolveLLMModel(provider, requestWith({ 'x-llm-model': 'not-a-real-model' }))).toBe(defaultModelFor(provider));
    }
  });

  test('rejects a model that belongs to a different provider', () => {
    // gpt-5-nano is valid for openai but not for gemini → default.
    expect(resolveLLMModel('gemini', requestWith({ 'x-llm-model': 'gpt-5-nano' }))).toBe('gemini-2.5-flash');
  });
});

describe('cost functions', () => {
  // 1,000,000 prompt + 1,000,000 completion tokens → cost == (in$ + out$) per 1M.
  const oneM: GroqUsage = { prompt_tokens: 1_000_000, completion_tokens: 1_000_000, total_tokens: 2_000_000 };

  test('computeOpenAIChatCost per model', () => {
    expect(computeOpenAIChatCost('gpt-5-mini', oneM)).toBeCloseTo(0.25 + 2.00, 6);
    expect(computeOpenAIChatCost('gpt-5-nano', oneM)).toBeCloseTo(0.05 + 0.40, 6);
    // Unknown model bills at the default (gpt-5-mini) rate, never $0.
    expect(computeOpenAIChatCost('unknown', oneM)).toBeCloseTo(0.25 + 2.00, 6);
  });

  test('computeGeminiChatCost per model', () => {
    expect(computeGeminiChatCost('gemini-2.5-flash', oneM)).toBeCloseTo(0.30 + 2.50, 6);
    expect(computeGeminiChatCost('gemini-2.5-flash-lite', oneM)).toBeCloseTo(0.10 + 0.40, 6);
  });

  test('computeMistralChatCost per model', () => {
    expect(computeMistralChatCost('mistral-small-latest', oneM)).toBeCloseTo(0.15 + 0.60, 6);
    expect(computeMistralChatCost('open-mistral-nemo', oneM)).toBeCloseTo(0.15 + 0.15, 6);
  });

  test('a small realistic usage hand-computes correctly (gpt-5-mini)', () => {
    // 1500 prompt @ 0.25/1M + 300 completion @ 2.00/1M
    const usage: GroqUsage = { prompt_tokens: 1500, completion_tokens: 300, total_tokens: 1800 };
    const expected = 1500 * (0.25 / 1_000_000) + 300 * (2.00 / 1_000_000);
    expect(computeOpenAIChatCost('gpt-5-mini', usage)).toBeCloseTo(expected, 9);
  });
});

describe('servedLLMName', () => {
  test('default model echoes the static provider name for every provider', () => {
    for (const provider of ALL_PROVIDERS) {
      expect(servedLLMName(provider, defaultModelFor(provider))).toBe(LLM_PROVIDER_NAMES[provider]);
    }
  });

  test('non-default multi-model models echo the resolved model, not the default', () => {
    expect(servedLLMName('openai', 'gpt-5-nano')).toBe('openai-gpt-5-nano');
    expect(servedLLMName('openai', 'gpt-5-nano')).not.toBe(LLM_PROVIDER_NAMES.openai);
    expect(servedLLMName('gemini', 'gemini-2.5-flash-lite')).toBe('gemini-2.5-flash-lite');
    expect(servedLLMName('mistral', 'open-mistral-nemo')).toBe('open-mistral-nemo');
  });

  test('an unknown model falls back to the static provider name', () => {
    expect(servedLLMName('openai', 'not-a-real-model')).toBe(LLM_PROVIDER_NAMES.openai);
  });
});

describe('fallback map', () => {
  test('every provider has a fallback entry', () => {
    for (const provider of ALL_PROVIDERS) {
      const fallback = fallbackProviderFor(provider);
      expect(ALL_PROVIDERS).toContain(fallback);
      expect(fallback).not.toBe(provider);
    }
  });
});
