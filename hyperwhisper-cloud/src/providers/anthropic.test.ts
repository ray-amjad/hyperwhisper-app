import { afterEach, beforeEach, describe, expect, test } from 'bun:test';

import {
  ANTHROPIC_WRAPPER_INSTRUCTION,
  requestAnthropicChat,
  streamAnthropicChat,
  type AnthropicMessage,
} from './anthropic';
import { ANTHROPIC_MAX_TOKENS } from '../lib/llm-token-limits';
import { getLLMCompletionStatus } from '../lib/llm-completion';
import { shouldFallback } from '../lib/llm-provider';
import type { CorrectionRequestPayload } from './groq-llm';

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
function stubFetch(response: Response | ((signal: AbortSignal | null | undefined) => Response)): void {
  globalThis.fetch = (async (url: string | URL | Request, init?: RequestInit) => {
    lastRequest = { url: String(url), init: init ?? {} };
    const signal = (init?.signal ?? null) as AbortSignal | null;
    return typeof response === 'function' ? response(signal) : response;
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

function sseText(events: unknown[]): string {
  return events.map((event) => `data: ${JSON.stringify(event)}\n\n`).join('');
}

/**
 * A response body that delivers `text` on the first read, then closes, errors,
 * or stays open until the caller's abort signal fires. Pull-based on purpose:
 * `controller.error()` from `start()` would discard the queued chunk, so the
 * consumer would never see the events that precede the failure.
 */
function bodyStream(
  text: string,
  opts: { closeAfter?: boolean; errorAfter?: Error; abortSignal?: AbortSignal | null } = {},
): ReadableStream<Uint8Array> {
  const { closeAfter = true, errorAfter, abortSignal } = opts;
  const encoder = new TextEncoder();
  let sent = false;
  return new ReadableStream<Uint8Array>({
    pull(controller) {
      if (!sent) {
        sent = true;
        controller.enqueue(encoder.encode(text));
        return;
      }
      if (errorAfter) {
        controller.error(errorAfter);
        return;
      }
      if (abortSignal) {
        // Mirror what a real fetch does when its signal aborts mid-body: the
        // response stream errors out rather than hanging forever.
        const aborted = () => new DOMException('The operation was aborted.', 'AbortError');
        if (abortSignal.aborted) return Promise.reject(aborted());
        return new Promise<void>((_resolve, reject) => {
          abortSignal.addEventListener('abort', () => reject(aborted()));
        });
      }
      if (closeAfter) controller.close();
    },
  });
}

/** Read a stream to completion and return the concatenated text. */
async function drain(stream: ReadableStream<Uint8Array>): Promise<string> {
  const reader = stream.getReader();
  const decoder = new TextDecoder();
  let out = '';
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    out += decoder.decode(value, { stream: true });
  }
  return out;
}

/** Split an SSE payload into its `data:` values, in order. */
function sseDataLines(raw: string): string[] {
  return raw
    .split('\n')
    .filter((line) => line.startsWith('data: '))
    .map((line) => line.slice(6));
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

    // Haiku 4.5: $1/Mtok in, $5/Mtok out, cache write 1.25x in, cache read 0.10x in.
    // 0.001000 + 0.002500 + 0.002500 + 0.000400
    expect(result.costUsd).toBeCloseTo(0.0064, 9);
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

    expect(error).toBeInstanceOf(Error);
    expect((error as { status?: number }).status).toBe(529);
    expect((error as Error).message).toContain('529');
    expect(shouldFallback(error)).toBe(true);
  });

  test('a 4xx is surfaced with its status and does NOT trigger provider fallback', async () => {
    stubFetch(new Response('invalid request', { status: 400 }));

    const error = await requestAnthropicChat(correctionPayload('sys', 'user'), REQUEST_ID).catch((e) => e);

    expect((error as { status?: number }).status).toBe(400);
    expect(shouldFallback(error)).toBe(false);
  });
});

// ────────────────────────────────────────────────────────────────────────────
// streamAnthropicChat — the /assistant SSE path
// ────────────────────────────────────────────────────────────────────────────

const ASSISTANT_MESSAGES: AnthropicMessage[] = [{ role: 'user', content: 'what is on my screen?' }];

