import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { Hono } from 'hono';

// Well-funded licensed user so auth + credit checks pass in-memory.
mock.module('../lib/redis', () => ({
  redis: {}, // satisfies static `import { redis }` in google-auth (via google-chirp)
  isIPBlocked: async () => false,
  getCachedLicense: async () => ({ isValid: true, credits: 1000, cachedAt: 'cached' }),
  cacheLicense: async () => {},
}));

const { transcribeRoute, estimateCreditsForProviderFallbacks } = await import('./transcribe');

const originalFetch = globalThis.fetch;

function buildApp(): Hono {
  const app = new Hono();
  app.post('/transcribe', transcribeRoute);
  return app;
}

function request(headers: Record<string, string>, query = ''): Request {
  const audio = new Uint8Array(2048);
  return new Request(`http://localhost/transcribe?license_key=test-license${query}`, {
    method: 'POST',
    headers: {
      'Content-Type': 'audio/wav',
      'Content-Length': String(audio.byteLength),
      ...headers,
    },
    body: audio,
  });
}

describe('fail-closed provider/model validation', () => {
  afterEach(() => { globalThis.fetch = originalFetch; });

  test('rejects an explicitly-supplied unknown provider with 400 (no silent default)', async () => {
    let upstreamCalled = false;
    globalThis.fetch = mock(async () => { upstreamCalled = true; return Response.json({}); }) as unknown as typeof fetch;

    const response = await buildApp().fetch(request({ 'X-STT-Provider': 'definitely-not-a-provider' }));
    const body = await response.json() as { error: string };

    expect(response.status).toBe(400);
    expect(body.error).toBe('Invalid STT provider');
    expect(upstreamCalled).toBe(false);
  });

  test('rejects a model that does not belong to the provider with 400', async () => {
    let upstreamCalled = false;
    globalThis.fetch = mock(async () => { upstreamCalled = true; return Response.json({}); }) as unknown as typeof fetch;

    const response = await buildApp().fetch(request({ 'X-STT-Provider': 'openai', 'X-STT-Model': 'nova-3-medical' }));
    const body = await response.json() as { error: string; valid_models: string[] };

    expect(response.status).toBe(400);
    expect(body.error).toBe('Invalid STT model');
    expect(body.valid_models).toContain('gpt-4o-transcribe');
    expect(upstreamCalled).toBe(false);
  });

  test('no provider header falls back to the deepgram default (back-compat)', async () => {
    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      if (String(input).includes('api.deepgram.com')) {
        return Response.json({
          results: { channels: [{ alternatives: [{ transcript: 'default ok' }], detected_language: 'en' }] },
          metadata: { duration: 1, request_id: 'dg' },
        });
      }
      throw new Error(`Unexpected fetch: ${input}`);
    }) as unknown as typeof fetch;

    process.env.DEEPGRAM_API_KEY = 'test';
    const response = await buildApp().fetch(request({}));
    const body = await response.json() as { text: string; metadata: { stt_provider: string } };
    expect(response.status).toBe(200);
    expect(body.text).toBe('default ok');
    expect(body.metadata.stt_provider).toContain('deepgram');
  });
});

describe('Mistral context_bias wire format (repeated multipart fields)', () => {
  beforeEach(() => { process.env.MISTRAL_API_KEY = 'test-mistral-key'; });
  afterEach(() => { globalThis.fetch = originalFetch; });

  test('emits one repeated context_bias field per term (not a comma-joined value)', async () => {
    let contextBiasValues: string[] = [];
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('api.mistral.ai')) {
        const form = init?.body as FormData;
        contextBiasValues = form.getAll('context_bias').map(String);
        return Response.json({ text: 'hello voxtral', language: 'en', usage: { prompt_audio_seconds: 5 } });
      }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(
      request({ 'X-STT-Provider': 'mistral' }, '&initial_prompt=Voxtral,HyperWhisper,SwiftUI'),
    );

    expect(response.status).toBe(200);
    // context_bias is an array (List[str]) — over multipart it must be one repeated
    // field per term so the server collects them into a list. A single comma-joined
    // value would be parsed as one literal bias phrase and boost nothing.
    expect(contextBiasValues).toEqual(['Voxtral', 'HyperWhisper', 'SwiftUI']);
  });
});

