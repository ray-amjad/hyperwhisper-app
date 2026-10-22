// AUDIO CONTAINER DURATION
// Reads how long an uploaded file plays from its own bytes, so /transcribe can
// hold credits for the real duration rather than guess one from the byte count
// (#2483). The container is chosen by its magic bytes, never by the Content-Type.
//
// Every value here is attacker-controlled, so the rules are:
// - count what a decoder would play (Opus TOC frames, FLAC and MPEG frames, AAC
//   access units) rather than trust one declared total, wherever that is cheap;
// - every scan is bounded and linear in the upload size (up to 128 MB);
// - nothing throws: unreadable audio comes back as `durationSeconds: undefined`,
//   and an unrecognised container as `undefined`.
// The caller (lib/audio-duration.ts) then applies `maxBytesPerSecond` as a
// floor, so a header that under-states the length cannot shrink the hold far.

import { MAX_COMPRESSED_BYTES_PER_SECOND, MAX_UPLOAD_BYTES_PER_SECOND } from './constants';

export type AudioContainer = 'wav' | 'ogg' | 'flac' | 'mp4' | 'mp3' | 'adts';

export interface ContainerDuration {
  container: AudioContainer;
  /** How long the audio plays, or undefined when the container could not be read. */
  durationSeconds: number | undefined;
  /** The highest byte rate this audio could have: size ÷ this is its shortest plausible length. */
  maxBytesPerSecond: number;
}

/** Read the duration of a recognised container, or undefined for anything else (WebM included). */
export function readAudioContainerDuration(audio: ArrayBuffer): ContainerDuration | undefined {
  const bytes = new Uint8Array(audio);
  let container: AudioContainer | undefined;
  let maxBytesPerSecond = MAX_COMPRESSED_BYTES_PER_SECOND;
  try {
    const sniffed = sniff(bytes);
    if (!sniffed) return undefined;
    container = sniffed.container;
    switch (sniffed.container) {
      case 'wav':
        maxBytesPerSecond = MAX_UPLOAD_BYTES_PER_SECOND;
        return { container, durationSeconds: wavDuration(bytes), maxBytesPerSecond };
      case 'flac':
        maxBytesPerSecond = MAX_UPLOAD_BYTES_PER_SECOND;
        return { container, durationSeconds: flacDuration(bytes, sniffed.start), maxBytesPerSecond };
      case 'ogg':
        return { container, durationSeconds: oggDuration(bytes), maxBytesPerSecond };
      case 'mp4':
        return { container, durationSeconds: mp4Duration(bytes), maxBytesPerSecond };
      case 'mp3':
        return { container, durationSeconds: frameWalkDuration(bytes, sniffed.start, mpegFrame), maxBytesPerSecond };
      case 'adts':
        return { container, durationSeconds: frameWalkDuration(bytes, sniffed.start, adtsFrame), maxBytesPerSecond };
    }
  } catch {
    // A parser bug or a hostile layout must never fail the request: the caller
    // falls back to its low-bitrate reservation.
    return container ? { container, durationSeconds: undefined, maxBytesPerSecond } : undefined;
  }
}

// ---------------------------------------------------------------------------
// Byte helpers. All of them return 0 past the end rather than throw.

function u8(b: Uint8Array, p: number): number {
  return p >= 0 && p < b.length ? b[p] : 0;
}
function u16le(b: Uint8Array, p: number): number {
  return u8(b, p) | (u8(b, p + 1) << 8);
}
function u32le(b: Uint8Array, p: number): number {
  return (u8(b, p) | (u8(b, p + 1) << 8) | (u8(b, p + 2) << 16) | (u8(b, p + 3) << 24)) >>> 0;
}
function u16be(b: Uint8Array, p: number): number {
  return (u8(b, p) << 8) | u8(b, p + 1);
}
function u24be(b: Uint8Array, p: number): number {
  return (u8(b, p) << 16) | (u8(b, p + 1) << 8) | u8(b, p + 2);
}
function u32be(b: Uint8Array, p: number): number {
  return ((u8(b, p) << 24) | (u8(b, p + 1) << 16) | (u8(b, p + 2) << 8) | u8(b, p + 3)) >>> 0;
}
function u64be(b: Uint8Array, p: number): number {
  return u32be(b, p) * 2 ** 32 + u32be(b, p + 4);
}
function ascii(b: Uint8Array, p: number, length: number): string {
  let value = '';
  for (let index = 0; index < length; index++) value += String.fromCharCode(u8(b, p + index));
  return value;
}

