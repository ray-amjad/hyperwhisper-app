"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { Cloud, Laptop, MapPin } from "lucide-react";

// The one place the credit rate is declared. This module is deliberately kept
// free of Next and Stripe imports so a client component can read it, which two
// others already do — the page had its own copy, and changing the rate at
// source would have repriced checkout while this page kept quoting the old one.
import { CREDITS_PER_DOLLAR } from "@/app/api/checkout/credits/validation";
import {
  isCloud,
  isDevice,
  modelsForPlatform,
  type Model,
  type Platform,
} from "@/lib/choosing-a-model/catalog";
import {
  DEFAULT_WEIGHTS,
  PRESETS,
  PRIORITIES,
  PRIORITY_LABELS,
  buildPool,
  creditsPerMinute,
  rankModels,
  rebalance,
  type LanguageNeed,
  type Priority,
  type Requirements,
  type ScoredModel,
  type Weights,
} from "@/lib/choosing-a-model/scoring";
import { regionCity } from "@/lib/latency/fly-regions";

type MeasuredLatencyByRegion = Record<string, Record<string, number>>;

type Props = {
  measured: MeasuredLatencyByRegion;
  /** Regions we hold measurements for, most-measured first. */
  regions: string[];
};

const PRIORITY_HINTS: Record<Priority, string> = {
  accuracy: "How often it gets a word wrong",
  latency: "How long you wait for the text",
  cost: "What a minute of audio costs you",
  privacy: "Whether the audio leaves your machine",
};

/**
 * Bar colours, one per priority. They repeat in the budget bar, the sliders and
 * every row's breakdown, so a colour always means the same thing on this page.
 */
const PRIORITY_BAR: Record<Priority, string> = {
  accuracy: "bg-purple-500",
  latency: "bg-blue-500",
  cost: "bg-emerald-500",
  privacy: "bg-amber-500",
};

const PRIORITY_ACCENT: Record<Priority, string> = {
  accuracy: "accent-purple-500",
  latency: "accent-blue-500",
  cost: "accent-emerald-500",
  privacy: "accent-amber-500",
};

const LANGUAGE_OPTIONS: { id: LanguageNeed; label: string }[] = [
  { id: "english", label: "English only" },
  { id: "european", label: "European" },
  { id: "wide", label: "Wide multilingual" },
];

const REQUIREMENT_OPTIONS: { id: keyof Requirements; label: string }[] = [
  { id: "streaming", label: "Live streaming" },
  { id: "customVocabulary", label: "Custom vocabulary" },
  { id: "stableOnly", label: "No preview models" },
];

function formatCost(model: Model): string {
  if (isDevice(model)) return "Free";
  const credits = creditsPerMinute(model);
  return `${credits.toFixed(2).replace(/\.?0+$/, "")} cr/min`;
}

function formatDollars(model: Model): string | null {
  if (isDevice(model)) return null;
  const perThousand = creditsPerMinute(model) * (1000 / CREDITS_PER_DOLLAR);
  return `$${perThousand.toFixed(2)} per 1,000 min`;
}

function formatSeconds(seconds: number | null): string {
  if (seconds === null) return "—";
  return seconds < 1
    ? `${Math.round(seconds * 1000)} ms`
    : `${seconds.toFixed(1)} s`;
}

/**
 * A measured round trip for one short clip. Kept in its own formatter, and its
 * own column, because it is not the per-minute estimate beside it and must
 * never be mistaken for it — see `measuredClipMs` in scoring.ts.
 */
function formatClipMs(ms: number): string {
  return ms < 1000 ? `${Math.round(ms)} ms` : `${(ms / 1000).toFixed(1)} s`;
}

/**
 * The language signal, spelled out. The count was carried but never rendered,
 * so a reader had no way to see that a model survived — or was cut from — a
 * breadth filter on a number its vendor never published.
 */
function formatLanguages(model: Model): string {
  if (model.languageScope === "unknown" || model.languages === null) {
    return "language count not published";
  }
  return `${model.languages} language${model.languages === 1 ? "" : "s"}`;
}

const MEASURED_HINT =
  "Our median wall time for one dictation clip under 10 seconds, measured " +
  "from your region over the last 90 days. Not a per-minute figure.";

