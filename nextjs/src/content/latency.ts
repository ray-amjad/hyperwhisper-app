import "server-only";

import { and, eq, gte, sql } from "drizzle-orm";

import { db } from "@/src/db";
import { sttLatencySamples } from "@/src/db/schema/stt-latency-samples";
import {
  DEFAULT_BUCKET,
  DURATION_BUCKETS,
  WINDOW_DAYS,
  type DurationBucket,
  type LatencyCell,
  type LatencyMatrixData,
} from "@/lib/latency/types";

function toNumber(value: unknown): number {
  const parsed = typeof value === "number" ? value : Number(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

/**
 * Aggregates the trailing 30-day window for one clip-length bucket, grouped by
 * provider and region. Postgres computes the percentiles — pulling raw rows into
 * the page and sorting them in JavaScript would not survive real volume. The
 * (created_at, duration_bucket, provider, fly_region) index matches this shape.
 *
 * Percentiles cover every attempt, failed ones included: a timeout is time the
 * user actually waited, so excluding it would flatter the slowest providers. The
 * error-rate metric gives the other view.
 *
 * A database failure is deliberately allowed to propagate. The page is static
 * with an hourly revalidate, and Next treats a returned value — empty data
 * included — as a successful render: swallowing the error would cache "0
 * provider attempts over the last 30 days" for the next hour because Postgres
 * was briefly unreachable during one background revalidation. Throwing instead
 * leaves the last good page in the ISR cache, which is both correct and fresher
 * than anything this function could invent. (blog.ts catches, but it revalidates
 * 60x more often, so a bad render there ages out in a minute.)
 *
 * An empty TABLE is not a failure and does not throw: no rows means no cells,
 * and the page renders its "no measurements yet" state.
 */
export async function getLatencyMatrix(
  bucket: DurationBucket = DEFAULT_BUCKET,
): Promise<LatencyMatrixData> {
  const since = new Date(Date.now() - WINDOW_DAYS * 24 * 60 * 60 * 1000);

  const rows = await db
    .select({
      provider: sttLatencySamples.provider,
      region: sttLatencySamples.flyRegion,
      samples: sql<string>`count(*)`,
      p50: sql<string>`percentile_cont(0.5) within group (order by ${sttLatencySamples.latencyMs})`,
      p95: sql<string>`percentile_cont(0.95) within group (order by ${sttLatencySamples.latencyMs})`,
      p99: sql<string>`percentile_cont(0.99) within group (order by ${sttLatencySamples.latencyMs})`,
      errorRate: sql<string>`avg(case when ${sttLatencySamples.ok} then 0 else 1 end)`,
    })
    .from(sttLatencySamples)
    .where(
      and(
        gte(sttLatencySamples.createdAt, since),
        eq(sttLatencySamples.durationBucket, bucket),
      ),
    )
    .groupBy(sttLatencySamples.provider, sttLatencySamples.flyRegion);

  const cells: LatencyCell[] = rows.map((row) => ({
    provider: row.provider,
    region: row.region,
    samples: toNumber(row.samples),
    p50: Math.round(toNumber(row.p50)),
    p95: Math.round(toNumber(row.p95)),
    p99: Math.round(toNumber(row.p99)),
    errorRate: toNumber(row.errorRate),
  }));

  // Axes come from what the data holds. There is no declared region list in
  // this repo, and a hardcoded one would rot the first time a region is added
  // or retired.
  const providers = Array.from(new Set(cells.map((cell) => cell.provider))).sort();
  const regions = Array.from(new Set(cells.map((cell) => cell.region))).sort();
  const totalSamples = cells.reduce((sum, cell) => sum + cell.samples, 0);

  return {
    cells,
    providers,
    regions,
    totalSamples,
    windowDays: WINDOW_DAYS,
  };
}

/** Loads every bucket up front, so the page's selector needs no round trip. */
export async function getAllLatencyMatrices(): Promise<
  Record<DurationBucket, LatencyMatrixData>
> {
  const [short, medium, long] = await Promise.all(
    DURATION_BUCKETS.map((bucket) => getLatencyMatrix(bucket)),
  );
  return { short, medium, long };
}