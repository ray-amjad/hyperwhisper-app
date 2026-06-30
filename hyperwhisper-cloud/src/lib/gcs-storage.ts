// GCS STORAGE (transcription scratch path)
//
// Tightly scoped service for one purpose: upload an audio payload to the
// HyperWhisper Cloud transcription bucket so Google Speech V2 can read it via
// a `gs://` URI, then delete the object after transcription completes. The
// module knows nothing about Chirp, Speech V2, or the transcribe route — it
// only knows how to PUT bytes to a bucket and DELETE them again.
//
// Lifecycle contract:
//   1. caller invokes `uploadTranscriptionAudio` → receives { gcsUri, ref }
//   2. caller passes `gcsUri` to Google Speech V2 in the `uri` field
//   3. caller invokes `deleteTranscriptionAudio(ref)` in a `finally` block
//
// Defense in depth: the bucket SHOULD also have a server-side lifecycle rule
// (Delete after 1 day) so an orphaned object from a worker crash or
// network failure between recognize and delete still gets cleaned up.
// See hyperwhisper-cloud/CLAUDE.md for the bucket setup playbook.
//
// Why a `gs://` URI not a public-read URL: Speech V2 reads from GCS via the
// service account's IAM, never via a public URL. The bucket can stay private
// (and SHOULD — these are user voice recordings).

import { getGoogleAccessToken } from './google-auth';
import { ProviderUnavailableError } from '../providers/types';

const GCS_API_BASE = 'https://storage.googleapis.com';
const UPLOAD_TIMEOUT_FLOOR_MS = 30_000;
const UPLOAD_TIMEOUT_PER_100KB_MS = 1_000;
const DELETE_TIMEOUT_MS = 10_000;

/**
 * Scale upload budget with payload size so a slow-region 50–100 MB upload
 * doesn't time out at 30 s. Floor stays at 30 s for small uploads, +1 s per
 * 100 KB on top. A 100 MB file gets ~1000 s, 2 GB (the upstream max) ~20 000 s.
 */
function computeUploadTimeoutMs(byteLength: number): number {
  return Math.max(UPLOAD_TIMEOUT_FLOOR_MS, Math.ceil(byteLength / 100_000) * UPLOAD_TIMEOUT_PER_100KB_MS);
}

/**
 * Opaque reference for a temporary transcription object, returned by
 * `uploadTranscriptionAudio` and consumed by `deleteTranscriptionAudio`.
 * Keeping bucket + objectName paired prevents callers from mixing buckets.
 */
export interface TranscriptionAudioRef {
  readonly bucket: string;
  readonly objectName: string;
}

export interface TranscriptionAudioUpload extends TranscriptionAudioRef {
  /** `gs://<bucket>/<objectName>` — pass straight to Speech V2 `uri`. */
  readonly gcsUri: string;
}

function getConfiguredBucket(): string | null {
  const bucket = process.env.GOOGLE_SPEECH_GCS_BUCKET?.trim();
  return bucket && bucket.length > 0 ? bucket : null;
}

/** True when `uploadTranscriptionAudio` can be used. */
export function isGcsTranscriptionBucketConfigured(): boolean {
  return getConfiguredBucket() !== null;
}

function inferExtension(contentType: string): string {
  const lower = contentType.toLowerCase();
  if (lower.includes('wav')) return 'wav';
  if (lower.includes('mp3') || lower.includes('mpeg')) return 'mp3';
  if (lower.includes('m4a') || lower.includes('mp4')) return 'm4a';
  if (lower.includes('webm')) return 'webm';
  if (lower.includes('ogg') || lower.includes('opus')) return 'ogg';
  if (lower.includes('flac')) return 'flac';
  if (lower.includes('aac')) return 'aac';
  return 'bin';
}

function buildObjectName(ext: string): string {
  // Bun and Node both expose crypto.randomUUID() globally.
  const uuid = crypto.randomUUID();
  // The `stt-temp/` prefix lets the bucket's lifecycle rule scope to
  // exactly this scratch path without affecting any other use of the bucket.
  return `stt-temp/${Date.now()}-${uuid}.${ext}`;
}

async function fetchWithTimeoutMs(url: string, init: RequestInit, timeoutMs: number): Promise<Response> {
  const controller = new AbortController();
  const handle = setTimeout(() => controller.abort(), timeoutMs);
  try {
    return await fetch(url, { ...init, signal: controller.signal });
  } finally {
    clearTimeout(handle);
  }
}

