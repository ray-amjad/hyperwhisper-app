import { afterEach, beforeEach, describe, expect, spyOn, test } from 'bun:test';

import {
  ANTHROPIC_MODEL,
  ANTHROPIC_WRAPPER_INSTRUCTION,
  anthropicThinkingFor,
  requestAnthropicChat,
} from './anthropic';
import { ANTHROPIC_MAX_TOKENS } from '../lib/llm-token-limits';
import { getLLMCompletionStatus } from '../lib/llm-completion';
import { shouldFallback } from '../lib/llm-provider';
import type { CorrectionRequestPayload } from './llm-contract';
import { LLMRequestError } from './llm-errors';
import { refusedUrl, silentUpstream, stalledBodyUpstream, type TestUpstream } from './test-upstreams';

// The Anthropic client talks to exactly one upstream over `fetch`, so the whole
// module is exercised by swapping `globalThis.fetch`. Nothing here mocks a
// module path — mock.module is process-global in bun and leaks into the other
// test files in the same run.

const originalFetch = globalThis.fetch;
const originalApiKey = process.env.ANTHROPIC_API_KEY;

const TEST_API_KEY = 'test-anthropic-key';
const REQUEST_ID = 'req-anthropic-test';

let lastRequest: { url: string; init: RequestInit } | null = null;

beforeEach(() => {
  process.env.ANTHROPIC_API_KEY = TEST_API_KEY;
  lastRequest = null;
});

afterEach(() => {
  globalThis.fetch = originalFetch;
  if (originalApiKey === undefined) {
    delete process.env.ANTHROPIC_API_KEY;
  } else {
    process.env.ANTHROPIC_API_KEY = originalApiKey;
  }
});

/** Install a fetch stub that records the outgoing request and returns `response`. */
function stubFetch(response: Response): void {
  globalThis.fetch = (async (url: string | URL | Request, init?: RequestInit) => {
    lastRequest = { url: String(url), init: init ?? {} };
    return response;
  }) as unknown as typeof fetch;
}

function requestBody(): Record<string, unknown> {
  if (!lastRequest) throw new Error('fetch was never called');
  return JSON.parse(String(lastRequest.init.body)) as Record<string, unknown>;
}

function requestHeaders(): Record<string, string> {
  if (!lastRequest) throw new Error('fetch was never called');
  return (lastRequest.init.headers ?? {}) as Record<string, string>;
}

function correctionPayload(system: string, user: string): CorrectionRequestPayload {
  return {
    messages: [
      { role: 'system', content: system },
      { role: 'user', content: user },
    ],
    temperature: 0.2,
  };
}

// ────────────────────────────────────────────────────────────────────────────
// requestAnthropicChat — the non-streaming post-process path
// ────────────────────────────────────────────────────────────────────────────

