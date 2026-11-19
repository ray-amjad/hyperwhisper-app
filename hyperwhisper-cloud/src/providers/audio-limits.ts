// One question, one answer: how many bytes may a request declare before this
// provider is guaranteed to reject them anyway? The transcribe route asks this
// against Content-Length so an oversized upload gets a 413 without allocating
// the ArrayBuffer first (up to MAX_AUDIO_SIZE_BYTES = 128 MB on a 1 GB Fly
// machine).
//
// These caps are a fast-fail gate, NOT the enforcement point. Every adapter
// re-checks its own limit on the real buffer and throws AudioTooLargeError, so
// a request that arrives without a Content-Length still cannot get past it.

import {
  GEMINI_INLINE_MAX_BYTES,
  GEMINI_TRANSCRIBE_INLINE_MAX_BYTES,
  GOOGLE_CHIRP_INLINE_MAX_BYTES,
  OPENAI_INLINE_MAX_BYTES,
  META_MUSE_SOURCE_MAX_BYTES,
} from '../lib/constants';
// From lib/gcs-config, not lib/gcs-storage: this gate only needs to know
// whether a bucket exists, and must not drag the storage client (Google OAuth,
// Redis) onto the pre-buffer path.
import { isGcsTranscriptionBucketConfigured } from '../lib/gcs-config';
import type { SttProviderId } from '../lib/stt-models';

/**
 * Resolved per request rather than read once at module load: the Chirp entry
 * depends on an environment variable, and the tests (plus a Fly machine that
 * picks up a newly-synced secret) must see a change without a fresh import.
 */
type PreBufferLimitResolver = () => number | null;

const PRE_BUFFER_LIMITS: Partial<Record<SttProviderId, PreBufferLimitResolver>> = {
  // Speech V2 sync `recognize` caps the inline payload at 10 MB. With a GCS
  // scratch bucket the provider uploads instead and runs batchRecognize, which
  // has no such cap — so the gate lifts entirely.
  'google-chirp': () => (isGcsTranscriptionBucketConfigured() ? null : GOOGLE_CHIRP_INLINE_MAX_BYTES),
  // Gemini sends audio inline as base64. The total request must stay under
  // 20 MB and base64 inflates ~33%, so raw audio caps at ~14 MB.
  gemini: () => GEMINI_INLINE_MAX_BYTES,
  // Gemini 3.5 Transcribe's /v1beta/interactions endpoint has no file-reference
  // form at all, so the base64 inline cap is unconditional — there is no
  // GCS-style overflow path that could lift it.
  'gemini-transcribe': () => GEMINI_TRANSCRIBE_INLINE_MAX_BYTES,
  // OpenAI hard-rejects audio over 25 MB with a 400.
  openai: () => OPENAI_INLINE_MAX_BYTES,
  // A noncanonical source can be larger than the 32 MB canonical-WAV limit and
  // still normalize below it, so retain one conversion-retry window. Twice the
  // canonical cap is still only 6.25% of a 1 GiB Fly machine before the
  // ArrayBuffer and multipart copies, avoiding the old 2 GiB allocation path.
  meta: () => META_MUSE_SOURCE_MAX_BYTES,
};

export function preBufferMaxBytes(provider: SttProviderId): number | null {
  return PRE_BUFFER_LIMITS[provider]?.() ?? null;
}
