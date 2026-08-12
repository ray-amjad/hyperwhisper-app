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
 * True while `next build` is prerendering, false in the server that serves and
 * revalidates the built page. Next sets NEXT_PHASE for the build and its render
 * workers inherit it; the runtime never has it set to this value.
 *
 * The two moments need opposite behaviour on a database failure (see
 * getLatencyMatrix), and this is the only thing that tells them apart from
 * inside a `force-static` page — there is no request, no headers and no cache
 * entry to consult. Read once at module load, which is already inside the
 * worker that will do the rendering.
 */
const IS_BUILD_PRERENDER = process.env.NEXT_PHASE === "phase-production-build";

/** The shape a bucket with no rows has. Also what a build-time failure renders. */
function emptyMatrix(): LatencyMatrixData {
  return { cells: [], providers: [], regions: [], totalSamples: 0, windowDays: WINDOW_DAYS };
}

/**
 * Aggregates the trailing 30-day window for one clip-length bucket, grouped by
 * provider and region. Postgres computes the percentiles — pulling raw rows into
 * the page and sorting them in JavaScript would not survive real volume. The
 * (duration_bucket, created_at, provider, fly_region) index matches this shape.
 *
 * Percentiles cover every attempt, failed ones included: a timeout is time the
 * user actually waited, so excluding it would flatter the slowest providers. The
 * error-rate metric gives the other view.
 *
 * A database failure means two different things depending on when it happens,
 * so it is handled two different ways:
 *
 *  - During `next build`, /en/latency is prerendered like every other static
 *    page. Throwing there fails `next build` outright — "Export encountered an
 *    error, exiting the build" — and takes every unrelated page of the deploy
 *    with it, for a page whose entire content is a 30-day average. A deploy must
 *    not depend on Postgres being reachable from the builder, so at build time
 *    the failure is logged and the page ships in its empty state; the first
 *    revalidation an hour later fills it in.
 *  - At runtime the error propagates. Next treats a returned value — empty data
 *    included — as a successful render, so swallowing it during a background
 *    revalidation would REPLACE a good page with "0 provider attempts over the
 *    last 30 days" and cache that for an hour. Throwing leaves the last good
 *    page in the ISR cache, which is both correct and fresher than anything this
 *    function could invent, and Next retries on the next request.
 *
 * An empty TABLE is not a failure on either path: no rows means no cells, and
 * the page renders its "no measurements yet" state.
 */
export async function getLatencyMatrix(
  bucket: DurationBucket = DEFAULT_BUCKET,
): Promise<LatencyMatrixData> {
  try {
    return await queryLatencyMatrix(bucket);
  } catch (error) {
    if (!IS_BUILD_PRERENDER) {
      throw error;
    }
    console.error(
      `[latency] database unreachable while prerendering bucket "${bucket}"; ` +
        "shipping the empty state and letting the hourly revalidate fill it in:",
      error,
    );
    return emptyMatrix();
  }
}

async function queryLatencyMatrix(bucket: DurationBucket): Promise<LatencyMatrixData> {
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