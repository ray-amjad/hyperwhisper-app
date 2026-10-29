"use client";

import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
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
import {
  prefersReducedMotion,
  slideRows,
  snapshotRowTops,
} from "@/lib/flip-rows";
import { regionCity } from "@/lib/latency/fly-regions";
import { isRecord } from "@/src/lib/type-guards";
import {
  GLASS,
  LABEL,
  SEG,
  SEG_ITEM,
  SEG_OFF,
  SEG_ON,
} from "@/components/landing/dusk-pages";

export { FLIP_DURATION_MS, FLIP_EASING } from "@/lib/flip-rows";

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
  accuracy: "bg-[#c4b5fd]",
  latency: "bg-[#93c5fd]",
  cost: "bg-[#86efac]",
  privacy: "bg-[#fdba8c]",
};

const PRIORITY_ACCENT: Record<Priority, string> = {
  accuracy: "accent-[#c4b5fd]",
  latency: "accent-[#93c5fd]",
  cost: "accent-[#86efac]",
  privacy: "accent-[#fdba8c]",
};

const FOCUS =
  "focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)]";

/** A filter chip ("Must have", "Languages you dictate"). */
const TAG = `rounded-full border px-3 py-[7px] text-[13px] transition-colors duration-150 motion-reduce:transition-none ${FOCUS}`;
const TAG_ON =
  "border-[rgba(196,181,253,.5)] bg-[rgba(196,181,253,.08)] text-[var(--dusk-lav)]";
const TAG_OFF =
  "border-[var(--dusk-line)] text-[var(--dusk-mute)] hover:text-white";

/** Table header and numeric cells of the ranked list. */
const TH =
  "px-3 py-[15px] text-[11.5px] font-normal uppercase tracking-[1.4px] text-[var(--dusk-dim)]";
const TD_NUM = "px-3 py-[15px] text-end tabular-nums text-[#d6d8ea]";

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
      <span className="inline-flex items-center gap-1 rounded-full border border-[rgba(134,239,172,.35)] bg-[rgba(134,239,172,.08)] px-2 py-0.5 text-[11px] font-semibold uppercase tracking-wider text-[#a7f3c4]">
        <Laptop aria-hidden="true" className="h-3 w-3" />
        On device
      </span>
    );
  }

  return (
    <span className="inline-flex items-center gap-1 rounded-full border border-[rgba(147,197,253,.35)] bg-[rgba(147,197,253,.08)] px-2 py-0.5 text-[11px] font-semibold uppercase tracking-wider text-[#bfdbfe]">
      <Cloud aria-hidden="true" className="h-3 w-3" />
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
    <span className="rounded-full border border-[var(--dusk-line)] px-1.5 py-0.5 text-[11px] uppercase tracking-wider text-[var(--dusk-dim)]">
      {label}
    </span>
  );
}

/**
 * Weight changes closer together than this are one gesture: a slider drag, or
 * a held arrow key (#766). Only the first change of a gesture replays the
 * name swap and the stat pop; during the rest of it the text just updates, so
 * a value the reader can already see is never pushed back to transparent.
 * Above the pop's own 250 ms, so a pop that is still running is never
 * restarted from opacity 0.
 */
export const SWAP_REST_MS = 300;

/** Whether a weight change at `now` starts a gesture, and so may replay. */
export function startsGesture(now: number, lastChangeAt: number | null) {
  return lastChangeAt === null || now - lastChangeAt >= SWAP_REST_MS;
}

/**
 * Where everything stood just before a weight change, so the commit that
 * follows can animate from it. Taken only by the weight sliders and presets:
 * a platform / language / requirement toggle changes the row SET, and those
 * rows just appear.
 */
