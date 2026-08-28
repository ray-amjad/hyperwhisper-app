// Tests for the Gemini transcription adapter's request path: credentials, the
// inline-audio size gate, the request body it builds, upstream error mapping,
// the per-request timeout budget, and how usageMetadata turns into cost.
//
// `gemini.test.ts` covers the pure helpers (`resolveAudioInputTokens`,
// `geminiMimeType`); this file covers `transcribeWithGemini` itself.
//
// NAMING: this file tests `gemini.ts` — the Gemini LLM-as-transcriber adapter —
// and was called `gemini-transcribe.test.ts` until issue #331 added a real
// `gemini-transcribe.ts` (the Gemini 3.5 Transcribe `/v1beta/interactions`
// adapter, whose own suite now owns that name). Two different providers from
// one vendor; do not merge the two files.
//
// Only global `fetch` is mocked — never a shared module. bun's `mock.module` is
// process-wide and leaks into every other file in the same `bun test src` run.

import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { transcribeWithGemini } from './gemini';
import { AudioTooLargeError, ProviderInputError, ProviderUnavailableError } from './types';
import { GEMINI_INLINE_MAX_BYTES } from '../lib/constants';

const originalFetch = globalThis.fetch;
const ENV_KEYS = ['GEMINI_API_KEY', 'GOOGLE_GEMINI_API_KEY', 'STT_PROVIDER_TIMEOUT_MS'] as const;
const savedEnv: Record<string, string | undefined> = {};

// Gemini bills audio at a flat 32 tokens/sec, so 1920 audio tokens = 60s.
const AUDIO_TOKENS_PER_SECOND = 32;

/** Audio whose base64 encoding is known, so the inline payload is assertable. */
function audio(bytes = 8): ArrayBuffer {
  const buffer = new ArrayBuffer(bytes);
  new Uint8Array(buffer).fill(0x41); // 'A'
  return buffer;
}

interface GeminiBody {
  contents: Array<{ role: string; parts: Array<Record<string, any>> }>;
  generationConfig: { temperature: number; thinkingConfig: Record<string, unknown> };
}

interface Captured {
  url?: string;
  headers?: Record<string, string>;
  body?: GeminiBody;
  calls: number;
}

function captureRequest(responseBody: unknown, status = 200): Captured {
  const captured: Captured = { calls: 0 };
  globalThis.fetch = mock(async (url: string, init: RequestInit) => {
    captured.calls += 1;
    captured.url = url;
    captured.headers = init.headers as Record<string, string>;
    captured.body = JSON.parse(init.body as string) as GeminiBody;
    return typeof responseBody === 'string'
      ? new Response(responseBody, { status })
      : new Response(JSON.stringify(responseBody), {
        status,
        headers: { 'Content-Type': 'application/json' },
      });
  }) as unknown as typeof fetch;
  return captured;
}

function respondWith(status: number, body = 'upstream said no'): { calls: number } {
  const state = { calls: 0 };
  globalThis.fetch = mock(async () => {
    state.calls += 1;
    return new Response(body, { status });
  }) as unknown as typeof fetch;
  return state;
}

/** A transcript-bearing generateContent response with the given usageMetadata. */
function generateContentResponse(text: string, usageMetadata?: Record<string, unknown>) {
  return {
    candidates: [{ content: { parts: [{ text }] } }],
    ...(usageMetadata ? { usageMetadata } : {}),
  };
}

function promptTextOf(captured: Captured): string {
  return captured.body!.contents[0].parts[0].text as string;
}

function inlineDataOf(captured: Captured): { mime_type: string; data: string } {
  return captured.body!.contents[0].parts[1].inline_data;
}

beforeEach(() => {
  for (const key of ENV_KEYS) savedEnv[key] = process.env[key];
  process.env.GEMINI_API_KEY = 'test-key';
  delete process.env.GOOGLE_GEMINI_API_KEY;
  delete process.env.STT_PROVIDER_TIMEOUT_MS;
});

afterEach(() => {
  globalThis.fetch = originalFetch;
  for (const key of ENV_KEYS) {
    if (savedEnv[key] === undefined) delete process.env[key];
    else process.env[key] = savedEnv[key];
  }
});

