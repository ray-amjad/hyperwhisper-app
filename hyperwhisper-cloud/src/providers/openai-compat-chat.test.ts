// Tests for the shared OpenAI-compatible chat client (openai-compat-chat.ts)
// and the six LLM clients that delegate to it: cerebras, groq, openai, gemini,
// mistral, xai.
//
// Three behaviours here are load-bearing outside this file:
//
//   1. Fallback routing. post-process.ts decides whether to retry on another
//      provider by calling `shouldFallback(error)`, which only says yes for a
//      5xx `status` on the error object. That `status` is attached here. So the
//      status this file puts on a thrown error IS the fallback decision, and a
//      429 or a 400 must stay on the same provider.
//   2. Fail-closed billing. A response whose `usage` block is missing or has
//      drifted must still be billed, via a char-based estimate — never at 0.
//   3. Per-provider body quirks. GPT-5 rejects `temperature: 0`, so the OpenAI
//      client strips it; Gemini 2.5-flash bills thinking tokens as output
//      unless `reasoning_effort: 'none'` is sent. Both are silent-cost or
//      hard-failure regressions if a refactor flattens them back together.
//
// Only global `fetch` and the per-provider API-key env vars are touched. No
// module is mocked: bun's module registry is process-wide and replacing a
// shared module leaks into every other test file in the same run. The real cost
// calculator, the real usage type guard and the real error classes all run.

import { afterEach, beforeEach, describe, expect, test } from 'bun:test';
import {
  computeCerebrasChatCost,
  computeGeminiChatCost,
  computeGroqChatCost,
  computeMistralChatCost,
  computeOpenAIChatCost,
  computeXaiGrokFastChatCost,
  estimateUsageFromChars,
  type GroqUsage,
} from '../lib/cost-calculator';
import { GROQ_MAX_COMPLETION_TOKENS } from '../lib/llm-token-limits';
import { shouldFallback } from '../lib/llm-provider';
import { buildCorrectionRequest, requestGroqChat, type CorrectionRequestPayload } from './groq-llm';
import { requestCerebrasChat } from './cerebras';
import { requestOpenAIChat } from './openai-llm';
import { requestGeminiChat } from './gemini-llm';
import { requestMistralChat } from './mistral-llm';
import { requestXaiGrokChat } from './xai-llm';

// ---------------------------------------------------------------------------
// Global fetch capture
// ---------------------------------------------------------------------------
type FetchCall = { url: string; init: RequestInit };
type FetchHandler = (call: FetchCall) => Response | Promise<Response>;

const originalFetch = globalThis.fetch;

const API_KEY_ENV = [
  'CEREBRAS_API_KEY',
  'GROQ_API_KEY',
  'OPENAI_API_KEY',
  'GEMINI_API_KEY',
  'GOOGLE_GEMINI_API_KEY',
  'MISTRAL_API_KEY',
  'XAI_API_KEY',
  'GROK_API_KEY',
] as const;

const savedEnv: Record<string, string | undefined> = {};

let calls: FetchCall[] = [];
let handler: FetchHandler = () => chatResponse();

const USAGE: GroqUsage = { prompt_tokens: 120, completion_tokens: 40, total_tokens: 160 };

function chatResponse(body: Record<string, unknown> = {}): Response {
  return new Response(
    JSON.stringify({
      choices: [{ message: { role: 'assistant', content: 'cleaned transcript' }, finish_reason: 'stop' }],
      usage: USAGE,
      ...body,
    }),
    { status: 200, headers: { 'content-type': 'application/json' } },
  );
}

const PAYLOAD: CorrectionRequestPayload = buildCorrectionRequest('system prompt', 'user transcript');

function lastBody(): Record<string, unknown> {
  return JSON.parse(calls[calls.length - 1].init.body as string);
}

function lastHeaders(): Record<string, string> {
  return calls[calls.length - 1].init.headers as Record<string, string>;
}