/** A four-character code as the big-endian number u32be() reads for it. */
function fourcc(code: string): number {
  return ((code.charCodeAt(0) << 24) | (code.charCodeAt(1) << 16) | (code.charCodeAt(2) << 8) | code.charCodeAt(3)) >>> 0;
}

/** Offset just past an ID3v2 tag at `p`, or `p` when there is none. */
function skipId3v2(b: Uint8Array, p: number): number {
  if (ascii(b, p, 3) !== 'ID3' || p + 10 > b.length) return p;
  // The size is four 7-bit "syncsafe" bytes; a set footer flag adds 10 bytes.
  const size = ((u8(b, p + 6) & 0x7F) << 21) | ((u8(b, p + 7) & 0x7F) << 14)
    | ((u8(b, p + 8) & 0x7F) << 7) | (u8(b, p + 9) & 0x7F);
  const footer = u8(b, p + 5) & 0x10 ? 10 : 0;
  return p + 10 + size + footer;
}

const MP4_TOP_LEVEL_TYPES = new Set(['ftyp', 'moov', 'mdat', 'free', 'skip', 'wide']);

function sniff(b: Uint8Array): { container: AudioContainer; start: number } | undefined {
  const head = ascii(b, 0, 4);
  if ((head === 'RIFF' || head === 'RF64') && ascii(b, 8, 4) === 'WAVE') return { container: 'wav', start: 0 };
  if (head === 'OggS') return { container: 'ogg', start: 0 };
  if (MP4_TOP_LEVEL_TYPES.has(ascii(b, 4, 4)) && u32be(b, 0) >= 8) return { container: 'mp4', start: 0 };

  // FLAC, MP3 and ADTS may all sit behind an ID3v2 tag.
  const start = skipId3v2(b, 0);
  if (ascii(b, start, 4) === 'fLaC') return { container: 'flac', start };
  // Without a tag the first frame must open the file; after one, some taggers
  // leave padding, so allow a short search.
  const limit = start > 0 ? Math.min(b.length, start + SNIFF_SYNC_WINDOW_BYTES) : 1;
  const first = firstConfirmedFrame(b, start, mpegFrame, limit);
  if (first !== undefined) return { container: 'mp3', start: first };
  const firstAdts = firstConfirmedFrame(b, start, adtsFrame, limit);
  if (firstAdts !== undefined) return { container: 'adts', start: firstAdts };
  return undefined;
}

// ---------------------------------------------------------------------------
// WAV: the bytes after the `data` header divided by the format's byte rate.
// Every byte after the header counts as audio (covers streaming writers that
// leave the size at 0 or 0xFFFFFFFF, and a declared size smaller than the
// payload); trailing metadata chunks are small, so this only over-reserves.

const FMT_CHUNK = fourcc('fmt ');
const DATA_CHUNK = fourcc('data');
const WAVE_FORMAT_PCM = 1;
const WAVE_FORMAT_IEEE_FLOAT = 3;
const WAVE_FORMAT_ALAW = 6;
const WAVE_FORMAT_MULAW = 7;
const WAVE_FORMAT_EXTENSIBLE = 0xFFFE;
const UNCOMPRESSED_WAVE_FORMATS = new Set([
  WAVE_FORMAT_PCM, WAVE_FORMAT_IEEE_FLOAT, WAVE_FORMAT_ALAW, WAVE_FORMAT_MULAW, WAVE_FORMAT_EXTENSIBLE,
]);

function wavDuration(b: Uint8Array): number | undefined {
  let offset = 12;
  let byteRate: number | undefined;
  let dataStart: number | undefined;
  // Each chunk advances at least 8 bytes, so this walk is linear.
  while (offset + 8 <= b.length) {
    const id = u32be(b, offset);
    const size = u32le(b, offset + 4);
    const body = offset + 8;
    if (id === FMT_CHUNK && size >= 16) {
      const formatTag = u16le(b, body);
      const channels = u16le(b, body + 2);
      const sampleRate = u32le(b, body + 4);
      const declaredByteRate = u32le(b, body + 8);
      const bitsPerSample = u16le(b, body + 14);
      if (UNCOMPRESSED_WAVE_FORMATS.has(formatTag)) {
        // Uncompressed: derive the rate from the format, never the declared byteRate.
        const frameBytes = channels * Math.ceil(bitsPerSample / 8);
        if (frameBytes > 0 && sampleRate > 0) byteRate = sampleRate * frameBytes;
      } else if (declaredByteRate > 0) {
        // A codec inside RIFF (ADPCM, GSM, MP3): only its declared average rate is known.
        byteRate = declaredByteRate;
      }
    } else if (id === DATA_CHUNK) {
      dataStart = body;
      break;
    }
    offset = body + size + (size & 1);
  }
  if (byteRate === undefined || dataStart === undefined) return undefined;
  const audioBytes = Math.max(0, b.length - dataStart);
  return audioBytes / Math.min(byteRate, MAX_UPLOAD_BYTES_PER_SECOND);
}

