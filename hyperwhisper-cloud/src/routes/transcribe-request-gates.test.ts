// THE /transcribe REQUEST GATES
//
// Everything `transcribeRoute` rejects BEFORE it calls a provider: the IP
// block, the streaming header checks, the auth check, and the guard that
// compares the arrived body against the declared Content-Length.
//
// These gates are the cheap half of the paid moat. The credit reservation in
// `estimateCreditsForProviderFallbacks` trusts the declared Content-Length, so
// a client that under-declares would reserve for a short clip and then stream a
// long one — we would pay the provider for audio the caller never reserved
// (ray-amjad/hyperwhisper#263). The mismatch guard is what closes that, and
// until now nothing exercised it.
//
// Every test here asserts that no provider fetch happened. A gate that returns
// the right status but still reaches an upstream is not a gate.
//
// The `../lib/redis` mock lists all four value exports on purpose: bun's
// `mock.module` is process-wide, so a factory that omits one deletes it for
// every LATER file in a `bun test src` run.

import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { Hono } from 'hono';
import { MAX_AUDIO_SIZE_BYTES } from '../lib/constants';

let ipBlocked = false;

mock.module('../lib/redis', () => ({
  redis: {},
  isIPBlocked: async () => ipBlocked,
  getCachedLicense: async () => ({ isValid: true, credits: 1000, cachedAt: 'cached' }),
  cacheLicense: async () => {},
}));

const { transcribeRoute } = await import('./transcribe');

const originalFetch = globalThis.fetch;

/** A licensed caller, so a request that clears the gates reaches a provider. */
const LICENSED_QUERY = '?account_key=test-account-key&language=en-US';

interface GateRequestOptions {
  query?: string;
  headers?: Record<string, string>;
  bodyBytes?: number;
  declaredLength?: string | null;
}

function gateRequest(options: GateRequestOptions = {}): Request {
  const bodyBytes = options.bodyBytes ?? 2048;
  const body = new Uint8Array(bodyBytes);

  const headers: Record<string, string> = {
    'Content-Type': 'audio/wav',
    'X-STT-Provider': 'deepgram',
    ...options.headers,
  };

  // `declaredLength: null` drops the header entirely; anything else declares
  // that exact string, including a value that contradicts the real body.
  if (options.declaredLength !== null) {
    headers['Content-Length'] = options.declaredLength ?? String(bodyBytes);
  }

  return new Request(`http://localhost/transcribe${options.query ?? LICENSED_QUERY}`, {
    method: 'POST',
    headers,
    body,
  });
}

