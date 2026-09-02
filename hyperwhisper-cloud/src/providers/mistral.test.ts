// Tests for the Mistral Voxtral synchronous multipart STT adapter.
//
// Only global `fetch` is mocked — never a shared module. bun's `mock.module` is
// process-wide and leaks into every other file in the same `bun test src` run.

import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { transcribeWithMistral } from './mistral';
import { ProviderInputError, ProviderUnavailableError } from './types';

const originalFetch = globalThis.fetch;
let savedKey: string | undefined;

/** 960,000 bytes is exactly 120 s under the 64 kbps byte→seconds estimate. */
const BYTES_FOR_120_SECONDS = 960_000;
const audio = (bytes = 1_000) => new ArrayBuffer(bytes);

beforeEach(() => {
  savedKey = process.env.MISTRAL_API_KEY;
  process.env.MISTRAL_API_KEY = 'test-key';
});

afterEach(() => {
  globalThis.fetch = originalFetch;
  if (savedKey === undefined) delete process.env.MISTRAL_API_KEY;
  else process.env.MISTRAL_API_KEY = savedKey;
});

function captureRequest(body: unknown) {
  const captured: { url?: string; headers?: HeadersInit; form?: FormData } = {};
  globalThis.fetch = mock(async (url: string, init: RequestInit) => {
    captured.url = url;
    captured.headers = init.headers;
    captured.form = init.body as FormData;
    return typeof body === 'string' ? new Response(body, { status: 200 }) : Response.json(body);
  }) as unknown as typeof fetch;
  return captured;
}

/**
 * Swaps `console.log` for the duration of `run` and returns the details object of
 * the `provider.no_speech` event it logged. Same swap-the-global idiom as
 * `utils.test.ts` — no spy library is used anywhere in this suite.
 */
async function captureNoSpeechEvent(run: () => Promise<unknown>): Promise<Record<string, unknown>> {
  const logged: unknown[][] = [];
  const originalLog = console.log;
  console.log = ((...args: unknown[]) => { logged.push(args); }) as typeof console.log;
  try {
    await run();
  } finally {
    console.log = originalLog;
  }
  const event = logged.find((args) => args[0] === 'provider.no_speech');
  if (!event) throw new Error('no provider.no_speech event was logged');
  return event[1] as Record<string, unknown>;
}

function errorResponse(status: number, text = 'upstream said no') {
  let called = false;
  globalThis.fetch = mock(async () => {
    called = true;
    return new Response(text, { status });
  }) as unknown as typeof fetch;
  return () => called;
}

const okBody = { text: 'hi', usage: { prompt_audio_seconds: 10 } };

describe('transcribeWithMistral — multipart request shape', () => {
  test('throws a plain Error (not a fallback signal) when MISTRAL_API_KEY is missing', async () => {
    delete process.env.MISTRAL_API_KEY;
    const wasCalled = errorResponse(200);

    const thrown = await transcribeWithMistral(audio(), 'audio/wav').catch((e) => e);
    expect(thrown).not.toBeInstanceOf(ProviderUnavailableError);
    expect(thrown.message).toBe('MISTRAL_API_KEY not configured');
    expect(wasCalled()).toBe(false);
  });

  test('posts to the Voxtral endpoint with a bearer key and the default model', async () => {
    process.env.MISTRAL_API_KEY = 'mist-unit-test';
    const captured = captureRequest(okBody);

    await transcribeWithMistral(audio(), 'audio/wav');
    expect(captured.url).toBe('https://api.mistral.ai/v1/audio/transcriptions');
    expect(captured.form?.get('model')).toBe('voxtral-mini-latest');
    expect((captured.headers as Record<string, string>).Authorization).toBe('Bearer mist-unit-test');
  });

  test('honours a model from the context', async () => {
    const captured = captureRequest(okBody);

    await transcribeWithMistral(audio(), 'audio/wav', undefined, undefined, { model: 'voxtral-small-latest' });
    expect(captured.form?.get('model')).toBe('voxtral-small-latest');
  });

  test('strips a BCP-47 region subtag, and omits the field for "auto"', async () => {
    const regional = captureRequest(okBody);
    await transcribeWithMistral(audio(), 'audio/wav', 'pt-BR');
    expect(regional.form?.get('language')).toBe('pt');

    const auto = captureRequest(okBody);
    await transcribeWithMistral(audio(), 'audio/wav', 'Auto');
    expect(auto.form?.has('language')).toBe(false);
  });

  test('accepts raw AAC, which the sibling adapters do not name', async () => {
    const captured = captureRequest(okBody);

    await transcribeWithMistral(audio(), 'audio/aac');
    expect((captured.form?.get('file') as File).name).toBe('audio.aac');
  });
});

