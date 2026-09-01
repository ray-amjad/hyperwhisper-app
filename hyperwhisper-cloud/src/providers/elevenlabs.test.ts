import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { transcribeWithElevenLabs } from './elevenlabs';
import { ProviderInputError, ProviderUnavailableError } from './types';

const originalFetch = globalThis.fetch;

beforeEach(() => {
  process.env.ELEVENLABS_API_KEY = 'test-key';
});

afterEach(() => {
  globalThis.fetch = originalFetch;
  delete process.env.ELEVENLABS_API_KEY;
  delete process.env.ELEVENLABS_ZERO_RETENTION;
});

const AUDIO = new ArrayBuffer(1_000);

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
}

function mockFetchOnce(handler: (url: string, init?: RequestInit) => Response | Promise<Response>) {
  let capturedUrl = '';
  let capturedInit: RequestInit | undefined;
  globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
    capturedUrl = String(input);
    capturedInit = init;
    return handler(capturedUrl, init);
  }) as unknown as typeof fetch;
  return {
    url: () => capturedUrl,
    init: () => capturedInit,
    formData: () => capturedInit!.body as FormData,
  };
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

describe('transcribeWithElevenLabs — configuration', () => {
  test('throws a plain Error when ELEVENLABS_API_KEY is not configured, without calling fetch', async () => {
    delete process.env.ELEVENLABS_API_KEY;
    let called = false;
    globalThis.fetch = mock(async () => { called = true; return jsonResponse({}); }) as unknown as typeof fetch;

    await expect(transcribeWithElevenLabs(AUDIO, 'audio/wav', 'en-US')).rejects.toThrow('ELEVENLABS_API_KEY not configured');
    expect(called).toBe(false);
  });

  test('defaults to scribe_v2 and sends the xi-api-key header plus a real User-Agent', async () => {
    const cap = mockFetchOnce(() => jsonResponse({ text: 'hi' }));
    await transcribeWithElevenLabs(AUDIO, 'audio/wav', 'en-US');
    const init = cap.init()!;
    expect((init.headers as Record<string, string>)['xi-api-key']).toBe('test-key');
    expect((init.headers as Record<string, string>)['User-Agent']).toBe('hyperwhisper-cloud/1.0');
    expect(cap.formData().get('model_id')).toBe('scribe_v2');
  });

  test('an explicit model in the request context overrides the scribe_v2 default', async () => {
    const cap = mockFetchOnce(() => jsonResponse({ text: 'hi' }));
    await transcribeWithElevenLabs(AUDIO, 'audio/wav', 'en-US', undefined, { model: 'scribe_v1' });
    expect(cap.formData().get('model_id')).toBe('scribe_v1');
  });

  test('ELEVENLABS_ZERO_RETENTION=true appends enable_logging=false to the URL; unset omits it', async () => {
    process.env.ELEVENLABS_ZERO_RETENTION = 'true';
    const cap1 = mockFetchOnce(() => jsonResponse({ text: 'hi' }));
    await transcribeWithElevenLabs(AUDIO, 'audio/wav', 'en-US');
    expect(cap1.url()).toContain('enable_logging=false');

    delete process.env.ELEVENLABS_ZERO_RETENTION;
    const cap2 = mockFetchOnce(() => jsonResponse({ text: 'hi' }));
    await transcribeWithElevenLabs(AUDIO, 'audio/wav', 'en-US');
    expect(cap2.url()).not.toContain('enable_logging');
  });
});

describe('transcribeWithElevenLabs — language handling', () => {
  test('a hyphenated BCP-47 locale is stripped to its bare primary subtag', async () => {
    const cap = mockFetchOnce(() => jsonResponse({ text: 'hi' }));
    await transcribeWithElevenLabs(AUDIO, 'audio/wav', 'EN-us');
    expect(cap.formData().get('language_code')).toBe('en');
  });

  test('"auto" (any case) sends no language_code at all', async () => {
    const cap1 = mockFetchOnce(() => jsonResponse({ text: 'hi' }));
    await transcribeWithElevenLabs(AUDIO, 'audio/wav', 'auto');
    expect(cap1.formData().get('language_code')).toBeNull();

    const cap2 = mockFetchOnce(() => jsonResponse({ text: 'hi' }));
    await transcribeWithElevenLabs(AUDIO, 'audio/wav', 'AUTO');
    expect(cap2.formData().get('language_code')).toBeNull();
  });

  test('an undefined language sends no language_code', async () => {
    const cap = mockFetchOnce(() => jsonResponse({ text: 'hi' }));
    await transcribeWithElevenLabs(AUDIO, 'audio/wav', undefined);
    expect(cap.formData().get('language_code')).toBeNull();
  });
});

