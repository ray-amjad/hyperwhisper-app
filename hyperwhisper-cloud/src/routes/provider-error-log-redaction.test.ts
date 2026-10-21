// #2163: a provider's error body can quote the transcript, prompt or
// vocabulary it rejected, and every log line ships to Axiom. These tests drive
// the real /post-process and /transcribe routes against upstreams whose 400 and
// 500 bodies echo a marker, capture EVERY console method, and assert the marker
// never reaches a log line while the status and the provider's error code do.

import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { Hono } from 'hono';

mock.module('../lib/redis', () => ({
  redis: {},
  isIPBlocked: async () => false,
  getCachedLicense: async () => ({ isValid: true, credits: 1000, cachedAt: 'cached' }),
  cacheLicense: async () => {},
}));

const { postProcessRoute } = await import('./post-process');
const { transcribeRoute } = await import('./transcribe');
const { drainPendingDeductions } = await import('../middleware/credits');

const MARKER = 'SECRET_TRANSCRIPT_MARKER_2163';
const ERROR_CODE = 'marker_test_error';
const UPSTREAM_REQUEST_ID = 'req_upstream_2163';

/**
 * An error body in the shape most vendors use, with the marker where a real
 * vendor quotes the input: the free-text message, a nested detail, and a raw
 * echo of the request.
 */
function markerBody(): string {
  return JSON.stringify({
    error: { code: ERROR_CODE, type: 'invalid_request_error', message: `Could not process: "${MARKER}"` },
    detail: { message: MARKER },
    echo: `transcript=${MARKER}`,
  });
}

function markerResponse(status: number): Response {
  return new Response(markerBody(), {
    status,
    headers: { 'content-type': 'application/json', 'x-request-id': UPSTREAM_REQUEST_ID },
  });
}

const ENV_KEYS = [
  'CEREBRAS_API_KEY', 'GROQ_API_KEY', 'ANTHROPIC_API_KEY', 'DEEPGRAM_API_KEY', 'ELEVENLABS_API_KEY',
  'SONIOX_API_KEY', 'ASSEMBLYAI_API_KEY', 'OPENAI_API_KEY',
  'AZURE_SPEECH_KEY_EASTUS', 'AZURE_SPEECH_KEY_NORTHEUROPE', 'AZURE_SPEECH_KEY_SOUTHEASTASIA',
] as const;
const savedEnv: Record<string, string | undefined> = {};
const originalFetch = globalThis.fetch;
const CONSOLE_METHODS = ['log', 'info', 'warn', 'error', 'debug'] as const;
type ConsoleMethod = typeof CONSOLE_METHODS[number];
const originalConsole = Object.fromEntries(CONSOLE_METHODS.map((m) => [m, console[m]])) as Record<ConsoleMethod, typeof console.log>;

let logged: string[] = [];
let upstreamCalls: string[] = [];

/** Every upstream answers `status` with the marker body; the licence API answers OK. */
function upstreamsAnswer(status: number): void {
  globalThis.fetch = mock(async (input: RequestInfo | URL) => {
    const url = String(input);
    if (url.includes('/api/license/')) return Response.json({ credits_remaining: 999, success: true });
    upstreamCalls.push(url);
    return markerResponse(status);
  }) as unknown as typeof fetch;
}

/** Everything any console method printed, rendered as the runtime would print it. */
function allLogText(): string {
  return logged.join('\n');
}

beforeEach(() => {
  logged = [];
  upstreamCalls = [];
  for (const key of ENV_KEYS) {
    savedEnv[key] = process.env[key];
    process.env[key] = `test-${key.toLowerCase()}`;
  }
  for (const method of CONSOLE_METHODS) {
    console[method] = (...args: unknown[]) => {
      logged.push(args.map((arg) => (typeof arg === 'string' ? arg : Bun.inspect(arg))).join(' '));
    };
  }
});

afterEach(async () => {
  // Fire-and-forget deductions/hold releases log too; drain them while captured.
  await drainPendingDeductions(2000);
  for (const method of CONSOLE_METHODS) console[method] = originalConsole[method];
  globalThis.fetch = originalFetch;
  for (const key of ENV_KEYS) {
    if (savedEnv[key] === undefined) delete process.env[key];
    else process.env[key] = savedEnv[key];
  }
});

function postProcessApp(): Hono {
  const app = new Hono();
  app.post('/post-process', postProcessRoute);
  return app;
}

function postProcessRequest(headers: Record<string, string> = {}): Request {
  const raw = JSON.stringify({ text: `hello ${MARKER}`, prompt: 'fix grammar', account_key: 'redaction-2163-lk' });
  return new Request('http://localhost/post-process', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'Content-Length': String(Buffer.byteLength(raw)), ...headers },
    body: raw,
  });
}

