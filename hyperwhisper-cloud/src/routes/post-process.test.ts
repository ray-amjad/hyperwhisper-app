import { afterEach, beforeEach, describe, expect, mock, spyOn, test } from 'bun:test';
import { Hono } from 'hono';
import { computeCerebrasChatCost, computeGroqChatCost, creditsForCost } from '../lib/cost-calculator';

// A valid, well-funded licensed user so auth + credit checks pass entirely
// in-memory (no network) and the route reaches the LLM provider chain.
let cachedLicense: { isValid: boolean; credits: number; cachedAt: string } | null = {
  isValid: true,
  credits: 1000,
  cachedAt: 'cached',
};

// The abuse gate. Default off so every existing suite reaches the LLM chain;
// the one suite that exercises the block flips it and resets it afterwards.
let ipBlocked = false;
const cacheReads: string[] = [];

mock.module('../lib/redis', () => ({
  // mock.module is process-wide in bun, so an incomplete redis mock here breaks
  // any OTHER suite in the run whose module graph reaches lib/google-auth's
  // static `import { redis }` — transcribe.test.ts failed to load at all,
  // silently, for exactly this reason. Keep the export even though nothing here
  // uses it.
  redis: {},
  isIPBlocked: async () => ipBlocked,
  getCachedLicense: async (licenseKey: string) => {
    cacheReads.push(licenseKey);
    return cachedLicense;
  },
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

// A `new Request` built in-process carries no Content-Length, but every real
// client's JSON POST does, and the route requires it (the pre-parse size gate).
function jsonRequest(raw: string, headers: Record<string, string> = {}): Request {
  return new Request('http://localhost/post-process', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'Content-Length': String(Buffer.byteLength(raw)),
      ...headers,
    },
    body: raw,
  });
}

function postProcessRequest(body: Record<string, unknown>, headers: Record<string, string> = {}): Request {
  return jsonRequest(JSON.stringify(body), headers);
}

// Default fetch mock: any request to the license credits-recording endpoint
// (fired-and-forgotten by deductCredits) succeeds quietly so it doesn't spam
// "Unexpected fetch" noise across tests that don't care about billing.
// A handler may take the outgoing RequestInit when it needs to assert on the
// body we sent (the billing suite reads the credit-deduction payload).
function withProviders(handlers: Record<string, (init: RequestInit) => Response | Promise<Response>>) {
  globalThis.fetch = mock(async (input: RequestInfo | URL, init: RequestInit = {}) => {
    const url = String(input);

    for (const [match, handler] of Object.entries(handlers)) {
      if (url.includes(match)) {
        return handler(init);
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
    cacheReads.length = 0;
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

  test('account_key wins over the legacy license_key when both are sent', async () => {
    let cerebrasCalls = 0;
    withProviders({
      'api.cerebras.ai': () => {
        cerebrasCalls += 1;
        return Response.json(chatCompletion('hello world'));
      },
    });

    const response = await buildApp().fetch(
      postProcessRequest({
        text: 'hello wrld',
        prompt: 'fix grammar',
        account_key: 'canonical-credential',
        license_key: 'legacy-credential',
      })
    );

    expect(response.status).toBe(200);
    expect(cerebrasCalls).toBe(1);
    expect(cacheReads).toEqual(['canonical-credential']);
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

  // #1565: a Cerebras 429 ended the request with a 500 (llm_failed_no_fallback)
  // because shouldFallback() took a 5xx only. Groq has its own rate limit, so
  // the fallback does fix a Cerebras 429.
  test('falls back to groq when cerebras rate-limits with a 429', async () => {
    let cerebrasCalls = 0;
    let groqCalls = 0;
    withProviders({
      'api.cerebras.ai': () => {
        cerebrasCalls += 1;
        return new Response('rate limited', { status: 429 });
      },
      'api.groq.com': () => {
        groqCalls += 1;
        return Response.json(chatCompletion('Hello from groq.'));
      },
    });
    const logSpy = spyOn(console, 'log').mockImplementation(() => {});

    try {
      const response = await buildApp().fetch(
        postProcessRequest({ text: 'hello wrld', prompt: 'fix grammar', account_key: 'lk' })
      );
      const body = await response.json() as { corrected: string };

      expect(response.status).toBe(200);
      expect(body.corrected).toBe('Hello from groq.');
      expect(response.headers.get('X-LLM-Provider')).toBe('groq-gpt-oss-120b');
      // cerebras keeps 0 same-provider retries: one 429, then straight to groq.
      expect(cerebrasCalls).toBe(1);
      expect(groqCalls).toBe(1);

      const events = logSpy.mock.calls
        .map((call) => String(call[0]))
        .filter((raw) => raw.startsWith('{') && raw.includes('"event":"post_process.'))
        .map((raw) => JSON.parse(raw) as Record<string, unknown>);
      const fallbackStart = events.filter((line) => line.event === 'post_process.llm_fallback_start');
      expect(fallbackStart).toHaveLength(1);
      expect(fallbackStart[0]).toMatchObject({ requestedProvider: 'cerebras', provider: 'groq' });
      expect(events.some((line) => line.reason === 'llm_failed_no_fallback')).toBe(false);
    } finally {
      logSpy.mockRestore();
    }
  });

  // #1568: groq keeps 3 same-provider retries, but a 429 from the requested
  // provider skips them (1 s + 2 s + 4 s of waiting on a vendor that just said
  // no) and goes straight to the fallback.
  test('a groq 429 falls over to cerebras at once, with exactly 1 groq call', async () => {
    let groqCalls = 0;
    let cerebrasCalls = 0;
    withProviders({
      'api.groq.com': () => {
        groqCalls += 1;
        return new Response('rate limited', { status: 429 });
      },
      'api.cerebras.ai': () => {
        cerebrasCalls += 1;
        return Response.json(chatCompletion('Hello from cerebras.'));
      },
    });

    const response = await buildApp().fetch(
      postProcessRequest(
        { text: 'hello wrld', prompt: 'fix grammar', account_key: 'lk' },
        { 'X-LLM-Provider': 'groq' }
      )
    );
    const body = await response.json() as { corrected: string };

    expect(response.status).toBe(200);
    expect(body.corrected).toBe('Hello from cerebras.');
    expect(response.headers.get('X-LLM-Provider')).toBe('cerebras-gpt-oss-120b');
    expect(groqCalls).toBe(1);
    expect(cerebrasCalls).toBe(1);
  });

  // #1568 decision 2: the fallback has no provider after it, so a 429 from the
  // fallback keeps its same-provider retries (groq: 1 attempt + 3 retries).
  test('a 429 from the fallback provider keeps its same-provider retries', async () => {
    let cerebrasCalls = 0;
    let groqCalls = 0;
    withProviders({
      'api.cerebras.ai': () => {
        cerebrasCalls += 1;
        return new Response('rate limited', { status: 429 });
      },
      'api.groq.com': () => {
        groqCalls += 1;
        return new Response('rate limited', { status: 429 });
      },
    });

    const response = await buildApp().fetch(
      postProcessRequest({ text: 'hello wrld', prompt: 'fix grammar', account_key: 'lk' })
    );

    expect(response.status).toBe(500);
    expect(cerebrasCalls).toBe(1);
    expect(groqCalls).toBe(4);
    // Real exponential backoff on the groq fallback (1 s + 2 s + 4 s).
  }, 15000);

  test('returns 500 without falling back when the primary provider fails with a 4xx other than 429', async () => {
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

// ---------------------------------------------------------------------------
// What we charge for a request whose output we cannot use.
//
// The LLM call is already paid for by the time the route decides the text is
// unusable, so "the user got their raw transcript back" must not silently mean
// "the call was free". These tests pin the amount, not just the status code:
// each expected figure is recomputed from the same cost table the route bills
// through, so a rate change moves both sides and a dropped charge moves only
// one.
// ---------------------------------------------------------------------------
describe('postProcessRoute billing when the output is unusable', () => {
  const LEAKED = '--TRANSCRIPT--\nleaked once\n--ENDTRANSCRIPT--';
  const TRANSCRIPT = 'the original transcript';

  function completion(content: string, promptTokens: number, completionTokens: number, finishReason = 'stop') {
    return {
      choices: [{ message: { content }, finish_reason: finishReason }],
      usage: usage(promptTokens, completionTokens),
    };
  }

  beforeEach(() => {
    cachedLicense = { isValid: true, credits: 1000, cachedAt: 'cached' };
    process.env.CEREBRAS_API_KEY = 'test-cerebras-key';
    process.env.GROQ_API_KEY = 'test-groq-key';
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  test('bills the leakage retry as well as the primary when the retry comes back truncated', async () => {
    // The retry is billed the moment it succeeds, before the route evaluates
    // it — so a retry that is charged by the provider and then discarded here
    // still reaches the invoice.
    withProviders({
      'api.cerebras.ai': () => Response.json(completion(LEAKED, 50, 20)),
      'api.groq.com': () => Response.json(completion('cut off mid-sen', 400, 300, 'length')),
    });

    const response = await buildApp().fetch(
      postProcessRequest({ text: TRANSCRIPT, prompt: 'fix grammar', account_key: 'lk' })
    );
    const body = await response.json() as { corrected: string; cost: { usd: number; credits: number } };

    const expectedUsd = computeCerebrasChatCost(usage(50, 20)) + computeGroqChatCost(usage(400, 300));

    expect(response.status).toBe(200);
    expect(body.corrected).toBe(TRANSCRIPT);
    expect(body.cost.usd).toBeCloseTo(expectedUsd, 9);
    expect(body.cost.credits).toBe(creditsForCost(expectedUsd));
    // providerUsed moves to the retry even though its text was discarded.
    expect(response.headers.get('X-LLM-Provider')).toBe('groq-gpt-oss-120b');
  });

  test('bills only the primary when the leakage retry itself never succeeds', async () => {
    let groqAttempts = 0;
    withProviders({
      'api.cerebras.ai': () => Response.json(completion(LEAKED, 50, 20)),
      'api.groq.com': () => {
        groqAttempts += 1;
        return new Response('server error', { status: 500 });
      },
    });

    const response = await buildApp().fetch(
      postProcessRequest({ text: TRANSCRIPT, prompt: 'fix grammar', account_key: 'lk' })
    );
    const body = await response.json() as { corrected: string; cost: { usd: number } };

    expect(response.status).toBe(200);
    expect(body.corrected).toBe(TRANSCRIPT);
    expect(body.cost.usd).toBeCloseTo(computeCerebrasChatCost(usage(50, 20)), 9);
    // groq's retry budget is 3, so a failing retry is 1 attempt + 3 retries.
    expect(groqAttempts).toBe(4);
    // Nothing was served by groq, so the header must still name cerebras.
    expect(response.headers.get('X-LLM-Provider')).toBe('cerebras-gpt-oss-120b');
  }, 15000);

  test('still charges the caller when the response carries no extractable text, then 500s', async () => {
    let deduction: { license_key: string; amount: number; metadata: Record<string, unknown> } | null = null;
    let onDeduction: () => void = () => {};
    const deducted = new Promise<void>((resolve) => { onDeduction = resolve; });

    withProviders({
      // A well-formed, complete, already-paid-for response with no text field
      // anywhere in it — extraction throws rather than returning empty.
      'api.cerebras.ai': () => Response.json({
        choices: [{ message: {}, finish_reason: 'stop' }],
        usage: usage(120, 90),
      }),
      '/api/license/credits': (init: RequestInit) => {
        deduction = JSON.parse(String(init.body)) as typeof deduction;
        onDeduction();
        return Response.json({ credits_remaining: 900 });
      },
    });

    const response = await buildApp().fetch(
      postProcessRequest({ text: TRANSCRIPT, prompt: 'fix grammar', account_key: 'lk' })
    );
    const body = await response.json() as { error: string };

    expect(response.status).toBe(500);
    expect(body.error).toBe('Post-processing failed');

    // deductCredits is fired without being awaited, so wait for the write.
    await deducted;
    expect(deduction).not.toBeNull();
    expect(deduction!.amount).toBe(creditsForCost(computeCerebrasChatCost(usage(120, 90))));
    expect(deduction!.metadata.endpoint).toBe('/post-process');
    expect(deduction!.metadata.llm_provider).toBe('cerebras');
    expect(deduction!.metadata.input_length).toBe(TRANSCRIPT.length);
    // Nothing usable was returned, so the output side of the record is zero.
    expect(deduction!.metadata.output_length).toBe(0);
  });
});

// ---------------------------------------------------------------------------
// The per-provider retry budget bounds latency and spend before the route
// gives up on a provider. It is only observable as a call count, so that is
// what these assert.
// ---------------------------------------------------------------------------
describe('postProcessRoute per-provider retry budget', () => {
  const originalOpenAIKey = process.env.OPENAI_API_KEY;
  const originalAnthropicKey = process.env.ANTHROPIC_API_KEY;

  beforeEach(() => {
    cachedLicense = { isValid: true, credits: 1000, cachedAt: 'cached' };
    process.env.CEREBRAS_API_KEY = 'test-cerebras-key';
    process.env.GROQ_API_KEY = 'test-groq-key';
    process.env.OPENAI_API_KEY = 'test-openai-key';
    process.env.ANTHROPIC_API_KEY = 'test-anthropic-key';
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    // These two keys are not set by the other suites in this file — leaving
    // them behind would change what an unrelated suite sees.
    if (originalOpenAIKey === undefined) delete process.env.OPENAI_API_KEY;
    else process.env.OPENAI_API_KEY = originalOpenAIKey;
    if (originalAnthropicKey === undefined) delete process.env.ANTHROPIC_API_KEY;
    else process.env.ANTHROPIC_API_KEY = originalAnthropicKey;
  });

  test('cerebras gets no retry — one attempt, then straight to the fallback', async () => {
    let cerebrasAttempts = 0;
    let groqAttempts = 0;
    withProviders({
      'api.cerebras.ai': () => {
        cerebrasAttempts += 1;
        return new Response('server error', { status: 500 });
      },
      'api.groq.com': () => {
        groqAttempts += 1;
        return Response.json(chatCompletion('Hello from groq.'));
      },
    });

    const response = await buildApp().fetch(
      postProcessRequest({ text: 'hello wrld', prompt: 'fix grammar', account_key: 'lk' })
    );

    expect(response.status).toBe(200);
    expect(cerebrasAttempts).toBe(1);
    expect(groqAttempts).toBe(1);
  });

  test('openai retries once and its anthropic fallback twice before the request fails', async () => {
    let openaiAttempts = 0;
    let anthropicAttempts = 0;
    withProviders({
      'api.openai.com': () => {
        openaiAttempts += 1;
        return new Response('server error', { status: 500 });
      },
      'api.anthropic.com': () => {
        anthropicAttempts += 1;
        return new Response('server error', { status: 500 });
      },
    });

    const response = await buildApp().fetch(
      postProcessRequest(
        { text: 'hello wrld', prompt: 'fix grammar', account_key: 'lk' },
        { 'X-LLM-Provider': 'openai' }
      )
    );

    expect(response.status).toBe(500);
    expect(openaiAttempts).toBe(2);      // 1 attempt + 1 retry
    expect(anthropicAttempts).toBe(3);   // 1 attempt + 2 retries
  }, 15000);
});

// ---------------------------------------------------------------------------
// Gates that must fire before the route spends anything.
// ---------------------------------------------------------------------------
describe('postProcessRoute request gates', () => {
  beforeEach(() => {
    cachedLicense = { isValid: true, credits: 1000, cachedAt: 'cached' };
    ipBlocked = false;
    process.env.CEREBRAS_API_KEY = 'test-cerebras-key';
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    ipBlocked = false;
  });

  test('rejects a blocked IP with 403 before reading the body or calling a provider', async () => {
    ipBlocked = true;
    let providerCalled = false;
    withProviders({
      'api.cerebras.ai': () => {
        providerCalled = true;
        return Response.json(chatCompletion('should not be reached'));
      },
    });

    const response = await buildApp().fetch(
      postProcessRequest({ text: 'hello wrld', prompt: 'fix grammar', account_key: 'lk' })
    );
    const body = await response.json() as { error: string };

    expect(response.status).toBe(403);
    expect(body.error).toBe('Access denied');
    expect(providerCalled).toBe(false);
  });

  test('rejects a malformed JSON body with 400 rather than a 500', async () => {
    let providerCalled = false;
    withProviders({
      'api.cerebras.ai': () => {
        providerCalled = true;
        return Response.json(chatCompletion('should not be reached'));
      },
    });

    const response = await buildApp().fetch(jsonRequest('{"text": "hello wrld", "prompt":'));
    const body = await response.json() as { error: string };

    expect(response.status).toBe(400);
    expect(body.error).toBe('Invalid JSON');
    expect(providerCalled).toBe(false);
  });

  // #2085: `null` parses, and reading `.text` off it threw past the route into a 500.
  test('rejects a JSON body that is not an object (null, number, string, array) with 400', async () => {
    let providerCalled = false;
    withProviders({
      'api.cerebras.ai': () => {
        providerCalled = true;
        return Response.json(chatCompletion('should not be reached'));
      },
    });

    for (const raw of ['null', '42', '"hello wrld"', '[]', '[{"text":"hello wrld","prompt":"fix grammar"}]']) {
      const response = await buildApp().fetch(jsonRequest(raw));
      const body = await response.json() as { error: string };
      expect({ raw, status: response.status, error: body.error }).toEqual({ raw, status: 400, error: 'Invalid JSON' });
    }
    expect(providerCalled).toBe(false);
  });
});

describe('postProcessRoute size gate', () => {
  beforeEach(() => {
    cachedLicense = { isValid: true, credits: 1000, cachedAt: 'cached' };
    ipBlocked = false;
    cacheReads.length = 0;
    process.env.CEREBRAS_API_KEY = 'test-cerebras-key';
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  function trackProvider(): { called: boolean } {
    const state = { called: false };
    withProviders({
      'api.cerebras.ai': () => {
        state.called = true;
        return Response.json(chatCompletion('Hello world.'));
      },
    });
    return state;
  }

  test('a Content-Length over 2 MB gets 413 before the body is read or auth runs', async () => {
    const provider = trackProvider();
    const request = postProcessRequest(
      { text: 'hello wrld', prompt: 'fix grammar', account_key: 'lk-oversized' },
      { 'Content-Length': '3000000' },
    );

    const response = await buildApp().fetch(request);
    const body = await response.json() as { error: string; max_size_bytes: number; content_length: number };

    expect(response.status).toBe(413);
    expect(body.error).toBe('Request too large');
    expect(body.max_size_bytes).toBe(2 * 1024 * 1024);
    expect(body.content_length).toBe(3_000_000);
    expect(request.bodyUsed).toBe(false);
    expect(cacheReads).toEqual([]);
    expect(provider.called).toBe(false);
  });

  test('a request with no Content-Length gets 400 and never reaches auth', async () => {
    const provider = trackProvider();
    const response = await buildApp().fetch(new Request('http://localhost/post-process', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ text: 'hello wrld', prompt: 'fix grammar', account_key: 'lk-noheader' }),
    }));
    const body = await response.json() as { error: string };

    expect(response.status).toBe(400);
    expect(body.error).toBe('Missing Content-Length');
    expect(cacheReads).toEqual([]);
    expect(provider.called).toBe(false);
  });

  test('a non-numeric Content-Length gets 400', async () => {
    const response = await buildApp().fetch(postProcessRequest(
      { text: 'hello wrld', prompt: 'fix grammar', account_key: 'lk-bad' },
      { 'Content-Length': 'abc' },
    ));
    const body = await response.json() as { error: string };

    expect(response.status).toBe(400);
    expect(body.error).toBe('Invalid Content-Length');
    expect(cacheReads).toEqual([]);
  });

  test('a normal 1 KB body still reaches auth and the provider', async () => {
    const provider = trackProvider();
    const text = 'hello wrld '.repeat(90);
    const response = await buildApp().fetch(
      postProcessRequest({ text, prompt: 'fix grammar', account_key: 'lk-normal' })
    );

    expect(response.status).toBe(200);
    expect(cacheReads).toEqual(['lk-normal']);
    expect(provider.called).toBe(true);
  });

  test('a real body of exactly 2 MB passes the gate and is parsed', async () => {
    const provider = trackProvider();
    const limit = 2 * 1024 * 1024;
    const base = { text: 'hello wrld', prompt: 'fix grammar', account_key: 'lk-edge', pad: '' };
    const raw = JSON.stringify({ ...base, pad: 'a'.repeat(limit - Buffer.byteLength(JSON.stringify(base))) });
    expect(Buffer.byteLength(raw)).toBe(limit);

    const response = await buildApp().fetch(jsonRequest(raw));

    expect(response.status).toBe(200);
    expect(cacheReads).toEqual(['lk-edge']);
    expect(provider.called).toBe(true);
  });

  test('one byte over 2 MB gets 413', async () => {
    const response = await buildApp().fetch(postProcessRequest(
      { text: 'hello wrld', prompt: 'fix grammar', account_key: 'lk-over' },
      { 'Content-Length': String(2 * 1024 * 1024 + 1) },
    ));

    expect(response.status).toBe(413);
    expect(cacheReads).toEqual([]);
  });

});

// A 500's `message` is a fixed string (#2066). The failure's own text can hold
// an upstream provider's error body or the name of a missing server env var;
// that belongs in the logs, never in the reply.
describe('postProcessRoute 500 replies do not echo the failure text', () => {
  const GENERIC = 'The language model request failed. Please try again.';
  const originalAnthropicKey = process.env.ANTHROPIC_API_KEY;
  const UPSTREAM_BODY = '{"type":"error","error":{"type":"invalid_request_error","message":"UPSTREAM-INTERNAL-DETAIL org_abc123"}}';

  beforeEach(() => {
    cachedLicense = { isValid: true, credits: 1000, cachedAt: 'cached' };
    process.env.CEREBRAS_API_KEY = 'test-cerebras-key';
    process.env.GROQ_API_KEY = 'test-groq-key';
    process.env.ANTHROPIC_API_KEY = 'test-anthropic-key';
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    if (originalAnthropicKey === undefined) delete process.env.ANTHROPIC_API_KEY;
    else process.env.ANTHROPIC_API_KEY = originalAnthropicKey;
  });

  async function readFailure(response: Response) {
    const body = await response.json() as { error: string; message: string; requestId?: string };
    expect(response.status).toBe(500);
    expect(body.error).toBe('Post-processing failed');
    expect(body.message).toBe(GENERIC);
    expect(body.requestId).toBeString();
    return body;
  }

  test('an upstream error body from a provider with no fallback is not echoed', async () => {
    withProviders({ 'api.anthropic.com': () => new Response(UPSTREAM_BODY, { status: 400 }) });

    const body = await readFailure(await buildApp().fetch(
      postProcessRequest({ text: 'hello wrld', prompt: 'fix grammar', account_key: 'lk' }, { 'X-LLM-Provider': 'anthropic' })
    ));

    expect(JSON.stringify(body)).not.toContain('UPSTREAM-INTERNAL-DETAIL');
  });

  test('a missing provider key is not named to the client', async () => {
    delete process.env.CEREBRAS_API_KEY;
    withProviders({});

    const body = await readFailure(await buildApp().fetch(
      postProcessRequest({ text: 'hello wrld', prompt: 'fix grammar', account_key: 'lk' })
    ));

    expect(JSON.stringify(body)).not.toContain('CEREBRAS_API_KEY');
  });

  test('the fallback provider\'s error body is not echoed when both providers fail', async () => {
    withProviders({
      'api.cerebras.ai': () => new Response('server error', { status: 500 }),
      'api.groq.com': () => new Response(UPSTREAM_BODY, { status: 500 }),
    });

    const body = await readFailure(await buildApp().fetch(
      postProcessRequest({ text: 'hello wrld', prompt: 'fix grammar', account_key: 'lk' })
    ));

    expect(JSON.stringify(body)).not.toContain('UPSTREAM-INTERNAL-DETAIL');
  }, 15000);

  test('a reply with no extractable text gets the same fixed message', async () => {
    withProviders({
      'api.cerebras.ai': () => Response.json({ choices: [{ message: {}, finish_reason: 'stop' }], usage: usage() }),
    });

    await readFailure(await buildApp().fetch(
      postProcessRequest({ text: 'hello wrld', prompt: 'fix grammar', account_key: 'lk' })
    ));
  });
});