describe('transcribeWithElevenLabs — keyterm biasing', () => {
  test('scribe_v2 sends one repeated keyterms field per parsed term, not a JSON array', async () => {
    const cap = mockFetchOnce(() => jsonResponse({ text: 'hi' }));
    await transcribeWithElevenLabs(AUDIO, 'audio/wav', 'en-US', 'HyperWhisper, SwiftUI\n- Fly.io');
    expect(cap.formData().getAll('keyterms')).toEqual(['HyperWhisper', 'SwiftUI', 'Fly.io']);
  });

  test('scribe_v1 drops keyterms entirely (no biasing support), even with an initialPrompt', async () => {
    const cap = mockFetchOnce(() => jsonResponse({ text: 'hi' }));
    await transcribeWithElevenLabs(AUDIO, 'audio/wav', 'en-US', 'alpha,beta', { model: 'scribe_v1' });
    expect(cap.formData().getAll('keyterms')).toEqual([]);
  });

  test('terms over 50 chars or over 5 words are dropped; blanks are dropped', async () => {
    const overLong = 'x'.repeat(51);
    const overWords = 'one two three four five six';
    const cap = mockFetchOnce(() => jsonResponse({ text: 'hi' }));
    await transcribeWithElevenLabs(AUDIO, 'audio/wav', 'en-US', `alpha,,   ,${overLong},${overWords},beta`);
    expect(cap.formData().getAll('keyterms')).toEqual(['alpha', 'beta']);
  });

  test('terms beyond the 100-term cap are truncated, preserving order', async () => {
    const terms = Array.from({ length: 105 }, (_, i) => `term${i}`);
    const cap = mockFetchOnce(() => jsonResponse({ text: 'hi' }));
    await transcribeWithElevenLabs(AUDIO, 'audio/wav', 'en-US', terms.join(','));
    const sent = cap.formData().getAll('keyterms');
    expect(sent.length).toBe(100);
    expect(sent[0]).toBe('term0');
    expect(sent[99]).toBe('term99');
  });

  test('no initialPrompt sends no keyterms params, and cost carries no surcharge', async () => {
    const cap = mockFetchOnce(() => jsonResponse({ text: 'hi', words: [{ start: 0, end: 60, text: 'hi' }] }));
    const result = await transcribeWithElevenLabs(AUDIO, 'audio/wav', 'en-US');
    expect(cap.formData().getAll('keyterms')).toEqual([]);
    // $0.00983/min * 1min, no +20% surcharge
    expect(result.costUsd).toBeCloseTo(0.00983, 6);
  });
});

describe('transcribeWithElevenLabs — successful response handling', () => {
  test('maps transcript, language, duration from the last word timing, and applies the keyterm cost surcharge', async () => {
    mockFetchOnce(() => jsonResponse({
      text: 'hola mundo',
      language_code: 'es',
      words: [{ start: 0, end: 30, text: 'hola' }, { start: 30, end: 60, text: 'mundo' }],
    }));

    const result = await transcribeWithElevenLabs(AUDIO, 'audio/wav', 'auto', 'hola,mundo');
    expect(result.text).toBe('hola mundo');
    expect(result.language).toBe('es');
    expect(result.durationSeconds).toBe(60);
    expect(result.source).toBe('elevenlabs');
    // $0.00983/min * 1min * 1.2 (keyterm surcharge)
    expect(result.costUsd).toBeCloseTo(0.011796, 6);
  });

  test('an empty transcript is a zero-cost no_speech result, not billed', async () => {
    mockFetchOnce(() => jsonResponse({ text: '   ', language_code: 'en' }));
    const result = await transcribeWithElevenLabs(AUDIO, 'audio/wav', 'en-US');
    expect(result.source).toBe('no_speech');
    expect(result.costUsd).toBe(0);
    expect(result.durationSeconds).toBe(0);
  });

  test('the no_speech log event records a null upstream duration, never 0 and never absent', async () => {
    // This adapter derives duration from the last word's end time, so an empty
    // transcript structurally leaves it 0. Logging 0 would read as "the upstream
    // said there was no audio"; the truth is that it reported nothing at all.
    mockFetchOnce(() => jsonResponse({ text: '   ', language_code: 'en' }));
    const details = await captureNoSpeechEvent(() => transcribeWithElevenLabs(AUDIO, 'audio/wav', 'en-US'));
    expect(details.upstreamDurationSeconds).toBeNull();
    // `undefined` would be dropped by JSON serialization, making "no duration"
    // indistinguishable from "the field was never added".
    expect(JSON.parse(JSON.stringify(details))).toHaveProperty('upstreamDurationSeconds', null);
  });

  test('a response with no words array defaults duration to 0', async () => {
    mockFetchOnce(() => jsonResponse({ text: 'hi' }));
    const result = await transcribeWithElevenLabs(AUDIO, 'audio/wav', 'en-US');
    expect(result.durationSeconds).toBe(0);
    expect(result.costUsd).toBe(0);
  });
});

