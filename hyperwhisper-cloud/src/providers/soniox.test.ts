import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { transcribeWithSoniox } from './soniox';
import { AudioTooLargeError, ProviderInputError, ProviderUnavailableError } from './types';

const originalFetch = globalThis.fetch;

beforeEach(() => {
  process.env.SONIOX_API_KEY = 'test-key';
});

afterEach(() => {
  globalThis.fetch = originalFetch;
  delete process.env.SONIOX_API_KEY;
});

const BASE = 'https://api.soniox.com';
const UPLOAD_URL = `${BASE}/v1/files`;
const CREATE_URL = `${BASE}/v1/transcriptions`;
const JOB_URL = `${CREATE_URL}/job-abc`;

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
}

// 48,000 bytes -> a clean 6s estimate at the 480,000 bytes/min heuristic
// (estimateSecondsFromBytes / BYTES_PER_MINUTE_ESTIMATE), so the fail-closed
// duration fallback has a predictable value.
const SMALL_AUDIO = new ArrayBuffer(48_000);
// The provider's own pre-upload gate: 30 minutes at the same heuristic.
const MAX_BYTES = 30 * 480_000;

type Call = { url: string; method: string; body?: unknown; authorization?: string };

/**
 * Routes the full Soniox flow: upload -> create -> poll -> transcript, plus the
 * two cleanup DELETEs. Each poll consumes one entry from `pollBodies` (the last
 * repeats if polled more often than provided).
 *
 * The poll loop sleeps for a real second per iteration, so tests that only care
 * about request shape or upstream error mapping pass an error `uploadStatus` /
 * `createStatus` to short-circuit before the loop is ever entered.
 */
function mockSonioxFlow(opts: {
  pollBodies?: Array<{ status: number; body?: unknown; raw?: string }>;
  uploadStatus?: { status: number; body?: unknown; raw?: string };
  createStatus?: { status: number; body?: unknown; raw?: string };
  transcriptStatus?: { status: number; body?: unknown; raw?: string };
  deleteStatus?: number;
}) {
  const calls: Call[] = [];
  const pollBodies = opts.pollBodies ?? [];
  let pollIndex = 0;

  const respond = (spec: { status: number; body?: unknown; raw?: string }) => (
    spec.raw !== undefined
      ? new Response(spec.raw, { status: spec.status })
      : jsonResponse(spec.body ?? {}, spec.status)
  );

  globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const method = init?.method || 'GET';
    const authorization = (init?.headers as Record<string, string> | undefined)?.Authorization;

    if (url === UPLOAD_URL && method === 'POST') {
      calls.push({ url, method, authorization });
      return respond(opts.uploadStatus ?? { status: 200, body: { id: 'file-xyz' } });
    }
    if (url === CREATE_URL && method === 'POST') {
      calls.push({ url, method, body: JSON.parse(init!.body as string), authorization });
      return respond(opts.createStatus ?? { status: 200, body: { id: 'job-abc' } });
    }
    if (url === `${JOB_URL}/transcript` && method === 'GET') {
      calls.push({ url, method, authorization });
      return respond(opts.transcriptStatus ?? { status: 200, body: { text: 'default transcript' } });
    }
    if (url === JOB_URL && method === 'GET') {
      calls.push({ url, method, authorization });
      const entry = pollBodies[Math.min(pollIndex, pollBodies.length - 1)];
      pollIndex += 1;
      if (!entry) throw new Error('Unexpected poll: no pollBodies configured');
      return respond(entry);
    }
    if (method === 'DELETE') {
      calls.push({ url, method, authorization });
      return new Response(null, { status: opts.deleteStatus ?? 200 });
    }
    throw new Error(`Unexpected fetch: ${method} ${url}`);
  }) as unknown as typeof fetch;

  return calls;
}

const completedPoll = (body: Record<string, unknown> = {}) => ({
  status: 200,
  body: { status: 'completed', audio_duration_ms: 120_000, ...body },
});

