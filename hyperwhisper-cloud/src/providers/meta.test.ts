import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { META_MUSE_MAX_BYTES } from '../lib/constants';
import { computeMetaMuseTranscriptionCost } from '../lib/cost-calculator';
import { transcribeWithMeta } from './meta';
import { AudioTooLargeError, ProviderInputError, ProviderUnavailableError, UnsupportedAudioFormatError } from './types';

const originalFetch = globalThis.fetch;
const originalKey = process.env.META_MODEL_API_KEY;

interface WavOptions {
  formatTag?: number;
  channels?: number;
  sampleRate?: number;
  bitsPerSample?: number;
  dataBytes?: number;
  junkBytes?: number;
}

function writeAscii(view: DataView, offset: number, value: string): void {
  for (let index = 0; index < value.length; index++) {
    view.setUint8(offset + index, value.charCodeAt(index));
  }
}

function wav(options: WavOptions = {}): ArrayBuffer {
  const formatTag = options.formatTag ?? 1;
  const channels = options.channels ?? 1;
  const sampleRate = options.sampleRate ?? 16_000;
  const bitsPerSample = options.bitsPerSample ?? 16;
  const blockAlign = channels * (bitsPerSample / 8);
  const dataBytes = options.dataBytes ?? Math.max(1, blockAlign) * sampleRate;
  const junkBytes = options.junkBytes ?? 0;
  const junkPadded = junkBytes + (junkBytes & 1);
  const totalBytes = 12 + 24 + (junkBytes > 0 ? 8 + junkPadded : 0) + 8 + dataBytes;
  const buffer = new ArrayBuffer(totalBytes);
  const view = new DataView(buffer);

  writeAscii(view, 0, 'RIFF');
  view.setUint32(4, totalBytes - 8, true);
  writeAscii(view, 8, 'WAVE');
  writeAscii(view, 12, 'fmt ');
  view.setUint32(16, 16, true);
  view.setUint16(20, formatTag, true);
  view.setUint16(22, channels, true);
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, sampleRate * blockAlign, true);
  view.setUint16(32, blockAlign, true);
  view.setUint16(34, bitsPerSample, true);

  let offset = 36;
  if (junkBytes > 0) {
    writeAscii(view, offset, 'JUNK');
    view.setUint32(offset + 4, junkBytes, true);
    offset += 8 + junkPadded;
  }
  writeAscii(view, offset, 'data');
  view.setUint32(offset + 4, dataBytes, true);
  return buffer;
}

function okResponse(transcript = '  hello from muse  ', audioDurationMs = 12_500): Response {
  return Response.json({ transcript, audioDurationMs, turns: [] });
}

beforeEach(() => {
  process.env.META_MODEL_API_KEY = 'test-meta-key';
});

afterEach(() => {
  globalThis.fetch = originalFetch;
  if (originalKey === undefined) delete process.env.META_MODEL_API_KEY;
  else process.env.META_MODEL_API_KEY = originalKey;
});

describe('Meta Muse request and response', () => {
  test('sends the documented multipart fields, bearer auth, model, language and keywords', async () => {
    let calledUrl = '';
    let authorization = '';
    let accept = '';
    let requestJson: Record<string, unknown> = {};
    let audioType = '';
    let audioName = '';
    globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
      calledUrl = String(input);
      const headers = init?.headers as Record<string, string>;
      authorization = headers.Authorization;
      accept = headers.Accept;
      const form = init?.body as FormData;
      expect([...form.keys()]).toEqual(['request', 'audio']);
      requestJson = JSON.parse(await (form.get('request') as File).text()) as Record<string, unknown>;
      const audioPart = form.get('audio') as File;
      audioType = audioPart.type;
      audioName = audioPart.name;
      return okResponse();
    }) as unknown as typeof fetch;

    const result = await transcribeWithMeta(
      wav({ sampleRate: 24_000 }),
      'audio/wav',
      'en-US',
      'HyperWhisper, SwiftUI\n- Meta Muse',
      { model: 'muse-voice-transcribe-1.0' },
    );

    expect(calledUrl).toBe('https://api.meta.ai/v1/asr/transcribe');
    expect(authorization).toBe('Bearer test-meta-key');
    expect(accept).toBe('application/json');
    expect(requestJson).toEqual({
      model: 'muse-voice-transcribe-1.0',
      audioEncoding: 'WAV',
      mode: 'PUSH_TO_TALK',
      keywords: ['HyperWhisper', 'SwiftUI', 'Meta Muse'],
      languageBias: ['English'],
    });
    expect(audioType).toBe('audio/wav');
    expect(audioName).toBe('audio.wav');
    expect(result).toEqual({
      text: 'hello from muse',
      durationSeconds: 12.5,
      costUsd: computeMetaMuseTranscriptionCost(12.5),
      source: 'meta',
    });
  });

  test('omits optional bias fields for automatic language and no vocabulary', async () => {
    let requestJson: Record<string, unknown> = {};
    globalThis.fetch = mock(async (_input: RequestInfo | URL, init?: RequestInit) => {
      const form = init?.body as FormData;
      requestJson = JSON.parse(await (form.get('request') as File).text()) as Record<string, unknown>;
      return okResponse('ok', 1000);
    }) as unknown as typeof fetch;

    await transcribeWithMeta(wav(), 'audio/wav', 'auto');
    expect(requestJson).toEqual({
      model: 'muse-voice-transcribe-1.0',
      audioEncoding: 'WAV',
      mode: 'PUSH_TO_TALK',
    });
  });

  test('normalizes an empty transcript to the free no-speech result', async () => {
    globalThis.fetch = mock(async () => okResponse('   ', 5000)) as unknown as typeof fetch;
    await expect(transcribeWithMeta(wav(), 'audio/wav')).resolves.toEqual({
      text: '', durationSeconds: 0, costUsd: 0, source: 'no_speech',
    });
  });

  test('requires the configured environment key without reaching upstream', async () => {
    delete process.env.META_MODEL_API_KEY;
    let requests = 0;
    globalThis.fetch = mock(async () => { requests++; return okResponse(); }) as unknown as typeof fetch;

    await expect(transcribeWithMeta(wav(), 'audio/wav')).rejects.toThrow('META_MODEL_API_KEY not configured');
    expect(requests).toBe(0);
  });

  test.each([
    [401, Error, 'invalid or unauthorized'],
    [403, Error, 'invalid or unauthorized'],
    [429, ProviderUnavailableError, 'rate limit exceeded'],
    [500, ProviderUnavailableError, 'upstream 5xx'],
    [400, ProviderInputError, 'rejected input'],
  ] as const)('maps HTTP %i to the existing error contract', async (status, errorType, message) => {
    globalThis.fetch = mock(async () => new Response('upstream detail', { status })) as unknown as typeof fetch;
    await expect(transcribeWithMeta(wav(), 'audio/wav')).rejects.toThrow(errorType);
    await expect(transcribeWithMeta(wav(), 'audio/wav')).rejects.toThrow(message);
  });

  test.each([
    new Response('<html>', { status: 200, headers: { 'Content-Type': 'text/html' } }),
    Response.json({ transcript: 'missing duration' }),
    Response.json({ transcript: 42, audioDurationMs: 1000 }),
    Response.json({ transcript: 'bad duration', audioDurationMs: Number.POSITIVE_INFINITY }),
    Response.json({ transcript: 'zero duration', audioDurationMs: 0 }),
  ])('maps malformed 200 responses to ProviderUnavailableError', async (upstream) => {
    globalThis.fetch = mock(async () => upstream.clone()) as unknown as typeof fetch;
    await expect(transcribeWithMeta(wav(), 'audio/wav')).rejects.toThrow(ProviderUnavailableError);
  });
});

