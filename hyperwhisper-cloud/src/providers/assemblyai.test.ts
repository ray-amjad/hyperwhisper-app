import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { hasExplicitLanguage, SYNC_ELIGIBLE_ESTIMATED_SECONDS, transcribeWithAssemblyAI } from './assemblyai';
import { ProviderInputError, ProviderUnavailableError } from './types';

const originalFetch = globalThis.fetch;

beforeEach(() => {
  process.env.ASSEMBLYAI_API_KEY = 'test-key';
});

afterEach(() => {
  globalThis.fetch = originalFetch;
  delete process.env.ASSEMBLYAI_API_KEY;
});

const SYNC_URL = 'https://sync.assemblyai.com/v1/transcribe';
const UPLOAD_URL = 'https://api.assemblyai.com/v2/upload';
const CREATE_URL = 'https://api.assemblyai.com/v2/transcript';

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
}

// 48,000 bytes -> a clean 6s estimate at the 480,000 bytes/min heuristic
// (estimateSecondsFromBytes / BYTES_PER_MINUTE_ESTIMATE). Well under the
// sync-eligible threshold, and gives a predictable fallback-estimate value.
const SMALL_AUDIO = new ArrayBuffer(48_000);
// 900,000 bytes -> ~112.5s estimate, clearing the 100s sync-eligible
// threshold, so the sync fast path is skipped entirely.
const LARGE_AUDIO = new ArrayBuffer(900_000);

type Call = { url: string; method: string; body?: unknown };

/** Routes upload -> create -> poll -> delete for the async flow. Each poll
 * consumes one entry from `pollBodies` (the last repeats if polled more than
 * provided). Passing an error `uploadStatus`/`createStatus` short-circuits
 * the flow before the poll loop's real sleep — used by tests that only care
 * about request shape or fallback triggering, not full completion. */
function mockAsyncFlow(opts: {
  pollBodies: Array<{ status: number; body?: unknown }>;
  uploadStatus?: { status: number; body?: unknown };
  createStatus?: { status: number; body?: unknown };
}) {
  const calls: Call[] = [];
  let pollIndex = 0;
  globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const method = init?.method || 'GET';

    if (url === UPLOAD_URL) {
      calls.push({ url, method });
      const { status, body } = opts.uploadStatus ?? { status: 200, body: { upload_url: 'https://cdn.assemblyai.com/upload/abc' } };
      return status === 200 ? jsonResponse(body) : new Response(JSON.stringify(body ?? {}), { status });
    }
    if (url === CREATE_URL && method === 'POST') {
      const parsedBody = JSON.parse(init!.body as string);
      calls.push({ url, method, body: parsedBody });
      const { status, body } = opts.createStatus ?? { status: 200, body: { id: 'transcript-123' } };
      return status === 200 ? jsonResponse(body) : new Response(JSON.stringify(body ?? {}), { status });
    }
    if (url.startsWith(`${CREATE_URL}/`) && method === 'GET') {
      calls.push({ url, method });
      const entry = opts.pollBodies[Math.min(pollIndex, opts.pollBodies.length - 1)];
      pollIndex += 1;
      return entry.status === 200 ? jsonResponse(entry.body) : new Response(JSON.stringify(entry.body ?? {}), { status: entry.status });
    }
    if (url.startsWith(`${CREATE_URL}/`) && method === 'DELETE') {
      calls.push({ url, method });
      return new Response(null, { status: 200 });
    }
    throw new Error(`Unexpected fetch: ${method} ${url}`);
  }) as unknown as typeof fetch;
  return calls;
}

describe('hasExplicitLanguage', () => {
  test('true for a real language code', () => {
    expect(hasExplicitLanguage('fr-FR')).toBe(true);
  });

  test('false for undefined, blank, and "auto" (case-insensitive)', () => {
    expect(hasExplicitLanguage(undefined)).toBe(false);
    expect(hasExplicitLanguage('   ')).toBe(false);
    expect(hasExplicitLanguage('auto')).toBe(false);
    expect(hasExplicitLanguage('AUTO')).toBe(false);
  });

  test('the sync-eligible threshold is 20s below the 120s sync hard cap', () => {
    expect(SYNC_ELIGIBLE_ESTIMATED_SECONDS).toBe(100);
  });
});

