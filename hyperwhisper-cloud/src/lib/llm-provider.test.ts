// callWithRetry's retry policy (#782 review round 2). A timeout from OUR
// per-attempt timer is not retried on the same provider: the provider has just
// been silent for the whole bound, and post-process.ts can only reach its
// fallback once callWithRetry gives up. Every other failure keeps the old
// retries.
//
// The seam is globalThis.fetch and real sockets on 127.0.0.1 (never
// mock.module, which leaks across test files in bun). setTimeout is spied to
// shrink every delay — the 20 s request bound and the 1 s/2 s/4 s backoff — so
// the real abort fires in milliseconds; the spy also records what was armed.

import { afterEach, beforeEach, describe, expect, spyOn, test } from 'bun:test';

import { callWithRetry, isRetryableLLMError, shouldFallback, type LLMProvider } from './llm-provider';
import { buildCorrectionRequest } from '../providers/llm-contract';
import { LLMRequestError, LLMTimeoutError } from '../providers/llm-errors';
import { LLM_REQUEST_TIMEOUT_MS } from '../providers/llm-fetch';
import { refusedUrl, silentUpstream, type TestUpstream } from '../providers/test-upstreams';

const originalFetch = globalThis.fetch;
const realSetTimeout = globalThis.setTimeout;
const ENV_KEYS = ['GROQ_API_KEY', 'ANTHROPIC_API_KEY'] as const;
const originalEnv = Object.fromEntries(ENV_KEYS.map((key) => [key, process.env[key]]));

const PAYLOAD = buildCorrectionRequest('system prompt', 'short dictation');
const REQUEST_ID = 'req-call-with-retry';

let upstream: TestUpstream | null = null;
let armed: number[] = [];
let timerSpy: ReturnType<typeof spyOn> | null = null;

beforeEach(() => {
  for (const key of ENV_KEYS) process.env[key] = `test-${key}`;
  armed = [];
  timerSpy = spyOn(globalThis, 'setTimeout').mockImplementation(((
    handler: (...args: unknown[]) => void,
    ms?: number,
    ...args: unknown[]
  ) => {
    armed.push(ms ?? 0);
    return realSetTimeout(handler, 5, ...args);
  }) as unknown as typeof setTimeout);
});

afterEach(() => {
  timerSpy?.mockRestore();
  timerSpy = null;
  globalThis.fetch = originalFetch;
  upstream?.stop();
  upstream = null;
  for (const key of ENV_KEYS) {
    if (originalEnv[key] === undefined) delete process.env[key];
    else process.env[key] = originalEnv[key];
  }
});

/** Send every fetch to `localUrl` and count the calls. */
function routeTo(localUrl: string): { calls: number } {
  const counter = { calls: 0 };
  globalThis.fetch = ((_input: string | URL | Request, init?: RequestInit) => {
    counter.calls += 1;
    return originalFetch(localUrl, init);
  }) as unknown as typeof fetch;
  return counter;
}

/** Answer every fetch with a fresh `status` response and count the calls. */
function respondWith(status: number): { calls: number } {
  const counter = { calls: 0 };
  globalThis.fetch = (async () => {
    counter.calls += 1;
    return new Response('upstream error', { status });
  }) as unknown as typeof fetch;
  return counter;
}

// groq retries 3 times, anthropic 2 (LLM_PROVIDER_RETRIES).
const CASES: Array<{ provider: LLMProvider; model: string; attempts: number }> = [
  { provider: 'groq', model: 'openai/gpt-oss-120b', attempts: 4 },
  { provider: 'anthropic', model: 'claude-haiku-5-5', attempts: 3 },
];

describe('callWithRetry', () => {
  for (const { provider, model, attempts } of CASES) {
    test(`${provider}: a timeout from our own timer is NOT retried, and still fails over`, async () => {
      upstream = silentUpstream();
      const counter = routeTo(upstream.url);

      const error = await callWithRetry(provider, PAYLOAD, REQUEST_ID, model).catch((e) => e);

      expect(error).toBeInstanceOf(LLMTimeoutError);
      expect((error as LLMRequestError).status).toBe(504);
      expect(shouldFallback(error)).toBe(true);
      expect(counter.calls).toBe(1);
      // One request bound armed, and no backoff delay after it.
      expect(armed.filter((ms) => ms === LLM_REQUEST_TIMEOUT_MS)).toHaveLength(1);
      expect(armed.filter((ms) => ms === 1_000)).toHaveLength(0);
    });

    test(`${provider}: an upstream 503 is still retried ${attempts - 1} times`, async () => {
      const counter = respondWith(503);

      const error = await callWithRetry(provider, PAYLOAD, REQUEST_ID, model).catch((e) => e);

      expect(error).toBeInstanceOf(LLMRequestError);
      expect(error).not.toBeInstanceOf(LLMTimeoutError);
      expect(counter.calls).toBe(attempts);
      expect(shouldFallback(error)).toBe(true);
    });

    test(`${provider}: an upstream-RETURNED 504 is still retried (only our timer skips retries)`, async () => {
      const counter = respondWith(504);

      const error = await callWithRetry(provider, PAYLOAD, REQUEST_ID, model).catch((e) => e);

      expect((error as LLMRequestError).status).toBe(504);
      expect(error).not.toBeInstanceOf(LLMTimeoutError);
      expect(counter.calls).toBe(attempts);
    });

    test(`${provider}: a network error is still retried ${attempts - 1} times`, async () => {
      const counter = routeTo(refusedUrl());

      const error = await callWithRetry(provider, PAYLOAD, REQUEST_ID, model).catch((e) => e);

      expect(error).not.toBeInstanceOf(LLMRequestError);
      expect(counter.calls).toBe(attempts);
    });
  }
});

describe('isRetryableLLMError', () => {
  test('refuses only our own timeout', () => {
    expect(isRetryableLLMError(new LLMTimeoutError('groq LLM request timeout after 20000ms', 'groq'))).toBe(false);
    expect(isRetryableLLMError(new LLMRequestError('upstream 504', 504, 'groq'))).toBe(true);
    expect(isRetryableLLMError(new LLMRequestError('upstream 503', 503))).toBe(true);
    expect(isRetryableLLMError(new TypeError('fetch failed'))).toBe(true);
  });
});
