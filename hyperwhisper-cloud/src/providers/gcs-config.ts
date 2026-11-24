// GCS SCRATCH BUCKET CONFIGURATION
//
// "Is a GCS scratch bucket configured, and what is it called?" — nothing else.
// Split out of `gcs-storage.ts` so a module can answer that question without
// pulling in the storage client (which reaches Google OAuth and Redis, and
// which the Chirp tests replace with a process-wide `mock.module`). The route's
// pre-buffer size gate needs the answer and none of the machinery.
//
// `gcs-storage.ts` re-exports `isGcsTranscriptionBucketConfigured` so existing
// importers are unaffected; this file stays the single place the environment
// variable is read.

/** The configured scratch bucket, or `null` when unset / blank. */
export function getConfiguredBucket(): string | null {
  const bucket = process.env.GOOGLE_SPEECH_GCS_BUCKET?.trim();
  return bucket && bucket.length > 0 ? bucket : null;
}

/** True when `uploadTranscriptionAudio` can be used. */
export function isGcsTranscriptionBucketConfigured(): boolean {
  return getConfiguredBucket() !== null;
}
