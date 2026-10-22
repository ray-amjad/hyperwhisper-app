import { describe, expect, test } from 'bun:test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { opusPacketSamples, readAudioContainerDuration } from './audio-container-duration';
import { MAX_COMPRESSED_BYTES_PER_SECOND, MAX_UPLOAD_BYTES_PER_SECOND } from './constants';
import { concat, oggPage, opusOgg, opusStreamPages } from '../test-support/ogg';
import { pcm16Wav, wav } from '../test-support/wav';

// Three seconds of a 440 Hz tone (silence for the lossless ones), made with
// ffmpeg 7 from `-f lavfi -i sine=...:duration=3:sample_rate=16000 -ac 1`.
const FIXTURES = join(import.meta.dir, '../test-support/audio-fixtures');

function fixture(name: string): ArrayBuffer {
  const bytes = readFileSync(join(FIXTURES, name));
  return bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength);
}

function durationOf(audio: ArrayBuffer): number | undefined {
  return readAudioContainerDuration(audio)?.durationSeconds;
}

describe('readAudioContainerDuration: real files', () => {
  test.each([
    ['opus-8kbps-3s.ogg', 'ogg'],
    ['vorbis-3s.ogg', 'ogg'],
    ['mp3-16kbps-id3-3s.mp3', 'mp3'],
    ['aac-16kbps-moov-last-3s.m4a', 'mp4'],
    ['aac-16kbps-moov-first-3s.m4a', 'mp4'],
    ['aac-adts-3s.aac', 'adts'],
    ['flac-silence-3s.flac', 'flac'],
  ] as const)('%s reads as about 3 s of %s', (name, container) => {
    const parsed = readAudioContainerDuration(fixture(name));
    expect(parsed?.container).toBe(container);
    expect(parsed?.durationSeconds).toBeGreaterThanOrEqual(2.95);
    expect(parsed?.durationSeconds).toBeLessThanOrEqual(3.2);
  });

  test('a WAV is its PCM payload over the format byte rate', () => {
    expect(readAudioContainerDuration(pcm16Wav(3))).toEqual({
      container: 'wav',
      durationSeconds: 3,
      maxBytesPerSecond: MAX_UPLOAD_BYTES_PER_SECOND,
    });
    // 48 kHz stereo float32, at the highest accepted byte rate.
    expect(durationOf(wav({ formatTag: 3, channels: 2, sampleRate: 48_000, bitsPerSample: 32, dataBytes: 384_000 * 2 }))).toBe(2);
    // Any rate, and the extensible format tag.
    expect(durationOf(wav({ formatTag: 0xFFFE, sampleRate: 22_050, dataBytes: 22_050 * 2 * 4 }))).toBe(4);
  });

  test('lossy containers report the compressed byte-rate ceiling', () => {
    expect(readAudioContainerDuration(fixture('opus-8kbps-3s.ogg'))?.maxBytesPerSecond).toBe(MAX_COMPRESSED_BYTES_PER_SECOND);
    expect(readAudioContainerDuration(fixture('flac-silence-3s.flac'))?.maxBytesPerSecond).toBe(MAX_UPLOAD_BYTES_PER_SECOND);
  });

  test('WebM is not recognised, and fragmented or non-AAC MP4 is unreadable', () => {
    expect(readAudioContainerDuration(fixture('opus-3s.webm'))).toBeUndefined();
    expect(readAudioContainerDuration(fixture('aac-fragmented-3s.m4a'))).toMatchObject({ container: 'mp4', durationSeconds: undefined });
    expect(readAudioContainerDuration(fixture('alac-silence-3s.m4a'))).toMatchObject({ container: 'mp4', durationSeconds: undefined });
  });

  test('the Content-Type plays no part: only magic bytes', () => {
    expect(readAudioContainerDuration(new TextEncoder().encode('RIFF but not really a wave file').buffer)).toBeUndefined();
    expect(readAudioContainerDuration(new ArrayBuffer(0))).toBeUndefined();
  });
});

