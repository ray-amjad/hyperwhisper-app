import { afterEach, describe, expect, mock, test } from 'bun:test';
import {
  DEFAULT_AUDIO_EXTENSIONS,
  audioExtensionFromContentType,
  computeUploadTimeoutMs,
  estimateAudioSeconds,
  estimateSecondsFromBytes,
  explicitLanguageSubtag,
  fetchWithTimeout,
  isExplicitLanguage,
  providerHttpError,
  readErrorBodyPreview,
  readRequiredJsonString,
  splitVocabularyTerms,
  upstreamDurationOrNull,
} from './utils';
import { ProviderInputError, ProviderUnavailableError } from './types';

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

describe('estimateAudioSeconds (content-type aware duration estimate)', () => {
  test('16 kHz/16-bit mono WAV — what both desktop apps upload — is 32,000 B/s', () => {
    expect(estimateAudioSeconds(96_000, 'audio/wav')).toBe(3);
    expect(estimateAudioSeconds(320_000, 'audio/x-wav')).toBe(10);
    expect(estimateAudioSeconds(96_000, 'audio/pcm; rate=16000')).toBe(3);
  });

  test('a 3-second WAV dictation is not reported as a 12-second clip', () => {
    // The flat 64 kbps billing heuristic (BYTES_PER_MINUTE_ESTIMATE) is 4x out
    // on raw PCM, which used to file failed short clips in the 'medium' bucket.
    expect(estimateAudioSeconds(96_000, 'audio/wav')).toBeLessThan(10);
    expect(estimateSecondsFromBytes(96_000)).toBeGreaterThan(10);
  });

  test('picks a representative rate per compressed container', () => {
    expect(estimateAudioSeconds(80_000, 'audio/webm; codecs=opus')).toBe(10);
    expect(estimateAudioSeconds(320_000, 'audio/flac')).toBe(10);
    expect(estimateAudioSeconds(160_000, 'audio/mpeg')).toBe(10);
    expect(estimateAudioSeconds(160_000, 'audio/mp4')).toBe(10);
  });

  test('falls back to 128 kbps for an unknown or missing content type', () => {
    expect(estimateAudioSeconds(160_000, 'application/octet-stream')).toBe(10);
    expect(estimateAudioSeconds(160_000, '')).toBe(10);
  });

  test('matches container hints in a stable order and folds case', () => {
    // 'audio/mp4; codecs=opus' carries two hints; opus wins, as it did in the
    // if/else chain this table replaced.
    expect(estimateAudioSeconds(80_000, 'audio/mp4; codecs=opus')).toBe(10);
    expect(estimateAudioSeconds(96_000, 'AUDIO/WAV')).toBe(3);
  });
});