describe('requestAnthropicChat', () => {
  test('throws before any network call when ANTHROPIC_API_KEY is unset', async () => {
    delete process.env.ANTHROPIC_API_KEY;
    globalThis.fetch = (async () => {
      throw new Error('fetch should not be reached without an API key');
    }) as unknown as typeof fetch;

    await expect(requestAnthropicChat(correctionPayload('sys', 'user'), REQUEST_ID)).rejects.toThrow(
      'ANTHROPIC_API_KEY not configured',
    );
  });

  test('translates the OpenAI-shaped payload into an Anthropic Messages request', async () => {
    stubFetch(
      Response.json({
        content: [{ type: 'text', text: 'ok' }],
        stop_reason: 'end_turn',
        usage: { input_tokens: 10, output_tokens: 3 },
      }),
    );

    await requestAnthropicChat(correctionPayload('Clean up the transcript.', 'helo wrold'), REQUEST_ID);

    expect(lastRequest?.url).toBe('https://api.anthropic.com/v1/messages');
    expect(lastRequest?.init.method).toBe('POST');

    const headers = requestHeaders();
    expect(headers['x-api-key']).toBe(TEST_API_KEY);
    expect(headers['anthropic-version']).toBe('2023-06-01');

    const body = requestBody();
    // The system message moves to `system` with the marker instruction appended;
    // only the user turn survives in `messages`.
    expect(body.system).toBe(`Clean up the transcript.\n\n${ANTHROPIC_WRAPPER_INSTRUCTION}`);
    expect(body.messages).toEqual([{ role: 'user', content: 'helo wrold' }]);
    expect(body.stream).toBe(false);
    expect(body.max_tokens).toBe(ANTHROPIC_MAX_TOKENS);
  });

  test('serves Haiku 5.5 with thinking turned off, sent as the last body key', async () => {
    stubFetch(Response.json({ content: [{ type: 'text', text: 'ok' }], usage: { input_tokens: 1, output_tokens: 1 } }));

    await requestAnthropicChat(correctionPayload('sys', 'user'), REQUEST_ID);

    const body = requestBody();
    expect(ANTHROPIC_MODEL).toBe('claude-haiku-5-5');
    expect(body.model).toBe('claude-haiku-5-5');
    expect(body.thinking).toEqual({ type: 'disabled' });
    expect(Object.keys(body).at(-1)).toBe('thinking');
  });

  test('a model that needs no thinking switch gets no thinking field', async () => {
    stubFetch(Response.json({ content: [{ type: 'text', text: 'ok' }], usage: { input_tokens: 1, output_tokens: 1 } }));

    await requestAnthropicChat(correctionPayload('sys', 'user'), REQUEST_ID, undefined, 'claude-sonnet-4-6');

    const body = requestBody();
    expect(body.model).toBe('claude-sonnet-4-6');
    expect('thinking' in body).toBe(false);
  });

  test('still sends the marker instruction when the payload has no system turn', async () => {
    stubFetch(
      Response.json({
        content: [{ type: 'text', text: 'ok' }],
        usage: { input_tokens: 1, output_tokens: 1 },
      }),
    );

    await requestAnthropicChat({ messages: [], temperature: 0 }, REQUEST_ID);

    const body = requestBody();
    expect(body.system).toBe(`\n\n${ANTHROPIC_WRAPPER_INSTRUCTION}`);
    expect(body.messages).toEqual([{ role: 'user', content: '' }]);
  });

  test('bills every usage bucket: uncached input, output, cache write and cache read', async () => {
    stubFetch(
      Response.json({
        content: [{ type: 'text', text: 'ok' }],
        stop_reason: 'end_turn',
        usage: {
          input_tokens: 1000,
          output_tokens: 500,
          cache_creation_input_tokens: 2000,
          cache_read_input_tokens: 4000,
        },
      }),
    );

    const result = await requestAnthropicChat(correctionPayload('sys', 'user'), REQUEST_ID);

    // Haiku 5.5: $0.10/Mtok in, $0.50/Mtok out, cache write 1.25x in, cache read 0.10x in.
    // 0.000100 + 0.000250 + 0.000250 + 0.000040
    expect(result.costUsd).toBeCloseTo(0.00064, 9);
    // Usage is normalized to the shared GroqUsage shape; the cache buckets are a
    // billing input only and must not inflate the reported prompt/total tokens.
    expect(result.usage).toEqual({ prompt_tokens: 1000, completion_tokens: 500, total_tokens: 1500 });
  });

  test('treats a response with no usage object as zero-cost instead of throwing', async () => {
    stubFetch(Response.json({ content: [{ type: 'text', text: 'ok' }] }));

    const result = await requestAnthropicChat(correctionPayload('sys', 'user'), REQUEST_ID);

    expect(result.costUsd).toBe(0);
    expect(result.usage).toEqual({ prompt_tokens: 0, completion_tokens: 0, total_tokens: 0 });
  });

  test('a non-number token count bills 0 for that bucket instead of a NaN cost (#922)', async () => {
    stubFetch(
      Response.json({
        content: [{ type: 'text', text: 'ok' }],
        stop_reason: 'end_turn',
        usage: { input_tokens: '1.2k', output_tokens: 500 },
      }),
    );

    const result = await requestAnthropicChat(correctionPayload('sys', 'user'), REQUEST_ID);

    expect(Number.isFinite(result.costUsd)).toBe(true);
    // Only the 500 well-formed output tokens are billed.
    expect(result.costUsd).toBeCloseTo(0.00025, 9);
    expect(result.usage).toEqual({ prompt_tokens: 0, completion_tokens: 500, total_tokens: 500 });
  });

  test('returns the raw body so the completion policy can read Anthropic stop_reason', async () => {
    stubFetch(
      Response.json({
        content: [{ type: 'text', text: 'truncated' }],
        stop_reason: 'max_tokens',
        usage: { input_tokens: 5, output_tokens: 8192 },
      }),
    );

    const result = await requestAnthropicChat(correctionPayload('sys', 'user'), REQUEST_ID);

    expect(getLLMCompletionStatus(result.raw)).toEqual({ state: 'output_limit', reason: 'max_tokens' });
  });

  test('attaches the upstream status to the thrown error so a 5xx triggers provider fallback', async () => {
    stubFetch(new Response('overloaded', { status: 529 }));

    const error = await requestAnthropicChat(correctionPayload('sys', 'user'), REQUEST_ID).catch((e) => e);

    expect(error).toBeInstanceOf(LLMRequestError);
    expect((error as { status?: number }).status).toBe(529);
    expect('provider' in error).toBe(false);
    expect((error as Error).message).toContain('529');
    expect(shouldFallback(error)).toBe(true);
  });

  test('a 4xx is surfaced with its status and does NOT trigger provider fallback', async () => {
    stubFetch(new Response('invalid request', { status: 400 }));

    const error = await requestAnthropicChat(correctionPayload('sys', 'user'), REQUEST_ID).catch((e) => e);

    expect(error).toBeInstanceOf(LLMRequestError);
    expect((error as { status?: number }).status).toBe(400);
    expect(shouldFallback(error)).toBe(false);
  });
});

