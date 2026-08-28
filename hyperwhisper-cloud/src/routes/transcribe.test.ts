import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { Hono } from 'hono';
import { BYTES_PER_MINUTE_ESTIMATE } from '../lib/constants';
import { computeElevenLabsTranscriptionCost, creditsForCost } from '../lib/cost-calculator';
import { drainPendingLatencyReports } from '../lib/latency-report';
import { estimateCreditsFromSize } from '../middleware/credits';
import { estimateCreditsForProviderFallbacks } from './transcribe';

/**
 * A client new enough to carry the "Share anonymous speed data" switch, which
 * is a precondition for being measured at all (lib/latency-eligibility.ts).
 * Every latency test below sends these so the thing under test is the thing
 * that varies, not eligibility.
 */
const ELIGIBLE_CLIENT_HEADERS = {
  'X-HyperWhisper-Platform': 'macos',
  'X-HyperWhisper-Version': '2.43.0',
} as const;

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
  // Satisfies the static `import { redis }` in lib/google-auth, reached from
  // this route via providers/google-chirp. mock.module is process-wide in bun,
  // so a redis mock ANYWHERE in the run that omits this export makes this whole
  // FILE fail to load with "Export named 'redis' not found" — which is what
  // `bun test src` (i.e. CI) did until every such mock grew this line. Every
  // assertion below was silently not running there.
  redis: {},
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

/** One row of a reported batch, as the ingest endpoint receives it. */
type ReportedSample = {
  provider: string;
  model?: string;
  flyRegion: string;
  latencyMs: number;
  ok: boolean;
  failureKind?: string;
  attempt: number;
  audioSeconds: number;
};

function samplesOf(batch: unknown): ReportedSample[] {
  return (batch as { samples: ReportedSample[] }).samples;
}

