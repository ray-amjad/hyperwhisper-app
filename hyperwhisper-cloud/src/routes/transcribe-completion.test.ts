// What completeTranscription() is answerable for: whether the caller's account
// is METERED for this transcription, with what cost and what attribution, and
// what the response then tells the client it was charged.
//
// buildTranscriptionSuccess() decides `billable` and is covered on its own in
// transcribe-success.test.ts. Nothing covered whether completion HONOURS that
// decision — a route that deducted on every result would charge for silence,
// and one that never deducted would serve a paid upstream for free.
//
// The seam here is globalThis.fetch, not mock.module: the billing POST that
// middleware/credits sends is the real boundary, and a module mock of
// '../middleware/credits' would stay installed for every LATER test file in the
// same bun process and silence their deduction assertions.

import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { Hono } from 'hono';

import { drainPendingDeductions } from '../middleware/credits';
import type { AuthContext } from '../middleware/auth';
import type { SttProviderId } from '../lib/stt-models';
import type { TranscriptionResult } from '../providers/types';
import { completeTranscription } from './transcribe-completion';
import type { PreparedTranscriptionRequest } from './transcribe-preparation';

const LICENSE_API_BASE = 'http://license.invalid';

const ACCOUNT: AuthContext = {
  identifier: 'acct-under-test',
  credits: 100,
  licenseKey: 'acct-under-test',
};

const originalFetch = globalThis.fetch;
const originalConsoleLog = console.log;
const originalLicenseApiUrl = process.env.NEXTJS_LICENSE_API_URL;

interface BillingPost {
  url: string;
  licenseKey: string;
  amount: number;
  metadata: Record<string, unknown>;
}

let billingPosts: BillingPost[] = [];
let logLines: Array<Record<string, unknown>> = [];

beforeEach(() => {
  billingPosts = [];
  logLines = [];
  process.env.NEXTJS_LICENSE_API_URL = LICENSE_API_BASE;

  globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
    const body = JSON.parse(String(init?.body)) as {
      license_key: string;
      amount: number;
      metadata: Record<string, unknown>;
    };
    billingPosts.push({
      url: String(input),
      licenseKey: body.license_key,
      amount: body.amount,
      metadata: body.metadata,
    });
    // No `credits_remaining`, so the license cache is never written and no
    // Upstash client is constructed.
    return new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json' } });
  }) as unknown as typeof fetch;

  console.log = (line: string) => {
    try {
      logLines.push(JSON.parse(line) as Record<string, unknown>);
    } catch {
      // Not a logEvent line; ignore it.
    }
  };
});

afterEach(async () => {
  await drainPendingDeductions(2000);
  globalThis.fetch = originalFetch;
  console.log = originalConsoleLog;
  if (originalLicenseApiUrl === undefined) {
    delete process.env.NEXTJS_LICENSE_API_URL;
  } else {
    process.env.NEXTJS_LICENSE_API_URL = originalLicenseApiUrl;
  }
});

function requestDone(): Record<string, unknown> {
  const line = logLines.find((entry) => entry.event === 'transcribe.request_done');
  if (!line) throw new Error('no transcribe.request_done line was logged');
  return line;
}

function preparation(
  overrides: Partial<PreparedTranscriptionRequest> = {},
): PreparedTranscriptionRequest {
  return {
    requestId: 'req-completion',
    startTime: performance.now(),
    clientIP: '203.0.113.7',
    provider: 'deepgram',
    model: 'nova-3-general',
    domain: undefined,
    contentType: 'audio/wav',
    language: 'en',
    initialPrompt: undefined,
    mode: 'default',
    clientPlatform: 'macos',
    clientVersion: '2.43.0',
    auth: ACCOUNT,
    audioBuffer: new ArrayBuffer(0),
    latencyOptOut: false,
    latencyReportable: true,
    ...overrides,
  };
}

function transcript(overrides: Partial<TranscriptionResult> = {}): TranscriptionResult {
  return {
    text: 'hello world',
    language: 'en',
    durationSeconds: 12,
    costUsd: 0.002,
    source: 'deepgram',
    ...overrides,
  };
}

interface CompletionOverrides {
  preparation?: Partial<PreparedTranscriptionRequest>;
  result?: TranscriptionResult;
  usedModel?: string;
  servedBy?: SttProviderId;
  chosenProviderAttempted?: boolean;
  fallbackFrom?: SttProviderId;
  fallbackCount?: number;
  attemptFailures?: Array<{
    provider: SttProviderId;
    kind: string;
    status?: number;
    attemptMs?: number;
    emptyTranscript?: true;
  }>;
}