// ────────────────────────────────────────────────────────────────────────────
// Bounded wait (#782) — real sockets on 127.0.0.1, real fetch, real abort.
// ANTHROPIC_API_URL is a module constant, so a shim forwards the request to the
// local upstream and records the URL the client asked for.
// ────────────────────────────────────────────────────────────────────────────

const TIMEOUT_MS = 200;

function routeAnthropicTo(localUrl: string): string[] {
  const requested: string[] = [];
  globalThis.fetch = ((input: string | URL | Request, init?: RequestInit) => {
    requested.push(String(input));
    return originalFetch(localUrl, init);
  }) as unknown as typeof fetch;
  return requested;
}

describe('requestAnthropicChat bounded wait', () => {
  let upstream: TestUpstream | undefined;
  afterEach(() => {
    upstream?.stop();
    upstream = undefined;
  });

  test('rejects with a 504 within the timeout when the upstream never answers', async () => {
    upstream = silentUpstream();
    const requested = routeAnthropicTo(upstream.url);

    const startedAt = performance.now();
    const error = await requestAnthropicChat(correctionPayload('sys', 'user'), REQUEST_ID, TIMEOUT_MS).catch((e) => e);
    const elapsedMs = performance.now() - startedAt;

    expect(requested).toEqual(['https://api.anthropic.com/v1/messages']);
    expect(error).toBeInstanceOf(LLMRequestError);
    expect((error as LLMRequestError).status).toBe(504);
    expect((error as LLMRequestError).provider).toBe('anthropic');
    expect(shouldFallback(error)).toBe(true);
    expect(elapsedMs).toBeLessThan(TIMEOUT_MS + 1500);
  });

  test('rejects with a 504 when the headers arrive but the body stalls', async () => {
    upstream = stalledBodyUpstream();
    routeAnthropicTo(upstream.url);

    const error = await requestAnthropicChat(correctionPayload('sys', 'user'), REQUEST_ID, TIMEOUT_MS).catch((e) => e);

    expect((error as LLMRequestError).status).toBe(504);
    expect(shouldFallback(error)).toBe(true);
  });

  // Only our timer is mapped. A real network error stays the untagged error it
  // was before #782 (retried by callWithRetry, not failed over).
  test('passes a refused connection through untagged, not as a timeout', async () => {
    routeAnthropicTo(refusedUrl());

    const error = await requestAnthropicChat(correctionPayload('sys', 'user'), REQUEST_ID, TIMEOUT_MS).catch((e) => e);

    expect(error).toBeInstanceOf(Error);
    expect(error).not.toBeInstanceOf(LLMRequestError);
    expect((error as { status?: number }).status).toBeUndefined();
    expect(shouldFallback(error)).toBe(false);
  });
});