describe('readAudioContainerDuration: Ogg Opus', () => {
  test('counts the TOC frames of an hour of 8 kbps Opus', () => {
    const audio = opusOgg({ seconds: 3600 });
    expect(audio.byteLength).toBeLessThan(4_000_000);
    expect(durationOf(audio)).toBeCloseTo(3600, 1);
  });

  test('a tampered granule position cannot shorten it: the packets are counted', () => {
    expect(durationOf(opusOgg({ seconds: 600, granule: () => 48_000 }))).toBeCloseTo(600, 1);
  });

  test('chained streams are summed', () => {
    const audio = concat([
      ...opusStreamPages({ seconds: 2, serial: 1 }),
      ...opusStreamPages({ seconds: 300, serial: 2 }),
    ]);
    expect(durationOf(audio)).toBeCloseTo(302, 1);
  });

  test('a second stream of an unknown codec makes the file unreadable', () => {
    const speex = new Uint8Array(80);
    speex.set([...'Speex   '].map((char) => char.charCodeAt(0)), 0);
    const audio = concat([
      oggPage({ serial: 9, flags: 0x02, granule: 0, packets: [speex] }),
      ...opusStreamPages({ seconds: 2 }),
    ]);
    expect(readAudioContainerDuration(audio)).toMatchObject({ container: 'ogg', durationSeconds: undefined });
  });

  test('a truncated stream counts what arrived', () => {
    const audio = opusOgg({ seconds: 100 });
    const half = audio.slice(0, Math.floor(audio.byteLength / 2) + 7);
    expect(durationOf(half)).toBeGreaterThan(45);
    expect(durationOf(half)).toBeLessThan(55);
  });

  test('frames per packet come from the TOC byte', () => {
    expect(opusPacketSamples(0x08, undefined)).toBe(960); // SILK 20 ms, one frame
    expect(opusPacketSamples(0x08 | 1, undefined)).toBe(1920); // two frames
    expect(opusPacketSamples((19 << 3) | 3, 6)).toBe(5760); // CELT 20 ms × 6 = 120 ms
    expect(opusPacketSamples((19 << 3) | 3, 7)).toBe(0); // over 120 ms is invalid
    expect(opusPacketSamples((16 << 3) | 3, undefined)).toBe(0);
  });
});

describe('readAudioContainerDuration: tampered headers', () => {
  test('a WAV with a zero or streaming data size uses the bytes present', () => {
    for (const declared of [0, 0xFFFFFFFF]) {
      const audio = pcm16Wav(5);
      new DataView(audio).setUint32(40, declared, true);
      expect(durationOf(audio)).toBe(5);
    }
  });

  test('a WAV declaring a small data chunk is still measured by its payload', () => {
    const audio = pcm16Wav(30);
    new DataView(audio).setUint32(40, 32_000, true);
    expect(durationOf(audio)).toBe(30);
  });

  test('a WAV cannot declare a byte rate above the highest accepted one', () => {
    const audio = wav({ sampleRate: 4_000_000, dataBytes: MAX_UPLOAD_BYTES_PER_SECOND * 3 });
    expect(durationOf(audio)).toBe(3);
  });

  test('FLAC frames are counted even when STREAMINFO declares one sample', () => {
    const audio = fixture('flac-silence-3s.flac');
    const bytes = new Uint8Array(audio);
    // STREAMINFO body starts at 8; the 36-bit total is in bytes 13-17 of it.
    bytes[8 + 13] &= 0xF0;
    new DataView(audio).setUint32(8 + 14, 1);
    expect(durationOf(audio)).toBe(3);
  });

  test('FLAC with no verifiable frame is unknown, not STREAMINFO\'s declared total', () => {
    const audio = fixture('flac-silence-3s.flac');
    const bytes = new Uint8Array(audio);
    // Keep fLaC + STREAMINFO (4 + 4 + 34 bytes), zero every frame after it.
    bytes.fill(0, 42);
    // Declare a single sample.
    bytes[8 + 13] &= 0xF0;
    new DataView(audio).setUint32(8 + 14, 1);
    expect(readAudioContainerDuration(audio)).toMatchObject({ container: 'flac', durationSeconds: undefined });
  });

  test('Ogg Opus with no countable packet is unknown, not the last granule', () => {
    // Every audio packet is an invalid code-3 packet (no frame-count byte), so
    // none is counted; the granules claim one second.
    const pages = opusStreamPages({ seconds: 600, granule: () => 48_000 });
    const audio = new Uint8Array(concat(pages));
    let offset = 0;
    for (const [index, page] of pages.entries()) {
      if (index >= 2) {
        const segments = page[26];
        let body = offset + 27 + segments;
        for (let segment = 0; segment < segments; segment++) {
          audio[body] |= 3;
          body += page[27 + segment];
        }
      }
      offset += page.length;
    }
    expect(readAudioContainerDuration(audio.buffer)).toMatchObject({ container: 'ogg', durationSeconds: undefined });
  });

  test('an M4A whose stts and mdhd claim a millisecond is measured by its AAC frames', () => {
    const audio = fixture('aac-16kbps-moov-last-3s.m4a');
    const bytes = new Uint8Array(audio);
    const view = new DataView(audio);
    const find = (type: string) => {
      const needle = [...type].map((char) => char.charCodeAt(0));
      for (let index = 0; index < bytes.length - 4; index++) {
        if (needle.every((byte, offset) => bytes[index + offset] === byte)) return index;
      }
      throw new Error(`no ${type}`);
    };
    const stts = find('stts');
    for (let entry = 0; entry < view.getUint32(stts + 8); entry++) view.setUint32(stts + 16 + entry * 8, 0);
    const mdhd = find('mdhd');
    view.setUint32(mdhd + 20, 16); // duration
    const mvhd = find('mvhd');
    view.setUint32(mvhd + 20, 1);
    expect(durationOf(audio)).toBeGreaterThanOrEqual(2.95);
  });
});

