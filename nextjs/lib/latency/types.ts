/**
 * Shapes and constants shared by the /latency page's server query, its client
 * component, and the ingest route that writes the rows. Kept out of
 * `src/content/latency.ts` because that module is `server-only` — a client
 * component importing it fails the build.
 */

/**
 * The clip-length model: one bucket per entry, in ascending order, each with
 * the human label the page prints for it and the largest whole second it holds
 * (`maxSeconds: null` on the open-ended last one).
 *
 * This is the only place the boundaries and their wording are written down. The
 * bucket ids, the labels, and the ingest's bucketing all derive from it, so a
 * boundary change can never leave the page describing a cell it no longer
 * contains.
 */
export const DURATION_BUCKET_MODEL = [
  { id: "short", label: "Under 10 seconds", maxSeconds: 9 },
  { id: "medium", label: "10 to 30 seconds", maxSeconds: 30 },
  { id: "long", label: "Over 30 seconds", maxSeconds: null },
] as const;

export type DurationBucket = (typeof DURATION_BUCKET_MODEL)[number]["id"];

export const DURATION_BUCKETS: readonly DurationBucket[] = DURATION_BUCKET_MODEL.map(
  (bucket) => bucket.id,
);

/**
 * Which bucket a clip length belongs to. Clip lengths are stored as whole
 * seconds, so the inclusive `maxSeconds` boundaries above cover every value.
 */
export function bucketForSeconds(seconds: number): DurationBucket {
  const match = DURATION_BUCKET_MODEL.find(
    (bucket) => bucket.maxSeconds === null || seconds <= bucket.maxSeconds,
  );
  return (match ?? DURATION_BUCKET_MODEL[DURATION_BUCKET_MODEL.length - 1]).id;
}

/** The bucket most clips land in, so the page opens on the busiest data. */
export const DEFAULT_BUCKET: DurationBucket = "short";

/** Trailing window the page reports on. Raw rows live for a year. */
export const WINDOW_DAYS = 30;

/**
 * A cell below this many attempts shows "not enough data" instead of a number.
 * A p95 drawn from a handful of samples is noise wearing a percentile's clothes.
 */
export const MIN_SAMPLES_PER_CELL = 20;

/**
 * p99 needs its own, far higher bar.
 *
 * `percentile_cont(0.99)` interpolates at index `0.99 × (n - 1)`, so at the p50
 * threshold of 20 attempts it lands at 18.81 — between the two slowest calls the
 * cell has ever made. Nineteen 300 ms calls and one 15-second timeout would
 * print ≈12,200 ms as though it were a stable figure, and one more slow call the
 * next day would move it by seconds. At 500 attempts the reported value has five
 * observations above it, so a single outlier shifts it by tens of milliseconds
 * rather than seconds. A cell that cannot clear this shows a dash under p99 and
 * still shows its p50 and p95.
 */
export const MIN_SAMPLES_FOR_P99 = 500;

/** The metrics the page can display, and the one dimension its cells vary on. */
export type LatencyMetric = "p50" | "p95" | "p99" | "errorRate";

/**
 * How many attempts a cell needs before this metric is worth printing. Every
 * metric but p99 rides on the same threshold; see MIN_SAMPLES_FOR_P99.
 */
export function minSamplesForMetric(metric: LatencyMetric): number {
  return metric === "p99" ? MIN_SAMPLES_FOR_P99 : MIN_SAMPLES_PER_CELL;
}

export const BUCKET_LABELS: Record<DurationBucket, string> = Object.fromEntries(
  DURATION_BUCKET_MODEL.map((bucket) => [bucket.id, bucket.label]),
) as Record<DurationBucket, string>;

export type LatencyCell = {
  provider: string;
  region: string;
  samples: number;
  p50: number;
  p95: number;
  p99: number;
  /** 0-1. Share of attempts that failed. */
  errorRate: number;
};

export type LatencyMatrixData = {
  cells: LatencyCell[];
  providers: string[];
  regions: string[];
  totalSamples: number;
  windowDays: number;
};