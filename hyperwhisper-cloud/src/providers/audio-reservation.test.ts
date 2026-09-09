import { describe, expect, test } from 'bun:test';
import { providerAudioReservation } from './audio-reservation';

function wav(seconds: number, sampleRate: 16_000 | 24_000): ArrayBuffer {
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

describe('providerAudioReservation', () => {
  test('a normal provider uses the standard size estimate without buffering', () => {
    const reservation = providerAudioReservation('deepgram', 80_000);

    expect(reservation.requiresBufferedBody).toBe(false);
    expect(reservation.estimatedAudioSeconds).toBe(10);
    expect(reservation.preBufferAudioSeconds).toBe(10);
    expect(reservation.resolveBufferedAudio(new ArrayBuffer(1), 'audio/wav')).toEqual({
      kind: 'duration',
      audioSeconds: 10,
    });
  });

  test.each([16_000, 24_000] as const)(
    'resolves the exact duration of a valid %i Hz Meta WAV',
    (sampleRate) => {
      const audio = wav(60, sampleRate);
      const reservation = providerAudioReservation('meta', audio.byteLength);

      expect(reservation.requiresBufferedBody).toBe(true);
      expect(reservation.estimatedAudioSeconds).toBe(
        Math.max(10, (audio.byteLength - 44) / (16_000 * 2)),
      );
      expect(reservation.preBufferAudioSeconds).toBe((audio.byteLength - 44) / (24_000 * 2));
      expect(reservation.resolveBufferedAudio(audio, 'audio/wav')).toEqual({
        kind: 'duration',
        audioSeconds: 60,
      });
    },
  );

  test('maps a Meta format error to a provider-neutral local input outcome', () => {
    const audio = new ArrayBuffer(44);
    const reservation = providerAudioReservation('meta', audio.byteLength);

    expect(reservation.resolveBufferedAudio(audio, 'audio/wav')).toEqual({
      kind: 'local-input-error',
    });
  });

  test('maps a Meta duration error to a provider-neutral local input outcome', () => {
    const audio = wav(601, 16_000);
    const reservation = providerAudioReservation('meta', audio.byteLength);

    expect(reservation.resolveBufferedAudio(audio, 'audio/wav')).toEqual({
      kind: 'local-input-error',
    });
  });
});
