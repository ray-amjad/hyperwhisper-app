// #2176: Chirp's Speech 200 bodies go through the shared `readJson200`
// contract. These tests drive the real /transcribe route against a Speech API
// whose 200 body is empty, non-JSON, `null` or an array, and assert the client
// gets the self-only 502 (a `bad_response` is an unavailable provider), never
// the 500 a plain Error gave, and that no log line carries the body.

import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { Hono } from 'hono';
import { stubEnv } from '../test-support/env';
import * as realGoogleAuth from '../lib/google-auth';

mock.module('../lib/redis', () => ({
  redis: {},
  isIPBlocked: async () => false,
  getCachedLicense: async () => ({ isValid: true, credits: 1000, cachedAt: 'cached' }),
  cacheLicense: async () => {},
}));

// Keep the spread: bun's module registry is process-wide (see google-chirp.test.ts).
mock.module('../lib/google-auth', () => ({
  ...realGoogleAuth,
  getGoogleAccessToken: async () => 'test-access-token',
  invalidateGoogleAccessToken: async () => {},
}));

const { transcribeRoute } = await import('./transcribe');
const { drainPendingDeductions } = await import('../middleware/credits');

const MARKER = 'SECRET_TRANSCRIPT_MARKER_2176';
const CONSOLE_METHODS = ['log', 'info', 'warn', 'error', 'debug'] as const;
type ConsoleMethod = typeof CONSOLE_METHODS[number];
const originalConsole = Object.fromEntries(CONSOLE_METHODS.map((m) => [m, console[m]])) as Record<ConsoleMethod, typeof console.log>;
const originalFetch = globalThis.fetch;

let logged: string[] = [];
let speechCalls = 0;
let restoreEnv: Array<() => void> = [];

function speechAnswers200(body: string, contentType: string): void {
  globalThis.fetch = mock(async (input: RequestInfo | URL) => {
    const url = String(input);
    if (url.includes('/api/license/')) return Response.json({ credits_remaining: 999, success: true });
    if (url.includes('speech.googleapis.com')) {
      speechCalls += 1;
      return new Response(body, { status: 200, headers: { 'content-type': contentType } });
    }
    throw new Error(`unexpected upstream call: ${url}`);
  }) as unknown as typeof fetch;
}

beforeEach(() => {
  logged = [];
  speechCalls = 0;
  restoreEnv = [
    stubEnv('GOOGLE_PROJECT_ID', 'test-project'),
    stubEnv('GOOGLE_SPEECH_REGION', undefined),
    stubEnv('GOOGLE_SPEECH_GCS_BUCKET', undefined),
  ];
  for (const method of CONSOLE_METHODS) {
    console[method] = (...args: unknown[]) => {
      logged.push(args.map((arg) => (typeof arg === 'string' ? arg : Bun.inspect(arg))).join(' '));
    };
  }
});

afterEach(async () => {
  await drainPendingDeductions(2000);
  for (const method of CONSOLE_METHODS) console[method] = originalConsole[method];
  globalThis.fetch = originalFetch;
  for (const restore of restoreEnv) restore();
});

function chirpRequest(): Request {
  // 64 KB of WAV is ~2 s: the inline sync `recognize` path.
  const audio = new Uint8Array(64 * 1024);
  const account = 'chirp-route-test';
  return new Request(`http://localhost/transcribe?license_key=${account}`, {
    method: 'POST',
    headers: {
      'Content-Type': 'audio/wav',
      'Content-Length': String(audio.byteLength),
      'X-STT-Provider': 'google-chirp',
    },
    body: audio,
  });
}

describe('/transcribe answers a malformed Chirp 200 with the self-only 502 (#2176)', () => {
  test.each([
    ['an empty body', '', 'application/json'],
    ['a non-JSON body', `<html>${MARKER}</html>`, 'text/html'],
    ['a truncated JSON body', `{"results":[{"alternatives":[{"transcript":"${MARKER}"}]}],"me`, 'application/json'],
    ['JSON null', 'null', 'application/json'],
    ['a JSON array', `[{"transcript":"${MARKER}"}]`, 'application/json'],
  ])('%s', async (_label, body, contentType) => {
    speechAnswers200(body, contentType);

    const app = new Hono();
    app.post('/transcribe', transcribeRoute);
    const res = await app.fetch(chirpRequest());
    const text = await res.text();

    expect(speechCalls).toBe(1);
    expect(res.status).toBe(502);
    expect(text).toContain('is currently unavailable');
    expect(text).not.toContain(MARKER);
    const logText = logged.join('\n');
    expect(logText).toContain('self_only_chain_failed');
    expect(logText).toContain('Google Chirp sync_recognize unavailable: malformed 200 response body');
    expect(logText).not.toContain(MARKER);
  }, 30_000);
});