describe('transcribeWithAssemblyAI — sync fast path eligibility', () => {
  test('an explicit language + WAV + short clip goes straight to sync, never touching upload/create', async () => {
    const calls: Call[] = [];
    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      calls.push({ url: String(input), method: 'POST' });
      return jsonResponse({ text: 'hello world', audio_duration_ms: 3000 });
    }) as unknown as typeof fetch;

    const result = await transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/wav', 'en-US');
    expect(calls).toEqual([{ url: SYNC_URL, method: 'POST' }]);
    expect(result.source).toBe('assemblyai');
    expect(result.model).toBe('universal-3-5-pro');
  });

  // The remaining eligibility gates are asserted by which endpoint gets hit
  // first, not by full completion — an immediate upload-phase error
  // short-circuits before the poll loop's real sleep.
  test('an absent/auto language skips sync and goes to async upload', async () => {
    const calls = mockAsyncFlow({ pollBodies: [], uploadStatus: { status: 500, body: {} } });
    await expect(transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/wav', 'auto')).rejects.toThrow(ProviderUnavailableError);
    expect(calls).toEqual([{ url: UPLOAD_URL, method: 'POST' }]);
  });

  test('a non-WAV content type skips sync and goes to async upload', async () => {
    const calls = mockAsyncFlow({ pollBodies: [], uploadStatus: { status: 500, body: {} } });
    await expect(transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/mpeg', 'en-US')).rejects.toThrow(ProviderUnavailableError);
    expect(calls).toEqual([{ url: UPLOAD_URL, method: 'POST' }]);
  });

  test('medical domain skips sync even with an explicit language and WAV', async () => {
    const calls = mockAsyncFlow({ pollBodies: [], uploadStatus: { status: 500, body: {} } });
    await expect(transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/wav', 'en-US', undefined, { domain: 'medical' }))
      .rejects.toThrow(ProviderUnavailableError);
    expect(calls).toEqual([{ url: UPLOAD_URL, method: 'POST' }]);
  });

  test('a clip whose byte-size estimate clears the sync threshold skips sync', async () => {
    const calls = mockAsyncFlow({ pollBodies: [], uploadStatus: { status: 500, body: {} } });
    await expect(transcribeWithAssemblyAI(LARGE_AUDIO, 'audio/wav', 'en-US')).rejects.toThrow(ProviderUnavailableError);
    expect(calls).toEqual([{ url: UPLOAD_URL, method: 'POST' }]);
  });
});

describe('transcribeWithAssemblyAI — sync fast path behavior', () => {
  test('a successful sync transcript reports the sync-only model and computed cost', async () => {
    globalThis.fetch = mock(async () => jsonResponse({ text: 'bonjour', audio_duration_ms: 60_000 })) as unknown as typeof fetch;

    const result = await transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/wav', 'fr-FR');
    expect(result.text).toBe('bonjour');
    expect(result.language).toBe('fr-FR');
    expect(result.durationSeconds).toBe(60);
    // sync rate is $0.45/hr-equivalent -> 0.45/60 per minute; 60s = 1 min.
    expect(result.costUsd).toBeCloseTo(0.0075, 6);
  });

  test('an empty sync transcript is a zero-cost no_speech result with no async fallback', async () => {
    const calls: Call[] = [];
    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      calls.push({ url: String(input), method: 'POST' });
      return jsonResponse({ text: '   ', audio_duration_ms: 1000 });
    }) as unknown as typeof fetch;

    const result = await transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/wav', 'en-US');
    expect(result.source).toBe('no_speech');
    expect(result.costUsd).toBe(0);
    expect(calls).toEqual([{ url: SYNC_URL, method: 'POST' }]);
  });

  test('a missing/zero sync duration falls back to the byte-size estimate', async () => {
    globalThis.fetch = mock(async () => jsonResponse({ text: 'hi' })) as unknown as typeof fetch;

    const result = await transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/wav', 'en-US');
    // 48,000 bytes -> 6s under the shared byte-size heuristic.
    expect(result.durationSeconds).toBe(6);
  });

  // Sync failures fall back to async — proven here by confirming the async
  // upload endpoint gets hit next, without waiting out a full poll cycle
  // (an immediate upload error short-circuits before the real poll sleep).
  test.each([
    ['a transport error', async () => { throw new TypeError('fetch failed'); }],
    ['a non-2xx response', async () => new Response('bad request', { status: 400 })],
    ['a malformed (non-JSON) body', async () => new Response('not json', { status: 200 })],
    ['a response missing the text field', async () => jsonResponse({ audio_duration_ms: 1000 })],
  ])('%s on sync falls back to attempting the async upload', async (_label, syncHandler) => {
    const calls: Call[] = [];
    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);
      calls.push({ url, method: 'POST' });
      if (url === SYNC_URL) return syncHandler();
      if (url === UPLOAD_URL) return new Response('boom', { status: 500 });
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    await expect(transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/wav', 'en-US')).rejects.toThrow(ProviderUnavailableError);
    expect(calls.map((c) => c.url)).toEqual([SYNC_URL, UPLOAD_URL]);
  });
});

