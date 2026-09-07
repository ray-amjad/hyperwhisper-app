import { describe, expect, test } from 'bun:test';

import {
  DEFAULT_LLM_PROVIDER,
  defaultModelFor,
  extractLLMProvider,
  fallbackProviderFor,
  resolveLLMModel,
  servedLLMName,
  LLM_PROVIDER_NAMES,
  __tables,
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
    expect(resolveLLMModel('gemini', requestWith({ 'x-llm-model': 'gemini-3.8-flash' }))).toBe('gemini-3.8-flash');
    expect(resolveLLMModel('mistral', requestWith({ 'x-llm-model': 'mistral-small-latest' }))).toBe('mistral-small-latest');
  });

  test('the retired open-mistral-nemo id falls back to the mistral default', () => {
    // Old clients still send it; it must never error, just resolve to the default.
    expect(resolveLLMModel('mistral', requestWith({ 'x-llm-model': 'open-mistral-nemo' }))).toBe('mistral-small-latest');
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

  test('adding gemini-3.8-flash did not move the gemini default', () => {
    // A typo'd or BYOK-only gemini-3.x id must still fall back to 2.5-flash,
    // not to the newest allowlisted model.
    expect(defaultModelFor('gemini')).toBe('gemini-2.5-flash');
    expect(resolveLLMModel('gemini', requestWith({ 'x-llm-model': 'gemini-3.7-flash' }))).toBe('gemini-2.5-flash');
    expect(resolveLLMModel('gemini', requestWith({ 'x-llm-model': 'gemini-3.8-flassh' }))).toBe('gemini-2.5-flash');
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
    // gemini-3.8-flash has no hand-written price line: its rate is pinned
    // generically, against the catalog, by the parity block in
    // src/lib/cost-calculator.test.ts, and its introductory price expires
    // 2026-12-31 (see the priceNote on its cloud-pp-catalog.json row). Restating
    // the number here would make that expiry a four-file edit.
  });

  test('computeMistralChatCost per model', () => {
    expect(computeMistralChatCost('mistral-small-latest', oneM)).toBeCloseTo(0.15 + 0.60, 6);
    // The retired open-mistral-nemo bills at the default (mistral-small-latest) rate.
    expect(computeMistralChatCost('open-mistral-nemo', oneM)).toBeCloseTo(0.15 + 0.60, 6);
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
    expect(servedLLMName('gemini', 'gemini-3.8-flash')).toBe('gemini-3.8-flash');
    expect(servedLLMName('gemini', 'gemini-3.8-flash')).not.toBe(LLM_PROVIDER_NAMES.gemini);
  });

  test('the retired open-mistral-nemo has no served name of its own', () => {
    expect(servedLLMName('mistral', 'open-mistral-nemo')).toBe(LLM_PROVIDER_NAMES.mistral);
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

// ---------------------------------------------------------------------------
// Allowlist  <->  billing parity
//
// `computeChatCost` in cost-calculator.ts is `rates[model] ?? rates[default]`:
// no throw, no log. So an id added to a provider's `allowed` list WITHOUT its
// own rate row bills silently at the provider default's rate, on every request,
// forever. For gemini-3.8-flash that would have been a 2.5x under-charge on
// input and 1.5x on output.
//
// These tests are generic over the allowlist, so they cover any id added after
// gemini-3.8-flash with no new assertion. `LLM_PROVIDER_MODELS` is reachable
// through llm-provider's `__tables` test-only export, the same convention
// language-codes.ts uses for its mirrored tables.
describe('allowlist parity', () => {
  const PP_CATALOG_PATH = `${import.meta.dir}/../../../shared-app-classification/cloud-pp-catalog.json`;

  interface PpCatalogProvider {
    id: string;
    llmProvider: string;
    models: { id: string }[];
  }

  // The providers that bill per-model. The other four have a single flat rate
  // and ignore the model id entirely — which is only safe while they allow
  // exactly one model, asserted below.
  const PER_MODEL_CHAT_COST: Partial<Record<LLMProvider, (model: string, usage: GroqUsage) => number>> = {
    openai: computeOpenAIChatCost,
    gemini: computeGeminiChatCost,
    mistral: computeMistralChatCost,
  };

  const ONE_M_INPUT: GroqUsage = { prompt_tokens: 1_000_000, completion_tokens: 0, total_tokens: 1_000_000 };
  const ONE_M_OUTPUT: GroqUsage = { prompt_tokens: 0, completion_tokens: 1_000_000, total_tokens: 1_000_000 };

  // An id no rate table can ever contain, so it always takes computeChatCost's
  // `?? rates[defaultModel]` branch. Billing that id is therefore a live read of
  // "what the provider default's rate would charge" — the probe an allowlisted
  // id must NOT match.
  const NO_SUCH_MODEL = '__no-such-model-anywhere__';

  function pricedAt(costFor: (model: string, usage: GroqUsage) => number, model: string): string {
    return `in=${costFor(model, ONE_M_INPUT)} out=${costFor(model, ONE_M_OUTPUT)}`;
  }

  test('a flat-rate provider allows exactly one model', () => {
    // cerebras/groq/anthropic/grok bill one rate regardless of the model id, so
    // a second allowlisted id there would bill at the first one's price.
    for (const provider of ALL_PROVIDERS) {
      if (PER_MODEL_CHAT_COST[provider]) continue;
      const { default: defaultModel, allowed } = __tables.LLM_PROVIDER_MODELS[provider];
      expect(`${provider}: ${allowed.join(',')}`).toBe(`${provider}: ${defaultModel}`);
    }
  });

  test('every allowlisted model has its OWN billing rate row', () => {
    const billedAtTheProviderDefaultRate: string[] = [];

    for (const provider of ALL_PROVIDERS) {
      const costFor = PER_MODEL_CHAT_COST[provider];
      if (!costFor) continue;
      const { default: defaultModel, allowed } = __tables.LLM_PROVIDER_MODELS[provider];
      const defaultRate = pricedAt(costFor, NO_SUCH_MODEL);

      for (const model of allowed) {
        if (model === defaultModel) continue;
        if (pricedAt(costFor, model) === defaultRate) {
          billedAtTheProviderDefaultRate.push(`${provider}/${model}`);
        }
      }
    }

    // Empty, or one of these ids has no rate row of its own in cost-calculator
    // and is being billed at its provider default's price.
    expect(billedAtTheProviderDefaultRate).toEqual([]);
  });

  test('the allowlists and cloud-pp-catalog.json ship the same model ids', async () => {
    // The comment on LLM_PROVIDER_MODELS says it MUST match the catalog. Drift
    // in either direction is a bug: an id here but not in the catalog bills
    // against a price no client displays, and an id in the catalog but not here
    // is silently downgraded to the provider default on every request.
    const catalog = (await Bun.file(PP_CATALOG_PATH).json()) as { providers: PpCatalogProvider[] };
    expect(catalog.providers.length).toBeGreaterThan(0);

    const fromCatalog: string[] = [];
    const fromAllowlist: string[] = [];

    for (const provider of catalog.providers) {
      for (const model of provider.models) {
        fromCatalog.push(`${provider.llmProvider}/${model.id}`);
      }
    }
    for (const provider of ALL_PROVIDERS) {
      for (const model of __tables.LLM_PROVIDER_MODELS[provider].allowed) {
        fromAllowlist.push(`${provider}/${model}`);
      }
    }

    expect(fromAllowlist.sort()).toEqual(fromCatalog.sort());
  });
});