describe('transcribeWithElevenLabs — upstream error mapping', () => {
  test('401 maps to a plain (non-fallback) Error', async () => {
    mockFetchOnce(() => new Response('unauthorized', { status: 401 }));
    await expect(transcribeWithElevenLabs(AUDIO, 'audio/wav', 'en-US'))
      .rejects.toThrow('ElevenLabs API key is invalid');
  });

  test('429 maps to ProviderUnavailableError with kind rate_limit (fails over to a sibling)', async () => {
    mockFetchOnce(() => new Response('rate limited', { status: 429 }));
    try {
      await transcribeWithElevenLabs(AUDIO, 'audio/wav', 'en-US');
      throw new Error('expected transcribeWithElevenLabs to throw');
    } catch (error) {
      expect(error).toBeInstanceOf(ProviderUnavailableError);
      expect((error as ProviderUnavailableError).kind).toBe('rate_limit');
    }
  });

  test('a 5xx maps to ProviderUnavailableError with kind upstream_5xx', async () => {
    mockFetchOnce(() => new Response('boom', { status: 503 }));
    try {
      await transcribeWithElevenLabs(AUDIO, 'audio/wav', 'en-US');
      throw new Error('expected transcribeWithElevenLabs to throw');
    } catch (error) {
      expect(error).toBeInstanceOf(ProviderUnavailableError);
      expect((error as ProviderUnavailableError).kind).toBe('upstream_5xx');
    }
  });

  test('an unmapped 4xx (e.g. 400 on a rejected language/format) maps to ProviderInputError carrying the status, so a sibling can pick it up', async () => {
    mockFetchOnce(() => new Response('bad request', { status: 400 }));
    try {
      await transcribeWithElevenLabs(AUDIO, 'audio/wav', 'en-US');
      throw new Error('expected transcribeWithElevenLabs to throw');
    } catch (error) {
      expect(error).toBeInstanceOf(ProviderInputError);
      expect((error as ProviderInputError).status).toBe(400);
    }
  });

  test('a geo-blocked 200 OK with an empty body maps to ProviderUnavailableError instead of crashing on the missing text', async () => {
    mockFetchOnce(() => new Response('', { status: 200, headers: { 'content-type': 'text/html' } }));
    try {
      await transcribeWithElevenLabs(AUDIO, 'audio/wav', 'en-US');
      throw new Error('expected transcribeWithElevenLabs to throw');
    } catch (error) {
      expect(error).toBeInstanceOf(ProviderUnavailableError);
      expect((error as ProviderUnavailableError).kind).toBe('bad_response');
    }
  });

  test('a geo-blocked 200 OK with an HTML FAQ body (non-JSON) maps to ProviderUnavailableError instead of crashing on JSON.parse', async () => {
    mockFetchOnce(() => new Response('<html>Do you restrict access...</html>', { status: 200, headers: { 'content-type': 'text/html' } }));
    try {
      await transcribeWithElevenLabs(AUDIO, 'audio/wav', 'en-US');
      throw new Error('expected transcribeWithElevenLabs to throw');
    } catch (error) {
      expect(error).toBeInstanceOf(ProviderUnavailableError);
      expect((error as ProviderUnavailableError).kind).toBe('bad_response');
    }
  });

  test('a transport-level failure (e.g. timeout via fetchWithTimeout) surfaces as ProviderUnavailableError with kind timeout', async () => {
    const originalTimeoutEnv = process.env.STT_PROVIDER_TIMEOUT_MS;
    process.env.STT_PROVIDER_TIMEOUT_MS = '20';
    globalThis.fetch = mock(async (_url: string, init?: RequestInit) => new Promise((_resolve, reject) => {
      init?.signal?.addEventListener('abort', () => reject(new DOMException('The operation was aborted', 'AbortError')));
    })) as unknown as typeof fetch;

    try {
      await transcribeWithElevenLabs(AUDIO, 'audio/wav', 'en-US');
      throw new Error('expected transcribeWithElevenLabs to throw');
    } catch (error) {
      expect(error).toBeInstanceOf(ProviderUnavailableError);
      expect((error as ProviderUnavailableError).kind).toBe('timeout');
    } finally {
      if (originalTimeoutEnv === undefined) delete process.env.STT_PROVIDER_TIMEOUT_MS;
      else process.env.STT_PROVIDER_TIMEOUT_MS = originalTimeoutEnv;
    }
  });
});

describe('transcribeWithElevenLabs — can never refuse an empty transcript (issue #381)', () => {
  test('an empty transcript at attempt 1 still resolves as no_speech', async () => {
    // ElevenLabs terminates all three covered fallback chains. If it could ever
    // throw on an empty transcript, a genuinely silent recording would walk a
    // chain to exhaustion instead of returning "No speech detected".
    mockFetchOnce(() => jsonResponse({ text: '   ', language_code: 'en' }));

    const result = await transcribeWithElevenLabs(AUDIO, 'audio/wav', 'en-US', undefined, { attempt: 1 });
    expect(result.source).toBe('no_speech');
    expect(result.costUsd).toBe(0);
    expect(result.durationSeconds).toBe(0);
  });
});