describe('transcribeWithSoniox — configuration', () => {
  test('a missing SONIOX_API_KEY throws before any upstream call', async () => {
    delete process.env.SONIOX_API_KEY;
    let called = false;
    globalThis.fetch = mock(async () => { called = true; return jsonResponse({}); }) as unknown as typeof fetch;

    await expect(transcribeWithSoniox(SMALL_AUDIO, 'audio/wav')).rejects.toThrow('SONIOX_API_KEY not configured');
    expect(called).toBe(false);
  });

  test('every request carries the bearer credential', async () => {
    const calls = mockSonioxFlow({ createStatus: { status: 400, body: {} } });
    await expect(transcribeWithSoniox(SMALL_AUDIO, 'audio/wav')).rejects.toThrow(ProviderInputError);
    expect(calls.length).toBeGreaterThan(0);
    expect(calls.every((c) => c.authorization === 'Bearer test-key')).toBe(true);
  });
});

describe('transcribeWithSoniox — pre-upload size gate', () => {
  // The gate exists because a job still processing at the poll deadline can't be
  // deleted (Soniox 409), which would orphan a running, billable upstream job —
  // so oversized audio must be rejected before ANY upstream resource is created.
  test('audio over the 30-minute cap throws AudioTooLargeError without creating an upstream file or job', async () => {
    let called = false;
    globalThis.fetch = mock(async () => { called = true; return jsonResponse({}); }) as unknown as typeof fetch;

    const oversized = new ArrayBuffer(MAX_BYTES + 1);
    await expect(transcribeWithSoniox(oversized, 'audio/wav')).rejects.toThrow(AudioTooLargeError);
    expect(called).toBe(false);
  });

  test('the rejection reports the actual and maximum byte counts', async () => {
    globalThis.fetch = mock(async () => jsonResponse({})) as unknown as typeof fetch;
    const oversized = new ArrayBuffer(MAX_BYTES + 1);

    const error = await transcribeWithSoniox(oversized, 'audio/wav').catch((e) => e);
    expect(error).toBeInstanceOf(AudioTooLargeError);
    expect((error as AudioTooLargeError).actualBytes).toBe(MAX_BYTES + 1);
    expect((error as AudioTooLargeError).maxBytes).toBe(MAX_BYTES);
  });

  test('audio exactly at the cap is accepted and proceeds to upload', async () => {
    const calls = mockSonioxFlow({ uploadStatus: { status: 503, body: {} } });
    await expect(transcribeWithSoniox(new ArrayBuffer(MAX_BYTES), 'audio/wav')).rejects.toThrow(ProviderUnavailableError);
    expect(calls.map((c) => c.url)).toEqual([UPLOAD_URL]);
  });
});

