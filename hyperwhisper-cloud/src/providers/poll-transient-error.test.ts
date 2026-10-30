// Regression for #2923: a single transient transport failure (network reset or
// per-request timeout) on ONE poll GET must not abort an already-running async
// job. fetchWithTimeout turns both into ProviderUnavailableError; the poll loops
// treat that like a 5xx poll and keep polling until their deadline.
//
// Fake fetch, like the neighbouring provider tests. The poll sleeps are made
// instant by firing the poll-interval setTimeout delays at once (the same
// trick google-chirp.test.ts uses), so the file needs no real 10 s of sleeps.
import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { transcribeWithAssemblyAI } from './assemblyai';
import { transcribeWithSoniox } from './soniox';
import { ProviderUnavailableError } from './types';
import { captureProviderEvent } from './test-support';
import * as realGcsStorage from '../lib/gcs-storage';
import * as realGoogleAuth from '../lib/google-auth';
import { stubEnv } from '../test-support/env';

// Chirp's batch path needs GCS + an access token. Spread the real modules and
// override only what google-chirp.ts calls: bun's module registry is
// process-wide (see the note in google-chirp.test.ts).
const chirpGcsUri = 'gs://test-bucket/stt-temp/audio.wav';
mock.module('../lib/gcs-storage', () => ({
  ...realGcsStorage,
  // Env-driven, like google-chirp.test.ts's mock: a constant `true` here would
  // leak into the route tests' no-bucket 413 cases.
  isGcsTranscriptionBucketConfigured: () => Boolean(process.env.GOOGLE_SPEECH_GCS_BUCKET?.trim()),
  uploadTranscriptionAudio: async () => ({ bucket: 'test-bucket', objectName: 'stt-temp/audio.wav', gcsUri: chirpGcsUri }),
  deleteTranscriptionAudio: async () => {},
}));
mock.module('../lib/google-auth', () => ({
  ...realGoogleAuth,
  getGoogleAccessToken: async () => 'test-access-token',
  invalidateGoogleAccessToken: async () => {},
}));
const { transcribeWithGoogleChirp, INLINE_AUDIO_MAX_BYTES } = await import('./google-chirp');

const originalFetch = globalThis.fetch;
const originalSetTimeout = globalThis.setTimeout;
const originalNow = performance.now;
const json = (b: unknown, status = 200) =>
  new Response(JSON.stringify(b), { status, headers: { 'content-type': 'application/json' } });

// AssemblyAI 2.5 s, Soniox 1 s, Chirp 500 ms first + 750 ms after.
const POLL_DELAYS = new Set([2_500, 1_000, 500, 750]);
let fakeNowMs = 0;

let restoreEnv: Array<() => void> = [];

beforeEach(() => {
  const fakeKey = ['test', 'key'].join('-');
  restoreEnv = [
    stubEnv('ASSEMBLYAI_API_KEY', fakeKey),
    stubEnv('SONIOX_API_KEY', fakeKey),
    stubEnv('GOOGLE_PROJECT_ID', 'test-project'),
    stubEnv('GOOGLE_SPEECH_REGION', undefined),
    stubEnv('GOOGLE_SPEECH_GCS_BUCKET', 'test-bucket'),
    stubEnv('STT_PROVIDER_TIMEOUT_MS', undefined),
  ];
  // Poll sleeps fire at once and advance a fake clock, so the poll deadlines
  // (240 s / 300 s) are reached in milliseconds of real time.
  fakeNowMs = 0;
  performance.now = () => fakeNowMs;
  globalThis.setTimeout = ((handler: (...args: unknown[]) => void, delay?: number, ...args: unknown[]) => {
    const ms = delay ?? 0;
    if (POLL_DELAYS.has(ms)) {
      fakeNowMs += ms;
      handler(...args);
      return 0;
    }
    return originalSetTimeout(handler, ms, ...args);
  }) as typeof setTimeout;
});

afterEach(() => {
  globalThis.fetch = originalFetch;
  globalThis.setTimeout = originalSetTimeout;
  performance.now = originalNow;
  for (const restore of restoreEnv) restore();
});

const networkBlip = () => { throw new TypeError('fetch failed: ECONNRESET'); };
// What fetch rejects with when fetchWithTimeout's AbortController fires.
const perRequestTimeout = () => { throw new DOMException('The operation was aborted.', 'AbortError'); };

