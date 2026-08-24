import { afterEach, beforeEach, describe, expect, test } from 'bun:test';
import { DEFAULT_API_BASE_URL } from './constants';
import { drainPendingLatencyReports, reportLatencySamples, type LatencySample } from './latency-report';

// Two things live here. First, guards: which machines are allowed to write to a
// public page at all. Second, the shaping this sender does on the way out — the
// region stamp, the batch cap, the rounding floors, the failureKind default —
// none of which the ingest endpoint's own tests can see, because they only ever
// receive an already-shaped payload.

const SAMPLE: LatencySample = {
  provider: 'deepgram',
  model: 'nova-3',
  latencyMs: 420,
  ok: true,
  attempt: 1,
  audioSeconds: 4,
};

const realFetch = globalThis.fetch;
const realWarn = console.warn;
const realEnv = { ...process.env };

interface SentRequest {
  url: string;
  init: RequestInit | undefined;
}

/** Shape the ingest endpoint receives; the sender builds it, so it is asserted here. */
interface WirePayload {
  samples: Array<Record<string, unknown>>;
}

let calls: string[] = [];
let requests: SentRequest[] = [];
let warnings: string[] = [];
/** Swappable per test, so one test can 503, reject, or hang. */
let respond: () => Response | Promise<Response>;

function okJson(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function sentPayload(index = 0): WirePayload {
  const body = requests[index]?.init?.body;
  return JSON.parse(String(body)) as WirePayload;
}

/** The one warn line matching an event name, parsed. Fails loudly if there is not exactly one. */
function warnedEvent(event: string): Record<string, unknown> {
  const matches = warnings.filter((line) => line.includes(event));
  expect(matches).toHaveLength(1);
  return JSON.parse(matches[0] as string) as Record<string, unknown>;
}

beforeEach(() => {
  calls = [];
  requests = [];
  warnings = [];
  respond = () => new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json' } });

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    calls.push(String(input));
    requests.push({ url: String(input), init });
    return respond();
  }) as typeof fetch;

  console.warn = (...args: unknown[]) => {
    warnings.push(args.map((arg) => (typeof arg === 'string' ? arg : JSON.stringify(arg))).join(' '));
  };

  process.env.FLY_REGION = 'lhr';
  process.env.FLY_APP_NAME = 'hyperwhisper-transcribe';
  process.env.HYPERWHISPER_INTERNAL_SECRET = 'test-secret';
  process.env.NEXTJS_LICENSE_API_URL = 'https://example.test';
});

afterEach(async () => {
  await drainPendingLatencyReports(1_000);
  globalThis.fetch = realFetch;
  console.warn = realWarn;
  process.env = { ...realEnv };
});

describe('reportLatencySamples', () => {
  test('publishes from the production Fly app', async () => {
    reportLatencySamples([SAMPLE]);
    await drainPendingLatencyReports(1_000);
    expect(calls).toEqual(['https://example.test/api/internal/latency']);
  });

  test('stays silent on the staging app, which shares the same secret', () => {
    process.env.FLY_APP_NAME = 'hyperwhisper-transcribe-staging';
    reportLatencySamples([SAMPLE]);
    expect(calls).toEqual([]);
  });

  test('stays silent when FLY_APP_NAME is unset', () => {
    delete process.env.FLY_APP_NAME;
    reportLatencySamples([SAMPLE]);
    expect(calls).toEqual([]);
  });

  test('stays silent off Fly', () => {
    delete process.env.FLY_REGION;
    reportLatencySamples([SAMPLE]);
    expect(calls).toEqual([]);
  });

  test('sends nothing for an empty batch', () => {
    reportLatencySamples([]);
    expect(calls).toEqual([]);
  });

  test('warns once and sends nothing when the internal secret is not synced to the machine', () => {
    delete process.env.HYPERWHISPER_INTERNAL_SECRET;

    reportLatencySamples([SAMPLE]);

    expect(calls).toEqual([]);
    // Unlike the region/app guards, an unset secret is a misconfiguration, so it is not silent.
    expect(warnings).toEqual(['latency_report.skipped_no_secret']);
  });
});