type MotionSnapshot = {
  rowTops: Map<string, number>;
  name: string | null;
  stats: string[];
  /** False mid-gesture: the swap and the pop are not replayed. */
  replay: boolean;
};

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

  const tbodyRef = useRef<HTMLTableSectionElement>(null);
  const rowRefs = useRef(new Map<string, HTMLTableRowElement>());
  const nameRef = useRef<HTMLHeadingElement>(null);
  const statsRef = useRef<HTMLDListElement>(null);
  const pendingMotion = useRef<MotionSnapshot | null>(null);
  const rowSlides = useRef(new Map<string, Animation>());
  const lastWeightChangeAt = useRef<number | null>(null);

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

    async function detectRegion() {
      try {
        const response = await fetch(
          `/api/geo/nearest-region?regions=${encodeURIComponent(regionsKey)}`,
          { signal: controller.signal },
        );
        const result: unknown = response.ok ? await response.json() : null;

        // A hand-picked region always wins, even if the answer lands later.
        if (
          regionPickedByUser.current ||
          !isRecord(result) ||
          typeof result.region !== "string"
        )
          return;
        setRegion(result.region);
        setDetectedCity(
          typeof result.city === "string"
            ? result.city
            : regionCity(result.region),
        );
      } catch {
        // A missing default is not worth an error message.
      }
    }

    void detectRegion();

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

  // FLIP the re-ranked rows (#766): each row is drawn back at the place it
  // held before the weight change, then let go so it slides to its new rank.
  // Runs after the DOM commit and before paint, so no frame shows the jump.
  // The slide itself is lib/flip-rows, shared with the /latency matrix (#812).
  useLayoutEffect(() => {
    const before = pendingMotion.current;

    pendingMotion.current = null;
    if (before === null || prefersReducedMotion()) return;

    const tbody = tbodyRef.current;
    const rows = rowRefs.current;
    const sameSet =
      rows.size === before.rowTops.size &&
      Array.from(rows.keys()).every((id) => before.rowTops.has(id));

    if (tbody !== null && sameSet) {
      slideRows(tbody, rows, before.rowTops, rowSlides.current);
    }

    // The best-match name and its stats swap in place: replay only what
    // changed, and only on the first change of a gesture (SWAP_REST_MS), so a
    // drag never pushes a value the reader can see back to transparent.
    if (!before.replay) return;
    const swapped: HTMLElement[] = [];
    const name = nameRef.current;

    if (
      name !== null &&
      before.name !== null &&
      name.textContent !== before.name
    ) {
      swapped.push(name);
    }
    statsRef.current?.querySelectorAll("dd").forEach((dd, i) => {
      if (before.stats[i] !== undefined && dd.textContent !== before.stats[i]) {
        swapped.push(dd);
      }
    });
    if (swapped.length === 0) return;
    // A data-motion-from element sits at its enter state with no transition
    // (globals.css); one flush for all of them, then dropping the attribute
    // lets each element's own transition carry it back.
    for (const el of swapped) el.setAttribute("data-motion-from", "");
    void swapped[0].offsetWidth;
    for (const el of swapped) el.removeAttribute("data-motion-from");
  }, [ranked]);

  const best = ranked[0] ?? null;
  const cloudCount = ranked.filter((entry) => isCloud(entry.model)).length;
  const deviceCount = ranked.length - cloudCount;
  const hasMeasurements = regions.length > 0;

  /** `at` is the input event's timeStamp, on the performance.now() clock. */
  function snapshotForMotion(at: number) {
    pendingMotion.current = null;
    const tbody = tbodyRef.current;
    const replay = startsGesture(at, lastWeightChangeAt.current);

    lastWeightChangeAt.current = at;

    if (tbody === null || prefersReducedMotion()) return;
    pendingMotion.current = {
      rowTops: snapshotRowTops(tbody, rowRefs.current),
      name: nameRef.current?.textContent ?? null,
      stats: Array.from(
        statsRef.current?.querySelectorAll("dd") ?? [],
        (dd) => dd.textContent ?? "",
      ),
      replay,
    };
  }

  function setPriority(priority: Priority, value: number, at: number) {
    snapshotForMotion(at);
    setWeights((current) => rebalance(current, priority, value));
  }

  function toggleRequirement(id: keyof Requirements) {
    setRequirements((current) => ({ ...current, [id]: !current[id] }));
  }

  // The base `grid-cols-1` is load-bearing, not decoration. Without an explicit
  // base track the lone implicit track is `auto`, so it is sized to the model
  // table's min-content and the whole grid runs past a phone viewport, taking
  // the control panel off-screen and out of reach — see #705.
  return (
    <div className="grid grid-cols-1 items-start gap-[22px] lg:grid-cols-[340px_minmax(0,1fr)]">
      {/* ---------------- Controls ---------------- */}
      {/* Sticks under the sticky site header, whose height LayoutWrapper publishes. */}
      <div className="lg:sticky lg:top-[var(--site-header-offset,7.5rem)] lg:self-start">
        <div className={`${GLASS} p-6 md:p-[26px]`}>
          <div className="flex items-baseline justify-between gap-3">
            <h2 className={LABEL}>Your 100 points</h2>
            <span className="text-xs tabular-nums text-[var(--dusk-dim)]">
              {Math.round(weights.accuracy)} / {Math.round(weights.latency)} /{" "}
              {Math.round(weights.cost)} / {Math.round(weights.privacy)}
            </span>
          </div>

          <div className="mt-4 flex h-1.5 overflow-hidden rounded-full bg-white/[.08]">
            {PRIORITIES.map((priority) => (
              <span
                key={priority}
                className={PRIORITY_BAR[priority]}
                style={{ width: `${weights[priority]}%` }}
              />
            ))}
          </div>

          <div className="mt-4 flex flex-wrap gap-1.5">
            {PRESETS.map((preset) => (
              <button
                key={preset.id}
                className={`rounded-full border border-dashed border-[var(--dusk-line)] px-3 py-[7px] text-[13px] text-[var(--dusk-mute)] transition-colors duration-150 hover:border-solid hover:border-[rgba(196,181,253,.5)] hover:text-white motion-reduce:transition-none ${FOCUS}`}
                type="button"
                onClick={(event) => {
                  // Same preset again: no re-render, so no snapshot to leave behind.
                  if (preset.weights === weights) return;
                  snapshotForMotion(event.timeStamp);
                  setWeights(preset.weights);
                }}
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
                    className="text-[14.5px] text-white"
                    htmlFor={`weight-${priority}`}
                  >
                    {PRIORITY_LABELS[priority]}
                  </label>
                  <span className="text-[14.5px] font-semibold tabular-nums text-white">
                    {Math.round(weights[priority])}
                  </span>
                </div>
                <p className="mt-0.5 text-xs text-[var(--dusk-mute)]">
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
                    setPriority(
                      priority,
                      Number(event.target.value),
                      event.timeStamp,
                    )
                  }
                />
              </div>
            ))}
          </div>

          <div className="mt-[26px]">
            <span className={LABEL} id="picker-platform-label">
              Your platform
            </span>
            <div
              aria-labelledby="picker-platform-label"
              className={`mt-2.5 flex ${SEG}`}
              role="group"
            >
              {(["macos", "windows"] as const).map((option) => (
                <button
                  key={option}
                  aria-pressed={platform === option}
                  className={`flex-1 ${SEG_ITEM} px-3 py-[7px] text-[13px] ${
                    platform === option ? SEG_ON : SEG_OFF
                  }`}
                  type="button"
                  onClick={() => setPlatform(option)}
                >
                  {option === "macos" ? "macOS" : "Windows"}
                </button>
              ))}
            </div>
          </div>

          <div className="mt-[22px]">
            <span className={LABEL} id="picker-languages-label">
              Languages you dictate
            </span>
            <div
              aria-labelledby="picker-languages-label"
              className="mt-2.5 flex flex-wrap gap-2"
              role="group"
            >
              {LANGUAGE_OPTIONS.map((option) => (
                <button
                  key={option.id}
                  aria-pressed={language === option.id}
                  className={`${TAG} ${language === option.id ? TAG_ON : TAG_OFF}`}
                  type="button"
                  onClick={() => setLanguage(option.id)}
                >
                  {option.label}
                </button>
              ))}
            </div>
          </div>

          <div className="mt-[22px]">
            <span className={LABEL} id="picker-requirements-label">
              Must have
            </span>
            <div
              aria-labelledby="picker-requirements-label"
              className="mt-2.5 flex flex-wrap gap-2"
              role="group"
            >
              {REQUIREMENT_OPTIONS.map((option) => (
                <button
                  key={option.id}
                  aria-pressed={requirements[option.id]}
                  className={`${TAG} ${requirements[option.id] ? TAG_ON : TAG_OFF}`}
                  type="button"
                  onClick={() => toggleRequirement(option.id)}
                >
                  {option.label}
                </button>
              ))}
            </div>
          </div>

          {hasMeasurements ? (
            <div className="mt-[22px]">
              <span className={LABEL} id="picker-region-label">
                Closest region
              </span>
              <select
                aria-labelledby="picker-region-label"
                className={`mt-2.5 w-full rounded-xl border border-[#2a2e4a] bg-[#090b18] px-3.5 py-2.5 text-sm text-white transition-colors hover:border-[rgba(196,181,253,.4)] ${FOCUS}`}
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
              {/* Reserves its line before the nearest-region answer lands, so the
                  panel does not grow under the reader. */}
              <p className="mt-2 flex min-h-4 items-center gap-1.5 text-xs text-[var(--dusk-lav)]">
                {detectedCity !== null ? (
                  <>
                    <MapPin aria-hidden="true" className="h-3 w-3" />
                    Picked for you — closest to {detectedCity}
                  </>
                ) : null}
              </p>
            </div>
          ) : null}
        </div>
      </div>

      {/* ---------------- Results ---------------- */}
      <div className="min-w-0">
        {best === null ? (
          <div className={`${GLASS} p-10 text-center`}>
            <p className="text-lg text-white">
              Nothing matches all of those requirements.
            </p>
            <p className="mt-2 text-sm text-[var(--dusk-mute)]">
              Turn one of the &ldquo;must have&rdquo; filters back off.
            </p>
          </div>
        ) : (
          <>
            <div className="rounded-[26px] border border-[rgba(196,181,253,.35)] bg-[radial-gradient(70%_120%_at_100%_0%,rgba(129,90,220,.28),transparent_60%),radial-gradient(50%_90%_at_0%_100%,rgba(253,186,140,.12),transparent_60%),#0b0d1f] p-6 md:p-8">
              <p className={`${LABEL} !text-[var(--dusk-lav)]`}>
                Best match for how you spent your points
              </p>
              <div className="mt-2.5 flex flex-wrap items-center gap-3">
                <h2
                  ref={nameRef}
                  className="t-text-swap text-2xl font-semibold leading-[1.1] tracking-[-0.03em] text-white [overflow-wrap:anywhere] md:text-[38px]"
                >
                  {best.model.name}
                </h2>
                <PlacementBadge model={best.model} />
              </div>
              <p className="mt-1.5 text-[15px] text-[var(--dusk-mute)]">
                {isDevice(best.model)
                  ? `${best.model.vendorLabel} · ${
                      platform === "windows" && best.model.sizeWindows
                        ? best.model.sizeWindows
                        : best.model.size
                    } download · runs entirely on your Mac or PC`
                  : `${best.model.vendorLabel} · runs on HyperWhisper Cloud`}
              </p>

              <dl
                ref={statsRef}
                className="mt-[26px] grid grid-cols-2 gap-x-6 gap-y-5 sm:grid-cols-4 lg:grid-cols-2 xl:grid-cols-4"
              >
                <div>
                  <dt className={LABEL}>Word errors</dt>
                  <dd className="t-number-pop mt-1 text-2xl font-semibold tabular-nums text-white">
                    {best.model.wer === null ? "—" : `${best.model.wer}%`}
                  </dd>
                </div>
                <div>
                  <dt className={LABEL}>Cost</dt>
                  <dd className="t-number-pop mt-1 text-2xl font-semibold tabular-nums text-white">
                    {formatCost(best.model)}
                  </dd>
                  {/* A second <dd>, not a <p> in the first one: a <dl> wrapper may hold
                      only <dt>/<dd> (#890), and inside the first <dd> the note would
                      inherit its text-2xl line box. */}
                  <dd className="t-number-pop mt-0.5 text-xs text-[var(--dusk-mute)]">
                    {formatDollars(best.model) ?? "no per-minute cost"}
                  </dd>
                </div>
                <div>
                  <dt className={LABEL}>Per audio minute</dt>
                  <dd className="t-number-pop mt-1 text-2xl font-semibold tabular-nums text-white">
                    {formatSeconds(best.estimatedSeconds)}
                  </dd>
                </div>
                <div>
                  <dt className={LABEL}>Match</dt>
                  <dd className="t-number-pop mt-1 text-2xl font-semibold tabular-nums text-white">
                    {Math.round(best.score * 100)}
                  </dd>
                </div>
              </dl>

              <p className="mt-6 border-t border-[var(--dusk-line)] pt-4 text-sm leading-[1.6] text-[var(--dusk-mute)]">
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

            <div className="mt-[18px]">
              <div className="flex flex-wrap items-baseline justify-between gap-2 px-1">
                <h2 className={LABEL}>Every model, ranked</h2>
                <p className="text-xs text-[var(--dusk-dim)]">
                  {cloudCount} cloud · {deviceCount} on-device
                </p>
              </div>

              <div
                aria-label="Model comparison table"
                className={`mt-3 overflow-x-auto ${GLASS} ${FOCUS}`}
                role="region"
                // eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex -- scrollable region needs a tab stop (WCAG 2.1.1, axe scrollable-region-focusable)
                tabIndex={0}
              >
                <table className="w-full border-collapse text-[14.5px]">
                  <thead>
                    <tr className="border-b border-[var(--dusk-line)]">
                      <th className={`${TH} ps-6 text-start`}>Model</th>
                      <th className={`${TH} text-end`}>WER</th>
                      <th className={`${TH} text-end`}>Cost</th>
                      <th className={`${TH} text-end`}>Per audio min</th>
                      <th className={`${TH} text-end`} title={MEASURED_HINT}>
                        Measured clip
                      </th>
                      <th className={`${TH} hidden text-start md:table-cell`}>
                        Why it scored
                      </th>
                      <th className={`${TH} pe-6 text-end`}>Match</th>
                    </tr>
                  </thead>
                  <tbody ref={tbodyRef}>
                    {ranked.map((entry, index) => (
                      <tr
                        key={entry.model.id}
                        ref={(row) => {
                          if (row === null)
                            rowRefs.current.delete(entry.model.id);
                          else rowRefs.current.set(entry.model.id, row);
                        }}
                        className={`border-b border-white/[.05] transition-colors last:border-0 hover:bg-white/[.03] motion-reduce:transition-none ${
                          index === 0 ? "bg-[rgba(196,181,253,.06)]" : ""
                        }`}
                      >
                        <td className="py-[15px] pe-3 ps-6">
                          <div className="flex flex-wrap items-center gap-2">
                            <span className="font-medium text-white">
                              {entry.model.name}
                            </span>
                            <PlacementBadge model={entry.model} />
                            <AccuracyNote model={entry.model} />
                          </div>
                          <div className="mt-0.5 text-[12.5px] text-[var(--dusk-mute)]">
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
                        <td className={TD_NUM}>
                          {entry.model.wer === null
                            ? "—"
                            : `${entry.model.wer}%`}
                        </td>
                        <td className={TD_NUM}>{formatCost(entry.model)}</td>
                        <td className={TD_NUM}>
                          {formatSeconds(entry.estimatedSeconds)}
                        </td>
                        <td
                          className={TD_NUM}
                          title={
                            entry.measuredClipMs === null
                              ? undefined
                              : MEASURED_HINT
                          }
                        >
                          {entry.measuredClipMs === null ? (
                            <span className="text-[var(--dusk-dim)]">—</span>
                          ) : (
                            <>
                              <span className="me-1 text-[var(--dusk-lav)]">
                                ●
                              </span>
                              {formatClipMs(entry.measuredClipMs)}
                            </>
                          )}
                        </td>
                        <td className="hidden px-3 py-[15px] md:table-cell">
                          <div className="flex h-1.5 w-40 overflow-hidden rounded-full bg-white/[.08]">
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
                        <td className="py-[15px] pe-6 ps-3 text-end font-semibold tabular-nums text-white">
                          <span className="inline-flex items-center justify-end gap-2.5">
                            <span
                              aria-hidden="true"
                              className="hidden h-1.5 rounded-full bg-[image:var(--dusk-accent)] sm:block"
                              style={{
                                width: `${Math.round(entry.score * 60)}px`,
                              }}
                            />
                            {Math.round(entry.score * 100)}
                          </span>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>

              <p className="mt-3 px-1 text-xs leading-[1.6] text-[var(--dusk-mute)]">
                The two speed columns are different questions.{" "}
                <span className="text-white">Per audio min</span> is how long a
                minute of audio takes, from the leaderboard&apos;s published
                speed factor — or, for on-device rows, estimated from the
                app&apos;s own speed rating.{" "}
                <span className="text-white">Measured clip</span>, marked{" "}
                <span className="text-[var(--dusk-lav)]">●</span>, is our own
                median wall time for one dictation clip under 10 seconds from
                your region. A short clip is mostly round trip rather than
                decoding, so the two do not convert into one another — and the
                ranking uses only the first, so a model we have measured is
                never compared against one we have not on a different footing.
              </p>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
