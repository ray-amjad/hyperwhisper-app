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
/** A provider call that takes longer than this is a bug, not a measurement. */
export const MAX_LATENCY_MS = 15 * 60 * 1000;
/** Longest clip we accept a length for. Anything above is a malformed report. */
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

function isPositiveInt(value: unknown, max: number): value is number {
  return typeof value === "number" && Number.isInteger(value) && value > 0 && value <= max;
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
  if (!isPositiveInt(s.latencyMs, MAX_LATENCY_MS)) {
    return { reason: "invalid latencyMs" };
  }
  if (!isPositiveInt(s.attempt, MAX_ATTEMPT)) {
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
      s.audioSeconds < 0 ||
      s.audioSeconds > MAX_AUDIO_SECONDS
    ) {
      return { reason: "invalid audioSeconds" };
    }
    audioSeconds = s.audioSeconds;
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
      latencyMs: s.latencyMs,
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
