import { describe, expect, test } from 'bun:test';
import { providerAudioReservation } from './audio-reservation';
import { pcm16Wav } from '../test-support/wav';

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
      const audio = pcm16Wav(60, sampleRate);
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
    const audio = pcm16Wav(601, 16_000);
    const reservation = providerAudioReservation('meta', audio.byteLength);

    expect(reservation.resolveBufferedAudio(audio, 'audio/wav')).toEqual({
      kind: 'local-input-error',
    });
  });
});
