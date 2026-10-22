import { afterEach, describe, expect, mock, test } from 'bun:test';
import { classifyProviderHttpError, providerHttpError, type ProviderHttpErrorPolicy } from './utils';
import { ProviderAuthError, ProviderInputError, ProviderUnavailableError } from './types';
import { readUpstreamError } from './upstream-error';
import { transcribeWithAzureMai } from './azure-mai';

// The knobs that let azure-mai, google-chirp, assemblyai and soniox run on the
// shared ladder without changing what they throw. (issue #731)

const CREDITS_BODY = '{"error":"Your team has used all available credits"}';
const at = (status: number, body = 'boom') => new Response(body, { status });

const terminalPolicy: ProviderHttpErrorPolicy = {
  label: 'Test',
  authStatuses: [],
  terminalStatuses: { 401: 'bad key', 403: 'disabled' },
  otherStatusError: (status) => new Error(`Test error: ${status}`),
};

describe('providerHttpError: terminalStatuses and otherStatusError', () => {
  test('a terminal status is a plain Error with exactly its message', async () => {
    for (const [status, message] of [[401, 'bad key'], [403, 'disabled']] as const) {
      const err = await providerHttpError('test', at(status), performance.now(), {}, terminalPolicy);
      expect(err.constructor).toBe(Error);
      expect(err.message).toBe(message);
    }
  });

  test('a terminal status wins over an out-of-credits body', async () => {
    const err = await providerHttpError('test', at(403, CREDITS_BODY), performance.now(), {}, terminalPolicy);
    expect(err).not.toBeInstanceOf(ProviderUnavailableError);
    expect(err.message).toBe('disabled');
  });

  test('authStatuses [] never raises a ProviderAuthError', async () => {
    const err = await providerHttpError('test', at(401), performance.now(), {}, {
      label: 'Test',
      authStatuses: [],
    });
    expect(err).not.toBeInstanceOf(ProviderAuthError);
    expect(err).toBeInstanceOf(ProviderInputError);
  });

  test('otherStatusError replaces the ProviderInputError fallthrough, and only that', async () => {
    const other = await providerHttpError('test', at(400), performance.now(), {}, terminalPolicy);
    expect(other.constructor).toBe(Error);
    expect(other.message).toBe('Test error: 400');

    const rateLimited = await providerHttpError('test', at(429), performance.now(), {}, terminalPolicy);
    expect(rateLimited).toBeInstanceOf(ProviderUnavailableError);
    const fiveHundred = await providerHttpError('test', at(503), performance.now(), {}, terminalPolicy);
    expect(fiveHundred).toBeInstanceOf(ProviderUnavailableError);
  });

  test('classifyProviderHttpError on a pre-read body matches providerHttpError', async () => {
    const preRead = await readUpstreamError(at(400, 'nope'));
    const fromRead = classifyProviderHttpError('test', preRead, performance.now(), {}, terminalPolicy);
    const fromResponse = await providerHttpError('test', at(400, 'nope'), performance.now(), {}, terminalPolicy);
    expect(fromRead.message).toBe(fromResponse.message);
    expect(fromRead.constructor).toBe(fromResponse.constructor);
  });
});

describe('azure-mai through the shared ladder', () => {
  const originalFetch = globalThis.fetch;
  const savedKey = process.env.AZURE_SPEECH_KEY_EASTUS;
  afterEach(() => {
    globalThis.fetch = originalFetch;
    if (savedKey === undefined) delete process.env.AZURE_SPEECH_KEY_EASTUS;
    else process.env.AZURE_SPEECH_KEY_EASTUS = savedKey;
  });

  test('a 403 carrying an out-of-credits body still ends the request with the curated plain Error', async () => {
    process.env.AZURE_SPEECH_KEY_EASTUS = 'test-key';
    globalThis.fetch = mock(async () => at(403, CREDITS_BODY)) as unknown as typeof fetch;
    const err = await transcribeWithAzureMai(new ArrayBuffer(10), 'audio/wav').catch((e: unknown) => e);
    expect(err).not.toBeInstanceOf(ProviderUnavailableError);
    expect((err as Error).message).toBe('Azure Speech subscription is disabled or out of quota');
  });
});