// The public /latency page is built from these rows, so "the user said no" has
// to hold on every path — including the paths where the transcription itself
// fails, which is where an opt-out is easiest to lose.
describe('X-Latency-Opt-Out', () => {
  const originalSecret = process.env.HYPERWHISPER_INTERNAL_SECRET;
  const originalRegion = process.env.FLY_REGION;
  const originalApp = process.env.FLY_APP_NAME;

  beforeEach(() => {
    process.env.DEEPGRAM_API_KEY = 'test-deepgram-key';
    process.env.ELEVENLABS_API_KEY = 'test-elevenlabs-key';
    process.env.GROQ_API_KEY = 'test-groq-key';
    // Three things independently silence reporting, and any of them would make
    // an opt-out assertion pass for the wrong reason: no ingest secret, no
    // FLY_REGION (off Fly nothing is reported, so a laptop never raises a
    // column on the public page), and any app but production (staging shares
    // the secret and must not publish). Set all three so the opt-out is the
    // only variable.
    process.env.HYPERWHISPER_INTERNAL_SECRET = 'test-internal-secret';
    process.env.FLY_REGION = 'fra';
    process.env.FLY_APP_NAME = 'hyperwhisper-transcribe';
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
    if (originalApp === undefined) {
      delete process.env.FLY_APP_NAME;
    } else {
      process.env.FLY_APP_NAME = originalApp;
    }
  });

  function buildApp(): Hono {
    const app = new Hono();
    app.post('/transcribe', transcribeRoute);
    return app;
  }

  /** Runs one transcription and returns the latency batches it reported. */
  async function reportedBatches(
    optOutHeader?: string,
    clientHeaders: Record<string, string> = ELIGIBLE_CLIENT_HEADERS,
  ): Promise<unknown[]> {
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
      ...clientHeaders,
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

  // Sharing is on by default, so the default may only be applied to a build
  // that can turn it off. These are the versions that were live when the page
  // launched: they were never asked, so they are never recorded — not even
  // though they send no opt-out header at all.
  test('reports nothing from a build that predates the opt-out switch', async () => {
    expect(
      await reportedBatches(undefined, {
        'X-HyperWhisper-Platform': 'macos',
        'X-HyperWhisper-Version': '2.42.0',
      }),
    ).toHaveLength(0);
    expect(
      await reportedBatches(undefined, {
        'X-HyperWhisper-Platform': 'windows',
        'X-HyperWhisper-Version': '1.9.0',
      }),
    ).toHaveLength(0);
  });

  test('reports nothing from a client it cannot identify', async () => {
    expect(await reportedBatches(undefined, {})).toHaveLength(0);
  });

  test('reports nothing from an old build even when it says it opted in', async () => {
    expect(
      await reportedBatches('0', {
        'X-HyperWhisper-Platform': 'macos',
        'X-HyperWhisper-Version': '2.42.0',
      }),
    ).toHaveLength(0);
  });

  // The legacy User-Agent is the only identity a build older than the
  // X-HyperWhisper-* headers sends, and it is exactly the population this gate
  // exists for — so it has to be read, not treated as unidentifiable.
  test('reads an old build from its User-Agent alone', async () => {
    expect(
      await reportedBatches(undefined, { 'User-Agent': 'HyperWhisper/2.41.0' }),
    ).toHaveLength(0);
    expect(
      await reportedBatches(undefined, { 'User-Agent': 'HyperWhisper-Windows/1.8.2' }),
    ).toHaveLength(0);
    expect(
      await reportedBatches(undefined, { 'User-Agent': 'HyperWhisper/2.43.0' }),
    ).toHaveLength(1);
  });

  /**
   * Runs a transcription where every provider in the chain rejects the input
   * with a 400 — the all-failed path, which returns early, so the `finally`
   * block is the only thing that still reports it.
   */
  async function reportedFailureBatches(optOutHeader?: string): Promise<unknown[]> {
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

      return new Response('{"detail":"bad input"}', { status: 400 });
    }) as unknown as typeof fetch;

    const audio = new Uint8Array(2048);
    const headers: Record<string, string> = {
      'Content-Type': 'audio/wav',
      'Content-Length': String(audio.byteLength),
      'X-STT-Provider': 'elevenlabs',
      ...ELIGIBLE_CLIENT_HEADERS,
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

    expect(response.status).toBe(400);
    await drainPendingLatencyReports(2_000);
    return batches;
  }

  // The half that has to hold first: without it, "no rows when opted out"
  // would pass just as happily if sample recording were deleted outright.
  test('records one row per failed attempt when the client has not opted out', async () => {
    const batches = await reportedFailureBatches();
    expect(batches).toHaveLength(1);

    // elevenlabs → deepgram → groq, each rejecting the input: three rows, in
    // chain order, every one a failure attributed to the provider that ran it.
    const samples = samplesOf(batches[0]);
    expect(samples.map((sample) => sample.provider)).toEqual(['elevenlabs', 'deepgram', 'groq']);
    expect(samples.map((sample) => sample.attempt)).toEqual([1, 2, 3]);
    expect(samples.every((sample) => sample.ok === false)).toBe(true);
    // A real upstream 4xx: the provider answered, so it is a measurement.
    expect(samples.every((sample) => sample.failureKind === 'input_rejected')).toBe(true);
    // Not `latencyMs >= 1`: sendBatch already clamps with Math.max(1, ...), so
    // that holds for 0, for a negative, and for a wrong clock. What the value
    // actually is, is pinned by 'times a failed attempt on the route's own
    // clock' below, where the route's number and the error's differ.
    expect(samples.every((sample) => Number.isInteger(sample.latencyMs))).toBe(true);
  });

  test('opting out also suppresses the failure rows', async () => {
    expect(await reportedFailureBatches('1')).toHaveLength(0);
  });

  // A provider that was never called cannot have a latency or an error rate.
  test('reports nothing for a rejection thrown before any provider call', async () => {
    const batches: unknown[] = [];
    let upstreamCalled = false;
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.includes('/api/internal/latency')) {
        batches.push(JSON.parse(String(init?.body)));
        return new Response('{"inserted":1}', { status: 200 });
      }

      if (url.includes('/api/license/credits')) {
        return Response.json({ success: true });
      }

      upstreamCalled = true;
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const audio = new Uint8Array(2048);
    // Azure MAI takes only wav/mp3/flac, and rejects anything else from a pure
    // content-type check above any fetch — a client that stopped pre-converting
    // its m4a must not show up as Azure failing calls in ~1 ms.
    const response = await buildApp().fetch(
      new Request('http://localhost/transcribe?license_key=test-license&language=en', {
        method: 'POST',
        headers: {
          'Content-Type': 'audio/webm;codecs=opus',
          'Content-Length': String(audio.byteLength),
          'X-STT-Provider': 'azure-mai',
        },
        body: audio,
      }),
    );

    expect(response.status).toBe(415);
    await drainPendingLatencyReports(2_000);
    expect(upstreamCalled).toBe(false);
    expect(batches).toHaveLength(0);
  });
});