// ---------------------------------------------------------------------------
// OGG: walk every page and reassemble packet boundaries from the lacing values.
// Opus: sum each audio packet's TOC-declared frames, which is exactly what a
// decoder plays, minus the pre-skip. Vorbis: the last granule over the sample
// rate, but never less than one short block per audio packet. Every logical
// stream is summed (chained files); a stream of any other codec makes the whole
// file unknown, because the bytes it carries might be the audio.

interface OggStream {
  packets: number;
  codec: 'opus' | 'vorbis' | 'skeleton' | 'other' | undefined;
  sampleRate: number;
  preSkip: number;
  /** Vorbis short block size, in samples. */
  shortBlock: number;
  opusSamples: number;
  vorbisAudioPackets: number;
  lastGranule: number;
  /** The packet being reassembled across segments, or undefined between packets. */
  pending: { length: number; b0: number; b1: number; start: number; contiguous: boolean } | undefined;
}

const OGG_CAPTURE = fourcc('OggS');
const MAX_CONCURRENT_OGG_STREAMS = 64;
/** An Opus packet never carries more than 120 ms (RFC 6716 §3.2.5). */
const OPUS_MAX_PACKET_SAMPLES = 5760;

/** 48 kHz samples in one Opus packet, from its TOC byte (and frame-count byte for code 3). */
export function opusPacketSamples(toc: number, second: number | undefined): number {
  const config = toc >> 3;
  let frameSamples: number;
  if (config < 12) frameSamples = [480, 960, 1920, 2880][config & 3];
  else if (config < 16) frameSamples = [480, 960][config & 1];
  else frameSamples = [120, 240, 480, 960][config & 3];
  const code = toc & 3;
  const frames = code === 0 ? 1 : code === 3 ? (second === undefined ? 0 : second & 0x3F) : 2;
  const samples = frames * frameSamples;
  return samples <= OPUS_MAX_PACKET_SAMPLES ? samples : 0;
}