describe('readAudioContainerDuration: hostile input never throws', () => {
  const names = [
    'opus-8kbps-3s.ogg', 'vorbis-3s.ogg', 'mp3-16kbps-id3-3s.mp3', 'aac-16kbps-moov-last-3s.m4a',
    'aac-16kbps-moov-first-3s.m4a', 'aac-adts-3s.aac', 'flac-silence-3s.flac', 'aac-fragmented-3s.m4a',
  ];

  test('every truncation of every fixture', () => {
    for (const name of names) {
      const audio = fixture(name);
      for (let length = 0; length < audio.byteLength; length += 97) {
        const parsed = readAudioContainerDuration(audio.slice(0, length));
        const seconds = parsed?.durationSeconds;
        if (seconds !== undefined) expect(Number.isFinite(seconds) && seconds >= 0).toBe(true);
      }
    }
  });

  test('fixtures with random bytes overwritten', () => {
    let seed = 2483;
    const random = () => {
      seed = (seed * 1_103_515_245 + 12_345) % 2 ** 31;
      return seed / 2 ** 31;
    };
    for (const name of names) {
      for (let round = 0; round < 50; round++) {
        const bytes = new Uint8Array(fixture(name));
        for (let hit = 0; hit < 20; hit++) bytes[Math.floor(random() * bytes.length)] = Math.floor(random() * 256);
        const seconds = readAudioContainerDuration(bytes.buffer)?.durationSeconds;
        if (seconds !== undefined) expect(Number.isFinite(seconds)).toBe(true);
      }
    }
  });

  test.each([
    ['fLaC', 'flac'],
    ['OggS', 'ogg'],
    ['ID3\u0003\u0000\u0000\u0000\u0000\u0000\u0000', undefined],
    ['\u0000\u0000\u0000\u0010ftypM4A ', 'mp4'],
  ] as const)('a 32 MB %j body of 0xFF bytes is scanned quickly', (magic, container) => {
    const bytes = new Uint8Array(32 * 1024 * 1024).fill(0xFF);
    bytes.set([...magic].map((char) => char.charCodeAt(0)), 0);
    const started = performance.now();
    const parsed = readAudioContainerDuration(bytes.buffer);
    expect(performance.now() - started).toBeLessThan(2_000);
    expect(parsed?.container).toBe(container);
  });
});
