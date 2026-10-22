import { describe, expect, test } from 'bun:test';
import { ProviderInputError, UnsupportedAudioFormatError } from '../providers/types';
import { parsePcm16Wav, type Pcm16WavPolicy } from './wav-header';

const ANY: Pcm16WavPolicy = {
  provider: 'Test',
  acceptedFormats: ['wav'],
  maxDurationSeconds: 60,
  tooLongMessage: 'too long for Test',
};
const STRICT: Pcm16WavPolicy = { ...ANY, channels: 1, sampleRates: [16_000, 24_000] };

interface Chunk { id: string; body: Uint8Array }

function fmt(rate = 16_000, channels = 1, overrides: Partial<Record<'audioFormat' | 'byteRate' | 'blockAlign' | 'bits', number>> = {}): Chunk {
  const body = new Uint8Array(16);
  const view = new DataView(body.buffer);
  const bits = overrides.bits ?? 16;
  view.setUint16(0, overrides.audioFormat ?? 1, true);
  view.setUint16(2, channels, true);
  view.setUint32(4, rate, true);
  view.setUint32(8, overrides.byteRate ?? rate * channels * (bits / 8), true);
  view.setUint16(12, overrides.blockAlign ?? channels * (bits / 8), true);
  view.setUint16(14, bits, true);
  return { id: 'fmt ', body };
}

const data = (bytes: number): Chunk => ({ id: 'data', body: new Uint8Array(bytes) });

/** Build a RIFF/WAVE file from chunks, padding odd-sized chunks as RIFF requires. */
function riff(chunks: Chunk[], riffSizeDelta = 0): ArrayBuffer {
  const length = 12 + chunks.reduce((sum, chunk) => sum + 8 + chunk.body.length + (chunk.body.length & 1), 0);
  const out = new Uint8Array(length);
  const view = new DataView(out.buffer);
  const ascii = (offset: number, value: string) => [...value].forEach((char, index) => view.setUint8(offset + index, char.charCodeAt(0)));
  ascii(0, 'RIFF');
  view.setUint32(4, length - 8 + riffSizeDelta, true);
  ascii(8, 'WAVE');
  let offset = 12;
  for (const chunk of chunks) {
    ascii(offset, chunk.id);
    view.setUint32(offset + 4, chunk.body.length, true);
    out.set(chunk.body, offset + 8);
    offset += 8 + chunk.body.length + (chunk.body.length & 1);
  }
  return out.buffer;
}

const canonical = (seconds = 1, rate = 16_000, channels = 1) => riff([fmt(rate, channels), data(seconds * rate * channels * 2)]);

function expectUnsupported(audio: ArrayBuffer, policy = ANY): void {
  let thrown: unknown;
  try { parsePcm16Wav(audio, 'audio/x-test', policy); } catch (error) { thrown = error; }
  expect(thrown).toBeInstanceOf(UnsupportedAudioFormatError);
  const error = thrown as UnsupportedAudioFormatError;
  expect(error.message).toBe('Test does not accept audio/x-test; accepts wav');
  expect(error.contentType).toBe('audio/x-test');
  expect(error.acceptedFormats).toEqual(['wav']);
}