async function captureError(fn: () => Promise<unknown>): Promise<unknown> {
  try {
    await fn();
  } catch (error) {
    return error;
  }
  throw new Error('expected the call to reject, but it resolved');
}

function errorStatus(error: unknown): number | undefined {
  return (error as { status?: number }).status;
}

function errorProvider(error: unknown): string | undefined {
  return (error as { provider?: string }).provider;
}

beforeEach(() => {
  calls = [];
  handler = () => chatResponse();

  for (const key of API_KEY_ENV) {
    savedEnv[key] = process.env[key];
    // Placeholder values only — never a real credential. The repo is public.
    process.env[key] = `test-${key.toLowerCase()}`;
  }

  globalThis.fetch = (async (input: string | URL | Request, init: RequestInit = {}) => {
    const url = typeof input === 'string' ? input : input.toString();
    calls.push({ url, init });
    return handler({ url, init });
  }) as unknown as typeof fetch;
});

afterEach(() => {
  globalThis.fetch = originalFetch;
  for (const key of API_KEY_ENV) {
    if (savedEnv[key] === undefined) {
      delete process.env[key];
    } else {
      process.env[key] = savedEnv[key];
    }
  }
});

// ===========================================================================
// Endpoint, auth and model routing
// ===========================================================================
describe('provider endpoint and auth', () => {
  test('posts to the cerebras chat endpoint with its own key and fixed model', async () => {
    await requestCerebrasChat(PAYLOAD, 'req-1');

    expect(calls).toHaveLength(1);
    expect(calls[0].url).toBe('https://api.cerebras.ai/v1/chat/completions');
    expect(calls[0].init.method).toBe('POST');
    expect(lastHeaders().Authorization).toBe('Bearer test-cerebras_api_key');
    expect(lastHeaders()['content-type']).toBe('application/json');
    expect(lastBody().model).toBe('gpt-oss-120b');
  });

  test('posts to the groq chat endpoint with its own key and fixed model', async () => {
    await requestGroqChat(PAYLOAD, 'req-1');

    expect(calls[0].url).toBe('https://api.groq.com/openai/v1/chat/completions');
    expect(lastHeaders().Authorization).toBe('Bearer test-groq_api_key');
    expect(lastBody().model).toBe('openai/gpt-oss-120b');
  });

  test('posts to the xai chat endpoint with its own key and fixed model', async () => {
    await requestXaiGrokChat(PAYLOAD, 'req-1');

    expect(calls[0].url).toBe('https://api.x.ai/v1/chat/completions');
    expect(lastHeaders().Authorization).toBe('Bearer test-xai_api_key');
    expect(lastBody().model).toBe('grok-4.3');
  });

  test('the multi-model providers send the model they were handed, not a default', async () => {
    // resolveLLMModel() has already validated the model against the provider
    // allowlist upstream; the client must not quietly substitute its own.
    await requestOpenAIChat(PAYLOAD, 'req-1', 'gpt-5-nano');
    expect(calls[0].url).toBe('https://api.openai.com/v1/chat/completions');
    expect(lastBody().model).toBe('gpt-5-nano');

    await requestGeminiChat(PAYLOAD, 'req-1', 'gemini-2.5-flash-lite');
    expect(calls[1].url).toBe('https://generativelanguage.googleapis.com/v1beta/openai/chat/completions');
    expect(lastBody().model).toBe('gemini-2.5-flash-lite');

    // Mistral is down to a single allowlisted model, so the handed model and the
    // provider default coincide — the client still must send what it was handed.
    await requestMistralChat(PAYLOAD, 'req-1', 'mistral-small-latest');
    expect(calls[2].url).toBe('https://api.mistral.ai/v1/chat/completions');
    expect(lastBody().model).toBe('mistral-small-latest');
  });

  test('accepts the secondary env var name for the two providers that have one', async () => {
    delete process.env.GEMINI_API_KEY;
    await requestGeminiChat(PAYLOAD, 'req-1', 'gemini-2.5-flash');
    expect(lastHeaders().Authorization).toBe('Bearer test-google_gemini_api_key');

    delete process.env.XAI_API_KEY;
    await requestXaiGrokChat(PAYLOAD, 'req-1');
    expect(lastHeaders().Authorization).toBe('Bearer test-grok_api_key');
  });
});

