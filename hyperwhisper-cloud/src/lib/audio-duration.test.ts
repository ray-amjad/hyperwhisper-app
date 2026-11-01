import { describe, expect, test } from 'bun:test';
import { estimateAudioSecondsFromSize, reservationAudioSeconds } from './audio-duration';
import { MAX_COMPRESSED_BYTES_PER_SECOND, RESERVATION_FALLBACK_BYTES_PER_SECOND } from './constants';
import { concat, oggPage, opusHead, opusOgg, opusTags } from '../test-support/ogg';
import { pcm16Wav } from '../test-support/wav';

describe('reservationAudioSeconds', () => {
  test('an hour of 8 kbps Opus reserves an hour, not the 450 s 64 kbps gave (#2483)', () => {
    expect(reservationAudioSeconds(opusOgg({ seconds: 3600 }))).toBeGreaterThanOrEqual(3599);
  });

  test('a WAV reserves its exact length, well under the 8 kbps size estimate', () => {
    const audio = pcm16Wav(60);
    expect(reservationAudioSeconds(audio)).toBe(60);
    expect(estimateAudioSecondsFromSize(audio.byteLength)).toBeGreaterThan(1000);
  });

  test('a header claiming less than the format can carry is floored by size', () => {
    // A valid OpusHead, then 1 MB of "packets" whose TOCs claim 2.5 ms each.
    // Size ÷ the compressed ceiling is longer than that, so the floor wins.
    const packet = new Uint8Array(250);
    packet[0] = 16 << 3; // CELT 2.5 ms, one frame
    const pages = [
      oggPage({ serial: 1, flags: 0x02, granule: 0, packets: [opusHead(0)] }),
      oggPage({ serial: 1, flags: 0, granule: 0, packets: [opusTags()] }),
    ];
    for (let index = 0; index < 4_000; index++) pages.push(oggPage({ serial: 1, flags: 0, granule: 0, packets: [packet] }));
    const audio = concat(pages);
    expect(reservationAudioSeconds(audio)).toBeCloseTo(audio.byteLength / MAX_COMPRESSED_BYTES_PER_SECOND, 6);
  });

  test('unrecognised or unreadable audio falls back to 8 kbps', () => {
    const garbage = new Uint8Array(500_000).fill(0x5A).buffer;
    expect(reservationAudioSeconds(garbage)).toBe(500_000 / RESERVATION_FALLBACK_BYTES_PER_SECOND);
    // Recognised as Ogg, but no codec header: unreadable.
    const headless = concat([oggPage({ serial: 1, flags: 0x02, granule: 0, packets: [new Uint8Array(60_000)] })]);
    expect(reservationAudioSeconds(headless)).toBe(headless.byteLength / RESERVATION_FALLBACK_BYTES_PER_SECOND);
  });

  test('never reserves under the 10 s minimum', () => {
    expect(reservationAudioSeconds(pcm16Wav(1))).toBe(10);
    expect(reservationAudioSeconds(new ArrayBuffer(0))).toBe(10);
  });
});
