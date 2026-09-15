// What completeTranscription() is answerable for: whether the caller's account
// is METERED for this transcription, with what cost and what attribution, and
// what the response then tells the client it was charged.
//
// buildTranscriptionSuccess() decides `billable` and is covered on its own in
// transcribe-success.test.ts. Nothing covered whether completion HONOURS that
// decision — a route that deducted on every result would charge for silence,
// and one that never deducted would serve a paid upstream for free.

import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { Hono } from 'hono';

import type { AuthContext } from '../middleware/auth';
import type { SttProviderId } from '../lib/stt-models';
import type { TranscriptionResult } from '../providers/types';

interface DeductionCall {
  auth: AuthContext;
  costUsd: number;
  metadata: Record<string, unknown>;
  clientIP: string;
}

const deductions: DeductionCall[] = [];

// Mock at the billing boundary, not below it: the real deductCredits() posts to
// the license API. credits.test.ts owns what it does with these arguments; this
// file owns whether it is called, and with which.
mock.module('../middleware/credits', () => ({
  deductCredits: (
    auth: AuthContext,
    costUsd: number,
    metadata: Record<string, unknown>,
    clientIP: string,
  ): Promise<number> => {
    deductions.push({ auth, costUsd, metadata, clientIP });
    return Promise.resolve(0);
  },
}));

const { completeTranscription } = await import('./transcribe-completion');
type PreparedTranscriptionRequest =
  import('./transcribe-preparation').PreparedTranscriptionRequest;

const originalConsoleLog = console.log;
let logLines: Array<Record<string, unknown>> = [];

beforeEach(() => {
  deductions.length = 0;
  logLines = [];
  console.log = (line: string) => {
    try {
      logLines.push(JSON.parse(line));
    } catch {
      // A non-JSON line is not a logEvent line; ignore it.
    }
  };
});

afterEach(() => {
  console.log = originalConsoleLog;
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
    auth: { identifier: 'acct-under-test', credits: 100, licenseKey: 'acct-under-test' },
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

/** Run completeTranscription inside a real Hono handler and read what it returned. */
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
  return app.request('http://localhost/transcribe', { method: 'POST' });
}

describe('completeTranscription billing', () => {
  test('meters a billable transcript against the calling account', async () => {
    await complete();

    expect(deductions).toHaveLength(1);
    const [deduction] = deductions;
    expect(deduction.auth.identifier).toBe('acct-under-test');
    expect(deduction.costUsd).toBe(0.002);
    expect(deduction.clientIP).toBe('203.0.113.7');
    expect(deduction.metadata).toEqual({
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

    expect(deductions).toHaveLength(0);
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

    expect(deductions).toHaveLength(1);
    expect(deductions[0].costUsd).toBe(0.0004);
    expect(deductions[0].metadata.stt_provider).toBe('gemini-transcribe/gemini-3.5-transcribe');
    expect(response.headers.get('X-Credits-Used')).toBe('0.4');
    expect((await response.json()).no_speech_detected).toBe(true);
  });

  test('meters the model that actually ran, not the one the client asked for', async () => {
    // AssemblyAI silently downgrades universal-3-5-pro → universal-2 for some
    // languages and bills at the model that ran.
    const response = await complete({
      preparation: { provider: 'assemblyai', model: 'universal-3-5-pro' },
      result: transcript({ source: 'assemblyai', model: 'universal-2' }),
      usedModel: 'universal-2',
      servedBy: 'assemblyai',
    });

    expect(deductions[0].metadata.stt_model).toBe('universal-2');
    expect(deductions[0].metadata.stt_provider).toBe('assemblyai/universal-2');
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
    expect(deductions[0].metadata.stt_provider).toBe(label);
    expect(response.headers.get('X-STT-Provider')).toBe(label);
    expect((await response.json()).metadata.stt_provider).toBe(label);
  });

  test('falls back to the requested language when the upstream reports none', async () => {
    await complete({
      preparation: { language: 'ja' },
      result: transcript({ language: undefined }),
    });

    expect(deductions[0].metadata.language).toBe('ja');
  });

  test("records 'auto' when neither the upstream nor the request named a language", async () => {
    await complete({
      preparation: { language: undefined },
      result: transcript({ language: undefined }),
    });

    expect(deductions[0].metadata.language).toBe('auto');
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

  test('omits X-STT-Model for a provider that takes no model', async () => {
    const response = await complete({
      preparation: { provider: 'grok', model: '' },
      result: transcript({ source: 'grok' }),
      usedModel: '',
      servedBy: 'grok',
    });

    expect(response.headers.get('X-STT-Model')).toBeNull();
    // grok is the one provider whose public label differs from its id.
    expect(response.headers.get('X-STT-Provider')).toBe('xai-grok');
    expect(deductions[0].metadata.stt_model).toBeUndefined();
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
