/**
 * Ogg fixture builders for the reservation tests (#2483): a synthetic Opus
 * stream of any length, built in memory. Page CRCs are left at zero; nothing
 * under test checks them. Test code only: nothing in `src/index.ts` imports it.
 */

export interface OggPageInput {
  serial: number;
  /** 0x01 continued, 0x02 first page of a stream, 0x04 last page. */
  flags: number;
  granule: number;
  /** Whole packets; each must fit in the page's 255 lacing values. */
  packets: Uint8Array[];
}

export function oggPage({ serial, flags, granule, packets }: OggPageInput): Uint8Array {
  const lacing: number[] = [];
  for (const packet of packets) {
    let remaining = packet.length;
    while (remaining >= 255) {
      lacing.push(255);
      remaining -= 255;
    }
    lacing.push(remaining);
  }
  if (lacing.length > 255) throw new Error('too many segments for one Ogg page');
  const bodyLength = packets.reduce((sum, packet) => sum + packet.length, 0);
  const page = new Uint8Array(27 + lacing.length + bodyLength);
  const view = new DataView(page.buffer);
  page.set([0x4F, 0x67, 0x67, 0x53], 0);
  page[5] = flags;
  view.setUint32(6, granule % 2 ** 32, true);
  view.setUint32(10, Math.floor(granule / 2 ** 32), true);
  view.setUint32(14, serial, true);
  page[26] = lacing.length;
  page.set(lacing, 27);
  let offset = 27 + lacing.length;
  for (const packet of packets) {
    page.set(packet, offset);
    offset += packet.length;
  }
  return page;
}

export function opusHead(preSkip = 312): Uint8Array {
  const head = new Uint8Array(19);
  const view = new DataView(head.buffer);
  head.set([...'OpusHead'].map((char) => char.charCodeAt(0)), 0);
  head[8] = 1; // version
  head[9] = 1; // channels
  view.setUint16(10, preSkip, true);
  view.setUint32(12, 16_000, true);
  return head;
}

export function opusTags(): Uint8Array {
  const tags = new Uint8Array(16);
  tags.set([...'OpusTags'].map((char) => char.charCodeAt(0)), 0);
  return tags;
}

export function concat(parts: Uint8Array[]): ArrayBuffer {
  const out = new Uint8Array(parts.reduce((sum, part) => sum + part.length, 0));
  let offset = 0;
  for (const part of parts) {
    out.set(part, offset);
    offset += part.length;
  }
  return out.buffer;
}

export interface OpusStreamOptions {
  seconds: number;
  /** Bytes per 20 ms packet; 20 is 8 kbps. */
  packetBytes?: number;
  serial?: number;
  preSkip?: number;
  /** Override every audio page's granule position (a tampered header). */
  granule?: (truePosition: number) => number;
}

/** The pages of one Opus stream of 20 ms SILK packets (TOC config 1, code 0). */
export function opusStreamPages(options: OpusStreamOptions): Uint8Array[] {
  const packetBytes = options.packetBytes ?? 20;
  const serial = options.serial ?? 1;
  const preSkip = options.preSkip ?? 312;
  const packetCount = Math.round(options.seconds * 50);
  const pages: Uint8Array[] = [
    oggPage({ serial, flags: 0x02, granule: 0, packets: [opusHead(preSkip)] }),
    oggPage({ serial, flags: 0, granule: 0, packets: [opusTags()] }),
  ];
  const packet = new Uint8Array(packetBytes);
  packet[0] = 1 << 3; // SILK narrowband, 20 ms, one frame
  const perPage = Math.max(1, Math.floor(255 / Math.ceil((packetBytes + 1) / 255)));
  let written = 0;
  while (written < packetCount) {
    const count = Math.min(perPage, packetCount - written);
    written += count;
    const truePosition = preSkip + written * 960;
    pages.push(oggPage({
      serial,
      flags: written === packetCount ? 0x04 : 0,
      granule: options.granule ? options.granule(truePosition) : truePosition,
      packets: Array.from({ length: count }, () => packet),
    }));
  }
  return pages;
}

/** A whole single-stream Ogg Opus file. */
export function opusOgg(options: OpusStreamOptions): ArrayBuffer {
  return concat(opusStreamPages(options));
}