function oggDuration(b: Uint8Array): number | undefined {
  const active = new Map<number, OggStream>();
  let totalSeconds = 0;
  let sawAudio = false;
  let unknown = false;

  const finish = (stream: OggStream) => {
    if (stream.codec === 'opus') {
      sawAudio = true;
      const samples = stream.opusSamples > 0
        ? stream.opusSamples
        : Math.max(0, stream.lastGranule);
      totalSeconds += Math.max(0, samples - stream.preSkip) / 48_000;
    } else if (stream.codec === 'vorbis') {
      sawAudio = true;
      const granuleSamples = Math.max(0, stream.lastGranule);
      const packetSamples = stream.vorbisAudioPackets * (stream.shortBlock / 2);
      totalSeconds += Math.max(granuleSamples, packetSamples) / stream.sampleRate;
    } else if (stream.codec === 'other' || (stream.codec === undefined && stream.packets > 0)) {
      unknown = true;
    }
  };

  const completePacket = (stream: OggStream) => {
    const packet = stream.pending!;
    stream.pending = undefined;
    const index = stream.packets++;
    if (index === 0) {
      stream.codec = 'other';
      if (!packet.contiguous) return;
      const id = ascii(b, packet.start, 8);
      if (id === 'OpusHead' && packet.length >= 19) {
        stream.codec = 'opus';
        stream.preSkip = u16le(b, packet.start + 10);
      } else if (u8(b, packet.start) === 1 && ascii(b, packet.start + 1, 6) === 'vorbis' && packet.length >= 30) {
        const sampleRate = u32le(b, packet.start + 12);
        const shortBlockExponent = u8(b, packet.start + 28) & 0x0F;
        // Vorbis I allows block sizes of 64..8192 samples.
        if (sampleRate > 0 && shortBlockExponent >= 6 && shortBlockExponent <= 13) {
          stream.codec = 'vorbis';
          stream.sampleRate = sampleRate;
          stream.shortBlock = 2 ** shortBlockExponent;
        }
      } else if (id === 'fishead\0') {
        stream.codec = 'skeleton';
      }
      return;
    }
    if (packet.length === 0) return;
    if (stream.codec === 'opus' && index >= 2) {
      // Packet 1 is OpusTags; every later packet is audio.
      stream.opusSamples += opusPacketSamples(packet.b0, packet.length > 1 ? packet.b1 : undefined);
    } else if (stream.codec === 'vorbis' && (packet.b0 & 1) === 0) {
      // Header packets have an odd type byte; audio packets start with a 0 bit.
      stream.vorbisAudioPackets++;
    }
  };

  let offset = 0;
  while (offset + 27 <= b.length) {
    if (u32be(b, offset) !== OGG_CAPTURE || u8(b, offset + 4) !== 0) {
      // Lost sync: scan forward for the next capture pattern, so this stays linear.
      const next = findOggS(b, offset + 1);
      if (next < 0) break;
      offset = next;
      continue;
    }
    const flags = u8(b, offset + 5);
    const granuleLow = u32le(b, offset + 6);
    const granuleHigh = u32le(b, offset + 10);
    const serial = u32le(b, offset + 14);
    const segments = u8(b, offset + 26);
    const lacingStart = offset + 27;
    let position = lacingStart + segments;
    if (position > b.length) break;

    let stream = active.get(serial);
    if (!stream) {
      if (active.size >= MAX_CONCURRENT_OGG_STREAMS) return undefined;
      stream = {
        packets: 0, codec: undefined, sampleRate: 0, preSkip: 0, shortBlock: 0,
        opusSamples: 0, vorbisAudioPackets: 0, lastGranule: -1, pending: undefined,
      };
      active.set(serial, stream);
    }

    const continued = (flags & 0x01) !== 0;
    // A continuation with nothing pending (or a fresh packet over a pending one)
    // means a page was lost: drop the broken packet rather than guess at it.
    let skipping = continued && !stream.pending;
    if (!continued) stream.pending = undefined;
    if (stream.pending) stream.pending.contiguous = false;

    for (let segment = 0; segment < segments; segment++) {
      const length = b[lacingStart + segment];
      if (position + length > b.length) break;
      if (skipping) {
        if (length < 255) skipping = false;
      } else {
        if (!stream.pending) stream.pending = { length: 0, b0: 0, b1: 0, start: position, contiguous: true };
        const pending = stream.pending;
        if (pending.length === 0 && length > 0) {
          pending.b0 = b[position];
          if (length > 1) pending.b1 = b[position + 1];
        } else if (pending.length === 1 && length > 0) {
          pending.b1 = b[position];
        }
        pending.length += length;
        if (length < 255) completePacket(stream);
      }
      position += length;
    }

    // All ones means no packet finished on this page.
    if (!(granuleLow === 0xFFFFFFFF && granuleHigh === 0xFFFFFFFF)) {
      stream.lastGranule = granuleHigh * 2 ** 32 + granuleLow;
    }
    if (flags & 0x04) {
      finish(stream);
      active.delete(serial);
    }
    offset = position;
  }
  for (const stream of active.values()) finish(stream);

  if (unknown || !sawAudio) return undefined;
  return totalSeconds;
}

function findOggS(b: Uint8Array, from: number): number {
  // A plain loop, not indexOf: it stays cheap when the data is dense with 'O'.
  for (let index = from; index + 4 <= b.length; index++) {
    if (b[index] === 0x4F && b[index + 1] === 0x67 && b[index + 2] === 0x67 && b[index + 3] === 0x53) return index;
  }
  return -1;
}

// ---------------------------------------------------------------------------
// FLAC: count the samples of every frame whose header passes its CRC-8, which
// is what a decoder plays. STREAMINFO's declared total is used only when no
// frame can be read. A false sync inside frame data has to pass the CRC too
// (about one in 50 million bytes), and it can only over-count.

const FLAC_MAX_HEADER_BYTES = 16;

const CRC8_TABLE = Uint8Array.from({ length: 256 }, (_, byte) => {
  let crc = byte;
  for (let bit = 0; bit < 8; bit++) crc = crc & 0x80 ? ((crc << 1) ^ 0x07) & 0xFF : (crc << 1) & 0xFF;
  return crc;
});

