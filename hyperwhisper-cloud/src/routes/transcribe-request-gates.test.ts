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

import { afterEach, beforeEach, describe, expect, mock, spyOn, test } from 'bun:test';
import { Hono } from 'hono';
import { MAX_AUDIO_SIZE_BYTES } from '../lib/constants';

let ipBlocked = false;
const cacheReads: string[] = [];

mock.module('../lib/redis', () => ({
  redis: {},
  isIPBlocked: async () => ipBlocked,
  getCachedLicense: async (licenseKey: string) => {
    cacheReads.push(licenseKey);
    return { isValid: true, credits: 1000, cachedAt: 'cached' };
  },
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
    cacheReads.length = 0;
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

  test('a Chirp upload over the 9.5 MB inline cap 413s naming 9.5 MB (#2607)', async () => {
    const savedBucket = process.env.GOOGLE_SPEECH_GCS_BUCKET;
    delete process.env.GOOGLE_SPEECH_GCS_BUCKET;
    try {
      const { status, body } = await send(gateRequest({
        headers: { 'X-STT-Provider': 'google-chirp' },
        bodyBytes: 1024,
        declaredLength: '9600000',
      }));

      expect(status).toBe(413);
      expect(body.error).toBe('File too large');
      expect(body.message).toBe('Audio file must be 9.5 MB or smaller. Your file is 9.60 MB.');
      expect(body.max_size_mb).toBe(9);
      expect(providerCalls).toEqual([]);
    } finally {
      if (savedBucket === undefined) delete process.env.GOOGLE_SPEECH_GCS_BUCKET;
      else process.env.GOOGLE_SPEECH_GCS_BUCKET = savedBucket;
    }
  });

  test('a Soniox upload over its 14.4 MB cap 413s before buffering, with the adapter\'s numbers', async () => {
    // Soniox used to be missing from the pre-buffer table, so this was buffered
    // up to the 128 MB global cap before the adapter's own 413. Same status and
    // the same `max_size_mb` / `actual_size_mb` the adapter's 413 carries.
    const savedKey = process.env.SONIOX_API_KEY;
    process.env.SONIOX_API_KEY = 'test-soniox-key';
    try {
      const { status, body } = await send(gateRequest({
        headers: { 'X-STT-Provider': 'soniox' },
        bodyBytes: 1024,
        declaredLength: '15000000',
      }));

      expect(status).toBe(413);
      expect(body.error).toBe('File too large');
      expect(body.message).toBe('Audio file must be 14.4 MB or smaller. Your file is 15.00 MB.');
      expect(body.max_size_mb).toBe(13);
      expect(body.actual_size_mb).toBe(14.31);
      expect(providerCalls).toEqual([]);
    } finally {
      if (savedKey === undefined) delete process.env.SONIOX_API_KEY;
      else process.env.SONIOX_API_KEY = savedKey;
    }
  });

  test('a provider-side 413 names its cap in the same unit as the file (#2607)', async () => {
    // Every Soniox payload over its cap is now refused by the pre-buffer gate
    // above (and a body cannot exceed its Content-Length), so the adapter's
    // 413 is reached through Chirp instead: a 2 MB WAV fits the 9.5 MB inline
    // byte cap but not the ~55 s duration budget, and with no GCS bucket the
    // adapter refuses it with its inline cap.
    const savedBucket = process.env.GOOGLE_SPEECH_GCS_BUCKET;
    const savedProject = process.env.GOOGLE_PROJECT_ID;
    delete process.env.GOOGLE_SPEECH_GCS_BUCKET;
    process.env.GOOGLE_PROJECT_ID = 'test-project';
    try {
      const { status, body } = await send(gateRequest({
        headers: { 'X-STT-Provider': 'google-chirp' },
        bodyBytes: 2_000_000,
      }));

      expect(status).toBe(413);
      expect(body.error).toBe('Audio too large for provider');
      expect(body.message).toBe('google-chirp accepts at most 9.5 MB inline. Your audio is 2.00 MB.');
      // Rounded down so the client never reads back more than the cap (#2704).
      expect(body.max_size_mb).toBe(9);
      expect(body.actual_size_mb).toBe(1.91);
      expect(providerCalls).toEqual([]);
    } finally {
      if (savedBucket === undefined) delete process.env.GOOGLE_SPEECH_GCS_BUCKET;
      else process.env.GOOGLE_SPEECH_GCS_BUCKET = savedBucket;
      if (savedProject === undefined) delete process.env.GOOGLE_PROJECT_ID;
      else process.env.GOOGLE_PROJECT_ID = savedProject;
    }
  });

  test('a request with no account key is refused with 401 and never reaches a provider', async () => {
    const { status, body } = await send(gateRequest({ query: '?language=en-US' }));

    expect(status).toBe(401);
    expect(body.error).toBe('License required');
    expect(providerCalls).toEqual([]);
  });

  // CTF #1a: `mode` and `language` ride along to the charge, and a long one
  // made the charge too big to land. Refused before auth, hold or vendor.
  test('an oversized mode is refused with 400 before auth or any provider call', async () => {
    const { status, body } = await send(gateRequest({
      query: `${LICENSED_QUERY}&mode=${'m'.repeat(17_000)}`,
    }));

    expect(status).toBe(400);
    expect(body.error).toBe('Invalid mode');
    expect(providerCalls).toEqual([]);
    expect(cacheReads).toEqual([]);
  });

  test('an oversized language is refused with 400 before auth or any provider call', async () => {
    const { status, body } = await send(gateRequest({
      query: `?account_key=test-account-key&language=${'e'.repeat(17_000)}`,
    }));

    expect(status).toBe(400);
    expect(body.error).toBe('Invalid language');
    expect(providerCalls).toEqual([]);
    expect(cacheReads).toEqual([]);
  });

  test('a multi-byte language is refused with 400 before any provider call', async () => {
    const { status, body } = await send(gateRequest({
      query: `?account_key=test-account-key&language=${encodeURIComponent('語'.repeat(30))}`,
    }));

    expect(status).toBe(400);
    expect(body.error).toBe('Invalid language');
    expect(providerCalls).toEqual([]);
  });

  test('a mode that is not a mode id is refused with 400', async () => {
    const { status, body } = await send(gateRequest({ query: `${LICENSED_QUERY}&mode=Email` }));

    expect(status).toBe(400);
    expect(body.error).toBe('Invalid mode');
    expect(providerCalls).toEqual([]);
  });

  test.each([
    ['a macOS mode id and a region tag', '?account_key=test-account-key&language=en-US&mode=00000000-0000-0000-0000-000000000001'],
    ['a lower-case mode id and a bare language', '?account_key=test-account-key&language=de&mode=c2f1a0b4-5d6e-4f70-8a9b-0c1d2e3f4a5b'],
    ['auto and no mode', '?account_key=test-account-key&language=auto'],
    ['an empty language and an empty mode', '?account_key=test-account-key&language=&mode='],
  ])('%s still reach the provider', async (_label, query) => {
    const { status } = await send(gateRequest({ query }));

    expect(status).toBe(200);
    expect(providerCalls.some((url) => url.includes('api.deepgram.com'))).toBe(true);
  });

  test('the legacy license_key alias still authenticates', async () => {
    const { status } = await send(gateRequest({ query: '?license_key=test-account-key&language=en-US' }));

    expect(status).toBe(200);
    expect(providerCalls.some((url) => url.includes('api.deepgram.com'))).toBe(true);
  });

  test('account_key wins over the legacy license_key when both are sent', async () => {
    const { status } = await send(gateRequest({
      query: '?account_key=canonical-credential&license_key=legacy-credential&language=en-US',
    }));

    expect(status).toBe(200);
    expect(cacheReads).toEqual(['canonical-credential']);
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

  // #1095: Dictation with the language on Auto 400s. Support must be able to
  // see it happened, so it writes the same rejection event as every other gate.
  test('a Dictation request with no explicit language logs one invalid_dictation_options rejection', async () => {
    const logSpy = spyOn(console, 'log').mockImplementation(() => {});
    try {
      const { status, body } = await send(gateRequest({
        query: '?account_key=test-account-key&language=auto',
        headers: { 'X-STT-Provider': 'assemblyai', 'X-STT-Model': 'dictation' },
      }));

      expect(status).toBe(400);
      expect(body.error).toBe('Invalid Dictation options');
      // #1290: the same message the route built from the vendor error before
      // the check moved behind providers/request-options.ts.
      expect(body.message).toBe('AssemblyAI Dictation rejected input (400): Select an explicit supported language instead of Auto for Dictation.');
      expect(providerCalls).toEqual([]);

      const rejections = logSpy.mock.calls
        .map((call) => String(call[0]))
        .filter((raw) => raw.startsWith('{') && raw.includes('"event":"transcribe.request_rejected"'))
        .map((raw) => JSON.parse(raw) as Record<string, unknown>);
      expect(rejections).toHaveLength(1);
      expect(rejections[0]).toMatchObject({
        reason: 'invalid_dictation_options',
        language: 'auto',
        hasDomain: false,
      });
      expect(JSON.stringify(rejections[0])).not.toContain('test-account-key');
    } finally {
      logSpy.mockRestore();
    }
  });

  // #1290: the option rule moved behind providers/request-options.ts. A
  // Dictation request with no language at all still gets today's 400, title
  // and message unchanged, before any provider is called.
  test('a Dictation request with no language param gets the same 400 title and message', async () => {
    const logSpy = spyOn(console, 'log').mockImplementation(() => {});
    try {
      const { status, body } = await send(gateRequest({
        query: '?account_key=test-account-key',
        headers: { 'X-STT-Provider': 'assemblyai', 'X-STT-Model': 'dictation' },
      }));

      expect(status).toBe(400);
      expect(body).toMatchObject({
        error: 'Invalid Dictation options',
        message: 'AssemblyAI Dictation rejected input (400): Select an explicit supported language instead of Auto for Dictation.',
      });
      expect(providerCalls).toEqual([]);
    } finally {
      logSpy.mockRestore();
    }
  });
});
