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
const { drainPendingDeductions } = await import('../middleware/credits');

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

  test('a silent clip is reported as no_speech but STILL charged', async () => {
    // Google bills 25 input tokens/sec whether or not a word comes back. Free
    // no-speech here would let one balance fund unlimited paid upstream calls.
    const charges: Array<{ amount: number }> = [];
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('/v1beta/interactions')) {
        return Response.json({
          id: 'interaction-silent',
          steps: [{ content: [{ text: '   ' }] }],
          usage: {
            input_tokens_by_modality: [{ modality: 'audio', tokens: 236 }],
            total_output_tokens: 0,
          },
        });
      }
      if (url.includes('/api/license/credits')) {
        charges.push(JSON.parse(String(init?.body)) as { amount: number });
        return Response.json({ credits_remaining: 999 });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    process.env.GEMINI_API_KEY = 'test';
    const response = await buildApp().fetch(request({ 'X-STT-Provider': 'gemini-transcribe' }));
    const body = await response.json() as {
      text: string;
      duration: number;
      no_speech_detected?: boolean;
      cost: { usd: number; credits: number };
    };
    await drainPendingDeductions(2000);

    expect(body.no_speech_detected).toBe(true);
    expect(body.text).toBe('');
    expect(body.duration).toBeCloseTo(9.44, 2);
    expect(body.cost.usd).toBeCloseTo(236 * (2 / 1e6), 9);
    expect(body.cost.credits).toBeGreaterThan(0);
    expect(charges).toHaveLength(1);
    expect(charges[0]!.amount).toBe(body.cost.credits);
  });

  test('a silent clip from a DURATION-billed provider is still free', async () => {
    // The route gates on the adapter's own cost, so the goodwill the other
    // adapters extend on a silent clip is untouched — no per-provider table.
    const charges: unknown[] = [];
    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('api.deepgram.com')) {
        return Response.json({
          results: { channels: [{ alternatives: [{ transcript: '  ' }] }] },
          // No `duration`: this fixture is about the BILLING gate, so it must not
          // also trip the empty-transcript failover (issue #381), which needs an
          // upstream-reported duration > 0. That path has its own test below.
          metadata: { request_id: 'dg-silent' },
        });
      }
      if (url.includes('/api/license/credits')) {
        charges.push(url);
        return Response.json({ credits_remaining: 999 });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    process.env.DEEPGRAM_API_KEY = 'test';
    const response = await buildApp().fetch(request({ 'X-STT-Provider': 'deepgram' }));
    const body = await response.json() as { no_speech_detected?: boolean; cost: { credits: number } };
    await drainPendingDeductions(2000);

    expect(body.no_speech_detected).toBe(true);
    expect(body.cost.credits).toBe(0);
    expect(charges).toHaveLength(0);
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

// Route log lines are JSON on console.log (lib/logging.ts). The empty-transcript
// cause is OPERATOR data and deliberately not on the response body — a client
// contract — so the assertions that used to read `attempt_failures` off the JSON
// response read the log event instead. Local to this file, like every other
// helper in the suite: a shared import here is one step from `mock.module`,
// which is process-wide in bun. (issue #381, review r2)
async function captureRouteEvents(run: () => Response | Promise<Response>): Promise<{
  response: Response;
  events: Array<Record<string, unknown>>;
}> {
  const events: Array<Record<string, unknown>> = [];
  const originalLog = console.log;
  console.log = (...args: unknown[]) => {
    if (typeof args[0] === 'string' && args[0].startsWith('{')) {
      try {
        const parsed = JSON.parse(args[0]) as Record<string, unknown>;
        if (typeof parsed.event === 'string') events.push(parsed);
      } catch { /* not a route log line */ }
    }
  };
  try {
    return { response: await run(), events };
  } finally {
    console.log = originalLog;
  }
}

describe('empty-transcript failover, end to end (issue #381)', () => {
  const savedXai = process.env.XAI_API_KEY;
  const savedGrok = process.env.GROK_API_KEY;

  beforeEach(() => {
    process.env.XAI_API_KEY = 'test-xai-key';
    delete process.env.GROK_API_KEY;
    process.env.DEEPGRAM_API_KEY = 'test-deepgram-key';
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    if (savedXai === undefined) delete process.env.XAI_API_KEY;
    else process.env.XAI_API_KEY = savedXai;
    if (savedGrok === undefined) delete process.env.GROK_API_KEY;
    else process.env.GROK_API_KEY = savedGrok;
  });

  test('grok returning empty text for 22.2 s of audio is recovered by deepgram, at one charge', async () => {
    const sttCalls: string[] = [];
    const charges: Array<{ amount: number }> = [];

    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('api.x.ai')) {
        sttCalls.push('grok');
        // The incident: a 200 OK, no text, and the upstream's own report that it
        // processed 22.2 seconds of audio.
        return Response.json({ text: '', duration: 22.2, request_id: 'xai-empty' });
      }
      if (url.includes('api.deepgram.com')) {
        sttCalls.push('deepgram');
        return Response.json({
          results: {
            channels: [{ alternatives: [{ transcript: 'hello from deepgram' }], detected_language: 'en' }],
          },
          metadata: { duration: 22.2, request_id: 'dg-recovered' },
        });
      }
      if (url.includes('/api/license/credits')) {
        charges.push(JSON.parse(String(init?.body)) as { amount: number });
        return Response.json({ credits_remaining: 999 });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(request({ 'X-STT-Provider': 'grok' }));
    const body = await response.json() as {
      text: string;
      no_speech_detected?: boolean;
      metadata: { stt_provider: string };
    };
    await drainPendingDeductions(2000);

    expect(response.status).toBe(200);
    expect(body.text).toBe('hello from deepgram');
    expect(body.no_speech_detected).toBeUndefined();
    // Exactly one extra upstream STT call — the chain stops at the first sibling
    // that answers, and groq/elevenlabs are never reached.
    expect(sttCalls).toEqual(['grok', 'deepgram']);
    expect(charges).toHaveLength(1);
    expect(body.metadata.stt_provider).toContain('deepgram');
    expect(body.metadata.stt_provider).toContain('fallback from');
  });

  test('a recovered request reports WHY it was degraded in the LOG, and adds nothing to the response body', async () => {
    // The deploy smoke test's fixture rows exist to catch a provider returning
    // nothing for a language (the deepgram/zh-roger.mp3 row). Once a sibling
    // covers that, the run goes green on a transcript the row's own provider
    // never produced, and `X-STT-Provider` alone cannot say whether the fallback
    // was an empty transcript or a transient 429. The shared `bad_response` kind
    // cannot either — it also means a geo-block page and a truncated body.
    //
    // The discriminator is `emptyTranscript` on the OUTCOME LOG LINE, not on the
    // response body: `/transcribe`'s body is a client contract that has to land
    // in the apps in the same cycle (`CLAUDE.md`), and this PR is server-only.
    // The second half of this test pins that — `attempt_failures` must never
    // appear on the wire. (review r2)
    process.env.GROQ_API_KEY = 'test-groq-key';

    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('api.deepgram.com')) {
        return Response.json({
          results: { channels: [{ alternatives: [{ transcript: '' }] }] },
          metadata: { duration: 8, request_id: 'dg-empty' },
        });
      }
      if (url.includes('api.groq.com')) {
        return Response.json({ text: 'the sibling covered it', language: 'zh', duration: 8 });
      }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const { response, events } = await captureRouteEvents(
      () => buildApp().fetch(request({ 'X-STT-Provider': 'deepgram' })),
    );
    const raw = await response.text();
    const body = JSON.parse(raw) as { text: string };
    await drainPendingDeductions(2000);

    expect(response.status).toBe(200);
    expect(body.text).toBe('the sibling covered it');
    expect(raw).not.toContain('attempt_failures');

    const done = events.find((e) => e.event === 'transcribe.request_done');
    expect(done?.attemptFailures).toEqual([
      expect.objectContaining({ provider: 'deepgram', kind: 'bad_response', emptyTranscript: true }),
    ]);
  });

  test('a transient sibling outage is NOT marked as an empty transcript', async () => {
    // The other half of the discriminator: if every degraded request looked like
    // an empty transcript, an operator could not size the failover's rate.
    process.env.GROQ_API_KEY = 'test-groq-key';

    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('api.deepgram.com')) return new Response('rate limited', { status: 429 });
      if (url.includes('api.groq.com')) {
        return Response.json({ text: 'the sibling covered it', language: 'en', duration: 8 });
      }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const { response, events } = await captureRouteEvents(
      () => buildApp().fetch(request({ 'X-STT-Provider': 'deepgram' })),
    );
    await response.text();
    await drainPendingDeductions(2000);

    expect(response.status).toBe(200);
    const done = events.find((e) => e.event === 'transcribe.request_done');
    const failures = done?.attemptFailures as Array<Record<string, unknown>> | undefined;
    expect(failures).toHaveLength(1);
    expect(failures?.[0]?.provider).toBe('deepgram');
    expect(failures?.[0]?.emptyTranscript).toBeUndefined();
  });

  test('every sibling failing leaves the request as the 200 no_speech it was before the failover', async () => {
    // Spec goal 3: no user's no_speech outcome becomes a hard error. Grok refuses,
    // Deepgram is rate-limited — and before this fix the route ran out of chain,
    // found no `result`, and returned 429. The native client classifies 429 as
    // RETRYABLE (hw-net/src/retry.rs), so it re-uploaded the same silent audio up
    // to 8 times over ~127 s, where NoSpeech is terminal.
    const sttCalls: string[] = [];
    const charges: unknown[] = [];

    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('api.x.ai')) {
        sttCalls.push('grok');
        return Response.json({ text: '', duration: 22.2, request_id: 'xai-empty' });
      }
      if (url.includes('api.deepgram.com')) {
        sttCalls.push('deepgram');
        return new Response('rate limited', { status: 429 });
      }
      if (url.includes('api.groq.com')) {
        sttCalls.push('groq');
        return Response.json({ text: 'groq should never be asked', duration: 22.2 });
      }
      if (url.includes('api.elevenlabs.io')) {
        sttCalls.push('elevenlabs');
        return Response.json({ text: 'elevenlabs should never be asked' });
      }
      if (url.includes('/api/license/credits')) {
        charges.push(JSON.parse(String(init?.body)));
        return Response.json({ credits_remaining: 999 });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    process.env.GROQ_API_KEY = 'test-groq-key';
    process.env.ELEVENLABS_API_KEY = 'test-11-key';
    const response = await buildApp().fetch(request({ 'X-STT-Provider': 'grok' }));
    const body = await response.json() as {
      text: string;
      no_speech_detected?: boolean;
      cost: { credits: number };
      metadata: { stt_provider: string };
    };
    await drainPendingDeductions(2000);

    expect(response.status).toBe(200);
    expect(body.no_speech_detected).toBe(true);
    expect(body.text).toBe('');
    expect(body.cost.credits).toBe(0);
    expect(charges).toHaveLength(0);
    // Spec goal 2, enforced rather than asserted: ONE extra upstream call. The
    // chain stops after the single sibling, so groq and elevenlabs — two more
    // paid calls, at zero revenue — are never reached.
    expect(sttCalls).toEqual(['grok', 'deepgram']);
    // The user chose grok and grok is what the answer is filed under.
    expect(body.metadata.stt_provider).toContain('xai-grok');
    expect(body.metadata.stt_provider).not.toContain('fallback from');
  });

  test('a sibling that fails BEFORE the wire does not spend the one extra call', async () => {
    // #381's literal incident, with one twist: DEEPGRAM_API_KEY unset (staging, or
    // a machine that booted before the secret synced). The adapter throws a plain
    // Error before any fetch.
    //
    // Two regressions in one test. The missing key used to become a 500 carrying
    // the internal secret name, where the pre-PR request was a 200 no_speech. And
    // then round 1's positional budget (`index > refusalIndex + 1`) charged that
    // non-call to the one extra upstream call the spec allows and stopped — so
    // 30 s of real speech was answered `no_speech` while elevenlabs, configured
    // and able to transcribe, was never asked. A chain POSITION is not a call.
    // (review r2)
    const sttCalls: string[] = [];
    delete process.env.DEEPGRAM_API_KEY;
    process.env.GROQ_API_KEY = 'test-groq-key';
    process.env.ELEVENLABS_API_KEY = 'test-11-key';

    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('api.groq.com')) {
        sttCalls.push('groq');
        return Response.json({ text: '', language: 'en', duration: 30 });
      }
      if (url.includes('api.deepgram.com')) {
        sttCalls.push('deepgram');
        return Response.json({ results: {} });
      }
      if (url.includes('api.elevenlabs.io')) {
        sttCalls.push('elevenlabs');
        return Response.json({ text: 'elevenlabs heard the speech', language_code: 'en' });
      }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const { response, events } = await captureRouteEvents(
      () => buildApp().fetch(request({ 'X-STT-Provider': 'groq' })),
    );
    const raw = await response.text();
    const body = JSON.parse(raw) as { text: string; no_speech_detected?: boolean };
    await drainPendingDeductions(2000);

    expect(response.status).toBe(200);
    expect(body.no_speech_detected).toBeUndefined();
    expect(body.text).toBe('elevenlabs heard the speech');
    // And the secret's name never reaches the client.
    expect(raw).not.toContain('DEEPGRAM_API_KEY');
    // Deepgram never reached the wire: the missing key is our gate, not a call.
    // Exactly ONE extra upstream call was spent — on elevenlabs, which answered.
    expect(sttCalls).toEqual(['groq', 'elevenlabs']);

    // A4: the terminal sibling's failure is a breadcrumb, not a silent `break`.
    const done = events.find((e) => e.event === 'transcribe.request_done');
    expect(done?.attemptFailures).toEqual([
      expect.objectContaining({ provider: 'groq', kind: 'bad_response', emptyTranscript: true }),
      expect.objectContaining({ provider: 'deepgram', kind: 'non_retryable' }),
    ]);
    delete process.env.ELEVENLABS_API_KEY;
  });

  test('a keyless sibling still cannot turn silence into a 500 when nothing else can answer', async () => {
    // The floor, unchanged: when every sibling after the refusal fails too, the
    // request is still the 200 no_speech it was before the failover existed.
    const sttCalls: string[] = [];
    delete process.env.DEEPGRAM_API_KEY;
    delete process.env.ELEVENLABS_API_KEY;
    process.env.GROQ_API_KEY = 'test-groq-key';

    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('api.groq.com')) {
        sttCalls.push('groq');
        return Response.json({ text: '', language: 'en', duration: 30 });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(request({ 'X-STT-Provider': 'groq' }));
    const raw = await response.text();
    const body = JSON.parse(raw) as { no_speech_detected?: boolean };

    expect(response.status).toBe(200);
    expect(body.no_speech_detected).toBe(true);
    expect(raw).not.toContain('DEEPGRAM_API_KEY');
    expect(raw).not.toContain('ELEVENLABS_API_KEY');
    expect(sttCalls).toEqual(['groq']);
  });

  test('a SIBLING covering for a failed primary never refuses, so elevenlabs is never reached', async () => {
    // Round 1 granted the refusal at every non-terminal chain position. Chosen
    // elevenlabs, chain ['elevenlabs','deepgram','groq']: elevenlabs 429s, and
    // deepgram at index 1 saw `1 < 2` and refused — pushing a silent clip onto
    // groq's Whisper. On the deepgram/groq/grok chains the same rule ends at
    // elevenlabs, ~15x groq's price and documented as the last resort, called at
    // zero revenue because a no_speech is never billable.
    //
    // The grant is the CALLER'S provider only. Reachable on neither main nor the
    // pre-review-1 commit, so this pins the regression round 1 introduced.
    // (review r2)
    const sttCalls: string[] = [];
    process.env.DEEPGRAM_API_KEY = 'test-deepgram-key';
    process.env.GROQ_API_KEY = 'test-groq-key';
    process.env.ELEVENLABS_API_KEY = 'test-11-key';

    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('api.elevenlabs.io')) {
        sttCalls.push('elevenlabs');
        return new Response('rate limited', { status: 429 });
      }
      if (url.includes('api.deepgram.com')) {
        sttCalls.push('deepgram');
        // A genuine silence, with the upstream reporting the submitted length.
        return Response.json({
          results: { channels: [{ alternatives: [{ transcript: '' }] }] },
          metadata: { duration: 6, request_id: 'dg-silent' },
        });
      }
      if (url.includes('api.groq.com')) {
        sttCalls.push('groq');
        return Response.json({ text: 'Thank you.', language: 'en', duration: 6 });
      }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(request({ 'X-STT-Provider': 'elevenlabs' }));
    const body = await response.json() as { no_speech_detected?: boolean; cost: { credits: number } };
    await drainPendingDeductions(2000);

    expect(response.status).toBe(200);
    expect(body.no_speech_detected).toBe(true);
    expect(body.cost.credits).toBe(0);
    // Deepgram's no_speech is the answer. Groq is never asked, so the Whisper
    // hallucination that would have been billed never happens either.
    expect(sttCalls).toEqual(['elevenlabs', 'deepgram']);
    delete process.env.ELEVENLABS_API_KEY;
  });

  test('a no_speech after a refusal is attributed to the chosen provider AND its model', async () => {
    // The reproduced string was
    //   `Deepgram/whisper-large-v3-turbo (fallback from Deepgram/nova-3-general)`
    // — a Whisper model attributed to Deepgram, falling back from itself. It
    // reached the client header, metadata.stt_provider, the credit-metering row
    // and request_done's finalProvider.
    process.env.DEEPGRAM_API_KEY = 'test-deepgram-key';
    process.env.GROQ_API_KEY = 'test-groq-key';

    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('api.deepgram.com')) {
        return Response.json({
          results: { channels: [{ alternatives: [{ transcript: '' }] }] },
          metadata: { duration: 12, request_id: 'dg-empty' },
        });
      }
      if (url.includes('api.groq.com')) {
        // The sibling agrees: no speech. It must not refuse in turn — the one
        // extra call is spent — and it must not lend its model to the answer.
        return Response.json({ text: '', language: 'en', duration: 12 });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    const response = await buildApp().fetch(request({ 'X-STT-Provider': 'deepgram' }));
    const body = await response.json() as {
      no_speech_detected?: boolean;
      metadata: { stt_provider: string; stt_model?: string };
    };

    expect(response.status).toBe(200);
    expect(body.no_speech_detected).toBe(true);
    expect(body.metadata.stt_provider).toBe('deepgram/nova-3-general');
    expect(body.metadata.stt_model).toBe('nova-3-general');
    expect(response.headers.get('X-STT-Provider')).toBe('deepgram/nova-3-general');
    expect(response.headers.get('X-STT-Model')).toBe('nova-3-general');
    expect(body.metadata.stt_provider).not.toContain('whisper');
    expect(body.metadata.stt_provider).not.toContain('fallback from');
  });

  test('the LAST provider of a geo-degraded chain never refuses', async () => {
    // `attempt === 1` was standing in for "a sibling exists", and it is not the
    // same claim: this request's chain is filtered by region before it is walked.
    // An oversized upload from nrt cannot be replayed, so elevenlabs is dropped
    // and the chain is ['deepgram', 'groq'] — groq is last, and a refusal there
    // has nowhere to go but a 429 for what is a benign no_speech.
    const savedRegion = process.env.FLY_REGION;
    process.env.FLY_REGION = 'nrt';
    process.env.DEEPGRAM_API_KEY = 'test-deepgram-key';
    process.env.GROQ_API_KEY = 'test-groq-key';
    const sttCalls: string[] = [];

    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('api.deepgram.com')) {
        sttCalls.push('deepgram');
        return new Response('rate limited', { status: 429 });
      }
      if (url.includes('api.groq.com')) {
        sttCalls.push('groq');
        return Response.json({ text: '', language: 'en', duration: 9 });
      }
      if (url.includes('api.elevenlabs.io')) {
        sttCalls.push('elevenlabs');
        return Response.json({ text: 'unreachable from nrt' });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    try {
      // Over FLY_REPLAY_MAX_BODY_BYTES (900 KB), so the request stays in nrt and
      // the chain is degraded instead of replayed to iad.
      const audio = new Uint8Array(950_000);
      const response = await buildApp().fetch(new Request(
        'http://localhost/transcribe?license_key=test-license',
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
      const body = await response.json() as {
        no_speech_detected?: boolean;
        metadata: { stt_provider: string; stt_model?: string };
      };

      expect(response.status).toBe(200);
      expect(body.no_speech_detected).toBe(true);
      expect(sttCalls).toEqual(['deepgram', 'groq']);
      // B1: the chosen provider was DROPPED from this region's chain and never
      // contacted, so the no_speech cannot be filed under it. Round 1 answered
      // `elevenlabs/scribe_v2` here — a provider that never saw the audio, which
      // Windows stamps into TranscriptionProviderDiagnostics on exactly this
      // path. (review r2)
      expect(body.metadata.stt_provider).toBe('groq/whisper-large-v3-turbo');
      expect(response.headers.get('X-STT-Provider')).toBe('groq/whisper-large-v3-turbo');
      expect(body.metadata.stt_provider).not.toContain('elevenlabs');
      expect(body.metadata.stt_provider).not.toContain('scribe');
      expect(body.metadata.stt_provider).not.toContain('fallback from');
    } finally {
      if (savedRegion === undefined) delete process.env.FLY_REGION;
      else process.env.FLY_REGION = savedRegion;
    }
  });

  test('a region that cannot reach a chain member never calls it, and never counts it', async () => {
    // A2. `reachableFromRegion()` used to run ONLY when the CHOSEN provider was
    // the geo-blocked one, so a `deepgram` request served from nrt kept
    // elevenlabs on the tail of its chain. Two consequences, both fixed by
    // filtering unconditionally: the route uploaded the audio a third time to a
    // host that answers a 200 text/html FAQ page and can never transcribe it,
    // and `chain.length` — which the empty-transcript grant reads — counted a
    // provider this request cannot use. Round 1 granted groq the refusal at
    // index 1 on `1 < 2` and spent the spec's one extra call on elevenlabs.
    // (review r2)
    const savedRegion = process.env.FLY_REGION;
    process.env.FLY_REGION = 'nrt';
    process.env.DEEPGRAM_API_KEY = 'test-deepgram-key';
    process.env.GROQ_API_KEY = 'test-groq-key';
    process.env.ELEVENLABS_API_KEY = 'test-11-key';
    const sttCalls: string[] = [];

    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('api.deepgram.com')) {
        sttCalls.push('deepgram');
        return new Response('rate limited', { status: 429 });
      }
      if (url.includes('api.groq.com')) {
        sttCalls.push('groq');
        return Response.json({ text: '', language: 'en', duration: 9 });
      }
      if (url.includes('api.elevenlabs.io')) {
        sttCalls.push('elevenlabs');
        // What nrt actually gets: the geo-block FAQ page, as a 200.
        return new Response('<html>Do you restrict access…</html>', {
          status: 200,
          headers: { 'Content-Type': 'text/html' },
        });
      }
      if (url.includes('/api/license/credits')) return Response.json({ credits_remaining: 999 });
      throw new Error(`Unexpected fetch: ${url}`);
    }) as unknown as typeof fetch;

    try {
      const response = await buildApp().fetch(request({ 'X-STT-Provider': 'deepgram' }));
      const body = await response.json() as { no_speech_detected?: boolean };

      expect(response.status).toBe(200);
      expect(body.no_speech_detected).toBe(true);
      expect(sttCalls).toEqual(['deepgram', 'groq']);
      expect(sttCalls).not.toContain('elevenlabs');
    } finally {
      delete process.env.ELEVENLABS_API_KEY;
      if (savedRegion === undefined) delete process.env.FLY_REGION;
      else process.env.FLY_REGION = savedRegion;
    }
  });
});
