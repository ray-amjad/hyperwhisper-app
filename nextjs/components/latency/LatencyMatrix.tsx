"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { ChevronDown, ChevronRight, MapPin, Pin } from "lucide-react";

import {
  BUCKET_LABELS,
  DURATION_BUCKETS,
  minSamplesForMetric,
  type DurationBucket,
  type LatencyCell,
  type LatencyMatrixData,
  type LatencyModelRow,
  type LatencyVendorRow,
} from "@/lib/latency/types";
import { regionCity } from "@/lib/latency/fly-regions";

const METRICS = [
  { key: "p50", label: "Median", unit: "ms" },
  { key: "p95", label: "p95", unit: "ms" },
  { key: "p99", label: "p99", unit: "ms" },
  { key: "errorRate", label: "Error rate", unit: "%" },
] as const;

type MetricKey = (typeof METRICS)[number]["key"];

type Props = {
  matrices: Record<DurationBucket, LatencyMatrixData>;
  defaultBucket: DurationBucket;
};

type CellValue = {
  value: number;
  samples: number;
  enough: boolean;
} | null;

/** A drawn row: a vendor, or one of its models while the breakdown is on. */
type DisplayRow = {
  key: string;
  label: string;
  isModel: boolean;
  isDefaultModel: boolean;
  /** The vendor row this belongs to — a model row pins and hovers with its vendor. */
  vendor: string;
  cells: Map<string, CellValue>;
};

function cellMap(cells: LatencyCell[], metric: MetricKey, minSamples: number) {
  const map = new Map<string, CellValue>();
  for (const cell of cells) {
    const value = metric === "errorRate" ? cell.errorRate * 100 : cell[metric];
    map.set(cell.region, {
      value,
      samples: cell.samples,
      enough: cell.samples >= minSamples,
    });
  }
  return map;
}

/**
 * The provider × region heatmap.
 *
 * Every cell prints its number, so colour is never the only channel carrying
 * meaning. A cell backed by too few attempts shows a dash and its sample count
 * on hover rather than a number nobody should trust.
 *
 * A row is a vendor, named the way the app's Provider dropdown names it. "Break
 * down by model" opens each vendor into the models underneath it, named the way
 * the app's Model dropdown names them. It is off by default: a model row counts
 * only the attempts that ran on that model, so the breakdown is a thinner,
 * noisier view of the same window, and the question most visitors arrive with is
 * about providers.
 */
