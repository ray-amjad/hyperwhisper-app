import { describe, expect, test } from 'bun:test';
import { providerAudioReservation } from './audio-reservation';
import { pcm16Wav } from '../test-support/wav';
import { opusOgg } from '../test-support/ogg';
import { CREDITS_PER_MINUTE, MAX_UPLOAD_BYTES_PER_SECOND, RESERVATION_FALLBACK_BYTES_PER_SECOND } from '../lib/constants';
import { estimateCreditsFromSize } from '../middleware/credits';

describe('providerAudioReservation', () => {
  test('a normal provider buffers the body and reserves its container duration (#2483)', () => {
    const sizeBytes = 3_840_000;
    const reservation = providerAudioReservation('deepgram', sizeBytes);

    expect(reservation.requiresBufferedBody).toBe(true);
    // Size-only: 8 kbps. Before the body: the highest legitimate byte rate.
    expect(reservation.estimatedAudioSeconds).toBe(sizeBytes / RESERVATION_FALLBACK_BYTES_PER_SECOND);
    expect(reservation.preBufferAudioSeconds).toBe(sizeBytes / MAX_UPLOAD_BYTES_PER_SECOND);
    // A 60 s WAV of that size reserves 60 s, whatever its Content-Type says.
    const audio = pcm16Wav(60, 32_000);
    expect(audio.byteLength).toBe(sizeBytes + 44);
    expect(reservation.resolveBufferedAudio(audio, 'application/octet-stream')).toEqual({ kind: 'duration', audioSeconds: 60 });
  });

  test('unreadable audio on the default route still gets a duration, never a local input error', () => {
    const reservation = providerAudioReservation('grok', 100_000);

    expect(reservation.resolveBufferedAudio(new Uint8Array(100_000).fill(7).buffer, 'audio/ogg')).toEqual({
      kind: 'duration',
      audioSeconds: 100_000 / RESERVATION_FALLBACK_BYTES_PER_SECOND,
    });
    expect(reservation.resolveBufferedAudio(new ArrayBuffer(1), 'audio/wav')).toEqual({ kind: 'duration', audioSeconds: 10 });
  });

  test('an hour of 8 kbps Ogg Opus reserves an hour once its body is read', () => {
    const audio = opusOgg({ seconds: 3600 });
    const reservation = providerAudioReservation('grok', audio.byteLength);
    const resolved = reservation.resolveBufferedAudio(audio, 'audio/ogg');

    expect(resolved.kind === 'duration' && resolved.audioSeconds).toBeGreaterThanOrEqual(3599);
  });

  // Issue #2483's repro: the size-based preflight must not let a 30-credit
  // balance through for an hour of audio.
  test('an hour of 8 kbps Opus (3.6 MB) is estimated as an hour on the default route', () => {
    const oneHourAt8kbps = (8_000 / 8) * 3600;
    const hourCost = 60 * CREDITS_PER_MINUTE;

    expect(providerAudioReservation('grok', oneHourAt8kbps).estimatedAudioSeconds).toBeGreaterThanOrEqual(3600);
    expect(estimateCreditsFromSize(oneHourAt8kbps)).toBeGreaterThanOrEqual(hourCost);
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