// ===========================================================================
// Per-provider request body
// ===========================================================================
describe('request body construction', () => {
  test('every provider sends the conversation and disables streaming', async () => {
    const senders: Array<() => Promise<unknown>> = [
      () => requestCerebrasChat(PAYLOAD, 'req-1'),
      () => requestGroqChat(PAYLOAD, 'req-1'),
      () => requestXaiGrokChat(PAYLOAD, 'req-1'),
      () => requestOpenAIChat(PAYLOAD, 'req-1', 'gpt-5-mini'),
      () => requestGeminiChat(PAYLOAD, 'req-1', 'gemini-2.5-flash'),
      () => requestMistralChat(PAYLOAD, 'req-1', 'mistral-small-latest'),
    ];

    for (const send of senders) {
      await send();
      // The route reads the whole completion in one shot; a streamed body
      // would arrive as SSE and fail to parse as JSON.
      expect(lastBody().stream).toBe(false);
      expect(lastBody().messages).toEqual(PAYLOAD.messages);
    }
  });

  test('openai drops temperature because GPT-5 rejects anything but its default', async () => {
    expect(PAYLOAD.temperature).toBe(0);

    await requestOpenAIChat(PAYLOAD, 'req-1', 'gpt-5-mini');

    // Sending `temperature: 0` here is a hard 400 from OpenAI, not a nudge.
    expect(lastBody()).not.toHaveProperty('temperature');
    expect(lastBody().reasoning_effort).toBe('minimal');
  });

  test('the other providers keep the temperature the payload asked for', async () => {
    await requestCerebrasChat(PAYLOAD, 'req-1');
    expect(lastBody().temperature).toBe(0);

    await requestMistralChat(PAYLOAD, 'req-1', 'mistral-small-latest');
    expect(lastBody().temperature).toBe(0);
  });

  test('gemini disables thinking on the 2.5-flash family only', async () => {
    // 2.5-flash defaults to a dynamic thinking budget and bills thinking
    // tokens as output — pure cost on a text-cleanup call.
    await requestGeminiChat(PAYLOAD, 'req-1', 'gemini-2.5-flash');
    expect(lastBody().reasoning_effort).toBe('none');

    await requestGeminiChat(PAYLOAD, 'req-1', 'gemini-2.5-flash-lite');
    expect(lastBody().reasoning_effort).toBe('none');

    await requestGeminiChat(PAYLOAD, 'req-1', 'gemini-3.0-pro');
    expect(lastBody()).not.toHaveProperty('reasoning_effort');
  });

  test('mistral sends no reasoning_effort at all', async () => {
    await requestMistralChat(PAYLOAD, 'req-1', 'mistral-small-latest');

    expect(lastBody()).not.toHaveProperty('reasoning_effort');
    expect(lastBody()).not.toHaveProperty('max_completion_tokens');
  });

  test('groq is the only provider that pins an output ceiling', async () => {
    await requestGroqChat(PAYLOAD, 'req-1');
    expect(lastBody().max_completion_tokens).toBe(GROQ_MAX_COMPLETION_TOKENS);

    // Everyone else uses their model default; adding a cap here would risk
    // finish_reason=length on long dictations.
    await requestCerebrasChat(PAYLOAD, 'req-1');
    expect(lastBody()).not.toHaveProperty('max_completion_tokens');

    await requestXaiGrokChat(PAYLOAD, 'req-1');
    expect(lastBody()).not.toHaveProperty('max_completion_tokens');
  });

  test('cerebras pins reasoning_effort even when the payload carries its own', async () => {
    // cerebras spreads `reasoning_effort` AFTER the payload, so it always wins.
    const withOverride = { ...PAYLOAD, reasoning_effort: 'high' } as CorrectionRequestPayload;

    await requestCerebrasChat(withOverride, 'req-1');

    expect(lastBody().reasoning_effort).toBe('low');
  });

  test('xai lets the payload override its reasoning_effort default', async () => {
    // xai spreads `reasoning_effort` BEFORE the payload. This asymmetry with
    // cerebras is easy to erase in a refactor, so it is pinned deliberately.
    await requestXaiGrokChat(PAYLOAD, 'req-1');
    expect(lastBody().reasoning_effort).toBe('none');

    const withOverride = { ...PAYLOAD, reasoning_effort: 'high' } as CorrectionRequestPayload;
    await requestXaiGrokChat(withOverride, 'req-1');
    expect(lastBody().reasoning_effort).toBe('high');
  });
});