export default function LatencyMatrix({ matrices, defaultBucket }: Props) {
  const [bucket, setBucket] = useState<DurationBucket>(defaultBucket);
  const [metric, setMetric] = useState<MetricKey>("p50");
  const [byModel, setByModel] = useState(false);
  const [sortRegion, setSortRegion] = useState<string | null>(null);
  const [pinnedVendor, setPinnedVendor] = useState<string | null>(null);
  const [hover, setHover] = useState<{ vendor: string; region: string } | null>(null);
  const [homeRegion, setHomeRegion] = useState<string | null>(null);
  const [homeCity, setHomeCity] = useState<string | null>(null);
  const [pickingRegion, setPickingRegion] = useState(false);
  // Once the visitor has chosen a region by hand, geolocation stops having an
  // opinion — switching clip-length bucket must not quietly move them back.
  const regionPickedByUser = useRef(false);

  const data = matrices[bucket];
  const { regions } = data;
  const hasData = regions.length > 0;
  // p99 asks much more of a cell than p50 or p95 do, so the bar moves with the
  // metric on screen rather than being one number for the whole page.
  const minSamples = minSamplesForMetric(metric);
  // The region list as a value, not an identity: each bucket's matrix builds its
  // own array, and the props re-cross the RSC boundary, so keying the effect
  // below on `regions` itself would re-run it on every bucket toggle.
  const regionsKey = regions.join(",");

  // Ask which region is nearest this visitor, once the page has painted. The
  // page itself is static, so it cannot know. Only regions that actually have
  // data are offered as candidates, so the highlight can never land on an empty
  // column.
  useEffect(() => {
    if (regionsKey === "") return;
    if (regionPickedByUser.current) return;
    const controller = new AbortController();

    fetch(`/api/geo/nearest-region?regions=${encodeURIComponent(regionsKey)}`, {
      signal: controller.signal,
    })
      .then((response) => (response.ok ? response.json() : null))
      .then((result) => {
        // The answer can land after a hand-pick; the visitor still wins.
        if (regionPickedByUser.current || !result?.region) return;
        setHomeRegion(result.region);
        setHomeCity(result.city ?? regionCity(result.region));
      })
      .catch(() => {
        // A missing highlight is not worth an error message.
      });

    return () => controller.abort();
  }, [regionsKey]);

  // The region to actually highlight. A pick is remembered across bucket
  // switches — geolocation must not quietly undo it — but every bucket derives
  // its own region axis from the rows it has, so the remembered region may not
  // exist in the bucket on screen. Everything visual keys off this rather than
  // off `homeRegion` being truthy: otherwise a sparser bucket leaves the header
  // announcing "Showing Frankfurt" while no column, cell or picker entry
  // matches, and nothing can ever reconcile the two. `homeRegion` itself is
  // kept, so switching back to a bucket that has it restores the highlight.
  const activeHomeRegion = homeRegion && regions.includes(homeRegion) ? homeRegion : null;

  // Exactly the same rule for the row-order pick, and for exactly the same
  // reason: a sort region that this bucket's axis does not have scores every
  // vendor null, the comparator falls through to localeCompare, and the table
  // silently re-orders by name while the Row-order button still claims to be
  // sorted by a city. Both the comparator and the label read this, never
  // `sortRegion` directly; `sortRegion` is still remembered so returning to a
  // bucket that has it restores the order.
  const activeSortRegion = sortRegion && regions.includes(sortRegion) ? sortRegion : null;

  /**
   * Every vendor's cells for the metric on screen, plus its models' cells. Built
   * once per (bucket, metric) rather than per lookup, and always for both levels
   * — the breakdown toggle changes which rows are drawn, not what is measured,
   * so flipping it does not re-aggregate anything.
   */
  const vendorRows = useMemo(
    () =>
      data.vendors.map((vendor: LatencyVendorRow) => ({
        vendor: vendor.vendor,
        label: vendor.label,
        cells: cellMap(vendor.cells, metric, minSamples),
        models: vendor.models.map((model: LatencyModelRow) => ({
          key: `${vendor.vendor}|${model.provider}|${model.model ?? ""}`,
          label: model.label,
          isDefault: model.isDefault,
          cells: cellMap(model.cells, metric, minSamples),
        })),
      })),
    [data, metric, minSamples],
  );

  /** Median of a row's usable cells, used for the default row order. */
  const globalValue = (cells: Map<string, CellValue>): number | null => {
    const values = regions
      .map((region) => cells.get(region))
      .filter((cell): cell is NonNullable<CellValue> => Boolean(cell?.enough))
      .map((cell) => cell.value)
      .sort((a, b) => a - b);
    if (values.length === 0) return null;
    return values[Math.floor(values.length / 2)];
  };

  /**
   * The rows to draw, in order. Vendors sort by the metric on screen; a vendor's
   * models keep the catalog order the server sent them in — the order the app's
   * Model dropdown uses — so the breakdown reads as a list of that vendor's
   * models rather than as a second, competing ranking.
   */
  const rows = useMemo(() => {
    const scored = vendorRows.map((vendor) => {
      const score = activeSortRegion
        ? (vendor.cells.get(activeSortRegion)?.enough
            ? vendor.cells.get(activeSortRegion)!.value
            : null)
        : globalValue(vendor.cells);
      return { vendor, score };
    });

    // Vendors with nothing to show sink to the bottom instead of sorting as 0.
    // The tie-break runs on the displayed name: on p99 no cell may clear the
    // 500-attempt bar, and that is the order the whole table gets.
    scored.sort((a, b) => {
      if (a.score === null && b.score === null) {
        return a.vendor.label.localeCompare(b.vendor.label);
      }
      if (a.score === null) return 1;
      if (b.score === null) return -1;
      return a.score - b.score;
    });

    const drawn: DisplayRow[] = [];
    for (const { vendor } of scored) {
      drawn.push({
        key: vendor.vendor,
        label: vendor.label,
        isModel: false,
        isDefaultModel: false,
        vendor: vendor.vendor,
        cells: vendor.cells,
      });
      if (!byModel) continue;
      for (const model of vendor.models) {
        drawn.push({
          key: model.key,
          label: model.label,
          isModel: true,
          isDefaultModel: model.isDefault,
          vendor: vendor.vendor,
          cells: model.cells,
        });
      }
    }
    return drawn;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [vendorRows, regions, activeSortRegion, byModel]);

  /**
   * Colour range over the rows actually on screen. Model rows are included only
   * while the breakdown is open, so opening it cannot leave the vendor rows
   * recoloured by numbers the visitor cannot see.
   */
  const scale = useMemo(() => {
    const values = rows
      .flatMap((row) => Array.from(row.cells.values()))
      .filter((cell): cell is NonNullable<CellValue> => Boolean(cell?.enough))
      .map((cell) => cell.value);
    if (values.length === 0) return { min: 0, max: 1 };
    return { min: Math.min(...values), max: Math.max(...values) };
  }, [rows]);

  /**
   * Green (fast / reliable) through amber to red (slow / failing). Colour only
   * ever repeats what the printed number already says.
   */
  const cellStyle = (value: number) => {
    const span = scale.max - scale.min || 1;
    const ratio = Math.min(1, Math.max(0, (value - scale.min) / span));
    const hue = 145 - ratio * 145;
    return {
      backgroundColor: `hsl(${hue} 70% 22% / ${0.35 + ratio * 0.45})`,
      color: "#f5f5f5",
    };
  };

  const format = (value: number) =>
    metric === "errorRate" ? `${value.toFixed(1)}%` : `${Math.round(value)}`;

  return (
    <div className="mt-10">
      {/* Controls */}
      <div className="flex flex-wrap items-center gap-6">
        <div className="flex flex-col gap-2">
          <span className="text-xs uppercase tracking-widest text-gray-500">Metric</span>
          <div className="flex flex-wrap gap-1 rounded-lg border border-gray-800 bg-gray-900/60 p-1">
            {METRICS.map((entry) => (
              <button
                key={entry.key}
                className={`rounded-md px-3 py-1.5 text-sm transition ${
                  metric === entry.key
                    ? "bg-purple-600 text-white"
                    : "text-gray-400 hover:text-white"
                }`}
                type="button"
                onClick={() => setMetric(entry.key)}
              >
                {entry.label}
              </button>
            ))}
          </div>
        </div>

        <div className="flex flex-col gap-2">
          <span className="text-xs uppercase tracking-widest text-gray-500">
            Clip length
          </span>
          <div className="flex flex-wrap gap-1 rounded-lg border border-gray-800 bg-gray-900/60 p-1">
            {DURATION_BUCKETS.map((entry) => (
              <button
                key={entry}
                className={`rounded-md px-3 py-1.5 text-sm transition ${
                  bucket === entry
                    ? "bg-purple-600 text-white"
                    : "text-gray-400 hover:text-white"
                }`}
                type="button"
                onClick={() => setBucket(entry)}
              >
                {BUCKET_LABELS[entry]}
              </button>
            ))}
          </div>
        </div>

        {hasData ? (
          <>
            <div className="flex flex-col gap-2">
              <span className="text-xs uppercase tracking-widest text-gray-500">
                Detail
              </span>
              <button
                aria-pressed={byModel}
                className={`flex items-center gap-2 rounded-lg border px-3 py-2 text-sm transition ${
                  byModel
                    ? "border-purple-600 bg-purple-950/40 text-white"
                    : "border-gray-800 bg-gray-900/60 text-gray-300 hover:text-white"
                }`}
                type="button"
                onClick={() => setByModel((open) => !open)}
              >
                {byModel ? (
                  <ChevronDown className="h-3.5 w-3.5" />
                ) : (
                  <ChevronRight className="h-3.5 w-3.5" />
                )}
                Break down by model
              </button>
            </div>

            <div className="flex flex-col gap-2">
              <span className="text-xs uppercase tracking-widest text-gray-500">
                Row order
              </span>
              <button
                className="rounded-lg border border-gray-800 bg-gray-900/60 px-3 py-2 text-sm text-gray-300 transition hover:text-white"
                type="button"
                onClick={() => setSortRegion(null)}
              >
                {activeSortRegion
                  ? `Sorted by ${regionCity(activeSortRegion)} — reset`
                  : "Sorted by global median"}
              </button>
            </div>
          </>
        ) : null}
      </div>

      {/* An empty bucket keeps every control above it: the clip-length selector
          is the only way back to a populated one, and the page header counts
          samples across all three buckets, so "no measurements yet" full stop
          would contradict it. */}
      {!hasData ? (
        <div className="mt-8 rounded-lg border border-gray-800 bg-gray-900/50 p-10 text-center">
          <p className="text-lg text-gray-300">
            No measurements for this clip length yet.
          </p>
          <p className="mt-2 text-sm text-gray-500">
            Nothing has been recorded for &ldquo;{BUCKET_LABELS[bucket]}&rdquo; in
            the last {data.windowDays} days. Pick another clip length above — this
            page fills in as transcriptions run.
          </p>
        </div>
      ) : (
        <>
          {/* Region highlight */}
          <div className="mt-6 flex flex-wrap items-center gap-3 text-sm">
            <MapPin className="h-4 w-4 text-purple-300" />
            {activeHomeRegion ? (
              <span className="text-gray-300">
                Showing{" "}
                <span className="font-semibold text-white">
                  {homeCity ?? regionCity(activeHomeRegion)}
                </span>
              </span>
            ) : (
              <span className="text-gray-500">Pick the region closest to you</span>
            )}
            <button
              className="text-purple-300 underline underline-offset-4 transition hover:text-purple-200"
              type="button"
              onClick={() => setPickingRegion((open) => !open)}
            >
              {pickingRegion ? "close" : "change"}
            </button>
            {pickingRegion ? (
              <div className="flex w-full flex-wrap gap-2 pt-2">
                {regions.map((region) => (
                  <button
                    key={region}
                    className={`rounded-full border px-3 py-1 text-xs transition ${
                      activeHomeRegion === region
                        ? "border-purple-500 bg-purple-600/30 text-white"
                        : "border-gray-700 text-gray-400 hover:text-white"
                    }`}
                    type="button"
                    onClick={() => {
                      regionPickedByUser.current = true;
                      setHomeRegion(region);
                      setHomeCity(regionCity(region));
                      setPickingRegion(false);
                    }}
                  >
                    {regionCity(region)}
                  </button>
                ))}
              </div>
            ) : null}
          </div>

          {/* Matrix */}
          <div className="mt-6 overflow-x-auto rounded-lg border border-gray-800 bg-gray-950/80">
            <table className="w-full border-collapse text-sm">
              <thead>
                <tr>
                  <th className="sticky left-0 z-20 bg-gray-950 px-4 py-3 text-left text-xs uppercase tracking-widest text-gray-500">
                    {byModel ? "Provider / model" : "Provider"}
                  </th>
                  {regions.map((region) => (
                    <th
                      key={region}
                      className={`px-3 py-3 text-center text-xs font-medium transition ${
                        activeHomeRegion === region
                          ? "bg-purple-950/40 text-purple-200"
                          : "text-gray-400"
                      } ${hover?.region === region ? "bg-gray-900" : ""}`}
                      scope="col"
                    >
                      <button
                        className="whitespace-nowrap transition hover:text-white"
                        title={`Sort providers by ${regionCity(region)}`}
                        type="button"
                        onClick={() =>
                          setSortRegion((current) => (current === region ? null : region))
                        }
                      >
                        {regionCity(region)}
                        <span className="block text-[10px] uppercase tracking-wider text-gray-600">
                          {region}
                        </span>
                      </button>
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {rows.map((row) => {
                  const pinned = pinnedVendor === row.vendor;
                  return (
                    <tr
                      key={row.key}
                      className={
                        pinned
                          ? "bg-purple-950/20"
                          : hover?.vendor === row.vendor
                            ? "bg-gray-900/60"
                            : ""
                      }
                    >
                      <th
                        className={`sticky left-0 z-10 whitespace-nowrap px-4 py-2 text-left ${
                          row.isModel ? "font-normal" : "font-medium"
                        } ${
                          pinned
                            ? "bg-purple-950/60 text-white"
                            : `bg-gray-950 ${row.isModel ? "text-gray-400" : "text-gray-200"}`
                        }`}
                        scope="row"
                      >
                        {row.isModel ? (
                          <span className="flex items-center gap-2 pl-6">
                            <span
                              aria-hidden="true"
                              className="h-4 w-px shrink-0 bg-gray-800"
                            />
                            {row.label}
                            {row.isDefaultModel ? (
                              <span className="rounded-full border border-purple-500/40 px-1.5 text-[10px] uppercase tracking-wider text-purple-300">
                                default
                              </span>
                            ) : null}
                          </span>
                        ) : (
                          <button
                            className="flex items-center gap-2 transition hover:text-purple-300"
                            title="Pin this provider"
                            type="button"
                            onClick={() =>
                              setPinnedVendor((current) =>
                                current === row.vendor ? null : row.vendor,
                              )
                            }
                          >
                            {pinned ? <Pin className="h-3 w-3" /> : null}
                            {row.label}
                          </button>
                        )}
                      </th>

                      {regions.map((region) => {
                        const cell = row.cells.get(region) ?? null;
                        const isHome = activeHomeRegion === region;

                        if (!cell || !cell.enough) {
                          return (
                            <td
                              key={region}
                              className={`px-3 py-2 text-center text-gray-700 ${
                                isHome ? "bg-purple-950/20" : ""
                              }`}
                              title={
                                cell
                                  ? `${cell.samples.toLocaleString()} attempts — fewer than the ${minSamples.toLocaleString()} this metric needs`
                                  : "No attempts recorded"
                              }
                              onMouseEnter={() =>
                                setHover({ vendor: row.vendor, region })
                              }
                              onMouseLeave={() => setHover(null)}
                            >
                              <span aria-label="not enough data">—</span>
                            </td>
                          );
                        }

                        return (
                          <td
                            key={region}
                            className={`px-3 py-2 text-center font-mono tabular-nums ${
                              isHome ? "ring-1 ring-inset ring-purple-500/40" : ""
                            }`}
                            style={cellStyle(cell.value)}
                            title={`${row.label} in ${regionCity(region)} — ${cell.samples.toLocaleString()} attempts`}
                            onMouseEnter={() => setHover({ vendor: row.vendor, region })}
                            onMouseLeave={() => setHover(null)}
                          >
                            {format(cell.value)}
                          </td>
                        );
                      })}
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>

          <p className="mt-4 text-sm text-gray-500">
            {metric === "errorRate"
              ? "Share of attempts that failed, including ones a fallback provider rescued."
              : "Milliseconds, provider call only."}{" "}
            A cell needs at least {minSamples.toLocaleString()} attempts to show a
            number
            {metric === "p99"
              ? " — p99 asks for more of them than the other metrics, because a 99th percentile drawn from a small sample is really just its slowest call"
              : ""}
            .{" "}
            {byModel
              ? "A model row counts only the attempts that ran on that model, so it falls under that bar long before its provider row does. "
              : ""}
            Click a region to sort by it, or a provider to pin its row.
          </p>
        </>
      )}
    </div>
  );
}
