import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { transcribeWithAssemblyAI } from './assemblyai';
import { dictationKeyterms, parseDictationWav, validateDictationOptions } from './assemblyai-dictation';
import { ProviderInputError, ProviderUnavailableError, UnsupportedAudioFormatError } from './types';
import { computeAssemblyAITranscriptionCost } from '../lib/cost-calculator';
import { estimatedUsdPerMinute, resolveModel } from '../lib/stt-models';
import { maxReservationUsdPerMinute } from './reservation';
import { providerAudioReservation } from './audio-reservation';

const originalFetch = globalThis.fetch;
const originalKey = process.env.ASSEMBLYAI_API_KEY;
beforeEach(() => { process.env.ASSEMBLYAI_API_KEY = 'test-key'; });
afterEach(() => {
  globalThis.fetch = originalFetch;
  if (originalKey === undefined) delete process.env.ASSEMBLYAI_API_KEY;
  else process.env.ASSEMBLYAI_API_KEY = originalKey;
});

function wav(seconds = 1, rate = 16000, channels = 1): ArrayBuffer {
  const bytes = Math.round(seconds * rate) * channels * 2;
  const audio = new ArrayBuffer(44 + bytes);
  const view = new DataView(audio);
  for (const [offset, value] of [[0, 'RIFF'], [8, 'WAVE'], [12, 'fmt '], [36, 'data']] as const) {
    [...value].forEach((char, index) => view.setUint8(offset + index, char.charCodeAt(0)));
  }
  view.setUint32(4, audio.byteLength - 8, true);
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, channels, true);
  view.setUint32(24, rate, true);
  view.setUint32(28, rate * channels * 2, true);
  view.setUint16(32, channels * 2, true);
  view.setUint16(34, 16, true);
  view.setUint32(40, bytes, true);
  return audio;
}
const transcribe = (audio = wav(), language: string | undefined = 'en', domain?: string) =>
  transcribeWithAssemblyAI(audio, 'audio/vnd.wave', language, 'HyperWhisper, a long phrase with more than six words', { model: 'dictation', domain });

