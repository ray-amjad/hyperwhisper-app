// Tests for the Groq Whisper synchronous multipart STT adapter.
//
// Only global `fetch` is mocked — never a shared module. bun's `mock.module` is
// process-wide and leaks into every other file in the same `bun test src` run.

import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { transcribeWithGroq } from './groq';
import { EmptyTranscriptError, ProviderInputError, ProviderUnavailableError } from './types';

const originalFetch = globalThis.fetch;
let savedKey: string | undefined;

const audio = (bytes = 1_000) => new ArrayBuffer(bytes);

beforeEach(() => {
  savedKey = process.env.GROQ_API_KEY;
  process.env.GROQ_API_KEY = 'test-key';
});

afterEach(() => {
  globalThis.fetch = originalFetch;
  if (savedKey === undefined) delete process.env.GROQ_API_KEY;
  else process.env.GROQ_API_KEY = savedKey;
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
  let called = false;
  globalThis.fetch = mock(async () => {
    called = true;
    return new Response(text, { status });
  }) as unknown as typeof fetch;
  return () => called;
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

describe('transcribeWithGroq — multipart request shape', () => {
  test('throws a plain Error (not a fallback signal) when GROQ_API_KEY is missing', async () => {
    delete process.env.GROQ_API_KEY;
    const wasCalled = errorResponse(200);

    const thrown = await transcribeWithGroq(audio(), 'audio/wav').catch((e) => e);
    expect(thrown).not.toBeInstanceOf(ProviderUnavailableError);
    expect(thrown.message).toBe('GROQ_API_KEY not configured');
    expect(wasCalled()).toBe(false);
  });

  test('posts verbose_json to the Groq transcriptions endpoint with a bearer key', async () => {
    process.env.GROQ_API_KEY = 'gsk-unit-test';
    const captured = captureRequest({ text: 'hi', duration: 5 });

    await transcribeWithGroq(audio(), 'audio/wav');
    expect(captured.url).toBe('https://api.groq.com/openai/v1/audio/transcriptions');
    expect(captured.form?.get('response_format')).toBe('verbose_json');
    expect((captured.headers as Record<string, string>).Authorization).toBe('Bearer gsk-unit-test');
  });

  test('defaults to whisper-large-v3-turbo and honours a model from the context', async () => {
    const byDefault = captureRequest({ text: 'hi', duration: 5 });
    await transcribeWithGroq(audio(), 'audio/wav');
    expect(byDefault.form?.get('model')).toBe('whisper-large-v3-turbo');

    const override = captureRequest({ text: 'hi', duration: 5 });
    await transcribeWithGroq(audio(), 'audio/wav', undefined, undefined, { model: 'whisper-large-v3' });
    expect(override.form?.get('model')).toBe('whisper-large-v3');
  });

  test('strips a BCP-47 region subtag, and omits the field for "auto"', async () => {
    const regional = captureRequest({ text: 'hi', duration: 5 });
    await transcribeWithGroq(audio(), 'audio/wav', 'en_US');
    expect(regional.form?.get('language')).toBe('en');

    const auto = captureRequest({ text: 'hi', duration: 5 });
    await transcribeWithGroq(audio(), 'audio/wav', 'auto');
    expect(auto.form?.has('language')).toBe(false);
  });

  test('forwards an initial prompt and names the file part from the content type', async () => {
    const captured = captureRequest({ text: 'hi', duration: 5 });

    await transcribeWithGroq(audio(), 'audio/flac', undefined, 'HyperWhisper');
    expect(captured.form?.get('prompt')).toBe('HyperWhisper');
    expect((captured.form?.get('file') as File).name).toBe('audio.flac');
  });
});

describe('transcribeWithGroq — HTTP error classification', () => {
  test('403 is treated as an edge-region block and fails over, not as a bad key', async () => {
    errorResponse(403);

    const thrown = await transcribeWithGroq(audio(), 'audio/wav').catch((e) => e);
    expect(thrown).toBeInstanceOf(ProviderUnavailableError);
    expect(thrown.message).toContain('403 Forbidden - likely edge region blocked');
  });

  test('401 throws a plain Error so the chain does NOT retry a bad key', async () => {
    errorResponse(401);

    const thrown = await transcribeWithGroq(audio(), 'audio/wav').catch((e) => e);
    expect(thrown).not.toBeInstanceOf(ProviderUnavailableError);
    expect(thrown).not.toBeInstanceOf(ProviderInputError);
    expect(thrown.message).toBe('Groq API key is invalid');
  });

  test('429 and 5xx fail over to the next provider', async () => {
    errorResponse(429);
    const rateLimited = await transcribeWithGroq(audio(), 'audio/wav').catch((e) => e);
    expect(rateLimited).toBeInstanceOf(ProviderUnavailableError);
    expect(rateLimited.message).toContain('rate limit exceeded');

    errorResponse(502);
    const upstream = await transcribeWithGroq(audio(), 'audio/wav').catch((e) => e);
    expect(upstream).toBeInstanceOf(ProviderUnavailableError);
    expect(upstream.message).toContain('upstream 5xx: 502');
  });

  test('other 4xx becomes ProviderInputError carrying the status', async () => {
    errorResponse(400, 'bad language');

    const thrown = await transcribeWithGroq(audio(), 'audio/wav').catch((e) => e);
    expect(thrown).toBeInstanceOf(ProviderInputError);
    expect(thrown.status).toBe(400);
  });

  test('a 200 with an unparsable body fails over instead of 500ing the request', async () => {
    captureRequest('not json');

    const thrown = await transcribeWithGroq(audio(), 'audio/wav').catch((e) => e);
    expect(thrown).toBeInstanceOf(ProviderUnavailableError);
    expect(thrown.message).toContain('malformed 200 response body');
  });
});

describe('transcribeWithGroq — transcript, duration and billing', () => {
  test('returns the transcript, language and duration the upstream reports', async () => {
    captureRequest({ text: 'hello world', language: 'en', duration: 60 });

    const result = await transcribeWithGroq(audio(), 'audio/wav');
    expect(result.text).toBe('hello world');
    expect(result.language).toBe('en');
    expect(result.durationSeconds).toBe(60);
    expect(result.source).toBe('groq');
  });

  test('bills whisper-large-v3 above turbo for the same duration', async () => {
    captureRequest({ text: 'hello', duration: 3_600 });
    const turbo = await transcribeWithGroq(audio(), 'audio/wav');

    captureRequest({ text: 'hello', duration: 3_600 });
    const large = await transcribeWithGroq(audio(), 'audio/wav', undefined, undefined, { model: 'whisper-large-v3' });

    expect(turbo.costUsd).toBeGreaterThan(0);
    expect(large.costUsd).toBeGreaterThan(turbo.costUsd);
  });

  test('a transcript with no duration still bills the 10-second minimum, not $0', async () => {
    captureRequest({ text: 'hello', duration: 0 });

    const result = await transcribeWithGroq(audio(), 'audio/wav');
    expect(result.durationSeconds).toBe(0);
    expect(result.costUsd).toBeGreaterThan(0);
  });

  test('an empty or whitespace-only transcript returns no_speech at zero duration and zero cost', async () => {
    for (const text of ['', '  ']) {
      captureRequest({ text, language: 'en', duration: 30 });

      const result = await transcribeWithGroq(audio(), 'audio/wav');
      expect(result.source).toBe('no_speech');
      expect(result.text).toBe('');
      expect(result.durationSeconds).toBe(0);
      expect(result.costUsd).toBe(0);
      expect(result.language).toBe('en');
    }
  });

  test('the no_speech log event records the upstream duration, and null when there is none', async () => {
    captureRequest({ text: '', language: 'en', duration: 30 });
    const reported = await captureNoSpeechEvent(() => transcribeWithGroq(audio(), 'audio/wav'));
    expect(reported.upstreamDurationSeconds).toBe(30);

    captureRequest({ text: '', language: 'en' });
    const missing = await captureNoSpeechEvent(() => transcribeWithGroq(audio(), 'audio/wav'));
    expect(missing.upstreamDurationSeconds).toBeNull();
  });
});

describe('transcribeWithGroq — empty-transcript failover (issue #381)', () => {
  test('refuses when the ROUTE grants the failover, and carries the no_speech it would have returned', async () => {
    captureRequest({ text: '', language: 'en', duration: 30 });

    let thrown: unknown;
    try {
      await transcribeWithGroq(audio(), 'audio/wav', undefined, undefined, { mayRefuseEmptyTranscript: true });
    } catch (error) {
      thrown = error;
    }

    expect(thrown).toBeInstanceOf(EmptyTranscriptError);
    const refusal = thrown as EmptyTranscriptError;
    // Still a ProviderUnavailableError, so the route's chain walk, its
    // attemptFailures and its /latency row are unchanged by construction.
    expect(refusal).toBeInstanceOf(ProviderUnavailableError);
    expect(refusal.kind).toBe('bad_response');
    expect(refusal.message).toContain('30');
    expect(refusal.upstreamDurationSeconds).toBe(30);
    // The request's floor: exactly the result the caller would have got had the
    // adapter not refused, so no sibling failure can turn it into an error.
    expect(refusal.noSpeechResult).toMatchObject({
      text: '',
      source: 'no_speech',
      costUsd: 0,
      durationSeconds: 0,
    });
  });

  test('a refusal still logs no_speech, with the duration and refused: true', async () => {
    // Goal 4 of the spec is "the production rate of no_speech per provider becomes
    // measurable". For the three covered providers the refusal path IS the common
    // path, so an event that fires only when the adapter does NOT refuse counts
    // nothing. Without this the field is dead by construction.
    captureRequest({ text: '', language: 'en', duration: 30 });

    const reported = await captureNoSpeechEvent(async () => {
      await transcribeWithGroq(audio(), 'audio/wav', undefined, undefined, { mayRefuseEmptyTranscript: true })
        .catch(() => undefined);
    });
    expect(reported.upstreamDurationSeconds).toBe(30);
    expect(reported.refused).toBe(true);
  });

  test('does NOT refuse without the grant, even on attempt 1 — a chain can filter down to one provider', async () => {
    // The gate is "the route says a sibling is there", never `attempt === 1`.
    // A geo-degraded chain's only provider is still its first attempt, and
    // refusing there would return 429 for what is a benign no_speech.
    captureRequest({ text: '', language: 'en', duration: 30 });

    const result = await transcribeWithGroq(audio(), 'audio/wav', undefined, undefined, { attempt: 1 });
    expect(result.source).toBe('no_speech');
    expect(result.costUsd).toBe(0);
    expect(result.text).toBe('');
  });

  test('an empty transcript with no reported duration resolves as no_speech even with the grant', async () => {
    captureRequest({ text: '', language: 'en' });

    const result = await transcribeWithGroq(audio(), 'audio/wav', undefined, undefined, { mayRefuseEmptyTranscript: true });
    expect(result.source).toBe('no_speech');
    expect(result.costUsd).toBe(0);
  });
});
