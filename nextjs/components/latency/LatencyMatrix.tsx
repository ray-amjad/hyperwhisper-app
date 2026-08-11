"use client";

import { useEffect, useMemo, useState } from "react";
import { MapPin, Pin } from "lucide-react";

import {
  BUCKET_LABELS,
  DURATION_BUCKETS,
  type DurationBucket,
  type LatencyMatrixData,
} from "@/lib/latency/types";
import { providerDisplayName } from "@/lib/latency/providers";
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

/**
 * The provider × region heatmap.
 *
 * Every cell prints its number, so colour is never the only channel carrying
 * meaning. A cell backed by too few attempts shows a dash and its sample count
 * on hover rather than a number nobody should trust.
 */
export default function LatencyMatrix({ matrices, defaultBucket }: Props) {
  const [bucket, setBucket] = useState<DurationBucket>(defaultBucket);
  const [metric, setMetric] = useState<MetricKey>("p50");
  const [sortRegion, setSortRegion] = useState<string | null>(null);
  const [pinnedProvider, setPinnedProvider] = useState<string | null>(null);
  const [hover, setHover] = useState<{ provider: string; region: string } | null>(null);
  const [homeRegion, setHomeRegion] = useState<string | null>(null);
  const [homeCity, setHomeCity] = useState<string | null>(null);
  const [pickingRegion, setPickingRegion] = useState(false);

  const data = matrices[bucket];
  const { providers, regions, minSamplesPerCell } = data;

  const lookup = useMemo(() => {
    const map = new Map<string, CellValue>();
    for (const cell of data.cells) {
      const value =
        metric === "errorRate" ? cell.errorRate * 100 : cell[metric];
      map.set(`${cell.provider}|${cell.region}`, {
        value,
        samples: cell.samples,
        enough: cell.samples >= minSamplesPerCell,
      });
    }
    return map;
  }, [data, metric, minSamplesPerCell]);

  // Ask which region is nearest this visitor, once the page has painted. The
  // page itself is static, so it cannot know. Only regions that actually have
  // data are offered as candidates, so the highlight can never land on an empty
  // column.
  useEffect(() => {
    if (regions.length === 0) return;
    const controller = new AbortController();

    fetch(`/api/geo/nearest-region?regions=${encodeURIComponent(regions.join(","))}`, {
      signal: controller.signal,
    })
      .then((response) => (response.ok ? response.json() : null))
      .then((result) => {
        if (!result?.region) return;
        setHomeRegion(result.region);
        setHomeCity(result.city ?? regionCity(result.region));
      })
      .catch(() => {
        // A missing highlight is not worth an error message.
      });

    return () => controller.abort();
  }, [regions]);

  const cellFor = (provider: string, region: string): CellValue =>
    lookup.get(`${provider}|${region}`) ?? null;

  /** Median of a provider's usable cells, used for the default row order. */
  const globalValue = (provider: string): number | null => {
    const values = regions
      .map((region) => cellFor(provider, region))
      .filter((cell): cell is NonNullable<CellValue> => Boolean(cell?.enough))
      .map((cell) => cell.value)
      .sort((a, b) => a - b);
    if (values.length === 0) return null;
    return values[Math.floor(values.length / 2)];
  };

  const sortedProviders = useMemo(() => {
    const scored = providers.map((provider) => {
      const score = sortRegion
        ? (cellFor(provider, sortRegion)?.enough
            ? cellFor(provider, sortRegion)!.value
            : null)
        : globalValue(provider);
      return { provider, score };
    });

    // Providers with nothing to show sink to the bottom instead of sorting as 0.
    scored.sort((a, b) => {
      if (a.score === null && b.score === null) {
        return a.provider.localeCompare(b.provider);
      }
      if (a.score === null) return 1;
      if (b.score === null) return -1;
      return a.score - b.score;
    });

    return scored.map((entry) => entry.provider);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [providers, regions, sortRegion, lookup]);

  const scale = useMemo(() => {
    const values = Array.from(lookup.values())
      .filter((cell): cell is NonNullable<CellValue> => Boolean(cell?.enough))
      .map((cell) => cell.value);
    if (values.length === 0) return { min: 0, max: 1 };
    return { min: Math.min(...values), max: Math.max(...values) };
  }, [lookup]);

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

  if (regions.length === 0) {
    return (
      <div className="mt-12 rounded-lg border border-gray-800 bg-gray-900/50 p-10 text-center">
        <p className="text-lg text-gray-300">No measurements yet.</p>
        <p className="mt-2 text-sm text-gray-500">
          This page fills in as transcriptions run. Check back shortly.
        </p>
      </div>
    );
  }

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

        <div className="flex flex-col gap-2">
          <span className="text-xs uppercase tracking-widest text-gray-500">
            Row order
          </span>
          <button
            className="rounded-lg border border-gray-800 bg-gray-900/60 px-3 py-2 text-sm text-gray-300 transition hover:text-white"
            type="button"
            onClick={() => setSortRegion(null)}
          >
            {sortRegion
              ? `Sorted by ${regionCity(sortRegion)} — reset`
              : "Sorted by global median"}
          </button>
        </div>
      </div>

      {/* Region highlight */}
      <div className="mt-6 flex flex-wrap items-center gap-3 text-sm">
        <MapPin className="h-4 w-4 text-purple-300" />
        {homeRegion ? (
          <span className="text-gray-300">
            Showing <span className="font-semibold text-white">{homeCity}</span>
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
                  homeRegion === region
                    ? "border-purple-500 bg-purple-600/30 text-white"
                    : "border-gray-700 text-gray-400 hover:text-white"
                }`}
                type="button"
                onClick={() => {
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
                Provider
              </th>
              {regions.map((region) => (
                <th
                  key={region}
                  className={`px-3 py-3 text-center text-xs font-medium transition ${
                    homeRegion === region
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
            {sortedProviders.map((provider) => {
              const pinned = pinnedProvider === provider;
              return (
                <tr
                  key={provider}
                  className={
                    pinned
                      ? "bg-purple-950/20"
                      : hover?.provider === provider
                        ? "bg-gray-900/60"
                        : ""
                  }
                >
                  <th
                    className={`sticky left-0 z-10 whitespace-nowrap px-4 py-2 text-left font-medium ${
                      pinned ? "bg-purple-950/60 text-white" : "bg-gray-950 text-gray-200"
                    }`}
                    scope="row"
                  >
                    <button
                      className="flex items-center gap-2 transition hover:text-purple-300"
                      title="Pin this provider"
                      type="button"
                      onClick={() =>
                        setPinnedProvider((current) =>
                          current === provider ? null : provider,
                        )
                      }
                    >
                      {pinned ? <Pin className="h-3 w-3" /> : null}
                      {providerDisplayName(provider)}
                    </button>
                  </th>

                  {regions.map((region) => {
                    const cell = cellFor(provider, region);
                    const isHome = homeRegion === region;

                    if (!cell || !cell.enough) {
                      return (
                        <td
                          key={region}
                          className={`px-3 py-2 text-center text-gray-700 ${
                            isHome ? "bg-purple-950/20" : ""
                          }`}
                          title={
                            cell
                              ? `${cell.samples} attempts — fewer than the ${minSamplesPerCell} needed`
                              : "No attempts recorded"
                          }
                          onMouseEnter={() => setHover({ provider, region })}
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
                        title={`${providerDisplayName(provider)} in ${regionCity(region)} — ${cell.samples.toLocaleString()} attempts`}
                        onMouseEnter={() => setHover({ provider, region })}
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
        A cell needs at least {minSamplesPerCell} attempts to show a number. Click a
        region to sort by it, or a provider to pin its row.
      </p>
    </div>
  );
}