describe('transcribeWithSoniox — create request shape', () => {
  // Each case reads the create-phase body captured just before an immediate
  // create-phase error short-circuits ahead of the poll loop's real sleep.
  async function createBodyFor(...args: Parameters<typeof transcribeWithSoniox>) {
    const calls = mockSonioxFlow({ createStatus: { status: 400, body: {} } });
    await expect(transcribeWithSoniox(...args)).rejects.toThrow(ProviderInputError);
    return calls.find((c) => c.url === CREATE_URL)!.body as Record<string, any>;
  }

  test('defaults to stt-async-v5 and always requests language identification', async () => {
    const body = await createBodyFor(SMALL_AUDIO, 'audio/wav');
    expect(body).toMatchObject({ file_id: 'file-xyz', model: 'stt-async-v5', enable_language_identification: true });
  });

  test('an explicitly pinned legacy v4 model is canonicalized to v5', async () => {
    const body = await createBodyFor(SMALL_AUDIO, 'audio/wav', undefined, undefined, { model: 'stt-async-v4' });
    expect(body.model).toBe('stt-async-v5');
  });

  test('a BCP-47 language is stripped to its bare ISO subtag so the hint actually biases detection', async () => {
    expect((await createBodyFor(SMALL_AUDIO, 'audio/wav', 'pt-BR')).language_hints).toEqual(['pt']);
    expect((await createBodyFor(SMALL_AUDIO, 'audio/wav', 'en_US')).language_hints).toEqual(['en']);
    expect((await createBodyFor(SMALL_AUDIO, 'audio/wav', 'FR')).language_hints).toEqual(['fr']);
  });

  test('an absent or "auto" language omits language_hints entirely', async () => {
    expect((await createBodyFor(SMALL_AUDIO, 'audio/wav')).language_hints).toBeUndefined();
    expect((await createBodyFor(SMALL_AUDIO, 'audio/wav', 'auto')).language_hints).toBeUndefined();
    expect((await createBodyFor(SMALL_AUDIO, 'audio/wav', 'AUTO')).language_hints).toBeUndefined();
  });

  test('initial_prompt is split on commas, semicolons and newlines, with list bullets stripped', async () => {
    const body = await createBodyFor(SMALL_AUDIO, 'audio/wav', 'en', 'HyperWhisper, SwiftUI; Fly.io\n- Upstash\n* Hono');
    expect(body.context).toEqual({ terms: ['HyperWhisper', 'SwiftUI', 'Fly.io', 'Upstash', 'Hono'] });
  });

  test('terms longer than 80 characters are dropped rather than sent', async () => {
    const body = await createBodyFor(SMALL_AUDIO, 'audio/wav', 'en', `keep,${'x'.repeat(81)},also-keep`);
    expect(body.context.terms).toEqual(['keep', 'also-keep']);
  });

  test('the context term list is capped at 200 entries', async () => {
    const prompt = Array.from({ length: 250 }, (_, i) => `term${i}`).join(',');
    const body = await createBodyFor(SMALL_AUDIO, 'audio/wav', 'en', prompt);
    expect(body.context.terms.length).toBe(200);
    expect(body.context.terms[199]).toBe('term199');
  });

  test('an absent or all-blank initial_prompt omits the context object', async () => {
    expect((await createBodyFor(SMALL_AUDIO, 'audio/wav', 'en')).context).toBeUndefined();
    expect((await createBodyFor(SMALL_AUDIO, 'audio/wav', 'en', ' , ; ')).context).toBeUndefined();
  });
});

describe('transcribeWithSoniox — upstream error mapping', () => {
  test.each([
    [401, 'Soniox API key is invalid or unauthorized'],
    [403, 'Soniox API key is invalid or unauthorized'],
  ])('an upload %i throws a plain non-fallback Error', async (status, message) => {
    mockSonioxFlow({ uploadStatus: { status, body: { error: 'nope' } } });
    const error = await transcribeWithSoniox(SMALL_AUDIO, 'audio/wav').catch((e) => e);
    expect(error).not.toBeInstanceOf(ProviderUnavailableError);
    expect((error as Error).message).toBe(message);
  });

  test.each([
    ['429 rate limiting', 429],
    ['402 upstream billing exhaustion', 402],
    ['a 500', 500],
    ['a 503', 503],
  ])('%s maps to ProviderUnavailableError', async (_label, status) => {
    mockSonioxFlow({ uploadStatus: { status, body: { error: 'boom' } } });
    await expect(transcribeWithSoniox(SMALL_AUDIO, 'audio/wav')).rejects.toThrow(ProviderUnavailableError);
  });

  test('an unmapped 4xx maps to ProviderInputError carrying the status', async () => {
    mockSonioxFlow({ uploadStatus: { status: 400, raw: 'bad container' } });
    const error = await transcribeWithSoniox(SMALL_AUDIO, 'audio/wav').catch((e) => e);
    expect(error).toBeInstanceOf(ProviderInputError);
    expect((error as ProviderInputError).status).toBe(400);
    expect((error as Error).message).toContain('bad container');
  });

  test('a malformed (non-JSON) upload response is provider-unavailable, not a client error', async () => {
    mockSonioxFlow({ uploadStatus: { status: 200, raw: 'not json' } });
    await expect(transcribeWithSoniox(SMALL_AUDIO, 'audio/wav')).rejects.toThrow('malformed upload response');
  });

  test('an upload 200 with no file id is provider-unavailable', async () => {
    mockSonioxFlow({ uploadStatus: { status: 200, body: {} } });
    await expect(transcribeWithSoniox(SMALL_AUDIO, 'audio/wav')).rejects.toThrow('upload returned no file id');
  });

  test('a create 200 with no transcription id is provider-unavailable', async () => {
    mockSonioxFlow({ createStatus: { status: 200, body: {} } });
    await expect(transcribeWithSoniox(SMALL_AUDIO, 'audio/wav')).rejects.toThrow('create returned no transcription id');
  });

  test('a malformed (non-JSON) create response is provider-unavailable', async () => {
    mockSonioxFlow({ createStatus: { status: 200, raw: '<html>502</html>' } });
    await expect(transcribeWithSoniox(SMALL_AUDIO, 'audio/wav')).rejects.toThrow('malformed create response');
  });
});

