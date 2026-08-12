import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { Hono } from 'hono';

// A valid, well-funded licensed user so auth + credit checks pass entirely
// in-memory (no network) and the route reaches the LLM provider chain.
let cachedLicense: { isValid: boolean; credits: number; cachedAt: string } | null = {
  isValid: true,
  credits: 1000,
  cachedAt: 'cached',
};

mock.module('../lib/redis', () => ({
  // mock.module is process-wide in bun, so an incomplete redis mock here breaks
  // any OTHER suite in the run whose module graph reaches lib/google-auth's
  // static `import { redis }` — transcribe.test.ts failed to load at all,
  // silently, for exactly this reason. Keep the export even though nothing here
  // uses it.
  redis: {},
  isIPBlocked: async () => false,
  getCachedLicense: async () => cachedLicense,
  cacheLicense: async () => {},
}));

const { postProcessRoute } = await import('./post-process');

const originalFetch = globalThis.fetch;

function usage(promptTokens = 50, completionTokens = 20) {
  return { prompt_tokens: promptTokens, completion_tokens: completionTokens, total_tokens: promptTokens + completionTokens };
}

function chatCompletion(content: string, finishReason = 'stop') {
  return {
    choices: [{ message: { content }, finish_reason: finishReason }],
    usage: usage(),
  };
}

function buildApp(): Hono {
  const app = new Hono();
  app.post('/post-process', postProcessRoute);
  return app;
}

function postProcessRequest(body: Record<string, unknown>, headers: Record<string, string> = {}): Request {
  return new Request('http://localhost/post-process', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...headers },
    body: JSON.stringify(body),
  });
}

// Default fetch mock: any request to the license credits-recording endpoint
// (fired-and-forgotten by deductCredits) succeeds quietly so it doesn't spam
// "Unexpected fetch" noise across tests that don't care about billing.
function withProviders(handlers: Record<string, () => Response | Promise<Response>>) {
  globalThis.fetch = mock(async (input: RequestInfo | URL) => {
    const url = String(input);

    for (const [match, handler] of Object.entries(handlers)) {
      if (url.includes(match)) {
        return handler();
      }
    }

    if (url.includes('/api/license/credits')) {
      return Response.json({ credits_remaining: 999 });
    }

    throw new Error(`Unexpected fetch: ${url}`);
  }) as unknown as typeof fetch;
}

