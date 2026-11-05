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

  test('a degenerate "-en" tag (empty subtag) falls back to language_detection, never language_code ""', () => {
    const { createBody } = buildTranscriptJob(URL_, '-en', undefined, false, 'universal-3-5-pro');
    expect(createBody.language_detection).toBe(true);
    expect('language_code' in createBody).toBe(false);
  });

  for (const language of [' en-US ', ' en-US', '\ten\n']) {
    test(`${JSON.stringify(language)} is trimmed like sync → language_code en`, () => {
      const { createBody } = buildTranscriptJob(URL_, language, undefined, false, 'universal-3-5-pro');
      expect(createBody.language_code).toBe('en');
      expect('language_detection' in createBody).toBe(false);
    });
  }

  test('" auto " (padded) is auto-detect, not language_code "auto"', () => {
    const { createBody } = buildTranscriptJob(URL_, ' auto ', undefined, false, 'universal-3-5-pro');
    expect(createBody.language_detection).toBe(true);
    expect('language_code' in createBody).toBe(false);
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

  test('a term over 50 characters is dropped; duplicates are kept', () => {
    const long = 'x'.repeat(51);
    const fifty = 'y'.repeat(50);
    const { keyterms } = buildTranscriptJob(
      URL_, 'en', `${long}, ${fifty}, Dup, Dup`, false, 'universal-3-5-pro',
    );
    expect(keyterms).toEqual([fifty, 'Dup', 'Dup']);
  });

  test('a phrase over 6 words is dropped, like sync; 6 words is kept', () => {
    const seven = 'one two three four five six seven';
    const six = 'one two three four five six';
    const { createBody, keyterms } = buildTranscriptJob(
      URL_, 'en', `${seven}, ${six}, Short`, false, 'universal-3-5-pro',
    );
    expect(keyterms).toEqual([six, 'Short']);
    expect(createBody.keyterms_prompt).toEqual([six, 'Short']);
  });

  test('only over-long phrases: no keyterms_prompt and empty keyterms (no keyterms add-on)', () => {
    const { createBody, keyterms } = buildTranscriptJob(
      URL_, 'en', 'one two three four five six seven, a b c d e f g h', false, 'universal-3-5-pro',
    );
    expect('keyterms_prompt' in createBody).toBe(false);
    expect(keyterms).toEqual([]);
  });

  test('over-long phrases do not use up the 200-term cap', () => {
    const longs = Array.from({ length: 10 }, (_, i) => `a b c d e f g${i}`);
    const shorts = Array.from({ length: 200 }, (_, i) => `term${i}`);
    const { keyterms } = buildTranscriptJob(
      URL_, 'en', [...longs, ...shorts].join(', '), false, 'universal-3-5-pro',
    );
    expect(keyterms.length).toBe(200);
    expect(keyterms[0]).toBe('term0');
    expect(keyterms[199]).toBe('term199');
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
