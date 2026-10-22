import { describe, expect, test } from 'bun:test';
import { buildTranscriptJob } from './assemblyai';

// Direct cases for the async create-job body. `assemblyai.test.ts` reaches the
// same rules only through a mocked `fetch`; these pin the exact object, and its
// key order, because the order is what `JSON.stringify` puts on the wire.

const URL_ = 'https://cdn.assemblyai.com/upload/abc';

describe('buildTranscriptJob — speech_models fallback chain', () => {
  test('universal-3-5-pro falls back to universal-2', () => {
    const { createBody } = buildTranscriptJob(URL_, 'en', undefined, false, 'universal-3-5-pro');
    expect(createBody.speech_models).toEqual(['universal-3-5-pro', 'universal-2']);
  });

  test('universal-2 is sent alone, with no fallback', () => {
    const { createBody } = buildTranscriptJob(URL_, 'en', undefined, false, 'universal-2');
    expect(createBody.speech_models).toEqual(['universal-2']);
  });

  test('any other model id is sent alone, as given', () => {
    const { createBody } = buildTranscriptJob(URL_, 'en', undefined, false, 'some-future-model');
    expect(createBody.speech_models).toEqual(['some-future-model']);
  });

  test('a legacy id is NOT canonicalized here (the caller does that first)', () => {
    const { createBody } = buildTranscriptJob(URL_, 'en', undefined, false, 'universal-3-pro');
    expect(createBody.speech_models).toEqual(['universal-3-pro']);
  });
});

describe('buildTranscriptJob — language_code primary-subtag rule', () => {
  const cases: Array<[string, string]> = [
    ['en', 'en'],
    ['en-US', 'en'],
    ['pt-BR', 'pt'],
    ['pt_BR', 'pt'],
    ['ZH-Hant-TW', 'zh'],
    ['ES', 'es'],
  ];
  for (const [language, code] of cases) {
    test(`${language} → language_code ${code}, no language_detection`, () => {
      const { createBody } = buildTranscriptJob(URL_, language, undefined, false, 'universal-3-5-pro');
      expect(createBody.language_code).toBe(code);
      expect('language_detection' in createBody).toBe(false);
    });
  }

  for (const language of [undefined, '', 'auto', 'AUTO']) {
    test(`${JSON.stringify(language)} → language_detection: true, no language_code`, () => {
      const { createBody } = buildTranscriptJob(URL_, language, undefined, false, 'universal-3-5-pro');
      expect(createBody.language_detection).toBe(true);
      expect('language_code' in createBody).toBe(false);
    });
  }

  test('a degenerate "-en" tag reduces to an empty language_code, not detection', () => {
    const { createBody } = buildTranscriptJob(URL_, '-en', undefined, false, 'universal-3-5-pro');
    expect(createBody.language_code).toBe('');
    expect('language_detection' in createBody).toBe(false);
  });

  test('the language is not trimmed (only the sync path trims)', () => {
    const { createBody } = buildTranscriptJob(URL_, ' en-US', undefined, false, 'universal-3-5-pro');
    expect(createBody.language_code).toBe(' en');
  });
});

describe('buildTranscriptJob — keyterms_prompt', () => {
  test('no initial prompt: no keyterms_prompt key, empty keyterms', () => {
    const { createBody, keyterms } = buildTranscriptJob(URL_, 'en', undefined, false, 'universal-3-5-pro');
    expect('keyterms_prompt' in createBody).toBe(false);
    expect(keyterms).toEqual([]);
  });

  test('a prompt of only separators: no keyterms_prompt key', () => {
    const { createBody, keyterms } = buildTranscriptJob(URL_, 'en', ' ,\n; - ', false, 'universal-3-5-pro');
    expect('keyterms_prompt' in createBody).toBe(false);
    expect(keyterms).toEqual([]);
  });

  test('separators split, bullets strip, and the returned keyterms are the body array', () => {
    const { createBody, keyterms } = buildTranscriptJob(
      URL_, 'en-US', 'HyperWhisper, SwiftUI\n- Fly.io', false, 'universal-3-5-pro',
    );
    expect(keyterms).toEqual(['HyperWhisper', 'SwiftUI', 'Fly.io']);
    expect(createBody.keyterms_prompt).toBe(keyterms);
  });

  test('a term over 50 characters is dropped; duplicates and long phrases are kept', () => {
    const long = 'x'.repeat(51);
    const fifty = 'y'.repeat(50);
    const phrase = 'one two three four five six seven';
    const { keyterms } = buildTranscriptJob(
      URL_, 'en', `${long}, ${fifty}, Dup, Dup, ${phrase}`, false, 'universal-3-5-pro',
    );
    expect(keyterms).toEqual([fifty, 'Dup', 'Dup', phrase]);
  });

  test('keyterms are capped at 200 terms', () => {
    const prompt = Array.from({ length: 250 }, (_, i) => `term${i}`).join(', ');
    const { keyterms } = buildTranscriptJob(URL_, 'en', prompt, false, 'universal-3-5-pro');
    expect(keyterms.length).toBe(200);
    expect(keyterms[199]).toBe('term199');
  });
});

describe('buildTranscriptJob — exact body', () => {
  test('default model, auto language, no prompt, not medical', () => {
    const { createBody } = buildTranscriptJob(URL_, undefined, undefined, false, 'universal-3-5-pro');
    expect(JSON.stringify(createBody)).toBe(JSON.stringify({
      audio_url: URL_,
      speech_models: ['universal-3-5-pro', 'universal-2'],
      language_detection: true,
    }));
  });

  test('every field set, in wire order: audio_url, speech_models, language, keyterms, domain', () => {
    const { createBody } = buildTranscriptJob(URL_, 'de-DE', 'Kardiologie; EKG', true, 'universal-2');
    expect(JSON.stringify(createBody)).toBe(JSON.stringify({
      audio_url: URL_,
      speech_models: ['universal-2'],
      language_code: 'de',
      keyterms_prompt: ['Kardiologie', 'EKG'],
      domain: 'medical-v1',
    }));
  });

  test('medical with auto language puts domain after language_detection', () => {
    const { createBody } = buildTranscriptJob(URL_, 'auto', undefined, true, 'universal-3-5-pro');
    expect(Object.keys(createBody)).toEqual(['audio_url', 'speech_models', 'language_detection', 'domain']);
    expect(createBody.domain).toBe('medical-v1');
  });

  test('not medical: no domain key at all', () => {
    const { createBody } = buildTranscriptJob(URL_, 'en', 'a', false, 'universal-3-5-pro');
    expect('domain' in createBody).toBe(false);
  });
});