// The non-streaming bound scales with the transcript (review rounds 1 and 2):
// the request arms its timer with computeLLMRequestTimeoutMs(transcript chars)
// unless a test injects its own. Spy on setTimeout to read the delay armed.
describe('requestAnthropicChat timeout scales with the transcript', () => {
  async function armedDelays(fn: () => Promise<unknown>): Promise<number[]> {
    const spy = spyOn(globalThis, 'setTimeout');
    try {
      await fn();
      return spy.mock.calls.map((call) => call[1] as number);
    } finally {
      spy.mockRestore();
    }
  }

  test('a short transcript arms the 20 s floor', async () => {
    stubFetch(Response.json({ content: [{ type: 'text', text: 'ok' }] }));
    const delays = await armedDelays(() => requestAnthropicChat(correctionPayload('sys', 'user'), REQUEST_ID));
    expect(delays).toContain(20_000);
  });

  test('a 4,997-character transcript arms 50 s, not the flat 20 s', async () => {
    stubFetch(Response.json({ content: [{ type: 'text', text: 'ok' }] }));
    const delays = await armedDelays(() =>
      requestAnthropicChat(correctionPayload('sys', 'x'.repeat(4_997)), REQUEST_ID));
    expect(delays).toContain(50_000);
    expect(delays).not.toContain(20_000);
  });

  test('a long system prompt does not lengthen the bound for a short transcript', async () => {
    stubFetch(Response.json({ content: [{ type: 'text', text: 'ok' }] }));
    const delays = await armedDelays(() =>
      requestAnthropicChat(correctionPayload('s'.repeat(30_000), 'user'), REQUEST_ID));
    expect(delays).toContain(20_000);
    expect(delays.filter((ms) => ms > 20_000)).toEqual([]);
  });

  test('an injected timeout still wins over the computed one', async () => {
    stubFetch(Response.json({ content: [{ type: 'text', text: 'ok' }] }));
    const delays = await armedDelays(() =>
      requestAnthropicChat(correctionPayload('sys', 'x'.repeat(4_997)), REQUEST_ID, 1_234));
    expect(delays).toContain(1_234);
    expect(delays).not.toContain(50_000);
  });
});

describe('anthropicThinkingFor', () => {
  test('matches the Rust hw-net table: off for Haiku 5.5 and Sonnet 5, between_tools for Sonnet 5.5', () => {
    expect(anthropicThinkingFor('claude-haiku-5-5')).toEqual({ type: 'disabled' });
    expect(anthropicThinkingFor('claude-sonnet-5')).toEqual({ type: 'disabled' });
    expect(anthropicThinkingFor('claude-sonnet-5-5')).toEqual({ type: 'between_tools' });
    expect(anthropicThinkingFor('claude-sonnet-4-6')).toBeUndefined();
    expect(anthropicThinkingFor('claude-haiku-4-5')).toBeUndefined();
  });
});
