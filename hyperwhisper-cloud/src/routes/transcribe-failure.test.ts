import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { Hono } from 'hono';
import { ProviderInputError } from '../providers/types';
import { providerChainFailureResponse } from './transcribe-failure';

// A valid, well-funded licensed user so the route reaches the provider chain.
// mock.module is process-wide in bun: keep the `redis` export (see
// transcribe.test.ts) or other files fail to load.
mock.module('../lib/redis', () => ({
  redis: {},
  isIPBlocked: async () => false,
  getCachedLicense: async () => ({ isValid: true, credits: 1000, cachedAt: 'cached' }),
  cacheLicense: async () => {},
}));

const { transcribeRoute } = await import('./transcribe');

function baseInput() {
  return {
    requestId: 'request-123',
    startTime: performance.now(),
    fallbackCount: 0,
    attemptFailures: [],
    lastError: undefined,
    lastInputError: undefined,
    sawUnavailable: false,
  };
}

async function body(response: Response) {
  return response.json() as Promise<Record<string, unknown>>;
}

describe('providerChainFailureResponse', () => {
  test('returns 400 when every provider rejects the request input', async () => {
    const inputError = new ProviderInputError('ElevenLabs', 422, 'invalid language');
    const response = providerChainFailureResponse({
      ...baseInput(),
      provider: 'elevenlabs',
      fallbackCount: 2,
      lastError: inputError,
      lastInputError: inputError,
    });

    expect(response.status).toBe(400);
    expect(await body(response)).toEqual({
      error: 'Transcription input rejected',
      message: 'No transcription provider accepted this audio or its settings (for example the language).',
      requestId: 'request-123',
      provider: 'elevenlabs',
    });
  });

  test('does not report a request error when any provider was unavailable', async () => {
    const inputError = new ProviderInputError('Groq', 400, 'unsupported audio');
    const response = providerChainFailureResponse({
      ...baseInput(),
      provider: 'deepgram',
      fallbackCount: 2,
      lastError: inputError,
      lastInputError: inputError,
      sawUnavailable: true,
    });

    expect(response.status).toBe(429);
    expect(await body(response)).toMatchObject({
      error: 'All providers unavailable',
      requestId: 'request-123',
    });
  });

  test('returns 502 for a self-only provider without echoing the upstream error', async () => {
    const response = providerChainFailureResponse({
      ...baseInput(),
      provider: 'google-chirp',
      lastError: new Error('Google Speech deadline exceeded'),
      sawUnavailable: true,
    });

    expect(response.status).toBe(502);
    expect(await body(response)).toEqual({
      error: 'google-chirp unavailable',
      message: 'google-chirp is currently unavailable. Please try again shortly.',
      requestId: 'request-123',
      provider: 'google-chirp',
    });
  });

  test('uses a useful default message when a self-only failure has no error', async () => {
    const response = providerChainFailureResponse({
      ...baseInput(),
      provider: 'azure-mai',
      sawUnavailable: true,
    });

    expect(response.status).toBe(502);
    expect(await body(response)).toEqual({
      error: 'azure-mai unavailable',
      message: 'azure-mai is currently unavailable. Please try again shortly.',
      requestId: 'request-123',
      provider: 'azure-mai',
    });
  });

  test('returns a retryable 429 only after a multi-provider chain is exhausted', async () => {
    const response = providerChainFailureResponse({
      ...baseInput(),
      provider: 'deepgram',
      fallbackCount: 2,
      attemptFailures: [
        { provider: 'deepgram', kind: 'timeout', attemptMs: 15_001 },
        { provider: 'groq', kind: 'rate_limit', status: 429 },
        { provider: 'elevenlabs', kind: 'upstream_5xx', status: 503 },
      ],
      lastError: new Error('ElevenLabs unavailable'),
      sawUnavailable: true,
    });

    expect(response.status).toBe(429);
    expect(await body(response)).toEqual({
      error: 'All providers unavailable',
      message: 'All transcription providers are currently rate-limited. Please try again shortly.',
      requestId: 'request-123',
    });
  });
});

