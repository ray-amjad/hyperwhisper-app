// Tests for the OpenAI synchronous multipart STT adapter.
//
// Only global `fetch` is mocked — never a shared module. bun's `mock.module` is
// process-wide and leaks into every other file in the same `bun test src` run,
// so the multipart body is inspected off the intercepted RequestInit instead.

import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { transcribeWithOpenAI } from './openai';
import { AudioTooLargeError, ProviderInputError, ProviderUnavailableError } from './types';
import { OPENAI_INLINE_MAX_BYTES } from '../lib/constants';

const originalFetch = globalThis.fetch;
let savedKey: string | undefined;

/** 960,000 bytes is exactly 120 s under the 64 kbps byte→seconds estimate. */
const BYTES_FOR_120_SECONDS = 960_000;
const audio = (bytes = 1_000) => new ArrayBuffer(bytes);

beforeEach(() => {
  savedKey = process.env.OPENAI_API_KEY;
  process.env.OPENAI_API_KEY = 'test-key';
});

afterEach(() => {
  globalThis.fetch = originalFetch;
  if (savedKey === undefined) delete process.env.OPENAI_API_KEY;
  else process.env.OPENAI_API_KEY = savedKey;
});

/** Intercepts the upstream call and exposes the multipart body it was sent. */
function captureRequest(body: unknown, init?: ResponseInit) {
  const captured: { url?: string; headers?: HeadersInit; form?: FormData } = {};
  globalThis.fetch = mock(async (url: string, requestInit: RequestInit) => {
    captured.url = url;
    captured.headers = requestInit.headers;
    captured.form = requestInit.body as FormData;
    return typeof body === 'string'
      ? new Response(body, { status: 200, ...init })
      : Response.json(body, init);
  }) as unknown as typeof fetch;
  return captured;
}

function errorResponse(status: number, text = 'upstream said no') {
  let called = false;
  globalThis.fetch = mock(async () => {
    called = true;
    return new Response(text, { status });
  }) as unknown as typeof fetch;
  return () => called;
}

describe('transcribeWithOpenAI — gates that run before any upstream call', () => {
  test('rejects audio over the 25 MB inline cap with AudioTooLargeError and never calls fetch', async () => {
    const wasCalled = errorResponse(200);

    await expect(transcribeWithOpenAI(audio(OPENAI_INLINE_MAX_BYTES + 1), 'audio/wav'))
      .rejects.toThrow(AudioTooLargeError);
    expect(wasCalled()).toBe(false);
  });

  test('accepts audio exactly at the cap', async () => {
    const captured = captureRequest({ text: 'ok', duration: 1 });

    await transcribeWithOpenAI(audio(OPENAI_INLINE_MAX_BYTES), 'audio/wav');
    expect(captured.url).toBe('https://api.openai.com/v1/audio/transcriptions');
  });

  test('throws a plain Error (not a fallback signal) when OPENAI_API_KEY is missing', async () => {
    delete process.env.OPENAI_API_KEY;
    const wasCalled = errorResponse(200);

    const thrown = await transcribeWithOpenAI(audio(), 'audio/wav').catch((e) => e);
    expect(thrown).toBeInstanceOf(Error);
    expect(thrown).not.toBeInstanceOf(ProviderUnavailableError);
    expect(thrown.message).toBe('OPENAI_API_KEY not configured');
    expect(wasCalled()).toBe(false);
  });
});

