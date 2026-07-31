import { describe, expect, test } from 'bun:test';
import { rawQuery } from './query';

describe('rawQuery', () => {
  test('preserves literal + in a percent-encoded value', () => {
    expect(rawQuery('http://x/transcribe?initial_prompt=C%2B%2B', 'initial_prompt')).toBe('C++');
  });

  test('preserves a raw + (no HTML-form plus-to-space step)', () => {
    expect(rawQuery('http://x/transcribe?initial_prompt=C++', 'initial_prompt')).toBe('C++');
  });

  test('decodes %20 as a space', () => {
    expect(rawQuery('http://x/t?initial_prompt=hello%20world', 'initial_prompt')).toBe('hello world');
  });

  test('returns undefined for an absent parameter', () => {
    expect(rawQuery('http://x/t?language=en', 'initial_prompt')).toBeUndefined();
  });

  test('returns undefined for an empty value (|| undefined semantics)', () => {
    expect(rawQuery('http://x/t?initial_prompt=&language=en', 'initial_prompt')).toBeUndefined();
    expect(rawQuery('http://x/t?initial_prompt', 'initial_prompt')).toBeUndefined();
  });

  test('returns undefined when there is no query string', () => {
    expect(rawQuery('http://x/t', 'language')).toBeUndefined();
  });

  test('returns the first occurrence of a repeated parameter', () => {
    expect(rawQuery('http://x/t?mode=a&mode=b', 'mode')).toBe('a');
  });

  test('falls back to the raw value on a malformed percent-escape', () => {
    expect(rawQuery('http://x/t?initial_prompt=50%&language=en', 'initial_prompt')).toBe('50%');
  });

  test('ignores a fragment', () => {
    expect(rawQuery('http://x/t?language=en#frag', 'language')).toBe('en');
  });

  test('does not match a prefix of a longer parameter name', () => {
    expect(rawQuery('http://x/t?languages=xx&language=en', 'language')).toBe('en');
  });
});