/**
 * Run completeTranscription inside a real Hono handler, then wait for the
 * billing POST it fires without awaiting.
 */
async function complete(overrides: CompletionOverrides = {}): Promise<Response> {
  const prepared = preparation(overrides.preparation);
  const result = overrides.result ?? transcript();
  const app = new Hono();
  app.post('/transcribe', (c) =>
    completeTranscription({
      c,
      preparation: prepared,
      result,
      usedModel: overrides.usedModel ?? prepared.model,
      servedBy: overrides.servedBy ?? prepared.provider,
      chosenProviderAttempted: overrides.chosenProviderAttempted ?? true,
      fallbackFrom: overrides.fallbackFrom,
      fallbackCount: overrides.fallbackCount ?? 0,
      attemptFailures: overrides.attemptFailures ?? [],
    }),
  );
  const response = await app.request('http://localhost/transcribe', { method: 'POST' });
  await drainPendingDeductions(2000);
  return response;
}

describe('completeTranscription billing', () => {
  test('meters a billable transcript against the calling account', async () => {
    await complete();

    expect(billingPosts).toHaveLength(1);
    const [post] = billingPosts;
    expect(post.url).toBe(`${LICENSE_API_BASE}/api/license/credits`);
    expect(post.licenseKey).toBe('acct-under-test');
    expect(post.amount).toBe(2);
    expect(post.metadata).toEqual({
      audio_duration_seconds: 12,
      transcription_cost_usd: 0.002,
      language: 'en',
      mode: 'default',
      endpoint: '/transcribe',
      stt_provider: 'deepgram/nova-3-general',
      stt_model: 'nova-3-general',
    });
  });

  test('does not meter a no-speech result the upstream charged nothing for', async () => {
    const response = await complete({
      result: transcript({ text: '', language: undefined, costUsd: 0, source: 'no_speech' }),
    });

    // A deduction of $0 would still post, at the 0.1 credit floor, so an empty
    // list means the deduction was never started.
    expect(billingPosts).toHaveLength(0);
    expect(response.headers.get('X-Credits-Used')).toBe('0.0');
    const body = await response.json();
    expect(body.no_speech_detected).toBe(true);
    expect(body.cost).toEqual({ usd: 0, credits: 0 });
  });

  test('meters a no-speech result the upstream still charged for', async () => {
    // gemini-transcribe bills its audio input in tokens whether or not a word
    // comes back. Skipping the deduction here would make silent audio a free
    // channel to a paid upstream.
    const response = await complete({
      preparation: { provider: 'gemini-transcribe', model: 'gemini-3.5-transcribe' },
      result: transcript({
        text: '',
        language: undefined,
        durationSeconds: 8,
        costUsd: 0.0004,
        source: 'no_speech',
      }),
      usedModel: 'gemini-3.5-transcribe',
      servedBy: 'gemini-transcribe',
    });

    expect(billingPosts).toHaveLength(1);
    expect(billingPosts[0].amount).toBe(0.4);
    expect(billingPosts[0].metadata.transcription_cost_usd).toBe(0.0004);
    expect(billingPosts[0].metadata.stt_provider).toBe('gemini-transcribe/gemini-3.5-transcribe');
    expect(response.headers.get('X-Credits-Used')).toBe('0.4');
    expect((await response.json()).no_speech_detected).toBe(true);
  });

  test('meters the model that actually ran, not the one the client asked for', async () => {
    // AssemblyAI silently downgrades universal-3-5-pro to universal-2 for some
    // languages and bills at the model that ran.
    const response = await complete({
      preparation: { provider: 'assemblyai', model: 'universal-3-5-pro' },
      result: transcript({ source: 'assemblyai', model: 'universal-2' }),
      usedModel: 'universal-2',
      servedBy: 'assemblyai',
    });

    expect(billingPosts[0].metadata.stt_model).toBe('universal-2');
    expect(billingPosts[0].metadata.stt_provider).toBe('assemblyai/universal-2');
    expect(response.headers.get('X-STT-Model')).toBe('universal-2');
  });

  test('meters a fallback transcript under the provider that answered', async () => {
    const response = await complete({
      preparation: { provider: 'elevenlabs', model: 'scribe_v2' },
      result: transcript({ source: 'deepgram' }),
      usedModel: 'nova-3-general',
      servedBy: 'deepgram',
      fallbackFrom: 'elevenlabs',
      fallbackCount: 1,
    });

    const label = 'deepgram/nova-3-general (fallback from elevenlabs/scribe_v2)';
    expect(billingPosts[0].metadata.stt_provider).toBe(label);
    expect(response.headers.get('X-STT-Provider')).toBe(label);
    expect((await response.json()).metadata.stt_provider).toBe(label);
  });

  test('falls back to the requested language when the upstream reports none', async () => {
    await complete({
      preparation: { language: 'ja' },
      result: transcript({ language: undefined }),
    });

    expect(billingPosts[0].metadata.language).toBe('ja');
  });

  test("records 'auto' when neither the upstream nor the request named a language", async () => {
    await complete({
      preparation: { language: undefined },
      result: transcript({ language: undefined }),
    });

    expect(billingPosts[0].metadata.language).toBe('auto');
  });
});

