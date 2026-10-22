// /transcribe CONTENT-TYPE CASE (ray-amjad/hyperwhisper-app#2084)
//
// Media type names are case-insensitive (RFC 9110 §8.3.1), so `Audio/WAV` is
// the same request as `audio/wav`. `validateStreamingHeaders` used to test the
// raw header with `startsWith('audio/')` and refuse it with 400.
//
// The route-level half drives `transcribeRoute` end to end and reads the
// Content-Type the provider was actually sent: the gate hands on one lowered
// form, so case-sensitive format matching in the adapters sees `audio/wav`.
//
// The `../lib/redis` mock lists all four value exports on purpose: bun's
// `mock.module` is process-wide, so a factory that omits one deletes it for
// every LATER file in a `bun test src` run.

import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { Hono } from 'hono';

mock.module('../lib/redis', () => ({
  redis: {},
  isIPBlocked: async () => false,
  getCachedLicense: async () => ({ isValid: true, credits: 1000, cachedAt: 'cached' }),
  cacheLicense: async () => {},
}));

const { validateStreamingHeaders } = await import('./transcribe-request');
const { transcribeRoute } = await import('./transcribe');

function ctx(headers: Record<string, string>) {
  const lower = Object.fromEntries(Object.entries(headers).map(([k, v]) => [k.toLowerCase(), v]));
  return { req: { header: (name: string) => lower[name.toLowerCase()] } } as never;
}

describe('validateStreamingHeaders Content-Type case', () => {
  test('Audio/WAV is accepted like audio/wav', () => {
    const lowerResult = validateStreamingHeaders(ctx({ 'Content-Type': 'audio/wav', 'Content-Length': '2048' }), 'deepgram');
    expect(lowerResult.ok).toBe(true);
    const upper = validateStreamingHeaders(ctx({ 'Content-Type': 'Audio/WAV', 'Content-Length': '2048' }), 'deepgram');
    expect(upper.ok).toBe(true);
  });

  test('the accepted result carries the lower-cased type, parameters included', () => {
    const result = validateStreamingHeaders(ctx({ 'Content-Type': 'AUDIO/WebM; Codecs=Opus', 'Content-Length': '2048' }), 'deepgram');
    expect(result).toEqual({ ok: true, contentType: 'audio/webm; codecs=opus', contentLength: 2048 });
  });

  test('a non-audio type in any case is still refused, echoing what the caller sent', async () => {
    const result = validateStreamingHeaders(ctx({ 'Content-Type': 'Application/JSON', 'Content-Length': '2048' }), 'deepgram');
    expect(result.ok).toBe(false);
    if (result.ok) return;
    expect(result.response.status).toBe(400);
    const body = await result.response.json() as Record<string, unknown>;
    expect(body.error).toBe('Invalid Content-Type');
    expect(body.received).toBe('Application/JSON');
  });
});

describe('transcribeRoute with an uppercase Content-Type', () => {
  const originalFetch = globalThis.fetch;
  let upstreamContentTypes: (string | null)[];

  beforeEach(() => {
    upstreamContentTypes = [];
    process.env.DEEPGRAM_API_KEY = 'test-deepgram-key';
    // Only the Deepgram call counts; the route's other calls (credit
    // deduction to the licence API) send JSON and are not the audio upload.
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input).includes('deepgram.com')) {
        upstreamContentTypes.push(new Headers(init?.headers).get('Content-Type'));
      }
      return Response.json({
        results: {
          channels: [{ alternatives: [{ transcript: 'reached the provider' }], detected_language: 'en' }],
        },
        metadata: { duration: 1, request_id: 'dg-req-case' },
      });
    }) as unknown as typeof fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  test('Audio/WAV transcribes, and the provider is sent audio/wav', async () => {
    const app = new Hono();
    app.post('/transcribe', transcribeRoute);
    const response = await app.fetch(new Request('http://localhost/transcribe?account_key=test-account-key&language=en-US', {
      method: 'POST',
      headers: { 'Content-Type': 'Audio/WAV', 'Content-Length': '2048', 'X-STT-Provider': 'deepgram' },
      body: new Uint8Array(2048),
    }));

    expect(response.status).toBe(200);
    expect(upstreamContentTypes).toEqual(['audio/wav']);
  });
});
