// GCS SCRATCH-STORAGE PATH (Chirp's large-file route to Speech V2)
//
// lib/gcs-storage.ts is the only thing between a >9.5 MB recording and a
// `gs://` URI, and every one of its failure branches is load-bearing for the
// transcribe route: which errors become a 502 fallback, which fail fast, how
// long an upload is allowed to take, and whether a dead upload still counts as
// an attempt that reached the wire. None of that had a test.
//
// Two mechanics make this testable without touching the network:
//
// 1. `./google-auth` is replaced with mock.module. The factory spreads the real
//    module and overrides only `getGoogleAccessToken` — bun's module registry
//    is process-wide, so a factory that LISTS exports deletes the rest for
//    every file that loads after this one. providers/google-chirp.test.ts
//    installs the same override for the same reason.
//
// 2. lib/gcs-storage is imported through a query-suffixed specifier. That is
//    NOT a cache-busting trick for this file's own benefit — it is how this
//    suite gets the REAL module. providers/google-chirp.test.ts replaces
//    `../lib/gcs-storage` process-wide with a stub, and if it loads first a
//    plain import here would resolve to that stub and assert nothing about the
//    code under test. A distinct specifier is a distinct registry key, so this
//    file exercises the real module whatever order bun walks the tree in.
//    That order is filesystem-dependent, so a plain import passing locally
//    would prove nothing. lib/google-auth.test.ts uses the same escape hatch.
//
// One consequence to expect in `bun test src --coverage`: the query-suffixed
// instance is a separate module, so lib/gcs-storage.ts still reports 0% funcs.
// The coverage number is the artefact; the code under test really does run.
//
// Global `fetch` is mocked (the azure-mai.test.ts pattern) rather than the
// shared providers/utils module, so the real AbortController wiring still runs.

import { afterEach, beforeEach, describe, expect, test } from 'bun:test';
import { mock } from 'bun:test';
import * as realGoogleAuth from './google-auth';
import { ProviderUnavailableError } from '../providers/types';
import { runProviderAttempt } from '../providers/utils';

// ---------------------------------------------------------------------------
// Google OAuth: one string, or a throw. gcs-storage mints the token itself, so
// the credential edge is part of its contract.
// ---------------------------------------------------------------------------
let accessToken = 'test-access-token';
let tokenError: Error | null = null;
let tokenMints = 0;

mock.module('./google-auth', () => ({
  ...realGoogleAuth,
  getGoogleAccessToken: async () => {
    tokenMints += 1;
    if (tokenError) throw tokenError;
    return accessToken;
  },
}));

const REAL_GCS_STORAGE = './gcs-storage.ts?scratch-path';
type GcsStorageModule = typeof import('./gcs-storage');

const gcs = (await import(REAL_GCS_STORAGE)) as GcsStorageModule;

const BUCKET = 'hyperwhisper-stt-scratch';

interface RecordedCall {
  url: string;
  method: string;
  headers: Record<string, string>;
  body: unknown;
}

const calls: RecordedCall[] = [];
let handler: (call: RecordedCall) => Response | Promise<Response> = () =>
  new Response('', { status: 200 });

const originalFetch = globalThis.fetch;
const originalBucket = process.env.GOOGLE_SPEECH_GCS_BUCKET;

beforeEach(() => {
  calls.length = 0;
  accessToken = 'test-access-token';
  tokenError = null;
  tokenMints = 0;
  handler = () => new Response('', { status: 200 });
  process.env.GOOGLE_SPEECH_GCS_BUCKET = BUCKET;

  globalThis.fetch = (async (input: RequestInfo | URL, init: RequestInit = {}) => {
    const headers: Record<string, string> = {};
    for (const [key, value] of Object.entries((init.headers ?? {}) as Record<string, string>)) {
      headers[key.toLowerCase()] = value;
    }
    const call: RecordedCall = {
      url: String(input),
      method: init.method ?? 'GET',
      headers,
      body: init.body,
    };
    calls.push(call);
    return handler(call);
  }) as unknown as typeof fetch;
});

afterEach(() => {
  globalThis.fetch = originalFetch;
  if (originalBucket === undefined) {
    delete process.env.GOOGLE_SPEECH_GCS_BUCKET;
  } else {
    process.env.GOOGLE_SPEECH_GCS_BUCKET = originalBucket;
  }
});

/**
 * The abort deadline is the only part of the timeout budget that is externally
 * observable: `fetchWithTimeoutMs` arms it with `setTimeout(abort, timeoutMs)`
 * and the mocked fetch resolves before it ever fires. Record the delays that
 * the module asks for, then put the real timer back.
 */