// #2131: drive the real /transcribe route so the provider's own failure text
// comes from a real adapter, and check it reaches the log but not the reply.
describe('transcribeRoute chain failure keeps provider text out of the reply (#2131)', () => {
  const MARKER = 'MARKER-2131-provider-text';
  const originalFetch = globalThis.fetch;
  const savedEnv: Record<string, string | undefined> = {};
  const keys = ['ELEVENLABS_API_KEY', 'DEEPGRAM_API_KEY', 'GROQ_API_KEY', 'OPENAI_API_KEY'];

  beforeEach(() => {
    for (const key of keys) {
      savedEnv[key] = process.env[key];
      process.env[key] = `test-${key}`;
    }
  });
  afterEach(() => {
    globalThis.fetch = originalFetch;
    for (const key of keys) {
      if (savedEnv[key] === undefined) delete process.env[key];
      else process.env[key] = savedEnv[key];
    }
  });

  function transcribeRequest(provider: string): Request {
    const audio = new Uint8Array(2048);
    return new Request('http://localhost/transcribe?license_key=test-license&language=en-US', {
      method: 'POST',
      headers: {
        'Content-Type': 'audio/wav',
        'Content-Length': String(audio.byteLength),
        'X-STT-Provider': provider,
      },
      body: audio,
    });
  }

  async function run(provider: string) {
    const app = new Hono();
    app.post('/transcribe', transcribeRoute);
    const logged: string[] = [];
    const originalLog = console.log;
    console.log = ((...args: unknown[]) => { logged.push(args.map(String).join(' ')); }) as typeof console.log;
    let response: Response;
    try {
      response = await app.fetch(transcribeRequest(provider));
    } finally {
      console.log = originalLog;
    }
    const raw = await response.text();
    const fail = logged
      .filter((line) => line.startsWith('{'))
      .map((line) => JSON.parse(line) as { event: string; requestId?: string; kind?: string; status?: number; message?: string })
      .find((event) => event.event === 'transcribe.request_fail');
    return { response, raw, body: JSON.parse(raw) as Record<string, unknown>, fail };
  }

  // Deliberately departs from the issue's Done-when ("the marker is in the
  // log"): since #2163 the request_fail line is content-free, so the provider's
  // body reaches neither the client nor the log; the log keeps the status.
  test('every provider 400s with a marker body: fixed 400, marker in neither the reply nor the log', async () => {
    const called: string[] = [];
    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('api.elevenlabs.io') || url.includes('api.deepgram.com') || url.includes('api.groq.com')) {
        called.push(new URL(url).hostname);
        return new Response(JSON.stringify({ detail: MARKER }), { status: 400 });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const { response, raw, body, fail } = await run('elevenlabs');

    expect(called).toEqual(['api.elevenlabs.io', 'api.deepgram.com', 'api.groq.com']);
    expect(response.status).toBe(400);
    expect(body).toMatchObject({
      error: 'Transcription input rejected',
      message: 'No transcription provider accepted this audio or its settings (for example the language).',
      provider: 'elevenlabs',
    });
    expect(raw).not.toContain(MARKER);
    expect(fail).toMatchObject({ requestId: body.requestId, kind: 'all_providers_rejected_input', status: 400 });
    expect(JSON.stringify(fail)).not.toContain(MARKER);
  });

  test('a self-only provider whose fetch rejects: fixed 502, marker only in the log', async () => {
    const called: string[] = [];
    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('api.openai.com')) {
        called.push('openai');
        throw new TypeError(MARKER);
      }
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const { response, raw, body, fail } = await run('openai');

    expect(called.length).toBeGreaterThan(0);
    expect(response.status).toBe(502);
    expect(body).toMatchObject({
      error: 'openai unavailable',
      message: 'openai is currently unavailable. Please try again shortly.',
      provider: 'openai',
    });
    expect(raw).not.toContain(MARKER);
    expect(fail).toMatchObject({ requestId: body.requestId, kind: 'self_only_chain_failed' });
    expect(fail?.message).toContain(MARKER);
  });
});
