// Tests for the Gemini 3.5 Transcribe adapter (/v1beta/interactions).
//
// Only global `fetch` is mocked — never a shared module. bun's `mock.module` is
// process-wide and leaks into every other file in the same `bun test src` run.
//
// The response fixtures below are trimmed copies of REAL responses from the live
// endpoint, including `total_output_tokens: 0` and the
// `input_tokens_by_modality` list form.

import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import {
  buildTranscriptionConfig,
  collectTranscript,
  readInputTokens,
  toVocabularyTerms,
  transcribeWithGeminiTranscribe,
} from './gemini-transcribe';
import { AudioTooLargeError, ProviderInputError, ProviderUnavailableError } from './types';
import { GEMINI_TRANSCRIBE_INLINE_MAX_BYTES } from '../lib/constants';

const originalFetch = globalThis.fetch;
const ENV_KEYS = ['GEMINI_API_KEY', 'GOOGLE_GEMINI_API_KEY'] as const;
const savedEnv: Record<string, string | undefined> = {};

const audio = (bytes = 1_000) => new ArrayBuffer(bytes);

// The live response for the 9.456 s sample clip, trimmed to the fields we read.
const SAMPLE_TRANSCRIPT = 'Hello, this is a test of HyperWhisper transcription. Let us meet on Tuesday, no, Wednesday, um, at the Kalamazoo office.';
const sampleResponse = (text = SAMPLE_TRANSCRIPT) => ({
  id: 'interaction-abc123',
  status: 'completed',
  model: 'gemini-3.5-transcribe',
  steps: [{ content: [{ type: 'text', text }] }],
  usage: {
    total_tokens: 237,
    total_input_tokens: 237,
    input_tokens_by_modality: [
      { modality: 'audio', tokens: 236 },
      { modality: 'text', tokens: 1 },
    ],
    total_cached_tokens: 0,
    total_output_tokens: 0,
  },
});

beforeEach(() => {
  for (const key of ENV_KEYS) savedEnv[key] = process.env[key];
  process.env.GEMINI_API_KEY = 'test-key';
  delete process.env.GOOGLE_GEMINI_API_KEY;
});

afterEach(() => {
  globalThis.fetch = originalFetch;
  for (const key of ENV_KEYS) {
    if (savedEnv[key] === undefined) delete process.env[key];
    else process.env[key] = savedEnv[key];
  }
});

interface Captured {
  url?: string;
  headers?: Record<string, string>;
  body?: Record<string, any>;
}

function captureRequest(payload: unknown = sampleResponse()): Captured {
  const captured: Captured = {};
  globalThis.fetch = mock(async (url: string, init: RequestInit) => {
    captured.url = url;
    captured.headers = init.headers as Record<string, string>;
    captured.body = JSON.parse(init.body as string);
    return Response.json(payload);
  }) as unknown as typeof fetch;
  return captured;
}

function errorResponse(status: number, text = 'upstream said no') {
  globalThis.fetch = mock(async () => new Response(text, { status })) as unknown as typeof fetch;
}

/**
 * Swaps `console.log` for the duration of `run` and returns the details object of
 * the `provider.no_speech` event it logged. Same swap-the-global idiom as
 * `utils.test.ts` — no spy library is used anywhere in this suite.
 */
async function captureNoSpeechEvent(run: () => Promise<unknown>): Promise<Record<string, unknown>> {
  const logged: unknown[][] = [];
  const originalLog = console.log;
  console.log = ((...args: unknown[]) => { logged.push(args); }) as typeof console.log;
  try {
    await run();
  } finally {
    console.log = originalLog;
  }
  const event = logged.find((args) => args[0] === 'provider.no_speech');
  if (!event) throw new Error('no provider.no_speech event was logged');
  return event[1] as Record<string, unknown>;
}