async function recordTimerDelays(run: () => Promise<unknown>): Promise<number[]> {
  const delays: number[] = [];
  const realSetTimeout = globalThis.setTimeout;
  globalThis.setTimeout = ((fn: () => void, ms?: number, ...rest: unknown[]) => {
    if (typeof ms === 'number') delays.push(ms);
    return (realSetTimeout as (...a: unknown[]) => unknown)(fn, ms, ...rest);
  }) as unknown as typeof globalThis.setTimeout;
  try {
    await run().catch(() => {});
  } finally {
    globalThis.setTimeout = realSetTimeout;
  }
  return delays;
}

/** Collects the event names the module logs, so "logged as OK" is assertable. */
async function captureConsole(run: () => Promise<unknown>): Promise<{ log: string[]; warn: string[] }> {
  const log: string[] = [];
  const warn: string[] = [];
  const realLog = console.log;
  const realWarn = console.warn;
  console.log = (...args: unknown[]) => { log.push(String(args[0])); };
  console.warn = (...args: unknown[]) => { warn.push(String(args[0])); };
  try {
    await run().catch(() => {});
  } finally {
    console.log = realLog;
    console.warn = realWarn;
  }
  return { log, warn };
}

describe('uploadTranscriptionAudio — bucket gate', () => {
  test('throws before minting a token or hitting the network when no bucket is set', async () => {
    delete process.env.GOOGLE_SPEECH_GCS_BUCKET;

    await expect(gcs.uploadTranscriptionAudio(new ArrayBuffer(8), 'audio/wav'))
      .rejects.toThrow('GOOGLE_SPEECH_GCS_BUCKET not configured');
    expect(tokenMints).toBe(0);
    expect(calls).toHaveLength(0);
  });

  test('treats a whitespace-only bucket as unconfigured', async () => {
    process.env.GOOGLE_SPEECH_GCS_BUCKET = '   ';

    await expect(gcs.uploadTranscriptionAudio(new ArrayBuffer(8), 'audio/wav'))
      .rejects.toThrow('GOOGLE_SPEECH_GCS_BUCKET not configured');
    expect(calls).toHaveLength(0);
  });
});

describe('uploadTranscriptionAudio — the request it builds', () => {
  test('POSTs a simple media upload carrying the bearer token, the audio and its content type', async () => {
    const audio = new Uint8Array([1, 2, 3, 4, 5, 6, 7, 8]).buffer;

    const upload = await gcs.uploadTranscriptionAudio(audio, 'audio/wav');

    expect(calls).toHaveLength(1);
    const call = calls[0]!;
    expect(call.method).toBe('POST');
    expect(call.headers.authorization).toBe('Bearer test-access-token');
    expect(call.headers['content-type']).toBe('audio/wav');
    expect(call.body).toBe(audio);

    const url = new URL(call.url);
    expect(url.origin).toBe('https://storage.googleapis.com');
    expect(url.pathname).toBe(`/upload/storage/v1/b/${BUCKET}/o`);
    expect(url.searchParams.get('uploadType')).toBe('media');
    // The object name is URL-encoded into the query, so the `stt-temp/` prefix
    // must survive as %2F rather than splitting the path.
    expect(url.searchParams.get('name')).toBe(upload.objectName);
    expect(call.url).toContain('stt-temp%2F');
  });

  test('returns a gs:// URI that matches the bucket and object it just wrote', async () => {
    const upload = await gcs.uploadTranscriptionAudio(new ArrayBuffer(8), 'audio/wav');

    expect(upload.bucket).toBe(BUCKET);
    expect(upload.objectName).toMatch(/^stt-temp\/\d+-[0-9a-f-]{36}\.wav$/);
    expect(upload.gcsUri).toBe(`gs://${upload.bucket}/${upload.objectName}`);
  });

  test('names every object uniquely so concurrent uploads cannot overwrite each other', async () => {
    const first = await gcs.uploadTranscriptionAudio(new ArrayBuffer(8), 'audio/wav');
    const second = await gcs.uploadTranscriptionAudio(new ArrayBuffer(8), 'audio/wav');

    expect(first.objectName).not.toBe(second.objectName);
  });

  test('falls back to application/octet-stream when the caller has no content type', async () => {
    const upload = await gcs.uploadTranscriptionAudio(new ArrayBuffer(8), '');

    expect(calls[0]!.headers['content-type']).toBe('application/octet-stream');
    expect(upload.objectName.endsWith('.bin')).toBe(true);
  });

  test.each([
    ['audio/wav', 'wav'],
    ['audio/x-wav', 'wav'],
    ['audio/mpeg', 'mp3'],
    ['audio/mp3', 'mp3'],
    ['audio/mp4', 'm4a'],
    ['audio/m4a', 'm4a'],
    ['audio/webm', 'webm'],
    ['audio/ogg', 'ogg'],
    ['audio/opus', 'ogg'],
    ['audio/flac', 'flac'],
    ['audio/aac', 'aac'],
    ['application/x-unknown', 'bin'],
  ])('gives %s the .%s extension', async (contentType, ext) => {
    const upload = await gcs.uploadTranscriptionAudio(new ArrayBuffer(8), contentType);

    expect(upload.objectName.endsWith(`.${ext}`)).toBe(true);
  });

  test('infers the extension case-insensitively', async () => {
    const upload = await gcs.uploadTranscriptionAudio(new ArrayBuffer(8), 'AUDIO/WEBM');

    expect(upload.objectName.endsWith('.webm')).toBe(true);
  });
});

