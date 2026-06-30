// GOOGLE CLOUD SPEECH-TO-TEXT V2 — CHIRP 3 PROVIDER
// Standard tier: $0.016/min, phrase-list biasing (up to 1000 terms),
// 100+ languages, word timestamps.
//
// Auth: Bearer OAuth token derived from a service-account JSON, cached in Redis.
//
// Audio delivery path:
//   - <= INLINE_AUDIO_MAX_BYTES (9.5 MB)  → inline base64 `content` → sync recognize
//   - >  INLINE_AUDIO_MAX_BYTES + bucket  → upload to GCS → batchRecognize + poll
//   - >  INLINE_AUDIO_MAX_BYTES + no bucket → 413 AudioTooLargeError
//
// Why two paths: sync `recognize` enforces TWO hard caps on Google's side —
// payload ≤ 10 MB AND audio duration ≤ ~60s — regardless of inline vs gs://
// delivery. batchRecognize lifts both and runs async (polled until done).
// We keep sync for small files because it skips the GCS round trip plus
// the operation poll loop, saving ~3–5 s on the hot path.

import { computeGoogleChirpTranscriptionCost } from '../lib/cost-calculator';
import { GOOGLE_CHIRP_INLINE_MAX_BYTES } from '../lib/constants';
import {
  deleteTranscriptionAudio,
  isGcsTranscriptionBucketConfigured,
  uploadTranscriptionAudio,
  type TranscriptionAudioRef,
} from '../lib/gcs-storage';
import { getGoogleAccessToken, invalidateGoogleAccessToken } from '../lib/google-auth';
import { AudioTooLargeError, ProviderUnavailableError } from './types';
import type { ProviderRequestContext, TranscriptionResult } from './types';
import { fetchWithTimeout, logProviderEvent, readErrorBodyPreview } from './utils';

// Re-exported for the transcribe route's pre-buffer header gate. Kept here
// historically; the canonical constant now lives in `lib/constants.ts`.
export const INLINE_AUDIO_MAX_BYTES = GOOGLE_CHIRP_INLINE_MAX_BYTES;
// Sync `recognize` enforces a ~60 s audio-duration cap independent of the
// 10 MB byte cap. Use 55 s as the gate to leave headroom for the byte-rate
// estimator being conservative on compressed audio.
const INLINE_AUDIO_MAX_SECONDS = 55;
const MAX_PHRASES = 1000;
const MAX_PHRASE_LEN = 100;
// batchRecognize polling. Real-world observed: a 90 s audio file on the
// default (immediate) processing path takes just over 90 s end-to-end, so
// 90 s is too aggressive a deadline. 300 s gives headroom for longer
// recordings while staying inside Fly's per-request budget. First poll waits
// only 500 ms (Google sometimes returns done=true almost immediately for the
// inline-result path), subsequent polls every 750 ms keep the spinner tight.
const BATCH_POLL_DEADLINE_MS = 300_000;
const BATCH_POLL_FIRST_DELAY_MS = 500;
const BATCH_POLL_INTERVAL_MS = 750;
const BATCH_POLL_FETCH_TIMEOUT_MS = 8_000;
// Log a `batch_progress` event every N polls so we can see in fly logs how
// long Google is actually taking — useful for tuning the constants above.
const BATCH_PROGRESS_LOG_EVERY = 10;
// Chirp 3 ships only in two multi-regions: `us` (Americas) and `eu` (Europe).
// `global` does NOT host chirp_3 — recognize calls return 400 INVALID_ARGUMENT
// "model 'chirp_3' does not exist in the location named 'global'".
// Ref: cloud.google.com/speech-to-text/v2/docs/chirp_3-model#regional_availability
const DEFAULT_REGION = 'us';

function getRegion(): string {
  const region = process.env.GOOGLE_SPEECH_REGION?.trim();
  return region && region.length > 0 ? region : DEFAULT_REGION;
}

function parsePhraseList(initialPrompt: string): string[] {
  return initialPrompt
    .split(/[,\n;]+/)
    .map(t => t.trim().replace(/^[-*]\s*/, ''))
    .filter(t => t.length > 0 && t.length <= MAX_PHRASE_LEN)
    .slice(0, MAX_PHRASES);
}