describe('buildTranscriptionConfig — TRAP 2 (the mutual exclusion)', () => {
  // Verified live: sending both gets HTTP 400
  // "custom_vocabulary is incompatible with diarization." / "... with timestamps."
  test('vocabulary wins — diarization and timestamps are dropped, never merged', () => {
    const config = buildTranscriptionConfig('en-US', ['HyperWhisper'], {
      diarizationMode: 'speaker',
      timestampGranularities: ['word'],
    });

    expect(config.custom_vocabulary).toEqual(['HyperWhisper']);
    expect(config).not.toHaveProperty('diarization_mode');
    expect(config).not.toHaveProperty('timestamp_granularities');
  });

  test('the extras are honoured only when there is no vocabulary to conflict with', () => {
    const config = buildTranscriptionConfig('en-US', [], {
      diarizationMode: 'speaker',
      timestampGranularities: ['word'],
    });

    expect(config.diarization_mode).toBe('speaker');
    expect(config.timestamp_granularities).toEqual(['word']);
    expect(config).not.toHaveProperty('custom_vocabulary');
  });

  test('no built config ever carries vocabulary alongside an incompatible field', () => {
    const languages = [undefined, 'auto', 'en', 'en-US'];
    const vocabularies = [[], ['HyperWhisper'], ['a', 'b']];
    const extras = [
      {},
      { diarizationMode: 'speaker' },
      { timestampGranularities: ['word'] },
      { diarizationMode: 'speaker', timestampGranularities: ['word', 'segment'] },
    ];

    for (const language of languages) {
      for (const vocabulary of vocabularies) {
        for (const extra of extras) {
          const config = buildTranscriptionConfig(language, vocabulary, extra);
          if ('custom_vocabulary' in config) {
            expect(config).not.toHaveProperty('diarization_mode');
            expect(config).not.toHaveProperty('timestamp_granularities');
          }
        }
      }
    }
  });

  test('auto-detect omits language_codes entirely; an explicit tag passes through as sent', () => {
    expect(buildTranscriptionConfig(undefined, [])).toEqual({});
    expect(buildTranscriptionConfig('auto', [])).toEqual({});
    expect(buildTranscriptionConfig('', [])).toEqual({});
    // Both forms are accepted upstream — unlike google-chirp, no region tag is
    // forced on, so a caller's 'en' is not rewritten.
    expect(buildTranscriptionConfig('en', []).language_codes).toEqual(['en']);
    expect(buildTranscriptionConfig('en-US', []).language_codes).toEqual(['en-US']);
  });
});

describe('toVocabularyTerms', () => {
  test('splits, strips brackets, collapses whitespace and de-duplicates case-insensitively', () => {
    expect(toVocabularyTerms('HyperWhisper, <b>Kalamazoo</b>\nhyperwhisper; spaced   out'))
      .toEqual(['HyperWhisper', 'bKalamazoo/b', 'spaced out']);
  });

  test('truncates rather than drops an over-long term, matching the Rust sanitizer', () => {
    const terms = toVocabularyTerms('a'.repeat(200));
    expect(terms).toHaveLength(1);
    expect(terms[0]).toHaveLength(80);
  });

  test('keeps a leading dash that is part of the term', () => {
    expect(toVocabularyTerms('-Xmx, - bulleted')).toEqual(['-Xmx', 'bulleted']);
  });

  test('caps the list at 100 terms', () => {
    const terms = toVocabularyTerms(Array.from({ length: 150 }, (_, i) => `term${i}`).join(','));
    expect(terms).toHaveLength(100);
  });
});