// ===========================================================================
// Success path: usage passthrough + cost
// ===========================================================================
describe('successful completion', () => {
  test('returns the parsed body, the reported usage, and the provider cost for it', async () => {
    const result = await requestCerebrasChat(PAYLOAD, 'req-1');

    expect(result.usage).toEqual(USAGE);
    expect((result.raw as { choices: Array<{ message: { content: string } }> }).choices[0].message.content)
      .toBe('cleaned transcript');
    expect(result.costUsd).toBe(computeCerebrasChatCost(USAGE));
    expect(result.costUsd).toBeGreaterThan(0);
  });

  test('each provider is priced with its own rate card, not a shared one', async () => {
    const cases: Array<[() => Promise<{ costUsd: number }>, number]> = [
      [() => requestCerebrasChat(PAYLOAD, 'req-1'), computeCerebrasChatCost(USAGE)],
      [() => requestGroqChat(PAYLOAD, 'req-1'), computeGroqChatCost(USAGE)],
      [() => requestXaiGrokChat(PAYLOAD, 'req-1'), computeXaiGrokFastChatCost(USAGE)],
      [() => requestOpenAIChat(PAYLOAD, 'req-1', 'gpt-5-mini'), computeOpenAIChatCost('gpt-5-mini', USAGE)],
      [() => requestGeminiChat(PAYLOAD, 'req-1', 'gemini-2.5-flash'), computeGeminiChatCost('gemini-2.5-flash', USAGE)],
      [
        () => requestMistralChat(PAYLOAD, 'req-1', 'mistral-small-latest'),
        computeMistralChatCost('mistral-small-latest', USAGE),
      ],
    ];

    for (const [send, expected] of cases) {
      const result = await send();
      expect(result.costUsd).toBe(expected);
      expect(result.costUsd).toBeGreaterThan(0);
    }
  });

  test('prices the multi-model providers on the model actually used', async () => {
    const flash = await requestGeminiChat(PAYLOAD, 'req-1', 'gemini-2.5-flash');
    const flashLite = await requestGeminiChat(PAYLOAD, 'req-1', 'gemini-2.5-flash-lite');

    expect(flash.costUsd).toBe(computeGeminiChatCost('gemini-2.5-flash', USAGE));
    expect(flashLite.costUsd).toBe(computeGeminiChatCost('gemini-2.5-flash-lite', USAGE));
    // Billing the cheap model at the expensive model's rate (or the reverse)
    // is invisible in the response, so pin that they differ.
    expect(flash.costUsd).not.toBe(flashLite.costUsd);
  });
});