function base64Encode(audio: ArrayBuffer): string {
  return Buffer.from(audio).toString('base64');
}

/**
 * Parse an ISO 8601 duration string ("12.345s", "PT1M30S") into seconds.
 * Google Speech V2 returns simple second values like "12.5s" from `metadata`.
 */
function parseIsoDurationToSeconds(duration: string | undefined): number {
  if (!duration) return 0;
  const simple = /^(\d+(?:\.\d+)?)s$/.exec(duration);
  if (simple) {
    return Number.parseFloat(simple[1]);
  }
  const iso = /^PT(?:(\d+)H)?(?:(\d+)M)?(?:(\d+(?:\.\d+)?)S)?$/i.exec(duration);
  if (iso) {
    const hours = iso[1] ? Number.parseInt(iso[1], 10) : 0;
    const minutes = iso[2] ? Number.parseInt(iso[2], 10) : 0;
    const seconds = iso[3] ? Number.parseFloat(iso[3]) : 0;
    return hours * 3600 + minutes * 60 + seconds;
  }
  return 0;
}

export async function transcribeWithGoogleChirp(
  audio: ArrayBuffer,
  contentType: string,
  language?: string,
  initialPrompt?: string,
  context: ProviderRequestContext = {},
): Promise<TranscriptionResult> {
  const startedAt = performance.now();
  const provider = 'google-chirp';

  const projectId = process.env.GOOGLE_PROJECT_ID;
  if (!projectId) {
    throw new Error('GOOGLE_PROJECT_ID not configured');
  }

  // Decide the audio delivery path. Inline is preferred when the payload fits
  // — it avoids two extra GCS round-trips. Larger payloads need a `gs://` URI
  // and v1 only enables that when GOOGLE_SPEECH_GCS_BUCKET is configured.
  // Sync `recognize` enforces BOTH a 10 MB byte cap AND a ~60 s duration cap,
  // so a compressed-audio payload that fits the byte cap can still 400 if it's
  // longer than ~60 s. Gate inline on duration as well as bytes.
  const estimatedSeconds = estimateAudioSeconds(audio.byteLength, contentType);
  const fitsInlineBytes = audio.byteLength <= INLINE_AUDIO_MAX_BYTES;
  const fitsInlineSeconds = estimatedSeconds <= INLINE_AUDIO_MAX_SECONDS;
  const useInlineAudio = fitsInlineBytes && fitsInlineSeconds;
  if (fitsInlineBytes && !fitsInlineSeconds) {
    logProviderEvent(provider, 'inline_disqualified_by_duration', {
      audioBytes: audio.byteLength,
      estimatedSeconds,
      inlineMaxSeconds: INLINE_AUDIO_MAX_SECONDS,
    }, context);
  }
  const gcsConfigured = isGcsTranscriptionBucketConfigured();
  if (!useInlineAudio && !gcsConfigured) {
    logProviderEvent(provider, 'audio_too_large', {
      audioBytes: audio.byteLength,
      inlineMaxBytes: INLINE_AUDIO_MAX_BYTES,
      gcsConfigured: false,
    }, context);
    throw new AudioTooLargeError('Google Chirp', audio.byteLength, INLINE_AUDIO_MAX_BYTES);
  }

  const region = getRegion();
  const syncUrl = `https://${region}-speech.googleapis.com/v2/projects/${projectId}/locations/${region}/recognizers/_:recognize`;
  const batchUrl = `https://${region}-speech.googleapis.com/v2/projects/${projectId}/locations/${region}/recognizers/_:batchRecognize`;

  // Don't lowercase — Speech V2 expects canonical BCP-47 codes (e.g. en-US),
  // and forcing lower-case breaks the region subtag matching.
  const isMonolingual = language && language.toLowerCase() !== 'auto';
  const phrases = initialPrompt ? parsePhraseList(initialPrompt) : [];

  const config: Record<string, unknown> = {
    autoDecodingConfig: {},
    // Per Chirp 3 docs: `languageCodes: ["auto"]` is the documented sentinel
    // for unrestricted automatic language detection. Other forms (empty array,
    // omitted field, "und", null) are rejected by V2. Monolingual requests
    // pass the single BCP-47 code instead.
    // Ref: cloud.google.com/speech-to-text/v2/docs/chirp_3-model
    languageCodes: isMonolingual ? [language!] : ['auto'],
    model: 'chirp_3',
  };
  // Speech V2 model adaptation (phrase biasing) is supported only by the
  // `long`, `short`, and telephony models — NOT by any `chirp*` model.
  // Sending `config.adaptation` against chirp_3 makes Google return
  // 404 NOT_FOUND ("Requested entity was not found"), failing the whole
  // request even though phrases are advisory.
  // Ref: https://cloud.google.com/speech-to-text/v2/docs/adaptation-model
  // We drop phrases silently and log so the call site can decide whether to
  // surface a UI affordance ("vocabulary ignored for this provider").
  const adaptationSupported = false; // chirp_3 only — flip if we add long/short later
  if (phrases.length > 0 && !adaptationSupported) {
    logProviderEvent(provider, 'phrases_dropped_unsupported_model', {
      model: 'chirp_3',
      phraseCount: phrases.length,
    }, context);
  }

  // Allocated before the try so the finally block can clean up even when
  // the upload throws part way through.
  let gcsRef: TranscriptionAudioRef | null = null;
  let currentOperationName: string | null = null;
  const delivery: 'inline' | 'gcs+batch' = useInlineAudio ? 'inline' : 'gcs+batch';

  try {
    logProviderEvent(provider, 'prepare', {
      audioBytes: audio.byteLength,
      contentType,
      language: language || 'auto',
      phraseCount: phrases.length,
      region,
      delivery,
    }, context);

    let normalized: NormalizedTranscript;

    if (useInlineAudio) {
      const bodyObject = { config, content: base64Encode(audio) };
      const accessToken = await getGoogleAccessToken();

      const response = await fetchWithTimeout(provider, syncUrl, {
        method: 'POST',
        headers: {
          'Authorization': `Bearer ${accessToken}`,
          'Content-Type': 'application/json',
        },
        body: JSON.stringify(bodyObject),
      }, context);

      if (!response.ok) {
        await throwForSpeechError(provider, response, startedAt, context);
      }

      const data = await parseJsonBody<SpeechRecognizeResponse>(response, provider, 'sync_recognize', context);
      normalized = normalizeSpeechResults(data.results, data.metadata);
    } else {
      // Upload to GCS, submit a batchRecognize operation, then poll until done.
      // batchRecognize lifts both the 10 MB and ~60 s caps that sync recognize
      // enforces — necessary for long recordings.
      const upload = await uploadTranscriptionAudio(audio, contentType);
      gcsRef = { bucket: upload.bucket, objectName: upload.objectName };

      normalized = await runBatchRecognize({
        region,
        batchUrl,
        gcsUri: upload.gcsUri,
        config,
        provider,
        startedAt,
        context,
        onOperationStarted: (name) => {
          currentOperationName = name;
        },
      });
      // Successful completion — operation is `done: true`, skip the cancel in
      // `finally` so we don't pay for a no-op API call on the happy path.
      currentOperationName = null;
    }

    const transcript = normalized.transcript;
    const detectedLanguage = normalized.languageCode;
    const rawBilled = normalized.billedDuration;

    let durationSeconds = parseIsoDurationToSeconds(rawBilled);
    if (durationSeconds <= 0) {
      // Estimate from byte length using a content-type-aware bytes-per-second
      // table. Raw PCM (32 kB/s) over-bills compressed audio by up to 10× if
      // applied blindly; the table picks a representative rate per codec so
      // an empty-`totalBilledDuration` response still bills a sensible value.
      const estimatedSeconds = estimateAudioSeconds(audio.byteLength, contentType);
      logProviderEvent(provider, 'billed_duration_missing', {
        metadataKeys: normalized.metadataKeys,
        audioBytes: audio.byteLength,
        contentType,
        estimatedSeconds,
      }, context);
      durationSeconds = estimatedSeconds;
    }

    if (!transcript || transcript.length === 0) {
      logProviderEvent(provider, 'no_speech', {
        elapsedMs: Math.round(performance.now() - startedAt),
        detectedLanguage,
      }, context);
      return {
        text: '',
        language: detectedLanguage,
        durationSeconds: 0,
        costUsd: 0,
        source: 'no_speech',
      };
    }

    logProviderEvent(provider, 'success', {
      elapsedMs: Math.round(performance.now() - startedAt),
      transcriptChars: transcript.length,
      durationSeconds,
      detectedLanguage,
      delivery,
    }, context);

    return {
      text: transcript,
      language: detectedLanguage,
      durationSeconds,
      costUsd: computeGoogleChirpTranscriptionCost(durationSeconds),
      source: 'google-chirp',
    };
  } finally {
    // Always delete the scratch object — even on throw. `deleteTranscriptionAudio`
    // swallows its own errors so we never mask the original failure.
    if (gcsRef) {
      await deleteTranscriptionAudio(gcsRef);
    }
    // If a batch operation was submitted but we're unwinding (timeout or
    // throw), cancel it — otherwise Google keeps running and bills us for
    // a result we'll throw away. Submit-race window: if the submit hit our
    // timeout before we read `name`, `currentOperationName` stays null and
    // we can't cancel — the GCS object delete + Google's 404 on the scratch
    // file is the natural failure signal.
    if (currentOperationName) {
      await cancelBatchOperation(region, currentOperationName, provider, context);
    }
  }
}