describe('completeTranscription response', () => {
  test('headers report the same request, cost and credits as the body', async () => {
    const response = await complete();
    const body = await response.json();

    expect(response.headers.get('X-Request-ID')).toBe('req-completion');
    expect(body.metadata.request_id).toBe('req-completion');
    // Six decimal places, so a sub-cent provider cost survives the header.
    expect(response.headers.get('X-Total-Cost-Usd')).toBe('0.002000');
    expect(body.cost.usd).toBe(0.002);
    expect(response.headers.get('X-Credits-Used')).toBe('2.0');
    expect(body.cost.credits).toBe(2);
    expect(body.text).toBe('hello world');
    expect(body.duration).toBe(12);
  });

  test('omits X-STT-Model when no model id reached the vendor', async () => {
    const response = await complete({
      preparation: { provider: 'grok', model: '' },
      result: transcript({ source: 'grok' }),
      usedModel: '',
      servedBy: 'grok',
    });

    expect(response.headers.get('X-STT-Model')).toBeNull();
    // grok is the one provider whose public label differs from its id.
    expect(response.headers.get('X-STT-Provider')).toBe('xai-grok');
    expect(billingPosts[0].metadata.stt_model).toBeUndefined();
  });
});

describe('completeTranscription outcome log', () => {
  test('names the final provider and the fallback count', async () => {
    await complete({
      preparation: { provider: 'elevenlabs', model: 'scribe_v2' },
      result: transcript({ source: 'deepgram' }),
      usedModel: 'nova-3-general',
      servedBy: 'deepgram',
      fallbackFrom: 'elevenlabs',
      fallbackCount: 1,
      attemptFailures: [{ provider: 'elevenlabs', kind: 'upstream_error', status: 503 }],
    });

    const line = requestDone();
    expect(line.finalProvider).toBe('deepgram/nova-3-general (fallback from elevenlabs/scribe_v2)');
    expect(line.fallbackCount).toBe(1);
    expect(line.attemptFailures).toEqual([
      { provider: 'elevenlabs', kind: 'upstream_error', status: 503 },
    ]);
    expect(line.clientPlatform).toBe('macos');
    expect(line.clientVersion).toBe('2.43.0');
    expect(line.noSpeech).toBe(false);
    expect(line.creditsUsed).toBe(2);
  });

  test('leaves attemptFailures off a clean first-attempt success', async () => {
    await complete();

    expect(requestDone()).not.toHaveProperty('attemptFailures');
  });

  test('leaves latencySkipped off a request that contributed a timing', async () => {
    await complete();

    expect(requestDone()).not.toHaveProperty('latencySkipped');
  });

  test("marks a client that opted out as 'opted_out'", async () => {
    await complete({ preparation: { latencyOptOut: true, latencyReportable: false } });

    expect(requestDone().latencySkipped).toBe('opted_out');
  });

  test("marks a build with no opt-out switch as 'client_too_old'", async () => {
    // Not opted out — it could not opt out. The two have to stay separable, or
    // a thin /latency dataset cannot be told from a broken ingest.
    await complete({
      preparation: {
        clientVersion: '2.10.0',
        latencyOptOut: false,
        latencyReportable: false,
      },
    });

    expect(requestDone().latencySkipped).toBe('client_too_old');
  });
});