describe('transcribeWithMistral — context_bias encoding', () => {
  test('sends one REPEATED form field per phrase, never a single joined value', async () => {
    const captured = captureRequest(okBody);

    await transcribeWithMistral(audio(), 'audio/wav', undefined, 'Drizzle, HyperWhisper\nVoxtral;Fly.io');
    expect(captured.form?.getAll('context_bias')).toEqual(['Drizzle', 'HyperWhisper', 'Voxtral', 'Fly.io']);
  });

  test('strips list bullets and surrounding whitespace from each phrase', async () => {
    const captured = captureRequest(okBody);

    await transcribeWithMistral(audio(), 'audio/wav', undefined, '- Drizzle\n*  HyperWhisper\n   Voxtral  ');
    expect(captured.form?.getAll('context_bias')).toEqual(['Drizzle', 'HyperWhisper', 'Voxtral']);
  });

  // Voxtral 400s the whole request when ANY item holds whitespace, so a single
  // multi-word vocabulary term used to fail the transcription outright.
  test('joins a multi-word phrase with underscores instead of leaving the space', async () => {
    const captured = captureRequest(okBody);

    await transcribeWithMistral(audio(), 'audio/wav', undefined, 'Claude Code,Drizzle,Fly.io');
    expect(captured.form?.getAll('context_bias')).toEqual(['Claude_Code', 'Drizzle', 'Fly.io']);
  });

  test('leaves no whitespace of any kind inside a phrase', async () => {
    const captured = captureRequest(okBody);

    await transcribeWithMistral(audio(), 'audio/wav', undefined, '-  New\tYork  City , a b');
    expect(captured.form?.getAll('context_bias')).toEqual(['New_York_City', 'a_b']);
    for (const term of captured.form?.getAll('context_bias') ?? []) {
      expect(String(term)).not.toMatch(/[\s,]/);
    }
  });

  test('drops phrases longer than 80 characters and keeps the rest', async () => {
    const captured = captureRequest(okBody);
    const tooLong = 'x'.repeat(81);

    await transcribeWithMistral(audio(), 'audio/wav', undefined, `${tooLong},Drizzle,${'y'.repeat(80)}`);
    expect(captured.form?.getAll('context_bias')).toEqual(['Drizzle', 'y'.repeat(80)]);
  });

  test('caps the list at 100 phrases', async () => {
    const captured = captureRequest(okBody);
    const prompt = Array.from({ length: 150 }, (_, i) => `term${i}`).join(',');

    await transcribeWithMistral(audio(), 'audio/wav', undefined, prompt);
    const bias = captured.form?.getAll('context_bias') ?? [];
    expect(bias).toHaveLength(100);
    expect(bias[0]).toBe('term0');
    expect(bias[99]).toBe('term99');
  });

  test('omits the field when the prompt is absent or yields no usable phrase', async () => {
    const noPrompt = captureRequest(okBody);
    await transcribeWithMistral(audio(), 'audio/wav');
    expect(noPrompt.form?.has('context_bias')).toBe(false);

    const blankPrompt = captureRequest(okBody);
    await transcribeWithMistral(audio(), 'audio/wav', undefined, ' , ,\n');
    expect(blankPrompt.form?.has('context_bias')).toBe(false);
  });
});

