import { afterEach, describe, expect, mock, test } from 'bun:test';
import {
  DEFAULT_AUDIO_EXTENSIONS,
  audioExtensionFromContentType,
  computeUploadTimeoutMs,
  estimateSecondsFromBytes,
  fetchWithTimeout,
  readErrorBodyPreview,
} from './utils';
import { ProviderUnavailableError } from './types';

describe('computeUploadTimeoutMs (size-scaled audio-upload budget)', () => {
  test('small payloads get the 30s floor', () => {
    expect(computeUploadTimeoutMs(0)).toBe(30_000);
    expect(computeUploadTimeoutMs(1_000_000)).toBe(30_000); // 1 MB → 10s scaled, floored to 30s
  });

  test('large payloads scale at 1s per 100 KB', () => {
    // 100 MB → ceil(100e6 / 100_000) = 1000 × 1000ms = 1000s.
    expect(computeUploadTimeoutMs(100_000_000)).toBe(1_000_000);
    // 300 MB (Azure MAI cap) → 3000s, comfortably above the 15s default that
    // would otherwise abort a large multipart upload.
    expect(computeUploadTimeoutMs(300_000_000)).toBe(3_000_000);
  });

  test('budget is monotonic in payload size', () => {
    expect(computeUploadTimeoutMs(50_000_000)).toBeGreaterThan(computeUploadTimeoutMs(5_000_000));
  });
});

describe('estimateSecondsFromBytes (64kbps encoded-audio duration heuristic)', () => {
  test('one BYTES_PER_MINUTE_ESTIMATE chunk (480,000 bytes) is 60 seconds', () => {
    expect(estimateSecondsFromBytes(480_000)).toBe(60);
  });

  test('scales linearly with byte length', () => {
    expect(estimateSecondsFromBytes(240_000)).toBe(30);
    expect(estimateSecondsFromBytes(0)).toBe(0);
  });
});

describe('audioExtensionFromContentType (multipart filename extension)', () => {
  test('maps each recognised container to its extension', () => {
    const cases: Array<[string, (typeof DEFAULT_AUDIO_EXTENSIONS)[number]]> = [
      ['audio/wav', 'wav'],
      ['audio/x-wav', 'wav'],
      ['audio/mp3', 'mp3'],
      ['audio/mpeg', 'mp3'],
      ['audio/m4a', 'm4a'],
      ['audio/mp4', 'm4a'],
      ['audio/webm', 'webm'],
      ['audio/ogg', 'ogg'],
      ['audio/flac', 'flac'],
    ];

    for (const [contentType, expected] of cases) {
      expect(audioExtensionFromContentType(contentType, DEFAULT_AUDIO_EXTENSIONS)).toBe(expected);
    }
  });

  test('returns undefined for an unrecognised container so the caller applies its own fallback', () => {
    expect(audioExtensionFromContentType('application/octet-stream', DEFAULT_AUDIO_EXTENSIONS)).toBeUndefined();
    expect(audioExtensionFromContentType('', DEFAULT_AUDIO_EXTENSIONS)).toBeUndefined();
    // aac is not in the default set — only Voxtral names it.
    expect(audioExtensionFromContentType('audio/aac', DEFAULT_AUDIO_EXTENSIONS)).toBeUndefined();
  });

  test('only resolves extensions the caller listed as accepted', () => {
    // Azure MAI takes wav/mp3/flac only; everything else must miss so the
    // adapter can raise UnsupportedAudioFormatError instead of uploading.
    const azure = ['wav', 'mp3', 'flac'] as const;
    expect(audioExtensionFromContentType('audio/flac', azure)).toBe('flac');
    expect(audioExtensionFromContentType('audio/m4a', azure)).toBeUndefined();
    expect(audioExtensionFromContentType('audio/webm', azure)).toBeUndefined();

    const withAac = [...DEFAULT_AUDIO_EXTENSIONS, 'aac'] as const;
    expect(audioExtensionFromContentType('audio/aac', withAac)).toBe('aac');
  });

  test('match order is fixed by the hint table, not by the accepted list', () => {
    // A content type naming two containers resolves to the earlier hint —
    // mp4 (m4a) beats the aac codec parameter, whichever order `accepted` is in.
    const withAac = [...DEFAULT_AUDIO_EXTENSIONS, 'aac'] as const;
    const reordered = ['aac', 'flac', 'ogg', 'webm', 'm4a', 'mp3', 'wav'] as const;
    expect(audioExtensionFromContentType('audio/mp4; codecs=aac', withAac)).toBe('m4a');
    expect(audioExtensionFromContentType('audio/mp4; codecs=aac', reordered)).toBe('m4a');
  });

  test('matching is case-sensitive — callers fold case themselves', () => {
    expect(audioExtensionFromContentType('AUDIO/FLAC', DEFAULT_AUDIO_EXTENSIONS)).toBeUndefined();
    expect(audioExtensionFromContentType('AUDIO/FLAC'.toLowerCase(), DEFAULT_AUDIO_EXTENSIONS)).toBe('flac');
  });
});

