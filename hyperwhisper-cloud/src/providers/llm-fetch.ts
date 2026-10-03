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
// Error mapping. shouldFallback() only accepts an LLMRequestError with a 5xx
// status, so the mapping is what lets a silent upstream reach the FALLBACK:
//   - our timer fired           -> LLMTimeoutError (an LLMRequestError, 504,
//                                  provider). callWithRetry does NOT retry it on
//                                  the same provider (review round 2): it goes
//                                  straight to post-process.ts's fallback.
//   - any other failure          -> rethrown unchanged, exactly as before #782:
//                                  a network error stays an untagged error
//                                  (retried, not failed over), and the non-2xx
//                                  handling in each caller keeps its own status
//                                  (retried, and a 5xx then fails over).

import { LLMRequestError, LLMTimeoutError } from './llm-errors';

/**
 * Floor of the per-attempt bound for one non-streaming LLM chat request
 * (headers and body together), and the flat first-byte bound for the
 * /assistant stream. Above the 15 s STT default in providers/utils.ts.
 */
export const LLM_REQUEST_TIMEOUT_MS = 20_000;

// Non-streaming bound, scaled with the TRANSCRIPT (same shape as
// computeUploadTimeoutMs in providers/utils.ts: a floor plus a per-size
// allowance). A non-streaming response only arrives after the upstream has
// generated ALL of its output, and a correction's output is about as long as
// the transcript it corrects, so a flat cap would abort a long, healthy
// correction. The system prompt is left out (review round 2): it is the
// caller's instructions, it does not lengthen the output, and counting it gave
// a short dictation under a long custom prompt a long bound for nothing.
//
// Rate: 10 s per 1,000 transcript characters. It assumes a deliberately slow
// upstream, 50 output tokens/s (about half of what Claude Haiku 4.5 usually
// streams; Groq and Cerebras are several times faster), and 2 characters per
// token (English runs about 4; non-Latin scripts run fewer characters per
// token). 1,000 chars ~ 500 tokens ~ 10 s.
//
// Ceiling: 180 s, the Windows client's /post-process wait
// (HyperWhisperCloudService DefaultTimeoutSeconds, the longest client budget in
// the repo; macOS waits 60 s). An attempt that could only finish after the
// client has hung up buys nothing, and the ceiling is what still ends a silent
// upstream on a very long prompt.
export const LLM_REQUEST_TIMEOUT_PER_1K_CHARS_MS = 10_000;
export const LLM_REQUEST_TIMEOUT_CEILING_MS = 180_000;

/** Per-attempt bound for a non-streaming chat request whose transcript is `transcriptChars` long. */
export function computeLLMRequestTimeoutMs(transcriptChars: number): number {
  const scaled = Math.ceil(transcriptChars / 1_000) * LLM_REQUEST_TIMEOUT_PER_1K_CHARS_MS;
  return Math.min(LLM_REQUEST_TIMEOUT_CEILING_MS, Math.max(LLM_REQUEST_TIMEOUT_MS, scaled));
}

/** Total characters across every message of a chat payload (system prompt included). */
export function promptCharCount(messages: ReadonlyArray<{ content: string }>): number {
  return messages.reduce((sum, message) => sum + message.content.length, 0);
}

/**
 * Characters of the transcript: every message except the system prompt. For a
 * /post-process payload that is the one user message, the transcript inside
 * its --TRANSCRIPT-- markers. This, not the whole prompt, sizes the timeout.
 */
export function transcriptCharCount(messages: ReadonlyArray<{ role: string; content: string }>): number {
  return messages.reduce((sum, message) => (message.role === 'system' ? sum : sum + message.content.length), 0);
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
    return new LLMTimeoutError(`${provider} LLM request timeout after ${timeoutMs}ms`, provider);
  };

  try {
    const response = await fetch(url, { ...init, signal: controller.signal });
    return await read(response);
  } catch (error) {
    // Only our own timer becomes an LLMTimeoutError (504). The non-2xx LLMRequestError thrown by
    // `read` keeps its upstream status even if the timer fired meanwhile.
    if (timedOut && !(error instanceof LLMRequestError)) throw timeoutError();
    throw error;
  } finally {
    clearTimeout(timeoutHandle);
  }
}
