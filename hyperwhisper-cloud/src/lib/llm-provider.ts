import { retryWithBackoff } from './utils';
import { buildCorrectionRequest, type CorrectionRequestPayload } from '../providers/llm-contract';
import { LLMMissingKeyError, LLMRequestError, LLMTimeoutError } from '../providers/llm-errors';
import { requestAnthropicChat } from '../providers/anthropic';
import { requestOpenAICompatibleChat, type OpenAICompatChatResult } from '../providers/openai-compat-chat';
import { defaultModelFor, isAllowedLLMModel, LLM_PROVIDERS, type LLMProvider } from './llm-models';

// The model registry (allowlist, defaults, served labels, rates) is llm-models.ts.
export { defaultModelFor, servedLLMName, type LLMProvider } from './llm-models';

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
 * The one answer to "is this the provider's transient fault?", which drives
 * both the same-provider retry (callWithRetry) and the move to the next
 * provider (completeWithFailover): an LLMRequestError with a 5xx or a 429
 * status (#1565). That covers an upstream 5xx, our own timeout
 * (LLMTimeoutError, 504), a network failure (LLMNetworkError, 502), a non-JSON
 * 200 (502) and a missing key (LLMMissingKeyError, 503). A 429 is the provider
 * rate-limiting us, and the fallback vendor has its own, separate rate limit,
 * so a different vendor does fix it. Every other 4xx is our own bad request
 * and stays excluded: a different vendor would reject it too, and so would the
 * same one on a retry.
 */
export function isTransientLLMError(error: unknown): boolean {
  if (!(error instanceof LLMRequestError)) return false;
  return error.status === 429 || (error.status >= 500 && error.status <= 599);
}

/**
 * Whether callWithRetry retries `error` on the same provider: a transient
 * error (isTransientLLMError), except a timeout from our own timer
 * (LLMTimeoutError, #782 review round 2): the provider has just been silent
 * for the whole per-attempt bound, so a retry would only make the caller wait
 * that long again before the fallback provider gets its turn. An
 * upstream-returned 504 is still retried. A missing API key
 * (LLMMissingKeyError) is not retried either: the env does not change between
 * attempts.
 *
 * `retryRateLimit: false` also refuses a 429 (#1568). The caller sets it when a
 * fallback provider is next: the vendor has just said no, and the fallback has
 * its own rate limit. The fallback call itself keeps the default, because it
 * has no further provider to try (Ray, #1568 decision 2).
 */
function isRetryableLLMError(error: Error, retryRateLimit = true): boolean {
  if (!isTransientLLMError(error)) return false;
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
    () => provider === 'anthropic'
      ? requestAnthropicChat(payload, requestId, model)
      : requestOpenAICompatibleChat(provider, payload, requestId, model),
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

/** One (provider, model) a completion can be served by. */
export interface LLMChainStep {
  provider: LLMProvider;
  model: string;
}

/** The step after `provider`: its fallback provider, on that provider's default model. */
export function fallbackStepFor(provider: LLMProvider): LLMChainStep {
  const next = fallbackProviderFor(provider);
  return { provider: next, model: defaultModelFor(next) };
}

/**
 * The failover chain for a request to (`provider`, `model`): the requested
 * step, then its fallback provider on that provider's default model.
 */
export function llmChainFor(provider: LLMProvider, model: string): LLMChainStep[] {
  return [{ provider, model }, fallbackStepFor(provider)];
}

export type LLMFailoverOutcome =
  | { ok: true; result: OpenAICompatChatResult; step: LLMChainStep; failedOver: boolean }
  | { ok: false; error: unknown; step: LLMChainStep; failedOver: boolean };

/**
 * Serve `payload` from the first step of `chain` that answers. Each step gets
 * callWithRetry's same-provider retries; a transient error (isTransientLLMError)
 * moves to the next step, and any other error ends the walk. Every step but the
 * last skips its 429 retries (#1568): a 429 goes straight to the next vendor.
 *
 * Never throws for an LLM failure: `ok: false` carries the last step tried and
 * its error. `failedOver` says whether a step after the first was reached.
 * `onStepFailed` sees each failed step, with the step it moves on to (or none).
 */
export async function completeWithFailover(
  chain: readonly LLMChainStep[],
  payload: CorrectionRequestPayload,
  requestId: string,
  { onStepFailed }: {
    onStepFailed?: (failed: LLMChainStep, error: unknown, index: number, next: LLMChainStep | undefined) => void;
  } = {}
): Promise<LLMFailoverOutcome> {
  if (chain.length === 0) throw new Error('completeWithFailover: empty chain');
  for (let index = 0; ; index += 1) {
    const step = chain[index];
    const isLast = index === chain.length - 1;
    try {
      const result = await callWithRetry(step.provider, payload, requestId, step.model, { retryRateLimit: isLast });
      return { ok: true, result, step, failedOver: index > 0 };
    } catch (error) {
      const next = !isLast && isTransientLLMError(error) ? chain[index + 1] : undefined;
      onStepFailed?.(step, error, index, next);
      if (!next) return { ok: false, error, step, failedOver: index > 0 };
    }
  }
}

export { buildCorrectionRequest };