/** CRC-8 (polynomial 0x07) over b[start, end), as the FLAC frame header uses. */
function crc8(b: Uint8Array, start: number, end: number): number {
  let crc = 0;
  for (let index = start; index < end; index++) crc = CRC8_TABLE[crc ^ b[index]];
  return crc;
}

/** The block size of a valid FLAC frame header at `p`, in samples. */
function flacFrameSamples(b: Uint8Array, p: number): number | undefined {
  if (p + 6 > b.length || b[p] !== 0xFF || (b[p + 1] & 0xFE) !== 0xF8) return undefined;
  const blockCode = b[p + 2] >> 4;
  const rateCode = b[p + 2] & 0x0F;
  if (blockCode === 0 || rateCode === 15 || (b[p + 3] >> 4) > 10 || (b[p + 3] & 1) !== 0) return undefined;

  // The frame/sample number: a UTF-8-style varint of up to 7 bytes (36 bits).
  let q = p + 4;
  const lead = b[q];
  let extra: number;
  if (lead < 0x80) extra = 0;
  else if ((lead & 0xE0) === 0xC0) extra = 1;
  else if ((lead & 0xF0) === 0xE0) extra = 2;
  else if ((lead & 0xF8) === 0xF0) extra = 3;
  else if ((lead & 0xFC) === 0xF8) extra = 4;
  else if ((lead & 0xFE) === 0xFC) extra = 5;
  else if (lead === 0xFE) extra = 6;
  else return undefined;
  q++;
  for (let index = 0; index < extra; index++, q++) {
    if (q >= b.length || (b[q] & 0xC0) !== 0x80) return undefined;
  }

  let blockSize: number;
  if (blockCode === 1) blockSize = 192;
  else if (blockCode <= 5) blockSize = 576 * 2 ** (blockCode - 2);
  else if (blockCode === 6) { blockSize = u8(b, q) + 1; q += 1; }
  else if (blockCode === 7) { blockSize = u16be(b, q) + 1; q += 2; }
  else blockSize = 256 * 2 ** (blockCode - 8);
  if (rateCode === 12) q += 1;
  else if (rateCode === 13 || rateCode === 14) q += 2;

  if (q >= b.length || q - p > FLAC_MAX_HEADER_BYTES || crc8(b, p, q) !== b[q]) return undefined;
  return blockSize;
}

function flacDuration(b: Uint8Array, start: number): number | undefined {
  let offset = start + 4;
  let sampleRate = 0;
  let declaredSamples = 0;
  // Metadata blocks: each advances at least 4 bytes.
  while (offset + 4 <= b.length) {
    const header = b[offset];
    const length = u24be(b, offset + 1);
    if ((header & 0x7F) === 0 && length >= 34) {
      const info = offset + 4;
      sampleRate = (u8(b, info + 10) << 12) | (u8(b, info + 11) << 4) | (u8(b, info + 12) >> 4);
      declaredSamples = (u8(b, info + 13) & 0x0F) * 2 ** 32 + u32be(b, info + 14);
    }
    offset += 4 + length;
    if (header & 0x80) break;
  }
  if (sampleRate === 0) return undefined;

  let counted = 0;
  // A plain loop, not indexOf: it stays cheap when the data is dense with 0xFF.
  const end = b.length - 1;
  for (let candidate = Math.max(0, offset); candidate < end; candidate++) {
    // Cheap rejects before a parse: the second sync byte, then a reserved
    // block-size code (0) or an invalid sample-rate code (15).
    if (b[candidate] !== 0xFF || (b[candidate + 1] & 0xFE) !== 0xF8) continue;
    const codes = u8(b, candidate + 2);
    if ((codes & 0xF0) === 0 || (codes & 0x0F) === 0x0F) continue;
    const blockSize = flacFrameSamples(b, candidate);
    if (blockSize !== undefined) {
      counted += blockSize;
      // A frame is at least its header plus one byte per subframe; skip the header.
      candidate += 5;
    }
  }
  const samples = counted > 0 ? counted : declaredSamples;
  return samples > 0 ? samples / sampleRate : undefined;
}

// ---------------------------------------------------------------------------
// MPEG audio (MP3) and ADTS (raw AAC): walk the frames by their own lengths and
// sum each one's samples. A Xing/Info/VBRI header is not trusted for the count:
// walking the frames is what a decoder does, and it is cheap.

