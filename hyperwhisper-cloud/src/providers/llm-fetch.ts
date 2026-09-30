// BOUNDED FETCH FOR THE NON-STREAMING LLM CHAT CALLS
//
// Issue #782: the post-processing chat calls used a bare fetch(). An upstream
// that accepted the connection and then sent nothing left the promise pending
// forever, so callWithRetry never saw a rejection and post-process.ts never
// reached its fallback provider.
//
// Why not providers/utils.ts fetchWithTimeout: its timer is cleared once the
// response headers arrive, so a body that stalls after the headers still hangs.
// Here one timer covers the request AND the body read done in `read`.
//
// Error mapping. retryWithBackoff already retries any rejection; what the
// mapping adds is the FALLBACK, because shouldFallback() only accepts an
// LLMRequestError with a 5xx status:
//   - the timer fired          -> LLMRequestError(..., 504, provider)
//   - fetch() itself rejected   -> LLMRequestError(..., 502, provider)
//   - anything `read` throws    -> passed through unchanged, so the non-2xx
//                                  handling in each caller keeps its own status.

import { LLMRequestError } from './llm-errors';

/**
 * Per-attempt bound for one LLM chat request, headers and body together.
 * Above the 15 s STT default in providers/utils.ts (a long correction prompt can
 * legitimately take longer), and small enough that one attempt fits inside
 * every client's /post-process budget (macOS waits 60 s).
 */
export const LLM_REQUEST_TIMEOUT_MS = 20_000;

function describeError(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

export async function fetchLLMWithTimeout<T>(
  provider: string,
  url: string,
  init: RequestInit,
  read: (response: Response) => Promise<T>,
  requestId: string,
  timeoutMs: number = LLM_REQUEST_TIMEOUT_MS,
): Promise<T> {
  const startedAt = performance.now();
  const controller = new AbortController();
  let timedOut = false;
  const timeoutHandle = setTimeout(() => {
    timedOut = true;
    controller.abort();
  }, timeoutMs);

  const timeoutError = () => {
    const elapsedMs = Math.round(performance.now() - startedAt);
    console.error('llm.transport_error', { provider, requestId, kind: 'timeout', timeoutMs, elapsedMs });
    return new LLMRequestError(`${provider} LLM request timeout after ${timeoutMs}ms`, 504, provider);
  };

  try {
    let response: Response;
    try {
      response = await fetch(url, { ...init, signal: controller.signal });
    } catch (error) {
      if (timedOut) throw timeoutError();
      console.error('llm.transport_error', {
        provider,
        requestId,
        kind: 'network_error',
        message: describeError(error),
        elapsedMs: Math.round(performance.now() - startedAt),
      });
      throw new LLMRequestError(`${provider} LLM request network error: ${describeError(error)}`, 502, provider);
    }

    try {
      return await read(response);
    } catch (error) {
      if (timedOut && !(error instanceof LLMRequestError)) throw timeoutError();
      throw error;
    }
  } finally {
    clearTimeout(timeoutHandle);
  }
}