describe('transcribeWithSoniox — poll loop', () => {
  test('a completed job returns the transcript, detected language, billed duration and job id', async () => {
    mockSonioxFlow({
      pollBodies: [completedPoll()],
      transcriptStatus: {
        status: 200,
        body: { text: '  hola mundo  ', tokens: [{}, { language: 'es' }, { language: 'en' }] },
      },
    });

    const result = await transcribeWithSoniox(SMALL_AUDIO, 'audio/wav', 'es');
    expect(result.text).toBe('hola mundo');
    // The first token carrying a language wins; tokens without one are skipped.
    expect(result.language).toBe('es');
    expect(result.durationSeconds).toBe(120);
    expect(result.source).toBe('soniox');
    expect(result.requestId).toBe('job-abc');
    // 120s at the blended ~$0.10/hr async rate, no context terms.
    expect(result.costUsd).toBeCloseTo(0.003333, 6);
  }, 10_000);

  test('it keeps polling through queued/processing states until completion', async () => {
    const calls = mockSonioxFlow({
      pollBodies: [
        { status: 200, body: { status: 'queued' } },
        { status: 200, body: { status: 'processing' } },
        completedPoll({ audio_duration_ms: 6_000 }),
      ],
      transcriptStatus: { status: 200, body: { text: 'done' } },
    });

    const result = await transcribeWithSoniox(SMALL_AUDIO, 'audio/wav');
    expect(result.text).toBe('done');
    expect(calls.filter((c) => c.url === JOB_URL && c.method === 'GET').length).toBe(3);
  }, 15_000);

  test('a transient poll HTTP error is retried rather than failing the request', async () => {
    const calls = mockSonioxFlow({
      pollBodies: [
        { status: 503, body: { error: 'temporarily unavailable' } },
        completedPoll({ audio_duration_ms: 6_000 }),
      ],
      transcriptStatus: { status: 200, body: { text: 'recovered' } },
    });

    const result = await transcribeWithSoniox(SMALL_AUDIO, 'audio/wav');
    expect(result.text).toBe('recovered');
    expect(calls.filter((c) => c.url === JOB_URL && c.method === 'GET').length).toBe(2);
  }, 15_000);

  test('a malformed poll body is retried rather than failing the request', async () => {
    mockSonioxFlow({
      pollBodies: [
        { status: 200, raw: 'not json' },
        completedPoll({ audio_duration_ms: 6_000 }),
      ],
      transcriptStatus: { status: 200, body: { text: 'recovered' } },
    });

    const result = await transcribeWithSoniox(SMALL_AUDIO, 'audio/wav');
    expect(result.text).toBe('recovered');
  }, 15_000);

  test('a 401 during polling fails immediately instead of retrying to the deadline', async () => {
    const calls = mockSonioxFlow({ pollBodies: [{ status: 401, body: { error: 'bad key' } }] });
    await expect(transcribeWithSoniox(SMALL_AUDIO, 'audio/wav'))
      .rejects.toThrow('Soniox API key is invalid or unauthorized');
    expect(calls.filter((c) => c.url === JOB_URL && c.method === 'GET').length).toBe(1);
  }, 10_000);
});

