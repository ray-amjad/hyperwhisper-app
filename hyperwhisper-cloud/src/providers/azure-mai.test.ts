import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { transcribeWithAzureMai } from './azure-mai';
import { AudioTooLargeError, ProviderUnavailableError, UnsupportedAudioFormatError } from './types';

const originalFetch = globalThis.fetch;
const ENV_KEYS = [
  'FLY_REGION',
  'AZURE_SPEECH_KEY_EASTUS',
  'AZURE_SPEECH_KEY_NORTHEUROPE',
  'AZURE_SPEECH_KEY_SOUTHEASTASIA',
] as const;
const savedEnv: Record<string, string | undefined> = {};

beforeEach(() => {
  for (const key of ENV_KEYS) savedEnv[key] = process.env[key];
});

afterEach(() => {
  globalThis.fetch = originalFetch;
  for (const key of ENV_KEYS) {
    if (savedEnv[key] === undefined) delete process.env[key];
    else process.env[key] = savedEnv[key];
  }
});

function successResponse(text = 'hello world', locale = 'en-US') {
  return Response.json({
    combinedPhrases: [{ text }],
    durationMilliseconds: 4200,
    phrases: [{ locale }],
  });
}

describe('transcribeWithAzureMai — input gates (no upstream call)', () => {
  test('rejects audio over the 300 MB cap with AudioTooLargeError before any fetch', async () => {
    let called = false;
    globalThis.fetch = mock(async () => { called = true; return successResponse(); }) as unknown as typeof fetch;

    const oversized = new ArrayBuffer(300 * 1024 * 1024 + 1);
    await expect(transcribeWithAzureMai(oversized, 'audio/wav')).rejects.toThrow(AudioTooLargeError);
    expect(called).toBe(false);
  });

  test('rejects an unaccepted content type (webm) with UnsupportedAudioFormatError before any fetch', async () => {
    let called = false;
    globalThis.fetch = mock(async () => { called = true; return successResponse(); }) as unknown as typeof fetch;

    await expect(transcribeWithAzureMai(new ArrayBuffer(10), 'audio/webm;codecs=opus'))
      .rejects.toThrow(UnsupportedAudioFormatError);
    expect(called).toBe(false);
  });

  test('accepts wav/mp3/flac content types (case- and parameter-insensitive)', async () => {
    process.env.AZURE_SPEECH_KEY_EASTUS = 'key';
    let calls = 0;
    globalThis.fetch = mock(async () => { calls += 1; return successResponse(); }) as unknown as typeof fetch;

    await transcribeWithAzureMai(new ArrayBuffer(10), 'audio/WAV');
    await transcribeWithAzureMai(new ArrayBuffer(10), 'audio/mpeg');
    await transcribeWithAzureMai(new ArrayBuffer(10), 'audio/flac');
    expect(calls).toBe(3);
  });

  test('throws a plain (non-fallback) Error when no key is configured for the resolved region', async () => {
    delete process.env.AZURE_SPEECH_KEY_EASTUS;
    delete process.env.FLY_REGION;
    let called = false;
    globalThis.fetch = mock(async () => { called = true; return successResponse(); }) as unknown as typeof fetch;

    await expect(transcribeWithAzureMai(new ArrayBuffer(10), 'audio/wav')).rejects.toThrow(
      'AZURE_SPEECH_KEY_EASTUS not configured',
    );
    expect(called).toBe(false);
  });
});

