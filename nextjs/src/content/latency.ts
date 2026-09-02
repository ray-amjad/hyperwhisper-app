import "server-only";

import { and, eq, gte, notInArray, sql } from "drizzle-orm";

import { db } from "@/src/db";
import { sttLatencySamples } from "@/src/db/schema/stt-latency-samples";
import {
  DEFAULT_BUCKET,
  DURATION_BUCKETS,
  WINDOW_DAYS,
  type DurationBucket,
  type LatencyCell,
  type LatencyMatrixData,
  type LatencyModelRow,
  type LatencyVendorRow,
} from "@/lib/latency/types";
import {
  RETIRED_PROVIDERS,
  STT_CATALOG,
  isDefaultModel,
  modelDisplayName,
  modelSortIndex,
  vendorDisplayName,
} from "@/lib/latency/providers";

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

/**
 * `provider` mapped to its catalog vendor, in SQL, so Postgres can group at the
 * level the table's rows are named at.
 *
 * The mapping cannot be applied after the query: a vendor row is a percentile
 * over that vendor's attempts, and percentiles do not average. Blending
 * google-chirp's p95 with gemini's — by sample count or any other weight — would
 * print a number no call ever took. The `else` arm keeps a provider this app has
 * not learned yet as its own row rather than dropping it.
 *
 * Written once, into a subquery column, and grouped on by name — never repeated
 * inline in the GROUP BY. A grouping element has to be the same expression as
 * the one it groups, and drizzle parameterises each catalog id, so a second
 * textual copy of this CASE is a different set of parameter placeholders and
 * Postgres rejects the query outright.
 */
const VENDOR_EXPR = sql.join(
  [
    sql`case`,
    ...STT_CATALOG.map(
      (entry) =>
        sql`when ${sttLatencySamples.provider} = ${entry.sttProvider} then ${entry.vendor}`,
    ),
    sql`else ${sttLatencySamples.provider} end`,
  ],
  sql` `,
);

/** The shape a bucket with no rows has. Also what a build-time failure renders. */
function emptyMatrix(): LatencyMatrixData {
  return { vendors: [], regions: [], totalSamples: 0, windowDays: WINDOW_DAYS };
}

/**
 * Aggregates the trailing window for one clip-length bucket, at both levels the
 * table shows: one set of cells per vendor, and one per (provider, model) inside
 * it. Postgres computes the percentiles — pulling raw rows into the page and
 * sorting them in JavaScript would not survive real volume. The
 * (duration_bucket, created_at, provider, fly_region) index matches this shape.
 *
 * Both levels come from ONE pass over the window, via GROUP BY GROUPING SETS.
 * Two queries would scan the same 90 days twice for numbers that must agree with
 * each other, and the vendor level cannot be derived from the model level anyway
 * (see VENDOR_EXPR). The two kinds of row are told apart by `provider`, which is
 * NOT NULL in the table and so is null only on the vendor rollup — no
 * `grouping()` bitmask to decode. `model` is genuinely nullable, which is why it
 * cannot play that part.
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
 *    with it, for a page whose entire content is a 90-day average. A deploy must
 *    not depend on Postgres being reachable from the builder, so at build time
 *    the failure is logged and the page ships in its empty state; the first
 *    revalidation an hour later fills it in.
 *  - At runtime the error propagates. Next treats a returned value — empty data
 *    included — as a successful render, so swallowing it during a background
 *    revalidation would REPLACE a good page with "0 provider attempts over the
 *    last 90 days" and cache that for an hour. Throwing leaves the last good
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

type AggregateRow = {
  vendor: string;
  /** Null on a vendor rollup row; the backend provider id on a model row. */
  provider: string | null;
  model: string | null;
  region: string;
  samples: string;
  p50: string;
  p95: string;
  p99: string;
  errorRate: string;
};

function toCell(row: AggregateRow): LatencyCell {
  return {
    region: row.region,
    samples: toNumber(row.samples),
    p50: Math.round(toNumber(row.p50)),
    p95: Math.round(toNumber(row.p95)),
    p99: Math.round(toNumber(row.p99)),
    errorRate: toNumber(row.errorRate),
  };
}

