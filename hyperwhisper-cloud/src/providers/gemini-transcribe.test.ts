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

  test('an empty transcript is no_speech and bills nothing', async () => {
    captureRequest(sampleResponse('   '));
    const result = await transcribeWithGeminiTranscribe(audio(), 'audio/mp3');

    expect(result.source).toBe('no_speech');
    expect(result.text).toBe('');
    expect(result.costUsd).toBe(0);
    expect(result.durationSeconds).toBe(0);
  });

  test('a response with no usage object still bills (fail-closed) from a size estimate', async () => {
    captureRequest({ steps: [{ content: [{ text: 'hello there' }] }] });
    // 32,000 B/s for wav → 10 s of audio.
    const result = await transcribeWithGeminiTranscribe(audio(320_000), 'audio/wav');

    expect(result.text).toBe('hello there');
    expect(result.costUsd).toBeGreaterThan(0);
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
