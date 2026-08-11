/**
 * Shapes and constants shared by the /latency page's server query and its
 * client component. Kept out of `src/content/latency.ts` because that module is
 * `server-only` — a client component importing it fails the build.
 */

export const DURATION_BUCKETS = ["short", "medium", "long"] as const;
export type DurationBucket = (typeof DURATION_BUCKETS)[number];

/** The bucket most clips land in, so the page opens on the busiest data. */
export const DEFAULT_BUCKET: DurationBucket = "short";

/** Trailing window the page reports on. Raw rows live for a year. */
export const WINDOW_DAYS = 30;

/**
 * A cell below this many attempts shows "not enough data" instead of a number.
 * A p95 drawn from a handful of samples is noise wearing a percentile's clothes.
 */
export const MIN_SAMPLES_PER_CELL = 20;

export const BUCKET_LABELS: Record<DurationBucket, string> = {
  short: "Under 10 seconds",
  medium: "10 to 30 seconds",
  long: "Over 30 seconds",
};

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
  minSamplesPerCell: number;
};