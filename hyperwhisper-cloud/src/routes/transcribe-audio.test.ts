import { describe, expect, test } from 'bun:test';
import type { Context } from 'hono';
import type { AuthContext } from '../middleware/auth';
import {
  estimateCreditsForProviderFallbacks,
  prepareTranscriptionAudio,
} from './transcribe-audio';

const BASE_INPUT = {
  requestId: 'request-credit-test',
  startTime: performance.now(),
  provider: 'deepgram' as const,
  contentType: 'audio/wav',
  contentLength: 2_048,
  model: 'nova-3-general',
  medical: false,
  auth: {
    identifier: 'licensed-account',
    licenseKey: 'licensed-account',
    credits: 1_000,
  } satisfies AuthContext,
  clientIP: '203.0.113.10',
};

function contextWithBody(body: ArrayBuffer, onRead?: () => void): Context {
  return {
    req: {
      arrayBuffer: async () => {
        onRead?.();
        return body;
      },
    },
  } as unknown as Context;
}

function pcm16Wav(seconds: number, sampleRate: 16_000 | 24_000): ArrayBuffer {
  const dataBytes = sampleRate * 2 * seconds;
  const audio = new ArrayBuffer(44 + dataBytes);
  const view = new DataView(audio);

  for (const [offset, value] of [[0, 'RIFF'], [8, 'WAVE'], [12, 'fmt '], [36, 'data']] as const) {
    for (let index = 0; index < value.length; index++) {
      view.setUint8(offset + index, value.charCodeAt(index));
    }
  }

  view.setUint32(4, audio.byteLength - 8, true);
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, 1, true);
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, sampleRate * 2, true);
  view.setUint16(32, 2, true);
  view.setUint16(34, 16, true);
  view.setUint32(40, dataBytes, true);
  return audio;
}

describe('prepareTranscriptionAudio credit enforcement', () => {
  test('rejects insufficient credits before it allocates an audio body', async () => {
    let bodyReads = 0;

    const result = await prepareTranscriptionAudio({
      ...BASE_INPUT,
      c: contextWithBody(new ArrayBuffer(BASE_INPUT.contentLength), () => bodyReads++),
      auth: { ...BASE_INPUT.auth, credits: 0 },
    });

    expect(result.ok).toBe(false);
    if (result.ok) throw new Error('expected the credit gate to reject the request');
    expect(result.response.status).toBe(402);
    expect((await result.response.json() as { error: string }).error).toBe('Insufficient credits');
    expect(bodyReads).toBe(0);
  });

  test('reads an affordable normal-provider body exactly once', async () => {
    const audio = new ArrayBuffer(BASE_INPUT.contentLength);
    let bodyReads = 0;

    const result = await prepareTranscriptionAudio({
      ...BASE_INPUT,
      c: contextWithBody(audio, () => bodyReads++),
    });

    expect(result).toEqual({ ok: true, audioBuffer: audio });
    expect(bodyReads).toBe(1);
  });

  test('rejects an under-declared body after the credit check', async () => {
    const actualBytes = BASE_INPUT.contentLength + 1;

    const result = await prepareTranscriptionAudio({
      ...BASE_INPUT,
      c: contextWithBody(new ArrayBuffer(actualBytes)),
    });

    expect(result.ok).toBe(false);
    if (result.ok) throw new Error('expected the size gate to reject the request');
    expect(result.response.status).toBe(400);
    expect(await result.response.json()).toMatchObject({
      error: 'Content-Length mismatch',
      declared_bytes: BASE_INPUT.contentLength,
      actual_bytes: actualBytes,
    });
  });

  test('checks a buffered provider again with the exact audio duration', async () => {
    // A 16 kHz WAV can be longer than the safe 24 kHz pre-buffer floor for the
    // same byte length. The first check permits the allocation. The exact check
    // must then stop the request when the account cannot pay for the full clip.
    const audio = pcm16Wav(60, 16_000);
    const minimumCredits = estimateCreditsForProviderFallbacks(
      audio.byteLength, 'meta', undefined, false, undefined, undefined, 40,
    );
    const exactCredits = estimateCreditsForProviderFallbacks(
      audio.byteLength, 'meta', undefined, false, undefined, undefined, 60,
    );
    expect(exactCredits).toBeGreaterThan(minimumCredits);

    let bodyReads = 0;
    const result = await prepareTranscriptionAudio({
      ...BASE_INPUT,
      c: contextWithBody(audio, () => bodyReads++),
      provider: 'meta',
      model: 'parakeet-tdt-0.6b-v3',
      contentLength: audio.byteLength,
      auth: { ...BASE_INPUT.auth, credits: (minimumCredits + exactCredits) / 2 },
    });

    expect(result.ok).toBe(false);
    if (result.ok) throw new Error('expected the exact credit gate to reject the request');
    expect(result.response.status).toBe(402);
    expect(bodyReads).toBe(1);
  });

  test('lets a provider report malformed buffered audio without a second credit rejection', async () => {
    // The adapter owns the public format error. The route must not replace it
    // with a credit error based on a duration estimate that the parser rejected.
    const malformedAudio = new ArrayBuffer(48_044);
    const minimumCredits = estimateCreditsForProviderFallbacks(
      malformedAudio.byteLength, 'meta', undefined, false, undefined, undefined, 1,
    );

    const result = await prepareTranscriptionAudio({
      ...BASE_INPUT,
      c: contextWithBody(malformedAudio),
      provider: 'meta',
      model: 'parakeet-tdt-0.6b-v3',
      contentLength: malformedAudio.byteLength,
      auth: { ...BASE_INPUT.auth, credits: minimumCredits },
    });

    expect(result).toEqual({ ok: true, audioBuffer: malformedAudio });
  });
});
