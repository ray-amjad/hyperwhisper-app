// LLM PROVIDER SELECTION + RETRY

import { retryWithBackoff } from './utils';
import type { CorrectionRequestPayload } from '../providers/groq-llm';
import { requestCerebrasChat } from '../providers/cerebras';
import { requestGroqChat } from '../providers/groq-llm';
import { requestAnthropicChat } from '../providers/anthropic';
import { requestXaiGrokChat } from '../providers/xai-llm';
import { requestOpenAIChat } from '../providers/openai-llm';
import { requestGeminiChat } from '../providers/gemini-llm';
import { requestMistralChat } from '../providers/mistral-llm';

export type LLMProvider = 'cerebras' | 'groq' | 'anthropic' | 'grok' | 'openai' | 'gemini' | 'mistral';

export const DEFAULT_LLM_PROVIDER: LLMProvider = 'cerebras';

export const LLM_PROVIDER_NAMES: Record<LLMProvider, string> = {
  cerebras: 'cerebras-gpt-oss-120b',
  groq: 'groq-gpt-oss-120b',
  anthropic: 'claude-haiku-4-5',
  grok: 'xai-grok-4.3',
  openai: 'openai-gpt-5-mini',
  gemini: 'gemini-2.5-flash',
  mistral: 'mistral-small-latest',
};

// Served name per (provider, resolved-model) pair, for the X-LLM-Provider
// response header / log. The default model maps back to LLM_PROVIDER_NAMES so
// single-model providers (and the default of the multi-model ones) are
// unchanged; the non-default allowlisted models of the multi-model providers
// (openai/gemini/mistral) get their own label so the response reflects the model
// actually used instead of the provider default. MUST stay in sync with
// LLM_PROVIDER_MODELS allowlists.
const LLM_SERVED_NAMES: Partial<Record<LLMProvider, Record<string, string>>> = {
  openai: { 'gpt-5-mini': 'openai-gpt-5-mini', 'gpt-5-nano': 'openai-gpt-5-nano' },
  gemini: { 'gemini-2.5-flash': 'gemini-2.5-flash', 'gemini-2.5-flash-lite': 'gemini-2.5-flash-lite' },
  mistral: { 'mistral-small-latest': 'mistral-small-latest', 'open-mistral-nemo': 'open-mistral-nemo' },
};

/**
 * Served name for the X-LLM-Provider response header / log: the (provider,
 * model) pair the request was actually answered with. For the single-model
 * providers (and any model not in the served-name map) this is the static
 * LLM_PROVIDER_NAMES label; for the multi-model providers' non-default models it
 * echoes the resolved model so callers (and the smoke test) can tell a
 * non-default model apart from the provider default.
 */
export function servedLLMName(provider: LLMProvider, model: string): string {
  return LLM_SERVED_NAMES[provider]?.[model] ?? LLM_PROVIDER_NAMES[provider];
}

const LLM_PROVIDER_FALLBACKS: Record<LLMProvider, LLMProvider> = {
  anthropic: 'cerebras',
  cerebras: 'groq',
  groq: 'cerebras',
  grok: 'anthropic',
  openai: 'anthropic',
  gemini: 'cerebras',
  mistral: 'groq',
};

// Per-provider allowlist of valid X-LLM-Model ids, with the default first. The
// resolved model is threaded through callWithRetry to the openai/gemini/mistral
// clients (the 4 single-model providers ignore it). MUST match the model ids in
// shared-app-classification/cloud-pp-catalog.json.
const LLM_PROVIDER_MODELS: Record<LLMProvider, { default: string; allowed: readonly string[] }> = {
  cerebras: { default: 'gpt-oss-120b', allowed: ['gpt-oss-120b'] },
  groq: { default: 'openai/gpt-oss-120b', allowed: ['openai/gpt-oss-120b'] },
  anthropic: { default: 'claude-haiku-4-5', allowed: ['claude-haiku-4-5'] },
  grok: { default: 'grok-4.3', allowed: ['grok-4.3'] },
  openai: { default: 'gpt-5-mini', allowed: ['gpt-5-mini', 'gpt-5-nano'] },
  gemini: { default: 'gemini-2.5-flash', allowed: ['gemini-2.5-flash', 'gemini-2.5-flash-lite'] },
  mistral: { default: 'mistral-small-latest', allowed: ['mistral-small-latest', 'open-mistral-nemo'] },
};

export function defaultModelFor(provider: LLMProvider): string {
  return LLM_PROVIDER_MODELS[provider].default;
}

export function fallbackProviderFor(provider: LLMProvider): LLMProvider {
  return LLM_PROVIDER_FALLBACKS[provider];
}

/**
 * Extract LLM provider from X-LLM-Provider header.
 * Returns default provider if header is missing or invalid.
 */
export function extractLLMProvider(request: Request): LLMProvider {
  const header = request.headers.get('x-llm-provider')?.toLowerCase().trim();

  switch (header) {
    case 'groq':
    case 'cerebras':
    case 'anthropic':
    case 'grok':
    case 'openai':
    case 'gemini':
    case 'mistral':
      return header;
    default:
      return DEFAULT_LLM_PROVIDER;
  }
}

/**
 * Resolve the model for a provider from the X-LLM-Model header, validating it
 * against the provider's allowlist. Missing or invalid models fall back to the
 * provider default so a bad header never bills the wrong (or no) model.
 */
export function resolveLLMModel(provider: LLMProvider, request: Request): string {
  const requested = request.headers.get('x-llm-model')?.toLowerCase().trim();
  const config = LLM_PROVIDER_MODELS[provider];
  if (requested && config.allowed.includes(requested)) {
    return requested;
  }
  return config.default;
}

/**
 * Retry LLM call with exponential backoff. `model` is the resolved (allowlisted)
 * model id — the multi-model providers (openai/gemini/mistral) route on it; the
 * single-model providers ignore it.
 */
export async function callWithRetry(
  provider: LLMProvider,
  payload: CorrectionRequestPayload,
  requestId: string,
  maxRetries: number,
  model: string
): Promise<Awaited<ReturnType<typeof requestCerebrasChat>>> {
  return retryWithBackoff(
    () => {
      if (provider === 'anthropic') return requestAnthropicChat(payload, requestId);
      if (provider === 'grok') return requestXaiGrokChat(payload, requestId);
      if (provider === 'openai') return requestOpenAIChat(payload, requestId, model);
      if (provider === 'gemini') return requestGeminiChat(payload, requestId, model);
      if (provider === 'mistral') return requestMistralChat(payload, requestId, model);
      return provider === 'cerebras'
        ? requestCerebrasChat(payload, requestId)
        : requestGroqChat(payload, requestId);
    },
    {
      maxRetries,
      initialDelayMs: 1000,
      backoffMultiplier: 2,
      onRetry: (attempt, error, delayMs) => {
        console.warn(`[llm] ${provider} failed - retrying`, {
          attempt,
          error: error.message,
          delayMs,
        });
      },
    }
  );
}

function getErrorStatus(error: unknown): number | undefined {
  if (!error || typeof error !== 'object') {
    return undefined;
  }

  const status = (error as { status?: unknown }).status;
  if (typeof status === 'number') {
    return status;
  }

  const message = (error as { message?: unknown }).message;
  if (typeof message === 'string') {
    const match = message.match(/status\s+(\d{3})/i);
    if (match) {
      return Number(match[1]);
    }
  }

  return undefined;
}

/**
 * Check if an error should trigger provider fallback (5xx).
 */
export function shouldFallback(error: unknown): boolean {
  const status = getErrorStatus(error);
  return typeof status === 'number' && status >= 500 && status <= 599;
}
