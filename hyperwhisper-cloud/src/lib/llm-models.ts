// LLM MODEL REGISTRY
// One row per (provider, model) for the /post-process LLMs: the wire id sent as
// the request `model`, the served label (X-LLM-Provider header / log), the
// default flag, the billing rates and the output cap. The allowlist, served
// names, provider defaults and the post-processing cost all read this table, so
// a model change is one row here plus its cloud-pp-catalog.json row.
//
// Rows MUST match shared-app-classification/cloud-pp-catalog.json (ids, the
// isDefault row, base-tier prices): src/providers/llm-dispatch.test.ts and the
// parity block in src/lib/cost-calculator.test.ts check it.

import { roundUsd, type GroqUsage } from './cost-calculator';

export type LLMProvider = 'cerebras' | 'groq' | 'anthropic' | 'grok' | 'openai' | 'gemini' | 'mistral';

export interface LLMChatRate {
  promptPerToken: number;
  completionPerToken: number;
}

export interface LLMModelRow {
  provider: LLMProvider;
  /** The id a client sends as X-LLM-Model and we send upstream as `model`. */
  model: string;
  /** Public label for the X-LLM-Provider response header and the log. */
  servedName: string;
  /** The provider's default: a missing or unknown X-LLM-Model resolves here. Exactly one per provider. */
  isDefault?: true;
  /** USD per token. For Anthropic this is the base (<= longPrompt threshold) tier. */
  rate: LLMChatRate;
  /**
   * Anthropic only: above `thresholdTokens` TOTAL input tokens (uncached + cache
   * write + cache read), EVERY token of the request bills at `rate`, output and
   * both cache buckets included.
   */
  longPrompt?: { thresholdTokens: number; rate: LLMChatRate };
  /** Anthropic only: cache write / read multipliers on the tier's input rate. */
  cache?: { writeMultiplier: number; readMultiplier: number };
  /**
   * Output ceiling sent upstream (`max_tokens` for Anthropic, which requires
   * one; `max_completion_tokens` on the OpenAI-compatible wire). Omitted where
   * the provider's own default fits a cleaned transcript.
   */
  maxOutputTokens?: number;
}

const M = 1_000_000;

export const LLM_MODELS: readonly LLMModelRow[] = [
  { provider: 'cerebras', model: 'gpt-oss-120b', servedName: 'cerebras-gpt-oss-120b', isDefault: true,
    rate: { promptPerToken: 0.35 / M, completionPerToken: 0.75 / M } },

  // Groq GPT-OSS-120B. Groq is the one OpenAI-compatible provider whose default
  // is not a usable ceiling: when the request omits a cap it applies a low
  // default (its reasoning docs cite 1,024), and openai/gpt-oss-120b spends
  // reasoning tokens from that same budget. Long dictations therefore came back
  // finish_reason=length, which the completion policy rejects — the caller falls
  // back to the raw transcript and the user is still billed for the call. 4,096
  // rather than 8,192 because Groq's TPM admission check counts prompt +
  // requested cap, not actual usage.
  { provider: 'groq', model: 'openai/gpt-oss-120b', servedName: 'groq-gpt-oss-120b', isDefault: true,
    rate: { promptPerToken: 0.15 / M, completionPerToken: 0.60 / M }, maxOutputTokens: 4096 },

  // Anthropic Claude Haiku 5.5. Undated id on purpose: Anthropic's undated
  // aliases are the supported form, and a pinned snapshot is what left the
  // client on Haiku 4.5 after Haiku 5.5 shipped. Two tiers: up to 100K total
  // input tokens $0.10 / $0.50 per 1M, above it $0.50 / $2.50 for the whole
  // request. Cache writes 1.25x and reads 0.10x the tier's input rate (5-minute
  // TTL) — ASSUMED unchanged from Haiku 4.5, Anthropic's standard multipliers.
  // The Messages API requires an explicit output ceiling.
  { provider: 'anthropic', model: 'claude-haiku-5-5', servedName: 'claude-haiku-5-5', isDefault: true,
    rate: { promptPerToken: 0.10 / M, completionPerToken: 0.50 / M },
    longPrompt: { thresholdTokens: 100_000, rate: { promptPerToken: 0.50 / M, completionPerToken: 2.50 / M } },
    cache: { writeMultiplier: 1.25, readMultiplier: 0.10 },
    maxOutputTokens: 8192 },

  { provider: 'grok', model: 'grok-4.3', servedName: 'xai-grok-4.3', isDefault: true,
    rate: { promptPerToken: 1.25 / M, completionPerToken: 2.50 / M } },

  // OpenAI GPT-5.6 Luna (chat/completions). $0.20/$1.20 per 1M, read 2026-09-25
  // at https://developers.openai.com/api/docs/models/gpt-5.6-luna. gpt-5-mini and
  // gpt-5-nano lose their only snapshots 2026-12-11 and are no longer rows, so an
  // old client still sending either id resolves to luna and bills at its rate
  // (the open-mistral-nemo precedent).
  { provider: 'openai', model: 'gpt-5.6-luna', servedName: 'openai-gpt-5.6-luna', isDefault: true,
    rate: { promptPerToken: 0.20 / M, completionPerToken: 1.20 / M } },

  // Google Gemini (OpenAI-compatible endpoint). https://ai.google.dev/gemini-api/docs/pricing
  // Google limits gemini-2.5-* to past users, so a request with no (or an
  // unknown) X-LLM-Model resolves to 3.8 Flash, the catalog isDefault row; the
  // 2.5 ids stay for clients that ask for them. 3.8 Flash is INTRODUCTORY
  // pricing — $0.75/$3.75 per 1M runs to 2026-12-31. Re-confirm on the first
  // model watch of 2027 and update this rate AND the pricePerMInput /
  // pricePerMOutput + priceNote on the gemini-3.8-flash row in
  // cloud-pp-catalog.json together (and the GEMINI_RATES transcription row in
  // cost-calculator.ts for the same id).
  { provider: 'gemini', model: 'gemini-3.8-flash', servedName: 'gemini-3.8-flash', isDefault: true,
    rate: { promptPerToken: 0.75 / M, completionPerToken: 3.75 / M } },
  { provider: 'gemini', model: 'gemini-2.5-flash', servedName: 'gemini-2.5-flash',
    rate: { promptPerToken: 0.30 / M, completionPerToken: 2.50 / M } },
  { provider: 'gemini', model: 'gemini-2.5-flash-lite', servedName: 'gemini-2.5-flash-lite',
    rate: { promptPerToken: 0.10 / M, completionPerToken: 0.40 / M } },

  // Mistral (chat/completions). https://mistral.ai/pricing + docs.mistral.ai
  // `mistral-small-latest` resolves to Mistral Small 4: the model-specific docs
  // list $0.15/$0.60, which conflicts with the lower pricing-tile figure — we
  // bill at the higher (model-docs) rate, the billing-safe choice. The retired
  // Nemo model is no longer a row; old clients still sending its id resolve to
  // `mistral-small-latest` and are billed at that rate.
  { provider: 'mistral', model: 'mistral-small-latest', servedName: 'mistral-small-latest', isDefault: true,
    rate: { promptPerToken: 0.15 / M, completionPerToken: 0.60 / M } },
];

