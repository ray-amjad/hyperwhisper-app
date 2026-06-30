import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { Hono } from 'hono';
import { BYTES_PER_MINUTE_ESTIMATE } from '../lib/constants';
import { computeElevenLabsTranscriptionCost, creditsForCost } from '../lib/cost-calculator';
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
});
