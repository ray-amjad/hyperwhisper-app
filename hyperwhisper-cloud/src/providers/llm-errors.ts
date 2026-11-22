/**
 * Provider-neutral failure from an LLM request.
 *
 * Callers use this normalized boundary instead of inspecting vendor response
 * types or parsing error messages. `provider` is optional because some existing
 * producers expose only an upstream status.
 */
export class LLMRequestError extends Error {
  readonly status: number;
  declare readonly provider?: string;

  constructor(message: string, status: number, provider?: string) {
    super(message);
    this.name = 'LLMRequestError';
    this.status = status;
    if (provider !== undefined) {
      this.provider = provider;
    }
  }
}

/**
 * Our own per-attempt timer ended an LLM request (#782). A 504, so
 * isTransientLLMError() fails over exactly as for any other 5xx; the subclass only
 * tells callWithRetry not to retry the same provider, which has just stayed
 * silent for the whole bound. An upstream that RETURNS a 504 is a plain
 * LLMRequestError and keeps the usual retries.
 */
export class LLMTimeoutError extends LLMRequestError {
  constructor(message: string, provider: string) {
    super(message, 504, provider);
    this.name = 'LLMTimeoutError';
  }
}

/**
 * The request never got an HTTP answer: a refused or reset connection, a DNS
 * failure. A 502, so isTransientLLMError() treats it like any other upstream
 * 5xx: retried on the same provider, then failed over. It was an untagged
 * error before, which callWithRetry retried but the fallback refused, two
 * answers to one question.
 */
export class LLMNetworkError extends LLMRequestError {
  constructor(message: string, provider: string, options?: { cause?: unknown }) {
    super(message, 502, provider);
    this.name = 'LLMNetworkError';
    if (options && 'cause' in options) this.cause = options.cause;
  }
}

/**
 * No API key is configured for an LLM provider. A 503, so isTransientLLMError()
 * moves the request to the fallback provider as if this one were down; the
 * subclass tells callWithRetry not to retry, since the env will not change
 * between attempts.
 */
export class LLMMissingKeyError extends LLMRequestError {
  constructor(envName: string, provider: string) {
    super(`${envName} not configured`, 503, provider);
    this.name = 'LLMMissingKeyError';
  }
}

/** The first set env var of `envNames`, or a LLMMissingKeyError naming the first. */
export function requireLLMApiKey(provider: string, ...envNames: [string, ...string[]]): string {
  for (const name of envNames) {
    const value = process.env[name];
    if (value) return value;
  }
  throw new LLMMissingKeyError(envNames[0], provider);
}