// The clip length a row is filed under decides which cell it lands in, so it
// has to describe the audio rather than whichever estimator the provider that
// answered happens to use.
describe('latency clip length and model', () => {
  const originalSecret = process.env.HYPERWHISPER_INTERNAL_SECRET;
  const originalRegion = process.env.FLY_REGION;
  const originalApp = process.env.FLY_APP_NAME;

  beforeEach(() => {
    process.env.OPENAI_API_KEY = 'test-openai-key';
    process.env.ASSEMBLYAI_API_KEY = 'test-assemblyai-key';
    process.env.HYPERWHISPER_INTERNAL_SECRET = 'test-internal-secret';
    process.env.FLY_REGION = 'fra';
    process.env.FLY_APP_NAME = 'hyperwhisper-transcribe';
  });

  afterEach(async () => {
    globalThis.fetch = originalFetch;
    delete process.env.OPENAI_API_KEY;
    delete process.env.ASSEMBLYAI_API_KEY;
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
    if (originalApp === undefined) {
      delete process.env.FLY_APP_NAME;
    } else {
      process.env.FLY_APP_NAME = originalApp;
    }
  });

  function buildApp(): Hono {
    const app = new Hono();
    app.post('/transcribe', transcribeRoute);
    return app;
  }

  test('files a success by the content-type estimate, not the adapter duration', async () => {
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
      if (url.includes('api.openai.com')) {
        // gpt-4o-transcribe (openai's default) returns tokens, never a
        // duration, so the adapter bills against the flat 64 kbps estimate:
        // 12 seconds for this buffer, four times its real length.
        return Response.json({ text: 'hello', usage: { input_tokens: 10, output_tokens: 3 } });
      }

      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    // 96,000 bytes of 16 kHz/16-bit mono WAV — exactly 3 seconds, the clip both
    // desktop apps produce for a short dictation. 'short' (<10s), not 'medium'.
    const audio = new Uint8Array(96_000);
    const response = await buildApp().fetch(
      new Request('http://localhost/transcribe?license_key=test-license&language=en', {
        method: 'POST',
        headers: {
          'Content-Type': 'audio/wav',
          'Content-Length': String(audio.byteLength),
          'X-STT-Provider': 'openai',
          ...ELIGIBLE_CLIENT_HEADERS,
        },
        body: audio,
      }),
    );
    expect(response.status).toBe(200);
    await drainPendingLatencyReports(2_000);

    expect(batches).toHaveLength(1);
    const [sample] = samplesOf(batches[0]);
    expect(sample.ok).toBe(true);
    expect(sample.audioSeconds).toBe(3);
  });

  test('records the model that actually ran, not the one requested', async () => {
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
      if (url.includes('sync.assemblyai.com')) {
        // AssemblyAI's sync fast path always runs universal-3-5-pro, whatever
        // async model the caller asked for.
        return Response.json({ text: 'hello', audio_duration_ms: 1_500 });
      }

      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const audio = new Uint8Array(48_000);
    const response = await buildApp().fetch(
      new Request('http://localhost/transcribe?license_key=test-license&language=en', {
        method: 'POST',
        headers: {
          'Content-Type': 'audio/wav',
          'Content-Length': String(audio.byteLength),
          'X-STT-Provider': 'assemblyai',
          'X-STT-Model': 'universal-2',
          ...ELIGIBLE_CLIENT_HEADERS,
        },
        body: audio,
      }),
    );
    expect(response.status).toBe(200);
    await drainPendingLatencyReports(2_000);

    expect(batches).toHaveLength(1);
    const [sample] = samplesOf(batches[0]);
    expect(sample.model).toBe('universal-3-5-pro');
  });
});