describe('Meta Muse WAV preflight', () => {
  test.each([16_000, 24_000])('accepts mono PCM16 WAV at %i Hz', async (sampleRate) => {
    globalThis.fetch = mock(async () => okResponse('valid', 1000)) as unknown as typeof fetch;
    await expect(transcribeWithMeta(wav({ sampleRate }), 'audio/wav')).resolves.toMatchObject({ text: 'valid' });
  });

  test('accepts exactly 10 minutes and rejects audio one frame longer before fetch', async () => {
    let requests = 0;
    globalThis.fetch = mock(async () => { requests++; return okResponse('valid', 600_000); }) as unknown as typeof fetch;

    await expect(transcribeWithMeta(wav({ dataBytes: 16_000 * 2 * 600 }), 'audio/wav'))
      .resolves.toMatchObject({ durationSeconds: 600 });
    expect(requests).toBe(1);

    await expect(transcribeWithMeta(wav({ dataBytes: (16_000 * 2 * 600) + 2 }), 'audio/wav'))
      .rejects.toThrow(ProviderInputError);
    expect(requests).toBe(1);
  });

  test('accepts a WAV file exactly at 32 MB and rejects one byte above the cap before fetch', async () => {
    const dataBytes = 2;
    const fixedBytes = 12 + 24 + 8 + 8 + dataBytes;
    const atCap = wav({ dataBytes, junkBytes: META_MUSE_MAX_BYTES - fixedBytes });
    expect(atCap.byteLength).toBe(META_MUSE_MAX_BYTES);

    let requests = 0;
    globalThis.fetch = mock(async () => { requests++; return okResponse('valid', 1); }) as unknown as typeof fetch;
    await expect(transcribeWithMeta(atCap, 'audio/wav')).resolves.toMatchObject({ text: 'valid' });
    expect(requests).toBe(1);

    await expect(transcribeWithMeta(new ArrayBuffer(META_MUSE_MAX_BYTES + 1), 'audio/wav'))
      .rejects.toThrow(AudioTooLargeError);
    expect(requests).toBe(1);
  });

  test.each([
    ['stereo', () => wav({ channels: 2 })],
    ['PCM8', () => wav({ bitsPerSample: 8 })],
    ['PCM24', () => wav({ bitsPerSample: 24 })],
    ['floating-point', () => wav({ formatTag: 3 })],
    ['compressed', () => wav({ formatTag: 2 })],
    ['8 kHz', () => wav({ sampleRate: 8_000 })],
    ['48 kHz', () => wav({ sampleRate: 48_000 })],
    ['non-WAV', () => new Uint8Array([1, 2, 3, 4]).buffer],
  ])('rejects %s input without an upstream request', async (_name, makeAudio) => {
    let requests = 0;
    globalThis.fetch = mock(async () => { requests++; return okResponse(); }) as unknown as typeof fetch;
    await expect(transcribeWithMeta(makeAudio(), 'application/octet-stream'))
      .rejects.toThrow(UnsupportedAudioFormatError);
    expect(requests).toBe(0);
  });

  test('rejects malformed RIFF chunk lengths without an upstream request', async () => {
    const malformed = wav();
    new DataView(malformed).setUint32(16, malformed.byteLength, true);
    let requests = 0;
    globalThis.fetch = mock(async () => { requests++; return okResponse(); }) as unknown as typeof fetch;

    await expect(transcribeWithMeta(malformed, 'audio/wav')).rejects.toThrow(UnsupportedAudioFormatError);
    expect(requests).toBe(0);
  });
});