describe('transcribeWithGeminiTranscribe — request shape', () => {
  test('posts to /v1beta/interactions with the pinned Api-Revision header', async () => {
    const captured = captureRequest();
    await transcribeWithGeminiTranscribe(audio(), 'audio/mp3');

    expect(captured.url).toBe('https://generativelanguage.googleapis.com/v1beta/interactions');
    expect(captured.headers?.['x-goog-api-key']).toBe('test-key');
    expect(captured.headers?.['Api-Revision']).toBe('2026-05-20');
    expect(captured.headers?.['Content-Type']).toBe('application/json');
    // TRAP 1: never :generateContent, which bills the audio and returns nothing.
    expect(captured.url).not.toContain('generateContent');
  });

  test('sends the audio inline as base64 with a resolved MIME type', async () => {
    const captured = captureRequest();
    const bytes = new Uint8Array([1, 2, 3, 4]);
    await transcribeWithGeminiTranscribe(bytes.buffer, 'audio/wav');

    expect(captured.body?.model).toBe('gemini-3.5-transcribe');
    expect(captured.body?.input).toEqual([{
      type: 'audio',
      mime_type: 'audio/wav',
      data: Buffer.from(bytes).toString('base64'),
    }]);
  });

  test('omits generation_config entirely for an auto-detect request with no vocabulary', async () => {
    const captured = captureRequest();
    await transcribeWithGeminiTranscribe(audio(), 'audio/mp3', 'auto');
    expect(captured.body).not.toHaveProperty('generation_config');
  });

  test('forwards the vocabulary as custom_vocabulary — the real upstream field', async () => {
    const captured = captureRequest();
    await transcribeWithGeminiTranscribe(audio(), 'audio/mp3', 'en-US', 'HyperWhisper, Kalamazoo');

    expect(captured.body?.generation_config.transcription_config).toEqual({
      language_codes: ['en-US'],
      custom_vocabulary: ['HyperWhisper', 'Kalamazoo'],
    });
  });

  test('never sends diarization or timestamps — /transcribe is text-only', async () => {
    const captured = captureRequest();
    await transcribeWithGeminiTranscribe(audio(), 'audio/mp3', 'en-US', 'HyperWhisper');
    const serialized = JSON.stringify(captured.body);
    expect(serialized).not.toContain('diarization_mode');
    expect(serialized).not.toContain('timestamp_granularities');
  });

  test('honours an explicitly requested model', async () => {
    const captured = captureRequest();
    await transcribeWithGeminiTranscribe(audio(), 'audio/mp3', undefined, undefined, {
      model: 'gemini-3.5-transcribe',
    });
    expect(captured.body?.model).toBe('gemini-3.5-transcribe');
  });
});

describe('transcribeWithGeminiTranscribe — gates before the wire', () => {
  test('rejects the live model without spending a round trip', async () => {
    let called = false;
    globalThis.fetch = mock(async () => { called = true; return Response.json(sampleResponse()); }) as unknown as typeof fetch;

    const thrown = await transcribeWithGeminiTranscribe(audio(), 'audio/mp3', undefined, undefined, {
      model: 'gemini-3.5-transcribe-live',
    }).catch((e) => e);

    expect(thrown).toBeInstanceOf(ProviderInputError);
    expect(thrown.message).toContain('WebSocket-only');
    expect(called).toBe(false);
  });

  test('a missing API key throws before any upstream call', async () => {
    delete process.env.GEMINI_API_KEY;
    let called = false;
    globalThis.fetch = mock(async () => { called = true; return Response.json(sampleResponse()); }) as unknown as typeof fetch;

    const thrown = await transcribeWithGeminiTranscribe(audio(), 'audio/mp3').catch((e) => e);
    expect(thrown.message).toContain('GEMINI_API_KEY not configured');
    expect(called).toBe(false);
  });

  test('falls back to GOOGLE_GEMINI_API_KEY', async () => {
    delete process.env.GEMINI_API_KEY;
    process.env.GOOGLE_GEMINI_API_KEY = 'other-key';
    const captured = captureRequest();

    await transcribeWithGeminiTranscribe(audio(), 'audio/mp3');
    expect(captured.headers?.['x-goog-api-key']).toBe('other-key');
  });

  test('audio over the inline cap is a 413, not a base64 blow-up', async () => {
    let called = false;
    globalThis.fetch = mock(async () => { called = true; return Response.json(sampleResponse()); }) as unknown as typeof fetch;

    const thrown = await transcribeWithGeminiTranscribe(
      audio(GEMINI_TRANSCRIBE_INLINE_MAX_BYTES + 1), 'audio/mp3',
    ).catch((e) => e);

    expect(thrown).toBeInstanceOf(AudioTooLargeError);
    expect(thrown.maxBytes).toBe(GEMINI_TRANSCRIBE_INLINE_MAX_BYTES);
    expect(called).toBe(false);
  });
});