describe('streamAnthropicChat', () => {
  test('throws synchronously when ANTHROPIC_API_KEY is unset', () => {
    delete process.env.ANTHROPIC_API_KEY;

    expect(() => streamAnthropicChat('sys', ASSISTANT_MESSAGES, REQUEST_ID)).toThrow(
      'ANTHROPIC_API_KEY not configured',
    );
  });

  test('converts Anthropic stream events into OpenAI-compatible chunks and bills the full usage', async () => {
    stubFetch(
      new Response(
        bodyStream(
          sseText([
            {
              type: 'message_start',
              message: {
                usage: {
                  input_tokens: 1000,
                  cache_creation_input_tokens: 2000,
                  cache_read_input_tokens: 4000,
                },
              },
            },
            { type: 'content_block_delta', delta: { text: 'Hello' } },
            { type: 'content_block_delta', delta: { text: ' world' } },
            { type: 'message_delta', usage: { output_tokens: 500 } },
            { type: 'message_stop' },
          ]),
        ),
      ),
    );

    const { stream, costPromise } = streamAnthropicChat('sys prompt', ASSISTANT_MESSAGES, REQUEST_ID);
    const out = await drain(stream);

    expect(sseDataLines(out)).toEqual([
      JSON.stringify({ choices: [{ delta: { content: 'Hello' } }] }),
      JSON.stringify({ choices: [{ delta: { content: ' world' } }] }),
      '[DONE]',
    ]);
    // Same buckets as the non-streaming path: 1000 in + 500 out + 2000 write + 4000 read.
    expect(await costPromise).toBeCloseTo(0.0064, 9);

    const body = requestBody();
    expect(body.stream).toBe(true);
    expect(body.system).toBe('sys prompt');
    expect(body.messages).toEqual(ASSISTANT_MESSAGES);
    expect(body.max_tokens).toBe(ANTHROPIC_MAX_TOKENS);
  });

  test('skips malformed data lines without dropping the deltas around them', async () => {
    const raw =
      `data: ${JSON.stringify({ type: 'content_block_delta', delta: { text: 'before' } })}\n\n` +
      'data: {not json\n\n' +
      ': an SSE comment line\n\n' +
      `data: ${JSON.stringify({ type: 'content_block_delta', delta: { text: 'after' } })}\n\n`;
    stubFetch(new Response(bodyStream(raw)));

    const { stream } = streamAnthropicChat('sys', ASSISTANT_MESSAGES, REQUEST_ID);

    expect(sseDataLines(await drain(stream))).toEqual([
      JSON.stringify({ choices: [{ delta: { content: 'before' } }] }),
      JSON.stringify({ choices: [{ delta: { content: 'after' } }] }),
      '[DONE]',
    ]);
  });

  test('an upstream non-2xx closes the stream with an error chunk and bills nothing', async () => {
    stubFetch(new Response('overloaded', { status: 529 }));

    const { stream, costPromise } = streamAnthropicChat('sys', ASSISTANT_MESSAGES, REQUEST_ID);
    const lines = sseDataLines(await drain(stream));

    expect(lines).toHaveLength(2);
    expect(JSON.parse(lines[0]!)).toEqual({
      choices: [{ delta: { content: '' }, finish_reason: 'error' }],
      error: 'Anthropic API error: 529',
    });
    expect(lines[1]).toBe('[DONE]');
    // The client is not charged for a request the upstream refused.
    expect(await costPromise).toBe(0);
  });

  test('a 2xx response with no body still terminates the stream at zero cost', async () => {
    stubFetch(new Response(null, { status: 200 }));

    const { stream, costPromise } = streamAnthropicChat('sys', ASSISTANT_MESSAGES, REQUEST_ID);

    expect(sseDataLines(await drain(stream))).toEqual(['[DONE]']);
    expect(await costPromise).toBe(0);
  });

  test('a mid-stream upstream failure bills the tokens already observed, not zero', async () => {
    stubFetch(
      new Response(
        bodyStream(
          sseText([
            { type: 'message_start', message: { usage: { input_tokens: 1000 } } },
            { type: 'content_block_delta', delta: { text: 'partial' } },
            { type: 'message_delta', usage: { output_tokens: 500 } },
          ]),
          { errorAfter: new Error('upstream connection reset') },
        ),
      ),
    );

    const { stream, costPromise } = streamAnthropicChat('sys', ASSISTANT_MESSAGES, REQUEST_ID);
    const lines = sseDataLines(await drain(stream));

    // The partial content the client did receive is preserved, and the stream is
    // still terminated cleanly rather than left hanging.
    expect(lines).toEqual([
      JSON.stringify({ choices: [{ delta: { content: 'partial' } }] }),
      '[DONE]',
    ]);
    // Anthropic charges for what it generated before the break: 1000 in + 500 out.
    expect(await costPromise).toBeCloseTo(0.0035, 9);
  });

  test('a client disconnect aborts the upstream request and bills only the tokens seen so far', async () => {
    let upstreamAborted = false;
    stubFetch((signal) => {
      signal?.addEventListener('abort', () => {
        upstreamAborted = true;
      });
      return new Response(
        bodyStream(
          sseText([
            { type: 'message_start', message: { usage: { input_tokens: 1000 } } },
            { type: 'content_block_delta', delta: { text: 'first' } },
          ]),
          { closeAfter: false, abortSignal: signal },
        ),
      );
    });

    const { stream, costPromise } = streamAnthropicChat('sys', ASSISTANT_MESSAGES, REQUEST_ID);
    const reader = stream.getReader();

    // Read the one chunk the client got, then hang up mid-response.
    const first = await reader.read();
    expect(new TextDecoder().decode(first.value)).toContain('first');
    await reader.cancel('client disconnected');

    expect(upstreamAborted).toBe(true);
    // Input tokens are known from message_start; no message_delta arrived, so
    // output is billed at 0 rather than the cost being dropped entirely.
    expect(await costPromise).toBeCloseTo(0.001, 9);
  });
});
