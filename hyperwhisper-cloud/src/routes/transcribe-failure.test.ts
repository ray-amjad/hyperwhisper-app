import { describe, expect, test } from 'bun:test';
import { ProviderInputError } from '../providers/types';
import { providerChainFailureResponse } from './transcribe-failure';

function baseInput() {
  return {
    requestId: 'request-123',
    startTime: performance.now(),
    fallbackCount: 0,
    attemptFailures: [],
    lastError: undefined,
    lastInputError: undefined,
    sawUnavailable: false,
  };
}

async function body(response: Response) {
  return response.json() as Promise<Record<string, unknown>>;
}

describe('providerChainFailureResponse', () => {
  test('returns 400 when every provider rejects the request input', async () => {
    const inputError = new ProviderInputError('ElevenLabs', 422, 'invalid language');
    const response = providerChainFailureResponse({
      ...baseInput(),
      provider: 'elevenlabs',
      fallbackCount: 2,
      lastError: inputError,
      lastInputError: inputError,
    });

    expect(response.status).toBe(400);
    expect(await body(response)).toEqual({
      error: 'Transcription input rejected',
      message: 'No transcription provider accepted this request: ElevenLabs rejected input (422): invalid language',
      requestId: 'request-123',
      provider: 'elevenlabs',
    });
  });

  test('does not report a request error when any provider was unavailable', async () => {
    const inputError = new ProviderInputError('Groq', 400, 'unsupported audio');
    const response = providerChainFailureResponse({
      ...baseInput(),
      provider: 'deepgram',
      fallbackCount: 2,
      lastError: inputError,
      lastInputError: inputError,
      sawUnavailable: true,
    });

    expect(response.status).toBe(429);
    expect(await body(response)).toMatchObject({
      error: 'All providers unavailable',
      requestId: 'request-123',
    });
  });

  test('returns 502 for a self-only provider and preserves the upstream error', async () => {
    const response = providerChainFailureResponse({
      ...baseInput(),
      provider: 'google-chirp',
      lastError: new Error('Google Speech deadline exceeded'),
      sawUnavailable: true,
    });

    expect(response.status).toBe(502);
    expect(await body(response)).toEqual({
      error: 'google-chirp unavailable',
      message: 'Google Speech deadline exceeded',
      requestId: 'request-123',
      provider: 'google-chirp',
    });
  });

  test('uses a useful default message when a self-only failure has no error', async () => {
    const response = providerChainFailureResponse({
      ...baseInput(),
      provider: 'azure-mai',
      sawUnavailable: true,
    });

    expect(response.status).toBe(502);
    expect(await body(response)).toEqual({
      error: 'azure-mai unavailable',
      message: 'azure-mai is currently unavailable. Please try again shortly.',
      requestId: 'request-123',
      provider: 'azure-mai',
    });
  });

  test('returns a retryable 429 only after a multi-provider chain is exhausted', async () => {
    const response = providerChainFailureResponse({
      ...baseInput(),
      provider: 'deepgram',
      fallbackCount: 2,
      attemptFailures: [
        { provider: 'deepgram', kind: 'timeout', attemptMs: 15_001 },
        { provider: 'groq', kind: 'rate_limit', status: 429 },
        { provider: 'elevenlabs', kind: 'upstream_5xx', status: 503 },
      ],
      lastError: new Error('ElevenLabs unavailable'),
      sawUnavailable: true,
    });

    expect(response.status).toBe(429);
    expect(await body(response)).toEqual({
      error: 'All providers unavailable',
      message: 'All transcription providers are currently rate-limited. Please try again shortly.',
      requestId: 'request-123',
    });
  });
});