// Shape returned by both sync recognize and the per-file slice of batchRecognize.
// `metadata.totalBilledDuration` lives on the same object in both responses.
interface SpeechRecognizeResponse {
  results?: Array<{
    alternatives?: Array<{ transcript?: string }>;
    languageCode?: string;
  }>;
  metadata?: Record<string, unknown>;
}

interface NormalizedTranscript {
  transcript: string;
  languageCode: string | undefined;
  billedDuration: string | undefined;
  metadataKeys: string[];
}

function normalizeSpeechResults(
  results: SpeechRecognizeResponse['results'],
  metadata: SpeechRecognizeResponse['metadata'],
): NormalizedTranscript {
  const transcript = (results ?? [])
    .map(r => r.alternatives?.[0]?.transcript ?? '')
    .filter(t => t.length > 0)
    .join(' ')
    .trim();
  const languageCode = results?.find(r => r.languageCode)?.languageCode;

  // Google V2 documents `totalBilledDuration` as "When available, billed
  // audio seconds." The "when available" caveat means we have to defend:
  // missing the field would silently bill 0 and the customer rides free.
  // Try the canonical key, fall back to plausible alternates; the caller
  // estimates from bytes if all fail.
  const meta = (metadata ?? {}) as Record<string, unknown>;
  const pickString = (key: string) =>
    typeof meta[key] === 'string' && (meta[key] as string).length > 0 ? (meta[key] as string) : undefined;
  const billedDuration =
    pickString('totalBilledDuration') ??
    pickString('totalBilledTime') ??
    pickString('billedDuration');

  return {
    transcript,
    languageCode,
    billedDuration,
    metadataKeys: Object.keys(meta),
  };
}

