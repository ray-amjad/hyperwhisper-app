import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { Hono } from 'hono';
import { BYTES_PER_MINUTE_ESTIMATE } from '../lib/constants';
import { computeElevenLabsTranscriptionCost, creditsForCost } from '../lib/cost-calculator';
import { drainPendingLatencyReports } from '../lib/latency-report';
import { estimateCreditsFromSize } from '../middleware/credits';
import { estimateCreditsForProviderFallbacks } from './transcribe';

describe('estimateCreditsForProviderFallbacks', () => {
  test('validates grok requests against the most expensive fallback provider', () => {
    const blendedEstimate = estimateCreditsFromSize(BYTES_PER_MINUTE_ESTIMATE);
    const grokFallbackEstimate = estimateCreditsForProviderFallbacks(BYTES_PER_MINUTE_ESTIMATE, 'grok');
    const elevenLabsEstimate = creditsForCost(computeElevenLabsTranscriptionCost(60));

    expect(blendedEstimate).toBe(6.3);
    expect(grokFallbackEstimate).toBe(elevenLabsEstimate);
    expect(grokFallbackEstimate).toBeGreaterThan(blendedEstimate);
  });

  test('does not under-estimate one minute of 64kbps audio', () => {
    const approximateOneMinute64KbpsUploadBytes = 512 * 1024;
    const grokFallbackEstimate = estimateCreditsForProviderFallbacks(approximateOneMinute64KbpsUploadBytes, 'grok');
    const oneMinuteElevenLabsEstimate = creditsForCost(computeElevenLabsTranscriptionCost(60));

    expect(grokFallbackEstimate).toBeGreaterThanOrEqual(oneMinuteElevenLabsEstimate);
  });
});

// A valid, well-funded licensed user so auth + credit checks pass entirely
// in-memory (no network) and the route reaches the provider fallback loop.
mock.module('../lib/redis', () => ({
  isIPBlocked: async () => false,
  getCachedLicense: async () => ({ isValid: true, credits: 1000, cachedAt: 'cached' }),
  cacheLicense: async () => {},
}));

const { transcribeRoute } = await import('./transcribe');

const originalFetch = globalThis.fetch;

describe('transcribeRoute provider fallback', () => {
  beforeEach(() => {
    process.env.ELEVENLABS_API_KEY = 'test-elevenlabs-key';
    process.env.DEEPGRAM_API_KEY = 'test-deepgram-key';
    process.env.GROQ_API_KEY = 'test-groq-key';
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  function buildApp(): Hono {
    const app = new Hono();
    app.post('/transcribe', transcribeRoute);
    return app;
  }

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

  test('continues the fallback chain when a provider rejects the input with a 400', async () => {
    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('api.elevenlabs.io')) {
        // Scribe v2 rejects the en-US language code with a 400 — historically
        // this aborted the whole request instead of trying the next provider.
        return new Response('{"detail":"invalid language_code"}', { status: 400 });
      }

      if (url.includes('api.deepgram.com')) {
        return Response.json({
          results: {
            channels: [{ alternatives: [{ transcript: 'hello from deepgram' }], detected_language: 'en' }],
          },
          metadata: { duration: 1, request_id: 'dg-req-1' },
        });
      }

      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(transcribeRequest('elevenlabs'));
    const body = await response.json() as {
      text: string;
      metadata: { stt_provider: string };
    };

    expect(response.status).toBe(200);
    expect(body.text).toBe('hello from deepgram');
    expect(body.metadata.stt_provider).toContain('deepgram');
    expect(body.metadata.stt_provider).toContain('fallback from');
  });

  test('returns 400 (not 429/500) when every provider rejects the input', async () => {
    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('api.elevenlabs.io') || url.includes('api.deepgram.com') || url.includes('api.groq.com')) {
        return new Response('{"detail":"bad input"}', { status: 400 });
      }

      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(transcribeRequest('elevenlabs'));
    const body = await response.json() as { error: string };

    expect(response.status).toBe(400);
    expect(body.error).toBe('Transcription input rejected');
  });

  test('still short-circuits with 500 on an auth (401) failure without trying fallbacks', async () => {
    let deepgramCalled = false;
    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('api.elevenlabs.io')) {
        return new Response('unauthorized', { status: 401 });
      }

      if (url.includes('api.deepgram.com')) {
        deepgramCalled = true;
        return Response.json({
          results: { channels: [{ alternatives: [{ transcript: 'should not reach here' }] }] },
          metadata: { duration: 1 },
        });
      }

      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(transcribeRequest('elevenlabs'));

    expect(response.status).toBe(500);
    expect(deepgramCalled).toBe(false);
  });

  test('preserves a literal + in initial_prompt end-to-end (no plus-to-space decode)', async () => {
    let deepgramUrl = '';
    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('api.deepgram.com')) {
        deepgramUrl = url;
        return Response.json({
          results: {
            channels: [{ alternatives: [{ transcript: 'ok' }], detected_language: 'en' }],
          },
          metadata: { duration: 1, request_id: 'dg-req-2' },
        });
      }

      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const audio = new Uint8Array(2048);
    const request = new Request(
      // `C%2B%2B` must reach the provider as the term `C++` — Hono's default
      // query decoder applied an extra + → space step (see lib/query.ts).
      'http://localhost/transcribe?license_key=test-license&language=en&initial_prompt=C%2B%2B',
      {
        method: 'POST',
        headers: {
          'Content-Type': 'audio/wav',
          'Content-Length': String(audio.byteLength),
          'X-STT-Provider': 'deepgram',
        },
        body: audio,
      },
    );

    const response = await buildApp().fetch(request);

    expect(response.status).toBe(200);
    // Deepgram converts initial_prompt terms to repeated keyterm params,
    // percent-encoded — the + must survive as %2B, not become a space.
    expect(deepgramUrl).toContain('keyterm=C%2B%2B');
  });
});

