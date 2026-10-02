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
 * shouldFallback() fails over exactly as for any other 5xx; the subclass only
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
