import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { AudioTooLargeError, ProviderUnavailableError } from './types';
import { computeGoogleChirpTranscriptionCost } from '../lib/cost-calculator';

// ---------------------------------------------------------------------------
// Mock the GCS scratch-storage module. google-chirp.ts only knows the shape
// of TranscriptionAudioRef/Upload — the real module does real network I/O,
// which these tests must never touch.
// ---------------------------------------------------------------------------
let gcsConfigured = false;
let uploadResult = {
  bucket: 'test-bucket',
  objectName: 'stt-temp/audio.wav',
  gcsUri: 'gs://test-bucket/stt-temp/audio.wav',
};
let uploadShouldThrow: Error | null = null;
const uploadCalls: Array<{ bytes: number; contentType: string }> = [];
const deleteCalls: Array<{ bucket: string; objectName: string }> = [];

mock.module('../lib/gcs-storage', () => ({
  isGcsTranscriptionBucketConfigured: () => gcsConfigured,
  uploadTranscriptionAudio: async (audio: ArrayBuffer, contentType: string) => {
    uploadCalls.push({ bytes: audio.byteLength, contentType });
    if (uploadShouldThrow) throw uploadShouldThrow;
    return uploadResult;
  },
  deleteTranscriptionAudio: async (ref: { bucket: string; objectName: string }) => {
    deleteCalls.push(ref);
  },
}));

// ---------------------------------------------------------------------------
// Mock the Google OAuth helper — no real service-account JSON or Redis.
// ---------------------------------------------------------------------------
let accessToken = 'test-access-token';
let invalidateCalls = 0;

mock.module('../lib/google-auth', () => ({
  getGoogleAccessToken: async () => accessToken,
  invalidateGoogleAccessToken: async () => { invalidateCalls += 1; },
}));

// ---------------------------------------------------------------------------
// Mock global fetch (same pattern as azure-mai.test.ts) rather than the
// shared providers/utils module — bun's module registry is process-wide, and
// overriding fetchWithTimeout there would leak into every other provider's
// test file that imports it for real. Real fetchWithTimeout still runs, so
// the actual AbortController/timeout wiring is exercised end to end.
// ---------------------------------------------------------------------------
type FetchHandler = (url: string, init: RequestInit) => Response | Promise<Response>;
let fetchHandler: FetchHandler = () => new Response('unhandled', { status: 500 });
const originalFetch = globalThis.fetch;

const { transcribeWithGoogleChirp, INLINE_AUDIO_MAX_BYTES } = await import('./google-chirp');

const ENV_KEYS = ['GOOGLE_PROJECT_ID', 'GOOGLE_SPEECH_REGION'] as const;
const savedEnv: Record<string, string | undefined> = {};

beforeEach(() => {
  for (const key of ENV_KEYS) savedEnv[key] = process.env[key];
  process.env.GOOGLE_PROJECT_ID = 'test-project';
  delete process.env.GOOGLE_SPEECH_REGION;

  gcsConfigured = false;
  uploadResult = {
    bucket: 'test-bucket',
    objectName: 'stt-temp/audio.wav',
    gcsUri: 'gs://test-bucket/stt-temp/audio.wav',
  };
  uploadShouldThrow = null;
  uploadCalls.length = 0;
  deleteCalls.length = 0;
  accessToken = 'test-access-token';
  invalidateCalls = 0;
  fetchHandler = () => new Response('unhandled', { status: 500 });
  globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) =>
    fetchHandler(String(input), init ?? {})) as unknown as typeof fetch;
});

afterEach(() => {
  globalThis.fetch = originalFetch;
  for (const key of ENV_KEYS) {
    if (savedEnv[key] === undefined) delete process.env[key];
    else process.env[key] = savedEnv[key];
  }
});

function syncRecognizeResponse(transcript = 'hello world', languageCode = 'en-US', totalBilledDuration?: string) {
  return Response.json({
    results: transcript ? [{ alternatives: [{ transcript }], languageCode }] : [],
    metadata: totalBilledDuration ? { totalBilledDuration } : {},
  });
}