/** The one badge that answers "does my audio leave this machine?". */
function PlacementBadge({ model }: { model: Model }) {
  if (isDevice(model)) {
    return (
      <span className="inline-flex items-center gap-1 rounded-full border border-emerald-500/40 bg-emerald-500/10 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wider text-emerald-300">
        <Laptop className="h-3 w-3" aria-hidden="true" />
        On device
      </span>
    );
  }
  return (
    <span className="inline-flex items-center gap-1 rounded-full border border-sky-500/40 bg-sky-500/10 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wider text-sky-300">
      <Cloud className="h-3 w-3" aria-hidden="true" />
      Cloud
    </span>
  );
}

function AccuracyNote({ model }: { model: Model }) {
  const label =
    model.accuracyBasis === "sameWeights"
      ? "same weights"
      : model.accuracyBasis === "appRating"
        ? "app rating"
        : model.accuracyBasis === "none"
          ? "not benchmarked"
          : null;

  if (label === null) return null;
  return (
    <span className="rounded-full border border-gray-700 px-1.5 py-0.5 text-[10px] uppercase tracking-wider text-gray-500">
      {label}
    </span>
  );
}

export default function ModelPicker({ measured, regions }: Props) {
  const [weights, setWeights] = useState<Weights>(DEFAULT_WEIGHTS);
  const [platform, setPlatform] = useState<Platform>("macos");
  const [language, setLanguage] = useState<LanguageNeed>("english");
  const [requirements, setRequirements] = useState<Requirements>({
    streaming: false,
    customVocabulary: false,
    stableOnly: false,
  });
  const [region, setRegion] = useState<string | null>(regions[0] ?? null);
  const [detectedCity, setDetectedCity] = useState<string | null>(null);
  const regionPickedByUser = useRef(false);

  const regionsKey = regions.join(",");

  // Ask the edge which region this reader is nearest to, the same way /latency
  // does. Nothing is stored; a failure just leaves the default region selected.
  useEffect(() => {
    if (regionsKey === "") return;
    // Do not even ask once the reader has chosen. The check below catches an
    // answer that lands after a hand-pick; this one keeps a settled choice from
    // costing a request at all, and is the guard this effect lost when it was
    // copied over from LatencyMatrix.
    if (regionPickedByUser.current) return;
    const controller = new AbortController();

    fetch(`/api/geo/nearest-region?regions=${encodeURIComponent(regionsKey)}`, {
      signal: controller.signal,
    })
      .then((response) => (response.ok ? response.json() : null))
      .then((result) => {
        // A hand-picked region always wins, even if the answer lands later.
        if (regionPickedByUser.current || !result?.region) return;
        setRegion(result.region);
        setDetectedCity(result.city ?? regionCity(result.region));
      })
      .catch(() => {
        // A missing default is not worth an error message.
      });

    return () => controller.abort();
  }, [regionsKey]);

  const ranked: ScoredModel[] = useMemo(() => {
    const pool = buildPool(
      modelsForPlatform(platform),
      platform,
      language,
      requirements,
    );
    return rankModels(pool, {
      weights,
      measured: (region && measured[region]) || {},
    });
  }, [weights, platform, language, requirements, region, measured]);

  const best = ranked[0] ?? null;
  const cloudCount = ranked.filter((entry) => isCloud(entry.model)).length;
  const deviceCount = ranked.length - cloudCount;
  const hasMeasurements = regions.length > 0;

  function setPriority(priority: Priority, value: number) {
    setWeights((current) => rebalance(current, priority, value));
  }

  function toggleRequirement(id: keyof Requirements) {
    setRequirements((current) => ({ ...current, [id]: !current[id] }));
  }

  return (
    <div className="mt-12 grid gap-8 lg:grid-cols-[380px_minmax(0,1fr)]">
      {/* ---------------- Controls ---------------- */}
      <div className="lg:sticky lg:top-6 lg:self-start">
        <div className="rounded-lg border border-gray-800 bg-gray-900/50 p-6 backdrop-blur-xl">
          <div className="flex items-baseline justify-between gap-3">
            <h2 className="text-sm font-semibold uppercase tracking-widest text-gray-400">
              Your 100 points
            </h2>
            <span className="text-xs text-gray-500">
              {Math.round(weights.accuracy)} / {Math.round(weights.latency)} /{" "}
              {Math.round(weights.cost)} / {Math.round(weights.privacy)}
            </span>
          </div>

          <div className="mt-4 flex h-3 overflow-hidden rounded-full border border-gray-800">
            {PRIORITIES.map((priority) => (
              <span
                key={priority}
                className={PRIORITY_BAR[priority]}
                style={{ width: `${weights[priority]}%` }}
              />
            ))}
          </div>

          <div className="mt-4 flex flex-wrap gap-1">
            {PRESETS.map((preset) => (
              <button
                key={preset.id}
                className="rounded-md border border-dashed border-gray-700 px-2.5 py-1 text-xs text-gray-400 transition hover:border-solid hover:border-purple-500/60 hover:text-white"
                type="button"
                onClick={() => setWeights(preset.weights)}
              >
                {preset.label}
              </button>
            ))}
          </div>

          <div className="mt-6 space-y-5">
            {PRIORITIES.map((priority) => (
              <div key={priority}>
                <div className="flex items-baseline justify-between gap-2">
                  <label
                    className="text-sm font-medium text-white"
                    htmlFor={`weight-${priority}`}
                  >
                    {PRIORITY_LABELS[priority]}
                  </label>
                  <span className="font-mono text-sm tabular-nums text-gray-300">
                    {Math.round(weights[priority])}
                  </span>
                </div>
                <p className="mt-0.5 text-xs text-gray-500">
                  {PRIORITY_HINTS[priority]}
                </p>
                <input
                  className={`mt-2 w-full ${PRIORITY_ACCENT[priority]}`}
                  id={`weight-${priority}`}
                  max={100}
                  min={0}
                  type="range"
                  value={Math.round(weights[priority])}
                  onChange={(event) =>
                    setPriority(priority, Number(event.target.value))
                  }
                />
              </div>
            ))}
          </div>

          <div className="mt-6 border-t border-gray-800 pt-5">
            <span className="text-xs uppercase tracking-widest text-gray-500">
              Your platform
            </span>
            <div className="mt-2 flex gap-1 rounded-lg border border-gray-800 bg-gray-900/60 p-1">
              {(["macos", "windows"] as const).map((option) => (
                <button
                  key={option}
                  className={`flex-1 rounded-md px-3 py-1.5 text-sm transition ${
                    platform === option
                      ? "bg-purple-600 text-white"
                      : "text-gray-400 hover:text-white"
                  }`}
                  type="button"
                  onClick={() => setPlatform(option)}
                >
                  {option === "macos" ? "macOS" : "Windows"}
                </button>
              ))}
            </div>
          </div>

          <div className="mt-5">
            <span className="text-xs uppercase tracking-widest text-gray-500">
              Languages you dictate
            </span>
            <div className="mt-2 flex flex-wrap gap-1">
              {LANGUAGE_OPTIONS.map((option) => (
                <button
                  key={option.id}
                  className={`rounded-full border px-3 py-1 text-xs transition ${
                    language === option.id
                      ? "border-purple-500/60 bg-purple-600/20 text-white"
                      : "border-gray-700 text-gray-400 hover:text-white"
                  }`}
                  type="button"
                  onClick={() => setLanguage(option.id)}
                >
                  {option.label}
                </button>
              ))}
            </div>
          </div>

          <div className="mt-5">
            <span className="text-xs uppercase tracking-widest text-gray-500">
              Must have
            </span>
            <div className="mt-2 flex flex-wrap gap-1">
              {REQUIREMENT_OPTIONS.map((option) => (
                <button
                  key={option.id}
                  aria-pressed={requirements[option.id]}
                  className={`rounded-full border px-3 py-1 text-xs transition ${
                    requirements[option.id]
                      ? "border-purple-500/60 bg-purple-600/20 text-white"
                      : "border-gray-700 text-gray-400 hover:text-white"
                  }`}
                  type="button"
                  onClick={() => toggleRequirement(option.id)}
                >
                  {option.label}
                </button>
              ))}
            </div>
          </div>

          {hasMeasurements ? (
            <div className="mt-5">
              <span className="text-xs uppercase tracking-widest text-gray-500">
                Closest region
              </span>
              <select
                className="mt-2 w-full rounded-lg border border-gray-800 bg-gray-900/60 px-3 py-2 text-sm text-gray-300 transition hover:text-white"
                value={region ?? ""}
                onChange={(event) => {
                  regionPickedByUser.current = true;
                  setRegion(event.target.value);
                  setDetectedCity(null);
                }}
              >
                {regions.map((code) => (
                  <option key={code} value={code}>
                    {regionCity(code)}
                  </option>
                ))}
              </select>
              {detectedCity !== null ? (
                <p className="mt-2 flex items-center gap-1.5 text-xs text-purple-300">
                  <MapPin className="h-3 w-3" aria-hidden="true" />
                  Picked for you — closest to {detectedCity}
                </p>
              ) : null}
            </div>
          ) : null}
        </div>
      </div>

      {/* ---------------- Results ---------------- */}
      <div>
        {best === null ? (
          <div className="rounded-lg border border-gray-800 bg-gray-900/50 p-10 text-center">
            <p className="text-lg text-gray-300">
              Nothing matches all of those requirements.
            </p>
            <p className="mt-2 text-sm text-gray-500">
              Turn one of the &ldquo;must have&rdquo; filters back off.
            </p>
          </div>
        ) : (
          <>
            <div className="relative overflow-hidden rounded-lg border border-gray-800 bg-gray-900/50 p-6 backdrop-blur-xl">
              <span className="absolute inset-y-0 left-0 w-1 bg-gradient-to-b from-purple-500 to-blue-500" />
              <p className="text-xs uppercase tracking-widest text-gray-500">
                Best match for how you spent your points
              </p>
              <div className="mt-2 flex flex-wrap items-center gap-3">
                <h2 className="text-2xl font-bold text-white">
                  {best.model.name}
                </h2>
                <PlacementBadge model={best.model} />
              </div>
              <p className="mt-1 text-sm text-gray-400">
                {isDevice(best.model)
                  ? `${best.model.vendorLabel} · ${
                      platform === "windows" && best.model.sizeWindows
                        ? best.model.sizeWindows
                        : best.model.size
                    } download · runs entirely on your Mac or PC`
                  : `${best.model.vendorLabel} · runs on HyperWhisper Cloud`}
              </p>

              <dl className="mt-6 grid gap-3 sm:grid-cols-4">
                <div className="rounded-lg border border-gray-800 bg-gray-950/60 px-3 py-2.5">
                  <dt className="text-[10px] uppercase tracking-wider text-gray-500">
                    Word errors
                  </dt>
                  <dd className="mt-1 font-mono text-lg tabular-nums text-white">
                    {best.model.wer === null ? "—" : `${best.model.wer}%`}
                  </dd>
                </div>
                <div className="rounded-lg border border-gray-800 bg-gray-950/60 px-3 py-2.5">
                  <dt className="text-[10px] uppercase tracking-wider text-gray-500">
                    Cost
                  </dt>
                  <dd className="mt-1 font-mono text-lg tabular-nums text-white">
                    {formatCost(best.model)}
                  </dd>
                  <p className="mt-0.5 text-[10px] text-gray-500">
                    {formatDollars(best.model) ?? "no per-minute cost"}
                  </p>
                </div>
                <div className="rounded-lg border border-gray-800 bg-gray-950/60 px-3 py-2.5">
                  <dt className="text-[10px] uppercase tracking-wider text-gray-500">
                    Per audio minute
                  </dt>
                  <dd className="mt-1 font-mono text-lg tabular-nums text-white">
                    {formatSeconds(best.estimatedSeconds)}
                  </dd>
                </div>
                <div className="rounded-lg border border-gray-800 bg-gray-950/60 px-3 py-2.5">
                  <dt className="text-[10px] uppercase tracking-wider text-gray-500">
                    Match
                  </dt>
                  <dd className="mt-1 font-mono text-lg tabular-nums text-white">
                    {Math.round(best.score * 100)}
                  </dd>
                </div>
              </dl>

              <p className="mt-5 border-t border-dashed border-gray-800 pt-4 text-sm text-gray-400">
                {isDevice(best.model)
                  ? "Your audio never leaves the machine, so there is no per-minute cost and no region to worry about."
                  : best.measuredClipMs !== null
                    ? `Per audio minute is the leaderboard's published speed factor. Separately, we have measured this model ourselves from ${regionCity(
                        region ?? "",
                      )}: a median of ${formatClipMs(
                        best.measuredClipMs,
                      )} to come back with the text for one short dictation clip.`
                    : "Per audio minute is the leaderboard's published speed factor. We have not measured this model from your region yet."}
              </p>
            </div>

            <div className="mt-8">
              <div className="flex flex-wrap items-baseline justify-between gap-2">
                <h2 className="text-sm font-semibold uppercase tracking-widest text-gray-400">
                  Every model, ranked
                </h2>
                <p className="text-xs text-gray-500">
                  {cloudCount} cloud · {deviceCount} on-device
                </p>
              </div>

              <div className="mt-4 overflow-x-auto rounded-lg border border-gray-800 bg-gray-950/80">
                <table className="w-full border-collapse text-sm">
                  <thead>
                    <tr className="border-b border-gray-800">
                      <th className="px-4 py-3 text-left text-xs uppercase tracking-widest text-gray-500">
                        Model
                      </th>
                      <th className="px-3 py-3 text-right text-xs uppercase tracking-widest text-gray-500">
                        WER
                      </th>
                      <th className="px-3 py-3 text-right text-xs uppercase tracking-widest text-gray-500">
                        Cost
                      </th>
                      <th className="px-3 py-3 text-right text-xs uppercase tracking-widest text-gray-500">
                        Per audio min
                      </th>
                      <th
                        className="px-3 py-3 text-right text-xs uppercase tracking-widest text-gray-500"
                        title={MEASURED_HINT}
                      >
                        Measured clip
                      </th>
                      <th className="hidden px-3 py-3 text-left text-xs uppercase tracking-widest text-gray-500 md:table-cell">
                        Why it scored
                      </th>
                      <th className="px-4 py-3 text-right text-xs uppercase tracking-widest text-gray-500">
                        Match
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {ranked.map((entry, index) => (
                      <tr
                        key={entry.model.id}
                        className={`border-b border-gray-900 transition hover:bg-gray-900/40 ${
                          index === 0 ? "bg-purple-600/5" : ""
                        }`}
                      >
                        <td className="px-4 py-3">
                          <div className="flex flex-wrap items-center gap-2">
                            <span className="font-medium text-white">
                              {entry.model.name}
                            </span>
                            <PlacementBadge model={entry.model} />
                            <AccuracyNote model={entry.model} />
                          </div>
                          <div className="mt-0.5 text-xs text-gray-500">
                            {entry.model.vendorLabel}
                            {isDevice(entry.model)
                              ? ` · ${
                                  platform === "windows" &&
                                  entry.model.sizeWindows
                                    ? entry.model.sizeWindows
                                    : entry.model.size
                                }`
                              : ""}
                            {` · ${formatLanguages(entry.model)}`}
                          </div>
                        </td>
                        <td className="px-3 py-3 text-right font-mono tabular-nums text-gray-300">
                          {entry.model.wer === null
                            ? "—"
                            : `${entry.model.wer}%`}
                        </td>
                        <td className="px-3 py-3 text-right font-mono tabular-nums text-gray-300">
                          {formatCost(entry.model)}
                        </td>
                        <td className="px-3 py-3 text-right font-mono tabular-nums text-gray-300">
                          {formatSeconds(entry.estimatedSeconds)}
                        </td>
                        <td
                          className="px-3 py-3 text-right font-mono tabular-nums text-gray-300"
                          title={
                            entry.measuredClipMs === null
                              ? undefined
                              : MEASURED_HINT
                          }
                        >
                          {entry.measuredClipMs === null ? (
                            <span className="text-gray-600">—</span>
                          ) : (
                            <>
                              <span className="mr-1 text-purple-400">●</span>
                              {formatClipMs(entry.measuredClipMs)}
                            </>
                          )}
                        </td>
                        <td className="hidden px-3 py-3 md:table-cell">
                          <div className="flex h-2 w-40 overflow-hidden rounded-full bg-gray-800">
                            {PRIORITIES.map((priority) => (
                              <span
                                key={priority}
                                className={PRIORITY_BAR[priority]}
                                style={{
                                  width: `${entry.contributions[priority] * 100}%`,
                                }}
                              />
                            ))}
                          </div>
                        </td>
                        <td className="px-4 py-3 text-right font-mono font-semibold tabular-nums text-white">
                          {Math.round(entry.score * 100)}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>

              <p className="mt-3 text-xs text-gray-500">
                The two speed columns are different questions.{" "}
                <span className="text-gray-300">Per audio min</span> is how long
                a minute of audio takes, from the leaderboard&apos;s published
                speed factor — or, for on-device rows, estimated from the
                app&apos;s own speed rating.{" "}
                <span className="text-gray-300">Measured clip</span>, marked{" "}
                <span className="text-purple-400">●</span>, is our own median
                wall time for one dictation clip under 10 seconds from your
                region. A short clip is mostly round trip rather than decoding,
                so the two do not convert into one another — and the ranking
                uses only the first, so a model we have measured is never
                compared against one we have not on a different footing.
              </p>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