describe('the request the sender builds', () => {
  test('posts JSON to the ingest endpoint with the internal secret header', async () => {
    reportLatencySamples([SAMPLE]);
    await drainPendingLatencyReports(1_000);

    const request = requests[0];
    expect(request?.url).toBe('https://example.test/api/internal/latency');
    expect(request?.init?.method).toBe('POST');
    expect(request?.init?.headers).toMatchObject({
      'Content-Type': 'application/json',
      'x-internal-secret': 'test-secret',
    });
  });

  test('strips trailing slashes from the configured API base, so the path is never doubled', async () => {
    process.env.NEXTJS_LICENSE_API_URL = 'https://example.test///';

    reportLatencySamples([SAMPLE]);
    await drainPendingLatencyReports(1_000);

    expect(calls).toEqual(['https://example.test/api/internal/latency']);
  });

  test('falls back to the default API base when the URL is not configured', async () => {
    delete process.env.NEXTJS_LICENSE_API_URL;

    reportLatencySamples([SAMPLE]);
    await drainPendingLatencyReports(1_000);

    expect(calls).toEqual([`${DEFAULT_API_BASE_URL}/api/internal/latency`]);
  });

  test('abandons the POST on a 5 second budget, so reporting never outlives the request it measures', async () => {
    reportLatencySamples([SAMPLE]);
    await drainPendingLatencyReports(1_000);

    const signal = requests[0]?.init?.signal;
    expect(signal).toBeInstanceOf(AbortSignal);
    expect(signal?.aborted).toBe(false);
  });
});

describe('the payload the sender shapes', () => {
  test("stamps every sample with the machine's region, which the sample itself never carries", async () => {
    process.env.FLY_REGION = 'fra';

    reportLatencySamples([SAMPLE, { ...SAMPLE, provider: 'groq', attempt: 2 }]);
    await drainPendingLatencyReports(1_000);

    const { samples } = sentPayload();
    expect(samples.map((sample) => sample.flyRegion)).toEqual(['fra', 'fra']);
  });

  test('carries provider, model and attempt position through unchanged', async () => {
    reportLatencySamples([{ ...SAMPLE, provider: 'elevenlabs', model: 'scribe-v1', attempt: 2 }]);
    await drainPendingLatencyReports(1_000);

    expect(sentPayload().samples[0]).toMatchObject({
      provider: 'elevenlabs',
      model: 'scribe-v1',
      attempt: 2,
    });
  });

  test('rounds latency to whole milliseconds and floors it at 1, so a sub-millisecond attempt is not reported as 0', async () => {
    reportLatencySamples([
      { ...SAMPLE, latencyMs: 419.6 },
      { ...SAMPLE, latencyMs: 0.4 },
      { ...SAMPLE, latencyMs: 0 },
    ]);
    await drainPendingLatencyReports(1_000);

    expect(sentPayload().samples.map((sample) => sample.latencyMs)).toEqual([420, 1, 1]);
  });

  test('rounds clip length to whole seconds and floors it at 0, so a bad estimate cannot go negative', async () => {
    reportLatencySamples([
      { ...SAMPLE, audioSeconds: 4.6 },
      { ...SAMPLE, audioSeconds: 0.2 },
      { ...SAMPLE, audioSeconds: -3 },
    ]);
    await drainPendingLatencyReports(1_000);

    expect(sentPayload().samples.map((sample) => sample.audioSeconds)).toEqual([5, 0, 0]);
  });

  test('omits model entirely for a provider that takes no model, rather than sending an empty string', async () => {
    const { model: _model, ...noModel } = SAMPLE;

    reportLatencySamples([noModel, { ...SAMPLE, model: '' }]);
    await drainPendingLatencyReports(1_000);

    const { samples } = sentPayload();
    expect(samples[0]).not.toHaveProperty('model');
    expect(samples[1]).not.toHaveProperty('model');
  });

  test('never sends a failure kind on a successful attempt, even when the sample carries one', async () => {
    reportLatencySamples([{ ...SAMPLE, ok: true, failureKind: 'timeout' }]);
    await drainPendingLatencyReports(1_000);

    const sample = sentPayload().samples[0];
    expect(sample?.ok).toBe(true);
    expect(sample).not.toHaveProperty('failureKind');
  });

  test('keeps a failed attempt’s kind, and defaults a kindless failure to unknown', async () => {
    reportLatencySamples([
      { ...SAMPLE, ok: false, failureKind: 'upstream_5xx' },
      { ...SAMPLE, ok: false },
    ]);
    await drainPendingLatencyReports(1_000);

    expect(sentPayload().samples.map((sample) => sample.failureKind)).toEqual(['upstream_5xx', 'unknown']);
  });

  test('caps the batch at the 20 samples the ingest endpoint accepts, keeping the earliest attempts', async () => {
    const chain = Array.from({ length: 25 }, (_unused, index) => ({
      ...SAMPLE,
      provider: `provider-${index}`,
      attempt: index + 1,
    }));

    reportLatencySamples(chain);
    await drainPendingLatencyReports(1_000);

    const { samples } = sentPayload();
    expect(samples).toHaveLength(20);
    expect(samples[0]?.provider).toBe('provider-0');
    expect(samples[19]?.provider).toBe('provider-19');
  });
});

