/**
 * The shared WAV fixture builder for the cloud tests that need a whole upload.
 * `src/lib/wav-header.test.ts` keeps its own chunk-list builder, because the
 * parser's unit tests need arbitrary chunk orders and sizes. Test code only:
 * nothing in `src/index.ts` imports this module.
 */

export interface WavOptions {
  formatTag?: number;
  channels?: number;
  sampleRate?: number;
  bitsPerSample?: number;
  /** Size of the `data` chunk. Defaults to one second of audio. */
  dataBytes?: number;
  /** Size of a `JUNK` chunk written before `data`; an odd size gets its pad byte. */
  junkBytes?: number;
}

function writeAscii(view: DataView, offset: number, value: string): void {
  for (let index = 0; index < value.length; index++) {
    view.setUint8(offset + index, value.charCodeAt(index));
  }
}

/** A RIFF/WAVE file: `fmt `, an optional `JUNK` chunk, then a zero-filled `data` chunk. */
export function wav(options: WavOptions = {}): ArrayBuffer {
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

/** A canonical 44-byte-header PCM16 WAV holding `seconds` of audio. */
export function pcm16Wav(seconds = 1, sampleRate = 16_000, channels = 1): ArrayBuffer {
  return wav({ sampleRate, channels, dataBytes: Math.round(seconds * sampleRate) * channels * 2 });
}
