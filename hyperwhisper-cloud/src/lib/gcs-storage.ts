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

import { getConfiguredBucket } from './gcs-config';
import { getGoogleAccessToken } from './google-auth';
import { ProviderUnavailableError } from '../providers/types';
import { markProviderNetworkCall } from '../providers/utils';
import {
  safeErrorToken,
  summarizeUpstreamError,
  upstreamErrorLogFields,
} from '../providers/upstream-error';

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

// Re-exported so every existing importer keeps its `from './gcs-storage'`
// path. The environment variable itself is read in exactly one place,
// `lib/gcs-config.ts`.
export { isGcsTranscriptionBucketConfigured } from './gcs-config';

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
  // Chirp's large-file path spends most of its wall time here, before a single
  // byte reaches Speech-to-Text. It is still the attempt leaving this process,
  // so it marks the attempt as measurable — see providers/utils.ts.
  markProviderNetworkCall();
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
    // Content-free fields only (#2176): the route logs these messages, and
    // the README promises no upstream error body is logged. A GCS body names
    // buckets and IAM principals, not audio, but the rule has no exceptions.
    const { reason } = await readGcsError(response);
    // Route transient GCS failures (429 throttling, 5xx outages) through
    // ProviderUnavailableError so transcribe.ts surfaces them as 502 via the
    // chain-fail path. Non-transient codes (403 bad IAM, 404 missing bucket,
    // 400 malformed request) stay as plain Errors so they fail fast instead
    // of silently cascading to alternate providers.
    if (response.status === 429 || response.status >= 500) {
      throw new ProviderUnavailableError(
        'GCS upload',
        `upload ${response.status} after ${elapsedMs}ms: ${reason}`,
      );
    }
    throw new Error(
      `GCS upload failed (status=${response.status}, elapsedMs=${elapsedMs}): ${reason}`,
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
 * Read a failed GCS response once and keep only content-free facts (#2176).
 * The body's prose (`error.message`, `errors[].message`) names buckets and
 * IAM principals and never reaches a message or a log line. What is kept:
 * the shared upstream fields (status, body length, request id) and GCS's own
 * reason token from `error.errors[0].reason` (`forbidden`, `notFound`,
 * `rateLimitExceeded`, ...), which tells a missing IAM grant from a missing
 * bucket. `reason` is that token, else `HTTP <status>`.
 */
async function readGcsError(
  response: Response,
): Promise<{ reason: string; logFields: Record<string, string | number> }> {
  let body: string | undefined;
  try {
    body = await response.text();
  } catch {
    body = undefined;
  }
  const logFields = upstreamErrorLogFields(summarizeUpstreamError(response.status, response.headers, body));
  const gcsReason = gcsErrorReasonToken(body);
  if (gcsReason !== undefined) logFields.gcsReason = gcsReason;
  return { reason: gcsReason ?? `HTTP ${response.status}`, logFields };
}

function gcsErrorReasonToken(body: string | undefined): string | undefined {
  if (!body) return undefined;
  let json: unknown;
  try {
    json = JSON.parse(body);
  } catch {
    return undefined;
  }
  const error = (json as { error?: unknown } | null)?.error;
  const errors = (error as { errors?: unknown } | null | undefined)?.errors;
  if (!Array.isArray(errors)) return undefined;
  return safeErrorToken((errors[0] as { reason?: unknown } | null | undefined)?.reason);
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
      const { logFields } = await readGcsError(response);
      console.warn('gcs.delete_failed', {
        bucket: ref.bucket,
        objectName: ref.objectName,
        ...logFields,
        elapsedMs,
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
