import { afterEach, beforeEach, describe, expect, test } from 'bun:test';
import { drainPendingLatencyReports, reportLatencySamples, type LatencySample } from './latency-report';

// Guards only. What the batch contains is the ingest endpoint's contract and is
// covered there; what this file has to protect is which machines are allowed to
// write to a public page at all.

const SAMPLE: LatencySample = {
  provider: 'deepgram',
  model: 'nova-3',
  latencyMs: 420,
  ok: true,
  attempt: 1,
  audioSeconds: 4,
};

const realFetch = globalThis.fetch;
const realEnv = { ...process.env };

let calls: string[] = [];

beforeEach(() => {
  calls = [];
  globalThis.fetch = (async (input: RequestInfo | URL) => {
    calls.push(String(input));
    return new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json' } });
  }) as typeof fetch;

  process.env.FLY_REGION = 'lhr';
  process.env.FLY_APP_NAME = 'hyperwhisper-transcribe';
  process.env.HYPERWHISPER_INTERNAL_SECRET = 'test-secret';
  process.env.NEXTJS_LICENSE_API_URL = 'https://example.test';
});

afterEach(async () => {
  await drainPendingLatencyReports(1_000);
  globalThis.fetch = realFetch;
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
});