interface AudioFrame { length: number; seconds: number }
type FrameParser = (b: Uint8Array, p: number) => AudioFrame | undefined;

/** After an ID3v2 tag, the first frame must be this close for the sniffer to call the file MPEG/ADTS. */
const SNIFF_SYNC_WINDOW_BYTES = 64 * 1024;

/** kbps by [MPEG-1 ? 0 : 1][layer - 1][bitrate index]. */
const MPEG_BITRATES_KBPS: readonly (readonly (readonly number[])[])[] = [
  [
    [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448],
    [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384],
    [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320],
  ],
  [
    [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256],
    [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160],
    [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160],
  ],
];
const MPEG_SAMPLE_RATES: Record<number, readonly number[]> = {
  3: [44_100, 48_000, 32_000], // MPEG-1
  2: [22_050, 24_000, 16_000], // MPEG-2
  0: [11_025, 12_000, 8_000], // MPEG-2.5
};

function mpegFrame(b: Uint8Array, p: number): AudioFrame | undefined {
  if (p + 4 > b.length || b[p] !== 0xFF || (b[p + 1] & 0xE0) !== 0xE0) return undefined;
  const versionBits = (b[p + 1] >> 3) & 3;
  const layerBits = (b[p + 1] >> 1) & 3;
  const bitrateIndex = b[p + 2] >> 4;
  const rateIndex = (b[p + 2] >> 2) & 3;
  if (versionBits === 1 || layerBits === 0 || bitrateIndex === 0 || bitrateIndex === 15 || rateIndex === 3) {
    return undefined;
  }
  const layer = 4 - layerBits;
  const mpeg1 = versionBits === 3;
  const bitrate = MPEG_BITRATES_KBPS[mpeg1 ? 0 : 1][layer - 1][bitrateIndex] * 1000;
  const sampleRate = MPEG_SAMPLE_RATES[versionBits][rateIndex];
  const padding = (b[p + 2] >> 1) & 1;
  const samples = layer === 1 ? 384 : layer === 2 || mpeg1 ? 1152 : 576;
  const length = layer === 1
    ? (Math.floor((12 * bitrate) / sampleRate) + padding) * 4
    : Math.floor(((samples / 8) * bitrate) / sampleRate) + padding;
  return { length, seconds: samples / sampleRate };
}

const ADTS_SAMPLE_RATES = [96_000, 88_200, 64_000, 48_000, 44_100, 32_000, 24_000, 22_050, 16_000, 12_000, 11_025, 8_000, 7_350];

function adtsFrame(b: Uint8Array, p: number): AudioFrame | undefined {
  // 12-bit sync, then layer bits that must be 00 (which is what tells ADTS from MP3).
  if (p + 7 > b.length || b[p] !== 0xFF || (b[p + 1] & 0xF6) !== 0xF0) return undefined;
  const sampleRate = ADTS_SAMPLE_RATES[(b[p + 2] >> 2) & 0x0F];
  const length = ((b[p + 3] & 0x03) << 11) | (b[p + 4] << 3) | (b[p + 5] >> 5);
  if (sampleRate === undefined || length < 7) return undefined;
  const blocks = (b[p + 6] & 0x03) + 1;
  return { length, seconds: (blocks * 1024) / sampleRate };
}

/**
 * The first frame starting in [from, limit) whose successor is also a valid
 * frame (or which ends exactly at the end of the file). Two in a row rules out
 * a stray 0xFF.
 */
function firstConfirmedFrame(b: Uint8Array, from: number, parse: FrameParser, limit = b.length): number | undefined {
  // A plain loop, not indexOf: it stays cheap when the data is dense with 0xFF.
  const end = Math.min(limit, b.length);
  for (let candidate = Math.max(0, from); candidate < end; candidate++) {
    // Cheap rejects first: both headers need 0xFFE/0xFFF, and a third byte of
    // 0xFF is invalid in both.
    if (b[candidate] !== 0xFF || (u8(b, candidate + 1) & 0xE0) !== 0xE0 || u8(b, candidate + 2) === 0xFF) continue;
    const frame = parse(b, candidate);
    if (frame) {
      const frameEnd = candidate + frame.length;
      if (frameEnd === b.length || parse(b, frameEnd)) return candidate;
    }
  }
  return undefined;
}