describe('transcribeRoute request gates', () => {
  let providerCalls: string[];

  beforeEach(() => {
    ipBlocked = false;
    providerCalls = [];
    process.env.DEEPGRAM_API_KEY = 'test-deepgram-key';

    // Any upstream call is a gate failure, so record it and answer with a
    // transcript — a test that leaks through gets a 200 rather than a crash
    // that could be mistaken for the rejection it expected.
    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      providerCalls.push(String(input));
      return Response.json({
        results: {
          channels: [{ alternatives: [{ transcript: 'reached the provider' }], detected_language: 'en' }],
        },
        metadata: { duration: 1, request_id: 'dg-req-gate' },
      });
    }) as unknown as typeof fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    ipBlocked = false;
  });

  async function send(request: Request): Promise<{ status: number; body: Record<string, unknown> }> {
    const app = new Hono();
    app.post('/transcribe', transcribeRoute);
    const response = await app.fetch(request);
    return { status: response.status, body: await response.json() as Record<string, unknown> };
  }

  test('a blocked IP is refused before the provider is selected', async () => {
    ipBlocked = true;

    // Deliberately also unroutable (`X-STT-Provider: not-a-provider`): the IP
    // block must win, proving it runs before provider selection.
    const { status, body } = await send(gateRequest({
      headers: { 'X-STT-Provider': 'not-a-provider' },
    }));

    expect(status).toBe(403);
    expect(body.error).toBe('Access denied');
    expect(providerCalls).toEqual([]);
  });

  test('a non-audio Content-Type is refused and the received type is echoed back', async () => {
    const { status, body } = await send(gateRequest({
      headers: { 'Content-Type': 'application/json' },
    }));

    expect(status).toBe(400);
    expect(body.error).toBe('Invalid Content-Type');
    expect(body.received).toBe('application/json');
    expect(providerCalls).toEqual([]);
  });

  test('a missing Content-Length is refused, because the credit estimate has nothing to size', async () => {
    const { status, body } = await send(gateRequest({ declaredLength: null }));

    expect(status).toBe(400);
    expect(body.error).toBe('Missing Content-Length');
    expect(providerCalls).toEqual([]);
  });

  test('a non-numeric Content-Length is refused rather than parsed to NaN', async () => {
    const { status, body } = await send(gateRequest({ declaredLength: 'not-a-number' }));

    expect(status).toBe(400);
    expect(body.error).toBe('Invalid Content-Length');
    expect(providerCalls).toEqual([]);
  });

  test('a zero Content-Length is refused', async () => {
    const { status, body } = await send(gateRequest({ declaredLength: '0' }));

    expect(status).toBe(400);
    expect(body.error).toBe('Invalid Content-Length');
    expect(providerCalls).toEqual([]);
  });

  test('a declared size over the global cap 413s without buffering the body', async () => {
    // The gate reads the header only, so an oversized declaration costs no
    // memory here — which is the whole point of checking before the buffer.
    const declared = MAX_AUDIO_SIZE_BYTES + 1;
    const { status, body } = await send(gateRequest({
      bodyBytes: 1024,
      declaredLength: String(declared),
    }));

    expect(status).toBe(413);
    expect(body.error).toBe('File too large');
    expect(body.max_size_mb).toBe(Math.round(MAX_AUDIO_SIZE_BYTES / (1024 * 1024)));
    expect(providerCalls).toEqual([]);
  });

  test('a request with no account key is refused with 401 and never reaches a provider', async () => {
    const { status, body } = await send(gateRequest({ query: '?language=en-US' }));

    expect(status).toBe(401);
    expect(body.error).toBe('License required');
    expect(providerCalls).toEqual([]);
  });

  test('the legacy license_key alias still authenticates', async () => {
    const { status } = await send(gateRequest({ query: '?license_key=test-account-key&language=en-US' }));

    expect(status).toBe(200);
    expect(providerCalls.some((url) => url.includes('api.deepgram.com'))).toBe(true);
  });

  test('a body larger than the declared Content-Length is refused, reporting both sizes', async () => {
    // The under-declaring client: reserve credits for 1 KB, then stream 64 KB.
    const { status, body } = await send(gateRequest({
      bodyBytes: 64 * 1024,
      declaredLength: '1024',
    }));

    expect(status).toBe(400);
    expect(body.error).toBe('Content-Length mismatch');
    expect(body.declared_bytes).toBe(1024);
    expect(body.actual_bytes).toBe(64 * 1024);
    expect(providerCalls).toEqual([]);
  });

  test('an honest body of exactly the declared size passes the mismatch guard', async () => {
    // The control for the test above: same route, same size, honest header.
    // Without this, a guard that rejected every request would look correct.
    const { status } = await send(gateRequest({ bodyBytes: 64 * 1024 }));

    expect(status).toBe(200);
    expect(providerCalls.some((url) => url.includes('api.deepgram.com'))).toBe(true);
  });

  test('a body smaller than the declared Content-Length is allowed through', async () => {
    // Only over-sending is an attack — under-sending over-reserves credits
    // against the caller's own balance, and a truncated upload must still be
    // transcribed rather than 400ed.
    const { status } = await send(gateRequest({
      bodyBytes: 2048,
      declaredLength: String(64 * 1024),
    }));

    expect(status).toBe(200);
    expect(providerCalls.some((url) => url.includes('api.deepgram.com'))).toBe(true);
  });
});
