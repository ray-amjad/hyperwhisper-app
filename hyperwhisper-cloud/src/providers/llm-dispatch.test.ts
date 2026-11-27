import { describe, expect, test } from 'bun:test';

import {
  defaultModelFor,
  extractLLMProvider,
  fallbackProviderFor,
  resolveLLMModel,
  servedLLMName,
  shouldFallback,
  type LLMProvider,
} from './llm-provider';
import { type GroqUsage } from '../lib/cost-calculator';
import { computeLLMChatCost, LLM_MODELS, LLM_PROVIDERS, llmModelsFor } from '../lib/llm-models';
import { LLMRequestError } from './llm-errors';

const ALL_PROVIDERS: LLMProvider[] = ['cerebras', 'groq', 'anthropic', 'grok', 'openai', 'gemini', 'mistral'];

/** The provider's static label: its default row's served name. */
function providerLabel(provider: LLMProvider): string {
  return servedLLMName(provider, defaultModelFor(provider));
}

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
    expect(extractLLMProvider(requestWith({ 'x-llm-provider': 'bogus' }))).toBe('cerebras');
    expect(extractLLMProvider(requestWith({}))).toBe('cerebras');
  });
});

describe('resolveLLMModel', () => {
  test('echoes a valid allowlisted model id', () => {
    expect(resolveLLMModel('openai', requestWith({ 'x-llm-model': 'gpt-5.6-luna' }))).toBe('gpt-5.6-luna');
    expect(resolveLLMModel('gemini', requestWith({ 'x-llm-model': 'gemini-2.5-flash-lite' }))).toBe('gemini-2.5-flash-lite');
    expect(resolveLLMModel('gemini', requestWith({ 'x-llm-model': 'gemini-3.8-flash' }))).toBe('gemini-3.8-flash');
    expect(resolveLLMModel('mistral', requestWith({ 'x-llm-model': 'mistral-small-latest' }))).toBe('mistral-small-latest');
  });

  test('the retired gpt-5-mini and gpt-5-nano ids resolve to gpt-5.6-luna', () => {
    // OpenAI removes both snapshots 2026-12-11 (#1018). Old clients still send
    // them; they must route to luna, never to a dead model and never error.
    expect(defaultModelFor('openai')).toBe('gpt-5.6-luna');
    expect(resolveLLMModel('openai', requestWith({ 'x-llm-model': 'gpt-5-mini' }))).toBe('gpt-5.6-luna');
    expect(resolveLLMModel('openai', requestWith({ 'x-llm-model': 'gpt-5-nano' }))).toBe('gpt-5.6-luna');
  });

  test('the retired open-mistral-nemo id falls back to the mistral default', () => {
    // Old clients still send it; it must never error, just resolve to the default.
    expect(resolveLLMModel('mistral', requestWith({ 'x-llm-model': 'open-mistral-nemo' }))).toBe('mistral-small-latest');
  });

  test('an old client sending a Claude Haiku 4.5 id is served Haiku 5.5, never a 400', () => {
    // No alias table: a retired id is not allowlisted, so it takes the provider
    // default, the same fallback as open-mistral-nemo above.
    expect(defaultModelFor('anthropic')).toBe('claude-haiku-5-5');
    expect(resolveLLMModel('anthropic', requestWith({ 'x-llm-model': 'claude-haiku-5-5' }))).toBe('claude-haiku-5-5');
    expect(resolveLLMModel('anthropic', requestWith({ 'x-llm-model': 'claude-haiku-4-5' }))).toBe('claude-haiku-5-5');
    expect(resolveLLMModel('anthropic', requestWith({ 'x-llm-model': 'claude-haiku-4-5-20251001' }))).toBe('claude-haiku-5-5');
    expect(resolveLLMModel('anthropic', requestWith({ 'x-llm-model': 'Claude-Haiku-4-5-20251001' }))).toBe('claude-haiku-5-5');
  });

  test('returns the provider default for missing or invalid model', () => {
    for (const provider of ALL_PROVIDERS) {
      expect(resolveLLMModel(provider, requestWith({}))).toBe(defaultModelFor(provider));
      expect(resolveLLMModel(provider, requestWith({ 'x-llm-model': 'not-a-real-model' }))).toBe(defaultModelFor(provider));
    }
  });

  test('rejects a model that belongs to a different provider', () => {
    // gpt-5.6-luna is valid for openai but not for gemini → default.
    expect(resolveLLMModel('gemini', requestWith({ 'x-llm-model': 'gpt-5.6-luna' }))).toBe('gemini-3.8-flash');
  });

  test('the gemini default is 3.8 Flash, and the 2.5 ids stay selectable', () => {
    // Google limits gemini-2.5-* to past users, so a blank, typo'd or BYOK-only
    // gemini id falls back to 3.8 Flash. 2.5 Flash Lite is still served by name;
    // the retired 2.5 Flash is served as 3.8 Flash (Ray, #3009).
    expect(defaultModelFor('gemini')).toBe('gemini-3.8-flash');
    expect(resolveLLMModel('gemini', requestWith({}))).toBe('gemini-3.8-flash');
    expect(resolveLLMModel('gemini', requestWith({ 'x-llm-model': 'gemini-3.7-flash' }))).toBe('gemini-3.8-flash');
    expect(resolveLLMModel('gemini', requestWith({ 'x-llm-model': 'gemini-3.8-flassh' }))).toBe('gemini-3.8-flash');
    expect(resolveLLMModel('gemini', requestWith({ 'x-llm-model': 'gemini-2.5-flash' }))).toBe('gemini-3.8-flash');
    expect(resolveLLMModel('gemini', requestWith({ 'x-llm-model': 'gemini-2.5-flash-lite' }))).toBe('gemini-2.5-flash-lite');
  });
});