describe('transcribeWithAzureMai — region routing', () => {
  test('defaults to eastus when FLY_REGION is unset', async () => {
    delete process.env.FLY_REGION;
    process.env.AZURE_SPEECH_KEY_EASTUS = 'east-key';
    let calledUrl = '';
    globalThis.fetch = mock(async (input: RequestInfo | URL) => { calledUrl = String(input); return successResponse(); }) as unknown as typeof fetch;

    await transcribeWithAzureMai(new ArrayBuffer(10), 'audio/wav');
    expect(calledUrl).toContain('eastus.api.cognitive.microsoft.com');
  });

  test('routes an APAC Fly region (nrt) to southeastasia', async () => {
    process.env.FLY_REGION = 'nrt';
    process.env.AZURE_SPEECH_KEY_SOUTHEASTASIA = 'apac-key';
    let calledUrl = '';
    let authHeader = '';
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      calledUrl = String(input);
      authHeader = (init?.headers as Record<string, string>)['Ocp-Apim-Subscription-Key'];
      return successResponse();
    }) as unknown as typeof fetch;

    await transcribeWithAzureMai(new ArrayBuffer(10), 'audio/wav');
    expect(calledUrl).toContain('southeastasia.api.cognitive.microsoft.com');
    expect(authHeader).toBe('apac-key');
  });

  test('routes an EU Fly region (fra) to northeurope', async () => {
    process.env.FLY_REGION = 'fra';
    process.env.AZURE_SPEECH_KEY_NORTHEUROPE = 'eu-key';
    let calledUrl = '';
    globalThis.fetch = mock(async (input: RequestInfo | URL) => { calledUrl = String(input); return successResponse(); }) as unknown as typeof fetch;

    await transcribeWithAzureMai(new ArrayBuffer(10), 'audio/wav');
    expect(calledUrl).toContain('northeurope.api.cognitive.microsoft.com');
  });

  test('falls back to eastus when the preferred regional key is not provisioned', async () => {
    process.env.FLY_REGION = 'nrt'; // prefers southeastasia
    delete process.env.AZURE_SPEECH_KEY_SOUTHEASTASIA; // not provisioned
    process.env.AZURE_SPEECH_KEY_EASTUS = 'east-key';
    let calledUrl = '';
    globalThis.fetch = mock(async (input: RequestInfo | URL) => { calledUrl = String(input); return successResponse(); }) as unknown as typeof fetch;

    await transcribeWithAzureMai(new ArrayBuffer(10), 'audio/wav');
    expect(calledUrl).toContain('eastus.api.cognitive.microsoft.com');
  });
});

describe('transcribeWithAzureMai — request shape', () => {
  beforeEach(() => { process.env.AZURE_SPEECH_KEY_EASTUS = 'east-key'; });

  test('sends a monolingual locale for an explicit non-auto language, and no locales for auto/absent', async () => {
    let lastDefinition: any;
    globalThis.fetch = mock(async (_input: RequestInfo | URL, init?: RequestInit) => {
      const form = init?.body as FormData;
      lastDefinition = JSON.parse(await (form.get('definition') as Blob).text());
      return successResponse();
    }) as unknown as typeof fetch;

    await transcribeWithAzureMai(new ArrayBuffer(10), 'audio/wav', 'fr-FR');
    expect(lastDefinition.locales).toEqual(['fr']);

    await transcribeWithAzureMai(new ArrayBuffer(10), 'audio/wav', 'auto');
    expect(lastDefinition.locales).toBeUndefined();

    await transcribeWithAzureMai(new ArrayBuffer(10), 'audio/wav');
    expect(lastDefinition.locales).toBeUndefined();
  });

  test('parses initial_prompt into a bounded phraseList (dedup separators, drop overlength terms, cap at 100)', async () => {
    let lastDefinition: any;
    globalThis.fetch = mock(async (_input: RequestInfo | URL, init?: RequestInit) => {
      const form = init?.body as FormData;
      lastDefinition = JSON.parse(await (form.get('definition') as Blob).text());
      return successResponse();
    }) as unknown as typeof fetch;

    const tooLong = 'x'.repeat(51);
    const prompt = `HyperWhisper, SwiftUI\n- Fly.io;${tooLong}`;
    await transcribeWithAzureMai(new ArrayBuffer(10), 'audio/wav', undefined, prompt);
    expect(lastDefinition.phraseList.phrases).toEqual(['HyperWhisper', 'SwiftUI', 'Fly.io']);

    const manyTerms = Array.from({ length: 150 }, (_, i) => `term${i}`).join(',');
    await transcribeWithAzureMai(new ArrayBuffer(10), 'audio/wav', undefined, manyTerms);
    expect(lastDefinition.phraseList.phrases.length).toBe(100);
  });

  test('omits phraseList when no initial_prompt is given', async () => {
    let lastDefinition: any;
    globalThis.fetch = mock(async (_input: RequestInfo | URL, init?: RequestInit) => {
      const form = init?.body as FormData;
      lastDefinition = JSON.parse(await (form.get('definition') as Blob).text());
      return successResponse();
    }) as unknown as typeof fetch;

    await transcribeWithAzureMai(new ArrayBuffer(10), 'audio/wav');
    expect(lastDefinition.phraseList).toBeUndefined();
  });

  test('sends the definition part as application/json (Azure rejects text/plain)', async () => {
    let definitionType = '';
    globalThis.fetch = mock(async (_input: RequestInfo | URL, init?: RequestInit) => {
      const form = init?.body as FormData;
      definitionType = (form.get('definition') as Blob).type;
      return successResponse();
    }) as unknown as typeof fetch;

    await transcribeWithAzureMai(new ArrayBuffer(10), 'audio/wav');
    // Blob normalizes the type string (adds charset=utf-8) but the essence must
    // stay application/json — text/plain is what triggers Azure's 400.
    expect(definitionType.startsWith('application/json')).toBe(true);
  });
});