// ===========================================================================
// Fail-closed billing when `usage` is missing or has drifted
// ===========================================================================
describe('missing or unrecognized usage', () => {
  const noUsage = () => new Response(
    JSON.stringify({ choices: [{ message: { content: 'cleaned transcript' }, finish_reason: 'stop' }] }),
    { status: 200 },
  );

  test('bills a char-based estimate rather than zero when usage is absent', async () => {
    handler = noUsage;

    const result = await requestCerebrasChat(PAYLOAD, 'req-1');

    const promptChars = PAYLOAD.messages.reduce((sum, m) => sum + m.content.length, 0);
    const estimate = estimateUsageFromChars(promptChars, 'cleaned transcript'.length);

    expect(result.usage).toBeUndefined();
    expect(result.costUsd).toBe(computeCerebrasChatCost(estimate));
    // The whole point of the fallback: a vendor schema change must not make
    // the call free.
    expect(result.costUsd).toBeGreaterThan(0);
  });

  test('treats a usage block with drifted field names as absent', async () => {
    // isGroqUsage requires all three numeric token fields; a renamed field
    // must not be read as a partial, under-counted usage.
    handler = () => chatResponse({ usage: { input_tokens: 120, output_tokens: 40 } });

    const result = await requestCerebrasChat(PAYLOAD, 'req-1');

    expect(result.usage).toBeUndefined();
    expect(result.costUsd).toBeGreaterThan(0);
  });

  test('treats a non-numeric token count as absent', async () => {
    handler = () => chatResponse({ usage: { prompt_tokens: '120', completion_tokens: 40, total_tokens: 160 } });

    const result = await requestCerebrasChat(PAYLOAD, 'req-1');

    expect(result.usage).toBeUndefined();
    expect(result.costUsd).toBeGreaterThan(0);
  });

  test('still bills when the response has no readable completion text', async () => {
    handler = () => new Response(JSON.stringify({ id: 'chatcmpl-1' }), { status: 200 });

    const result = await requestCerebrasChat(PAYLOAD, 'req-1');

    expect(result.costUsd).toBeGreaterThan(0);
  });
});

// ===========================================================================
// Error propagation — this is what drives provider fallback
// ===========================================================================
describe('upstream error propagation', () => {
  test.each([500, 502, 503, 504])('tags an upstream %i so post-process falls back to the next provider', async (status) => {
    handler = () => new Response('upstream exploded', { status });

    const error = await captureError(() => requestCerebrasChat(PAYLOAD, 'req-1'));

    expect(errorStatus(error)).toBe(status);
    expect(errorProvider(error)).toBe('cerebras');
    expect((error as Error).message).toBe(`Cerebras chat failed with status ${status}`);
    expect(shouldFallback(error)).toBe(true);
  });

  test.each([400, 401, 403, 404, 422, 429])('keeps an upstream %i on the same provider', async (status) => {
    handler = () => new Response('client error', { status });

    const error = await captureError(() => requestCerebrasChat(PAYLOAD, 'req-1'));

    expect(errorStatus(error)).toBe(status);
    // A 429 is the provider rate-limiting us and a 4xx is our own bad request.
    // Neither is fixed by re-sending the same request to a different vendor.
    expect(shouldFallback(error)).toBe(false);
  });

  test('tags the failing provider, not the one that will take over', async () => {
    handler = () => new Response('upstream exploded', { status: 500 });

    expect(errorProvider(await captureError(() => requestGroqChat(PAYLOAD, 'req-1')))).toBe('groq');
    const grokError = await captureError(() => requestXaiGrokChat(PAYLOAD, 'req-1'));
    expect(errorProvider(grokError)).toBe('grok');
    expect((grokError as Error).message).toBe('SpaceXAI Grok chat failed with status 500');
    expect(errorProvider(await captureError(() => requestOpenAIChat(PAYLOAD, 'req-1', 'gpt-5-mini')))).toBe('openai');
    expect(errorProvider(await captureError(() => requestGeminiChat(PAYLOAD, 'req-1', 'gemini-2.5-flash')))).toBe('gemini');
    expect(errorProvider(await captureError(() => requestMistralChat(PAYLOAD, 'req-1', 'mistral-small-latest')))).toBe('mistral');
  });

  test('reports the upstream status even when its error body cannot be read', async () => {
    handler = () => ({
      ok: false,
      status: 503,
      statusText: 'Service Unavailable',
      text: async () => { throw new Error('body already consumed'); },
    }) as unknown as Response;

    const error = await captureError(() => requestCerebrasChat(PAYLOAD, 'req-1'));

    expect(errorStatus(error)).toBe(503);
    expect(shouldFallback(error)).toBe(true);
  });

  test('lets a transport failure through untagged so it is retried, not failed over', async () => {
    handler = () => { throw new TypeError('connection reset by peer'); };

    const error = await captureError(() => requestCerebrasChat(PAYLOAD, 'req-1'));

    expect((error as Error).message).toContain('connection reset by peer');
    expect(errorStatus(error)).toBeUndefined();
    expect(shouldFallback(error)).toBe(false);
  });
});