describe('transcribeWithGemini — credentials', () => {
  test('a missing API key throws before any upstream call is made', async () => {
    delete process.env.GEMINI_API_KEY;
    const state = respondWith(200, '{}');

    const thrown = await transcribeWithGemini(audio(), 'audio/wav').catch((e) => e);
    expect(thrown).toBeInstanceOf(Error);
    expect(thrown.message).toBe('GEMINI_API_KEY not configured');
    expect(state.calls).toBe(0);
  });

  test('falls back to GOOGLE_GEMINI_API_KEY and sends it as x-goog-api-key', async () => {
    delete process.env.GEMINI_API_KEY;
    process.env.GOOGLE_GEMINI_API_KEY = 'legacy-key';
    const captured = captureRequest(generateContentResponse('hello'));

    await transcribeWithGemini(audio(), 'audio/wav');
    expect(captured.headers!['x-goog-api-key']).toBe('legacy-key');
    // The key must never travel as a query parameter — that leaks it into logs.
    expect(captured.url).not.toContain('legacy-key');
  });
});

describe('transcribeWithGemini — inline size gate', () => {
  test('rejects audio over the inline cap without calling Gemini', async () => {
    const state = respondWith(200, '{}');

    const thrown = await transcribeWithGemini(audio(GEMINI_INLINE_MAX_BYTES + 1), 'audio/wav')
      .catch((e) => e);
    expect(thrown).toBeInstanceOf(AudioTooLargeError);
    expect(thrown.actualBytes).toBe(GEMINI_INLINE_MAX_BYTES + 1);
    expect(thrown.maxBytes).toBe(GEMINI_INLINE_MAX_BYTES);
    expect(state.calls).toBe(0);
  });

  test('accepts audio exactly at the inline cap', async () => {
    const captured = captureRequest(generateContentResponse('ok'));

    await transcribeWithGemini(audio(GEMINI_INLINE_MAX_BYTES), 'audio/wav');
    expect(captured.calls).toBe(1);
  });
});

describe('transcribeWithGemini — request shape', () => {
  test('posts to the default model endpoint with the audio inlined as base64', async () => {
    const captured = captureRequest(generateContentResponse('hello'));

    await transcribeWithGemini(audio(6), 'audio/mpeg');

    expect(captured.url).toBe(
      'https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent'
    );
    expect(inlineDataOf(captured).mime_type).toBe('audio/mp3');
    expect(inlineDataOf(captured).data).toBe(Buffer.from(new Uint8Array(6).fill(0x41)).toString('base64'));
    expect(captured.body!.generationConfig.temperature).toBe(0);
    expect(captured.body!.contents[0].role).toBe('user');
  });

  test('URL-encodes the caller-selected model instead of splicing it in raw', async () => {
    const captured = captureRequest(generateContentResponse('hello'));

    await transcribeWithGemini(audio(), 'audio/wav', undefined, undefined, { model: 'models/weird one' });
    expect(captured.url).toBe(
      'https://generativelanguage.googleapis.com/v1beta/models/models%2Fweird%20one:generateContent'
    );
  });

  test('keeps thinking at the lowest value each model family allows', async () => {
    const cases: Array<[string, Record<string, unknown>]> = [
      ['gemini-2.5-flash', { thinkingBudget: 0 }],
      ['gemini-2.5-flash-lite', { thinkingBudget: 0 }],
      // 0 is rejected by 2.5 Pro, so the floor there is 128.
      ['gemini-2.5-pro', { thinkingBudget: 128 }],
      ['gemini-3-flash-preview', { thinkingLevel: 'minimal' }],
      ['gemini-3.1-pro-preview', { thinkingLevel: 'low' }],
      // An unrecognised model falls back to the 2.5-flash config.
      ['some-future-model', { thinkingBudget: 0 }],
    ];

    for (const [model, expected] of cases) {
      const captured = captureRequest(generateContentResponse('hello'));
      await transcribeWithGemini(audio(), 'audio/wav', undefined, undefined, { model });
      expect(captured.body!.generationConfig.thinkingConfig).toEqual(expected);
    }
  });

  test('names an explicit language in prose and keeps the code beside it', async () => {
    const captured = captureRequest(generateContentResponse('bonjour'));

    await transcribeWithGemini(audio(), 'audio/wav', 'fr-FR');
    const prompt = promptTextOf(captured);
    expect(prompt).toContain('French');
    expect(prompt).toContain('code "fr-FR"');
    expect(prompt).toContain('transcribe it in that language');
  });

  test('says nothing about language for auto-detect', async () => {
    const captured = captureRequest(generateContentResponse('hello'));

    await transcribeWithGemini(audio(), 'audio/wav', 'auto');
    expect(promptTextOf(captured)).not.toContain('The audio is in');
  });

  test('quotes vocabulary terms and strips bullet markers', async () => {
    const captured = captureRequest(generateContentResponse('hello'));

    await transcribeWithGemini(audio(), 'audio/wav', undefined, '- HyperWhisper\n- Fly.io, Deepgram');
    const prompt = promptTextOf(captured);
    expect(prompt).toContain('Spell these terms exactly when you hear them: "HyperWhisper", "Fly.io", "Deepgram".');
    expect(prompt).not.toContain('"- HyperWhisper"');
  });

  test('caps the vocabulary at 100 terms so a huge list cannot inflate the prompt', async () => {
    const captured = captureRequest(generateContentResponse('hello'));
    const terms = Array.from({ length: 150 }, (_, i) => `term${i}`);

    await transcribeWithGemini(audio(), 'audio/wav', undefined, terms.join(','));
    const prompt = promptTextOf(captured);
    expect(prompt).toContain('"term99"');
    expect(prompt).not.toContain('"term100"');
  });

  test('omits the vocabulary sentence when the prompt has no usable terms', async () => {
    const captured = captureRequest(generateContentResponse('hello'));

    await transcribeWithGemini(audio(), 'audio/wav', undefined, ',,\n');
    expect(promptTextOf(captured)).not.toContain('Spell these terms');
  });
});

