import { describe, expect, test } from 'bun:test';
import { parseClientInfo, UNKNOWN_CLIENT } from './client-info';

describe('parseClientInfo', () => {
  test('reads the explicit headers', () => {
    expect(parseClientInfo('macos', '2.41.0', 'HyperWhisper/2.41.0')).toEqual({
      clientPlatform: 'macos',
      clientVersion: '2.41.0',
    });
  });

  test('lowercases the platform so macOS and macos are one bucket', () => {
    expect(parseClientInfo('macOS', '2.41.0', undefined).clientPlatform).toBe('macos');
  });

  test('falls back to the macOS User-Agent of shipped builds', () => {
    expect(parseClientInfo(undefined, undefined, 'HyperWhisper/2.41.0')).toEqual({
      clientPlatform: 'macos',
      clientVersion: '2.41.0',
    });
  });

  test('falls back to the Windows User-Agent of shipped builds', () => {
    expect(parseClientInfo(undefined, undefined, 'HyperWhisper-Windows/1.8.2')).toEqual({
      clientPlatform: 'windows',
      clientVersion: '1.8.2',
    });
  });

  test('fills a missing half from the User-Agent', () => {
    expect(parseClientInfo('windows', undefined, 'HyperWhisper-Windows/1.8.2')).toEqual({
      clientPlatform: 'windows',
      clientVersion: '1.8.2',
    });
  });

  test('keeps the platform when the client could not read its own version', () => {
    expect(parseClientInfo(undefined, undefined, 'HyperWhisper-Windows/unknown')).toEqual({
      clientPlatform: 'windows',
      clientVersion: 'unknown',
    });
  });

  test('reports unknown for a caller that is not the app', () => {
    expect(parseClientInfo(undefined, undefined, 'curl/8.4.0')).toEqual({
      clientPlatform: UNKNOWN_CLIENT,
      clientVersion: UNKNOWN_CLIENT,
    });
    expect(parseClientInfo(undefined, undefined, undefined)).toEqual({
      clientPlatform: UNKNOWN_CLIENT,
      clientVersion: UNKNOWN_CLIENT,
    });
  });

  test('rejects a value that would corrupt the JSON log line', () => {
    expect(parseClientInfo('mac os\n{"event":"fake"}', '2.41.0', undefined)).toEqual({
      clientPlatform: UNKNOWN_CLIENT,
      clientVersion: '2.41.0',
    });
  });

  test('rejects an over-long value', () => {
    expect(parseClientInfo('m'.repeat(33), 'v'.repeat(33), undefined)).toEqual({
      clientPlatform: UNKNOWN_CLIENT,
      clientVersion: UNKNOWN_CLIENT,
    });
  });
});