describe('transcribeWithOpenAI — multipart request shape', () => {
  test('sends verbose_json only for whisper-1; gpt-4o models get json', async () => {
    const whisper = captureRequest({ text: 'hi', duration: 3 });
    await transcribeWithOpenAI(audio(), 'audio/wav', undefined, undefined, { model: 'whisper-1' });
    expect(whisper.form?.get('response_format')).toBe('verbose_json');
    expect(whisper.form?.get('model')).toBe('whisper-1');

    const gpt4o = captureRequest({ text: 'hi', usage: { input_tokens: 10, output_tokens: 5 } });
    await transcribeWithOpenAI(audio(), 'audio/wav', undefined, undefined, { model: 'gpt-4o-transcribe' });
    expect(gpt4o.form?.get('response_format')).toBe('json');
  });

  test('defaults to gpt-4o-transcribe when the context names no model', async () => {
    const captured = captureRequest({ text: 'hi', usage: { input_tokens: 10 } });

    await transcribeWithOpenAI(audio(), 'audio/wav');
    expect(captured.form?.get('model')).toBe('gpt-4o-transcribe');
    expect(captured.form?.get('response_format')).toBe('json');
  });

  test('strips a BCP-47 region subtag to the bare ISO-639-1 code', async () => {
    const captured = captureRequest({ text: 'olá', usage: { input_tokens: 1 } });

    await transcribeWithOpenAI(audio(), 'audio/wav', 'pt-BR');
    expect(captured.form?.get('language')).toBe('pt');
  });

  test('omits the language field for "auto" and for an unset language', async () => {
    const auto = captureRequest({ text: 'hi', usage: { input_tokens: 1 } });
    await transcribeWithOpenAI(audio(), 'audio/wav', 'AUTO');
    expect(auto.form?.has('language')).toBe(false);

    const unset = captureRequest({ text: 'hi', usage: { input_tokens: 1 } });
    await transcribeWithOpenAI(audio(), 'audio/wav');
    expect(unset.form?.has('language')).toBe(false);
  });

  test('forwards an initial prompt as `prompt`, and omits the field without one', async () => {
    const withPrompt = captureRequest({ text: 'hi', usage: { input_tokens: 1 } });
    await transcribeWithOpenAI(audio(), 'audio/wav', undefined, 'HyperWhisper, Drizzle');
    expect(withPrompt.form?.get('prompt')).toBe('HyperWhisper, Drizzle');

    const without = captureRequest({ text: 'hi', usage: { input_tokens: 1 } });
    await transcribeWithOpenAI(audio(), 'audio/wav');
    expect(without.form?.has('prompt')).toBe(false);
  });

  test('sends the vocabulary as keywords[] on gpt-transcribe, not as prompt', async () => {
    const captured = captureRequest({ text: 'hi', usage: { input_tokens: 1 } });

    await transcribeWithOpenAI(audio(), 'audio/wav', undefined, 'HyperWhisper, Drizzle', {
      model: 'gpt-transcribe',
    });
    expect(captured.form?.getAll('keywords[]')).toEqual(['HyperWhisper', 'Drizzle']);
    expect(captured.form?.has('prompt')).toBe(false);
  });

  test('de-duplicates keywords, strips angle brackets, and caps the list at 100', async () => {
    const captured = captureRequest({ text: 'hi', usage: { input_tokens: 1 } });
    const terms = ['<b>Bold', 'Kept', 'kept', ...Array.from({ length: 120 }, (_, i) => `term${i}`)];

    await transcribeWithOpenAI(audio(), 'audio/wav', undefined, terms.join(', '), {
      model: 'gpt-transcribe',
    });
    const sent = captured.form?.getAll('keywords[]') as string[];
    expect(sent.length).toBe(100);
    expect(sent[0]).toBe('bBold');
    expect(sent.filter((k) => k.toLowerCase() === 'kept').length).toBe(1);
  });

  test('keeps the prompt path for whisper-1 and the gpt-4o models', async () => {
    for (const model of ['whisper-1', 'gpt-4o-transcribe', 'gpt-4o-mini-transcribe']) {
      const captured = captureRequest({ text: 'hi', usage: { input_tokens: 1 } });
      await transcribeWithOpenAI(audio(), 'audio/wav', undefined, 'HyperWhisper', { model });
      expect(captured.form?.get('prompt')).toBe('HyperWhisper');
      expect(captured.form?.has('keywords[]')).toBe(false);
    }
  });

  test('names the file part from the content type, falling back to .wav', async () => {
    const mp3 = captureRequest({ text: 'hi', usage: { input_tokens: 1 } });
    await transcribeWithOpenAI(audio(), 'audio/mpeg');
    expect((mp3.form?.get('file') as File).name).toBe('audio.mp3');

    const unknown = captureRequest({ text: 'hi', usage: { input_tokens: 1 } });
    await transcribeWithOpenAI(audio(), 'application/octet-stream');
    expect((unknown.form?.get('file') as File).name).toBe('audio.wav');
  });

  test('sends the API key as a bearer token', async () => {
    process.env.OPENAI_API_KEY = 'sk-unit-test';
    const captured = captureRequest({ text: 'hi', usage: { input_tokens: 1 } });

    await transcribeWithOpenAI(audio(), 'audio/wav');
    expect((captured.headers as Record<string, string>).Authorization).toBe('Bearer sk-unit-test');
  });
});