describe('transcribeWithGemini — upstream error mapping', () => {
  test('a bad API key is a hard error, not a failover signal', async () => {
    for (const status of [401, 403]) {
      respondWith(status);
      const thrown = await transcribeWithGemini(audio(), 'audio/wav').catch((e) => e);
      // ProviderUnavailableError would send the route on to the next provider;
      // our own broken credential must surface instead of being papered over.
      expect(thrown).not.toBeInstanceOf(ProviderUnavailableError);
      expect(thrown.message).toBe('Gemini API key is invalid or unauthorized');
    }
  });

  test('a 429 fails over as a rate limit', async () => {
    respondWith(429);
    const thrown = await transcribeWithGemini(audio(), 'audio/wav').catch((e) => e);
    expect(thrown).toBeInstanceOf(ProviderUnavailableError);
    expect(thrown.message).toContain('rate limit exceeded');
  });

  test('a 402 fails over instead of failing the request', async () => {
    respondWith(402);
    const thrown = await transcribeWithGemini(audio(), 'audio/wav').catch((e) => e);
    expect(thrown).toBeInstanceOf(ProviderUnavailableError);
    expect(thrown.message).toContain('insufficient funds');
  });

  test('a 5xx fails over as an upstream error', async () => {
    respondWith(503);
    const thrown = await transcribeWithGemini(audio(), 'audio/wav').catch((e) => e);
    expect(thrown).toBeInstanceOf(ProviderUnavailableError);
    expect(thrown.message).toContain('upstream 5xx: 503');
  });

  test('another 4xx is an input error carrying the upstream status', async () => {
    respondWith(400, 'unsupported audio mime type');
    const thrown = await transcribeWithGemini(audio(), 'audio/wav').catch((e) => e);
    expect(thrown).toBeInstanceOf(ProviderInputError);
    expect(thrown.status).toBe(400);
    expect(thrown.message).toContain('unsupported audio mime type');
  });

  test('a 200 with a body that is not JSON fails over rather than throwing a parse error', async () => {
    globalThis.fetch = mock(async () => new Response('<html>proxy error</html>', {
      status: 200,
      headers: { 'Content-Type': 'text/html' },
    })) as unknown as typeof fetch;

    const thrown = await transcribeWithGemini(audio(), 'audio/wav').catch((e) => e);
    expect(thrown).toBeInstanceOf(ProviderUnavailableError);
    expect(thrown.message).toContain('malformed 200 response body');
  });
});

describe('transcribeWithGemini — timeout budget', () => {
  test('aborts on the configured budget and reports it as a timeout', async () => {
    process.env.STT_PROVIDER_TIMEOUT_MS = '25';
    let aborted = false;
    globalThis.fetch = mock((_url: string, init: RequestInit) => new Promise((_resolve, reject) => {
      init.signal!.addEventListener('abort', () => {
        aborted = true;
        reject(new DOMException('The operation was aborted.', 'AbortError'));
      });
    })) as unknown as typeof fetch;

    const thrown = await transcribeWithGemini(audio(), 'audio/wav').catch((e) => e);
    expect(aborted).toBe(true);
    expect(thrown).toBeInstanceOf(ProviderUnavailableError);
    expect(thrown.kind).toBe('timeout');
    expect(thrown.message).toContain('timeout after 25ms');
  });

  test('a connection failure is a network error, not a timeout', async () => {
    globalThis.fetch = mock(async () => {
      throw new TypeError('Unable to connect');
    }) as unknown as typeof fetch;

    const thrown = await transcribeWithGemini(audio(), 'audio/wav').catch((e) => e);
    expect(thrown).toBeInstanceOf(ProviderUnavailableError);
    expect(thrown.kind).toBe('network_error');
  });
});