describe('transcribeWithAzureMai — success and no-speech results', () => {
  beforeEach(() => { process.env.AZURE_SPEECH_KEY_EASTUS = 'east-key'; });

  test('returns text, duration, detected language and computed cost on a transcript', async () => {
    globalThis.fetch = mock(async () => successResponse('bonjour le monde', 'fr-FR')) as unknown as typeof fetch;

    const result = await transcribeWithAzureMai(new ArrayBuffer(10), 'audio/wav');
    expect(result.text).toBe('bonjour le monde');
    expect(result.language).toBe('fr-FR');
    expect(result.durationSeconds).toBe(4.2);
    expect(result.source).toBe('azure-mai');
    // 4.2s = 0.07 min * $0.006/min = $0.00042 -> rounded
    expect(result.costUsd).toBeCloseTo(0.00042, 5);
  });

  test('an empty or whitespace-only transcript maps to a zero-cost no_speech result', async () => {
    globalThis.fetch = mock(async () => successResponse('   ', 'en-US')) as unknown as typeof fetch;

    const result = await transcribeWithAzureMai(new ArrayBuffer(10), 'audio/wav');
    expect(result.source).toBe('no_speech');
    expect(result.text).toBe('');
    expect(result.costUsd).toBe(0);
  });

  test('a response with no combinedPhrases at all is also treated as no_speech', async () => {
    globalThis.fetch = mock(async () => Response.json({ durationMilliseconds: 1000 })) as unknown as typeof fetch;

    const result = await transcribeWithAzureMai(new ArrayBuffer(10), 'audio/wav');
    expect(result.source).toBe('no_speech');
  });
});

describe('transcribeWithAzureMai — upstream error mapping', () => {
  beforeEach(() => { process.env.AZURE_SPEECH_KEY_EASTUS = 'east-key'; });

  test('401 maps to a plain Error (invalid key) — not retryable via the fallback chain', async () => {
    globalThis.fetch = mock(async () => new Response('unauthorized', { status: 401 })) as unknown as typeof fetch;
    await expect(transcribeWithAzureMai(new ArrayBuffer(10), 'audio/wav'))
      .rejects.toThrow('Azure Speech subscription key is invalid or expired');
  });

  test('403 maps to a plain Error (suspended/out of quota)', async () => {
    globalThis.fetch = mock(async () => new Response('forbidden', { status: 403 })) as unknown as typeof fetch;
    await expect(transcribeWithAzureMai(new ArrayBuffer(10), 'audio/wav'))
      .rejects.toThrow('Azure Speech subscription is disabled or out of quota');
  });

  test('402 maps to a plain Error (insufficient funds)', async () => {
    globalThis.fetch = mock(async () => new Response('payment required', { status: 402 })) as unknown as typeof fetch;
    await expect(transcribeWithAzureMai(new ArrayBuffer(10), 'audio/wav'))
      .rejects.toThrow('Azure Speech account has insufficient funds');
  });

  test('429 maps to ProviderUnavailableError so the route retries the fallback chain', async () => {
    globalThis.fetch = mock(async () => new Response('rate limited', { status: 429 })) as unknown as typeof fetch;
    await expect(transcribeWithAzureMai(new ArrayBuffer(10), 'audio/wav')).rejects.toThrow(ProviderUnavailableError);
  });

  test('a 5xx maps to ProviderUnavailableError (transient, retryable)', async () => {
    globalThis.fetch = mock(async () => new Response('boom', { status: 503 })) as unknown as typeof fetch;
    await expect(transcribeWithAzureMai(new ArrayBuffer(10), 'audio/wav')).rejects.toThrow(ProviderUnavailableError);
  });

  test('an unmapped 4xx (e.g. 400) surfaces as a plain Error carrying the status', async () => {
    globalThis.fetch = mock(async () => new Response('bad request', { status: 400 })) as unknown as typeof fetch;
    await expect(transcribeWithAzureMai(new ArrayBuffer(10), 'audio/wav')).rejects.toThrow('Azure MAI error: 400');
  });
});