function frameWalkDuration(b: Uint8Array, start: number, parse: FrameParser): number | undefined {
  let seconds = 0;
  let position: number | undefined = start;
  // Each step either advances by a whole frame (at least 7 bytes) or resyncs
  // forward past the failed position, so the walk is linear.
  while (position !== undefined && position < b.length) {
    const frame = parse(b, position);
    if (frame) {
      seconds += frame.seconds;
      position += frame.length;
    } else {
      position = firstConfirmedFrame(b, position + 1, parse);
    }
  }
  return seconds > 0 ? seconds : undefined;
}

// ---------------------------------------------------------------------------
// MP4 / M4A: the longest AAC ('mp4a') track. A decoder plays every access unit
// listed in the sample tables, and each is 1,024 samples at the AAC core rate,
// so the length is at least sample count × 1,024 / rate however small stts and
// mdhd claim it is. Other codecs (ALAC, PCM, Opus-in-MP4) and fragmented files
// (moof) are reported as unknown: their tables cannot be bounded this cheaply.

/** `type` is the box's four-character code, as fourcc() spells it. */
interface Mp4Box { type: number; body: number; end: number }

const BOX = {
  moov: fourcc('moov'), moof: fourcc('moof'), mvex: fourcc('mvex'), trak: fourcc('trak'), mdia: fourcc('mdia'),
  hdlr: fourcc('hdlr'), mdhd: fourcc('mdhd'), minf: fourcc('minf'), stbl: fourcc('stbl'), stsd: fourcc('stsd'),
  stts: fourcc('stts'), stsz: fourcc('stsz'), stz2: fourcc('stz2'), mp4a: fourcc('mp4a'), esds: fourcc('esds'),
  wave: fourcc('wave'), soun: fourcc('soun'),
} as const;

function mp4Box(b: Uint8Array, p: number, limit: number): Mp4Box | undefined {
  if (p + 8 > limit) return undefined;
  let size = u32be(b, p);
  const type = u32be(b, p + 4);
  let header = 8;
  if (size === 1) {
    if (p + 16 > limit) return undefined;
    size = u64be(b, p + 8);
    header = 16;
  } else if (size === 0) {
    size = limit - p;
  }
  if (size < header) return undefined;
  return { type, body: p + header, end: Math.min(p + size, limit) };
}

/** Each child box advances the walk by at least 8 bytes. */
function* mp4Children(b: Uint8Array, start: number, end: number): Generator<Mp4Box> {
  let p = start;
  while (p < end) {
    const box = mp4Box(b, p, end);
    if (!box) return;
    yield box;
    p = box.end;
  }
}

function mp4Child(b: Uint8Array, parent: Mp4Box, type: number): Mp4Box | undefined {
  for (const box of mp4Children(b, parent.body, parent.end)) if (box.type === type) return box;
  return undefined;
}

const AAC_SAMPLE_RATES = ADTS_SAMPLE_RATES;
const MAX_AAC_SAMPLE_RATE = 96_000;

/** The AudioSpecificConfig sample rate inside an esds box, if it can be found. */
function esdsSampleRate(b: Uint8Array, esds: Mp4Box): number | undefined {
  // Descriptors: tag byte, then a 1-4 byte length with a continuation bit.
  let p = esds.body + 4; // version + flags
  for (let guard = 0; guard < 16 && p < esds.end; guard++) {
    const tag = b[p++];
    let length = 0;
    for (let index = 0; index < 4 && p < esds.end; index++) {
      const byte = b[p++];
      length = (length << 7) | (byte & 0x7F);
      if (!(byte & 0x80)) break;
    }
    if (tag === 0x03) {
      const flags = u8(b, p + 2);
      p += 3 + (flags & 0x80 ? 2 : 0);
      if (flags & 0x40) p += 1 + u8(b, p);
      if (flags & 0x20) p += 2;
    } else if (tag === 0x04) {
      p += 13;
    } else if (tag === 0x05) {
      const index = ((u8(b, p) & 0x07) << 1) | (u8(b, p + 1) >> 7);
      if (index === 15) return (((u8(b, p + 1) & 0x7F) << 17) | (u8(b, p + 2) << 9) | (u8(b, p + 3) << 1) | (u8(b, p + 4) >> 7));
      return AAC_SAMPLE_RATES[index];
    } else {
      p += length;
    }
  }
  return undefined;
}

/** QuickTime sound description versions 1 and 2 extend the AudioSampleEntry. */
const SAMPLE_ENTRY_CHILD_OFFSET: Record<number, number> = { 0: 28, 1: 44, 2: 64 };
/** The lowest AAC sampling frequency (index 12). */
const MIN_AAC_SAMPLE_RATE = 7_350;