describe('transcribeWithGeminiTranscribe — response handling', () => {
  test('reads the transcript from steps[].content[].text and bills the reported tokens', async () => {
    captureRequest();
    const result = await transcribeWithGeminiTranscribe(audio(), 'audio/mp3', 'en-US');

    expect(result.text).toBe(SAMPLE_TRANSCRIPT);
    expect(result.source).toBe('gemini-transcribe');
    expect(result.requestId).toBe('interaction-abc123');
    // 236 audio tokens at 25 tok/s is the clip's real 9.44 s.
    expect(result.durationSeconds).toBeCloseTo(9.44, 2);
    // 237 input tokens @ $2/1M + ceil(119/4)=30 output tokens @ $12/1M.
    expect(result.costUsd).toBeCloseTo(237 * (2 / 1e6) + 30 * (12 / 1e6), 9);
  });

  test('joins multiple steps and content entries', () => {
    expect(collectTranscript({
      steps: [{ content: [{ text: 'one ' }, { text: 'two' }] }, { content: [{ text: ' three' }] }],
    })).toBe('one two three');
  });

  test('reads input tokens out of the modality LIST, not a map', () => {
    expect(readInputTokens(sampleResponse().usage)).toEqual({ audioTokens: 236, textTokens: 1 });
    expect(readInputTokens(undefined)).toEqual({ audioTokens: 0, textTokens: 0 });
    expect(readInputTokens({ input_tokens_by_modality: [{ modality: 'image', tokens: 5 }] }))
      .toEqual({ audioTokens: 0, textTokens: 0 });
  });

  test('an empty transcript is no_speech but still bills the audio Google charged us for', async () => {
    // NOT the free no_speech the duration-billed adapters return. This endpoint
    // bills 25 input tokens/sec whether or not a word comes back, so a $0 answer
    // would make silent audio an unmetered channel to a paid upstream request.
    captureRequest(sampleResponse('   '));
    const result = await transcribeWithGeminiTranscribe(audio(), 'audio/mp3');

    expect(result.source).toBe('no_speech');
    expect(result.text).toBe('');
    // The real clip length, not 0: 236 audio tokens at 25 tok/s.
    expect(result.durationSeconds).toBeCloseTo(9.44, 2);
    // 236 audio + 1 text input token at $2/1M, and no output tokens — there is
    // genuinely no transcript to bill for.
    expect(result.costUsd).toBeCloseTo(237 * (2 / 1e6), 9);
  });

  test('the no_speech log event derives the upstream duration from audio tokens, never the byte estimate', async () => {
    captureRequest(sampleResponse('   '));
    const reported = await captureNoSpeechEvent(() => transcribeWithGeminiTranscribe(audio(), 'audio/mp3'));
    expect(reported.upstreamDurationSeconds as number).toBeCloseTo(9.44, 2);

    // No usage at all: the BILLED `durationSeconds` falls back to a 10 s byte
    // estimate, which the telemetry must not present as Google's own number.
    captureRequest({ steps: [{ content: [{ text: '' }] }] });
    const estimated = await captureNoSpeechEvent(
      () => transcribeWithGeminiTranscribe(audio(320_000), 'audio/wav'),
    );
    expect(estimated.upstreamDurationSeconds).toBeNull();
    // The pre-existing billed field is untouched by this telemetry addition.
    expect(estimated.durationSeconds as number).toBeCloseTo(10, 3);
  });

  test('a silent clip with no usage object bills the duration estimate, not $0', async () => {
    captureRequest({ steps: [{ content: [{ text: '' }] }] });
    const result = await transcribeWithGeminiTranscribe(audio(320_000), 'audio/wav');

    expect(result.source).toBe('no_speech');
    // 10 s of wav: 250 audio tokens at $2/1M, plus the per-second output
    // estimate the fail-closed fallback carries.
    expect(result.costUsd).toBeCloseTo(10 * 25 * (2 / 1e6) + 10 * 3.125 * (12 / 1e6), 9);
    expect(result.durationSeconds).toBeCloseTo(10, 3);
  });

  test('a response with no usage object bills the duration estimate, not the output tokens alone', async () => {
    // The shape production actually emits when `usage` is missing or loses a
    // modality: the transcript is present, so the estimated OUTPUT tokens are
    // non-zero and the old `tokenCost > 0` guard skipped the fallback entirely.
    captureRequest({ steps: [{ content: [{ text: 'hello there' }] }] });
    // 32,000 B/s for wav → 10 s of audio.
    const result = await transcribeWithGeminiTranscribe(audio(320_000), 'audio/wav');

    expect(result.text).toBe('hello there');
    expect(result.durationSeconds).toBeCloseTo(10, 3);
    // 10 s at 25 audio tok/s × $2/1M + the 3.125 output tok/s estimate × $12/1M.
    expect(result.costUsd).toBeCloseTo(0.000875, 9);
    // What billing the estimated output tokens alone would have charged — 24x
    // under. ceil(11 chars / 4) = 3 tokens at $12/1M.
    expect(result.costUsd).toBeGreaterThan(3 * (12 / 1e6) * 20);
  });

  test('a usage object that reports text tokens but no audio modality also fails closed', async () => {
    captureRequest({
      steps: [{ content: [{ text: 'hello there' }] }],
      usage: {
        total_input_tokens: 1,
        input_tokens_by_modality: [{ modality: 'text', tokens: 1 }],
        total_output_tokens: 0,
      },
    });
    const result = await transcribeWithGeminiTranscribe(audio(320_000), 'audio/wav');

    // The audio-token count is the one field that cannot be reconstructed, so
    // its absence — not a zero TOTAL — is what arms the duration fallback.
    expect(result.costUsd).toBeCloseTo(0.000875, 9);
    expect(result.durationSeconds).toBeCloseTo(10, 3);
  });

  test('a malformed 200 body fails over rather than 500ing', async () => {
    globalThis.fetch = mock(async () => new Response('<html>nope', { status: 200 })) as unknown as typeof fetch;
    const thrown = await transcribeWithGeminiTranscribe(audio(), 'audio/mp3').catch((e) => e);
    expect(thrown).toBeInstanceOf(ProviderUnavailableError);
  });
});