describe('transcribeWithOpenAI — HTTP error classification', () => {
  test('401 and 403 throw a plain Error so the chain does NOT retry a bad key', async () => {
    for (const status of [401, 403]) {
      errorResponse(status);
      const thrown = await transcribeWithOpenAI(audio(), 'audio/wav').catch((e) => e);
      expect(thrown).not.toBeInstanceOf(ProviderUnavailableError);
      expect(thrown).not.toBeInstanceOf(ProviderInputError);
      expect(thrown.message).toBe('OpenAI API key is invalid or unauthorized');
    }
  });

  test('402 fails over as insufficient funds so a sibling provider can still serve', async () => {
    errorResponse(402);

    const thrown = await transcribeWithOpenAI(audio(), 'audio/wav').catch((e) => e);
    expect(thrown).toBeInstanceOf(ProviderUnavailableError);
    expect(thrown.message).toContain('insufficient funds');
  });

  test('429 fails over as a rate limit', async () => {
    errorResponse(429);

    const thrown = await transcribeWithOpenAI(audio(), 'audio/wav').catch((e) => e);
    expect(thrown).toBeInstanceOf(ProviderUnavailableError);
    expect(thrown.message).toContain('rate limit exceeded');
  });

  test('5xx fails over as upstream_5xx', async () => {
    errorResponse(503);

    const thrown = await transcribeWithOpenAI(audio(), 'audio/wav').catch((e) => e);
    expect(thrown).toBeInstanceOf(ProviderUnavailableError);
    expect(thrown.message).toContain('upstream 5xx: 503');
  });

  test('other 4xx becomes ProviderInputError carrying the status and body preview', async () => {
    errorResponse(400, 'unsupported language code');

    const thrown = await transcribeWithOpenAI(audio(), 'audio/wav').catch((e) => e);
    expect(thrown).toBeInstanceOf(ProviderInputError);
    expect(thrown.status).toBe(400);
    expect(thrown.message).toContain('unsupported language code');
  });

  test('a 200 with an unparsable body fails over instead of 500ing the request', async () => {
    captureRequest('<html>edge proxy</html>');

    const thrown = await transcribeWithOpenAI(audio(), 'audio/wav').catch((e) => e);
    expect(thrown).toBeInstanceOf(ProviderUnavailableError);
    expect(thrown.message).toContain('malformed 200 response body');
  });
});

describe('transcribeWithOpenAI — transcript, duration and billing', () => {
  test('returns the transcript, language and duration whisper-1 reports', async () => {
    captureRequest({ text: 'hello world', language: 'english', duration: 12.5 });

    const result = await transcribeWithOpenAI(audio(), 'audio/wav', undefined, undefined, { model: 'whisper-1' });
    expect(result.text).toBe('hello world');
    expect(result.language).toBe('english');
    expect(result.durationSeconds).toBe(12.5);
    expect(result.source).toBe('openai');
    expect(result.costUsd).toBeGreaterThan(0);
  });

  test('whisper-1 falls back to usage.seconds when the body omits duration', async () => {
    captureRequest({ text: 'hello', usage: { type: 'duration', seconds: 30 } });

    const result = await transcribeWithOpenAI(audio(), 'audio/wav', undefined, undefined, { model: 'whisper-1' });
    expect(result.durationSeconds).toBe(30);
  });

  test('whisper-1 fails closed: a transcript with no usable duration bills a byte estimate, never $0', async () => {
    captureRequest({ text: 'hello', duration: 0 });

    const result = await transcribeWithOpenAI(
      audio(BYTES_FOR_120_SECONDS), 'audio/wav', undefined, undefined, { model: 'whisper-1' },
    );
    expect(result.durationSeconds).toBeCloseTo(120, 6);
    expect(result.costUsd).toBeGreaterThan(0);
  });

  test('gpt-4o ignores any echoed duration and estimates seconds from the payload size', async () => {
    captureRequest({ text: 'hello', duration: 999, usage: { input_tokens: 100, output_tokens: 20 } });

    const result = await transcribeWithOpenAI(audio(BYTES_FOR_120_SECONDS), 'audio/wav');
    expect(result.durationSeconds).toBeCloseTo(120, 6);
  });

  test('gpt-4o bills on tokens — mini is cheaper than the full model for identical usage', async () => {
    const usage = { input_tokens: 100_000, output_tokens: 20_000 };

    captureRequest({ text: 'hello', usage });
    const full = await transcribeWithOpenAI(audio(), 'audio/wav', undefined, undefined, { model: 'gpt-4o-transcribe' });

    captureRequest({ text: 'hello', usage });
    const mini = await transcribeWithOpenAI(
      audio(), 'audio/wav', undefined, undefined, { model: 'gpt-4o-mini-transcribe' },
    );

    expect(full.costUsd).toBeGreaterThan(0);
    expect(mini.costUsd).toBeGreaterThan(0);
    expect(mini.costUsd).toBeLessThan(full.costUsd);
  });

  test('an empty or whitespace-only transcript returns no_speech at zero duration and zero cost', async () => {
    for (const text of ['', '   \n ']) {
      captureRequest({ text, language: 'en', duration: 42 });

      const result = await transcribeWithOpenAI(audio(BYTES_FOR_120_SECONDS), 'audio/wav');
      expect(result.source).toBe('no_speech');
      expect(result.text).toBe('');
      expect(result.durationSeconds).toBe(0);
      expect(result.costUsd).toBe(0);
      expect(result.language).toBe('en');
    }
  });
});