describe('transcribeWithSoniox — failed-job classification', () => {
  // A failed async job is HTTP 200 with status:"failed"; the error_type slug —
  // not the message — decides whether the caller could fix it (4xx) or it was an
  // upstream condition (502). Mislabeling an upstream failure as a client error
  // on this self-only provider would surface a wrong status to the app.
  test.each(['invalid_request', 'invalid_audio_file', 'model_not_available'])(
    'error_type %s is a client input rejection (422)',
    async (errorType) => {
      mockSonioxFlow({ pollBodies: [{ status: 200, body: { status: 'failed', error_type: errorType, error_message: 'nope' } }] });
      const error = await transcribeWithSoniox(SMALL_AUDIO, 'audio/wav').catch((e) => e);
      expect(error).toBeInstanceOf(ProviderInputError);
      expect((error as ProviderInputError).status).toBe(422);
    },
    10_000,
  );

  test.each(['insufficient_funds', 'rate_limit_exceeded', 'internal_error'])(
    'error_type %s is an upstream failure, not a client error',
    async (errorType) => {
      mockSonioxFlow({ pollBodies: [{ status: 200, body: { status: 'failed', error_type: errorType, error_message: 'upstream said no' } }] });
      const error = await transcribeWithSoniox(SMALL_AUDIO, 'audio/wav').catch((e) => e);
      expect(error).toBeInstanceOf(ProviderUnavailableError);
      expect((error as Error).message).toContain('upstream said no');
    },
    10_000,
  );

  test('a failure with no error_type at all falls back to a 422', async () => {
    mockSonioxFlow({ pollBodies: [{ status: 200, body: { status: 'failed', error_message: 'transcription failed' } }] });
    const error = await transcribeWithSoniox(SMALL_AUDIO, 'audio/wav').catch((e) => e);
    expect(error).toBeInstanceOf(ProviderInputError);
    expect((error as ProviderInputError).status).toBe(422);
  }, 10_000);

  test('the legacy status:"error" spelling is treated as a failure too', async () => {
    mockSonioxFlow({ pollBodies: [{ status: 200, body: { status: 'error', error_type: 'invalid_audio_file' } }] });
    await expect(transcribeWithSoniox(SMALL_AUDIO, 'audio/wav')).rejects.toThrow(ProviderInputError);
  }, 10_000);
});

describe('transcribeWithSoniox — transcript fetch and billing', () => {
  test('an empty transcript is a zero-cost, zero-duration no_speech result', async () => {
    mockSonioxFlow({
      pollBodies: [completedPoll()],
      transcriptStatus: { status: 200, body: { text: '   ', tokens: [{ language: 'en' }] } },
    });

    const result = await transcribeWithSoniox(SMALL_AUDIO, 'audio/wav');
    expect(result.source).toBe('no_speech');
    expect(result.costUsd).toBe(0);
    expect(result.durationSeconds).toBe(0);
    expect(result.language).toBe('en');
  }, 10_000);

  test('a successful transcript with a missing duration bills the byte-size estimate, never $0', async () => {
    mockSonioxFlow({
      pollBodies: [{ status: 200, body: { status: 'completed' } }],
      transcriptStatus: { status: 200, body: { text: 'billed anyway' } },
    });

    const result = await transcribeWithSoniox(SMALL_AUDIO, 'audio/wav');
    // 48,000 bytes -> 6s under the shared 64 kbps byte-size heuristic.
    expect(result.durationSeconds).toBe(6);
    expect(result.costUsd).toBeGreaterThan(0);
  }, 10_000);

  test('a zero audio_duration_ms also falls back to the byte-size estimate', async () => {
    mockSonioxFlow({
      pollBodies: [completedPoll({ audio_duration_ms: 0 })],
      transcriptStatus: { status: 200, body: { text: 'billed anyway' } },
    });

    const result = await transcribeWithSoniox(SMALL_AUDIO, 'audio/wav');
    expect(result.durationSeconds).toBe(6);
  }, 10_000);

  test('custom-context terms are billed on top of the audio blend', async () => {
    mockSonioxFlow({
      pollBodies: [completedPoll()],
      transcriptStatus: { status: 200, body: { text: 'hola' } },
    });
    const plain = await transcribeWithSoniox(SMALL_AUDIO, 'audio/wav', 'es');

    mockSonioxFlow({
      pollBodies: [completedPoll()],
      transcriptStatus: { status: 200, body: { text: 'hola' } },
    });
    const terms = Array.from({ length: 200 }, (_, i) => `terminology${i}`).join(',');
    const withContext = await transcribeWithSoniox(SMALL_AUDIO, 'audio/wav', 'es', terms);

    expect(withContext.durationSeconds).toBe(plain.durationSeconds);
    expect(withContext.costUsd).toBeGreaterThan(plain.costUsd);
  }, 20_000);

  test('a 5xx on the transcript fetch maps through the same status classifier', async () => {
    mockSonioxFlow({
      pollBodies: [completedPoll()],
      transcriptStatus: { status: 503, body: { error: 'boom' } },
    });
    await expect(transcribeWithSoniox(SMALL_AUDIO, 'audio/wav')).rejects.toThrow(ProviderUnavailableError);
  }, 10_000);

  test('a malformed transcript body is provider-unavailable', async () => {
    mockSonioxFlow({
      pollBodies: [completedPoll()],
      transcriptStatus: { status: 200, raw: 'not json' },
    });
    await expect(transcribeWithSoniox(SMALL_AUDIO, 'audio/wav')).rejects.toThrow('malformed transcript response');
  }, 10_000);
});