describe('transcribeWithAssemblyAI — async request shape', () => {
  // Each case only needs the create-phase request body, captured before an
  // immediate create-phase error short-circuits the flow ahead of the poll
  // loop's real sleep.
  test('an absent/auto language requests language_detection with the universal-3-pro fallback chain', async () => {
    const calls = mockAsyncFlow({ pollBodies: [], createStatus: { status: 400, body: {} } });
    await expect(transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/mpeg', 'auto')).rejects.toThrow(ProviderInputError);
    const createCall = calls.find((c) => c.url === CREATE_URL)!;
    expect(createCall.body).toMatchObject({
      language_detection: true,
      speech_models: ['universal-3-pro', 'universal-2'],
    });
  });

  test('an explicit non-default model sends only that model, no fallback chain', async () => {
    const calls = mockAsyncFlow({ pollBodies: [], createStatus: { status: 400, body: {} } });
    await expect(transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/mpeg', 'auto', undefined, { model: 'universal-2' }))
      .rejects.toThrow(ProviderInputError);
    const createCall = calls.find((c) => c.url === CREATE_URL)!;
    expect(createCall.body).toMatchObject({ speech_models: ['universal-2'] });
  });

  test('an explicit BCP-47 language is stripped to its bare ISO-639-1 subtag', async () => {
    const calls = mockAsyncFlow({ pollBodies: [], createStatus: { status: 400, body: {} } });
    await expect(transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/mpeg', 'pt-BR')).rejects.toThrow(ProviderInputError);
    const createCall = calls.find((c) => c.url === CREATE_URL)!;
    expect(createCall.body).toMatchObject({ language_code: 'pt' });
    expect((createCall.body as any).language_detection).toBeUndefined();
  });

  test('medical domain sets domain: medical-v1 on the create body', async () => {
    const calls = mockAsyncFlow({ pollBodies: [], createStatus: { status: 400, body: {} } });
    await expect(transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/mpeg', 'en-US', undefined, { domain: 'medical' }))
      .rejects.toThrow(ProviderInputError);
    const createCall = calls.find((c) => c.url === CREATE_URL)!;
    expect(createCall.body).toMatchObject({ domain: 'medical-v1' });
  });

  test('initial_prompt is parsed into bounded keyterms_prompt (separators split, short terms kept)', async () => {
    const calls = mockAsyncFlow({ pollBodies: [], createStatus: { status: 400, body: {} } });
    await expect(transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/mpeg', 'en-US', 'HyperWhisper, SwiftUI\n- Fly.io'))
      .rejects.toThrow(ProviderInputError);
    const createCall = calls.find((c) => c.url === CREATE_URL)!;
    expect(createCall.body).toMatchObject({ keyterms_prompt: ['HyperWhisper', 'SwiftUI', 'Fly.io'] });
  });

  test('no initial_prompt omits keyterms_prompt entirely', async () => {
    const calls = mockAsyncFlow({ pollBodies: [], createStatus: { status: 400, body: {} } });
    await expect(transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/mpeg', 'en-US')).rejects.toThrow(ProviderInputError);
    const createCall = calls.find((c) => c.url === CREATE_URL)!;
    expect((createCall.body as any).keyterms_prompt).toBeUndefined();
  });
});

describe('transcribeWithAssemblyAI — async polling, billing, and cleanup', () => {
  test('bills the model that actually ran, computes cost off it, and cleans up the transcript on success', async () => {
    const calls = mockAsyncFlow({
      pollBodies: [{ status: 200, body: { status: 'completed', text: 'hola', audio_duration: 120, speech_model_used: 'universal-2', language_code: 'es' } }],
    });
    const result = await transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/mpeg', 'auto');
    expect(result.model).toBe('universal-2');
    // universal-2 rate 0.15/60 per minute; 120s = 2 min -> 2 * 0.0025 = 0.005
    expect(result.costUsd).toBeCloseTo(0.005, 6);
    const deleteCall = calls.find((c) => c.method === 'DELETE');
    expect(deleteCall?.url).toBe(`${CREATE_URL}/transcript-123`);
  }, 10_000);

  test('an unrecognized speech_model_used bills the requested model; a non-positive duration falls back to the byte estimate', async () => {
    mockAsyncFlow({
      pollBodies: [{ status: 200, body: { status: 'completed', text: 'hola', audio_duration: 0, speech_model_used: 'some-future-model' } }],
    });
    const result = await transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/mpeg', 'auto');
    expect(result.model).toBe('universal-3-pro');
    expect(result.durationSeconds).toBe(6); // 48,000 bytes -> 6s estimate
  }, 10_000);

  test('an empty completed transcript is a zero-cost no_speech result', async () => {
    mockAsyncFlow({ pollBodies: [{ status: 200, body: { status: 'completed', text: '', audio_duration: 10 } }] });
    const result = await transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/mpeg', 'en-US');
    expect(result.source).toBe('no_speech');
    expect(result.costUsd).toBe(0);
  }, 10_000);

  test('a failed transcript job (HTTP 200, status:"error") throws ProviderInputError and still cleans up', async () => {
    const calls = mockAsyncFlow({ pollBodies: [{ status: 200, body: { status: 'error', error: 'unreadable audio' } }] });
    await expect(transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/mpeg', 'en-US')).rejects.toThrow(ProviderInputError);
    const deleteCall = calls.find((c) => c.method === 'DELETE');
    expect(deleteCall?.url).toBe(`${CREATE_URL}/transcript-123`);
  }, 10_000);

  test('polling survives a transient poll error and succeeds on the next attempt', async () => {
    const calls = mockAsyncFlow({
      pollBodies: [
        { status: 503, body: { error: 'temporarily unavailable' } },
        { status: 200, body: { status: 'completed', text: 'recovered', audio_duration: 3 } },
      ],
    });
    const result = await transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/mpeg', 'en-US');
    expect(result.text).toBe('recovered');
    expect(calls.filter((c) => c.url.startsWith(`${CREATE_URL}/`) && c.method === 'GET').length).toBe(2);
  }, 10_000);

  test('a 401/403 during polling throws immediately without retrying', async () => {
    const calls = mockAsyncFlow({ pollBodies: [{ status: 401, body: { error: 'bad key' } }] });
    await expect(transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/mpeg', 'en-US'))
      .rejects.toThrow('AssemblyAI API key is invalid or unauthorized');
    expect(calls.filter((c) => c.url.startsWith(`${CREATE_URL}/`) && c.method === 'GET').length).toBe(1);
  }, 10_000);
});

describe('transcribeWithAssemblyAI — upstream error mapping (upload phase)', () => {
  test('401 maps to a plain (non-fallback) Error', async () => {
    mockAsyncFlow({ pollBodies: [], uploadStatus: { status: 401, body: { error: 'unauthorized' } } });
    await expect(transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/mpeg', 'en-US'))
      .rejects.toThrow('AssemblyAI API key is invalid or unauthorized');
  });

  test('429 maps to ProviderUnavailableError (retryable via the fallback chain)', async () => {
    mockAsyncFlow({ pollBodies: [], uploadStatus: { status: 429, body: { error: 'rate limited' } } });
    await expect(transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/mpeg', 'en-US')).rejects.toThrow(ProviderUnavailableError);
  });

  test('402 maps to ProviderUnavailableError (upstream billing failure, not a client error)', async () => {
    mockAsyncFlow({ pollBodies: [], uploadStatus: { status: 402, body: { error: 'insufficient funds' } } });
    await expect(transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/mpeg', 'en-US')).rejects.toThrow(ProviderUnavailableError);
  });

  test('a 5xx maps to ProviderUnavailableError', async () => {
    mockAsyncFlow({ pollBodies: [], uploadStatus: { status: 503, body: { error: 'boom' } } });
    await expect(transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/mpeg', 'en-US')).rejects.toThrow(ProviderUnavailableError);
  });

  test('an unmapped 4xx maps to ProviderInputError carrying the status', async () => {
    mockAsyncFlow({ pollBodies: [], uploadStatus: { status: 400, body: { error: 'bad request' } } });
    await expect(transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/mpeg', 'en-US')).rejects.toThrow(ProviderInputError);
  });

  test('a malformed (non-JSON) upload response throws ProviderUnavailableError', async () => {
    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === UPLOAD_URL) return new Response('not json', { status: 200 });
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;
    await expect(transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/mpeg', 'en-US')).rejects.toThrow(ProviderUnavailableError);
  });

  test('an upload response missing upload_url throws ProviderUnavailableError', async () => {
    mockAsyncFlow({ pollBodies: [], uploadStatus: { status: 200, body: {} } });
    await expect(transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/mpeg', 'en-US')).rejects.toThrow(ProviderUnavailableError);
  });
});

describe('transcribeWithAssemblyAI — configuration', () => {
  test('throws a plain Error when ASSEMBLYAI_API_KEY is not configured', async () => {
    delete process.env.ASSEMBLYAI_API_KEY;
    let called = false;
    globalThis.fetch = mock(async () => { called = true; return jsonResponse({}); }) as unknown as typeof fetch;

    await expect(transcribeWithAssemblyAI(SMALL_AUDIO, 'audio/wav', 'en-US')).rejects.toThrow('ASSEMBLYAI_API_KEY not configured');
    expect(called).toBe(false);
  });
});
