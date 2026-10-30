"use client";

import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
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
import {
  prefersReducedMotion,
  slideRows,
  snapshotRowTops,
} from "@/lib/flip-rows";
import { regionCity } from "@/lib/latency/fly-regions";
import {
  GLASS,
  GLASS_ON,
  LABEL,
  SEG,
  SEG_ITEM,
  SEG_OFF,
  SEG_ON,
} from "@/components/landing/dusk-pages";
import { isRecord } from "@/src/lib/type-guards";

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
/**
 * The sticky provider column sits over the heat cells when the table scrolls
 * sideways, but `border-spacing-1.5` leaves 6px transparent gaps around each
 * cell that the cells show through. A 6px night spread shadow (the same width
 * as the spacing) fills them. The cell itself stays square so the shadow has
 * no rounded corners to leak at; a pinned row's rounded highlight is a
 * pseudo element behind the content instead.
 */
const STICKY_COVER = "shadow-[0_0_0_6px_#0b0c17]";

/** The metric and bucket toggles, and the "Sorted by … — reset" chip. */
const PILL =
  "flex items-center gap-2 rounded-full border px-4 py-[9px] text-sm transition-colors motion-reduce:transition-none focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)]";

/**
 * A podium bar's width, against the podium's own best score (lower is
 * better): #1 is always the full bar, and the others shrink in proportion, so
 * a bar is half as long when its number is twice as high. Never under 10%, so
 * a bar always shows; an all-zero error rate is a full bar for everyone.
 */
export function podiumBarWidth(score: number, best: number): number {
  if (score <= best) return 100;

  return Math.max(10, Math.round((best / score) * 100));
}