describe('how the sender handles the ingest response', () => {
  test('logs a rejected batch with the status and the count it tried to send', async () => {
    respond = () => new Response('nope', { status: 503 });

    reportLatencySamples([SAMPLE, { ...SAMPLE, attempt: 2 }]);
    await drainPendingLatencyReports(1_000);

    expect(warnedEvent('latency_report.rejected')).toMatchObject({
      event: 'latency_report.rejected',
      status: 503,
      count: 2,
    });
  });

  test('names the distinct reasons when the ingest silently drops rows from a 200', async () => {
    respond = () => okJson({
      skipped: [
        { index: 0, reason: 'unknown_provider' },
        { index: 1, reason: 'unknown_provider' },
        { index: 2, reason: 'unknown_failure_kind' },
      ],
    });

    reportLatencySamples([SAMPLE, { ...SAMPLE, attempt: 2 }, { ...SAMPLE, attempt: 3 }]);
    await drainPendingLatencyReports(1_000);

    const warned = warnedEvent('latency_report.samples_skipped');
    expect(warned).toMatchObject({ count: 3, skippedCount: 3 });
    expect(warned.reasons).toEqual(['unknown_provider', 'unknown_failure_kind']);
  });

  test('stays quiet when a 200 reports no skipped rows', async () => {
    respond = () => okJson({ skipped: [] });

    reportLatencySamples([SAMPLE]);
    await drainPendingLatencyReports(1_000);

    expect(warnings).toEqual([]);
  });

  test('stays quiet when a 200 body is not JSON at all, rather than logging a parse failure', async () => {
    respond = () => new Response('OK', { status: 200 });

    reportLatencySamples([SAMPLE]);
    await drainPendingLatencyReports(1_000);

    expect(warnings).toEqual([]);
  });

  test('swallows a network failure, logging it once with the count that was lost', async () => {
    respond = () => Promise.reject(new Error('connection reset'));

    // A throw escaping here would reach an unhandled rejection, since call sites never await.
    expect(() => reportLatencySamples([SAMPLE])).not.toThrow();
    await drainPendingLatencyReports(1_000);

    expect(warnedEvent('latency_report.failed')).toMatchObject({
      event: 'latency_report.failed',
      count: 1,
      message: 'connection reset',
    });
  });
});

describe('drainPendingLatencyReports', () => {
  test('returns 0 immediately when nothing is in flight', async () => {
    expect(await drainPendingLatencyReports(1_000)).toBe(0);
  });

  test('counts every in-flight batch and waits for all of them to finish', async () => {
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => { release = resolve; });
    respond = async () => {
      await gate;
      return okJson({ skipped: [{ index: 0, reason: 'unknown_provider' }] });
    };

    reportLatencySamples([SAMPLE]);
    reportLatencySamples([{ ...SAMPLE, provider: 'groq' }]);
    release();

    expect(await drainPendingLatencyReports(2_000)).toBe(2);
    // Both responses were read, not just dispatched: the skipped rows are already logged.
    expect(warnings).toHaveLength(2);
  });

  test('gives up at the timeout instead of holding shutdown open for a hung ingest', async () => {
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => { release = resolve; });
    respond = async () => {
      await gate;
      return okJson({ skipped: [{ index: 0, reason: 'unknown_provider' }] });
    };

    reportLatencySamples([SAMPLE]);

    expect(await drainPendingLatencyReports(20)).toBe(1);
    // Returned before the batch settled, which is the point of the timeout.
    expect(warnings).toEqual([]);

    release();
  });

  test('drops a settled batch from the in-flight set, so the next drain sees nothing', async () => {
    reportLatencySamples([SAMPLE]);
    expect(await drainPendingLatencyReports(1_000)).toBe(1);
    expect(await drainPendingLatencyReports(1_000)).toBe(0);
  });
});