describe('async poll loop survives one transport blip (#2923)', () => {
  test('AssemblyAI: a network error on poll #1 is retried, poll #2 returns the transcript', async () => {
    let polls = 0;
    globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      if (url.endsWith('/v2/upload')) return json({ upload_url: 'https://cdn/x' });
      if (url.endsWith('/v2/transcript') && method === 'POST') return json({ id: 'tr-1' });
      if (url.endsWith('/v2/transcript/tr-1') && method === 'GET') {
        polls += 1;
        if (polls === 1) networkBlip();
        return json({ status: 'completed', text: 'hello world', audio_duration: 5, speech_model_used: 'universal-3-5-pro' });
      }
      if (method === 'DELETE') return json({});
      throw new Error(`unexpected ${method} ${url}`);
    }) as typeof fetch;

    // m4a + no language -> async path (sync skipped)
    let result: unknown;
    let error: unknown;
    const logged = await captureProviderEvent('provider.poll_transport_error', async () => {
      try {
        result = await transcribeWithAssemblyAI(new ArrayBuffer(48_000), 'audio/mp4', undefined, undefined, {});
      } catch (e) { error = e; }
    });
    expect(error).toBeUndefined();
    expect((result as { text: string }).text).toBe('hello world');
    expect(polls).toBe(2);
    expect(logged).toMatchObject({ provider: 'assemblyai', kind: 'network_error', polls: 1 });
  });

  test('AssemblyAI: a per-request timeout on poll #1 is retried too', async () => {
    let polls = 0;
    let creates = 0;
    globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      if (url.endsWith('/v2/upload')) return json({ upload_url: 'https://cdn/x' });
      if (url.endsWith('/v2/transcript') && method === 'POST') { creates += 1; return json({ id: 'tr-1' }); }
      if (url.endsWith('/v2/transcript/tr-1') && method === 'GET') {
        polls += 1;
        if (polls === 1) perRequestTimeout();
        return json({ status: 'completed', text: 'hello world', audio_duration: 5, speech_model_used: 'universal-3-5-pro' });
      }
      if (method === 'DELETE') return json({});
      throw new Error(`unexpected ${method} ${url}`);
    }) as typeof fetch;

    const logged = await captureProviderEvent('provider.poll_transport_error', async () => {
      const result = await transcribeWithAssemblyAI(new ArrayBuffer(48_000), 'audio/mp4', undefined, undefined, {});
      expect(result.text).toBe('hello world');
    });
    expect(logged).toMatchObject({ kind: 'timeout', polls: 1 });
    // The create step is never re-run: one billed upstream job.
    expect(creates).toBe(1);
  });

  test('AssemblyAI: transport errors on every poll still end at the poll deadline and delete the transcript', async () => {
    const deletes: string[] = [];
    globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      if (url.endsWith('/v2/upload')) return json({ upload_url: 'https://cdn/x' });
      if (url.endsWith('/v2/transcript') && method === 'POST') return json({ id: 'tr-1' });
      if (url.endsWith('/v2/transcript/tr-1') && method === 'GET') networkBlip();
      if (method === 'DELETE') { deletes.push(url); return json({}); }
      throw new Error(`unexpected ${method} ${url}`);
    }) as typeof fetch;

    const error = await transcribeWithAssemblyAI(new ArrayBuffer(48_000), 'audio/mp4', undefined, undefined, {})
      .then(() => undefined, (e: unknown) => e);
    expect(error).toBeInstanceOf(ProviderUnavailableError);
    expect((error as Error).message).toContain('poll deadline exceeded');
    expect(deletes).toEqual(['https://api.assemblyai.com/v2/transcript/tr-1']);
  });

  test('AssemblyAI: an auth failure on a poll still hard-fails at once', async () => {
    let polls = 0;
    globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      if (url.endsWith('/v2/upload')) return json({ upload_url: 'https://cdn/x' });
      if (url.endsWith('/v2/transcript') && method === 'POST') return json({ id: 'tr-1' });
      if (url.endsWith('/v2/transcript/tr-1') && method === 'GET') {
        polls += 1;
        if (polls === 1) networkBlip();
        return json({ error: 'unauthorized' }, 401);
      }
      if (method === 'DELETE') return json({});
      throw new Error(`unexpected ${method} ${url}`);
    }) as typeof fetch;

    await expect(transcribeWithAssemblyAI(new ArrayBuffer(48_000), 'audio/mp4', undefined, undefined, {}))
      .rejects.toThrow('AssemblyAI API key is invalid or unauthorized');
    expect(polls).toBe(2);
  });

  test('Soniox: a network error on poll #1 is retried, poll #2 completes', async () => {
    let polls = 0;
    globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      if (url.endsWith('/v1/files') && method === 'POST') return json({ id: 'file-1' });
      if (url.endsWith('/v1/transcriptions') && method === 'POST') return json({ id: 'job-1' });
      if (url.endsWith('/v1/transcriptions/job-1') && method === 'GET') {
        polls += 1;
        if (polls === 1) networkBlip();
        return json({ status: 'completed', audio_duration_ms: 5000 });
      }
      if (url.endsWith('/v1/transcriptions/job-1/transcript')) return json({ text: 'hello world', tokens: [{ language: 'en' }] });
      if (method === 'DELETE') return json({});
      throw new Error(`unexpected ${method} ${url}`);
    }) as typeof fetch;

    let result: unknown;
    let error: unknown;
    const logged = await captureProviderEvent('provider.poll_transport_error', async () => {
      try {
        result = await transcribeWithSoniox(new ArrayBuffer(48_000), 'audio/wav', 'en', undefined, {});
      } catch (e) { error = e; }
    });
    expect(error).toBeUndefined();
    expect((result as { text: string }).text).toBe('hello world');
    expect(logged).toMatchObject({ provider: 'soniox', kind: 'network_error', polls: 1 });
  });

  test('Soniox: a blip while the job is still processing leaves the job running (DELETE 409) instead of polling on', async () => {
    let polls = 0;
    const deletes: Array<{ path: string; status: number }> = [];
    let processing = true;
    globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      if (url.endsWith('/v1/files') && method === 'POST') return json({ id: 'file-1' });
      if (url.endsWith('/v1/transcriptions') && method === 'POST') return json({ id: 'job-1' });
      if (url.endsWith('/v1/transcriptions/job-1') && method === 'GET') {
        polls += 1;
        if (polls === 1) networkBlip();
        processing = false;
        return json({ status: 'completed', audio_duration_ms: 5000 });
      }
      if (url.endsWith('/v1/transcriptions/job-1/transcript')) return json({ text: 'hello world' });
      if (method === 'DELETE') {
        // Soniox refuses to delete a transcription that is still processing.
        const status = url.includes('/v1/transcriptions/') && processing ? 409 : 200;
        deletes.push({ path: new URL(url).pathname, status });
        return json({}, status);
      }
      throw new Error(`unexpected ${method} ${url}`);
    }) as typeof fetch;

    try { await transcribeWithSoniox(new ArrayBuffer(48_000), 'audio/wav', 'en', undefined, {}); } catch { /* asserted below */ }
    // Expected: no cleanup DELETE of the transcription was refused as still-processing.
    expect(deletes.filter((d) => d.status === 409)).toEqual([]);
    expect(deletes.map((d) => d.path)).toContain('/v1/transcriptions/job-1');
  });

  test('Soniox: transport errors on every poll still end at the poll deadline', async () => {
    let creates = 0;
    globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      if (url.endsWith('/v1/files') && method === 'POST') return json({ id: 'file-1' });
      if (url.endsWith('/v1/transcriptions') && method === 'POST') { creates += 1; return json({ id: 'job-1' }); }
      if (url.endsWith('/v1/transcriptions/job-1') && method === 'GET') perRequestTimeout();
      if (method === 'DELETE') return json({});
      throw new Error(`unexpected ${method} ${url}`);
    }) as typeof fetch;

    const error = await transcribeWithSoniox(new ArrayBuffer(48_000), 'audio/wav', 'en', undefined, {})
      .then(() => undefined, (e: unknown) => e);
    expect(error).toBeInstanceOf(ProviderUnavailableError);
    expect((error as Error).message).toContain('poll deadline exceeded');
    expect(creates).toBe(1);
  });

  test('Google Chirp: a network error on a batch operation poll is retried, the next poll returns the transcript', async () => {
    let polls = 0;
    let submits = 0;
    let cancels = 0;
    globalThis.fetch = (async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes(':batchRecognize')) { submits += 1; return json({ name: 'operations/op-1' }); }
      if (url.endsWith(':cancel')) { cancels += 1; return json({}); }
      if (url.endsWith('/operations/op-1')) {
        polls += 1;
        if (polls === 1) networkBlip();
        return json({
          done: true,
          response: {
            results: {
              [chirpGcsUri]: {
                transcript: { results: [{ alternatives: [{ transcript: 'hello world' }], languageCode: 'en-US' }] },
                metadata: { totalBilledDuration: '5s' },
              },
            },
          },
        });
      }
      throw new Error(`unexpected ${url}`);
    }) as typeof fetch;

    let result: unknown;
    let error: unknown;
    const logged = await captureProviderEvent('provider.batch_poll_transient', async () => {
      try {
        result = await transcribeWithGoogleChirp(new ArrayBuffer(INLINE_AUDIO_MAX_BYTES + 1), 'audio/wav');
      } catch (e) { error = e; }
    });
    expect(error).toBeUndefined();
    expect((result as { text: string }).text).toBe('hello world');
    expect(logged).toMatchObject({ kind: 'network_error' });
    expect(polls).toBe(2);
    expect(submits).toBe(1);
    expect(cancels).toBe(0);
  });

  test('Google Chirp: transport errors on every batch poll still end at the deadline and cancel the operation', async () => {
    let submits = 0;
    let cancels = 0;
    globalThis.fetch = (async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes(':batchRecognize')) { submits += 1; return json({ name: 'operations/op-1' }); }
      if (url.endsWith(':cancel')) { cancels += 1; return json({}); }
      if (url.endsWith('/operations/op-1')) perRequestTimeout();
      throw new Error(`unexpected ${url}`);
    }) as typeof fetch;

    const error = await transcribeWithGoogleChirp(new ArrayBuffer(INLINE_AUDIO_MAX_BYTES + 1), 'audio/wav')
      .then(() => undefined, (e: unknown) => e);
    expect(error).toBeInstanceOf(ProviderUnavailableError);
    expect((error as Error).message).toContain('batchRecognize did not complete');
    expect(submits).toBe(1);
    expect(cancels).toBe(1);
  });
});