describe('transcribeWithGoogleChirp — input gates (no upstream call)', () => {
  test('throws a plain Error when GOOGLE_PROJECT_ID is not configured', async () => {
    delete process.env.GOOGLE_PROJECT_ID;
    await expect(transcribeWithGoogleChirp(new ArrayBuffer(100), 'audio/wav'))
      .rejects.toThrow('GOOGLE_PROJECT_ID not configured');
    expect((globalThis.fetch as any).mock.calls.length).toBe(0);
  });

  test('rejects audio over the inline byte cap with AudioTooLargeError when no GCS bucket is configured', async () => {
    gcsConfigured = false;
    const oversized = new ArrayBuffer(INLINE_AUDIO_MAX_BYTES + 1);
    await expect(transcribeWithGoogleChirp(oversized, 'audio/wav')).rejects.toThrow(AudioTooLargeError);
    expect((globalThis.fetch as any).mock.calls.length).toBe(0);
    expect(uploadCalls.length).toBe(0);
  });

  test('rejects audio that fits the byte cap but exceeds the ~55s inline duration cap, when no GCS bucket is configured', async () => {
    gcsConfigured = false;
    // webm ~8,000 bytes/sec estimate: 480,000 bytes ~= 60s, over the 55s inline
    // cutoff, but nowhere near the 9.5MB byte cap.
    const audio = new ArrayBuffer(480_000);
    await expect(transcribeWithGoogleChirp(audio, 'audio/webm')).rejects.toThrow(AudioTooLargeError);
    expect((globalThis.fetch as any).mock.calls.length).toBe(0);
  });
});

describe('transcribeWithGoogleChirp — inline sync recognize path', () => {
  test('calls sync recognize (not batchRecognize) and returns transcript, language, duration and cost', async () => {
    fetchHandler = (url) => {
      expect(url).toContain(':recognize');
      expect(url).not.toContain('batchRecognize');
      return syncRecognizeResponse('hello world', 'en-US', '5.5s');
    };
    const result = await transcribeWithGoogleChirp(new ArrayBuffer(1000), 'audio/wav');
    expect(result.text).toBe('hello world');
    expect(result.language).toBe('en-US');
    expect(result.durationSeconds).toBe(5.5);
    expect(result.source).toBe('google-chirp');
    expect(result.costUsd).toBeCloseTo(computeGoogleChirpTranscriptionCost(5.5), 8);
    expect((globalThis.fetch as any).mock.calls.length).toBe(1);
  });

  test('sends the exact BCP-47 code (not lowercased) for a monolingual language', async () => {
    let sentBody: any;
    fetchHandler = (_url, init) => {
      sentBody = JSON.parse(init.body as string);
      return syncRecognizeResponse('bonjour', 'fr-FR', '2s');
    };
    await transcribeWithGoogleChirp(new ArrayBuffer(1000), 'audio/wav', 'fr-FR');
    expect(sentBody.config.languageCodes).toEqual(['fr-FR']);
  });

  test('treats "auto" (any case) as the unrestricted-detection sentinel, not a monolingual code', async () => {
    let sentBody: any;
    fetchHandler = (_url, init) => {
      sentBody = JSON.parse(init.body as string);
      return syncRecognizeResponse('hi', 'en-US', '1s');
    };
    await transcribeWithGoogleChirp(new ArrayBuffer(1000), 'audio/wav', 'AUTO');
    expect(sentBody.config.languageCodes).toEqual(['auto']);
  });

  test('an empty result set maps to a zero-cost no_speech result', async () => {
    fetchHandler = () => syncRecognizeResponse('', 'en-US');
    const result = await transcribeWithGoogleChirp(new ArrayBuffer(1000), 'audio/wav');
    expect(result.source).toBe('no_speech');
    expect(result.text).toBe('');
    expect(result.costUsd).toBe(0);
  });

  test('falls back to a byte-length duration estimate when totalBilledDuration is missing, instead of zero-billing', async () => {
    fetchHandler = () => syncRecognizeResponse('hello', 'en-US'); // no metadata at all
    const audioBytes = 160_000; // wav @ 32,000 B/s estimate => 5s
    const result = await transcribeWithGoogleChirp(new ArrayBuffer(audioBytes), 'audio/wav');
    expect(result.durationSeconds).toBeCloseTo(5, 5);
    expect(result.costUsd).toBeCloseTo(computeGoogleChirpTranscriptionCost(5), 8);
  });

  test('carries the mocked Google access token as a Bearer Authorization header', async () => {
    accessToken = 'abc-123-token';
    let authHeader = '';
    fetchHandler = (_url, init) => {
      authHeader = (init.headers as Record<string, string>)['Authorization'];
      return syncRecognizeResponse('hi');
    };
    await transcribeWithGoogleChirp(new ArrayBuffer(1000), 'audio/wav');
    expect(authHeader).toBe('Bearer abc-123-token');
  });
});

