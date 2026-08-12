/**
 * Pure logic for the anonymous STT latency ingest — what a row must look like to
 * be stored, and how its timestamp is coarsened. Kept out of route.ts so it is
 * unit-testable with `node --test` (there is no render/route harness in this
 * repo). No database, no request, no env access here.
 */

const HOUR_MS = 60 * 60 * 1000;

/**
 * The `created_at` an ingested row is stored with: the hour it arrived in, not
 * the instant.
 *
 * One transcription's whole fallback chain arrives in a single multi-row INSERT,
 * and Postgres `now()` is the transaction timestamp — so the column default
 * would stamp every row of one request with the same microsecond value.
 * Grouping on that plus the region would reassemble each original request and
 * its provider chain, which is exactly the correlation this table promises not
 * to hold. At hour granularity a row is indistinguishable from every other row
 * written in that hour and region.
 *
 * The page's only query is a 30-day window aggregate, so hour-level timestamps
 * change nothing it reports; the retention prune deletes on age alone and is
 * likewise unaffected.
 */
export function coarseCreatedAt(now: number = Date.now()): Date {
  return new Date(Math.floor(now / HOUR_MS) * HOUR_MS);
}

export const FAILURE_KINDS = [
  "timeout",
  "rate_limit",
  "upstream_5xx",
  "bad_response",
  "network_error",
  "input_rejected",
  "unknown",
] as const;

/** One transcription runs at most a handful of fallback attempts. */
export const MAX_SAMPLES_PER_REQUEST = 20;

/**
 * Ceiling on a reported attempt — a CLAMP, not a rejection.
 *
 * The two are not interchangeable. Rejecting drops the whole row, and the rows
 * that would breach any plausible ceiling are exactly the slowest attempts the
 * page exists to show: the "Over 30 seconds" bucket's percentiles deliberately
 * include timeouts, and its error rate counts failures, so silently deleting
 * the longest attempts biases p95, p99 AND the error rate downward at once.
 * Clamping keeps the row and compresses only the very top of the tail.
 *
 * The value is set above anything the edge service can legitimately produce, so
 * the clamp should never bind in practice. Its worst case is one attempt's
 * upload budget: `computeUploadTimeoutMs` is max(30 s, 1 s per 100 KB) and the
 * route accepts up to MAX_AUDIO_SIZE_BYTES = 2 GB, which is ~21,475 s of upload
 * on its own, plus AssemblyAI/Soniox/Chirp job polling on top. Six hours covers
 * that with room to spare; anything past it is a broken clock, and one clamped
 * row is a better answer than a hole in the tail.
 */
export const MAX_LATENCY_MS = 6 * 60 * 60 * 1000;

/**
 * Ceiling on the reported clip length — likewise a clamp. 2 GB of Opus is over
 * 70 hours, so a legitimate report can exceed a day; and since the value is only
 * ever used to pick a bucket, and everything past 30 s is already 'long',
 * clamping cannot move a row into the wrong cell.
 */
export const MAX_AUDIO_SECONDS = 24 * 60 * 60;
export const MAX_ATTEMPT = 5;
const MAX_MODEL_LENGTH = 120;

export type ValidSample = {
  provider: string;
  model: string | null;
  flyRegion: string;
  audioSeconds: number | null;
  latencyMs: number;
  ok: boolean;
  failureKind: string | null;
  attempt: number;
};

export type ValidationResult =
  | { ok: true; samples: ValidSample[]; skipped: { index: number; reason: string }[] }
  | { ok: false; error: string };

function isPositiveInt(value: unknown): value is number {
  // Number.isInteger already excludes NaN and ±Infinity, so an unbounded check
  // is still closed against garbage — it just no longer throws away the tail.
  return typeof value === "number" && Number.isInteger(value) && value > 0;
}

/** Keeps a row that overshoots a ceiling instead of dropping it. */
function clamp(value: number, max: number): number {
  return value > max ? max : value;
}

/**
 * Fly region codes are exactly three lowercase letters (`fra`, `iad`).
 *
 * Anything else is rejected, `local` included: a machine off Fly reports that,
 * and the public page derives its region axis from whatever rows exist, so one
 * developer running the edge service against production would raise a "Local
 * machine" column on hyperwhisper.com. The edge service already declines to
 * report when FLY_REGION is unset; this is the second half of the same rule, on
 * the side that owns the table.
 */