// Which attempts become a row, and what the number on them means. Both of these
// have been broken by a "safe" refactor once already, and neither is visible
// from the response the caller gets — only from this batch.
describe('what a latency row measures', () => {
  const originalEnv = { ...process.env };

  beforeEach(() => {
    process.env.HYPERWHISPER_INTERNAL_SECRET = 'test-internal-secret';
    process.env.FLY_REGION = 'fra';
    process.env.FLY_APP_NAME = 'hyperwhisper-transcribe';
  });

  afterEach(async () => {
    globalThis.fetch = originalFetch;
    process.env = { ...originalEnv };
  });

  function buildApp(): Hono {
    const app = new Hono();
    app.post('/transcribe', transcribeRoute);
    return app;
  }

  function request(provider: string, query = ''): Request {
    const audio = new Uint8Array(2048);
    return new Request(`http://localhost/transcribe?license_key=test-license${query}`, {
      method: 'POST',
      headers: {
        'Content-Type': 'audio/wav',
        'Content-Length': String(audio.byteLength),
        'X-STT-Provider': provider,
        ...ELIGIBLE_CLIENT_HEADERS,
      },
      body: audio,
    });
  }

  /**
   * The number on a failed row is the route's clock over the WHOLE attempt, not
   * ProviderUnavailableError.elapsedMs — which for the async providers times
   * only the one call that failed.
   *
   * AssemblyAI's async path is the real shape of that: upload, create, poll.
   * Here the upload takes 250 ms and the create then fails instantly, so the
   * error carries an elapsedMs of ~0 while the attempt genuinely cost the user
   * a quarter second. The assertion is on the pre-clamp value (250 ≫ the 1 ms
   * floor sendBatch applies), so switching the route back to `error.elapsedMs`
   * reports ~1 and fails here.
   */
  test("times a failed attempt on the route's own clock, not the error's elapsedMs", async () => {
    process.env.ASSEMBLYAI_API_KEY = 'test-assemblyai-key';
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
      if (url.includes('/v2/upload')) {
        await new Promise((resolve) => setTimeout(resolve, 250));
        return Response.json({ upload_url: 'https://cdn.assemblyai.com/upload/abc' });
      }
      if (url.includes('/v2/transcript')) {
        // A reset connection on job creation: fetchWithTimeout turns this into
        // ProviderUnavailableError with elapsedMs of its own call alone (~0).
        throw new Error('connection reset');
      }

      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    // No `language` param: AssemblyAI's sync fast path needs an explicit
    // language, so omitting it routes through upload → create → poll.
    const response = await buildApp().fetch(request('assemblyai'));
    expect(response.status).toBe(502);
    await drainPendingLatencyReports(2_000);

    expect(batches).toHaveLength(1);
    const samples = samplesOf(batches[0]);
    expect(samples).toHaveLength(1);
    expect(samples[0].provider).toBe('assemblyai');
    expect(samples[0].ok).toBe(false);
    expect(samples[0].failureKind).toBe('network_error');
    expect(samples[0].latencyMs).toBeGreaterThanOrEqual(200);
  });

  /**
   * The other half: an attempt that never reached the provider is not a
   * measurement, and one that did still is.
   *
   * Grok's missing-key check throws ProviderUnavailableError above its fetch, so
   * the chain carries on to Deepgram and the user gets a transcript — while grok
   * accrued a ~1 ms row on every single request, which is enough to render it
   * the fastest provider in the region for the next 30 days.
   */
  test('reports nothing for a provider whose key is missing, but reports the one that ran', async () => {
    delete process.env.XAI_API_KEY;
    delete process.env.GROK_API_KEY;
    process.env.DEEPGRAM_API_KEY = 'test-deepgram-key';

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
      if (url.includes('api.deepgram.com')) {
        return Response.json({
          results: {
            channels: [{ alternatives: [{ transcript: 'hello' }], detected_language: 'en' }],
          },
          metadata: { duration: 1, request_id: 'dg-req-nokey' },
        });
      }

      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(request('grok', '&language=en'));
    expect(response.status).toBe(200);
    await drainPendingLatencyReports(2_000);

    expect(batches).toHaveLength(1);
    const samples = samplesOf(batches[0]);
    expect(samples.map((sample) => sample.provider)).toEqual(['deepgram']);
    expect(samples[0].ok).toBe(true);
  });
});

/**
 * "Has this provider got a sibling to fall back to?" is the registry's answer
 * (`isSelfOnly`), not something a caller may measure off the chain array it is
 * holding. The route filters its own copy of that array — it drops ElevenLabs
 * when the request landed in a region where ElevenLabs is geo-blocked and the
 * body was too large to fly-replay — so the array's length answers "how many
 * did we try", which is a different question.
 *
 * The two answers agree for every provider today, which is exactly why this
 * needs a test: a shorter ElevenLabs chain (say a fourth cheap provider
 * retired) would make the old `chain.length === 1` derivation start returning
 * 502 "elevenlabs unavailable" for a request ElevenLabs never even ran.
 */