describe('Dictation transport', () => {
  test('config is typed JSON first, canonical WAV second, cleaned text and exact duration win', async () => {
    let calls = 0;
    globalThis.fetch = mock(async (url, init) => {
      calls++;
      expect(String(url)).toBe('https://dictation.assemblyai.com/v1/transcribe/live');
      expect(new Headers(init?.headers).get('Authorization')).toBe('test-key');
      expect(new Headers(init?.headers).has('X-AAI-Model')).toBe(false);
      expect(init?.signal).toBeInstanceOf(AbortSignal);
      const wire = await new Request(String(url), init).text();
      expect(wire.indexOf('name="config"')).toBeLessThan(wire.indexOf('name="audio"'));
      expect(wire).toContain('Content-Type: application/json');
      expect(wire).toContain('Content-Type: audio/wav');
      const form = init?.body as FormData;
      expect([...form.keys()]).toEqual(['config', 'audio']);
      expect((form.get('config') as File).type.split(';')[0]).toBe('application/json');
      expect((form.get('audio') as File).type).toBe('audio/wav');
      expect(JSON.parse(await (form.get('config') as File).text())).toEqual({
        language_codes: ['ja'], keyterms_prompt: ['HyperWhisper', 'a long phrase with more than six words'],
      });
      return Response.json({ text: 'raw', llm_response: 'Cleaned.', audio_duration_ms: 900000, session_id: 'id' });
    }) as unknown as typeof fetch;
    const result = await transcribe(wav(60), 'ja-JP');
    expect(result).toMatchObject({ text: 'Cleaned.', durationSeconds: 60, costUsd: 0.010333, model: 'dictation', requestId: 'id' });
    expect(calls).toBe(1);
  });
  for (const cleaned of [null, '', '   ', 42]) test(`raw fallback for ${JSON.stringify(cleaned)}`, async () => {
    globalThis.fetch = mock(async () => Response.json({ text: 'raw', llm_response: cleaned, llm_error: 'timeout' })) as unknown as typeof fetch;
    expect((await transcribe()).text).toBe('raw');
  });
  test('empty text is no speech, not another STT attempt', async () => {
    const fetchMock = mock(async () => Response.json({ text: ' ', llm_response: null }));
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    expect(await transcribe()).toMatchObject({ source: 'no_speech', costUsd: 0 });
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
  for (const status of [400, 401, 402, 403, 415, 429, 500]) test(`HTTP ${status} does not enter sync or async fallback`, async () => {
    const fetchMock = mock(async () => new Response('{"detail":"rejected"}', { status }));
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    if ([400, 415].includes(status)) await expect(transcribe()).rejects.toBeInstanceOf(ProviderInputError);
    else if ([402, 429, 500].includes(status)) await expect(transcribe()).rejects.toBeInstanceOf(ProviderUnavailableError);
    else await expect(transcribe()).rejects.toThrow('unauthorized');
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
  for (const body of ['not json', 'null', '{}', '[]']) test(`invalid response ${body} does not fall back`, async () => {
    const fetchMock = mock(async () => new Response(body));
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    await expect(transcribe()).rejects.toBeInstanceOf(ProviderUnavailableError);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
  test('90-second budget aborts the request signal without fallback', async () => {
    const originalSetTimeout = globalThis.setTimeout;
    let requestedBudget = 0;
    globalThis.setTimeout = ((handler: TimerHandler, milliseconds?: number, ...args: unknown[]) => {
      if (milliseconds === 90_000) requestedBudget = milliseconds;
      return originalSetTimeout(handler, milliseconds === 90_000 ? 5 : milliseconds, ...args);
    }) as typeof setTimeout;
    const fetchMock = mock((_input: RequestInfo | URL, init?: RequestInit) => new Promise<Response>((_resolve, reject) => {
      init?.signal?.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')), { once: true });
    }));
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    try {
      await expect(transcribe()).rejects.toMatchObject({ kind: 'timeout' });
      expect(requestedBudget).toBe(90_000);
      expect(fetchMock).toHaveBeenCalledTimes(1);
    } finally { globalThis.setTimeout = originalSetTimeout; }
  });
  test('aborted transport is typed timeout, no retry', async () => {
    const fetchMock = mock(async () => { throw new DOMException('aborted', 'AbortError'); });
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    await expect(transcribe()).rejects.toMatchObject({ kind: 'timeout' });
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});

describe('Dictation validation and billing', () => {
  for (const seconds of [1, 120]) test(`actual ${seconds}s accepted across PCM rates`, () => {
    for (const rate of [8000, 16000, 24000, 44100, 48000]) {
      expect(parseDictationWav(wav(seconds, rate, 2), 'audio/wav').durationSeconds).toBe(seconds);
    }
  });
  test('over 120 seconds fails even at a low byte rate', () => {
    expect(() => parseDictationWav(wav(120.001, 8000), 'audio/wav')).toThrow('120 seconds');
  });
  test('truncated, mismatched, empty and compressed WAV fail', () => {
    const invalid = [new ArrayBuffer(0), wav().slice(0, -1), wav(0)];
    for (const [offset, value, short] of [[20, 3, true], [22, 0, true], [24, 0, false], [28, 1, false], [32, 1, true], [34, 24, true], [40, 999999, false]] as const) {
      const buffer = wav(); const view = new DataView(buffer);
      if (short) view.setUint16(offset, value, true); else view.setUint32(offset, value, true);
      invalid.push(buffer);
    }
    for (const buffer of invalid) expect(() => parseDictationWav(buffer, 'audio/wav')).toThrow(UnsupportedAudioFormatError);
  });
  test('unsupported language and domain fail before fetch', async () => {
    const fetchMock = mock(async () => Response.json({ text: 'wrong' }));
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    for (const lang of ['', 'auto', 'pl']) await expect(transcribe(wav(), lang)).rejects.toBeInstanceOf(ProviderInputError);
    expect(() => validateDictationOptions(undefined)).toThrow('explicit');
    await expect(transcribe(wav(), 'en', 'medical')).rejects.toThrow('Medical Mode');
    expect(fetchMock).not.toHaveBeenCalled();
    expect(validateDictationOptions(' nb-NO ')).toBe('no');
  });
  test('vocabulary deduplicates and has no old six-word restriction', () => {
    expect(dictationKeyterms('<HyperWhisper>,hyperwhisper, a  long phrase with more than six words')).toEqual(['HyperWhisper', 'a long phrase with more than six words']);
    expect(dictationKeyterms(Array.from({ length: 101 }, (_, n) => `term${n}`).join(','))).toHaveLength(100);
    expect(dictationKeyterms('界'.repeat(80))).toHaveLength(1);
    expect(dictationKeyterms('界'.repeat(81))).toEqual(['界'.repeat(80)]);
  });
  test('all-in rate is shared by reservation and deduction without surcharges', () => {
    expect(resolveModel('assemblyai', 'dictation').ok).toBe(true);
    expect(resolveModel('assemblyai', 'dictation-medical').ok).toBe(false);
    const rate = 0.62 / 60;
    expect(estimatedUsdPerMinute('assemblyai', 'dictation', true, true)).toBe(rate);
    expect(computeAssemblyAITranscriptionCost(3600, 'dictation', true, true)).toBe(0.62);
    expect(maxReservationUsdPerMinute({ provider: 'assemblyai', model: 'dictation', medical: false, hasInitialPrompt: true, language: 'en', estimatedSeconds: 1 })).toBe(rate);
    const audio = wav(60, 8000);
    const reservation = providerAudioReservation('assemblyai', audio.byteLength, 'dictation');
    expect(reservation.requiresBufferedBody).toBe(true);
    expect(reservation.resolveBufferedAudio(audio, 'audio/wav')).toEqual({ kind: 'duration', audioSeconds: 60 });
    expect(providerAudioReservation('assemblyai', audio.byteLength, 'universal-2').requiresBufferedBody).toBe(false);
  });
});
