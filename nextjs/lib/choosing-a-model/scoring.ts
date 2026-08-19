/**
 * Turns "how much do you care about each of four things" into a ranking.
 *
 * The reader spends a fixed 100 points across accuracy, latency, cost and
 * privacy. Every model gets a 0-1 sub-score on each of the four, and its total
 * is the weighted sum. Nothing here reads the DOM or React state, so the whole
 * ranking is testable as data in / data out.
 *
 * Sub-scores are normalised against the pool actually being ranked, not against
 * a fixed scale. That is deliberate: "cheap" means cheap compared to the other
 * models you could pick right now, and filtering the pool should re-spread the
 * remaining models across the full 0-1 range rather than bunching them.
 */

// Types only. This module stays a pure function of the models it is handed —
// it never reaches into the catalog itself — so the ranking can be exercised
// against a fixture as easily as against what we ship.
import type { CloudModel, DeviceModel, Model } from "./catalog";

/** Narrowing helpers, kept local so this module has no runtime dependencies. */
function isCloud(model: Model): model is CloudModel {
  return model.placement === "cloud";
}

function isDevice(model: Model): model is DeviceModel {
  return model.placement === "device";
}

export type Priority = "accuracy" | "latency" | "cost" | "privacy";

export const PRIORITIES: readonly Priority[] = [
  "accuracy",
  "latency",
  "cost",
  "privacy",
];

export type Weights = Record<Priority, number>;

export const TOTAL_POINTS = 100;

export const DEFAULT_WEIGHTS: Weights = {
  accuracy: 40,
  latency: 20,
  cost: 30,
  privacy: 10,
};

export type Preset = { id: string; label: string; weights: Weights };

export const PRESETS: readonly Preset[] = [
  { id: "balanced", label: "Balanced", weights: { accuracy: 35, latency: 25, cost: 30, privacy: 10 } },
  { id: "accuracy", label: "Accuracy first", weights: { accuracy: 70, latency: 10, cost: 15, privacy: 5 } },
  { id: "cheap", label: "Cheapest", weights: { accuracy: 20, latency: 15, cost: 60, privacy: 5 } },
  { id: "fast", label: "Fastest", weights: { accuracy: 20, latency: 60, cost: 15, privacy: 5 } },
  { id: "private", label: "Fully private", weights: { accuracy: 25, latency: 15, cost: 10, privacy: 50 } },
];

/** Which languages the reader needs, as a minimum documented language count. */
export type LanguageNeed = "english" | "european" | "wide";

export const LANGUAGE_MINIMUMS: Record<LanguageNeed, number> = {
  english: 1,
  european: 13,
  wide: 60,
};

export type Requirements = {
  streaming: boolean;
  customVocabulary: boolean;
  stableOnly: boolean;
};

export const NO_REQUIREMENTS: Requirements = {
  streaming: false,
  customVocabulary: false,
  stableOnly: false,
};

/**
 * Median milliseconds a provider took to answer, per backend provider id, from
 * the measurements behind /latency. Keyed `sttProvider` or
 * `sttProvider:modelId` — a model-level entry wins when we have one.
 */
export type MeasuredLatency = Record<string, number>;

export type ScoreInput = {
  weights: Weights;
  /** Timings for the reader's region. Empty falls back to speed factors. */
  measured: MeasuredLatency;
};

export type ScoredModel = {
  model: Model;
  /** 0-1 per priority, before weighting. */
  parts: Record<Priority, number>;
  /** Each priority's share of the final score. Sums to `score`. */
  contributions: Record<Priority, number>;
  /** 0-1. */
  score: number;
  /**
   * Estimated seconds to transcribe one minute of audio, for display. Null when
   * a cloud model has neither a measurement nor a published speed factor.
   */
  estimatedSeconds: number | null;
  /** True when `estimatedSeconds` came from our own measurements. */
  latencyIsMeasured: boolean;
};

/**
 * Rough seconds an on-device model needs per audio minute, derived from the
 * app's 1-5 speed rating. Real time depends on the reader's hardware, which we
 * cannot know, so this is an ordering device — not a promise.
 */
function deviceSeconds(speedRating: number): number {
  return 60 / (speedRating * 11) + 0.15;
}

/**
 * Seconds to transcribe one audio minute.
 *
 * A measured median beats a published speed factor: it is our own traffic, on
 * our own edge, including whatever the provider actually does under load. The
 * leaderboard's speed factor is the fallback for a model we have not measured
 * yet — a new model, or one nobody has picked.
 */
export function estimateSeconds(
  model: Model,
  measured: MeasuredLatency,
): { seconds: number | null; isMeasured: boolean } {
  if (isDevice(model)) {
    return { seconds: deviceSeconds(model.speedRating), isMeasured: false };
  }

  const modelKey = `${model.sttProvider}:${model.modelId}`;
  const ms = measured[modelKey] ?? measured[model.sttProvider];
  if (ms !== undefined) {
    return { seconds: ms / 1000, isMeasured: true };
  }

  if (model.speedFactor === null) {
    return { seconds: null, isMeasured: false };
  }
  // Speed factor is audio seconds per wall second, so one audio minute takes
  // 60/factor, plus a small fixed cost for the round trip.
  return { seconds: 60 / model.speedFactor + 0.25, isMeasured: false };
}

