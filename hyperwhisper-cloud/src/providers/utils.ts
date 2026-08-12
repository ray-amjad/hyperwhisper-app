import { AsyncLocalStorage } from 'node:async_hooks';
import { ProviderUnavailableError } from './types';
import type { ProviderRequestContext } from './types';
import { BYTES_PER_MINUTE_ESTIMATE } from '../lib/constants';

const DEFAULT_PROVIDER_TIMEOUT_MS = 15_000;
const ERROR_BODY_PREVIEW_LIMIT = 500;

// Audio-upload timeout budget, scaled with payload size. The default 15s is fine
// for the small create/poll/transcript calls, but an upload that re-sends the
// whole recording (AssemblyAI /v2/upload, Soniox /v1/files, Azure MAI multipart)
// can far exceed 15s for the large files these async/large-cap providers accept —
// aborting mid-upload would surface as a spurious 502. Floor 30s + 1s per 100 KB.
// Mirrors gcs-storage's GCS upload budget.
const UPLOAD_TIMEOUT_FLOOR_MS = 30_000;
const UPLOAD_TIMEOUT_PER_100KB_MS = 1_000;
export function computeUploadTimeoutMs(byteLength: number): number {
  return Math.max(UPLOAD_TIMEOUT_FLOOR_MS, Math.ceil(byteLength / 100_000) * UPLOAD_TIMEOUT_PER_100KB_MS);
}

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

/** Filename extensions the multipart adapters use for the audio part. */
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
  { ext: 'ogg', hints: ['ogg'] },
  { ext: 'flac', hints: ['flac'] },
];

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
 * Whether one provider attempt ever opened a socket to its upstream.
 *
 * The /latency page may only publish an attempt the provider actually received.
 * Every adapter runs its own gates first — a missing API key, a byte-length cap,
 * a content-type check — and each of those throws in single-digit microseconds,
 * which the page would otherwise render as that provider answering in 1 ms.
 * Enumerating those throw sites is what failed twice (a whitelist of error types
 * has to be extended by whoever adds the next gate, and nothing makes them).
 *
 * So the signal is taken from the one place that cannot be bypassed without
 * noticing: the request actually leaving this process. `markProviderNetworkCall()`
 * is called by fetchWithTimeout() below — every adapter's only route to an
 * upstream — and by the GCS uploader on Chirp's large-file path, BEFORE the
 * fetch, so a timeout or a connection reset still counts as a measurement. A new
 * gate added above any of those calls is excluded automatically; a new gate
 * added below one is, correctly, a real attempt.
 *
 * AsyncLocalStorage rather than a module-level flag: attempts from concurrent
 * requests interleave on one event loop, and a shared flag would let one
 * request's fetch mark another request's rejection as measured.
 *
 * One deliberate omission: lib/google-auth mints its Google token through
 * google-auth-library's own HTTP client, so a Chirp attempt that dies there is
 * not reported. That is the right answer — a bad or missing service-account
 * credential is our configuration failing, not Google answering slowly.
 */
export type ProviderAttemptNetwork = { reachedProvider: boolean };

const attemptNetworkStorage = new AsyncLocalStorage<ProviderAttemptNetwork>();

/** Runs one provider attempt, recording into `state` whether it reached the wire. */
export function runProviderAttempt<T>(
  state: ProviderAttemptNetwork,
  fn: () => Promise<T>,
): Promise<T> {
  return attemptNetworkStorage.run(state, fn);
}

/** Called immediately before an upstream request leaves this process. */
export function markProviderNetworkCall(): void {
  const state = attemptNetworkStorage.getStore();
  if (state) {
    state.reachedProvider = true;
  }
}

function resolveProviderTimeoutMs(): number {
  const configured = Number.parseInt(process.env.STT_PROVIDER_TIMEOUT_MS || '', 10);
  if (Number.isFinite(configured) && configured > 0) {
    return configured;
  }

  return DEFAULT_PROVIDER_TIMEOUT_MS;
}

function serializeError(error: unknown): string {
  if (error instanceof Error) {
    return error.message;
  }

  return String(error);
}

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError';
}

export function logProviderEvent(
  provider: string,
  event: string,
  details: Record<string, unknown>,
  context: ProviderRequestContext = {},
) {
  console.log(`provider.${event}`, {
    provider,
    requestId: context.requestId,
    attempt: context.attempt,
    ...details,
  });
}

export async function fetchWithTimeout(
  provider: string,
  url: string,
  init: RequestInit,
  context: ProviderRequestContext = {},
  timeoutMsOverride?: number,
): Promise<Response> {
  const timeoutMs = typeof timeoutMsOverride === 'number' && timeoutMsOverride > 0
    ? timeoutMsOverride
    : resolveProviderTimeoutMs();
  const startedAt = performance.now();
  const controller = new AbortController();
  const timeoutHandle = setTimeout(() => controller.abort(), timeoutMs);

  // Before the fetch, not after: a timeout or a connection reset is a provider
  // attempt the user waited on, so it has to count as a measurement. See
  // ProviderAttemptNetwork above.
  markProviderNetworkCall();
  logProviderEvent(provider, 'request_start', { timeoutMs }, context);

  try {
    const response = await fetch(url, {
      ...init,
      signal: controller.signal,
    });

    logProviderEvent(provider, 'http_response', {
      elapsedMs: Math.round(performance.now() - startedAt),
      status: response.status,
      ok: response.ok,
    }, context);

    return response;
  } catch (error) {
    const elapsedMs = Math.round(performance.now() - startedAt);

    if (isAbortError(error)) {
      logProviderEvent(provider, 'transport_error', {
        elapsedMs,
        kind: 'timeout',
        timeoutMs,
      }, context);
      throw new ProviderUnavailableError(provider, `timeout after ${timeoutMs}ms`, {
        kind: 'timeout',
        elapsedMs,
      });
    }

    logProviderEvent(provider, 'transport_error', {
      elapsedMs,
      kind: 'network_error',
      message: serializeError(error),
    }, context);
    throw new ProviderUnavailableError(provider, `network error: ${serializeError(error)}`, {
      kind: 'network_error',
      elapsedMs,
    });
  } finally {
    clearTimeout(timeoutHandle);
  }
}

export function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

export async function readErrorBodyPreview(response: Response): Promise<string> {
  try {
    const body = await response.text();
    if (body.length <= ERROR_BODY_PREVIEW_LIMIT) {
      return body;
    }

    return `${body.slice(0, ERROR_BODY_PREVIEW_LIMIT)}...`;
  } catch {
    return '<unreadable>';
  }
}