async function throwForSpeechError(
  provider: string,
  response: Response,
  startedAt: number,
  context: ProviderRequestContext,
): Promise<never> {
  const errorText = await readErrorBodyPreview(response);
  const elapsedMs = Math.round(performance.now() - startedAt);
  const kind = response.status >= 500
    ? 'upstream_5xx'
    : response.status === 429
      ? 'rate_limit'
      : 'http_error';

  logProviderEvent(provider, 'http_error', {
    elapsedMs,
    status: response.status,
    kind,
    bodyPreview: errorText,
  }, context);

  if (response.status === 401) {
    throw new Error('Google Speech credentials are invalid or expired');
  }
  if (response.status === 403) {
    // V2 returns 403 for "billing not enabled" as well as "service account
    // lacks roles/speech.editor". Surface both possibilities.
    throw new Error('Google Speech access denied — check service account roles and billing');
  }
  if (response.status === 429) {
    throw new ProviderUnavailableError('Google Chirp', 'rate limit exceeded');
  }
  if (response.status >= 500) {
    throw new ProviderUnavailableError('Google Chirp', `upstream 5xx: ${response.status}`);
  }
  throw new Error(`Google Chirp error: ${response.status}`);
}

interface BatchRecognizeParams {
  region: string;
  batchUrl: string;
  gcsUri: string;
  config: Record<string, unknown>;
  provider: string;
  startedAt: number;
  context: ProviderRequestContext;
  /**
   * Called once `submitData.name` is read, so the outer `finally` can issue
   * an `operations.cancel` if the request later throws or times out. Without
   * this we'd keep paying Google for a result we throw away.
   */
  onOperationStarted?: (operationName: string) => void;
}

