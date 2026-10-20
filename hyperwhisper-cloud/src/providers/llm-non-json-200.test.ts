// #2065: a 200 from the primary LLM whose body is not JSON (a CDN/proxy HTML
// page, a truncated body) escaped openai-compat-chat.ts / anthropic.ts
// `response.json()` as a raw SyntaxError. shouldFallback() only accepts an
// LLMRequestError, so /post-process answered 500 with no failover, while a
// 5xx/429 from the same vendor failed over. readLLMJson (llm-fetch.ts) now maps
// it to an LLMRequestError 502: retried per the provider's budget, then failed
// over like any other 5xx.

import { afterEach, beforeEach, describe, expect, mock, spyOn, test } from 'bun:test';
import { Hono } from 'hono';
import { LLMRequestError } from './llm-errors';
import { readLLMJson } from './llm-fetch';

// mock.module is process-wide in bun: keep every export post-process.test.ts
// keeps, so no other suite in the run loses one (see the note there).
mock.module('../lib/redis', () => ({
  redis: {},
  isIPBlocked: async () => false,
  getCachedLicense: async () => ({ isValid: true, credits: 1000, cachedAt: 'cached' }),
  cacheLicense: async () => {},
}));

const { postProcessRoute } = await import('../routes/post-process');
const originalFetch = globalThis.fetch;

function app() {
  const a = new Hono();
  a.post('/post-process', postProcessRoute);
  return a;
}

function req(headers: Record<string, string> = {}) {
  const raw = JSON.stringify({ text: 'hello wrld', prompt: 'fix grammar', account_key: 'llm-non-json-200-lk' });
  return new Request('http://localhost/post-process', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'Content-Length': String(Buffer.byteLength(raw)), ...headers },
    body: raw,
  });
}

const okCompletion = (content: string) => ({
  choices: [{ message: { content }, finish_reason: 'stop' }],
  usage: { prompt_tokens: 50, completion_tokens: 20, total_tokens: 70 },
});

const HTML_PAGE = '<html><body>Upstream temporarily unavailable</body></html>';
const htmlPage = () => new Response(HTML_PAGE, { status: 200, headers: { 'content-type': 'text/html' } });

let calls: string[] = [];
function providers(handlers: Record<string, () => Response>) {
  globalThis.fetch = mock(async (input: RequestInfo | URL) => {
    const url = String(input);
    for (const [m, h] of Object.entries(handlers)) if (url.includes(m)) { calls.push(m); return h(); }
    if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
    throw new Error(`Unexpected fetch: ${url}`);
  }) as unknown as typeof fetch;
}

describe('non-JSON 200 from the primary LLM provider', () => {
  beforeEach(() => {
    calls = [];
    process.env.CEREBRAS_API_KEY = 'dummy-cerebras';
    process.env.GROQ_API_KEY = 'dummy-groq';
    process.env.ANTHROPIC_API_KEY = 'dummy-anthropic';
  });
  afterEach(() => { globalThis.fetch = originalFetch; });

  test('cerebras (default) HTML 200 fails over to groq like a 5xx does', async () => {
    providers({
      'api.cerebras.ai': htmlPage,
      'api.groq.com': () => Response.json(okCompletion('Hello from groq.')),
    });
    const res = await app().fetch(req());
    const body = await res.json() as { corrected: string };
    expect(calls).toEqual(['api.cerebras.ai', 'api.groq.com']);
    expect(res.status).toBe(200);
    expect(body.corrected).toBe('Hello from groq.');
    expect(res.headers.get('X-LLM-Provider')).toBe('groq-gpt-oss-120b');
  });

  test('anthropic HTML 200 is retried per its budget, then fails over to cerebras like a 5xx does', async () => {
    providers({
      'api.anthropic.com': htmlPage,
      'api.cerebras.ai': () => Response.json(okCompletion('Hello from cerebras.')),
    });
    const res = await app().fetch(req({ 'X-LLM-Provider': 'anthropic' }));
    const body = await res.json() as { corrected: string };
    // anthropic keeps its 2 same-provider retries (1 + 2), exactly as for a 502 status.
    expect(calls).toEqual(['api.anthropic.com', 'api.anthropic.com', 'api.anthropic.com', 'api.cerebras.ai']);
    expect(res.status).toBe(200);
    expect(body.corrected).toBe('Hello from cerebras.');
  }, 20_000);

  test('the 500 when the fallback also answers HTML carries no body text', async () => {
    providers({ 'api.cerebras.ai': htmlPage, 'api.groq.com': htmlPage });
    const res = await app().fetch(req());
    const body = await res.text();
    expect(calls[0]).toBe('api.cerebras.ai');
    expect(calls).toContain('api.groq.com');
    expect(res.status).toBe(500);
    expect(body).not.toContain('Upstream temporarily unavailable');
  }, 20_000);
});

describe('readLLMJson', () => {
  test('parses a JSON body', async () => {
    await expect(readLLMJson(Response.json({ a: 1 }), 'cerebras', 'req-1')).resolves.toEqual({ a: 1 });
  });

  test.each([
    ['an HTML page', HTML_PAGE],
    ['a truncated body', '{"choices":[{"message":'],
    ['an empty body', ''],
  ])('maps %s to an LLMRequestError 502 that names no body text', async (_label, raw) => {
    const errorSpy = spyOn(console, 'error').mockImplementation(() => {});
    try {
      const error = await readLLMJson(new Response(raw, { status: 200 }), 'cerebras', 'req-1').catch((e: unknown) => e) as LLMRequestError;
      expect(error).toBeInstanceOf(LLMRequestError);
      expect(error.status).toBe(502);
      expect(error.provider).toBe('cerebras');
      expect(error.message).toBe('cerebras returned a non-JSON 200 response');
      expect(JSON.stringify(errorSpy.mock.calls)).not.toContain('Upstream temporarily unavailable');
    } finally {
      errorSpy.mockRestore();
    }
  });
});