describe('AssemblyAI bills the model that actually ran (speech_models fallback)', () => {
  beforeEach(() => { process.env.ASSEMBLYAI_API_KEY = 'test-asm-key'; });
  afterEach(() => { globalThis.fetch = originalFetch; });

  // universal-3-5-pro requested + keyterms, but the completed job reports it fell
  // back to universal-2 → bill the universal-2 base rate with NO keyterms add-on.
  test('a universal-2 fallback is billed at the universal-2 rate, not universal-3-5-pro + keyterms', async () => {
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = (init?.method || 'GET').toUpperCase();
      if (url.includes('api.assemblyai.com')) {
        if (url.endsWith('/v2/upload')) return Response.json({ upload_url: 'https://cdn.assemblyai.com/u/x' });
        if (url.endsWith('/v2/transcript') && method === 'POST') return Response.json({ id: 'tid-1' });
        if (url.endsWith('/v2/transcript/tid-1') && method === 'GET') {
          return Response.json({
            status: 'completed', text: 'hola mundo', language_code: 'es',
            audio_duration: 60, speech_model_used: 'universal-2',
          });
        }
        if (method === 'DELETE') return Response.json({}); // best-effort cleanup
      }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${method} ${url}`);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(
      request({ 'X-STT-Provider': 'assemblyai' }, '&language=es&initial_prompt=Foo,Bar'),
    );
    const body = await response.json() as { cost: { usd: number } };

    expect(response.status).toBe(200);
    // universal-2 base for 60s = $0.0025; keyterms are free on universal-2.
    expect(body.cost.usd).toBeCloseTo(0.15 / 60, 6);
    // Must be strictly cheaper than universal-3-5-pro base + keyterms add-on,
    // which is what billing the REQUESTED model would have charged.
    expect(body.cost.usd).toBeLessThan((0.21 / 60) + (0.05 / 60));
    // The transcript ran on universal-2, so X-STT-Model must report that — not
    // the requested universal-3-5-pro — so the label matches what was billed.
    expect(response.headers.get('X-STT-Model')).toBe('universal-2');
  }, 10_000);

  test('normalizes a hyphenated BCP-47 locale to AssemblyAI\'s bare language_code', async () => {
    let sentLanguageCode: unknown;
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = (init?.method || 'GET').toUpperCase();
      if (url.includes('api.assemblyai.com')) {
        if (url.endsWith('/v2/upload')) return Response.json({ upload_url: 'https://cdn.assemblyai.com/u/x' });
        if (url.endsWith('/v2/transcript') && method === 'POST') {
          sentLanguageCode = (JSON.parse(String(init?.body)) as { language_code?: string }).language_code;
          return Response.json({ id: 'tid-2' });
        }
        if (url.endsWith('/v2/transcript/tid-2') && method === 'GET') {
          return Response.json({
            status: 'completed', text: 'hello', language_code: 'en',
            audio_duration: 30, speech_model_used: 'universal-3-5-pro',
          });
        }
        if (method === 'DELETE') return Response.json({});
      }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${method} ${url}`);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(
      request({ 'X-STT-Provider': 'assemblyai' }, '&language=en-US'),
    );

    expect(response.status).toBe(200);
    // "en-US" → "en" (NOT "en-us", which AssemblyAI rejects at job creation).
    expect(sentLanguageCode).toBe('en');
  }, 10_000);
});

