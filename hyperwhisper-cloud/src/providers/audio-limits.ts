// PER-PROVIDER AUDIO SIZE CAPS
//
// One table, one row per STT provider: the payload cap each adapter enforces,
// and the cap the transcribe route checks against Content-Length so an
// oversized upload gets a 413 without allocating the ArrayBuffer first (up to
// MAX_AUDIO_SIZE_BYTES = 128 MB on a 1 GB Fly machine).
//
// This module exists so the route does not have to know THREE provider
// internals it previously carried inline:
//   1. which providers have a payload cap at all,
//   2. what each cap is,
//   3. that Google Chirp's cap disappears when a GCS scratch bucket is
//      configured, because the long-file batchRecognize path takes over — a
//      fact owned by `lib/gcs-config.ts`, not by the route. The route used to
//      read `process.env.GOOGLE_SPEECH_GCS_BUCKET` itself, a second copy of
//      that module's own configuration check.
//
// Adding a provider with a cap is a row in the table below, not a new `if` arm
// in the route. The table is a `Record<SttProviderId, …>`, so a new provider does not
// compile until someone writes down its cap (or `null`, for none). It used to
// be a `Partial` map the adapters did not read, and Soniox's 14.4 MB cap was
// missing from it: a Soniox upload was buffered up to the 128 MB global cap
// before the adapter's own 413. Each adapter now reads its `adapterMaxBytes`
// from here, so the number in its AudioTooLargeError (and so its 413's
// `max_size_mb`) is the one the gate uses.
//
// The pre-buffer cap is a fast-fail gate, NOT the enforcement point. Every
// adapter re-checks its own limit on the real buffer and throws
// AudioTooLargeError, so a request that arrives without a Content-Length still
// cannot get past it.

import {
  AZURE_MAI_MAX_BYTES,
  GEMINI_INLINE_MAX_BYTES,
  GEMINI_TRANSCRIBE_INLINE_MAX_BYTES,
  GOOGLE_CHIRP_INLINE_MAX_BYTES,
  OPENAI_INLINE_MAX_BYTES,
  META_MUSE_MAX_BYTES,
  META_MUSE_SOURCE_MAX_BYTES,
  SONIOX_MAX_BYTES,
} from '../lib/constants';
// From lib/gcs-config, not lib/gcs-storage: this gate only needs to know
// whether a bucket exists, and must not drag the storage client (Google OAuth,
// Redis) onto the pre-buffer path.
import { isGcsTranscriptionBucketConfigured } from '../lib/gcs-config';
import type { SttProviderId } from '../lib/stt-models';

export interface AudioSizeCap {
  /**
   * The largest buffer the adapter accepts. It is the `maxBytes` of the
   * adapter's AudioTooLargeError, so it is also what its 413 advertises.
   */
  readonly adapterMaxBytes: number;
  /**
   * The Content-Length cap the route checks before buffering; omit it when it
   * is `adapterMaxBytes`. `null` from it means no pre-buffer cap right now.
   *
   * Resolved per request rather than read once at module load: the Chirp entry
   * depends on an environment variable, and the tests (plus a Fly machine that
   * picks up a newly-synced secret) must see a change without a fresh import.
   */
  readonly preBufferMaxBytes?: () => number | null;
}

/** `null`: no cap of its own; only the global MAX_AUDIO_SIZE_BYTES applies, which the route enforces for everyone. */
export const PROVIDER_AUDIO_CAPS = {
  deepgram: null,
  groq: null,
  elevenlabs: null,
  grok: null,
  assemblyai: null,
  mistral: null,
  // Speech V2 sync `recognize` caps the inline payload at 10 MB. With a GCS
  // scratch bucket the provider uploads instead and runs batchRecognize, which
  // has no such cap — so the gate lifts entirely.
  'google-chirp': {
    adapterMaxBytes: GOOGLE_CHIRP_INLINE_MAX_BYTES,
    preBufferMaxBytes: () => (isGcsTranscriptionBucketConfigured() ? null : GOOGLE_CHIRP_INLINE_MAX_BYTES),
  },
  // Gemini sends audio inline as base64. The total request must stay under
  // 20 MB and base64 inflates ~33%, so raw audio caps at ~14 MB.
  gemini: { adapterMaxBytes: GEMINI_INLINE_MAX_BYTES },
  // Gemini 3.5 Transcribe's /v1beta/interactions endpoint has no file-reference
  // form at all, so the base64 inline cap is unconditional — there is no
  // GCS-style overflow path that could lift it.
  'gemini-transcribe': { adapterMaxBytes: GEMINI_TRANSCRIBE_INLINE_MAX_BYTES },
  // OpenAI hard-rejects audio over 25 MB with a 400.
  openai: { adapterMaxBytes: OPENAI_INLINE_MAX_BYTES },
  // ~30 min at the byte→seconds estimate, so a job always finishes inside the
  // adapter's poll deadline (see SONIOX_MAX_BYTES).
  soniox: { adapterMaxBytes: SONIOX_MAX_BYTES },
  // Azure's own upload limit (300 MB) is above MAX_AUDIO_SIZE_BYTES (128 MB),
  // so today the global gate, and Bun's maxRequestBodySize, reject first and
  // neither this gate nor the adapter's check ever fires. Kept as Azure's real
  // number so raising the global cap cannot admit more than Azure takes.
  'azure-mai': { adapterMaxBytes: AZURE_MAI_MAX_BYTES },
  // A noncanonical source can be larger than the 32 MB canonical-WAV limit and
  // still normalize below it, so retain one conversion-retry window. Twice the
  // canonical cap is still only 6.25% of a 1 GiB Fly machine before the
  // ArrayBuffer and multipart copies, avoiding the old 2 GiB allocation path.
  meta: {
    adapterMaxBytes: META_MUSE_MAX_BYTES,
    preBufferMaxBytes: () => META_MUSE_SOURCE_MAX_BYTES,
  },
} as const satisfies Record<SttProviderId, AudioSizeCap | null>;

/**
 * The largest payload this provider can be given, or `null` when it has no
 * pre-buffer cap of its own.
 *
 * Callers compare it against Content-Length and 413 on the way past. They do
 * not need to know which provider they are asking about.
 */
export function preBufferMaxBytes(provider: SttProviderId): number | null {
  const cap: AudioSizeCap | null = PROVIDER_AUDIO_CAPS[provider];
  if (!cap) return null;
  return cap.preBufferMaxBytes ? cap.preBufferMaxBytes() : cap.adapterMaxBytes;
}