describe('uploadTranscriptionAudio — timeout budget', () => {
  test('gives a small payload the 30 s floor', async () => {
    const delays = await recordTimerDelays(() =>
      gcs.uploadTranscriptionAudio(new ArrayBuffer(1024), 'audio/wav'));

    expect(delays).toContain(30_000);
  });

  test('scales the budget with payload size — 10 MiB gets 105 s, not the floor', async () => {
    // 10 MiB / 100 KB = 104.8576 chunks, rounded up to 105 seconds.
    const delays = await recordTimerDelays(() =>
      gcs.uploadTranscriptionAudio(new ArrayBuffer(10 * 1024 * 1024), 'audio/wav'));

    expect(delays).toContain(105_000);
    expect(delays).not.toContain(30_000);
  });
});

describe('uploadTranscriptionAudio — failure classification', () => {
  test.each([429, 500, 502, 503])(
    'reports a %s as ProviderUnavailableError so transcribe.ts can fall back',
    async (status) => {
      handler = () => new Response('slow down', { status });

      const error = await gcs.uploadTranscriptionAudio(new ArrayBuffer(8), 'audio/wav')
        .then(() => null, (e: unknown) => e);

      expect(error).toBeInstanceOf(ProviderUnavailableError);
      expect((error as Error).message).toContain(`upload ${status}`);
      expect((error as Error).message).toContain('slow down');
    },
  );

  test.each([400, 403, 404])(
    'lets a %s fail fast as a plain Error instead of cascading to other providers',
    async (status) => {
      handler = () => new Response('denied', { status });

      const error = await gcs.uploadTranscriptionAudio(new ArrayBuffer(8), 'audio/wav')
        .then(() => null, (e: unknown) => e);

      expect(error).toBeInstanceOf(Error);
      expect(error).not.toBeInstanceOf(ProviderUnavailableError);
      expect((error as Error).message).toContain(`GCS upload failed (status=${status}`);
    },
  );

  test('truncates a huge upstream error body instead of logging it whole', async () => {
    handler = () => new Response('x'.repeat(5000), { status: 403 });

    const error = await gcs.uploadTranscriptionAudio(new ArrayBuffer(8), 'audio/wav')
      .then(() => null, (e: unknown) => e);

    expect((error as Error).message).toContain('x'.repeat(400));
    expect((error as Error).message).not.toContain('x'.repeat(401));
  });

  test('survives an unreadable error body', async () => {
    handler = () => new Response(
      new ReadableStream({ start(controller) { controller.error(new Error('torn')); } }),
      { status: 500 },
    );

    const error = await gcs.uploadTranscriptionAudio(new ArrayBuffer(8), 'audio/wav')
      .then(() => null, (e: unknown) => e);

    expect(error).toBeInstanceOf(ProviderUnavailableError);
    expect((error as Error).message).toContain('<unreadable>');
  });

  test('turns an aborted upload into ProviderUnavailableError naming the budget it blew', async () => {
    handler = () => { throw new DOMException('The operation was aborted.', 'AbortError'); };

    const error = await gcs.uploadTranscriptionAudio(new ArrayBuffer(8), 'audio/wav')
      .then(() => null, (e: unknown) => e);

    expect(error).toBeInstanceOf(ProviderUnavailableError);
    expect((error as Error).message).toContain('upload timeout after 30000ms');
  });

  test('turns a connection failure into ProviderUnavailableError, not a 500', async () => {
    handler = () => { throw new TypeError('fetch failed'); };

    const error = await gcs.uploadTranscriptionAudio(new ArrayBuffer(8), 'audio/wav')
      .then(() => null, (e: unknown) => e);

    expect(error).toBeInstanceOf(ProviderUnavailableError);
    expect((error as Error).message).toContain('network error: fetch failed');
  });

  test('lets a credential fault fail fast rather than dressing it as an unavailable provider', async () => {
    // providers/utils.ts documents this split: a bad service account is our
    // configuration failing, not Google being slow, so it must not look like a
    // transient upstream error.
    tokenError = new Error('GOOGLE_SERVICE_ACCOUNT_JSON not configured');

    const error = await gcs.uploadTranscriptionAudio(new ArrayBuffer(8), 'audio/wav')
      .then(() => null, (e: unknown) => e);

    expect(error).toBeInstanceOf(Error);
    expect(error).not.toBeInstanceOf(ProviderUnavailableError);
    expect((error as Error).message).toBe('GOOGLE_SERVICE_ACCOUNT_JSON not configured');
    expect(calls).toHaveLength(0);
  });
});