describe('self-only is a provider policy, not the length of a filtered chain', () => {
  const originalRegion = process.env.FLY_REGION;

  beforeEach(() => {
    process.env.DEEPGRAM_API_KEY = 'test-deepgram-key';
    process.env.GROQ_API_KEY = 'test-groq-key';
    process.env.ELEVENLABS_API_KEY = 'test-elevenlabs-key';
    // A region where ElevenLabs serves its geo-block HTML instead of JSON.
    process.env.FLY_REGION = 'nrt';
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    delete process.env.AZURE_SPEECH_KEY_SOUTHEASTASIA;
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

  test('a geo-blocked, oversized ElevenLabs request that exhausts its siblings is 429, not 502', async () => {
    // Over FLY_REPLAY_MAX_BODY_BYTES (900_000), so the route cannot replay the
    // request to `iad` and drops ElevenLabs from the chain instead. What is
    // left is ['deepgram', 'groq'].
    const audio = new Uint8Array(1_000_000);

    // Counted, not thrown: a throw from inside the mocked fetch is swallowed by
    // fetchWithTimeout into a ProviderUnavailableError, so the chain would just
    // carry on to deepgram and the 429 assertion below would still pass with
    // the ElevenLabs filter deleted. The count is what actually fails then.
    let elevenlabsCalls = 0;

    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('api.elevenlabs.io')) {
        elevenlabsCalls += 1;
        return new Response('upstream boom', { status: 503 });
      }
      if (url.includes('api.deepgram.com') || url.includes('api.groq.com')) {
        return new Response('upstream boom', { status: 503 });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(new Request(
      'http://localhost/transcribe?license_key=test-license&language=en',
      {
        method: 'POST',
        headers: {
          'Content-Type': 'audio/wav',
          'Content-Length': String(audio.byteLength),
          'X-STT-Provider': 'elevenlabs',
        },
        body: audio,
      },
    ));
    const body = await response.json() as { error: string };

    // ElevenLabs is NOT self-only, so every sibling failing is "all providers
    // unavailable" (429, retry later) — never the 502 the route reserves for a
    // provider the caller deliberately pinned.
    expect(response.status).toBe(429);
    expect(body.error).toBe('All providers unavailable');
    // ElevenLabs was dropped from the chain, so it was never called — the route
    // did not simply try it and lose to its geo-block HTML.
    expect(elevenlabsCalls).toBe(0);
  });

  test('a genuinely self-only provider still gets the 502 that says "no sibling ran"', async () => {
    // 'nrt' routes Azure MAI at its southeastasia resource (azure-mai.ts picks
    // the key from FLY_REGION), so this is the key that attempt needs.
    process.env.AZURE_SPEECH_KEY_SOUTHEASTASIA = 'test-azure-key';
    const audio = new Uint8Array(2048);

    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('api.cognitive.microsoft.com')) {
        return new Response('upstream boom', { status: 503 });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(new Request(
      'http://localhost/transcribe?license_key=test-license&language=en',
      {
        method: 'POST',
        headers: {
          'Content-Type': 'audio/wav',
          'Content-Length': String(audio.byteLength),
          'X-STT-Provider': 'azure-mai',
        },
        body: audio,
      },
    ));

    expect(response.status).toBe(502);
  });
});

/**
 * The route no longer knows WHICH provider is geo-blocked, from WHERE, or where
 * a blocked request goes instead — `providers/geo-availability.ts` owns all of
 * that (see its own tests). What the route still owns is carrying the plan out:
 * emit the `fly-replay` header Fly reads, and spend nothing upstream first.
 */
describe('the route carries out the geo-routing plan it is handed', () => {
  const originalRegion = process.env.FLY_REGION;

  beforeEach(() => {
    process.env.ELEVENLABS_API_KEY = 'test-elevenlabs-key';
    process.env.FLY_REGION = 'nrt';
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    if (originalRegion === undefined) {
      delete process.env.FLY_REGION;
    } else {
      process.env.FLY_REGION = originalRegion;
    }
  });

  test('a small ElevenLabs request from a blocked region is replayed, untouched', async () => {
    const audio = new Uint8Array(2048);

    // Any upstream call at all means the replay did not short-circuit the route.
    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      throw new Error(`Unexpected fetch: ${String(input)}`);
    }) as unknown as typeof fetch;

    const app = new Hono();
    app.post('/transcribe', transcribeRoute);
    const response = await app.fetch(new Request(
      'http://localhost/transcribe?license_key=test-license&language=en',
      {
        method: 'POST',
        headers: {
          'Content-Type': 'audio/wav',
          'Content-Length': String(audio.byteLength),
          'X-STT-Provider': 'elevenlabs',
        },
        body: audio,
      },
    ));

    expect(response.status).toBe(200);
    expect(response.headers.get('fly-replay')).toBe('region=iad');
    expect(await response.text()).toBe('');
  });
});
