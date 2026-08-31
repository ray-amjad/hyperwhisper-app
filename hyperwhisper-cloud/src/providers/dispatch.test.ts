// The boundary the transcribe route now calls instead of holding its own
// id → adapter table. What matters here is that the table is TOTAL over the
// registry, that dispatch reaches the adapter the id names, and that it stays
// transparent — the route's whole fallback policy is built on the argument
// contract and the error types passing straight through.

import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';

import { hasProviderAdapter, transcribeWithProvider } from './dispatch';
import { ProviderUnavailableError } from './types';
import { ALL_STT_PROVIDER_IDS } from '../lib/stt-models';

const originalFetch = globalThis.fetch;

function audio(): ArrayBuffer {
  return new Uint8Array(2048).buffer;
}

describe('dispatch covers the registry', () => {
  // The registry decides which providers are routable, and the route dispatches
  // every id it is handed without checking. A registered provider with no
  // adapter row would therefore be a runtime "PROVIDER_FN[x] is not a function"
  // on a paid request. `Record<SttProviderId, TranscribeFn>` makes that a
  // compile error too; this is the runtime half of the same guard.
  test('every registered provider has an adapter', () => {
    const missing = ALL_STT_PROVIDER_IDS.filter((id) => !hasProviderAdapter(id));
    expect(missing).toEqual([]);
  });

  test('the registry is not empty, so the check above can actually fail', () => {
    expect(ALL_STT_PROVIDER_IDS.length).toBeGreaterThan(0);
  });
});

describe('transcribeWithProvider', () => {
  beforeEach(() => { process.env.DEEPGRAM_API_KEY = 'test-deepgram-key'; });
  afterEach(() => { globalThis.fetch = originalFetch; });

  test('routes the id to that provider\'s upstream and returns its result', async () => {
    let calledUrl = '';
    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      calledUrl = String(input);
      return Response.json({
        results: { channels: [{ alternatives: [{ transcript: 'dispatched' }], detected_language: 'en' }] },
        metadata: { duration: 3, request_id: 'dg-1' },
      });
    }) as unknown as typeof fetch;

    const result = await transcribeWithProvider('deepgram', audio(), 'audio/wav');

    expect(calledUrl).toContain('api.deepgram.com');
    expect(result.text).toBe('dispatched');
    expect(result.source).toBe('deepgram');
  });

  // Every optional argument travels through this one call, and the resolved
  // model arrives inside `context`. Dropping any of them would silently run the
  // provider default on auto-detect with no vocabulary — a correct-looking 200
  // that bills the wrong model. The upstream ids below are the adapter's own
  // translation of the registry ids (`nova-2-general` → `nova-2`, and nova-2
  // takes `keywords` where nova-3 takes `keyterm`); what this pins is that all
  // three values reach it at all.
  test('forwards the language, the prompt and the request context to the adapter', async () => {
    let calledUrl = '';
    globalThis.fetch = mock(async (input: RequestInfo | URL) => {
      calledUrl = String(input);
      return Response.json({
        results: { channels: [{ alternatives: [{ transcript: 'ok' }] }] },
        metadata: { duration: 1, request_id: 'dg-2' },
      });
    }) as unknown as typeof fetch;

    await transcribeWithProvider('deepgram', audio(), 'audio/wav', 'de', 'HyperWhisper', {
      requestId: 'req-dispatch',
      attempt: 2,
      model: 'nova-2-general',
    });

    expect(calledUrl).toContain('model=nova-2');
    expect(calledUrl).toContain('language=de');
    expect(calledUrl).toContain('keywords=HyperWhisper');
  });

  // The route falls back to a sibling on ProviderUnavailableError, returns 400
  // on ProviderInputError, and 413/415 on the two audio errors. If dispatch
  // caught or rewrapped anything, every one of those arms would become the
  // catch-all 500 instead.
  test('lets the adapter\'s own error type reach the caller unchanged', async () => {
    globalThis.fetch = mock(async () => new Response('upstream down', { status: 503 })) as unknown as typeof fetch;

    await expect(transcribeWithProvider('deepgram', audio(), 'audio/wav'))
      .rejects.toBeInstanceOf(ProviderUnavailableError);
  });
});