export default function LatencyMatrix({ matrices, defaultBucket }: Props) {
  const [bucket, setBucket] = useState<DurationBucket>(defaultBucket);
  const [metric, setMetric] = useState<MetricKey>("p50");
  // Set by the first click that changes the Metric or Clip length (#813).
  // Until then the cell numbers do not fade, so the table does not fade in on
  // first paint.
  const [swapped, setSwapped] = useState(false);
  const [byModel, setByModel] = useState(false);
  const [sortRegion, setSortRegion] = useState<string | null>(null);
  const [pinnedVendor, setPinnedVendor] = useState<string | null>(null);
  const [hover, setHover] = useState<{ vendor: string; region: string } | null>(
    null,
  );
  const [homeRegion, setHomeRegion] = useState<string | null>(null);
  const [homeCity, setHomeCity] = useState<string | null>(null);
  const [pickingRegion, setPickingRegion] = useState(false);
  // Once the visitor has chosen a region by hand, geolocation stops having an
  // opinion — switching clip-length bucket must not quietly move them back.
  const regionPickedByUser = useRef(false);
  // The matrix region and its table (#702). "Break down by model" adds a dozen
  // rows at once, and a table row cannot tween, so the region tweens to the
  // table's measured height instead (.t-resize) and the page under it glides
  // rather than jumping 500 px in one frame.
  const matrixRegion = useRef<HTMLDivElement>(null);
  const matrixTable = useRef<HTMLTableElement>(null);
  // The re-sort FLIP (#812): where each row stood just before a re-order, and
  // the slides in flight.
  const tbodyRef = useRef<HTMLTableSectionElement>(null);
  const rowRefs = useRef(new Map<string, HTMLTableRowElement>());
  const pendingRowTops = useRef<Map<string, number> | null>(null);
  const rowSlides = useRef(new Map<string, Animation>());

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

    async function detectHomeRegion() {
      try {
        const response = await fetch(
          `/api/geo/nearest-region?regions=${encodeURIComponent(regionsKey)}`,
          { signal: controller.signal },
        );
        const result: unknown = response.ok ? await response.json() : null;

        // The answer can land after a hand-pick; the visitor still wins.
        if (
          regionPickedByUser.current ||
          !isRecord(result) ||
          typeof result.region !== "string"
        )
          return;
        setHomeRegion(result.region);
        setHomeCity(
          typeof result.city === "string"
            ? result.city
            : regionCity(result.region),
        );
      } catch {
        // A missing highlight is not worth an error message.
      }
    }

    void detectHomeRegion();

    return () => controller.abort();
  }, [regionsKey]);

  // ResizeObserver reports after layout and before paint, so the region still
  // holds its old height in the frame the rows change, and the transition
  // starts from there. The first report only pins the height it already has.
  // The region is observed too: a horizontal scrollbar that comes or goes
  // changes its chrome (border + scrollbar) without changing the table.
  useEffect(() => {
    const region = matrixRegion.current;
    const table = matrixTable.current;

    if (!region || !table) return;
    const observer = new ResizeObserver(() => {
      const chrome = region.offsetHeight - region.clientHeight;

      region.style.height = `${table.offsetHeight + chrome}px`;
    });

    observer.observe(table);
    observer.observe(region);

    return () => observer.disconnect();
  }, [hasData]);

  // The region to actually highlight. A pick is remembered across bucket
  // switches — geolocation must not quietly undo it — but every bucket derives
  // its own region axis from the rows it has, so the remembered region may not
  // exist in the bucket on screen. Everything visual keys off this rather than
  // off `homeRegion` being truthy: otherwise a sparser bucket leaves the header
  // announcing "Showing Frankfurt" while no column, cell or picker entry
  // matches, and nothing can ever reconcile the two. `homeRegion` itself is
  // kept, so switching back to a bucket that has it restores the highlight.
  const activeHomeRegion =
    homeRegion && regions.includes(homeRegion) ? homeRegion : null;

  // Exactly the same rule for the row-order pick, and for exactly the same
  // reason: a sort region that this bucket's axis does not have scores every
  // vendor null, the comparator falls through to localeCompare, and the table
  // silently re-orders by name while the Row-order button still claims to be
  // sorted by a city. Both the comparator and the label read this, never
  // `sortRegion` directly; `sortRegion` is still remembered so returning to a
  // bucket that has it restores the order.
  const activeSortRegion =
    sortRegion && regions.includes(sortRegion) ? sortRegion : null;

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
      if (!activeSortRegion) {
        return { vendor, score: globalValue(vendor.cells) };
      }
      const cell = vendor.cells.get(activeSortRegion);

      return { vendor, score: cell?.enough ? cell.value : null };
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
  }, [vendorRows, regions, activeSortRegion, byModel]);

  // Slide the re-sorted rows to their new places (#812) instead of painting
  // the new order in one frame, so the reader who clicked a column can follow
  // the provider they were looking at. Only a re-order snapshots the rows: the
  // "Break down by model" toggle changes the row SET, and #702 glides that.
  useLayoutEffect(() => {
    const before = pendingRowTops.current;

    pendingRowTops.current = null;
    const tbody = tbodyRef.current;

    if (before === null || tbody === null || prefersReducedMotion()) return;
    slideRows(tbody, rowRefs.current, before, rowSlides.current);
  }, [rows]);

  /** Call before any state change that re-orders the rows. */
  function snapshotForSort() {
    pendingRowTops.current = null;
    const tbody = tbodyRef.current;

    if (tbody === null || prefersReducedMotion()) return;
    pendingRowTops.current = snapshotRowTops(tbody, rowRefs.current);
  }

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
   * Lavender, strongest where the number is best (fastest / most reliable) and
   * fading as it gets worse. Colour only ever repeats what the printed number
   * already says. The alpha tops out at .55 so white text keeps its contrast.
   */
  /** 0 for the worst number on screen, 1 for the best (lower is better). */
  const goodness = (value: number) => {
    const span = scale.max - scale.min || 1;

    return 1 - Math.min(1, Math.max(0, (value - scale.min) / span));
  };

  const cellStyle = (value: number) => {
    const ratio = 1 - goodness(value);

    return {
      backgroundColor: `rgba(196, 181, 253, ${(0.55 - ratio * 0.45).toFixed(2)})`,
      color: "#ffffff",
    };
  };

  /**
   * The podium: the three providers with the best number for the metric on
   * screen, in the region the table is sorted by or the visitor's own region,
   * else by their global median. The same cells and the same "enough attempts"
   * bar the table uses, cut to three.
   */
  const podiumRegion = activeSortRegion ?? activeHomeRegion;
  const podium = useMemo(() => {
    const scored: { vendor: (typeof vendorRows)[number]; score: number }[] = [];

    for (const vendor of vendorRows) {
      const cell = podiumRegion ? vendor.cells.get(podiumRegion) : null;
      const score = podiumRegion
        ? cell?.enough
          ? cell.value
          : null
        : globalValue(vendor.cells);

      if (score !== null) scored.push({ vendor, score });
    }
    scored.sort(
      (a, b) =>
        a.score - b.score || a.vendor.label.localeCompare(b.vendor.label),
    );

    return scored.slice(0, 3);
  }, [vendorRows, regions, podiumRegion]);

  const format = (value: number) =>
    metric === "errorRate" ? `${value.toFixed(1)}%` : `${Math.round(value)}`;

  const metricUnit =
    METRICS.find((entry) => entry.key === metric)?.unit ?? "ms";
  // One name per region for the "Showing …" chip and the podium, so they
  // never disagree: the visitor's own region reads as the city the lookup
  // returned, any other region as its Fly city.
  const placeName = (region: string) =>
    region === activeHomeRegion && homeCity ? homeCity : regionCity(region);
  const podiumWhere = podiumRegion
    ? `in ${placeName(podiumRegion)}`
    : "overall";

  return (
    <div>
      {/* Controls */}
      <div className="flex flex-wrap items-end gap-x-3 gap-y-4">
        <div className="flex flex-col gap-2">
          <span className={LABEL} id="latency-metric-label">
            Metric
          </span>
          <div
            aria-labelledby="latency-metric-label"
            className={SEG}
            role="group"
          >
            {METRICS.map((entry) => (
              <button
                key={entry.key}
                aria-pressed={metric === entry.key}
                className={`${SEG_ITEM} ${metric === entry.key ? SEG_ON : SEG_OFF}`}
                type="button"
                onClick={() => {
                  if (entry.key === metric) return;
                  setSwapped(true);
                  snapshotForSort();
                  setMetric(entry.key);
                }}
              >
                {entry.label}
              </button>
            ))}
          </div>
        </div>

        <div className="flex flex-col gap-2">
          <span className={LABEL} id="latency-bucket-label">
            Clip length
          </span>
          <div
            aria-labelledby="latency-bucket-label"
            className={SEG}
            role="group"
          >
            {DURATION_BUCKETS.map((entry) => (
              <button
                key={entry}
                aria-pressed={bucket === entry}
                className={`${SEG_ITEM} ${bucket === entry ? SEG_ON : SEG_OFF}`}
                type="button"
                onClick={() => {
                  if (entry === bucket) return;
                  setSwapped(true);
                  snapshotForSort();
                  setBucket(entry);
                }}
              >
                {BUCKET_LABELS[entry]}
              </button>
            ))}
          </div>
        </div>

        {hasData ? (
          <>
            <div className="flex flex-col gap-2">
              <span className={LABEL}>Detail</span>
              <button
                aria-pressed={byModel}
                className={`${PILL} ${
                  byModel
                    ? "border-[rgba(196,181,253,.5)] bg-[rgba(196,181,253,.1)] text-white"
                    : "border-[var(--dusk-line)] bg-white/[.05] text-[var(--dusk-mute)] hover:text-white"
                }`}
                type="button"
                onClick={() => setByModel((open) => !open)}
              >
                {byModel ? (
                  <ChevronDown className="h-3.5 w-3.5" strokeWidth={1.5} />
                ) : (
                  <ChevronRight
                    className="h-3.5 w-3.5 rtl:-scale-x-100"
                    strokeWidth={1.5}
                  />
                )}
                Break down by model
              </button>
            </div>

            <div className="flex flex-col gap-2">
              <span className={LABEL}>Row order</span>
              <button
                className={`${PILL} border-[var(--dusk-line)] bg-white/[.05] text-[var(--dusk-mute)] hover:text-white`}
                type="button"
                onClick={() => {
                  // Only a re-order may leave a snapshot behind: a stale one
                  // would slide rows on the next, unrelated change.
                  if (activeSortRegion !== null) snapshotForSort();
                  setSortRegion(null);
                }}
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
        <div className={`mt-[22px] ${GLASS} p-10 text-center`}>
          <p className="text-lg text-white">
            No measurements for this clip length yet.
          </p>
          <p className="mt-2 text-sm text-[var(--dusk-mute)]">
            Nothing has been recorded for &ldquo;{BUCKET_LABELS[bucket]}&rdquo;
            in the last {data.windowDays} days. Pick another clip length above —
            this page fills in as transcriptions run.
          </p>
        </div>
      ) : (
        <>
          {/* Region highlight */}
          <div className="mt-[22px] text-sm">
            <div className="inline-flex flex-wrap items-center gap-x-2.5 gap-y-1 rounded-full border border-[var(--dusk-line)] bg-[var(--dusk-glass)] px-4 py-2.5">
              <MapPin
                aria-hidden="true"
                className="h-4 w-4 text-[var(--dusk-lav)]"
                strokeWidth={1.5}
              />
              {activeHomeRegion ? (
                <span className="text-[var(--dusk-mute)]">
                  Showing{" "}
                  <span className="font-semibold text-white">
                    {placeName(activeHomeRegion)}
                  </span>
                </span>
              ) : (
                <span className="text-[var(--dusk-mute)]">
                  Pick the region closest to you
                </span>
              )}
              <span aria-hidden="true" className="text-[var(--dusk-dim)]">
                ·
              </span>
              <button
                className="rounded text-[var(--dusk-lav)] underline-offset-4 transition-colors hover:text-white hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)]"
                type="button"
                onClick={() => setPickingRegion((open) => !open)}
              >
                {pickingRegion ? "close" : "change"}
              </button>
            </div>
            {/* The chip row grows open from 0fr (#702) instead of arriving at
                full height in one frame. It sits under the flex row rather than
                in it, so no row gap lands before the reveal; pt-4 is the gap. */}
            {pickingRegion ? (
              <div className="t-cell-reveal">
                <div>
                  <div className="flex flex-wrap gap-2 pt-4">
                    {regions.map((region) => (
                      <button
                        key={region}
                        aria-pressed={activeHomeRegion === region}
                        className={`rounded-full border px-3 py-[7px] text-[13px] transition-colors motion-reduce:transition-none focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)] ${
                          activeHomeRegion === region
                            ? "border-white bg-white text-[#0b0b14]"
                            : "border-[var(--dusk-line)] text-[var(--dusk-mute)] hover:text-white"
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
                </div>
              </div>
            ) : null}
          </div>

          {/* Podium: the table's own best three for this metric, never more. */}
          {podium.length > 0 ? (
            <ol
              aria-label={`Best three providers ${podiumWhere}`}
              className="mt-[22px] grid grid-cols-1 gap-4 sm:grid-cols-3"
            >
              {podium.map((entry, index) => (
                <li
                  key={entry.vendor.vendor}
                  className={`p-6 ${index === 0 ? GLASS_ON : GLASS}`}
                >
                  <span className={LABEL}>
                    #{index + 1} {podiumWhere}
                  </span>
                  <p className="mt-2 text-xl font-semibold text-white">
                    {entry.vendor.label}
                  </p>
                  <p className="mt-1.5 text-[34px] font-semibold leading-none tracking-[-0.035em] tabular-nums text-white md:text-[44px]">
                    {metric === "errorRate"
                      ? entry.score.toFixed(1)
                      : Math.round(entry.score)}
                    <small className="ms-1 text-base font-normal tracking-normal text-[var(--dusk-mute)]">
                      {metricUnit}
                    </small>
                  </p>
                  <div
                    aria-hidden="true"
                    className="mt-3.5 h-1.5 rounded-full bg-white/[.07]"
                  >
                    <span
                      className="block h-full rounded-full bg-[image:var(--dusk-accent)]"
                      style={{
                        width: `${podiumBarWidth(entry.score, podium[0].score)}%`,
                      }}
                    />
                  </div>
                </li>
              ))}
            </ol>
          ) : null}

          {/* Matrix */}
          <div
            ref={matrixRegion}
            aria-label="Latency matrix"
            className={`t-resize mt-[18px] overflow-x-auto overflow-y-hidden ${GLASS} focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)]`}
            role="region"
            // eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex -- scrollable region needs a tab stop (WCAG 2.1.1, axe scrollable-region-focusable)
            tabIndex={0}
          >
            <table
              ref={matrixTable}
              className="w-full border-separate border-spacing-1.5 p-1 text-sm"
            >
              <thead>
                <tr>
                  <th
                    className={`sticky start-0 z-20 bg-[#0b0c17] ${STICKY_COVER} px-3 py-3 text-start text-[11.5px] font-normal uppercase tracking-[1.3px] text-[var(--dusk-dim)]`}
                  >
                    {byModel ? "Provider / model" : "Provider"}
                  </th>
                  {regions.map((region) => (
                    <th
                      key={region}
                      className={`rounded-[9px] px-2 py-3 text-center text-[11.5px] font-normal uppercase tracking-[1.3px] transition-colors motion-reduce:transition-none ${
                        activeHomeRegion === region
                          ? "bg-[rgba(196,181,253,.1)] text-white"
                          : "text-[var(--dusk-dim)]"
                      } ${hover?.region === region ? "bg-white/[.05]" : ""}`}
                      scope="col"
                    >
                      <button
                        className="whitespace-nowrap rounded transition-colors hover:text-white focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)]"
                        title={`Sort providers by ${regionCity(region)}`}
                        type="button"
                        onClick={() => {
                          snapshotForSort();
                          setSortRegion((current) =>
                            current === region ? null : region,
                          );
                        }}
                      >
                        {regionCity(region)}
                        <span className="block text-[11px] tracking-wider text-[var(--dusk-dim)]">
                          {region}
                        </span>
                      </button>
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody ref={tbodyRef}>
                {rows.map((row) => {
                  const pinned = pinnedVendor === row.vendor;

                  return (
                    <tr
                      key={row.key}
                      ref={(tr) => {
                        if (tr === null) rowRefs.current.delete(row.key);
                        else rowRefs.current.set(row.key, tr);
                      }}
                      className={
                        pinned
                          ? "bg-[rgba(196,181,253,.08)]"
                          : hover?.vendor === row.vendor
                            ? "bg-white/[.03]"
                            : ""
                      }
                    >
                      <th
                        className={`sticky start-0 z-10 whitespace-nowrap bg-[#0b0c17] ${STICKY_COVER} px-3 py-2 text-start ${
                          row.isModel ? "font-normal" : "font-medium"
                        } ${
                          pinned
                            ? "text-white before:absolute before:inset-0 before:-z-10 before:rounded-[9px] before:bg-[#1a1830]"
                            : row.isModel
                              ? "text-[var(--dusk-mute)]"
                              : "text-white"
                        }`}
                        scope="row"
                      >
                        {row.isModel ? (
                          <span className="flex items-center gap-2 ps-6">
                            <span
                              aria-hidden="true"
                              className="h-4 w-px shrink-0 bg-[var(--dusk-line)]"
                            />
                            {row.label}
                            {row.isDefaultModel ? (
                              <span className="rounded-full border border-[rgba(196,181,253,.4)] px-1.5 text-[11px] uppercase tracking-wider text-[var(--dusk-lav)]">
                                default
                              </span>
                            ) : null}
                          </span>
                        ) : (
                          <button
                            className="flex items-center gap-2 rounded transition-colors hover:text-[var(--dusk-lav)] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)]"
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
                              className={`t-heat-cell rounded-[9px] px-2 py-[11px] text-center text-[var(--dusk-dim)] ${
                                isHome ? "bg-[rgba(196,181,253,.06)]" : ""
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
                            className={`t-heat-cell rounded-[9px] px-2 py-[11px] text-center tabular-nums ${
                              isHome ? "ring-1 ring-inset ring-white/50" : ""
                            }`}
                            style={cellStyle(cell.value)}
                            title={`${row.label} in ${regionCity(region)} — ${cell.samples.toLocaleString()} attempts`}
                            onMouseEnter={() =>
                              setHover({ vendor: row.vendor, region })
                            }
                            onMouseLeave={() => setHover(null)}
                          >
                            {/* A new key per metric and clip length remounts the
                                number, so it fades in under the colour (#813). */}
                            <span
                              key={`${metric}:${bucket}`}
                              className={swapped ? "t-heat-swap" : undefined}
                            >
                              {format(cell.value)}
                            </span>
                          </td>
                        );
                      })}
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>

          <p className="mt-3.5 text-center text-[13.5px] leading-[1.6] text-[var(--dusk-mute)]">
            {metric === "errorRate"
              ? "Share of attempts that failed, including ones a fallback provider rescued."
              : "Milliseconds, provider call only."}{" "}
            A cell needs at least {minSamples.toLocaleString()} attempts to show
            a number
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
