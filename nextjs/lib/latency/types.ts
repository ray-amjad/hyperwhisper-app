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

/**
 * Trailing window the page reports on. Raw rows live for a year, so this can
 * move without a data migration.
 *
 * 90 days rather than 30 because the page now cuts each vendor into its models,
 * and a model row counts only the attempts that ran on that model. A 30-day
 * window left most model rows under MIN_SAMPLES_PER_CELL, and p99 needs 500
 * attempts in a single cell — a bar a per-model cell reaches only with a window
 * this long. The cost is that a provider's recent improvement takes longer to
 * show; the page states the window in its header, so nobody reads a 90-day
 * figure as a reading of today.
 */
export const WINDOW_DAYS = 90;

/**
 * A cell below this many attempts shows "not enough data" instead of a number.
 *
 * Deliberately low. The honest statistical bar is far higher — a p95 from three
 * calls is that cell's slowest call wearing a percentile's clothes — but a page
 * of nothing but dashes tells a visitor less than a rough number does, and the
 * table already carries the caveat in two places: the footnote states the bar,
 * and every populated cell puts its own attempt count in the hover title, so a
 * three-attempt cell never passes itself off as a settled figure. Three is the
 * floor at which a median has a middle value that is not simply one of the two
 * extremes.
 *
 * Raise this as the dataset fills in. p99 is exempt and always has been; see
 * MIN_SAMPLES_FOR_P99 for the arithmetic that makes it a different question.
 */
export const MIN_SAMPLES_PER_CELL = 3;

/**
 * p99 needs its own, far higher bar.
 *
 * `percentile_cont(0.99)` interpolates at index `0.99 × (n - 1)`, so at the p50
 * threshold of 3 attempts it lands at 1.98 — 98% of the way from the cell's
 * second-slowest call to its slowest. Two 300 ms calls and one 15-second timeout
 * would print ≈14,700 ms as though it were a stable figure, and one more slow
 * call the next day would move it by seconds. The same holds at 20 attempts
 * (index 18.81) and at any cell size a young dataset reaches: p99 does not
 * become meaningful by lowering the bar, only by collecting more calls. At 500
 * attempts the reported value has five observations above it, so a single
 * outlier shifts it by tens of milliseconds rather than seconds. A cell that
 * cannot clear this shows a dash under p99 and still shows its p50 and p95.
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

/** One region's numbers for one row of the table. */
export type LatencyCell = {
  region: string;
  samples: number;
  p50: number;
  p95: number;
  p99: number;
  /** 0-1. Share of attempts that failed. */
  errorRate: number;
};

/**
 * One model of one vendor — a row the table shows only while "Break down by
 * model" is on.
 *
 * `provider` is the backend id the model belongs to, kept beside the model id
 * because a vendor can span several: selecting a Gemini model under the merged
 * Google row is what moves the backend provider from google-chirp to gemini.
 */
export type LatencyModelRow = {
  provider: string;
  /** Null for a provider whose endpoint takes no model id. */
  model: string | null;
  label: string;
  /** The model a fresh install lands on, badged as such in the table. */
  isDefault: boolean;
  cells: LatencyCell[];
};

/**
 * One vendor — a row of the table, named the way the app's Provider dropdown
 * names it.
 *
 * Its cells are NOT a blend of the model rows below it. Percentiles do not
 * average, so both levels are aggregated separately in Postgres over the same
 * rows; see src/content/latency.ts.
 */
export type LatencyVendorRow = {
  /** Catalog `vendor` key. Google covers both Chirp and Gemini. */
  vendor: string;
  label: string;
  cells: LatencyCell[];
  /** In the order the app's Model dropdown lists them. */
  models: LatencyModelRow[];
};

export type LatencyMatrixData = {
  vendors: LatencyVendorRow[];
  regions: string[];
  totalSamples: number;
  windowDays: number;
};