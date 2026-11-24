import { describe, expect, test } from 'bun:test';
import {
  MAX_LANGUAGE_TAG_LENGTH,
  checkLanguageParam,
  checkModeParam,
  checkRequestLabels,
} from './request-labels';

describe('checkModeParam', () => {
  test.each([
    ['absent', undefined],
    ['empty', ''],
    // macOS `uuidString` is upper case; the built-in modes use fixed ids.
    ['macOS built-in mode id', '00000000-0000-0000-0000-000000000001'],
    ['upper-case UUID', 'C2F1A0B4-5D6E-4F70-8A9B-0C1D2E3F4A5B'],
    ['lower-case UUID', 'c2f1a0b4-5d6e-4f70-8a9b-0c1d2e3f4a5b'],
  ])('accepts %s', (_label, mode) => {
    expect(checkModeParam(mode)).toEqual({ ok: true });
  });

  test.each([
    ['a 17 KB value', 'a'.repeat(17_000)],
    ['a UUID with trailing text', 'c2f1a0b4-5d6e-4f70-8a9b-0c1d2e3f4a5bX'],
    ['a UUID without dashes', 'c2f1a0b45d6e4f708a9b0c1d2e3f4a5b'],
    ['a mode name', 'Email'],
    ['non-hex characters', 'g2f1a0b4-5d6e-4f70-8a9b-0c1d2e3f4a5b'],
  ])('rejects %s', (_label, mode) => {
    const check = checkModeParam(mode);
    expect(check.ok).toBe(false);
    if (!check.ok) expect(check.field).toBe('mode');
  });
});

describe('checkLanguageParam', () => {
  // Every value prod saw from a client in the 30 days before this change, plus
  // the longer tag shapes the provider maps handle.
  test.each([
    undefined, '', 'auto', 'AUTO', 'en', 'de', 'ja-JP', 'hi-IN', 'en-US', 'en-us', 'zh-TW',
    'yue', 'haw', 'zh-Hant', 'zh-Hant-TW', 'sr-Latn-RS', 'pt_BR', 'es-419',
  ])('accepts %p', (language) => {
    expect(checkLanguageParam(language)).toEqual({ ok: true });
  });

  test('accepts a well-formed tag of exactly the maximum length', () => {
    const tag = 'abcdefgh-abcdefgh-abcdefgh-abcdefgh';
    expect(tag.length).toBe(MAX_LANGUAGE_TAG_LENGTH);
    expect(checkLanguageParam(tag)).toEqual({ ok: true });
  });

  test.each([
    ['a 17 KB value', 'e'.repeat(17_000)],
    ['a long run of letters', 'english'.repeat(6)],
    ['too many subtags', 'en-US-x-a-b-c'],
    ['spaces', 'en US'],
    ['a multi-byte value', '日本語'],
    ['a leading digit', '1en'],
    ['a single letter', 'e'],
    ['JSON-ish text', '{"a":1}'],
  ])('rejects %s', (_label, language) => {
    const check = checkLanguageParam(language);
    expect(check.ok).toBe(false);
    if (!check.ok) expect(check.field).toBe('language');
  });
});

describe('checkRequestLabels', () => {
  test('names the mode first when both are bad', () => {
    const check = checkRequestLabels({ mode: 'x'.repeat(100), language: 'y'.repeat(100) });
    expect(check.ok).toBe(false);
    if (!check.ok) expect(check.field).toBe('mode');
  });

  test('passes a real macOS request', () => {
    expect(checkRequestLabels({ mode: '00000000-0000-0000-0000-000000000001', language: 'en' })).toEqual({ ok: true });
  });
});
