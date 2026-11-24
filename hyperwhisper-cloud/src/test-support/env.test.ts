import { afterEach, describe, expect, test } from 'bun:test';
import { saveEnv, stubEnv } from './env';

const NAME = 'HW_TEST_SUPPORT_ENV_PROBE';

afterEach(() => {
  delete process.env[NAME];
});

describe('stubEnv', () => {
  test('sets a var that was unset, and the restore deletes the key again', () => {
    const restore = stubEnv(NAME, 'stubbed');
    expect(process.env[NAME]).toBe('stubbed');
    restore();
    expect(NAME in process.env).toBe(false);
  });

  test('clears a var that was set, and the restore writes the old value back', () => {
    process.env[NAME] = 'original';
    const restore = stubEnv(NAME, undefined);
    expect(NAME in process.env).toBe(false);
    restore();
    expect(process.env[NAME]).toBe('original');
  });

  test('keeps an empty string as a value, not as unset', () => {
    process.env[NAME] = '';
    const restore = stubEnv(NAME, 'stubbed');
    restore();
    expect(process.env[NAME]).toBe('');
  });
});

describe('saveEnv', () => {
  test('leaves the value alone, and its restore can run after every test', () => {
    process.env[NAME] = 'original';
    const restore = saveEnv(NAME);
    expect(process.env[NAME]).toBe('original');
    process.env[NAME] = 'written by a test';
    restore();
    expect(process.env[NAME]).toBe('original');
    process.env[NAME] = 'written by the next test';
    restore();
    expect(process.env[NAME]).toBe('original');
  });
});