/**
 * Submit a batchRecognize long-running operation against the given gs:// URI,
 * then poll the operations endpoint until done (or deadline). Returns the
 * normalized transcript pulled out of `response.results[<uri>].transcript`.
 *
 * batchRecognize is the only Speech V2 path that lifts both the 10 MB payload
 * cap and the ~60 s duration cap that sync `recognize` enforces. We pin
 * `inlineResponseConfig: {}` so results come back in the operation response
 * itself — no second GCS round trip to fetch them from a bucket.
 */
async function runBatchRecognize(params: BatchRecognizeParams): Promise<NormalizedTranscript> {
  const { region, batchUrl, gcsUri, config, provider, startedAt, context, onOperationStarted } = params;
  const accessToken = await getGoogleAccessToken();

  const submitBody = {
    config,
    files: [{ uri: gcsUri }],
    recognitionOutputConfig: { inlineResponseConfig: {} },
    // No `processingStrategy` here — that field defaults to
    // `PROCESSING_STRATEGY_UNSPECIFIED`, which is Google's IMMEDIATE path
    // (results within seconds-to-minutes). The named `DYNAMIC_BATCHING`
    // enum is the opposite: a deferred low-cost queue fulfilled within 24
    // hours. We want immediate fulfilment for interactive transcription;
    // omitting the field is the correct way to opt in.
  };

  const submitResponse = await fetchWithTimeout(provider, batchUrl, {
    method: 'POST',
    headers: {
      'Authorization': `Bearer ${accessToken}`,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify(submitBody),
  }, context);

  if (!submitResponse.ok) {
    await throwForSpeechError(provider, submitResponse, startedAt, context);
  }

  const submitData = await parseJsonBody<{ name?: string }>(submitResponse, provider, 'batch_submit', context);
  if (!submitData.name) {
    throw new Error('Google Speech batchRecognize did not return an operation name');
  }
  const operationName = submitData.name;
  const operationUrl = `https://${region}-speech.googleapis.com/v2/${operationName}`;
  onOperationStarted?.(operationName);

  logProviderEvent(provider, 'batch_submitted', {
    operationName,
    submitMs: Math.round(performance.now() - startedAt),
  }, context);

  const deadline = performance.now() + BATCH_POLL_DEADLINE_MS;
  const pollStart = performance.now();
  let pollAttempts = 0;
  let refreshedToken = false;
  let pollAccessToken = accessToken;
  await sleep(BATCH_POLL_FIRST_DELAY_MS);

  while (performance.now() < deadline) {
    pollAttempts++;
    let pollData: BatchOperationResponse;
    try {
      pollData = await pollOperation(operationUrl, pollAccessToken, provider, startedAt, context);
    } catch (error) {
      // One-shot token refresh: if the cached token expired mid-poll,
      // invalidate the Redis entry, re-mint, and retry this iteration.
      // A second 401 surfaces unwrapped — matches the sync-path behaviour.
      const message = error instanceof Error ? error.message : String(error);
      if (!refreshedToken && message.includes('Google Speech credentials are invalid or expired')) {
        await invalidateGoogleAccessToken();
        pollAccessToken = await getGoogleAccessToken();
        refreshedToken = true;
        logProviderEvent(provider, 'batch_token_refreshed', {
          attempts: pollAttempts,
          elapsedMs: Math.round(performance.now() - startedAt),
        }, context);
        continue;
      }
      throw error;
    }
    if (pollAttempts % BATCH_PROGRESS_LOG_EVERY === 0) {
      logProviderEvent(provider, 'batch_progress', {
        attempts: pollAttempts,
        pollElapsedMs: Math.round(performance.now() - pollStart),
        progressPercent: pollData.metadata?.progressPercent,
      }, context);
    }

    if (pollData.done) {
      logProviderEvent(provider, 'batch_done', {
        attempts: pollAttempts,
        elapsedMs: Math.round(performance.now() - startedAt),
      }, context);

      if (pollData.error) {
        throw new Error(
          `Google Speech batchRecognize failed (${pollData.error.code}): ${pollData.error.message}`,
        );
      }

      const results = pollData.response?.results ?? {};
      // batchRecognize keys results by the input file URI, so we look up our
      // own gs:// URI. Defensive fallback: if Google changes the keying we
      // still pull the first file's transcript rather than zero-billing.
      const fileResult = results[gcsUri] ?? Object.values(results)[0];
      if (!fileResult) {
        // No fileResult means we silently zero-bill — emit an event so
        // operators can alert on it. Mirrors `billed_duration_missing` above.
        logProviderEvent(provider, 'batch_empty_results', {
          operationName,
          resultsKeys: Object.keys(results),
          gcsUri,
        }, context);
        return { transcript: '', languageCode: undefined, billedDuration: undefined, metadataKeys: [] };
      }

      if (fileResult.error) {
        throw new Error(
          `Google Speech batchRecognize file error (${fileResult.error.code}): ${fileResult.error.message}`,
        );
      }

      // batchRecognize's `totalBilledDuration` lives on `fileResult.metadata`,
      // NOT on `fileResult.transcript.metadata` (where sync recognize puts it
      // and where the V2 schema docs imply). `transcript.metadata.prompt` is
      // unrelated config echo. Merge the candidate metadata levels so we pick
      // up the billed duration wherever Google places it — if they move it
      // again the merge still wins, and the byte-estimate stays as a backstop.
      return normalizeSpeechResults(
        fileResult.transcript?.results,
        mergeMetadata(
          pollData.metadata as Record<string, unknown> | undefined,
          fileResult.transcript?.metadata,
          fileResult.metadata,
        ),
      );
    }

    await sleep(BATCH_POLL_INTERVAL_MS);
  }

  logProviderEvent(provider, 'batch_timeout', {
    attempts: pollAttempts,
    deadlineMs: BATCH_POLL_DEADLINE_MS,
  }, context);
  throw new ProviderUnavailableError(
    'Google Chirp',
    `batchRecognize did not complete within ${BATCH_POLL_DEADLINE_MS}ms`,
  );
}

interface BatchOperationResponse {
  done?: boolean;
  error?: { code: number; message: string };
  metadata?: {
    progressPercent?: number;
    [key: string]: unknown;
  };
  response?: {
    results?: Record<string, {
      // batchRecognize wraps each file's results in `transcript` (results +
      // prompt-echo metadata) AND separately exposes `metadata` at the file
      // level — that's where `totalBilledDuration` actually lives.
      transcript?: SpeechRecognizeResponse;
      metadata?: Record<string, unknown>;
      error?: { code: number; message: string };
    }>;
  };
}

async function pollOperation(
  url: string,
  accessToken: string,
  provider: string,
  startedAt: number,
  context: ProviderRequestContext,
): Promise<BatchOperationResponse> {
  const response = await fetchWithTimeout(
    provider,
    url,
    {
      method: 'GET',
      headers: { 'Authorization': `Bearer ${accessToken}` },
    },
    context,
    BATCH_POLL_FETCH_TIMEOUT_MS,
  );

  if (!response.ok) {
    // 429 / 5xx on poll → retry next tick. Any other failure surfaces via
    // throwForSpeechError so 401 → "credentials invalid/expired" and others
    // hit the typed-error contract transcribe.ts routes on.
    if (response.status === 429 || response.status >= 500) {
      logProviderEvent(provider, 'batch_poll_transient', {
        status: response.status,
      }, context);
      return { done: false };
    }
    await throwForSpeechError(provider, response, startedAt, context);
  }

  return parseJsonBody<BatchOperationResponse>(response, provider, 'batch_poll', context);
}

function sleep(ms: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, ms));
}