describe('AssemblyAI sync fast path (clips under the byte-size duration estimate)', () => {
  beforeEach(() => { process.env.ASSEMBLYAI_API_KEY = 'test-asm-key'; });
  afterEach(() => { globalThis.fetch = originalFetch; });

  function requestWithAudio(byteLength: number, headers: Record<string, string>, query = ''): Request {
    const audio = new Uint8Array(byteLength);
    return new Request(`http://localhost/transcribe?license_key=test-license${query}`, {
      method: 'POST',
      headers: {
        'Content-Type': 'audio/wav',
        'Content-Length': String(audio.byteLength),
        ...headers,
      },
      body: audio,
    });
  }

  const syncOk = (text = 'hello sync world') => Response.json({
    text, confidence: 0.98, audio_duration_ms: 1500, session_id: 'sess-1',
  });

  test('a short clip hits sync.assemblyai.com and never touches the async v2 endpoints', async () => {
    let sawAsyncCall = false;
    let sawSyncModelHeader: unknown;
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === 'https://sync.assemblyai.com/v1/transcribe') {
        sawSyncModelHeader = (init?.headers as Record<string, string>)['X-AAI-Model'];
        return syncOk();
      }
      if (url.includes('api.assemblyai.com')) {
        sawAsyncCall = true;
        throw new Error('async v2 endpoint should not be called for a short clip');
      }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    // 2048 bytes ≈ 0.26s estimated — well under the sync cap. Explicit
    // language required for sync eligibility (see the auto-language test below).
    const response = await buildApp().fetch(
      requestWithAudio(2048, { 'X-STT-Provider': 'assemblyai' }, '&language=en'),
    );
    const body = await response.json() as { text: string; cost: { usd: number } };

    expect(response.status).toBe(200);
    expect(body.text).toBe('hello sync world');
    expect(sawAsyncCall).toBe(false);
    expect(sawSyncModelHeader).toBe('universal-3-5-pro');
    expect(response.headers.get('X-STT-Model')).toBe('universal-3-5-pro');
    // 1500ms @ $0.45/hr.
    expect(body.cost.usd).toBeCloseTo((1.5 / 60) * (0.45 / 60), 6);
  });

  test('a clip estimated over the sync threshold skips straight to the async flow', async () => {
    let sawSyncCall = false;
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = (init?.method || 'GET').toUpperCase();
      if (url === 'https://sync.assemblyai.com/v1/transcribe') {
        sawSyncCall = true;
        throw new Error('sync endpoint should not be called for a long clip');
      }
      if (url.includes('api.assemblyai.com')) {
        if (url.endsWith('/v2/upload')) return Response.json({ upload_url: 'https://cdn.assemblyai.com/u/y' });
        if (url.endsWith('/v2/transcript') && method === 'POST') return Response.json({ id: 'tid-long' });
        if (url.endsWith('/v2/transcript/tid-long') && method === 'GET') {
          return Response.json({ status: 'completed', text: 'async fallback text', audio_duration: 110 });
        }
        if (method === 'DELETE') return Response.json({});
      }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${method} ${url}`);
    }) as unknown as typeof fetch;

    // 900,000 bytes ≈ 112.5s estimated — over the 100s sync-eligibility cap.
    const response = await buildApp().fetch(requestWithAudio(900_000, { 'X-STT-Provider': 'assemblyai' }));
    const body = await response.json() as { text: string };

    expect(response.status).toBe(200);
    expect(body.text).toBe('async fallback text');
    expect(sawSyncCall).toBe(false);
  }, 10_000);

  test('a sync HTTP error falls back to the async flow instead of failing the request', async () => {
    let syncAttempted = false;
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = (init?.method || 'GET').toUpperCase();
      if (url === 'https://sync.assemblyai.com/v1/transcribe') {
        syncAttempted = true;
        return new Response('{"status":503,"title":"Capacity Exceeded"}', { status: 503 });
      }
      if (url.includes('api.assemblyai.com')) {
        if (url.endsWith('/v2/upload')) return Response.json({ upload_url: 'https://cdn.assemblyai.com/u/z' });
        if (url.endsWith('/v2/transcript') && method === 'POST') return Response.json({ id: 'tid-fallback' });
        if (url.endsWith('/v2/transcript/tid-fallback') && method === 'GET') {
          return Response.json({ status: 'completed', text: 'recovered via async', audio_duration: 3 });
        }
        if (method === 'DELETE') return Response.json({});
      }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${method} ${url}`);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(
      requestWithAudio(2048, { 'X-STT-Provider': 'assemblyai' }, '&language=en'),
    );
    const body = await response.json() as { text: string };

    expect(response.status).toBe(200);
    expect(syncAttempted).toBe(true);
    expect(body.text).toBe('recovered via async');
  }, 10_000);

  test('an absent language skips sync entirely (an omitted language_codes defaults to English server-side, not auto-detect)', async () => {
    let sawSyncCall = false;
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = (init?.method || 'GET').toUpperCase();
      if (url === 'https://sync.assemblyai.com/v1/transcribe') {
        sawSyncCall = true;
        throw new Error('sync endpoint should not be called without an explicit language');
      }
      if (url.includes('api.assemblyai.com')) {
        if (url.endsWith('/v2/upload')) return Response.json({ upload_url: 'https://cdn.assemblyai.com/u/auto' });
        if (url.endsWith('/v2/transcript') && method === 'POST') return Response.json({ id: 'tid-auto' });
        if (url.endsWith('/v2/transcript/tid-auto') && method === 'GET') {
          return Response.json({ status: 'completed', text: 'auto-detected async text', audio_duration: 2 });
        }
        if (method === 'DELETE') return Response.json({});
      }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${method} ${url}`);
    }) as unknown as typeof fetch;

    // No &language= query param at all — same as an explicit "auto".
    const response = await buildApp().fetch(requestWithAudio(2048, { 'X-STT-Provider': 'assemblyai' }));
    const body = await response.json() as { text: string };

    expect(response.status).toBe(200);
    expect(body.text).toBe('auto-detected async text');
    expect(sawSyncCall).toBe(false);
  }, 10_000);

  test('an explicit "auto" language skips sync entirely, same as an absent language', async () => {
    let sawSyncCall = false;
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = (init?.method || 'GET').toUpperCase();
      if (url === 'https://sync.assemblyai.com/v1/transcribe') {
        sawSyncCall = true;
        throw new Error('sync endpoint should not be called for language=auto');
      }
      if (url.includes('api.assemblyai.com')) {
        if (url.endsWith('/v2/upload')) return Response.json({ upload_url: 'https://cdn.assemblyai.com/u/auto2' });
        if (url.endsWith('/v2/transcript') && method === 'POST') return Response.json({ id: 'tid-auto2' });
        if (url.endsWith('/v2/transcript/tid-auto2') && method === 'GET') {
          return Response.json({ status: 'completed', text: 'auto async text', audio_duration: 2 });
        }
        if (method === 'DELETE') return Response.json({});
      }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${method} ${url}`);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(
      requestWithAudio(2048, { 'X-STT-Provider': 'assemblyai' }, '&language=auto'),
    );
    const body = await response.json() as { text: string };

    expect(response.status).toBe(200);
    expect(body.text).toBe('auto async text');
    expect(sawSyncCall).toBe(false);
  }, 10_000);

  test('medical domain skips sync entirely even for a short clip', async () => {
    let sawSyncCall = false;
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = (init?.method || 'GET').toUpperCase();
      if (url === 'https://sync.assemblyai.com/v1/transcribe') {
        sawSyncCall = true;
        throw new Error('sync endpoint should not be called for a medical-domain request');
      }
      if (url.includes('api.assemblyai.com')) {
        if (url.endsWith('/v2/upload')) return Response.json({ upload_url: 'https://cdn.assemblyai.com/u/m' });
        if (url.endsWith('/v2/transcript') && method === 'POST') return Response.json({ id: 'tid-med' });
        if (url.endsWith('/v2/transcript/tid-med') && method === 'GET') {
          return Response.json({ status: 'completed', text: 'medical async text', audio_duration: 2 });
        }
        if (method === 'DELETE') return Response.json({});
      }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${method} ${url}`);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(
      requestWithAudio(2048, { 'X-STT-Provider': 'assemblyai', 'X-STT-Domain': 'medical' }),
    );
    const body = await response.json() as { text: string };

    expect(response.status).toBe(200);
    expect(body.text).toBe('medical async text');
    expect(sawSyncCall).toBe(false);
  }, 10_000);
});

describe('ElevenLabs keyterms (scribe_v2 only)', () => {
  beforeEach(() => { process.env.ELEVENLABS_API_KEY = 'test-11l-key'; });
  afterEach(() => { globalThis.fetch = originalFetch; });

  const ok = () => new Response(
    JSON.stringify({ text: 'hello world', language_code: 'en', words: [{ start: 0, end: 1, text: 'hello' }] }),
    { headers: { 'content-type': 'application/json' } },
  );

  test('scribe_v2 sends each keyterm as its own repeated form field (not a JSON-array string)', async () => {
    let keytermValues: string[] = [];
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('api.elevenlabs.io')) {
        keytermValues = (init?.body as FormData).getAll('keyterms').map(String);
        return ok();
      }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(
      request({ 'X-STT-Provider': 'elevenlabs', 'X-STT-Model': 'scribe_v2' }, '&initial_prompt=HyperWhisper,SwiftUI'),
    );

    expect(response.status).toBe(200);
    // Repeated fields, one per term — NOT a single `["HyperWhisper","SwiftUI"]`
    // value (the API forbids literal [ ] and would treat it as one bad term).
    expect(keytermValues).toEqual(['HyperWhisper', 'SwiftUI']);
  });

  test('scribe_v1 is retired — rejected with 400 before any upstream call (fail-closed)', async () => {
    let upstreamCalled = false;
    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('api.elevenlabs.io')) {
        upstreamCalled = true;
        return ok();
      }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(
      request({ 'X-STT-Provider': 'elevenlabs', 'X-STT-Model': 'scribe_v1' }, '&initial_prompt=HyperWhisper,SwiftUI'),
    );
    const body = await response.json() as { error: string; valid_models: string[] };

    expect(response.status).toBe(400);
    expect(body.error).toBe('Invalid STT model');
    expect(body.valid_models).not.toContain('scribe_v1');
    expect(upstreamCalled).toBe(false);
  });
});

describe('Soniox language_hints normalization (BCP-47 → ISO)', () => {
  beforeEach(() => { process.env.SONIOX_API_KEY = 'test-soniox-key'; });
  afterEach(() => { globalThis.fetch = originalFetch; });

  test('a region-qualified tag (en-US) is sent to Soniox as the ISO code (en)', async () => {
    let languageHints: unknown = null;
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = (init?.method || 'GET').toUpperCase();
      if (url.includes('api.soniox.com')) {
        if (url.endsWith('/v1/files') && method === 'POST') return Response.json({ id: 'file-1' });
        if (url.endsWith('/v1/transcriptions') && method === 'POST') {
          languageHints = JSON.parse(String(init?.body)).language_hints;
          return Response.json({ id: 'tx-1' });
        }
        if (url.endsWith('/v1/transcriptions/tx-1') && method === 'GET') {
          return Response.json({ status: 'completed', audio_duration_ms: 1000 });
        }
        if (url.endsWith('/v1/transcriptions/tx-1/transcript') && method === 'GET') {
          return Response.json({ text: 'hola', tokens: [{ language: 'es' }] });
        }
        if (method === 'DELETE') return Response.json({}); // best-effort cleanup
      }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${method} ${url}`);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(
      request({ 'X-STT-Provider': 'soniox' }, '&language=en-US'),
    );

    expect(response.status).toBe(200);
    expect(languageHints).toEqual(['en']);
  }, 15_000);

  test('a balance-exhausted async failure surfaces as 502 (not a 400 input rejection)', async () => {
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = (init?.method || 'GET').toUpperCase();
      if (url.includes('api.soniox.com')) {
        if (url.endsWith('/v1/files') && method === 'POST') return Response.json({ id: 'file-1' });
        if (url.endsWith('/v1/transcriptions') && method === 'POST') return Response.json({ id: 'tx-1' });
        if (url.endsWith('/v1/transcriptions/tx-1') && method === 'GET') {
          // Soniox reports billing exhaustion as a failed async job, not an HTTP error.
          return Response.json({
            status: 'failed',
            error_type: 'organization_balance_exhausted',
            error_message: 'The available balance has dropped to zero.',
          });
        }
        if (method === 'DELETE') return Response.json({});
      }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${method} ${url}`);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(request({ 'X-STT-Provider': 'soniox' }));
    // Self-only provider + upstream billing failure → 502, so the client doesn't
    // misdiagnose its own audio/params as the problem.
    expect(response.status).toBe(502);
  }, 15_000);

  test('an invalid-audio async failure stays a client error (not 502)', async () => {
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = (init?.method || 'GET').toUpperCase();
      if (url.includes('api.soniox.com')) {
        if (url.endsWith('/v1/files') && method === 'POST') return Response.json({ id: 'file-1' });
        if (url.endsWith('/v1/transcriptions') && method === 'POST') return Response.json({ id: 'tx-1' });
        if (url.endsWith('/v1/transcriptions/tx-1') && method === 'GET') {
          return Response.json({
            status: 'failed',
            error_type: 'invalid_audio_file',
            error_message: 'The audio could not be decoded.',
          });
        }
        if (method === 'DELETE') return Response.json({});
      }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${method} ${url}`);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(request({ 'X-STT-Provider': 'soniox' }));
    // Genuine bad input → NOT a 502 (self-only provider returns a 4xx-class error).
    expect(response.status).not.toBe(502);
    expect(response.status).toBeGreaterThanOrEqual(400);
  }, 15_000);

  test('audio estimated longer than the 30-min poll-safe limit is rejected (413) before any upstream job', async () => {
    let sonioxCalled = false;
    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('api.soniox.com')) { sonioxCalled = true; return Response.json({ id: 'x' }); }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    // > 30 min at the 64 kbps estimate (480_000 bytes/min): 31 min ≈ 14.88 MB.
    const body = new Uint8Array(31 * 480_000);
    const req = new Request('http://localhost/transcribe?license_key=test-license', {
      method: 'POST',
      headers: {
        'Content-Type': 'audio/wav',
        'Content-Length': String(body.byteLength),
        'X-STT-Provider': 'soniox',
      },
      body,
    });

    const response = await buildApp().fetch(req);
    expect(response.status).toBe(413);
    // Must reject before creating any Soniox file/transcription (no orphan).
    expect(sonioxCalled).toBe(false);
  }, 15_000);
});