function findEsds(b: Uint8Array, start: number, end: number): Mp4Box | undefined {
  for (const box of mp4Children(b, start, end)) {
    if (box.type === BOX.esds) return box;
    // QuickTime nests it in a 'wave' box.
    if (box.type === BOX.wave) return findEsds(b, box.body, box.end);
  }
  return undefined;
}

/**
 * Seconds of a sound track: null when the track is not sound, undefined when
 * it is sound that cannot be measured (any codec but AAC, or no tables).
 */
function soundTrackSeconds(b: Uint8Array, trak: Mp4Box): number | null | undefined {
  const mdia = mp4Child(b, trak, BOX.mdia);
  if (!mdia) return null;
  const hdlr = mp4Child(b, mdia, BOX.hdlr);
  if (!hdlr || u32be(b, hdlr.body + 8) !== BOX.soun) return null;
  const mdhd = mp4Child(b, mdia, BOX.mdhd);
  const minf = mp4Child(b, mdia, BOX.minf);
  const stbl = minf ? mp4Child(b, minf, BOX.stbl) : undefined;
  if (!mdhd || !stbl) return undefined;

  const stsd = mp4Child(b, stbl, BOX.stsd);
  // stsd: version/flags, entry count, then the first sample entry box.
  const entry = stsd ? mp4Box(b, stsd.body + 8, stsd.end) : undefined;
  if (!entry || entry.type !== BOX.mp4a) return undefined;
  // AudioSampleEntry: 6 reserved + 2 index, a 2-byte version, 6 reserved,
  // 2 channels + 2 size + 4 reserved, then the rate as 16.16 fixed point.
  const childOffset = SAMPLE_ENTRY_CHILD_OFFSET[u16be(b, entry.body + 8)];
  if (childOffset === undefined) return undefined;
  const entryRate = u16be(b, entry.body + 24);
  const esds = findEsds(b, entry.body + childOffset, entry.end);
  const configRate = esds ? esdsSampleRate(b, esds) : undefined;
  // A version 2 entry stores a placeholder rate here, so drop impossible ones.
  const rates = [entryRate, configRate].filter((rate): rate is number => rate !== undefined && rate >= MIN_AAC_SAMPLE_RATE);
  if (rates.length === 0) return undefined;
  // The lower rate is the longer reading; HE-AAC signals its core rate here.
  const sampleRate = Math.min(MAX_AAC_SAMPLE_RATE, ...rates);

  const version = u8(b, mdhd.body);
  const timescale = u32be(b, mdhd.body + (version === 1 ? 20 : 12));

  let sampleCount = 0;
  let sttsTicks = 0;
  const stts = mp4Child(b, stbl, BOX.stts);
  if (stts) {
    const entries = Math.min(u32be(b, stts.body + 4), Math.floor((stts.end - stts.body - 8) / 8));
    for (let index = 0; index < entries; index++) {
      const count = u32be(b, stts.body + 8 + index * 8);
      sampleCount += count;
      sttsTicks += count * u32be(b, stts.body + 12 + index * 8);
    }
  }
  const stsz = mp4Child(b, stbl, BOX.stsz) ?? mp4Child(b, stbl, BOX.stz2);
  if (stsz) sampleCount = Math.max(sampleCount, u32be(b, stsz.body + 8));

  const frameSeconds = (sampleCount * 1024) / sampleRate;
  const tableSeconds = timescale > 0 ? sttsTicks / timescale : 0;
  const seconds = Math.max(frameSeconds, tableSeconds);
  return seconds > 0 ? seconds : undefined;
}

function mp4Duration(b: Uint8Array): number | undefined {
  let longest: number | undefined;
  let sawMoov = false;
  for (const box of mp4Children(b, 0, b.length)) {
    if (box.type === BOX.moof) return undefined;
    if (box.type !== BOX.moov) continue;
    sawMoov = true;
    for (const child of mp4Children(b, box.body, box.end)) {
      if (child.type === BOX.mvex) return undefined;
      if (child.type !== BOX.trak) continue;
      const seconds = soundTrackSeconds(b, child);
      if (seconds === null) continue;
      // A sound track this cannot measure might be the one that is played.
      if (seconds === undefined) return undefined;
      longest = Math.max(longest ?? 0, seconds);
    }
  }
  return sawMoov ? longest : undefined;
}