describe('parsePcm16Wav chunk walk', () => {
  test('canonical PCM16 WAV reports exact duration, rate and channels', () => {
    expect(parsePcm16Wav(canonical(2, 24_000, 2), 'audio/wav', ANY)).toEqual({ durationSeconds: 2, sampleRate: 24_000, channels: 2 });
  });

  test('unknown chunks are skipped, odd sizes are padded, data may come first', () => {
    const junk = { id: 'JUNK', body: new Uint8Array(7) };
    expect(parsePcm16Wav(riff([junk, fmt(), junk, data(32_000)]), 'audio/wav', STRICT).durationSeconds).toBe(1);
    expect(parsePcm16Wav(riff([data(32_000), fmt()]), 'audio/wav', STRICT).durationSeconds).toBe(1);
  });

  test('a fmt chunk longer than 16 bytes is accepted', () => {
    const extended = fmt();
    const body = new Uint8Array(18);
    body.set(extended.body);
    expect(parsePcm16Wav(riff([{ id: 'fmt ', body }, data(32_000)]), 'audio/wav', ANY).durationSeconds).toBe(1);
  });

  test('broken RIFF framing is unsupported', () => {
    const notRiff = canonical(); new DataView(notRiff).setUint8(0, 0x58);
    const notWave = canonical(); new DataView(notWave).setUint8(8, 0x58);
    const overrun = riff([fmt(), data(32_000)]); new DataView(overrun).setUint32(40, 32_002, true);
    const oddUnpadded = riff([fmt(), data(32_000), { id: 'JUNK', body: new Uint8Array(7) }]).slice(0, -1);
    new DataView(oddUnpadded).setUint32(4, oddUnpadded.byteLength - 8, true);
    // Four stray bytes after the last chunk: too short for another chunk header.
    const truncatedHeader = new Uint8Array(canonical().byteLength + 4);
    truncatedHeader.set(new Uint8Array(canonical()));
    new DataView(truncatedHeader.buffer).setUint32(4, truncatedHeader.byteLength - 8, true);
    for (const audio of [
      new ArrayBuffer(0), new ArrayBuffer(11), notRiff, notWave,
      riff([fmt(), data(32_000)], 2), riff([fmt(), data(32_000)], -2),
      canonical().slice(0, -1), overrun, oddUnpadded,
      truncatedHeader.buffer,
    ]) expectUnsupported(audio);
  });

  test('missing, duplicate, short or empty chunks are unsupported', () => {
    for (const audio of [
      riff([data(32_000)]), riff([fmt()]), riff([fmt(), data(0)]),
      riff([fmt(), fmt(), data(32_000)]), riff([fmt(), data(16_000), data(16_000)]),
      riff([{ id: 'fmt ', body: fmt().body.slice(0, 14) }, data(32_000)]),
    ]) expectUnsupported(audio);
  });

  test('non-PCM16 or inconsistent format fields are unsupported', () => {
    for (const audio of [
      riff([fmt(16_000, 1, { audioFormat: 3 }), data(32_000)]),
      riff([fmt(16_000, 1, { bits: 24 }), data(48_000)]),
      riff([fmt(16_000, 0), data(32_000)]),
      riff([fmt(0, 1), data(32_000)]),
      riff([fmt(16_000, 1, { blockAlign: 4 }), data(32_000)]),
      riff([fmt(16_000, 1, { byteRate: 16_000 }), data(32_000)]),
      riff([fmt(), data(32_001)]),
    ]) expectUnsupported(audio);
  });
});

describe('parsePcm16Wav policy', () => {
  test('no channel or rate rule accepts any count and rate >= 1', () => {
    for (const [rate, channels] of [[8_000, 1], [44_100, 2], [48_000, 6], [1, 1]] as const) {
      expect(parsePcm16Wav(canonical(1, rate, channels), 'audio/wav', ANY).durationSeconds).toBe(1);
    }
  });

  test('channel and rate rules reject everything else', () => {
    expect(parsePcm16Wav(canonical(1, 24_000), 'audio/wav', STRICT).sampleRate).toBe(24_000);
    for (const audio of [canonical(1, 16_000, 2), canonical(1, 8_000), canonical(1, 44_100)]) expectUnsupported(audio, STRICT);
  });

  test('the duration cap is inclusive and throws the policy message', () => {
    expect(parsePcm16Wav(canonical(60), 'audio/wav', ANY).durationSeconds).toBe(60);
    let thrown: unknown;
    try { parsePcm16Wav(riff([fmt(), data(60 * 32_000 + 2)]), 'audio/wav', ANY); } catch (error) { thrown = error; }
    expect(thrown).toBeInstanceOf(ProviderInputError);
    expect(thrown).toMatchObject({ status: 400, message: 'Test rejected input (400): too long for Test' });
  });
});