describe('OpenAI language_hint normalization (BCP-47 → ISO)', () => {
  beforeEach(() => { process.env.OPENAI_API_KEY = 'test-openai-key'; });
  afterEach(() => { globalThis.fetch = originalFetch; });

  test('a region-qualified tag (en-US) is forwarded to OpenAI as the ISO code (en)', async () => {
    let languageField: unknown = null;
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('api.openai.com')) {
        languageField = (init?.body as FormData).get('language');
        return Response.json({ text: 'hello', language: 'english', duration: 1 });
      }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(
      request({ 'X-STT-Provider': 'openai', 'X-STT-Model': 'whisper-1' }, '&language=en-US'),
    );

    expect(response.status).toBe(200);
    expect(languageField).toBe('en');
  });
});

describe('Whisper-family locale normalization (BCP-47 → bare ISO)', () => {
  afterEach(() => { globalThis.fetch = originalFetch; });

  test('Groq forwards en-US as the bare ISO code (en)', async () => {
    process.env.GROQ_API_KEY = 'test-groq-key';
    let languageField: unknown = null;
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('api.groq.com')) {
        languageField = (init?.body as FormData).get('language');
        return Response.json({ text: 'hello', language: 'en', duration: 1, segments: [] });
      }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(request({ 'X-STT-Provider': 'groq' }, '&language=en-US'));
    expect(response.status).toBe(200);
    expect(languageField).toBe('en');
  });

  test('Mistral forwards pt-BR as the bare ISO code (pt)', async () => {
    process.env.MISTRAL_API_KEY = 'test-mistral-key';
    let languageField: unknown = null;
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('api.mistral.ai')) {
        languageField = (init?.body as FormData).get('language');
        return Response.json({ text: 'olá', language: 'pt', usage: { prompt_audio_seconds: 3 } });
      }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(request({ 'X-STT-Provider': 'mistral' }, '&language=pt-BR'));
    expect(response.status).toBe(200);
    expect(languageField).toBe('pt');
  });

  test('ElevenLabs forwards en-US as the bare ISO language_code (en)', async () => {
    process.env.ELEVENLABS_API_KEY = 'test-11l-key';
    let languageField: unknown = null;
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('api.elevenlabs.io')) {
        languageField = (init?.body as FormData).get('language_code');
        return new Response(
          JSON.stringify({ text: 'hello', language_code: 'en', words: [{ start: 0, end: 1, text: 'hello' }] }),
          { headers: { 'content-type': 'application/json' } },
        );
      }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(
      request({ 'X-STT-Provider': 'elevenlabs', 'X-STT-Model': 'scribe_v2' }, '&language=en-US'),
    );
    expect(response.status).toBe(200);
    expect(languageField).toBe('en');
  });
});