function isValidRegion(value: unknown): value is string {
  return typeof value === "string" && /^[a-z]{3}$/.test(value);
}

/**
 * Validates one sample against the provider ids the site knows.
 *
 * `knownProviders` is passed in rather than imported so this module keeps no
 * imports at all: `node --test --experimental-strip-types` loads it directly and
 * resolves neither the `@/` alias nor an extensionless relative `.ts` path. The
 * one list the ingest actually runs on is `KNOWN_PROVIDERS` in
 * lib/latency/providers.ts, derived from the page's display map — route.ts
 * hands it in, and the test loads the same export.
 */
export function validateSample(
  raw: unknown,
  knownProviders: readonly string[],
): { sample: ValidSample } | { reason: string } {
  if (typeof raw !== "object" || raw === null || Array.isArray(raw)) {
    return { reason: "not an object" };
  }
  const s = raw as Record<string, unknown>;

  if (typeof s.provider !== "string" || !knownProviders.includes(s.provider)) {
    return { reason: "unknown provider" };
  }
  if (!isValidRegion(s.flyRegion)) {
    return { reason: "invalid flyRegion" };
  }
  if (!isPositiveInt(s.latencyMs)) {
    return { reason: "invalid latencyMs" };
  }
  // `attempt` stays a rejection: it is a position in a fallback chain of at most
  // four, so a value past MAX_ATTEMPT is a malformed report rather than a long
  // one, and clamping it would file the row against the wrong attempt number.
  if (!isPositiveInt(s.attempt) || s.attempt > MAX_ATTEMPT) {
    return { reason: "invalid attempt" };
  }
  if (typeof s.ok !== "boolean") {
    return { reason: "invalid ok" };
  }

  let audioSeconds: number | null = null;
  if (s.audioSeconds !== undefined && s.audioSeconds !== null) {
    if (
      typeof s.audioSeconds !== "number" ||
      !Number.isInteger(s.audioSeconds) ||
      s.audioSeconds < 0
    ) {
      return { reason: "invalid audioSeconds" };
    }
    audioSeconds = clamp(s.audioSeconds, MAX_AUDIO_SECONDS);
  }

  // A sample with no length cannot be compared fairly against one that has a
  // length, so it never reaches the table.
  if (audioSeconds === null) {
    return { reason: "missing audioSeconds" };
  }

  let failureKind: string | null = null;
  if (!s.ok) {
    if (typeof s.failureKind !== "string" || !(FAILURE_KINDS as readonly string[]).includes(s.failureKind)) {
      return { reason: "invalid failureKind" };
    }
    failureKind = s.failureKind;
  }

  let model: string | null = null;
  if (typeof s.model === "string" && s.model.length > 0) {
    if (s.model.length > MAX_MODEL_LENGTH) {
      return { reason: "model too long" };
    }
    model = s.model;
  }

  return {
    sample: {
      provider: s.provider,
      model,
      flyRegion: s.flyRegion,
      audioSeconds,
      latencyMs: clamp(s.latencyMs, MAX_LATENCY_MS),
      ok: s.ok,
      failureKind,
      attempt: s.attempt,
    },
  };
}

/**
 * Validates the whole batch. A malformed sample is skipped and reported by its
 * index — one bad row never costs the caller the good rows beside it. Only a
 * malformed envelope fails the whole request.
 */
export function validateBatch(
  body: unknown,
  knownProviders: readonly string[],
): ValidationResult {
  if (typeof body !== "object" || body === null) {
    return { ok: false, error: "body must be an object" };
  }
  const samples = (body as Record<string, unknown>).samples;
  if (!Array.isArray(samples)) {
    return { ok: false, error: "samples must be an array" };
  }
  if (samples.length === 0) {
    return { ok: false, error: "samples must not be empty" };
  }
  if (samples.length > MAX_SAMPLES_PER_REQUEST) {
    return { ok: false, error: `at most ${MAX_SAMPLES_PER_REQUEST} samples per request` };
  }

  const valid: ValidSample[] = [];
  const skipped: { index: number; reason: string }[] = [];
  samples.forEach((raw, index) => {
    const result = validateSample(raw, knownProviders);
    if ("sample" in result) {
      valid.push(result.sample);
    } else {
      skipped.push({ index, reason: result.reason });
    }
  });

  return { ok: true, samples: valid, skipped };
}