describe('isExplicitLanguage / explicitLanguageSubtag (auto vs. a named language)', () => {
  test('undefined, empty and "auto" (any case) all mean auto-detect', () => {
    for (const value of [undefined, '', 'auto', 'AUTO', 'Auto']) {
      expect(isExplicitLanguage(value)).toBe(false);
      expect(explicitLanguageSubtag(value)).toBeUndefined();
    }
  });

  test('a named language reduces to its lower-case primary subtag', () => {
    expect(explicitLanguageSubtag('en')).toBe('en');
    expect(explicitLanguageSubtag('EN')).toBe('en');
    expect(explicitLanguageSubtag('en-US')).toBe('en');
    expect(explicitLanguageSubtag('pt_BR')).toBe('pt');
    expect(explicitLanguageSubtag('zh-Hant-TW')).toBe('zh');
    expect(isExplicitLanguage('en-US')).toBe(true);
  });

  test('does not trim — matches the inline copies it replaces', () => {
    // AssemblyAI's sync path trims BEFORE calling; nothing else did, and adding
    // a trim here would change what those adapters send.
    expect(explicitLanguageSubtag(' en-US ')).toBe(' en');
    expect(explicitLanguageSubtag('  ')).toBe('  ');
  });

  test('a degenerate tag yields an empty subtag, not undefined', () => {
    // Callers distinguish "auto" from "explicit but empty" by testing against
    // undefined, so `-en` must stay on the explicit branch.
    expect(explicitLanguageSubtag('-en')).toBe('');
    expect(isExplicitLanguage('-en')).toBe(true);
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

describe('readRequiredJsonString', () => {
  const options = {
    provider: 'TestProvider',
    field: 'job_id',
    malformedMessage: 'malformed create response',
    missingMessage: 'create returned no job id',
  };

  test('returns the required field', async () => {
    const response = Response.json({ job_id: 'job-123' });
    expect(await readRequiredJsonString(response, options)).toBe('job-123');
  });

  test('preserves the malformed JSON error', async () => {
    const response = new Response('{');
    await expect(readRequiredJsonString(response, options)).rejects.toThrow(
      'TestProvider unavailable: malformed create response',
    );
  });

  test('preserves the missing-field error', async () => {
    const response = Response.json({ other: 'value' });
    await expect(readRequiredJsonString(response, options)).rejects.toThrow(
      'TestProvider unavailable: create returned no job id',
    );
  });
});

describe('providerHttpError (shared non-2xx classification for the sync STT adapters)', () => {
  const policy = { label: 'TestProvider', authMessage: 'TestProvider API key is invalid' };
  const at = (status: number, body = 'boom') => new Response(body, { status });

  test('auth statuses throw a plain Error (a sibling retry cannot fix a bad key)', async () => {
    const err = await providerHttpError('test', at(401), performance.now(), {}, policy);
    expect(err).toBeInstanceOf(Error);
    expect(err).not.toBeInstanceOf(ProviderUnavailableError);
    expect(err.message).toBe('TestProvider API key is invalid');
  });

  test('403 is an auth failure only for adapters that opt in', async () => {
    const plain = await providerHttpError('test', at(403), performance.now(), {}, policy);
    expect(plain).toBeInstanceOf(ProviderInputError);

    const optedIn = await providerHttpError('test', at(403), performance.now(), {}, {
      ...policy,
      authStatuses: [401, 403],
    });
    expect(optedIn).not.toBeInstanceOf(ProviderInputError);
    expect(optedIn.message).toBe('TestProvider API key is invalid');
  });

  test('429 and 5xx fail over to the next provider', async () => {
    const rateLimited = await providerHttpError('test', at(429), performance.now(), {}, policy);
    expect(rateLimited).toBeInstanceOf(ProviderUnavailableError);
    expect(rateLimited.message).toBe('TestProvider unavailable: rate limit exceeded');

    const serverError = await providerHttpError('test', at(503), performance.now(), {}, policy);
    expect(serverError).toBeInstanceOf(ProviderUnavailableError);
    expect(serverError.message).toBe('TestProvider unavailable: upstream 5xx: 503');
  });

  test('402 fails over only when the adapter opts in, else it is an input error', async () => {
    const notOptedIn = await providerHttpError('test', at(402), performance.now(), {}, policy);
    expect(notOptedIn).toBeInstanceOf(ProviderInputError);

    const optedIn = await providerHttpError('test', at(402), performance.now(), {}, {
      ...policy,
      failoverOn402: true,
    });
    expect(optedIn).toBeInstanceOf(ProviderUnavailableError);
    expect(optedIn.message).toBe('TestProvider unavailable: insufficient funds');
  });

  test('other 4xx become a ProviderInputError carrying the body preview', async () => {
    const err = await providerHttpError('test', at(400, 'bad language code'), performance.now(), {}, policy);
    expect(err).toBeInstanceOf(ProviderInputError);
    expect((err as ProviderInputError).status).toBe(400);
    expect(err.message).toContain('bad language code');
  });

  test('an empty error body falls back to the bare status in the message', async () => {
    const err = await providerHttpError('test', at(400, ''), performance.now(), {}, policy);
    expect(err.message).toBe('TestProvider rejected input (400): HTTP 400');
  });

  test('attachUnavailableDetails carries kind/status through to the failover error', async () => {
    const bare = await providerHttpError('test', at(500), performance.now(), {}, policy) as ProviderUnavailableError;
    expect(bare.kind).toBe('unknown');
    expect(bare.status).toBeUndefined();

    const detailed = await providerHttpError('test', at(500), performance.now(), {}, {
      ...policy,
      attachUnavailableDetails: true,
    }) as ProviderUnavailableError;
    expect(detailed.kind).toBe('upstream_5xx');
    expect(detailed.status).toBe(500);
    expect(detailed.elapsedMs).toBeGreaterThanOrEqual(0);
  });

  test('logs one http_error event with the kind and any adapter-supplied details', async () => {
    const logged: unknown[][] = [];
    const originalLog = console.log;
    console.log = ((...args: unknown[]) => { logged.push(args); }) as typeof console.log;
    try {
      await providerHttpError('test', at(429), performance.now(), { requestId: 'req-1' }, {
        ...policy,
        logDetails: { model: 'test-model' },
      });
    } finally {
      console.log = originalLog;
    }

    expect(logged).toHaveLength(1);
    expect(logged[0][0]).toBe('provider.http_error');
    expect(logged[0][1]).toMatchObject({
      provider: 'test',
      requestId: 'req-1',
      model: 'test-model',
      status: 429,
      kind: 'rate_limit',
      bodyPreview: 'boom',
    });
  });
});

describe('splitVocabularyTerms (shared vocabulary-prompt splitter)', () => {
  const NO_LIMITS = { maxTerms: 1000 };

  test('splits on commas, newlines and semicolons', () => {
    expect(splitVocabularyTerms('HyperWhisper,SwiftUI;Claude\nBun', NO_LIMITS))
      .toEqual(['HyperWhisper', 'SwiftUI', 'Claude', 'Bun']);
  });

  test('collapses runs of separators instead of emitting empty terms', () => {
    expect(splitVocabularyTerms('a,,;\n,b', NO_LIMITS)).toEqual(['a', 'b']);
  });

  test('trims each term and drops empty ones', () => {
    expect(splitVocabularyTerms('  a  ,   ,b\t', NO_LIMITS)).toEqual(['a', 'b']);
  });

  test('strips a leading bullet marker, with or without a following space', () => {
    expect(splitVocabularyTerms('- a\n* b\n-c\n*d', NO_LIMITS)).toEqual(['a', 'b', 'c', 'd']);
  });

  test('a term that is only a bullet marker is dropped', () => {
    expect(splitVocabularyTerms('-,*,a', NO_LIMITS)).toEqual(['a']);
  });

  test('inner whitespace is preserved by default', () => {
    expect(splitVocabularyTerms('Claude  Code', NO_LIMITS)).toEqual(['Claude  Code']);
  });

  test('maxTermChars drops over-long terms rather than truncating them', () => {
    const terms = splitVocabularyTerms(`ok,${'x'.repeat(51)},${'y'.repeat(50)}`, {
      maxTerms: 100,
      maxTermChars: 50,
    });
    expect(terms).toEqual(['ok', 'y'.repeat(50)]);
  });

  test('omitting maxTermChars applies no length limit', () => {
    expect(splitVocabularyTerms('z'.repeat(400), NO_LIMITS)).toEqual(['z'.repeat(400)]);
  });

  test('maxTermWords drops phrases with too many words', () => {
    const terms = splitVocabularyTerms('one two three,one two three four', {
      maxTerms: 100,
      maxTermWords: 3,
    });
    expect(terms).toEqual(['one two three']);
  });

  test('maxTerms caps the result, counting only kept terms', () => {
    // The over-long term is dropped before the cap, so three terms still come back.
    const terms = splitVocabularyTerms(`a,${'x'.repeat(99)},b,c,d`, {
      maxTerms: 3,
      maxTermChars: 10,
    });
    expect(terms).toEqual(['a', 'b', 'c']);
  });

  test('joinWordsWith replaces inner whitespace runs before the length check', () => {
    // Mistral Voxtral 400s the whole request on a `context_bias` item holding
    // whitespace, so a multi-word term is joined rather than dropped.
    expect(splitVocabularyTerms('- Claude  Code, Bun', { maxTerms: 100, joinWordsWith: '_' }))
      .toEqual(['Claude_Code', 'Bun']);
    // 'a' + 5 spaces + 'b' is 7 chars raw but 3 once joined, so the cap sees 3.
    expect(splitVocabularyTerms('a     b', { maxTerms: 100, maxTermChars: 4, joinWordsWith: '_' }))
      .toEqual(['a_b']);
  });

  test('an empty prompt yields no terms', () => {
    expect(splitVocabularyTerms('', NO_LIMITS)).toEqual([]);
    expect(splitVocabularyTerms('   ', NO_LIMITS)).toEqual([]);
  });
});

describe('upstreamDurationOrNull (issue #381)', () => {
  test('accepts a positive finite number and rejects everything else', () => {
    expect(upstreamDurationOrNull(22.2)).toBe(22.2);
    expect(upstreamDurationOrNull(0)).toBeNull();
    expect(upstreamDurationOrNull(-1)).toBeNull();
    expect(upstreamDurationOrNull(undefined)).toBeNull();
    expect(upstreamDurationOrNull('22.2')).toBeNull();
    expect(upstreamDurationOrNull(NaN)).toBeNull();
  });

  test('rejects Infinity, which a plain `> 0` lets through', () => {
    // Not hypothetical: `JSON.parse('{"duration":1e999}')` yields Infinity, and
    // the hand-written `duration > 0` at each of the three refusal sites passed
    // it straight into the refusal message —
    //   "Groq unavailable: empty transcript for Infinitys of audio"
    // — and into `upstreamDurationSeconds`, the log field whose whole job is to
    // hold a number an operator can trust.
    const parsed = JSON.parse('{"duration":1e999}') as { duration: number };
    expect(parsed.duration).toBe(Infinity);
    expect(parsed.duration > 0).toBe(true);
    expect(upstreamDurationOrNull(parsed.duration)).toBeNull();
    expect(upstreamDurationOrNull(-Infinity)).toBeNull();
  });
});