// The public /latency page is built from these rows, so "the user said no" has
// to hold on every path — including the paths where the transcription itself
// fails, which is where an opt-out is easiest to lose.
describe('X-Latency-Opt-Out', () => {
  const originalSecret = process.env.HYPERWHISPER_INTERNAL_SECRET;
  const originalRegion = process.env.FLY_REGION;

  beforeEach(() => {
    process.env.DEEPGRAM_API_KEY = 'test-deepgram-key';
    process.env.ELEVENLABS_API_KEY = 'test-elevenlabs-key';
    process.env.GROQ_API_KEY = 'test-groq-key';
    // Two things independently silence reporting, and either would make an
    // opt-out assertion pass for the wrong reason: no ingest secret, and no
    // FLY_REGION (off Fly nothing is reported, so a laptop never raises a
    // column on the public page). Set both so the opt-out is the only variable.
    process.env.HYPERWHISPER_INTERNAL_SECRET = 'test-internal-secret';
    process.env.FLY_REGION = 'fra';
  });

  afterEach(async () => {
    globalThis.fetch = originalFetch;
    if (originalSecret === undefined) {
      delete process.env.HYPERWHISPER_INTERNAL_SECRET;
    } else {
      process.env.HYPERWHISPER_INTERNAL_SECRET = originalSecret;
    }
    if (originalRegion === undefined) {
      delete process.env.FLY_REGION;
    } else {
      process.env.FLY_REGION = originalRegion;
    }
  });

  function buildApp(): Hono {
    const app = new Hono();
    app.post('/transcribe', transcribeRoute);
    return app;
  }

  /** Runs one transcription and returns the latency batches it reported. */
  async function reportedBatches(optOutHeader?: string): Promise<unknown[]> {
    const batches: unknown[] = [];
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.includes('/api/internal/latency')) {
        batches.push(JSON.parse(String(init?.body)));
        return new Response('{"inserted":1}', { status: 200 });
      }

      if (url.includes('api.deepgram.com')) {
        return Response.json({
          results: {
            channels: [{ alternatives: [{ transcript: 'hello' }], detected_language: 'en' }],
          },
          metadata: { duration: 3, request_id: 'dg-req-optout' },
        });
      }

      if (url.includes('/api/license/credits')) {
        return Response.json({ success: true });
      }

      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const audio = new Uint8Array(2048);
    const headers: Record<string, string> = {
      'Content-Type': 'audio/wav',
      'Content-Length': String(audio.byteLength),
      'X-STT-Provider': 'deepgram',
    };
    if (optOutHeader !== undefined) {
      headers['X-Latency-Opt-Out'] = optOutHeader;
    }

    const response = await buildApp().fetch(
      new Request('http://localhost/transcribe?license_key=test-license&language=en', {
        method: 'POST',
        headers,
        body: audio,
      }),
    );
    expect(response.status).toBe(200);

    // Reporting is fired without awaiting, so the POST is still in flight when
    // the response returns. Drain it the same way SIGTERM does.
    await drainPendingLatencyReports(2_000);
    return batches;
  }

  test('reports one sample when the header is absent', async () => {
    const batches = await reportedBatches();
    expect(batches).toHaveLength(1);
    expect((batches[0] as { samples: unknown[] }).samples).toHaveLength(1);
  });

  test('reports nothing when the client opts out', async () => {
    expect(await reportedBatches('1')).toHaveLength(0);
  });

  test('treats any unrecognised value as an opt-out', async () => {
    expect(await reportedBatches('true')).toHaveLength(0);
    expect(await reportedBatches('yes')).toHaveLength(0);
    expect(await reportedBatches('please-dont')).toHaveLength(0);
  });

  test('an explicit negative value keeps reporting on', async () => {
    expect(await reportedBatches('0')).toHaveLength(1);
    expect(await reportedBatches('false')).toHaveLength(1);
    // An empty header is what a client sends when a setting is unset, not a
    // deliberate opt-out.
    expect(await reportedBatches('')).toHaveLength(1);
  });

  test('opting out also suppresses the failure rows', async () => {
    const batches: unknown[] = [];
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.includes('/api/internal/latency')) {
        batches.push(JSON.parse(String(init?.body)));
        return new Response('{"inserted":1}', { status: 200 });
      }

      if (url.includes('/api/license/credits')) {
        return Response.json({ success: true });
      }

      // Every provider rejects the input: the all-failed path, which returns
      // early — the finally block is what still reports it.
      return new Response('{"detail":"bad input"}', { status: 400 });
    }) as unknown as typeof fetch;

    const audio = new Uint8Array(2048);
    const response = await buildApp().fetch(
      new Request('http://localhost/transcribe?license_key=test-license&language=en', {
        method: 'POST',
        headers: {
          'Content-Type': 'audio/wav',
          'Content-Length': String(audio.byteLength),
          'X-STT-Provider': 'elevenlabs',
          'X-Latency-Opt-Out': '1',
        },
        body: audio,
      }),
    );

    expect(response.status).toBe(400);
    await drainPendingLatencyReports(2_000);
    expect(batches).toHaveLength(0);
  });
});
