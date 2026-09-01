import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { transcribeWithDeepgram } from './deepgram';
import { ProviderInputError, ProviderUnavailableError } from './types';

const originalFetch = globalThis.fetch;

beforeEach(() => {
  process.env.DEEPGRAM_API_KEY = 'test-key';
});

afterEach(() => {
  globalThis.fetch = originalFetch;
  delete process.env.DEEPGRAM_API_KEY;
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
    params: () => new URL(capturedUrl).searchParams,
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

describe('transcribeWithDeepgram — configuration', () => {
  test('throws a plain Error when DEEPGRAM_API_KEY is not configured, without calling fetch', async () => {
    delete process.env.DEEPGRAM_API_KEY;
    let called = false;
    globalThis.fetch = mock(async () => { called = true; return jsonResponse({}); }) as unknown as typeof fetch;

    await expect(transcribeWithDeepgram(AUDIO, 'audio/wav', 'en-US')).rejects.toThrow('DEEPGRAM_API_KEY not configured');
    expect(called).toBe(false);
  });
});

describe('transcribeWithDeepgram — URL/param building', () => {
  test('the default model (nova-3-general) is sent to Deepgram as the bare "nova-3"', async () => {
    const cap = mockFetchOnce(() => jsonResponse({ results: { channels: [{ alternatives: [{ transcript: 'hi' }] }] } }));
    await transcribeWithDeepgram(AUDIO, 'audio/wav', 'en-US');
    expect(cap.params().get('model')).toBe('nova-3');
  });

  test('nova-2-general maps to the bare "nova-2"; other model ids pass through unchanged', async () => {
    const cap1 = mockFetchOnce(() => jsonResponse({ results: {} }));
    await transcribeWithDeepgram(AUDIO, 'audio/wav', 'en-US', undefined, { model: 'nova-2-general' });
    expect(cap1.params().get('model')).toBe('nova-2');

    const cap2 = mockFetchOnce(() => jsonResponse({ results: {} }));
    await transcribeWithDeepgram(AUDIO, 'audio/wav', 'en-US', undefined, { model: 'nova-3-medical' });
    expect(cap2.params().get('model')).toBe('nova-3-medical');
  });

  test('a region Deepgram does not list is dropped to the base code; detect_language is omitted', async () => {
    // Deepgram lists `fr` and `fr-CA`, never `fr-FR`. Forwarding the region
    // verbatim is the class of bug this resolver exists to stop, so `FR-fr`
    // resolves to the base code rather than a locale the upstream would refuse.
    const cap = mockFetchOnce(() => jsonResponse({ results: {} }));
    await transcribeWithDeepgram(AUDIO, 'audio/wav', 'FR-fr');
    expect(cap.params().get('language')).toBe('fr');
    expect(cap.params().get('detect_language')).toBeNull();
  });

  test('a region Deepgram DOES list is preserved rather than flattened', async () => {
    // The other half of the same rule: dropping `en-GB` to `en` would quietly
    // move British dictation onto the US spelling model.
    const cap = mockFetchOnce(() => jsonResponse({ results: {} }));
    await transcribeWithDeepgram(AUDIO, 'audio/wav', 'en-GB');
    expect(cap.params().get('language')).toBe('en-gb');
    expect(cap.params().get('detect_language')).toBeNull();
  });

  test.each([
    ['undefined', undefined],
    ['"auto" lowercase', 'auto'],
    ['"AUTO" uppercase', 'AUTO'],
  ])('%s language requests detect_language instead of a fixed language', async (_label, language) => {
    const cap = mockFetchOnce(() => jsonResponse({ results: {} }));
    await transcribeWithDeepgram(AUDIO, 'audio/wav', language);
    expect(cap.params().get('detect_language')).toBe('true');
    expect(cap.params().get('language')).toBeNull();
  });

  test('always requests smart_format, utterances, and mip_opt_out', async () => {
    const cap = mockFetchOnce(() => jsonResponse({ results: {} }));
    await transcribeWithDeepgram(AUDIO, 'audio/wav', 'en-US');
    expect(cap.params().get('smart_format')).toBe('true');
    expect(cap.params().get('utterances')).toBe('true');
    expect(cap.params().get('mip_opt_out')).toBe('true');
  });

  test('sends the raw audio bytes and content type through unchanged, with a Token auth header', async () => {
    const cap = mockFetchOnce(() => jsonResponse({ results: {} }));
    await transcribeWithDeepgram(AUDIO, 'audio/mpeg', 'en-US');
    const init = cap.init()!;
    expect(init.method).toBe('POST');
    expect(init.body).toBe(AUDIO);
    expect((init.headers as Record<string, string>)['Content-Type']).toBe('audio/mpeg');
    expect((init.headers as Record<string, string>)['Authorization']).toBe('Token test-key');
  });
});

describe('transcribeWithDeepgram — vocabulary boosting', () => {
  test('a Nova-3 model uses repeated keyterm= params, one per parsed term (not comma-joined)', async () => {
    const cap = mockFetchOnce(() => jsonResponse({ results: {} }));
    await transcribeWithDeepgram(AUDIO, 'audio/wav', 'en-US', 'HyperWhisper, SwiftUI\n- Fly.io');
    expect(cap.params().getAll('keyterm')).toEqual(['HyperWhisper', 'SwiftUI', 'Fly.io']);
    expect(cap.params().getAll('keywords')).toEqual([]);
  });

  test('a Nova-2 model uses the legacy keywords= param instead of keyterm', async () => {
    const cap = mockFetchOnce(() => jsonResponse({ results: {} }));
    await transcribeWithDeepgram(AUDIO, 'audio/wav', 'en-US', 'foo,bar', { model: 'nova-2-general' });
    expect(cap.params().getAll('keywords')).toEqual(['foo', 'bar']);
    expect(cap.params().getAll('keyterm')).toEqual([]);
  });

  test('blank terms and over-length (>50 char) terms are dropped', async () => {
    const overLong = 'x'.repeat(51);
    const cap = mockFetchOnce(() => jsonResponse({ results: {} }));
    await transcribeWithDeepgram(AUDIO, 'audio/wav', 'en-US', `alpha,,   ,${overLong},beta`);
    expect(cap.params().getAll('keyterm')).toEqual(['alpha', 'beta']);
  });

  test('terms beyond the 100-term cap are truncated, preserving order', async () => {
    const terms = Array.from({ length: 105 }, (_, i) => `term${i}`);
    const cap = mockFetchOnce(() => jsonResponse({ results: {} }));
    await transcribeWithDeepgram(AUDIO, 'audio/wav', 'en-US', terms.join(','));
    const sent = cap.params().getAll('keyterm');
    expect(sent.length).toBe(100);
    expect(sent[0]).toBe('term0');
    expect(sent[99]).toBe('term99');
  });

  test('no initial_prompt sends no keyterm/keywords params at all', async () => {
    const cap = mockFetchOnce(() => jsonResponse({ results: {} }));
    await transcribeWithDeepgram(AUDIO, 'audio/wav', 'en-US');
    expect(cap.params().getAll('keyterm')).toEqual([]);
    expect(cap.params().getAll('keywords')).toEqual([]);
  });
});

describe('transcribeWithDeepgram — successful response handling', () => {
  test('maps transcript, detected language, duration, cost, and request id', async () => {
    mockFetchOnce(() => jsonResponse({
      results: { channels: [{ alternatives: [{ transcript: 'hola mundo' }], detected_language: 'es' }] },
      metadata: { duration: 120, request_id: 'req-abc' },
    }));

    const result = await transcribeWithDeepgram(AUDIO, 'audio/wav', 'auto');
    expect(result.text).toBe('hola mundo');
    expect(result.language).toBe('es');
    expect(result.durationSeconds).toBe(120);
    expect(result.source).toBe('deepgram');
    expect(result.requestId).toBe('req-abc');
    // $0.0055/min * 2min = 0.011
    expect(result.costUsd).toBeCloseTo(0.011, 6);
  });

  test('an empty/whitespace transcript is a zero-cost no_speech result, not billed', async () => {
    mockFetchOnce(() => jsonResponse({
      results: { channels: [{ alternatives: [{ transcript: '   ' }], detected_language: 'en' }] },
      metadata: { duration: 45, request_id: 'req-empty' },
    }));

    const result = await transcribeWithDeepgram(AUDIO, 'audio/wav', 'en-US');
    expect(result.source).toBe('no_speech');
    expect(result.costUsd).toBe(0);
    expect(result.durationSeconds).toBe(0);
    expect(result.language).toBe('en');
    expect(result.requestId).toBe('req-empty');
  });

  test('the no_speech log event records the upstream duration, and null when there is none', async () => {
    mockFetchOnce(() => jsonResponse({
      results: { channels: [{ alternatives: [{ transcript: '   ' }] }] },
      metadata: { duration: 45 },
    }));
    const reported = await captureNoSpeechEvent(() => transcribeWithDeepgram(AUDIO, 'audio/wav', 'en-US'));
    expect(reported.upstreamDurationSeconds).toBe(45);

    mockFetchOnce(() => jsonResponse({ results: { channels: [{ alternatives: [{ transcript: '' }] }] } }));
    const missing = await captureNoSpeechEvent(() => transcribeWithDeepgram(AUDIO, 'audio/wav', 'en-US'));
    expect(missing.upstreamDurationSeconds).toBeNull();
  });

  test('a response missing results/channels entirely is treated as no_speech, not a crash', async () => {
    mockFetchOnce(() => jsonResponse({}));
    const result = await transcribeWithDeepgram(AUDIO, 'audio/wav', 'en-US');
    expect(result.source).toBe('no_speech');
    expect(result.costUsd).toBe(0);
  });
});

describe('transcribeWithDeepgram — upstream error mapping', () => {
  test('401 maps to a plain (non-fallback) Error', async () => {
    mockFetchOnce(() => new Response('unauthorized', { status: 401 }));
    await expect(transcribeWithDeepgram(AUDIO, 'audio/wav', 'en-US'))
      .rejects.toThrow('Deepgram API key is invalid or expired');
  });

  test('402 maps to ProviderUnavailableError (billing exhaustion, fails over to a sibling)', async () => {
    mockFetchOnce(() => new Response('payment required', { status: 402 }));
    await expect(transcribeWithDeepgram(AUDIO, 'audio/wav', 'en-US')).rejects.toThrow(ProviderUnavailableError);
  });

  test('429 maps to ProviderUnavailableError (retryable via the fallback chain)', async () => {
    mockFetchOnce(() => new Response('rate limited', { status: 429 }));
    await expect(transcribeWithDeepgram(AUDIO, 'audio/wav', 'en-US')).rejects.toThrow(ProviderUnavailableError);
  });

  test('a 5xx maps to ProviderUnavailableError', async () => {
    mockFetchOnce(() => new Response('boom', { status: 503 }));
    await expect(transcribeWithDeepgram(AUDIO, 'audio/wav', 'en-US')).rejects.toThrow(ProviderUnavailableError);
  });

  test('an unmapped 4xx (e.g. 400 on a rejected language/format) maps to ProviderInputError carrying the status', async () => {
    mockFetchOnce(() => new Response('bad request', { status: 400 }));
    try {
      await transcribeWithDeepgram(AUDIO, 'audio/wav', 'en-US');
      throw new Error('expected transcribeWithDeepgram to throw');
    } catch (error) {
      expect(error).toBeInstanceOf(ProviderInputError);
      expect((error as ProviderInputError).status).toBe(400);
    }
  });

  test('a malformed (non-JSON) 200 response throws ProviderUnavailableError instead of crashing', async () => {
    mockFetchOnce(() => new Response('not json', { status: 200 }));
    await expect(transcribeWithDeepgram(AUDIO, 'audio/wav', 'en-US')).rejects.toThrow(ProviderUnavailableError);
  });

  test('a transport-level failure (e.g. timeout via fetchWithTimeout) surfaces as ProviderUnavailableError', async () => {
    const originalTimeoutEnv = process.env.STT_PROVIDER_TIMEOUT_MS;
    process.env.STT_PROVIDER_TIMEOUT_MS = '20';
    globalThis.fetch = mock(async (_url: string, init?: RequestInit) => new Promise((_resolve, reject) => {
      init?.signal?.addEventListener('abort', () => reject(new DOMException('The operation was aborted', 'AbortError')));
    })) as unknown as typeof fetch;

    try {
      await transcribeWithDeepgram(AUDIO, 'audio/wav', 'en-US');
      throw new Error('expected transcribeWithDeepgram to throw');
    } catch (error) {
      expect(error).toBeInstanceOf(ProviderUnavailableError);
      expect((error as ProviderUnavailableError).kind).toBe('timeout');
    } finally {
      if (originalTimeoutEnv === undefined) delete process.env.STT_PROVIDER_TIMEOUT_MS;
      else process.env.STT_PROVIDER_TIMEOUT_MS = originalTimeoutEnv;
    }
  });
});