describe('transcribeWithSoniox — mandatory cleanup', () => {
  // Soniox never auto-deletes; orphans count against the 1,000-file / 10 GB
  // account caps, so both resources must be removed on every exit path.
  test('a successful run deletes the transcription and then the file', async () => {
    const calls = mockSonioxFlow({
      pollBodies: [completedPoll()],
      transcriptStatus: { status: 200, body: { text: 'hola' } },
    });

    await transcribeWithSoniox(SMALL_AUDIO, 'audio/wav');
    expect(calls.filter((c) => c.method === 'DELETE').map((c) => c.url))
      .toEqual([`${CREATE_URL}/job-abc`, `${UPLOAD_URL}/file-xyz`]);
  }, 10_000);

  test('a failed job still deletes both resources', async () => {
    const calls = mockSonioxFlow({
      pollBodies: [{ status: 200, body: { status: 'failed', error_type: 'invalid_audio_file' } }],
    });

    await expect(transcribeWithSoniox(SMALL_AUDIO, 'audio/wav')).rejects.toThrow(ProviderInputError);
    expect(calls.filter((c) => c.method === 'DELETE').map((c) => c.url))
      .toEqual([`${CREATE_URL}/job-abc`, `${UPLOAD_URL}/file-xyz`]);
  }, 10_000);

  test('a create-phase failure deletes the uploaded file even though no job exists', async () => {
    const calls = mockSonioxFlow({ createStatus: { status: 500, body: {} } });

    await expect(transcribeWithSoniox(SMALL_AUDIO, 'audio/wav')).rejects.toThrow(ProviderUnavailableError);
    expect(calls.filter((c) => c.method === 'DELETE').map((c) => c.url)).toEqual([`${UPLOAD_URL}/file-xyz`]);
  });

  test('an upload-phase failure issues no DELETE, since nothing was created', async () => {
    const calls = mockSonioxFlow({ uploadStatus: { status: 500, body: {} } });

    await expect(transcribeWithSoniox(SMALL_AUDIO, 'audio/wav')).rejects.toThrow(ProviderUnavailableError);
    expect(calls.filter((c) => c.method === 'DELETE')).toEqual([]);
  });

  test('a cleanup DELETE that fails is swallowed and never masks the transcript', async () => {
    mockSonioxFlow({
      pollBodies: [completedPoll()],
      transcriptStatus: { status: 200, body: { text: 'hola' } },
      deleteStatus: 500,
    });

    const result = await transcribeWithSoniox(SMALL_AUDIO, 'audio/wav');
    expect(result.text).toBe('hola');
    expect(result.source).toBe('soniox');
  }, 10_000);

  test('a cleanup DELETE that throws at the transport layer never masks the transcript', async () => {
    const inner = mockSonioxFlow({
      pollBodies: [completedPoll()],
      transcriptStatus: { status: 200, body: { text: 'hola' } },
    });
    const routed = globalThis.fetch;
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      if ((init?.method || 'GET') === 'DELETE') {
        inner.push({ url: String(input), method: 'DELETE' });
        throw new TypeError('connection reset');
      }
      return routed(input as never, init as never);
    }) as unknown as typeof fetch;

    const result = await transcribeWithSoniox(SMALL_AUDIO, 'audio/wav');
    expect(result.text).toBe('hola');
    // Both deletes were still attempted despite the first one throwing.
    expect(inner.filter((c) => c.method === 'DELETE').length).toBe(2);
  }, 10_000);
});