// ===========================================================================
// Missing API key
// ===========================================================================
describe('missing API key', () => {
  test('never sends a request without a key', async () => {
    for (const key of API_KEY_ENV) delete process.env[key];

    await captureError(() => requestCerebrasChat(PAYLOAD, 'req-1'));
    await captureError(() => requestGroqChat(PAYLOAD, 'req-1'));
    await captureError(() => requestOpenAIChat(PAYLOAD, 'req-1', 'gpt-5-mini'));
    await captureError(() => requestGeminiChat(PAYLOAD, 'req-1', 'gemini-2.5-flash'));
    await captureError(() => requestMistralChat(PAYLOAD, 'req-1', 'mistral-small-latest'));
    await captureError(() => requestXaiGrokChat(PAYLOAD, 'req-1'));

    expect(calls).toHaveLength(0);
  });

  test('the four late-added providers report an unconfigured key as failover-eligible', async () => {
    // A provider we have no key for is, from the route's point of view, down —
    // tagging it 503 lets post-process serve the request off the fallback
    // instead of returning the raw transcript.
    const cases: Array<[() => Promise<unknown>, string, string]> = [
      [() => requestOpenAIChat(PAYLOAD, 'req-1', 'gpt-5-mini'), 'openai', 'OPENAI_API_KEY'],
      [() => requestGeminiChat(PAYLOAD, 'req-1', 'gemini-2.5-flash'), 'gemini', 'GEMINI_API_KEY'],
      [() => requestMistralChat(PAYLOAD, 'req-1', 'mistral-small-latest'), 'mistral', 'MISTRAL_API_KEY'],
      [() => requestXaiGrokChat(PAYLOAD, 'req-1'), 'grok', 'XAI_API_KEY'],
    ];

    for (const [send, provider, envName] of cases) {
      for (const key of API_KEY_ENV) delete process.env[key];

      const error = await captureError(send);

      expect((error as Error).message).toBe(`${envName} not configured`);
      expect(errorStatus(error)).toBe(503);
      expect(errorProvider(error)).toBe(provider);
      expect(shouldFallback(error)).toBe(true);
    }
  });

  test('cerebras and groq report an unconfigured key as a plain, non-failover error', async () => {
    // Documenting current behaviour, which differs from the four above: these
    // two throw untagged, so shouldFallback() is false and the request does
    // NOT move to another provider.
    for (const key of API_KEY_ENV) delete process.env[key];

    const cerebrasError = await captureError(() => requestCerebrasChat(PAYLOAD, 'req-1'));
    expect((cerebrasError as Error).message).toBe('CEREBRAS_API_KEY not configured');
    expect(errorStatus(cerebrasError)).toBeUndefined();
    expect(shouldFallback(cerebrasError)).toBe(false);

    const groqError = await captureError(() => requestGroqChat(PAYLOAD, 'req-1'));
    expect((groqError as Error).message).toBe('GROQ_API_KEY not configured');
    expect(errorStatus(groqError)).toBeUndefined();
    expect(shouldFallback(groqError)).toBe(false);
  });

  test('treats an empty-string key as unconfigured', async () => {
    process.env.CEREBRAS_API_KEY = '';

    const error = await captureError(() => requestCerebrasChat(PAYLOAD, 'req-1'));

    // An empty key would otherwise be sent as `Bearer ` and come back 401.
    expect((error as Error).message).toBe('CEREBRAS_API_KEY not configured');
    expect(calls).toHaveLength(0);
  });
});