/**
 * PUT the audio into the configured bucket and return the gs:// URI plus
 * the delete handle. Throws if the bucket isn't configured — callers should
 * gate with `isGcsTranscriptionBucketConfigured()` first.
 */
export async function uploadTranscriptionAudio(
  audio: ArrayBuffer,
  contentType: string,
): Promise<TranscriptionAudioUpload> {
  const bucket = getConfiguredBucket();
  if (!bucket) {
    throw new Error('GOOGLE_SPEECH_GCS_BUCKET not configured');
  }

  const ext = inferExtension(contentType);
  const objectName = buildObjectName(ext);
  const accessToken = await getGoogleAccessToken();

  // Simple media upload (single request, no resumable session). Audio
  // payloads here are capped well under 5 GB (the simple-upload limit) by
  // MAX_AUDIO_SIZE_BYTES upstream.
  const url = `${GCS_API_BASE}/upload/storage/v1/b/${encodeURIComponent(bucket)}/o`
    + `?uploadType=media&name=${encodeURIComponent(objectName)}`;

  const start = performance.now();
  const timeoutMs = computeUploadTimeoutMs(audio.byteLength);
  let response: Response;
  try {
    response = await fetchWithTimeoutMs(url, {
      method: 'POST',
      headers: {
        'Authorization': `Bearer ${accessToken}`,
        'Content-Type': contentType || 'application/octet-stream',
      },
      body: audio,
    }, timeoutMs);
  } catch (error) {
    // Route timeouts/network errors through ProviderUnavailableError so
    // transcribe.ts hits the 502 chain-fail path instead of returning 500.
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw new ProviderUnavailableError('GCS upload', `upload timeout after ${timeoutMs}ms`);
    }
    throw new ProviderUnavailableError(
      'GCS upload',
      `network error: ${error instanceof Error ? error.message : String(error)}`,
    );
  }

  const elapsedMs = Math.round(performance.now() - start);

  if (!response.ok) {
    const preview = await response.text().catch(() => '<unreadable>');
    // Route transient GCS failures (429 throttling, 5xx outages) through
    // ProviderUnavailableError so transcribe.ts surfaces them as 502 via the
    // chain-fail path. Non-transient codes (403 bad IAM, 404 missing bucket,
    // 400 malformed request) stay as plain Errors so they fail fast instead
    // of silently cascading to alternate providers.
    if (response.status === 429 || response.status >= 500) {
      throw new ProviderUnavailableError(
        'GCS upload',
        `upload ${response.status} after ${elapsedMs}ms: ${preview.slice(0, 200)}`,
      );
    }
    throw new Error(
      `GCS upload failed (status=${response.status}, elapsedMs=${elapsedMs}): ${preview.slice(0, 400)}`,
    );
  }

  console.log('gcs.upload_ok', {
    bucket,
    objectName,
    bytes: audio.byteLength,
    elapsedMs,
  });

  return {
    bucket,
    objectName,
    gcsUri: `gs://${bucket}/${objectName}`,
  };
}

/**
 * Delete a temporary transcription object. Swallows all errors and logs them
 * — the caller has already produced (or attempted) a transcript and should
 * not surface a 5xx because cleanup failed. The bucket's lifecycle rule is
 * the durable backstop for any object this miss.
 */
export async function deleteTranscriptionAudio(ref: TranscriptionAudioRef): Promise<void> {
  const url = `${GCS_API_BASE}/storage/v1/b/${encodeURIComponent(ref.bucket)}/o/${encodeURIComponent(ref.objectName)}`;

  try {
    const accessToken = await getGoogleAccessToken();
    const start = performance.now();
    const response = await fetchWithTimeoutMs(url, {
      method: 'DELETE',
      headers: { 'Authorization': `Bearer ${accessToken}` },
    }, DELETE_TIMEOUT_MS);

    const elapsedMs = Math.round(performance.now() - start);

    // 404 is fine — already gone (re-entrant cleanup or lifecycle race).
    if (!response.ok && response.status !== 404) {
      const preview = await response.text().catch(() => '<unreadable>');
      console.warn('gcs.delete_failed', {
        bucket: ref.bucket,
        objectName: ref.objectName,
        status: response.status,
        elapsedMs,
        bodyPreview: preview.slice(0, 200),
      });
      return;
    }

    console.log('gcs.delete_ok', {
      bucket: ref.bucket,
      objectName: ref.objectName,
      elapsedMs,
      status: response.status,
    });
  } catch (error) {
    console.warn('gcs.delete_error', {
      bucket: ref.bucket,
      objectName: ref.objectName,
      message: error instanceof Error ? error.message : String(error),
    });
  }
}