describe('transcribeWithGoogleChirp — sync recognize error mapping', () => {
  test('401 maps to a plain Error (invalid/expired credentials)', async () => {
    fetchHandler = () => new Response('unauthorized', { status: 401 });
    await expect(transcribeWithGoogleChirp(new ArrayBuffer(1000), 'audio/wav'))
      .rejects.toThrow('Google Speech credentials are invalid or expired');
  });

  test('403 maps to a plain Error (access denied)', async () => {
    fetchHandler = () => new Response('forbidden', { status: 403 });
    await expect(transcribeWithGoogleChirp(new ArrayBuffer(1000), 'audio/wav'))
      .rejects.toThrow('Google Speech access denied');
  });

  test('429 maps to ProviderUnavailableError (Chirp is self-only, but the request itself may be retried)', async () => {
    fetchHandler = () => new Response('rate limited', { status: 429 });
    await expect(transcribeWithGoogleChirp(new ArrayBuffer(1000), 'audio/wav'))
      .rejects.toThrow(ProviderUnavailableError);
  });

  test('a 5xx maps to ProviderUnavailableError', async () => {
    fetchHandler = () => new Response('boom', { status: 503 });
    await expect(transcribeWithGoogleChirp(new ArrayBuffer(1000), 'audio/wav'))
      .rejects.toThrow(ProviderUnavailableError);
  });

  test('an unmapped 4xx surfaces as a plain Error carrying the status', async () => {
    fetchHandler = () => new Response('bad request', { status: 400 });
    await expect(transcribeWithGoogleChirp(new ArrayBuffer(1000), 'audio/wav'))
      .rejects.toThrow('Google Chirp error: 400');
  });
});