describe('transcribeWithGemini — result and billing', () => {
  test('joins every text part, trims it, and derives duration from the audio tokens', async () => {
    globalThis.fetch = mock(async () => Response.json({
      candidates: [{ content: { parts: [{ text: '  hello ' }, { text: 'world  ' }, { notText: 1 }] } }],
      usageMetadata: {
        promptTokenCount: 2000,
        promptTokensDetails: [{ modality: 'AUDIO', tokenCount: 1920 }],
        candidatesTokenCount: 10,
      },
    })) as unknown as typeof fetch;

    const result = await transcribeWithGemini(audio(), 'audio/wav', 'en-US');
    expect(result.text).toBe('hello world');
    expect(result.source).toBe('gemini');
    expect(result.language).toBe('en-US');
    expect(result.durationSeconds).toBe(1920 / AUDIO_TOKENS_PER_SECOND);
  });

  test('bills the prompt-text remainder at the text rate, not the audio rate', async () => {
    globalThis.fetch = mock(async () => Response.json(generateContentResponse('hello', {
      promptTokenCount: 2000,
      promptTokensDetails: [{ modality: 'AUDIO', tokenCount: 1920 }],
      candidatesTokenCount: 100,
      thoughtsTokenCount: 50,
    }))) as unknown as typeof fetch;

    const result = await transcribeWithGemini(audio(), 'audio/wav');

    // gemini-2.5-flash: audio $1.00/M, text $0.30/M, output $2.50/M.
    // 1920 audio + 80 text + (100 + 50) output.
    expect(result.costUsd).toBeCloseTo(0.002319, 9);
    // Billing the 80 text tokens at the audio rate would give 0.002375.
    expect(result.costUsd).toBeLessThan(0.002375);
  });

  test('bills thinking tokens as output, not as free', async () => {
    const costFor = async (thoughtsTokenCount: number) => {
      globalThis.fetch = mock(async () => Response.json(generateContentResponse('hello', {
        promptTokenCount: 1920,
        promptTokensDetails: [{ modality: 'AUDIO', tokenCount: 1920 }],
        candidatesTokenCount: 100,
        thoughtsTokenCount,
      }))) as unknown as typeof fetch;
      const result = await transcribeWithGemini(audio(), 'audio/wav');
      return result.costUsd;
    };

    const withoutThinking = await costFor(0);
    const withThinking = await costFor(400);
    // 400 extra output tokens at $2.50/M.
    expect(withThinking - withoutThinking).toBeCloseTo(400 * 2.5e-6, 9);
  });

  test('an empty transcript is reported as no_speech and is not billed', async () => {
    globalThis.fetch = mock(async () => Response.json(generateContentResponse('   ', {
      promptTokenCount: 2000,
      promptTokensDetails: [{ modality: 'AUDIO', tokenCount: 1920 }],
      candidatesTokenCount: 5,
    }))) as unknown as typeof fetch;

    const result = await transcribeWithGemini(audio(), 'audio/wav', 'en-US');
    expect(result.source).toBe('no_speech');
    expect(result.text).toBe('');
    expect(result.costUsd).toBe(0);
    expect(result.durationSeconds).toBe(0);
  });

  test('a response with no candidates at all is no_speech rather than a crash', async () => {
    globalThis.fetch = mock(async () => Response.json({ usageMetadata: { promptTokenCount: 100 } })) as unknown as typeof fetch;

    const result = await transcribeWithGemini(audio(), 'audio/wav');
    expect(result.source).toBe('no_speech');
    expect(result.text).toBe('');
  });

  test('falls back to a byte-based estimate when Gemini returns no usage at all', async () => {
    globalThis.fetch = mock(async () => Response.json(generateContentResponse('hello'))) as unknown as typeof fetch;

    // 480_000 bytes ≈ 1 minute at the 64 kbps estimate → 1920 audio tokens.
    const result = await transcribeWithGemini(audio(480_000), 'audio/wav');
    expect(result.durationSeconds).toBeCloseTo(60, 6);
    // No usage total → no text tokens are invented, so it is audio-only billing.
    expect(result.costUsd).toBeCloseTo(1920 * 1e-6, 9);
  });

  test('prices a non-default model at that model rate', async () => {
    globalThis.fetch = mock(async () => Response.json(generateContentResponse('hello', {
      promptTokenCount: 1920,
      promptTokensDetails: [{ modality: 'AUDIO', tokenCount: 1920 }],
      candidatesTokenCount: 0,
    }))) as unknown as typeof fetch;

    const result = await transcribeWithGemini(audio(), 'audio/wav', undefined, undefined, {
      model: 'gemini-2.5-flash-lite',
    });
    // flash-lite audio input is $0.30/M against flash's $1.00/M.
    expect(result.costUsd).toBeCloseTo(1920 * 0.3e-6, 9);
  });
});
