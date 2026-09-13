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
