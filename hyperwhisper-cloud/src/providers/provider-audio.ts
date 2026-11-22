import { BYTES_PER_MINUTE_ESTIMATE } from '../lib/constants';

/**
 * Conservative audio-duration estimate (seconds) from the encoded byte length,
 * for fail-closed billing when an upstream returns a successful transcript but
 * omits the duration/usage we'd normally bill on. Mirrors the route's preflight
 * size→seconds heuristic (64 kbps encoded).
 */
export function estimateSecondsFromBytes(byteLength: number): number {
  return (byteLength / BYTES_PER_MINUTE_ESTIMATE) * 60;
}

/**
 * Representative bytes-per-second per container, for the duration estimates that
 * have to be roughly RIGHT rather than deliberately conservative — Chirp's
 * inline/duration gate and missing-`totalBilledDuration` fallback, and the
 * /latency page's clip-length bucketing.
 *
 * The desktop apps upload 16 kHz/16-bit mono WAV (32,000 B/s), so the flat
 * 64 kbps assumption behind estimateSecondsFromBytes() overstates their clips
 * by ~4×. Unknown or compressed containers fall back to 16,000 B/s (128 kbps),
 * which is the middle of the range we actually receive.
 *
 * Match order is the order google-chirp.ts's if/else chain used before this
 * moved here — a content type carrying two hints (`audio/mp4; codecs=opus`)
 * resolves to the first entry that matches. Keep it stable.
 */
const BYTES_PER_SECOND_BY_CONTAINER: ReadonlyArray<{ hints: readonly string[]; bytesPerSecond: number }> = [
  { hints: ['wav', 'pcm'], bytesPerSecond: 32_000 },
  { hints: ['opus', 'webm'], bytesPerSecond: 8_000 },
  { hints: ['flac', 'ogg'], bytesPerSecond: 32_000 },
  { hints: ['mp3', 'mpeg'], bytesPerSecond: 16_000 },
  { hints: ['m4a', 'mp4', 'aac'], bytesPerSecond: 16_000 },
];

const DEFAULT_BYTES_PER_SECOND = 16_000;

/**
 * Estimate audio duration (seconds) from byte length using the rate for the
 * request's Content-Type. Over-estimates slightly on compressed audio and
 * under-estimates slightly on raw — both preferable to being an order of
 * magnitude out on the WAV every desktop client sends.
 */
export function estimateAudioSeconds(byteLength: number, contentType: string): number {
  const lower = (contentType || '').toLowerCase();
  const match = BYTES_PER_SECOND_BY_CONTAINER.find(({ hints }) =>
    hints.some((hint) => lower.includes(hint)),
  );

  return byteLength / (match?.bytesPerSecond ?? DEFAULT_BYTES_PER_SECOND);
}

/** Filename extensions the multipart adapters (and the GCS scratch object) use for the audio. */
export type AudioExtension = 'wav' | 'mp3' | 'm4a' | 'aac' | 'webm' | 'ogg' | 'flac';

/**
 * Content-type substrings that identify each extension, in match order.
 *
 * Order matters and is the order every adapter already used: a content type
 * carrying more than one hint (`audio/mp4; codecs=aac`) resolves to the first
 * entry that matches, not the most specific one. Keep it stable — reordering
 * silently renames the uploaded part for those inputs.
 */
const AUDIO_EXTENSION_HINTS: ReadonlyArray<{ ext: AudioExtension; hints: readonly string[] }> = [
  { ext: 'wav', hints: ['wav'] },
  { ext: 'mp3', hints: ['mp3', 'mpeg'] },
  { ext: 'm4a', hints: ['m4a', 'mp4'] },
  { ext: 'aac', hints: ['aac'] },
  { ext: 'webm', hints: ['webm'] },
  // A bare `audio/opus` is an Ogg Opus file (webm/mp4 Opus carry their own
  // container hint, matched earlier), so it is named .ogg for every caller.
  { ext: 'ogg', hints: ['ogg', 'opus'] },
  { ext: 'flac', hints: ['flac'] },
];

/** Every extension the map knows — for a caller with no container restriction (the GCS scratch object). */
export const ALL_AUDIO_EXTENSIONS: readonly AudioExtension[] = AUDIO_EXTENSION_HINTS.map(({ ext }) => ext);

/** The container set the Whisper-style multipart adapters recognise. */
export const DEFAULT_AUDIO_EXTENSIONS = ['wav', 'mp3', 'm4a', 'webm', 'ogg', 'flac'] as const;

/**
 * Resolve the filename extension for the multipart `file`/`audio` part from the
 * request's Content-Type.
 *
 * `accepted` is the set of extensions the caller's upstream will take, so a
 * provider that rejects a container (Azure MAI takes only wav/mp3/flac) never
 * resolves to it; match order comes from AUDIO_EXTENSION_HINTS, not from the
 * order of `accepted`. Returns undefined when nothing matches — callers decide
 * whether that is a fallback extension or a hard rejection. Matching is
 * case-sensitive; pass an already-lowercased content type to fold case.
 */
export function audioExtensionFromContentType<T extends AudioExtension>(
  contentType: string,
  accepted: readonly T[],
): T | undefined {
  for (const { ext, hints } of AUDIO_EXTENSION_HINTS) {
    if (!(accepted as readonly AudioExtension[]).includes(ext)) continue;
    if (hints.some((hint) => contentType.includes(hint))) return ext as T;
  }

  return undefined;
}

/**
 * The upstream's own reported audio length, or `null` when it did not report one
 * we can use.
 *
 * `> 0` alone is not the test. `JSON.parse('{"duration":1e999}')` yields
 * `Infinity`, which passes `> 0` and produced the log line — and the refusal
 * message — `empty transcript for Infinitys of audio`. It is also the guard that
 * keeps phase 1's invariant honest: `upstreamDurationSeconds` is the upstream's
 * number or nothing, never a value we could not read.
 * (issue ray-amjad/hyperwhisper-app#381)
 */
export function upstreamDurationOrNull(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) && value > 0 ? value : null;
}

/**
 * The seconds a successful transcript is billed on: the upstream's own reported
 * length when it gave one we can use, else the conservative byte estimate. One
 * rule for every duration-billed adapter, so a 200 that omits its duration is
 * billed fail-closed, never $0.
 */
export function billableSeconds(upstreamDuration: unknown, audioByteLength: number): number {
  return upstreamDurationOrNull(upstreamDuration) ?? estimateSecondsFromBytes(audioByteLength);
}