describe('transcribeWithGoogleChirp — GCS + batchRecognize path', () => {
  beforeEach(() => {
    gcsConfigured = true;
  });

  function bigAudio() {
    return new ArrayBuffer(INLINE_AUDIO_MAX_BYTES + 1); // forces gcs+batch on bytes alone
  }

  test('uploads to GCS, submits+polls batchRecognize, merges the file-level metadata, and deletes the scratch object on success', async () => {
    let pollCount = 0;
    fetchHandler = (url) => {
      if (url.includes(':batchRecognize')) {
        return Response.json({ name: 'operations/op-1' });
      }
      if (url.endsWith(':cancel')) {
        throw new Error('must not cancel a successfully completed operation');
      }
      pollCount += 1;
      if (pollCount === 1) {
        return Response.json({ done: false });
      }
      return Response.json({
        done: true,
        response: {
          results: {
            [uploadResult.gcsUri]: {
              transcript: {
                results: [{ alternatives: [{ transcript: 'batched transcript' }], languageCode: 'en-US' }],
              },
              // The documented gotcha: totalBilledDuration lives on the
              // file-level `metadata`, NOT on `transcript.metadata`.
              metadata: { totalBilledDuration: '12.3s' },
            },
          },
        },
      });
    };

    const result = await transcribeWithGoogleChirp(bigAudio(), 'audio/wav');

    expect(result.text).toBe('batched transcript');
    expect(result.language).toBe('en-US');
    expect(result.durationSeconds).toBe(12.3);
    expect(result.source).toBe('google-chirp');
    expect(uploadCalls).toEqual([{ bytes: INLINE_AUDIO_MAX_BYTES + 1, contentType: 'audio/wav' }]);
    expect(deleteCalls).toEqual([{ bucket: 'test-bucket', objectName: 'stt-temp/audio.wav' }]);
    // batchRecognize submit + at least 2 polls (done:false then done:true).
    expect((globalThis.fetch as any).mock.calls.length).toBeGreaterThanOrEqual(3);
  });

  test('audio that fits the byte cap but fails the duration gate is routed to GCS+batch instead of erroring', async () => {
    fetchHandler = (url) => {
      if (url.includes(':batchRecognize')) return Response.json({ name: 'operations/op-2' });
      return Response.json({
        done: true,
        response: {
          results: {
            [uploadResult.gcsUri]: {
              transcript: { results: [{ alternatives: [{ transcript: 'long clip' }] }] },
              metadata: { totalBilledDuration: '60s' },
            },
          },
        },
      });
    };
    const audio = new ArrayBuffer(480_000); // webm, ~60s estimate — over the 55s inline cutoff
    const result = await transcribeWithGoogleChirp(audio, 'audio/webm');
    expect(result.text).toBe('long clip');
    expect(uploadCalls.length).toBe(1);
  });

  test('a batchRecognize operation-level error cancels the operation, still deletes the GCS object, and rejects', async () => {
    let cancelled = false;
    fetchHandler = (url) => {
      if (url.includes(':batchRecognize')) return Response.json({ name: 'operations/op-3' });
      if (url.endsWith(':cancel')) {
        cancelled = true;
        return Response.json({});
      }
      return Response.json({ done: true, error: { code: 3, message: 'bad audio' } });
    };

    await expect(transcribeWithGoogleChirp(bigAudio(), 'audio/wav'))
      .rejects.toThrow('Google Speech batchRecognize failed (3): bad audio');
    expect(cancelled).toBe(true);
    expect(deleteCalls.length).toBe(1);
  });

  test('a missing fileResult for the submitted gcsUri returns an empty (no_speech) result instead of throwing', async () => {
    fetchHandler = (url) => {
      if (url.includes(':batchRecognize')) return Response.json({ name: 'operations/op-4' });
      return Response.json({ done: true, response: { results: {} } });
    };
    const result = await transcribeWithGoogleChirp(bigAudio(), 'audio/wav');
    expect(result.source).toBe('no_speech');
    expect(result.costUsd).toBe(0);
  });

  test('refreshes the access token exactly once on a mid-poll 401 and retries, rather than failing the request', async () => {
    let pollAttempt = 0;
    fetchHandler = (url) => {
      if (url.includes(':batchRecognize')) return Response.json({ name: 'operations/op-5' });
      if (url.endsWith(':cancel')) return Response.json({});
      pollAttempt += 1;
      if (pollAttempt === 1) {
        return new Response('unauthorized', { status: 401 });
      }
      return Response.json({
        done: true,
        response: {
          results: {
            [uploadResult.gcsUri]: {
              transcript: { results: [{ alternatives: [{ transcript: 'ok after refresh' }] }] },
              metadata: { totalBilledDuration: '3s' },
            },
          },
        },
      });
    };

    const result = await transcribeWithGoogleChirp(bigAudio(), 'audio/wav');
    expect(result.text).toBe('ok after refresh');
    expect(invalidateCalls).toBe(1);
  });

  test('a second consecutive 401 (after the one-shot refresh already fired) fails the request', async () => {
    fetchHandler = (url) => {
      if (url.includes(':batchRecognize')) return Response.json({ name: 'operations/op-6' });
      if (url.endsWith(':cancel')) return Response.json({});
      return new Response('unauthorized', { status: 401 });
    };

    await expect(transcribeWithGoogleChirp(bigAudio(), 'audio/wav'))
      .rejects.toThrow('Google Speech credentials are invalid or expired');
    expect(invalidateCalls).toBe(1);
  });

  test('a GCS upload failure propagates without attempting to delete a scratch object that was never created', async () => {
    uploadShouldThrow = new ProviderUnavailableError('GCS upload', 'network error: boom');
    await expect(transcribeWithGoogleChirp(bigAudio(), 'audio/wav')).rejects.toThrow(ProviderUnavailableError);
    expect(deleteCalls.length).toBe(0);
  });
});