function transcribeApp(): Hono {
  const app = new Hono();
  app.post('/transcribe', transcribeRoute);
  return app;
}

function transcribeRequest(provider: string): Request {
  const audio = new Uint8Array(2048);
  return new Request('http://localhost/transcribe?license_key=redaction-2163-lk', {
    method: 'POST',
    headers: {
      'Content-Type': 'audio/wav',
      'Content-Length': String(audio.byteLength),
      'X-STT-Provider': provider,
    },
    body: audio,
  });
}

function expectRedactedLogs(status: number): void {
  const text = allLogText();
  expect(logged.length).toBeGreaterThan(0);
  expect(text).not.toContain(MARKER);
  expect(text).toContain(String(status));
}

describe('/post-process never logs an LLM provider error body (#2163)', () => {
  test.each([400, 500])('cerebras (OpenAI-compatible) %i: status, code and request id logged, body not', async (status) => {
    upstreamsAnswer(status);
    const res = await postProcessApp().fetch(postProcessRequest());
    await res.text();

    expect(upstreamCalls.some((url) => url.includes('api.cerebras.ai'))).toBe(true);
    expectRedactedLogs(status);
    const text = allLogText();
    expect(text).toContain(ERROR_CODE);
    expect(text).toContain(UPSTREAM_REQUEST_ID);
  }, 30_000);

  test.each([400, 500])('anthropic %i: the error message the route logs carries the type, not the body', async (status) => {
    upstreamsAnswer(status);
    const res = await postProcessApp().fetch(postProcessRequest({ 'X-LLM-Provider': 'anthropic' }));
    await res.text();

    expect(upstreamCalls.some((url) => url.includes('api.anthropic.com'))).toBe(true);
    expectRedactedLogs(status);
    expect(allLogText()).toContain(`Anthropic API error: ${status} invalid_request_error`);
  }, 30_000);
});

describe('/transcribe never logs an STT provider error body (#2163)', () => {
  test.each([400, 500])('deepgram chain (shared providerHttpError) %i', async (status) => {
    upstreamsAnswer(status);
    const res = await transcribeApp().fetch(transcribeRequest('deepgram'));
    const body = await res.text();

    expect(upstreamCalls.some((url) => url.includes('deepgram.com'))).toBe(true);
    expectRedactedLogs(status);
    expect(allLogText()).toContain(ERROR_CODE);
    expect(allLogText()).toContain(UPSTREAM_REQUEST_ID);
    if (status === 400) {
      // #2131: the client's 400 is a fixed message, never the upstream's wording.
      expect(res.status).toBe(400);
      expect(body).toContain('No transcription provider accepted this audio or its settings');
      expect(body).not.toContain(MARKER);
    }
  }, 30_000);

  test.each([400, 500])('soniox (own status mapping) %i', async (status) => {
    upstreamsAnswer(status);
    const res = await transcribeApp().fetch(transcribeRequest('soniox'));
    const body = await res.text();

    expect(upstreamCalls.some((url) => url.includes('api.soniox.com'))).toBe(true);
    expectRedactedLogs(status);
    expect(allLogText()).toContain(ERROR_CODE);
    if (status === 400) {
      expect(res.status).toBe(400);
      expect(body).not.toContain(MARKER);
    }
  }, 30_000);

  test.each([400, 500])('assemblyai (own status mapping, sync fast path first) %i', async (status) => {
    upstreamsAnswer(status);
    const res = await transcribeApp().fetch(transcribeRequest('assemblyai'));
    await res.text();

    expect(upstreamCalls.some((url) => url.includes('assemblyai.com'))).toBe(true);
    expectRedactedLogs(status);
    expect(allLogText()).toContain(ERROR_CODE);
  }, 30_000);

  test.each([400, 500])('azure-mai %i', async (status) => {
    upstreamsAnswer(status);
    const res = await transcribeApp().fetch(transcribeRequest('azure-mai'));
    await res.text();

    expect(upstreamCalls.some((url) => url.includes('cognitive.microsoft.com'))).toBe(true);
    expectRedactedLogs(status);
    expect(allLogText()).toContain(ERROR_CODE);
  }, 30_000);

  test.each([400, 500])('openai (self-only, shared providerHttpError) %i', async (status) => {
    upstreamsAnswer(status);
    const res = await transcribeApp().fetch(transcribeRequest('openai'));
    await res.text();

    expect(upstreamCalls.some((url) => url.includes('api.openai.com'))).toBe(true);
    expectRedactedLogs(status);
    expect(allLogText()).toContain(ERROR_CODE);
  }, 30_000);
});