async function queryLatencyMatrix(bucket: DurationBucket): Promise<LatencyMatrixData> {
  const since = new Date(Date.now() - WINDOW_DAYS * 24 * 60 * 60 * 1000);

  // The window's attempts with their vendor named. Filtering here keeps the
  // whole scan on the (duration_bucket, created_at, …) index, and naming the
  // vendor once is what lets both grouping sets below refer to it by column.
  const attempt = db
    .select({
      vendor: VENDOR_EXPR.as("vendor"),
      provider: sttLatencySamples.provider,
      model: sttLatencySamples.model,
      region: sttLatencySamples.flyRegion,
      latencyMs: sttLatencySamples.latencyMs,
      ok: sttLatencySamples.ok,
    })
    .from(sttLatencySamples)
    .where(
      and(
        gte(sttLatencySamples.createdAt, since),
        eq(sttLatencySamples.durationBucket, bucket),
        // Dropped at the scan, not after the aggregate. `google-chirp` falls
        // through VENDOR_EXPR's else arm to a vendor row of its own, so today
        // dropping it later would work — but a provider retired while still
        // mapped to a live vendor would already have moved that vendor's
        // percentile, and percentiles do not come apart again. Filtering at the
        // source is correct for both, and keeps the scan on the same index.
        // `notInArray` is guarded because drizzle renders an empty one as a
        // contradiction, which would blank the whole page.
        ...(RETIRED_PROVIDERS.length > 0
          ? [notInArray(sttLatencySamples.provider, [...RETIRED_PROVIDERS])]
          : []),
      ),
    )
    .as("attempt");

  const rows = await db
    .select({
      vendor: attempt.vendor,
      // Null on the vendor rollup rows — see the grouping sets below.
      provider: sql<string | null>`${attempt.provider}`,
      model: attempt.model,
      region: attempt.region,
      samples: sql<string>`count(*)`,
      p50: sql<string>`percentile_cont(0.5) within group (order by ${attempt.latencyMs})`,
      p95: sql<string>`percentile_cont(0.95) within group (order by ${attempt.latencyMs})`,
      p99: sql<string>`percentile_cont(0.99) within group (order by ${attempt.latencyMs})`,
      errorRate: sql<string>`avg(case when ${attempt.ok} then 0 else 1 end)`,
    })
    .from(attempt)
    .groupBy(
      sql`grouping sets (
        (${attempt.vendor}, ${attempt.region}),
        (${attempt.vendor}, ${attempt.provider}, ${attempt.model}, ${attempt.region})
      )`,
    );

  return buildMatrix(rows as AggregateRow[]);
}

/**
 * Turns the flat aggregate into the nested rows the table draws.
 *
 * Axes and rows come from what the data holds. There is no declared region list
 * in this repo, and a hardcoded one would rot the first time a region is added
 * or retired. A vendor with no rows in this bucket is likewise absent rather
 * than present and empty.
 */
function buildMatrix(rows: AggregateRow[]): LatencyMatrixData {
  // Arrays hold the result and Maps only index into them: this tsconfig targets
  // es5, where iterating a Map is a type error (TS2802) rather than a runtime
  // one. `get` and `set` are fine.
  const vendors: LatencyVendorRow[] = [];
  const vendorIndex = new Map<string, LatencyVendorRow>();
  const modelIndex = new Map<string, LatencyModelRow>();
  const regions: string[] = [];
  const seenRegions = new Map<string, true>();
  let totalSamples = 0;

  const vendorRowFor = (key: string): LatencyVendorRow => {
    const existing = vendorIndex.get(key);
    if (existing) return existing;
    const created: LatencyVendorRow = {
      vendor: key,
      label: vendorDisplayName(key),
      cells: [],
      models: [],
    };
    vendorIndex.set(key, created);
    vendors.push(created);
    return created;
  };

  for (const row of rows) {
    if (!seenRegions.has(row.region)) {
      seenRegions.set(row.region, true);
      regions.push(row.region);
    }

    const vendor = vendorRowFor(row.vendor);

    if (row.provider === null) {
      // The vendor rollup. Only these are totalled: the model rows partition the
      // very same attempts, so counting both would report the window twice.
      const cell = toCell(row);
      vendor.cells.push(cell);
      totalSamples += cell.samples;
      continue;
    }

    const key = `${row.vendor}|${row.provider}|${row.model ?? ""}`;
    let model = modelIndex.get(key);
    if (!model) {
      model = {
        provider: row.provider,
        model: row.model,
        label: modelDisplayName(row.provider, row.model),
        isDefault: isDefaultModel(row.provider, row.model),
        cells: [],
      };
      modelIndex.set(key, model);
      vendor.models.push(model);
    }
    model.cells.push(toCell(row));
  }

  for (const vendor of vendors) {
    // Catalog order, so the rows read like the app's Model dropdown rather than
    // like whatever order Postgres happened to return.
    vendor.models.sort(
      (a, b) => modelSortIndex(a.provider, a.model) - modelSortIndex(b.provider, b.model),
    );
  }

  return {
    // Alphabetical by name is only the starting order — the client sorts by the
    // metric on screen — but it keeps the server's output stable between builds.
    vendors: vendors.sort((a, b) => a.label.localeCompare(b.label)),
    regions: regions.sort(),
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