describe('Gemini pre-buffer size gate (413 before any upstream call)', () => {
  afterEach(() => { globalThis.fetch = originalFetch; });

  test('rejects an oversized Content-Length with 413 before buffering or calling fetch', async () => {
    let fetchCalled = false;
    globalThis.fetch = mock(async () => { fetchCalled = true; return Response.json({}); }) as unknown as typeof fetch;

    // Tiny actual body, but a Content-Length declaring >14 MB so the Gemini
    // inline cap (GEMINI_INLINE_MAX_BYTES = 14 MiB) gate fires pre-buffer.
    const body = new Uint8Array(8);
    const oversizedContentLength = 15 * 1024 * 1024;
    const req = new Request('http://localhost/transcribe?license_key=test-license', {
      method: 'POST',
      headers: {
        'Content-Type': 'audio/wav',
        'Content-Length': String(oversizedContentLength),
        'X-STT-Provider': 'gemini',
      },
      body,
    });

    const response = await buildApp().fetch(req);
    expect(response.status).toBe(413);
    // The gate must fire before any buffering / upstream provider call.
    expect(fetchCalled).toBe(false);
  });
});

describe('Gemini 3.5 Transcribe routing (X-STT-Provider: gemini-transcribe)', () => {
  afterEach(() => { globalThis.fetch = originalFetch; });

  test('routes to /v1beta/interactions and never to :generateContent (TRAP 1)', async () => {
    const urls: string[] = [];
    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);
      urls.push(url);
      if (url.includes('/v1beta/interactions')) {
        return Response.json({
          id: 'interaction-1',
          steps: [{ content: [{ text: 'routed ok' }] }],
          usage: {
            input_tokens_by_modality: [{ modality: 'audio', tokens: 236 }, { modality: 'text', tokens: 1 }],
            total_output_tokens: 0,
          },
        });
      }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    process.env.GEMINI_API_KEY = 'test';
    const response = await buildApp().fetch(request({ 'X-STT-Provider': 'gemini-transcribe' }, '&language=en'));
    const body = await response.json() as { text: string; metadata: { stt_provider: string } };

    expect(response.status).toBe(200);
    expect(body.text).toBe('routed ok');
    expect(body.metadata.stt_provider).toContain('gemini-transcribe/gemini-3.5-transcribe');
    expect(urls.some((u) => u.includes('/v1beta/interactions'))).toBe(true);
    expect(urls.some((u) => u.includes('generateContent'))).toBe(false);
  });

  test('the WebSocket-only live model is rejected without reaching the upstream', async () => {
    let interactionsCalled = false;
    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('generativelanguage.googleapis.com')) {
        interactionsCalled = true;
        return Response.json({});
      }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    process.env.GEMINI_API_KEY = 'test';
    const response = await buildApp().fetch(request({
      'X-STT-Provider': 'gemini-transcribe',
      'X-STT-Model': 'gemini-3.5-transcribe-live',
    }));

    // Registered in the model registry (so the price is right) but served by the
    // WebSocket route — /transcribe must say so rather than silently substituting
    // the pre-recorded model.
    expect(response.status).toBe(400);
    expect(interactionsCalled).toBe(false);
  });

  test('rejects an oversized Content-Length with 413 before buffering or calling fetch', async () => {
    let fetchCalled = false;
    globalThis.fetch = mock(async () => { fetchCalled = true; return Response.json({}); }) as unknown as typeof fetch;

    const body = new Uint8Array(8);
    const req = new Request('http://localhost/transcribe?license_key=test-license', {
      method: 'POST',
      headers: {
        'Content-Type': 'audio/wav',
        'Content-Length': String(15 * 1024 * 1024),
        'X-STT-Provider': 'gemini-transcribe',
      },
      body,
    });

    const response = await buildApp().fetch(req);
    expect(response.status).toBe(413);
    expect(fetchCalled).toBe(false);
  });
});

