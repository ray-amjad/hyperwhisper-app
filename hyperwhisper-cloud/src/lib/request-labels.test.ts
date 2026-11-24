import { describe, expect, test } from 'bun:test';
import {
  MAX_LANGUAGE_BYTES,
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
  // Every value prod saw from a client in the 30 days before this change, the
  // longer tag shapes the provider maps handle, and free text a user can type
  // into a mode's language box (main accepts it and falls back to auto-detect).
  test.each([
    undefined, '', 'auto', 'AUTO', 'en', 'de', 'ja-JP', 'hi-IN', 'en-US', 'en-us', 'zh-TW',
    'yue', 'haw', 'zh-Hant', 'zh-Hant-TW', 'sr-Latn-RS', 'pt_BR', 'es-419',
    'en US', '中文', 'English (US)', 'e', 'Español', '1en', '{"a":1}',
  ])('accepts %p', (language) => {
    expect(checkLanguageParam(language)).toEqual({ ok: true });
  });

  test('accepts a value of exactly the maximum bytes', () => {
    expect(checkLanguageParam('a'.repeat(MAX_LANGUAGE_BYTES))).toEqual({ ok: true });
    // 11 three-byte characters plus two ASCII: 35 bytes.
    expect(checkLanguageParam(`${'語'.repeat(11)}ab`)).toEqual({ ok: true });
  });

  test.each([
    ['a 36-byte value', 'a'.repeat(MAX_LANGUAGE_BYTES + 1)],
    ['a 17 KB value', 'e'.repeat(17_000)],
    ['a multi-byte value over 35 bytes (12 characters, 36 bytes)', '語'.repeat(12)],
    ['a 4-byte-character value over 35 bytes', '😀'.repeat(9)],
    ['a C0 control character', 'en\u0001'],
    ['a NUL', '\u0000'],
    ['a newline', 'en\nUS'],
    ['a tab', 'en\tUS'],
    ['DEL', 'en\u007F'],
    ['a C1 control character', 'en\u0085'],
    ['the last C1 control character', '\u009F'],
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