/**
 * Best-effort cancel of an in-flight batchRecognize operation. Mirrors
 * `deleteTranscriptionAudio`: errors are swallowed and logged so cleanup
 * never masks the original failure. Used by the outer `finally` to stop
 * Google from charging us for a result we're about to throw away.
 */
async function cancelBatchOperation(
  region: string,
  operationName: string,
  provider: string,
  context: ProviderRequestContext,
): Promise<void> {
  try {
    // Mint a fresh token rather than reusing the (possibly stale-after-refresh)
    // poll-loop token. Mirrors `deleteTranscriptionAudio`'s pattern.
    const accessToken = await getGoogleAccessToken();
    const url = `https://${region}-speech.googleapis.com/v2/${operationName}:cancel`;
    const response = await fetchWithTimeout(
      provider,
      url,
      {
        method: 'POST',
        headers: {
          'Authorization': `Bearer ${accessToken}`,
          'Content-Type': 'application/json',
        },
        body: '{}',
      },
      context,
      BATCH_POLL_FETCH_TIMEOUT_MS,
    );
    logProviderEvent(provider, 'operation_cancelled', {
      operationName,
      status: response.status,
    }, context);
  } catch (error) {
    logProviderEvent(provider, 'operation_cancel_failed', {
      operationName,
      message: error instanceof Error ? error.message : String(error),
    }, context);
  }
}