describe('OpenAI pre-buffer size gate (413 before any upstream call)', () => {
  afterEach(() => { globalThis.fetch = originalFetch; });

  test('rejects an oversized Content-Length with 413 before buffering or calling fetch', async () => {
    let fetchCalled = false;
    globalThis.fetch = mock(async () => { fetchCalled = true; return Response.json({}); }) as unknown as typeof fetch;

    // Tiny actual body, but a Content-Length declaring >25 MB so the OpenAI
    // cap (OPENAI_INLINE_MAX_BYTES = 25 MiB) gate fires pre-buffer.
    const body = new Uint8Array(8);
    const oversizedContentLength = 26 * 1024 * 1024;
    const req = new Request('http://localhost/transcribe?license_key=test-license', {
      method: 'POST',
      headers: {
        'Content-Type': 'audio/wav',
        'Content-Length': String(oversizedContentLength),
        'X-STT-Provider': 'openai',
      },
      body,
    });

    const response = await buildApp().fetch(req);
    expect(response.status).toBe(413);
    // The gate must fire before any buffering / upstream provider call.
    expect(fetchCalled).toBe(false);
  });
});

describe('Google Chirp pre-buffer size gate (conditional on the GCS scratch bucket)', () => {
  const originalBucket = process.env.GOOGLE_SPEECH_GCS_BUCKET;
  const oversizedContentLength = 50 * 1024 * 1024;

  function oversizedChirpRequest(): Request {
    // Tiny actual body, but a Content-Length well over the 9.5 MB inline cap.
    return new Request('http://localhost/transcribe?license_key=test-license', {
      method: 'POST',
      headers: {
        'Content-Type': 'audio/wav',
        'Content-Length': String(oversizedContentLength),
        'X-STT-Provider': 'google-chirp',
      },
      body: new Uint8Array(8),
    });
  }

  afterEach(() => {
    globalThis.fetch = originalFetch;
    if (originalBucket === undefined) {
      delete process.env.GOOGLE_SPEECH_GCS_BUCKET;
    } else {
      process.env.GOOGLE_SPEECH_GCS_BUCKET = originalBucket;
    }
  });

  test('rejects an oversized Content-Length with 413 when no bucket is configured', async () => {
    delete process.env.GOOGLE_SPEECH_GCS_BUCKET;
    let fetchCalled = false;
    globalThis.fetch = mock(async () => { fetchCalled = true; return Response.json({}); }) as unknown as typeof fetch;

    const response = await buildApp().fetch(oversizedChirpRequest());

    expect(response.status).toBe(413);
    // The gate must fire before any buffering / upstream provider call.
    expect(fetchCalled).toBe(false);
  });

  test('admits the same request once a bucket is configured (batchRecognize has no inline cap)', async () => {
    process.env.GOOGLE_SPEECH_GCS_BUCKET = 'hyperwhisper-stt-scratch';
    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      if (String(input).includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      // The attempt itself fails (no service-account credentials in the test
      // environment). What this asserts is only that the pre-buffer gate let
      // the request through to it.
      return new Response('nope', { status: 500 });
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(oversizedChirpRequest());

    expect(response.status).not.toBe(413);
  });
});

describe('AssemblyAI keyterms preflight credit reservation', () => {
  // A big-enough clip that it's NOT sync-eligible (estimated well over the
  // ~100s sync threshold), so these tests isolate the async keyterms-add-on
  // signal — a small clip would have its reservation dominated by the (higher)
  // sync rate regardless of the keyterms flag; see the dedicated sync-rate
  // reservation tests below for that case.
  const sizeBytes = 5_000_000;

  test('default model (universal-3-5-pro) with keyterms reserves more than without', () => {
    // Omitting the model resolves to the provider default (universal-3-5-pro), which
    // charges the keyterms add-on. The reservation must be larger when a prompt is present.
    const base = estimateCreditsForProviderFallbacks(sizeBytes, 'assemblyai', undefined, false, undefined);
    const withKeyterms = estimateCreditsForProviderFallbacks(sizeBytes, 'assemblyai', undefined, false, 'Foo,Bar');
    expect(withKeyterms).toBeGreaterThan(base);
  });

  test('explicit universal-2 model with a prompt reserves the same as without (keyterms free on universal-2)', () => {
    const base = estimateCreditsForProviderFallbacks(sizeBytes, 'assemblyai', 'universal-2', false, undefined);
    const withKeyterms = estimateCreditsForProviderFallbacks(sizeBytes, 'assemblyai', 'universal-2', false, 'Foo,Bar');
    expect(withKeyterms).toBe(base);
  });

  test('a short non-medical clip with an explicit language reserves at least the sync rate, not just the (lower) async catalog rate', () => {
    // Sync always runs universal-3-5-pro at $0.0075/min — higher than either
    // async tier (universal-2 $0.0025/min, universal-3-5-pro $0.0035/min). A
    // short clip with an explicit language is exactly sync's target case, so
    // the reservation must cover the sync rate or a low-balance account could
    // be deducted more than was reserved when the request actually routes
    // through sync.
    const shortClipBytes = 2048; // well under the ~100s sync-eligibility threshold
    const universal2 = estimateCreditsForProviderFallbacks(shortClipBytes, 'assemblyai', 'universal-2', false, undefined, 'en');
    const universal3Pro = estimateCreditsForProviderFallbacks(shortClipBytes, 'assemblyai', 'universal-3-5-pro', false, undefined, 'en');
    // 10s (the MIN_ESTIMATED_SECONDS floor) at $0.0075/min = 0.00125 USD =
    // 1.25 credits, rounded up to the nearest tenth.
    const minimumSyncRateCredits = 1.3;
    expect(universal2).toBeGreaterThanOrEqual(minimumSyncRateCredits);
    expect(universal3Pro).toBeGreaterThanOrEqual(minimumSyncRateCredits);
  });

  test('a short medical clip reserves only the async rate — medical is excluded from sync eligibility', () => {
    // Medical requests never route through sync (sync has no medical/domain
    // concept), so a short medical clip's reservation must NOT be inflated to
    // the sync rate the way the non-medical test above is. universal-2 base
    // ($0.0025/min) + the medical add-on ($0.0025/min) over the 10s floor is
    // well under the 1.3-credit sync-rate floor asserted above.
    const shortClipBytes = 2048;
    const medicalShort = estimateCreditsForProviderFallbacks(shortClipBytes, 'assemblyai', 'universal-2', true, undefined, 'en');
    const minimumSyncRateCredits = 1.3;
    expect(medicalShort).toBeLessThan(minimumSyncRateCredits);
  });

  test('a short clip with no/auto language reserves only the async rate — sync is never eligible without an explicit language', () => {
    // The REAL sync-eligibility gate in providers/assemblyai.ts also requires
    // an explicit, non-"auto" language before it will even attempt sync. A
    // short, non-medical, auto-language request always goes straight to
    // async, so reserving at the (higher) sync rate here would over-reserve
    // and could wrongly reject a low-balance user who could actually afford
    // the cheaper async request. This must mirror the medical-exclusion test
    // above but via the language condition instead.
    const shortClipBytes = 2048;
    const minimumSyncRateCredits = 1.3;
    const noLanguage = estimateCreditsForProviderFallbacks(shortClipBytes, 'assemblyai', 'universal-2', false, undefined, undefined);
    const autoLanguage = estimateCreditsForProviderFallbacks(shortClipBytes, 'assemblyai', 'universal-2', false, undefined, 'auto');
    const blankLanguage = estimateCreditsForProviderFallbacks(shortClipBytes, 'assemblyai', 'universal-2', false, undefined, '   ');
    expect(noLanguage).toBeLessThan(minimumSyncRateCredits);
    expect(autoLanguage).toBeLessThan(minimumSyncRateCredits);
    expect(blankLanguage).toBeLessThan(minimumSyncRateCredits);
  });

  test('Deepgram primary with an initial_prompt reserves the ElevenLabs fallback surcharge', () => {
    // Deepgram's fallback chain ends at ElevenLabs (scribe_v2), which forwards the
    // initial_prompt and bills the +20% keyterm surcharge. The reservation must
    // account for that even though Deepgram itself charges no surcharge — else a
    // user with credits for the base fallback but not the surcharge passes the
    // gate and gets deducted more than was reserved. Use a large payload so the
    // ElevenLabs surcharge clears the 0.1-credit floor and is observable.
    const bigBytes = 5_000_000;
    const base = estimateCreditsForProviderFallbacks(bigBytes, 'deepgram', 'nova-3-general', false, undefined);
    const withPrompt = estimateCreditsForProviderFallbacks(bigBytes, 'deepgram', 'nova-3-general', false, 'Foo,Bar');
    expect(withPrompt).toBeGreaterThan(base);
  });

  test('Groq and Grok primaries also reserve the ElevenLabs fallback surcharge', () => {
    const bigBytes = 5_000_000;
    for (const provider of ['groq', 'grok'] as const) {
      const base = estimateCreditsForProviderFallbacks(bigBytes, provider, undefined, false, undefined);
      const withPrompt = estimateCreditsForProviderFallbacks(bigBytes, provider, undefined, false, 'Foo,Bar');
      expect(withPrompt).toBeGreaterThan(base);
    }
  });

  test('token-billed Gemini reserves the prompt-token cost on a short clip with a large prompt', () => {
    // Gemini bills the instruction+vocab prompt as text-input tokens, so the
    // reservation must grow with the prompt — else a low-balance account passes
    // the gate on a tiny clip + large vocab and is deducted more than reserved.
    const shortClip = 64_000; // ~8s by the 64 kbps estimate
    const largePrompt = Array.from({ length: 100 }, (_, i) => `Terminology${i}`).join(',');
    const base = estimateCreditsForProviderFallbacks(shortClip, 'gemini', 'gemini-2.5-pro', false, undefined);
    const withPrompt = estimateCreditsForProviderFallbacks(shortClip, 'gemini', 'gemini-2.5-pro', false, largePrompt);
    expect(withPrompt).toBeGreaterThan(base);
  });

  test('OpenAI gpt-4o reservation includes an output-token allowance over the duration-billed whisper-1', () => {
    // gpt-4o-transcribe is token-billed (input + output); its reservation must
    // cover output, so it reserves strictly more than duration-billed whisper-1
    // for the same audio.
    const bigBytes = 5_000_000;
    const whisper = estimateCreditsForProviderFallbacks(bigBytes, 'openai', 'whisper-1');
    const gpt4o = estimateCreditsForProviderFallbacks(bigBytes, 'openai', 'gpt-4o-transcribe');
    const gpt4oMini = estimateCreditsForProviderFallbacks(bigBytes, 'openai', 'gpt-4o-mini-transcribe');
    expect(gpt4o).toBeGreaterThan(whisper);
    expect(gpt4o).toBeGreaterThan(gpt4oMini);
  });

  test('duration-billed self-only providers do NOT inflate the reservation for a prompt', () => {
    // Mistral is duration-billed and has no keyterm surcharge / prompt-token
    // charge in our metering — its reservation must not grow with a prompt.
    const bigBytes = 5_000_000;
    const base = estimateCreditsForProviderFallbacks(bigBytes, 'mistral', undefined, false, undefined);
    const withPrompt = estimateCreditsForProviderFallbacks(bigBytes, 'mistral', undefined, false, 'Foo,Bar');
    expect(withPrompt).toBe(base);
  });
});

describe('existing provider model switching (Deepgram)', () => {
  beforeEach(() => { process.env.DEEPGRAM_API_KEY = 'test-deepgram-key'; });
  afterEach(() => { globalThis.fetch = originalFetch; });

  test('passes the chosen Deepgram model through and uses keywords (not keyterm) for nova-2', async () => {
    let deepgramUrl = '';
    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('api.deepgram.com')) {
        deepgramUrl = url;
        return Response.json({
          results: { channels: [{ alternatives: [{ transcript: 'medical text' }], detected_language: 'en' }] },
          metadata: { duration: 2, request_id: 'dg' },
        });
      }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(
      request({ 'X-STT-Provider': 'deepgram', 'X-STT-Model': 'nova-2-medical' }, '&initial_prompt=Wellbutrin,Lisinopril'),
    );
    const body = await response.json() as { metadata: { stt_model: string } };

    expect(response.status).toBe(200);
    expect(deepgramUrl).toContain('model=nova-2-medical');
    expect(deepgramUrl).not.toContain('keyterm=');
    expect(body.metadata.stt_model).toBe('nova-2-medical');

    // Each term must be its own repeated `keywords` value, NOT comma-joined into
    // one literal `keywords=Wellbutrin,Lisinopril` (which boosts nothing).
    const keywordValues = new URL(deepgramUrl).searchParams.getAll('keywords');
    expect(keywordValues).toEqual(['Wellbutrin', 'Lisinopril']);
  });

  test('nova-3 emits one repeated keyterm value per term (not comma-joined)', async () => {
    let deepgramUrl = '';
    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('api.deepgram.com')) {
        deepgramUrl = url;
        return Response.json({
          results: { channels: [{ alternatives: [{ transcript: 'hello' }], detected_language: 'en' }] },
          metadata: { duration: 2, request_id: 'dg' },
        });
      }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(
      request({ 'X-STT-Provider': 'deepgram', 'X-STT-Model': 'nova-3-general' }, '&initial_prompt=HyperWhisper,SwiftUI'),
    );

    expect(response.status).toBe(200);
    expect(deepgramUrl).not.toContain('keywords=');
    const keytermValues = new URL(deepgramUrl).searchParams.getAll('keyterm');
    expect(keytermValues).toEqual(['HyperWhisper', 'SwiftUI']);
  });
});