describe('postProcessRoute validation', () => {
  beforeEach(() => {
    cachedLicense = { isValid: true, credits: 1000, cachedAt: 'cached' };
    process.env.CEREBRAS_API_KEY = 'test-cerebras-key';
    process.env.GROQ_API_KEY = 'test-groq-key';
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  test('rejects a non-JSON Content-Type with 400', async () => {
    const response = await buildApp().fetch(
      new Request('http://localhost/post-process', {
        method: 'POST',
        headers: { 'Content-Type': 'text/plain' },
        body: 'hello',
      })
    );

    expect(response.status).toBe(400);
  });

  test('rejects a missing "text" field with 400', async () => {
    const response = await buildApp().fetch(postProcessRequest({ prompt: 'fix grammar', account_key: 'lk' }));
    const body = await response.json() as { error: string };

    expect(response.status).toBe(400);
    expect(body.error).toBe('Missing field');
  });

  test('rejects text over the max length with 400 before any auth/LLM work', async () => {
    const response = await buildApp().fetch(
      postProcessRequest({ text: 'a'.repeat(100001), prompt: 'fix grammar', account_key: 'lk' })
    );
    const body = await response.json() as { error: string; max_length: number; actual_length: number };

    expect(response.status).toBe(400);
    expect(body.error).toBe('Text too long');
    expect(body.max_length).toBe(100000);
    expect(body.actual_length).toBe(100001);
  });

  test('rejects a missing "prompt" field with 400', async () => {
    const response = await buildApp().fetch(postProcessRequest({ text: 'hello wrld', account_key: 'lk' }));
    const body = await response.json() as { error: string };

    expect(response.status).toBe(400);
    expect(body.error).toBe('Missing field');
  });

  test('rejects a request with no license key with 401 before touching any LLM provider', async () => {
    withProviders({});

    const response = await buildApp().fetch(postProcessRequest({ text: 'hello wrld', prompt: 'fix grammar' }));

    expect(response.status).toBe(401);
  });

  test('rejects a request from an under-funded account with 402 before touching any LLM provider', async () => {
    cachedLicense = { isValid: true, credits: 0.1, cachedAt: 'cached' };
    withProviders({});

    const response = await buildApp().fetch(
      postProcessRequest({ text: 'hello wrld', prompt: 'fix grammar', account_key: 'lk' })
    );

    expect(response.status).toBe(402);
  });

  test('accepts the legacy license_key field as an alias for account_key', async () => {
    withProviders({
      'api.cerebras.ai': () => Response.json(chatCompletion('hello world')),
    });

    const response = await buildApp().fetch(
      postProcessRequest({ text: 'hello wrld', prompt: 'fix grammar', license_key: 'lk' })
    );

    expect(response.status).toBe(200);
  });
});

describe('postProcessRoute LLM provider chain', () => {
  beforeEach(() => {
    cachedLicense = { isValid: true, credits: 1000, cachedAt: 'cached' };
    process.env.CEREBRAS_API_KEY = 'test-cerebras-key';
    process.env.GROQ_API_KEY = 'test-groq-key';
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  test('returns the corrected text and billing headers from the default (cerebras) provider', async () => {
    withProviders({
      'api.cerebras.ai': () => Response.json(chatCompletion('Hello world.')),
    });

    const response = await buildApp().fetch(
      postProcessRequest({ text: 'hello wrld', prompt: 'fix grammar', account_key: 'lk' })
    );
    const body = await response.json() as { corrected: string; cost: { usd: number; credits: number } };

    expect(response.status).toBe(200);
    expect(body.corrected).toBe('Hello world.');
    expect(response.headers.get('X-LLM-Provider')).toBe('cerebras-gpt-oss-120b');
    expect(Number(response.headers.get('X-Credits-Used'))).toBeGreaterThan(0);
    expect(body.cost.credits).toBeGreaterThan(0);
  });

  test('falls back to groq when cerebras fails with a 5xx', async () => {
    let groqCalled = false;
    withProviders({
      'api.cerebras.ai': () => new Response('server error', { status: 500 }),
      'api.groq.com': () => {
        groqCalled = true;
        return Response.json(chatCompletion('Hello from groq.'));
      },
    });

    const response = await buildApp().fetch(
      postProcessRequest({ text: 'hello wrld', prompt: 'fix grammar', account_key: 'lk' })
    );
    const body = await response.json() as { corrected: string };

    expect(groqCalled).toBe(true);
    expect(response.status).toBe(200);
    expect(body.corrected).toBe('Hello from groq.');
    expect(response.headers.get('X-LLM-Provider')).toBe('groq-gpt-oss-120b');
  });

  test('returns 500 without falling back when the primary provider fails with a non-5xx error', async () => {
    let groqCalled = false;
    withProviders({
      'api.cerebras.ai': () => new Response('bad request', { status: 400 }),
      'api.groq.com': () => {
        groqCalled = true;
        return Response.json(chatCompletion('should not be reached'));
      },
    });

    const response = await buildApp().fetch(
      postProcessRequest({ text: 'hello wrld', prompt: 'fix grammar', account_key: 'lk' })
    );

    expect(response.status).toBe(500);
    expect(groqCalled).toBe(false);
  });

  test('returns 500 when both the primary and fallback providers fail', async () => {
    withProviders({
      'api.cerebras.ai': () => new Response('server error', { status: 500 }),
      'api.groq.com': () => new Response('server error', { status: 500 }),
    });

    const response = await buildApp().fetch(
      postProcessRequest({ text: 'hello wrld', prompt: 'fix grammar', account_key: 'lk' })
    );
    const body = await response.json() as { error: string };

    expect(response.status).toBe(500);
    expect(body.error).toBe('Post-processing failed');
    // The groq fallback retries 3x with real exponential backoff (1s+2s+4s)
    // before giving up, well past bun's default 5s per-test timeout.
  }, 10000);
});

describe('postProcessRoute completion evaluation', () => {
  beforeEach(() => {
    cachedLicense = { isValid: true, credits: 1000, cachedAt: 'cached' };
    process.env.CEREBRAS_API_KEY = 'test-cerebras-key';
    process.env.GROQ_API_KEY = 'test-groq-key';
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  test('keeps the raw transcript (no alternate-provider retry) when the response is truncated', async () => {
    let groqCalled = false;
    withProviders({
      'api.cerebras.ai': () => Response.json(chatCompletion('cut off mid-sen', 'length')),
      'api.groq.com': () => {
        groqCalled = true;
        return Response.json(chatCompletion('should not be reached'));
      },
    });

    const response = await buildApp().fetch(
      postProcessRequest({ text: 'the original transcript', prompt: 'fix grammar', account_key: 'lk' })
    );
    const body = await response.json() as { corrected: string };

    expect(response.status).toBe(200);
    expect(body.corrected).toBe('the original transcript');
    expect(groqCalled).toBe(false);
  });

  test('retries an alternate provider on prompt leakage and returns its clean text', async () => {
    withProviders({
      'api.cerebras.ai': () => Response.json(chatCompletion('--TRANSCRIPT--\nleaked prompt\n--ENDTRANSCRIPT--')),
      'api.groq.com': () => Response.json(chatCompletion('Clean corrected text.')),
    });

    const response = await buildApp().fetch(
      postProcessRequest({ text: 'the original transcript', prompt: 'fix grammar', account_key: 'lk' })
    );
    const body = await response.json() as { corrected: string };

    expect(response.status).toBe(200);
    expect(body.corrected).toBe('Clean corrected text.');
    expect(response.headers.get('X-LLM-Provider')).toBe('groq-gpt-oss-120b');
  });

  test('falls back to the raw transcript when prompt leakage persists on the alternate provider too', async () => {
    withProviders({
      'api.cerebras.ai': () => Response.json(chatCompletion('--TRANSCRIPT--\nleaked once\n--ENDTRANSCRIPT--')),
      'api.groq.com': () => Response.json(chatCompletion('--TRANSCRIPT--\nleaked again\n--ENDTRANSCRIPT--')),
    });

    const response = await buildApp().fetch(
      postProcessRequest({ text: 'the original transcript', prompt: 'fix grammar', account_key: 'lk' })
    );
    const body = await response.json() as { corrected: string };

    expect(response.status).toBe(200);
    expect(body.corrected).toBe('the original transcript');
  });
});