describe('cost functions', () => {
  // 1,000,000 prompt + 1,000,000 completion tokens → cost == (in$ + out$) per 1M.
  const oneM: GroqUsage = { prompt_tokens: 1_000_000, completion_tokens: 1_000_000, total_tokens: 2_000_000 };

  test('openai bills every id at the one luna rate', () => {
    expect(computeLLMChatCost('openai', 'gpt-5.6-luna', oneM)).toBeCloseTo(0.20 + 1.20, 6);
    // Unknown model bills at the default (gpt-5.6-luna) rate, never $0 — and so
    // do the retired gpt-5-mini / gpt-5-nano ids.
    expect(computeLLMChatCost('openai', 'unknown', oneM)).toBeCloseTo(0.20 + 1.20, 6);
    expect(computeLLMChatCost('openai', 'gpt-5-mini', oneM)).toBeCloseTo(0.20 + 1.20, 6);
    expect(computeLLMChatCost('openai', 'gpt-5-nano', oneM)).toBeCloseTo(0.20 + 1.20, 6);
  });

  test('gemini bills per model', () => {
    // Retired (#3009): bills at the 3.8 Flash default it is served as.
    expect(computeLLMChatCost('gemini', 'gemini-2.5-flash', oneM)).toBe(computeLLMChatCost('gemini', 'gemini-3.8-flash', oneM));
    expect(computeLLMChatCost('gemini', 'gemini-2.5-flash-lite', oneM)).toBeCloseTo(0.10 + 0.40, 6);
    // gemini-3.8-flash has no hand-written price line: its rate is pinned
    // generically, against the catalog, by the parity block in
    // src/lib/cost-calculator.test.ts, and its introductory price expires
    // 2026-12-31 (see the priceNote on its cloud-pp-catalog.json row). Restating
    // the number here would make that expiry a four-file edit.
  });

  test('mistral bills per model', () => {
    expect(computeLLMChatCost('mistral', 'mistral-small-latest', oneM)).toBeCloseTo(0.15 + 0.60, 6);
    // The retired open-mistral-nemo bills at the default (mistral-small-latest) rate.
    expect(computeLLMChatCost('mistral', 'open-mistral-nemo', oneM)).toBeCloseTo(0.15 + 0.60, 6);
  });

  test('a small realistic usage hand-computes correctly (gpt-5.6-luna)', () => {
    // 1500 prompt @ 0.20/1M + 300 completion @ 1.20/1M
    const usage: GroqUsage = { prompt_tokens: 1500, completion_tokens: 300, total_tokens: 1800 };
    const expected = 1500 * (0.20 / 1_000_000) + 300 * (1.20 / 1_000_000);
    expect(computeLLMChatCost('openai', 'gpt-5.6-luna', usage)).toBeCloseTo(expected, 9);
  });
});