describe('uploadTranscriptionAudio — attempt measurement', () => {
  test('marks the attempt as having reached the wire on success', async () => {
    const state = { reachedProvider: false };

    await runProviderAttempt(state, () =>
      gcs.uploadTranscriptionAudio(new ArrayBuffer(8), 'audio/wav'));

    expect(state.reachedProvider).toBe(true);
  });

  test('still marks it when the upload dies mid-flight', async () => {
    handler = () => { throw new TypeError('fetch failed'); };
    const state = { reachedProvider: false };

    await runProviderAttempt(state, () =>
      gcs.uploadTranscriptionAudio(new ArrayBuffer(8), 'audio/wav')).catch(() => {});

    expect(state.reachedProvider).toBe(true);
  });

  test('does not mark it when the token never minted — nothing left the process', async () => {
    tokenError = new Error('GOOGLE_SERVICE_ACCOUNT_JSON not configured');
    const state = { reachedProvider: false };

    await runProviderAttempt(state, () =>
      gcs.uploadTranscriptionAudio(new ArrayBuffer(8), 'audio/wav')).catch(() => {});

    expect(state.reachedProvider).toBe(false);
  });
});

describe('deleteTranscriptionAudio', () => {
  const ref = { bucket: BUCKET, objectName: 'stt-temp/1756600000000-abc.wav' };

  test('DELETEs the object with the name percent-encoded into a single path segment', async () => {
    handler = () => new Response('', { status: 204 });

    await gcs.deleteTranscriptionAudio(ref);

    expect(calls).toHaveLength(1);
    expect(calls[0]!.method).toBe('DELETE');
    expect(calls[0]!.headers.authorization).toBe('Bearer test-access-token');
    expect(calls[0]!.url).toBe(
      `https://storage.googleapis.com/storage/v1/b/${BUCKET}/o/${encodeURIComponent(ref.objectName)}`,
    );
    expect(calls[0]!.url).toContain('stt-temp%2F');
  });

  test('uses a flat 10 s budget — cleanup must not hold a machine open', async () => {
    handler = () => new Response('', { status: 204 });

    const delays = await recordTimerDelays(() => gcs.deleteTranscriptionAudio(ref));

    expect(delays).toContain(10_000);
  });

  test('accepts a 404 as already gone rather than warning about it', async () => {
    // Re-entrant cleanup and the bucket's own lifecycle rule both race this
    // delete, so a 404 is the expected outcome, not an incident.
    handler = () => new Response('Not Found', { status: 404 });

    const logged = await captureConsole(() => gcs.deleteTranscriptionAudio(ref));

    expect(logged.log).toContain('gcs.delete_ok');
    expect(logged.warn).not.toContain('gcs.delete_failed');
  });

  test.each([403, 500])('swallows a %s so a failed cleanup never becomes a 5xx', async (status) => {
    handler = () => new Response('nope', { status });

    const logged = await captureConsole(() => gcs.deleteTranscriptionAudio(ref));

    expect(calls).toHaveLength(1);
    expect(logged.warn).toContain('gcs.delete_failed');
    expect(logged.log).not.toContain('gcs.delete_ok');
  });

  test('swallows a connection failure', async () => {
    handler = () => { throw new TypeError('fetch failed'); };

    await expect(gcs.deleteTranscriptionAudio(ref)).resolves.toBeUndefined();
  });

  test('swallows a credential fault and never reaches the network', async () => {
    tokenError = new Error('GOOGLE_SERVICE_ACCOUNT_JSON not configured');

    await expect(gcs.deleteTranscriptionAudio(ref)).resolves.toBeUndefined();
    expect(calls).toHaveLength(0);
  });
});

describe('isGcsTranscriptionBucketConfigured re-export', () => {
  test('answers from the environment, so callers can gate before uploading', () => {
    process.env.GOOGLE_SPEECH_GCS_BUCKET = BUCKET;
    expect(gcs.isGcsTranscriptionBucketConfigured()).toBe(true);

    delete process.env.GOOGLE_SPEECH_GCS_BUCKET;
    expect(gcs.isGcsTranscriptionBucketConfigured()).toBe(false);
  });
});