/** Position of `value` in `[low, high]`, clamped, with an empty range at 0.5. */
function normalise(value: number, low: number, high: number): number {
  if (high === low) return 0.5;
  const t = (value - low) / (high - low);
  return Math.min(1, Math.max(0, t));
}

/**
 * Privacy is about where the audio goes, so it is a three-step ladder rather
 * than a spread: on-device never transmits, bring-your-own-key transmits to the
 * vendor on the reader's own account, and our cloud tier transmits through us.
 */
function privacyScore(model: Model): number {
  if (isDevice(model)) return 1;
  return model.byok ? 0.5 : 0.15;
}

/** Credits per audio minute. On-device models cost nothing to run. */
export function creditsPerMinute(model: Model): number {
  return isCloud(model) ? model.credits : 0;
}

export function meetsRequirements(
  model: Model,
  language: LanguageNeed,
  requirements: Requirements,
): boolean {
  const minimum = LANGUAGE_MINIMUMS[language];
  // A cloud vendor that publishes no language count is not excluded — an
  // unpublished number is not the same as a small one.
  if (model.languages !== null && model.languages < minimum) return false;

  if (isCloud(model)) {
    if (requirements.streaming && !model.streaming) return false;
    if (requirements.customVocabulary && !model.customVocabulary) return false;
    if (requirements.stableOnly && model.preview) return false;
    return true;
  }

  // On-device models transcribe as you speak and always accept a vocabulary
  // list, and none of them are preview builds.
  return true;
}

/**
 * Narrows a list of models to the ones a reader could actually use. The caller
 * supplies the list — `modelsForPlatform(platform)` in the page — so this stays
 * independent of the catalog.
 */
export function buildPool(
  models: readonly Model[],
  language: LanguageNeed,
  requirements: Requirements,
): readonly Model[] {
  return models.filter((model) =>
    meetsRequirements(model, language, requirements),
  );
}

/**
 * Ranks a pool, best first.
 *
 * A model with no published word error rate scores a neutral 0.5 on accuracy
 * rather than a guessed number — except on-device models, which fall back to
 * the app's own 1-5 accuracy rating. Without that fallback Whisper Tiny would
 * score the same as an unbenchmarked frontier model purely for being free and
 * private, and it would win the balanced preset outright.
 */
export function rankModels(
  pool: readonly Model[],
  input: ScoreInput,
): ScoredModel[] {
  if (pool.length === 0) return [];

  const timings = pool.map((model) => estimateSeconds(model, input.measured));

  const wers = pool
    .map((model) => model.wer)
    .filter((wer): wer is number => wer !== null);
  const werLow = wers.length ? Math.min(...wers) : 0;
  const werHigh = wers.length ? Math.max(...wers) : 1;

  const seconds = timings
    .map((timing) => timing.seconds)
    .filter((value): value is number => value !== null);
  const secondsLow = seconds.length ? Math.min(...seconds) : 0;
  const secondsHigh = seconds.length ? Math.max(...seconds) : 1;

  const costs = pool.map(creditsPerMinute);
  const costLow = Math.min(...costs);
  const costHigh = Math.max(...costs);

  const totalWeight =
    PRIORITIES.reduce((sum, key) => sum + input.weights[key], 0) || 1;

  const scored = pool.map((model, index) => {
    const timing = timings[index];

    const accuracy =
      model.wer !== null
        ? 1 - normalise(model.wer, werLow, werHigh)
        : isDevice(model)
          ? // 1-5 rating mapped into 0.1-0.9, so a rating of 1 still scores
            // clearly worse than a benchmarked mid-table model.
            (model.accuracyRating - 0.5) / 5
          : 0.5;

    const latency =
      timing.seconds === null
        ? 0.5
        : 1 - normalise(timing.seconds, secondsLow, secondsHigh);

    const cost = 1 - normalise(creditsPerMinute(model), costLow, costHigh);
    const privacy = privacyScore(model);

    const parts: Record<Priority, number> = { accuracy, latency, cost, privacy };
    const contributions = {} as Record<Priority, number>;
    let score = 0;
    for (const key of PRIORITIES) {
      const contribution = (parts[key] * input.weights[key]) / totalWeight;
      contributions[key] = contribution;
      score += contribution;
    }

    return {
      model,
      parts,
      contributions,
      score,
      estimatedSeconds: timing.seconds,
      latencyIsMeasured: timing.isMeasured,
    };
  });

  return scored.sort((a, b) => b.score - a.score);
}

/**
 * Moves one slider and takes the difference out of (or spreads it across) the
 * other three in proportion, so the four always total 100.
 *
 * When the other three are all at zero there is no proportion to preserve, so
 * the remainder is split evenly — otherwise the budget would silently stop
 * adding up to 100.
 */
export function rebalance(
  weights: Weights,
  changed: Priority,
  rawValue: number,
): Weights {
  const value = Math.min(TOTAL_POINTS, Math.max(0, rawValue));
  const others = PRIORITIES.filter((key) => key !== changed);
  const remaining = TOTAL_POINTS - value;
  const othersTotal = others.reduce((sum, key) => sum + weights[key], 0);

  const next = { ...weights, [changed]: value } as Weights;
  for (const key of others) {
    next[key] =
      othersTotal <= 0
        ? remaining / others.length
        : (weights[key] / othersTotal) * remaining;
  }
  return next;
}

export const PRIORITY_LABELS: Record<Priority, string> = {
  accuracy: "Accuracy",
  latency: "Speed",
  cost: "Cost",
  privacy: "Privacy",
};