describe('servedLLMName', () => {
  test('each provider default serves its public label (the X-LLM-Provider value clients see)', () => {
    const labels = Object.fromEntries(ALL_PROVIDERS.map((provider) => [provider, providerLabel(provider)]));
    expect(labels).toEqual({
      cerebras: 'cerebras-gpt-oss-120b',
      groq: 'groq-gpt-oss-120b',
      anthropic: 'claude-haiku-5-5',
      grok: 'xai-grok-4.3',
      openai: 'openai-gpt-5.6-luna',
      gemini: 'gemini-3.8-flash',
      mistral: 'mistral-small-latest',
    });
  });

  test('non-default multi-model models echo the resolved model, not the default', () => {
    expect(servedLLMName('gemini', 'gemini-2.5-flash-lite')).toBe('gemini-2.5-flash-lite');
    expect(servedLLMName('gemini', 'gemini-2.5-flash-lite')).not.toBe(providerLabel('gemini'));
    expect(servedLLMName('gemini', 'gemini-3.8-flash')).toBe('gemini-3.8-flash');
  });

  test('the retired open-mistral-nemo has no served name of its own', () => {
    expect(servedLLMName('mistral', 'open-mistral-nemo')).toBe(providerLabel('mistral'));
  });

  test('the retired gpt-5-mini / gpt-5-nano have no served name of their own', () => {
    expect(servedLLMName('openai', 'gpt-5-mini')).toBe(providerLabel('openai'));
    expect(servedLLMName('openai', 'gpt-5-nano')).toBe(providerLabel('openai'));
  });

  test('an unknown model falls back to the static provider name', () => {
    expect(servedLLMName('openai', 'not-a-real-model')).toBe(providerLabel('openai'));
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

describe('shouldFallback', () => {
  test('accepts only normalized 5xx and 429 request errors', () => {
    expect(shouldFallback(new LLMRequestError('upstream failed', 500, 'openai'))).toBe(true);
    expect(shouldFallback(new LLMRequestError('upstream failed', 599, 'openai'))).toBe(true);
    // #1565: the fallback vendor has its own rate limit, so a 429 fails over.
    expect(shouldFallback(new LLMRequestError('rate limited', 429, 'openai'))).toBe(true);
    expect(shouldFallback(new LLMRequestError('bad request', 400, 'openai'))).toBe(false);
    expect(shouldFallback(new LLMRequestError('too early', 428, 'openai'))).toBe(false);
    expect(shouldFallback(new LLMRequestError('header too large', 431, 'openai'))).toBe(false);
  });

  test('rejects arbitrary errors that look like provider failures', () => {
    const structuralLookalike = Object.assign(new Error('upstream failed'), {
      status: 503,
      provider: 'openai',
    });

    expect(shouldFallback(structuralLookalike)).toBe(false);
    expect(shouldFallback(new Error('upstream failed with status 503'))).toBe(false);
  });
});

// ---------------------------------------------------------------------------
// Registry  <->  cloud-pp-catalog.json parity
//
// llm-models.ts holds one row per (provider, model) with its own rate, so an
// allowlisted id cannot bill at another model's price by construction. What
// can still drift is the registry against the catalog clients display.
describe('registry parity', () => {
  const PP_CATALOG_PATH = `${import.meta.dir}/../../../shared-app-classification/cloud-pp-catalog.json`;

  interface PpCatalogProvider {
    id: string;
    llmProvider: string;
    models: { id: string; isDefault?: boolean }[];
  }

  test('the registry covers exactly the routable providers', () => {
    expect([...LLM_PROVIDERS].sort()).toEqual([...ALL_PROVIDERS].sort());
    expect([...new Set(LLM_MODELS.map((row) => row.provider))].sort()).toEqual([...ALL_PROVIDERS].sort());
  });

  test('every provider has exactly one default row, and no (provider, model) repeats', () => {
    for (const provider of ALL_PROVIDERS) {
      const defaults = LLM_MODELS.filter((row) => row.provider === provider && row.isDefault).map((row) => row.model);
      expect(`${provider}: ${defaults.join(',')}`).toBe(`${provider}: ${defaultModelFor(provider)}`);
      expect(llmModelsFor(provider)[0]?.model).toBe(defaultModelFor(provider));
    }
    const keys = LLM_MODELS.map((row) => `${row.provider}/${row.model}`);
    expect(new Set(keys).size).toBe(keys.length);
  });

  test('every allowlisted model resolves to itself and serves its own row label', () => {
    for (const row of LLM_MODELS) {
      expect(resolveLLMModel(row.provider, requestWith({ 'x-llm-model': row.model }))).toBe(row.model);
      expect(servedLLMName(row.provider, row.model)).toBe(row.servedName);
    }
  });

  test('every anthropic row sets the output cap the Messages API requires', () => {
    for (const row of llmModelsFor('anthropic')) {
      expect(`${row.model}: ${typeof row.maxOutputTokens}`).toBe(`${row.model}: number`);
    }
  });

  test('the registry and cloud-pp-catalog.json ship the same model ids', async () => {
    // Drift in either direction is a bug: an id here but not in the catalog
    // bills against a price no client displays, and an id in the catalog but
    // not here is silently downgraded to the provider default on every request.
    const catalog = (await Bun.file(PP_CATALOG_PATH).json()) as { providers: PpCatalogProvider[] };
    expect(catalog.providers.length).toBeGreaterThan(0);

    const fromCatalog = catalog.providers.flatMap((provider) =>
      provider.models.map((model) => `${provider.llmProvider}/${model.id}`));
    const fromRegistry = LLM_MODELS.map((row) => `${row.provider}/${row.model}`);

    expect(fromRegistry.sort()).toEqual(fromCatalog.sort());
  });

  test('each provider default is the cloud-pp-catalog.json isDefault row', async () => {
    // A client resolves a blank model to the catalog isDefault row and sends it
    // as X-LLM-Model. A request with no X-LLM-Model resolves to the default
    // here. The two must name the same model, or the answer depends on which
    // client sent the request.
    const catalog = (await Bun.file(PP_CATALOG_PATH).json()) as { providers: PpCatalogProvider[] };
    const fromCatalog = catalog.providers.map((provider) =>
      `${provider.llmProvider}/${provider.models.filter((m) => m.isDefault === true).map((m) => m.id).join('+')}`);
    const fromRegistry = ALL_PROVIDERS.map((provider) => `${provider}/${defaultModelFor(provider)}`);

    expect(fromRegistry.sort()).toEqual(fromCatalog.sort());
  });
});