describe('transcribeWithGeminiTranscribe — error classification', () => {
  test('a 400 "API key not valid" is an auth failure, not an input rejection', async () => {
    // Verified live: Google answers a bad key on this endpoint with 400
    // INVALID_ARGUMENT / API_KEY_INVALID, never 401.
    errorResponse(400, JSON.stringify({
      error: {
        code: 400,
        message: 'API key not valid. Please pass a valid API key.',
        status: 'INVALID_ARGUMENT',
        details: [{ reason: 'API_KEY_INVALID' }],
      },
    }));

    const thrown = await transcribeWithGeminiTranscribe(audio(), 'audio/mp3').catch((e) => e);
    expect(thrown).not.toBeInstanceOf(ProviderInputError);
    expect(thrown.message).toContain('invalid or unauthorized');
  });

  test('any other 400 stays an input rejection', async () => {
    errorResponse(400, JSON.stringify({
      error: { message: 'custom_vocabulary is incompatible with timestamps.', code: 'invalid_request' },
    }));

    const thrown = await transcribeWithGeminiTranscribe(audio(), 'audio/mp3').catch((e) => e);
    expect(thrown).toBeInstanceOf(ProviderInputError);
    expect(thrown.status).toBe(400);
  });

  test('401/403 are auth failures; 429 and 5xx fail over', async () => {
    for (const status of [401, 403]) {
      errorResponse(status);
      const thrown = await transcribeWithGeminiTranscribe(audio(), 'audio/mp3').catch((e) => e);
      expect(thrown).not.toBeInstanceOf(ProviderUnavailableError);
      expect(thrown.message).toContain('invalid or unauthorized');
    }

    for (const status of [429, 500, 503]) {
      errorResponse(status);
      const thrown = await transcribeWithGeminiTranscribe(audio(), 'audio/mp3').catch((e) => e);
      expect(thrown).toBeInstanceOf(ProviderUnavailableError);
      expect(thrown.status).toBe(status);
    }
  });

  test('a 404 for an unknown model is an input rejection the caller can fix', async () => {
    errorResponse(404, JSON.stringify({ error: { message: "Model 'x' not found.", code: 'not_found' } }));
    const thrown = await transcribeWithGeminiTranscribe(audio(), 'audio/mp3').catch((e) => e);
    expect(thrown).toBeInstanceOf(ProviderInputError);
    expect(thrown.status).toBe(404);
  });
});
