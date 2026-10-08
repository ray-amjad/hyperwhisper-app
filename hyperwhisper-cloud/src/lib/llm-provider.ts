// LLM PROVIDER SELECTION + RETRY

import { retryWithBackoff } from './utils';
import { buildCorrectionRequest, type CorrectionRequestPayload } from '../providers/llm-contract';
import { LLMRequestError, LLMTimeoutError } from '../providers/llm-errors';
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
  anthropic: 'claude-haiku-5-5',
  grok: 'xai-grok-4.3',
  openai: 'openai-gpt-5.6-luna',
  gemini: 'gemini-3.8-flash',
  mistral: 'mistral-small-latest',
};

// Served name per (provider, resolved-model) pair, for the X-LLM-Provider
// response header / log. The default model maps back to LLM_PROVIDER_NAMES so
// single-model providers (and the default of the multi-model one) are
// unchanged; the non-default allowlisted models of the multi-model provider
// (gemini) get their own label so the response reflects the model actually used
// instead of the provider default. openai has one model since #1018 and needs no
// entry: servedLLMName falls through to LLM_PROVIDER_NAMES.openai. MUST stay in
// sync with LLM_PROVIDER_MODELS allowlists.
const LLM_SERVED_NAMES: Partial<Record<LLMProvider, Record<string, string>>> = {
  gemini: {
    'gemini-2.5-flash': 'gemini-2.5-flash',
    'gemini-2.5-flash-lite': 'gemini-2.5-flash-lite',
    'gemini-3.8-flash': 'gemini-3.8-flash',
  },
  mistral: { 'mistral-small-latest': 'mistral-small-latest' },
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

// Per-provider retry count. Fast/cheap providers retry more; pricier or slower
// ones retry less to bound latency and spend before falling back. A timeout
// from our own per-attempt timer is never retried (see isRetryableLLMError).
const LLM_PROVIDER_RETRIES: Record<LLMProvider, number> = {
  anthropic: 2,
  cerebras: 0,
  grok: 1,
  openai: 1,
  gemini: 2,
  mistral: 2,
  groq: 3,
};

// Per-provider allowlist of valid X-LLM-Model ids, with the default first. The
// resolved model is threaded through callWithRetry to the anthropic/openai/
// gemini/mistral clients, which put it in the request body (the other 3
// providers ignore it).
// Only gemini allows more than one model today. MUST match the model ids in
// shared-app-classification/cloud-pp-catalog.json.
const LLM_PROVIDER_MODELS: Record<LLMProvider, { default: string; allowed: readonly string[] }> = {
  cerebras: { default: 'gpt-oss-120b', allowed: ['gpt-oss-120b'] },
  groq: { default: 'openai/gpt-oss-120b', allowed: ['openai/gpt-oss-120b'] },
  anthropic: { default: 'claude-haiku-5-5', allowed: ['claude-haiku-5-5'] },
  grok: { default: 'grok-4.3', allowed: ['grok-4.3'] },
  // gpt-5-mini / gpt-5-nano lose their only snapshots 2026-12-11. They are no
  // longer allowlisted, so an old client still sending either id resolves to the
  // default and is billed at the luna rate (the open-mistral-nemo precedent).
  openai: { default: 'gpt-5.6-luna', allowed: ['gpt-5.6-luna'] },
  // Google limits gemini-2.5-* to past users, so a request with no (or an
  // unknown) X-LLM-Model resolves to 3.8 Flash, the cloud-pp-catalog.json
  // isDefault row. The 2.5 ids stay allowed for clients that ask for them.
  gemini: {
    default: 'gemini-3.8-flash',
    allowed: ['gemini-3.8-flash', 'gemini-2.5-flash', 'gemini-2.5-flash-lite'],
  },
  mistral: { default: 'mistral-small-latest', allowed: ['mistral-small-latest'] },
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
 * Whether callWithRetry retries `error` on the same provider. Everything is,
 * except a timeout from our own timer (LLMTimeoutError, #782 review round 2):
 * the provider has just been silent for the whole per-attempt bound, so a
 * retry would only make the caller wait that long again before the fallback
 * provider gets its turn. An upstream-returned 504, any other non-2xx and a
 * network error are still retried.
 */
export function isRetryableLLMError(error: Error): boolean {
  return !(error instanceof LLMTimeoutError);
}

/**
 * Retry LLM call with exponential backoff. `model` is the resolved (allowlisted)
 * model id — the anthropic/openai/gemini/mistral clients send it as the request
 * model; the other providers ignore it.
 */
export async function callWithRetry(
  provider: LLMProvider,
  payload: CorrectionRequestPayload,
  requestId: string,
  model: string
): Promise<Awaited<ReturnType<typeof requestCerebrasChat>>> {
  return retryWithBackoff(
    () => {
      if (provider === 'anthropic') return requestAnthropicChat(payload, requestId, undefined, model);
      if (provider === 'grok') return requestXaiGrokChat(payload, requestId);
      if (provider === 'openai') return requestOpenAIChat(payload, requestId, model);
      if (provider === 'gemini') return requestGeminiChat(payload, requestId, model);
      if (provider === 'mistral') return requestMistralChat(payload, requestId, model);
      return provider === 'cerebras'
        ? requestCerebrasChat(payload, requestId)
        : requestGroqChat(payload, requestId);
    },
    {
      maxRetries: LLM_PROVIDER_RETRIES[provider],
      initialDelayMs: 1000,
      backoffMultiplier: 2,
      shouldRetry: isRetryableLLMError,
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

/**
 * Check if an error should trigger provider fallback: an LLMRequestError with a
 * 5xx or a 429 status (#1565). A 429 is the provider rate-limiting us, and the
 * fallback vendor has its own, separate rate limit, so a different vendor does
 * fix it. Every other 4xx is our own bad request and stays excluded: a
 * different vendor would reject it too.
 */
export function shouldFallback(error: unknown): boolean {
  if (!(error instanceof LLMRequestError)) return false;
  return error.status === 429 || (error.status >= 500 && error.status <= 599);
}

export { buildCorrectionRequest };

/** Exported for the parity test only. */
export const __tables = {
  LLM_PROVIDER_MODELS,
  LLM_PROVIDER_RETRIES,
};