/**
 * Merge candidate metadata objects, last-write-wins for any keys present in
 * more than one. Used to flatten the multiple levels at which batchRecognize
 * scatters billed-duration info (per-transcript, per-file, per-operation).
 */
function mergeMetadata(
  ...sources: Array<Record<string, unknown> | undefined>
): Record<string, unknown> {
  const merged: Record<string, unknown> = {};
  for (const src of sources) {
    if (!src) continue;
    for (const [k, v] of Object.entries(src)) {
      if (v !== undefined && v !== null) {
        merged[k] = v;
      }
    }
  }
  return merged;
}

/**
 * Bun's `response.json()` has been observed to throw "Failed to parse JSON" on
 * gzip+chunked 200 responses (same quirk that bit ElevenLabs). Read as text
 * first and JSON.parse — bypasses the Bun bug and surfaces actionable
 * diagnostics if the body ever becomes genuinely empty or non-JSON.
 */
async function parseJsonBody<T>(
  response: Response,
  provider: string,
  phase: string,
  context: ProviderRequestContext,
): Promise<T> {
  const raw = await response.text();
  if (!raw) {
    const ct = response.headers.get('content-type') ?? 'unknown';
    const ce = response.headers.get('content-encoding') ?? 'none';
    logProviderEvent(provider, 'empty_body', { phase, contentType: ct, contentEncoding: ce }, context);
    throw new Error(`Google Speech returned empty 200 body during ${phase} (content-type=${ct}, content-encoding=${ce})`);
  }
  try {
    return JSON.parse(raw) as T;
  } catch {
    const ct = response.headers.get('content-type') ?? 'unknown';
    logProviderEvent(provider, 'parse_error', {
      phase,
      contentType: ct,
      bodyLength: raw.length,
      bodyPreview: raw.slice(0, 400),
    }, context);
    throw new Error(`Google Speech returned non-JSON 200 body during ${phase} (content-type=${ct}, len=${raw.length}): ${raw.slice(0, 200)}`);
  }
}

/**
 * Estimate audio duration from byte length using a representative bytes-per-second
 * rate for the given content type. Used as a fallback when Google's
 * `totalBilledDuration` is missing from the response. Over-bills slightly on
 * compressed audio and under-bills slightly on raw — both preferable to
 * zero-billing a real transcription.
 */
function estimateAudioSeconds(byteLength: number, contentType: string): number {
  const lower = (contentType || '').toLowerCase();
  let bytesPerSecond = 16_000;
  if (lower.includes('wav') || lower.includes('pcm')) {
    bytesPerSecond = 32_000;
  } else if (lower.includes('opus') || lower.includes('webm')) {
    bytesPerSecond = 8_000;
  } else if (lower.includes('flac') || lower.includes('ogg')) {
    bytesPerSecond = 32_000;
  } else if (lower.includes('mp3') || lower.includes('mpeg')) {
    bytesPerSecond = 16_000;
  } else if (lower.includes('m4a') || lower.includes('mp4') || lower.includes('aac')) {
    bytesPerSecond = 16_000;
  }
  return byteLength / bytesPerSecond;
}