describe('transcribeWithMistral — HTTP error classification', () => {
  test('401 and 403 throw a plain Error so the chain does NOT retry a bad key', async () => {
    for (const status of [401, 403]) {
      errorResponse(status);
      const thrown = await transcribeWithMistral(audio(), 'audio/wav').catch((e) => e);
      expect(thrown).not.toBeInstanceOf(ProviderUnavailableError);
      expect(thrown).not.toBeInstanceOf(ProviderInputError);
      expect(thrown.message).toBe('Mistral API key is invalid or unauthorized');
    }
  });

  test('402 fails over as insufficient funds so a sibling provider can still serve', async () => {
    errorResponse(402);

    const thrown = await transcribeWithMistral(audio(), 'audio/wav').catch((e) => e);
    expect(thrown).toBeInstanceOf(ProviderUnavailableError);
    expect(thrown.message).toContain('insufficient funds');
  });

  test('429 and 5xx fail over to the next provider', async () => {
    errorResponse(429);
    const rateLimited = await transcribeWithMistral(audio(), 'audio/wav').catch((e) => e);
    expect(rateLimited).toBeInstanceOf(ProviderUnavailableError);
    expect(rateLimited.message).toContain('rate limit exceeded');

    errorResponse(504);
    const upstream = await transcribeWithMistral(audio(), 'audio/wav').catch((e) => e);
    expect(upstream).toBeInstanceOf(ProviderUnavailableError);
    expect(upstream.message).toContain('upstream 5xx: 504');
  });

  test('other 4xx becomes ProviderInputError carrying the status and body preview', async () => {
    errorResponse(422, 'unsupported container');

    const thrown = await transcribeWithMistral(audio(), 'audio/wav').catch((e) => e);
    expect(thrown).toBeInstanceOf(ProviderInputError);
    expect(thrown.status).toBe(422);
    expect(thrown.message).toContain('unsupported container');
  });

  test('a 200 with an unparsable body fails over instead of 500ing the request', async () => {
    captureRequest('not json');

    const thrown = await transcribeWithMistral(audio(), 'audio/wav').catch((e) => e);
    expect(thrown).toBeInstanceOf(ProviderUnavailableError);
    expect(thrown.message).toContain('malformed 200 response body');
  });
});

describe('transcribeWithMistral — transcript, duration and billing', () => {
  test('bills the audio seconds the usage block reports', async () => {
    captureRequest({ text: 'hello world', language: 'en', usage: { prompt_audio_seconds: 60 } });

    const result = await transcribeWithMistral(audio(), 'audio/wav');
    expect(result.text).toBe('hello world');
    expect(result.language).toBe('en');
    expect(result.durationSeconds).toBe(60);
    expect(result.source).toBe('mistral');
    expect(result.costUsd).toBeCloseTo(0.003, 6);
  });

  test('fails closed: a transcript with no usable duration bills a byte estimate, never $0', async () => {
    for (const usage of [undefined, { prompt_audio_seconds: 0 }]) {
      captureRequest({ text: 'hello', usage });

      const result = await transcribeWithMistral(audio(BYTES_FOR_120_SECONDS), 'audio/wav');
      expect(result.durationSeconds).toBeCloseTo(120, 6);
      expect(result.costUsd).toBeGreaterThan(0);
    }
  });

  test('an empty or whitespace-only transcript returns no_speech at zero duration and zero cost', async () => {
    for (const text of ['', '  \n']) {
      captureRequest({ text, language: 'fr', usage: { prompt_audio_seconds: 42 } });

      const result = await transcribeWithMistral(audio(BYTES_FOR_120_SECONDS), 'audio/wav');
      expect(result.source).toBe('no_speech');
      expect(result.text).toBe('');
      expect(result.durationSeconds).toBe(0);
      expect(result.costUsd).toBe(0);
      expect(result.language).toBe('fr');
    }
  });

  test('the no_speech log event records the upstream duration, and null when there is none', async () => {
    captureRequest({ text: '  \n', language: 'fr', usage: { prompt_audio_seconds: 42 } });
    const reported = await captureNoSpeechEvent(
      () => transcribeWithMistral(audio(BYTES_FOR_120_SECONDS), 'audio/wav'),
    );
    expect(reported.upstreamDurationSeconds).toBe(42);

    // No usage block: the success path would bill a 120 s byte estimate here, so
    // the telemetry must report null rather than either 0 or our own guess.
    captureRequest({ text: '', language: 'fr' });
    const missing = await captureNoSpeechEvent(
      () => transcribeWithMistral(audio(BYTES_FOR_120_SECONDS), 'audio/wav'),
    );
    expect(missing.upstreamDurationSeconds).toBeNull();
    expect(missing.upstreamDurationSeconds).not.toBe(120);
  });
});