export const LLM_PROVIDERS: readonly LLMProvider[] = [
  'cerebras', 'groq', 'anthropic', 'grok', 'openai', 'gemini', 'mistral',
];

function defaultRowFor(provider: LLMProvider): LLMModelRow {
  const row = LLM_MODELS.find((r) => r.provider === provider && r.isDefault);
  if (!row) throw new Error(`llm-models: no default row for ${provider}`);
  return row;
}

export function llmModelsFor(provider: LLMProvider): LLMModelRow[] {
  const rows = LLM_MODELS.filter((r) => r.provider === provider);
  return [...rows.filter((r) => r.isDefault), ...rows.filter((r) => !r.isDefault)];
}

/**
 * The row for (provider, model). An id with no row of its own resolves to the
 * provider default, so header/catalog drift never bills $0 or an unknown rate
 * (fail-closed) and never sends an id we have not allowlisted.
 */
export function llmModelRow(provider: LLMProvider, model: string): LLMModelRow {
  return LLM_MODELS.find((r) => r.provider === provider && r.model === model) ?? defaultRowFor(provider);
}

export function defaultModelFor(provider: LLMProvider): string {
  return defaultRowFor(provider).model;
}

export function isAllowedLLMModel(provider: LLMProvider, model: string): boolean {
  return LLM_MODELS.some((r) => r.provider === provider && r.model === model);
}

export function servedLLMName(provider: LLMProvider, model: string): string {
  return llmModelRow(provider, model).servedName;
}

export function computeLLMChatCost(provider: LLMProvider, model: string, usage: GroqUsage): number {
  const { rate } = llmModelRow(provider, model);
  const promptCost = usage.prompt_tokens * rate.promptPerToken;
  const completionCost = usage.completion_tokens * rate.completionPerToken;
  return roundUsd(promptCost + completionCost);
}

/**
 * USD for one Anthropic Messages call, at the model's row (tiers + cache).
 * `inputTokens` is the uncached input delta; cache buckets are billed
 * separately. The tier is decided by the whole prompt — all three input
 * buckets together.
 */
export function computeAnthropicCost(
  model: string,
  inputTokens: number,
  outputTokens: number,
  cacheCreationTokens: number = 0,
  cacheReadTokens: number = 0,
): number {
  const row = llmModelRow('anthropic', model);
  const totalInputTokens = inputTokens + cacheCreationTokens + cacheReadTokens;
  const rate = row.longPrompt && totalInputTokens > row.longPrompt.thresholdTokens
    ? row.longPrompt.rate
    : row.rate;
  const cache = row.cache ?? { writeMultiplier: 1, readMultiplier: 1 };
  const promptCost = inputTokens * rate.promptPerToken;
  const completionCost = outputTokens * rate.completionPerToken;
  const cacheWriteCost = cacheCreationTokens * rate.promptPerToken * cache.writeMultiplier;
  const cacheReadCost = cacheReadTokens * rate.promptPerToken * cache.readMultiplier;
  return roundUsd(promptCost + completionCost + cacheWriteCost + cacheReadCost);
}
