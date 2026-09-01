// Tests for the xAI Grok synchronous multipart STT adapter.
//
// Only global `fetch` is mocked — never a shared module. bun's `mock.module` is
// process-wide and leaks into every other file in the same `bun test src` run.

import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { transcribeWithXaiGrok } from './xai-stt';
import { ProviderInputError, ProviderUnavailableError } from './types';

const originalFetch = globalThis.fetch;
const ENV_KEYS = ['XAI_API_KEY', 'GROK_API_KEY'] as const;
const savedEnv: Record<string, string | undefined> = {};

const audio = (bytes = 1_000) => new ArrayBuffer(bytes);

beforeEach(() => {
  for (const key of ENV_KEYS) savedEnv[key] = process.env[key];
  process.env.XAI_API_KEY = 'test-key';
  delete process.env.GROK_API_KEY;
});

afterEach(() => {
  globalThis.fetch = originalFetch;
  for (const key of ENV_KEYS) {
    if (savedEnv[key] === undefined) delete process.env[key];
    else process.env[key] = savedEnv[key];
  }
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

function errorResponse(status: number, text = 'upstream said no') {
  globalThis.fetch = mock(async () => new Response(text, { status })) as unknown as typeof fetch;
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

describe('transcribeWithXaiGrok — credentials', () => {
  test('a missing key fails over rather than throwing a plain Error', async () => {
    delete process.env.XAI_API_KEY;
    let called = false;
    globalThis.fetch = mock(async () => { called = true; return Response.json({ text: 'hi' }); }) as unknown as typeof fetch;

    const thrown = await transcribeWithXaiGrok(audio(), 'audio/mp3').catch((e) => e);
    expect(thrown).toBeInstanceOf(ProviderUnavailableError);
    expect(thrown.message).toContain('XAI_API_KEY not configured');
    expect(called).toBe(false);
  });

  test('falls back to GROK_API_KEY when XAI_API_KEY is unset', async () => {
    delete process.env.XAI_API_KEY;
    process.env.GROK_API_KEY = 'legacy-key';
    const captured = captureRequest({ text: 'hi', duration: 4 });

    await transcribeWithXaiGrok(audio(), 'audio/mp3');
    expect((captured.headers as Record<string, string>).Authorization).toBe('Bearer legacy-key');
  });
});

describe('transcribeWithXaiGrok — multipart request shape', () => {
  test('sends format=true with the normalised language for a supported locale', async () => {
    const captured = captureRequest({ text: 'hola', duration: 4 });

    await transcribeWithXaiGrok(audio(), 'audio/mp3', 'es-419');
    expect(captured.url).toBe('https://api.x.ai/v1/stt');
    expect(captured.form?.get('format')).toBe('true');
    expect(captured.form?.get('language')).toBe('es');
  });

  test('maps Tagalog (tl) to the Filipino (fil) code the API accepts', async () => {
    const captured = captureRequest({ text: 'kumusta', duration: 4 });

    await transcribeWithXaiGrok(audio(), 'audio/mp3', 'tl-PH');
    expect(captured.form?.get('language')).toBe('fil');
  });

  test('drops formatting entirely for an unsupported language or for "auto"', async () => {
    const unsupported = captureRequest({ text: 'hi', duration: 4 });
    await transcribeWithXaiGrok(audio(), 'audio/mp3', 'sw-KE');
    expect(unsupported.form?.has('language')).toBe(false);
    expect(unsupported.form?.has('format')).toBe(false);

    const auto = captureRequest({ text: 'hi', duration: 4 });
    await transcribeWithXaiGrok(audio(), 'audio/mp3', 'auto');
    expect(auto.form?.has('language')).toBe(false);
    expect(auto.form?.has('format')).toBe(false);
  });

  test('appends the file part last, after the formatting fields xAI requires first', async () => {
    const captured = captureRequest({ text: 'hi', duration: 4 });

    await transcribeWithXaiGrok(audio(), 'audio/mp3', 'de-DE');
    expect([...(captured.form as FormData).keys()]).toEqual(['format', 'language', 'file']);
  });

  test('names the file part from the content type, falling back to .mp3', async () => {
    const wav = captureRequest({ text: 'hi', duration: 4 });
    await transcribeWithXaiGrok(audio(), 'audio/wav');
    expect((wav.form?.get('file') as File).name).toBe('audio.wav');

    const unknown = captureRequest({ text: 'hi', duration: 4 });
    await transcribeWithXaiGrok(audio(), 'application/octet-stream');
    expect((unknown.form?.get('file') as File).name).toBe('audio.mp3');
  });

  test('sends an initial prompt as repeated keyterm fields, never as prompt', async () => {
    const captured = captureRequest({ text: 'hi', duration: 4 });

    await transcribeWithXaiGrok(audio(), 'audio/mp3', undefined, 'HyperWhisper, Drizzle');
    expect(captured.form?.has('prompt')).toBe(false);
    expect(captured.form?.getAll('keyterm')).toEqual(['HyperWhisper', 'Drizzle']);
    // the file part stays last.
    expect([...(captured.form as FormData).keys()]).toEqual(['keyterm', 'keyterm', 'file']);
  });

  test('sends no keyterm field when there is no initial prompt', async () => {
    const captured = captureRequest({ text: 'hi', duration: 4 });

    await transcribeWithXaiGrok(audio(), 'audio/mp3');
    expect(captured.form?.has('keyterm')).toBe(false);
  });

  test('drops terms over 50 characters, de-duplicates, and caps at 100', async () => {
    const captured = captureRequest({ text: 'hi', duration: 4 });
    const tooLong = 'x'.repeat(51);
    const terms = [tooLong, 'Kept', 'kept', ...Array.from({ length: 120 }, (_, i) => `term${i}`)];

    await transcribeWithXaiGrok(audio(), 'audio/mp3', undefined, terms.join(', '));
    const sent = captured.form?.getAll('keyterm') as string[];
    expect(sent.length).toBe(100);
    expect(sent[0]).toBe('Kept');
    expect(sent).not.toContain(tooLong);
    expect(sent.filter((t) => t.toLowerCase() === 'kept').length).toBe(1);
  });
});

describe('transcribeWithXaiGrok — HTTP error classification', () => {
  test('401 and 403 throw a plain Error so the chain does NOT retry a bad key', async () => {
    for (const status of [401, 403]) {
      errorResponse(status);
      const thrown = await transcribeWithXaiGrok(audio(), 'audio/mp3').catch((e) => e);
      expect(thrown).not.toBeInstanceOf(ProviderUnavailableError);
      expect(thrown).not.toBeInstanceOf(ProviderInputError);
      expect(thrown.message).toBe('xAI API key is invalid or unauthorized');
    }
  });

  test('429 and 5xx fail over to the next provider', async () => {
    errorResponse(429);
    const rateLimited = await transcribeWithXaiGrok(audio(), 'audio/mp3').catch((e) => e);
    expect(rateLimited).toBeInstanceOf(ProviderUnavailableError);
    expect(rateLimited.message).toContain('rate limit exceeded');

    errorResponse(500);
    const upstream = await transcribeWithXaiGrok(audio(), 'audio/mp3').catch((e) => e);
    expect(upstream).toBeInstanceOf(ProviderUnavailableError);
    expect(upstream.message).toContain('upstream 5xx: 500');
  });

  test('402 is NOT a failover here — it falls through to ProviderInputError', async () => {
    errorResponse(402, 'credits exhausted');

    const thrown = await transcribeWithXaiGrok(audio(), 'audio/mp3').catch((e) => e);
    expect(thrown).toBeInstanceOf(ProviderInputError);
    expect(thrown.status).toBe(402);
  });

  test('a 200 with an unparsable body fails over instead of 500ing the request', async () => {
    captureRequest('<html>blocked</html>');

    const thrown = await transcribeWithXaiGrok(audio(), 'audio/mp3').catch((e) => e);
    expect(thrown).toBeInstanceOf(ProviderUnavailableError);
    expect(thrown.message).toContain('malformed 200 response body');
  });
});

describe('transcribeWithXaiGrok — transcript, duration and billing', () => {
  test('returns the transcript with the reported duration and bills above $0', async () => {
    captureRequest({ text: 'hello world', duration: 3_600, language: 'en', request_id: 'req-1' });

    const result = await transcribeWithXaiGrok(audio(), 'audio/mp3');
    expect(result.text).toBe('hello world');
    expect(result.durationSeconds).toBe(3_600);
    expect(result.language).toBe('en');
    expect(result.source).toBe('grok');
    expect(result.requestId).toBe('req-1');
    expect(result.costUsd).toBeCloseTo(0.1, 6);
  });

  test('derives the duration from the last word end when the body omits duration', async () => {
    captureRequest({
      text: 'hello world',
      words: [{ start: 0, end: 1.2, text: 'hello' }, { start: 1.2, end: 4.5, text: 'world' }],
    });

    const result = await transcribeWithXaiGrok(audio(), 'audio/mp3');
    expect(result.durationSeconds).toBe(4.5);
    expect(result.costUsd).toBeGreaterThan(0);
  });

  test('falls back to `id` when the body carries no request_id', async () => {
    captureRequest({ text: 'hello', duration: 4, id: 'fallback-id' });

    const result = await transcribeWithXaiGrok(audio(), 'audio/mp3');
    expect(result.requestId).toBe('fallback-id');
  });

  test('reports the requested formatting language when the body echoes none', async () => {
    captureRequest({ text: 'bonjour', duration: 4 });

    const result = await transcribeWithXaiGrok(audio(), 'audio/mp3', 'fr-CA');
    expect(result.language).toBe('fr');
  });

  test('an empty or whitespace-only transcript returns no_speech, keeping the request id', async () => {
    for (const text of ['', ' \t']) {
      captureRequest({ text, duration: 12, request_id: 'req-empty' });

      const result = await transcribeWithXaiGrok(audio(), 'audio/mp3', 'ja');
      expect(result.source).toBe('no_speech');
      expect(result.text).toBe('');
      expect(result.durationSeconds).toBe(0);
      expect(result.costUsd).toBe(0);
      expect(result.language).toBe('ja');
      expect(result.requestId).toBe('req-empty');
    }
  });

  test('the no_speech log event records the upstream duration, and null when there is none', async () => {
    // The incident shape from issue #381: 22.2 s of audio, no text.
    captureRequest({ text: '', duration: 22.2, request_id: 'req-empty' });
    const reported = await captureNoSpeechEvent(() => transcribeWithXaiGrok(audio(), 'audio/mp3', 'en'));
    expect(reported.upstreamDurationSeconds).toBe(22.2);

    captureRequest({ text: '', request_id: 'req-empty' });
    const missing = await captureNoSpeechEvent(() => transcribeWithXaiGrok(audio(), 'audio/mp3', 'en'));
    expect(missing.upstreamDurationSeconds).toBeNull();
  });
});

describe('transcribeWithXaiGrok — empty-transcript failover (issue #381)', () => {
  test('refuses an empty transcript with a reported duration on attempt 1, and logs no no_speech', async () => {
    // The incident shape: 22.2 s of audio submitted, no text returned.
    captureRequest({ text: '', duration: 22.2, request_id: 'req-empty' });

    const logged: unknown[][] = [];
    const originalLog = console.log;
    console.log = ((...args: unknown[]) => { logged.push(args); }) as typeof console.log;
    let thrown: unknown;
    try {
      await transcribeWithXaiGrok(audio(), 'audio/mp3', 'en', undefined, { attempt: 1 });
    } catch (error) {
      thrown = error;
    } finally {
      console.log = originalLog;
    }

    expect(thrown).toBeInstanceOf(ProviderUnavailableError);
    expect((thrown as ProviderUnavailableError).kind).toBe('bad_response');
    expect((thrown as Error).message).toContain('22.2');
    // A refusal is not a no_speech outcome. Logging one would corrupt the very
    // rate `upstreamDurationSeconds` was added to measure.
    expect(logged.some((args) => args[0] === 'provider.no_speech')).toBe(false);
  });

  test('the same body on attempt 2 resolves as no_speech at zero cost (one extra call, never two)', async () => {
    captureRequest({ text: '', duration: 22.2, request_id: 'req-empty' });

    const result = await transcribeWithXaiGrok(audio(), 'audio/mp3', 'en', undefined, { attempt: 2 });
    expect(result.source).toBe('no_speech');
    expect(result.costUsd).toBe(0);
    expect(result.text).toBe('');
  });

  test('an empty transcript with no reported duration resolves as no_speech even on attempt 1', async () => {
    captureRequest({ text: '', request_id: 'req-empty' });

    const result = await transcribeWithXaiGrok(audio(), 'audio/mp3', 'en', undefined, { attempt: 1 });
    expect(result.source).toBe('no_speech');
    expect(result.costUsd).toBe(0);
  });
});
