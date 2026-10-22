// PCM16 WAV HEADER PARSER
// One RIFF chunk walk for every provider that needs the exact duration of an uploaded WAV.
// It reads bytes a caller uploads, so harden it here, once. Providers pass only their policy.

import { ProviderInputError, UnsupportedAudioFormatError } from '../providers/types';

export interface Pcm16WavPolicy {
  /** Provider label used in the thrown errors. */
  provider: string;
  /** The UnsupportedAudioFormatError third argument. */
  acceptedFormats: readonly string[];
  /** Exact channel count, or undefined for any count >= 1. */
  channels?: number;
  /** Allowed sample rates, or undefined for any rate >= 1. */
  sampleRates?: readonly number[];
  maxDurationSeconds: number;
  /** The exact ProviderInputError message when the audio is longer than the cap. */
  tooLongMessage: string;
}

export interface Pcm16WavHeader {
  durationSeconds: number;
  sampleRate: number;
  channels: number;
}

function ascii(view: DataView, offset: number, length: number): string {
  let value = '';
  for (let index = 0; index < length; index++) {
    value += String.fromCharCode(view.getUint8(offset + index));
  }
  return value;
}

/** Inspect the actual RIFF chunks, never a MIME label or a byte-size estimate. */
export function parsePcm16Wav(audio: ArrayBuffer, contentType: string, policy: Pcm16WavPolicy): Pcm16WavHeader {
  const unsupported = () => new UnsupportedAudioFormatError(policy.provider, contentType, policy.acceptedFormats);
  if (audio.byteLength < 12) throw unsupported();

  const view = new DataView(audio);
  if (ascii(view, 0, 4) !== 'RIFF' || ascii(view, 8, 4) !== 'WAVE') {
    throw unsupported();
  }

  const riffEnd = view.getUint32(4, true) + 8;
  if (riffEnd !== audio.byteLength) throw unsupported();

  let offset = 12;
  let format: {
    audioFormat: number;
    channels: number;
    sampleRate: number;
    byteRate: number;
    blockAlign: number;
    bitsPerSample: number;
  } | undefined;
  let dataBytes: number | undefined;

  while (offset < riffEnd) {
    if (offset + 8 > riffEnd) throw unsupported();
    const chunkId = ascii(view, offset, 4);
    const chunkSize = view.getUint32(offset + 4, true);
    const chunkDataStart = offset + 8;
    const chunkDataEnd = chunkDataStart + chunkSize;
    const paddedChunkEnd = chunkDataEnd + (chunkSize & 1);
    if (chunkDataEnd > riffEnd || paddedChunkEnd > riffEnd) throw unsupported();

    if (chunkId === 'fmt ') {
      if (format || chunkSize < 16) throw unsupported();
      format = {
        audioFormat: view.getUint16(chunkDataStart, true),
        channels: view.getUint16(chunkDataStart + 2, true),
        sampleRate: view.getUint32(chunkDataStart + 4, true),
        byteRate: view.getUint32(chunkDataStart + 8, true),
        blockAlign: view.getUint16(chunkDataStart + 12, true),
        bitsPerSample: view.getUint16(chunkDataStart + 14, true),
      };
    } else if (chunkId === 'data') {
      if (dataBytes !== undefined) throw unsupported();
      dataBytes = chunkSize;
    }

    offset = paddedChunkEnd;
  }

  if (!format || dataBytes === undefined || dataBytes === 0) throw unsupported();
  const expectedBlockAlign = format.channels * (format.bitsPerSample / 8);
  const expectedByteRate = format.sampleRate * expectedBlockAlign;
  if (
    format.audioFormat !== 1
    || (policy.channels === undefined ? format.channels < 1 : format.channels !== policy.channels)
    || format.bitsPerSample !== 16
    || (policy.sampleRates === undefined ? format.sampleRate < 1 : !policy.sampleRates.includes(format.sampleRate))
    || format.blockAlign !== expectedBlockAlign
    || format.byteRate !== expectedByteRate
    || dataBytes % format.blockAlign !== 0
  ) {
    throw unsupported();
  }

  const durationSeconds = dataBytes / format.byteRate;
  if (durationSeconds > policy.maxDurationSeconds) {
    throw new ProviderInputError(policy.provider, 400, policy.tooLongMessage);
  }

  return { durationSeconds, sampleRate: format.sampleRate, channels: format.channels };
}
