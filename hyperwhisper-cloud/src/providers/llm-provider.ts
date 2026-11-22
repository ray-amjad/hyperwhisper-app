import { retryWithBackoff } from '../lib/utils';
import { buildCorrectionRequest, type CorrectionRequestPayload } from './llm-contract';
import { LLMMissingKeyError, LLMRequestError, LLMTimeoutError } from './llm-errors';
import { requestAnthropicChat } from './anthropic';
import { requestOpenAICompatibleChat, type OpenAICompatChatResult, type OpenAICompatProvider } from './openai-compat-chat';
import { defaultModelFor, isAllowedLLMModel, LLM_PROVIDERS, type LLMProvider } from '../lib/llm-models';

// The model registry (allowlist, defaults, served labels, rates) is llm-models.ts.
export { defaultModelFor, servedLLMName, type LLMProvider } from '../lib/llm-models';

const DEFAULT_LLM_PROVIDER: LLMProvider = 'cerebras';

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

type LLMChatRequest = (
  payload: CorrectionRequestPayload,
  requestId: string,
  model: string,
) => Promise<OpenAICompatChatResult>;

const openAICompatChat = (provider: OpenAICompatProvider): LLMChatRequest =>
  (payload, requestId, model) => requestOpenAICompatibleChat(provider, payload, requestId, model);

// Each provider's request function, one row per provider: a new LLMProvider
// without a row here is a compile error, not a silent OpenAI-compatible call.
const LLM_CHAT_REQUESTS: Record<LLMProvider, LLMChatRequest> = {
  anthropic: requestAnthropicChat,
  cerebras: openAICompatChat('cerebras'),
  groq: openAICompatChat('groq'),
  grok: openAICompatChat('grok'),
  openai: openAICompatChat('openai'),
  gemini: openAICompatChat('gemini'),
  mistral: openAICompatChat('mistral'),
};

export function fallbackProviderFor(provider: LLMProvider): LLMProvider {
  return LLM_PROVIDER_FALLBACKS[provider];
}

/**
 * Extract LLM provider from X-LLM-Provider header.
 * Returns default provider if header is missing or invalid.
 */
export function extractLLMProvider(request: Request): LLMProvider {
  const header = request.headers.get('x-llm-provider')?.toLowerCase().trim();

  return LLM_PROVIDERS.find((provider) => provider === header) ?? DEFAULT_LLM_PROVIDER;
}

/**
 * Resolve the model for a provider from the X-LLM-Model header, validating it
 * against the provider's allowlist. Missing or invalid models fall back to the
 * provider default so a bad header never bills the wrong (or no) model.
 */
export function resolveLLMModel(provider: LLMProvider, request: Request): string {
  const requested = request.headers.get('x-llm-model')?.toLowerCase().trim();
  if (requested && isAllowedLLMModel(provider, requested)) {
    return requested;
  }
  return defaultModelFor(provider);
}

/**
 * Whether callWithRetry retries `error` on the same provider. Everything is,
 * except a timeout from our own timer (LLMTimeoutError, #782 review round 2):
 * the provider has just been silent for the whole per-attempt bound, so a
 * retry would only make the caller wait that long again before the fallback
 * provider gets its turn. An upstream-returned 504, any other non-2xx and a
 * network error are still retried. A missing API key (LLMMissingKeyError) is
 * not retried either: the env does not change between attempts.
 *
 * `retryRateLimit: false` also refuses a 429 (#1568). The caller sets it when a
 * fallback provider is next: the vendor has just said no, and the fallback has
 * its own rate limit. The fallback call itself keeps the default, because it
 * has no further provider to try (Ray, #1568 decision 2).
 */
function isRetryableLLMError(error: Error, retryRateLimit = true): boolean {
  if (error instanceof LLMTimeoutError || error instanceof LLMMissingKeyError) return false;
  if (!retryRateLimit && error instanceof LLMRequestError && error.status === 429) return false;
  return true;
}

/**
 * Retry LLM call with exponential backoff. `model` is the resolved (allowlisted)
 * model id the client sends as the request model. See isRetryableLLMError for
 * `retryRateLimit`.
 */
export async function callWithRetry(
  provider: LLMProvider,
  payload: CorrectionRequestPayload,
  requestId: string,
  model: string,
  { retryRateLimit = true }: { retryRateLimit?: boolean } = {}
): Promise<OpenAICompatChatResult> {
  return retryWithBackoff(
    () => LLM_CHAT_REQUESTS[provider](payload, requestId, model),
    {
      maxRetries: LLM_PROVIDER_RETRIES[provider],
      initialDelayMs: 1000,
      backoffMultiplier: 2,
      shouldRetry: (error) => isRetryableLLMError(error, retryRateLimit),
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