const originalFetch = globalThis.fetch;
const originalTimeoutEnv = process.env.STT_PROVIDER_TIMEOUT_MS;

afterEach(() => {
  globalThis.fetch = originalFetch;
  if (originalTimeoutEnv === undefined) delete process.env.STT_PROVIDER_TIMEOUT_MS;
  else process.env.STT_PROVIDER_TIMEOUT_MS = originalTimeoutEnv;
});

function fetchThatRejectsOnAbort(rejection: () => Error) {
  return mock((_url: string, init?: RequestInit) => new Promise((_resolve, reject) => {
    init?.signal?.addEventListener('abort', () => reject(rejection()));
  })) as unknown as typeof fetch;
}

describe('fetchWithTimeout', () => {
  test('resolves with the response and passes an AbortSignal through init', async () => {
    globalThis.fetch = mock(async (_url: string, init?: RequestInit) => {
      expect(init?.signal).toBeInstanceOf(AbortSignal);
      return new Response('ok', { status: 200 });
    }) as unknown as typeof fetch;

    const response = await fetchWithTimeout('test-provider', 'https://example.com', {}, {}, 5_000);
    expect(response.status).toBe(200);
  });

  test('aborts once timeoutMsOverride elapses and throws a "timeout" ProviderUnavailableError', async () => {
    globalThis.fetch = fetchThatRejectsOnAbort(() => new DOMException('The operation was aborted', 'AbortError'));

    try {
      await fetchWithTimeout('test-provider', 'https://example.com', {}, {}, 15);
      throw new Error('expected fetchWithTimeout to throw');
    } catch (error) {
      expect(error).toBeInstanceOf(ProviderUnavailableError);
      const providerError = error as ProviderUnavailableError;
      expect(providerError.kind).toBe('timeout');
      expect(providerError.message).toContain('timeout after 15ms');
      expect(providerError.elapsedMs).toBeGreaterThanOrEqual(0);
    }
  });

  test('classifies a non-abort fetch rejection as a "network_error" ProviderUnavailableError', async () => {
    globalThis.fetch = mock(async () => { throw new Error('ECONNRESET'); }) as unknown as typeof fetch;

    try {
      await fetchWithTimeout('test-provider', 'https://example.com', {}, {}, 5_000);
      throw new Error('expected fetchWithTimeout to throw');
    } catch (error) {
      expect(error).toBeInstanceOf(ProviderUnavailableError);
      const providerError = error as ProviderUnavailableError;
      expect(providerError.kind).toBe('network_error');
      expect(providerError.message).toContain('ECONNRESET');
    }
  });

  test('honours STT_PROVIDER_TIMEOUT_MS when no explicit override is given', async () => {
    process.env.STT_PROVIDER_TIMEOUT_MS = '20';
    globalThis.fetch = fetchThatRejectsOnAbort(() => new DOMException('Aborted', 'AbortError'));

    await expect(
      fetchWithTimeout('test-provider', 'https://example.com', {})
    ).rejects.toThrow(/timeout after 20ms/);
  });

  test('falls back to the default budget when STT_PROVIDER_TIMEOUT_MS is not a valid positive number', async () => {
    process.env.STT_PROVIDER_TIMEOUT_MS = 'not-a-number';
    // Resolves after 30ms — proves the override wasn't parsed as something
    // tiny (e.g. NaN, which setTimeout treats as 0) that would abort first.
    globalThis.fetch = mock((_url: string, init?: RequestInit) => new Promise((resolve, reject) => {
      const timer = setTimeout(() => resolve(new Response('ok', { status: 200 })), 30);
      init?.signal?.addEventListener('abort', () => {
        clearTimeout(timer);
        reject(new DOMException('Aborted', 'AbortError'));
      });
    })) as unknown as typeof fetch;

    const response = await fetchWithTimeout('test-provider', 'https://example.com', {});
    expect(response.status).toBe(200);
  });
});

describe('readErrorBodyPreview', () => {
  test('returns the full body when at or under the 500-char preview limit', async () => {
    const body = 'x'.repeat(500);
    expect(await readErrorBodyPreview(new Response(body))).toBe(body);
  });

  test('truncates bodies over 500 chars and appends an ellipsis', async () => {
    const body = 'y'.repeat(600);
    const preview = await readErrorBodyPreview(new Response(body));
    expect(preview).toBe(`${'y'.repeat(500)}...`);
  });

  test('returns a placeholder when the body cannot be read', async () => {
    const unreadable = { text: () => { throw new Error('stream already used'); } } as unknown as Response;
    expect(await readErrorBodyPreview(unreadable)).toBe('<unreadable>');
  });
});
