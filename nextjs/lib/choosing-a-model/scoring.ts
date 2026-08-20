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
import type {
  CloudModel,
  DeviceModel,
  LanguageScope,
  Model,
  Platform,
} from "./catalog";

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

/** Which languages the reader needs. */
export type LanguageNeed = "english" | "european" | "wide";

/**
 * Which coverage rungs answer each need.
 *
 * Was a minimum language count, which is not the same question and got both
 * ends of it wrong — it dropped a six-language European model from "European"
 * while keeping a thirteen-language global one, and let every model with no
 * published count through "Wide multilingual" as though silence were breadth.
 * See `LanguageScope` in the catalog for how a rung is arrived at.
 *
 * "English only" admits everything, including `unknown`: every model in the
 * catalog transcribes English, and a vendor declining to publish a count casts
 * no doubt on that. The two breadth needs are where an unpublished count has to
 * be read conservatively, so `unknown` is absent from both.
 */
const LANGUAGE_NEED_SCOPES: Record<LanguageNeed, readonly LanguageScope[]> = {
  english: ["narrow", "european", "wide", "unknown"],
  european: ["european", "wide"],
  wide: ["wide"],
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
 * Median milliseconds a provider took to answer ONE short dictation clip —
 * under 10 seconds of audio — from the measurements behind /latency. Keyed
 * `sttProvider` or `sttProvider:modelId`; a model-level entry wins when we have
 * one.
 *
 * This is a wall-clock round trip for one clip, NOT a throughput figure. See
 * `measuredClipMs` for why the two can never be added up or compared.
 */
export type MeasuredLatency = Record<string, number>;

export type ScoreInput = {
  weights: Weights;
  /** Measured round trips for the reader's region. Display only. */
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
   * Estimated seconds to transcribe one minute of audio, and what the latency
   * sub-score ranks on. Null when a cloud model publishes no speed factor.
   */
  estimatedSeconds: number | null;
  /**
   * Our own median round trip for one short clip, in milliseconds, or null
   * where we have not measured this model from the reader's region. A
   * separate quantity from `estimatedSeconds` — shown beside it, never mixed
   * into it.
   */
  measuredClipMs: number | null;
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
 * Seconds to transcribe one audio minute. One unit, one source per placement,
 * for every model in the pool.
 *
 * Deliberately does NOT consult our own measurements, even though we have them
 * for some models. What /latency stores is `latency_ms`: the wall time of one
 * attempt on one clip, from the `short` bucket — clips under 10 seconds
 * (`lib/latency/types.ts`). That is a round trip, not a throughput, and it
 * cannot be converted into a per-minute figure: `audio_seconds` is written null
 * on purpose (`src/db/schema/stt-latency-samples.ts`), so the clip it belongs to
 * has no length beyond "under 10 s", and a short clip's time is dominated by
 * fixed round-trip cost rather than by how fast the model decodes.
 *
 * Substituting it for a per-minute number therefore punishes precisely the
 * providers we know most about. Deepgram Nova 3 models out at 0.36 s per audio
 * minute; a perfectly healthy measured median of 900 ms for a 4-second clip
 * would print 0.90 s and drop it six places on the Fastest preset, behind
 * models that lead only because nobody has measured them. Ranking a pool on two
 * incommensurable units is worse than ranking it on one imperfect one, so the
 * measurement is surfaced as its own figure instead — see `measuredClipMs`.
 */
export function estimateSeconds(model: Model): number | null {
  if (isDevice(model)) {
    return deviceSeconds(model.speedRating);
  }
  if (model.speedFactor === null) {
    return null;
  }
  // Speed factor is audio seconds per wall second, so one audio minute takes
  // 60/factor, plus a small fixed cost for the round trip.
  return 60 / model.speedFactor + 0.25;
}

/**
 * Our own median wall time, in milliseconds, for a single short dictation clip
 * — the thing the reader actually waits through — from the reader's nearest
 * region over the last 90 days.
 *
 * Real, first-party, and worth showing: it is our traffic, on our edge,
 * including whatever the provider does under load. It is just not a per-minute
 * throughput, so the UI labels it as what it is and the ranking leaves it
 * alone. On-device models never have one: nothing about them crosses a network,
 * and /latency only measures what the edge service calls.
 */
export function measuredClipMs(
  model: Model,
  measured: MeasuredLatency,
): number | null {
  if (isDevice(model)) return null;
  const modelKey = `${model.sttProvider}:${model.modelId}`;
  return measured[modelKey] ?? measured[model.sttProvider] ?? null;
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

/**
 * Whether the reader's app can transcribe live with this model.
 *
 * Cloud support is a property of the endpoint and the same everywhere. Local
 * support is a property of the app in front of it, and the two apps differ: see
 * `DeviceModel.streamingPlatforms`.
 */
export function supportsStreaming(model: Model, platform: Platform): boolean {
  return isCloud(model)
    ? model.streaming
    : model.streamingPlatforms.includes(platform);
}

/** Whether the reader's app really applies a vocabulary list to this model. */
export function supportsCustomVocabulary(
  model: Model,
  platform: Platform,
): boolean {
  return isCloud(model)
    ? model.customVocabulary
    : model.customVocabularyPlatforms.includes(platform);
}

/**
 * Whether a model survives the reader's must-haves.
 *
 * Takes the platform because two of the three requirements are answered by the
 * app, not by the model. The version this replaces returned `true` for every
 * on-device model on the grounds that local models "transcribe as you speak and
 * always accept a vocabulary list" — neither half of which is true. Windows has
 * no local streaming provider at all, macOS has two, and every Parakeet,
 * Nemotron and Qwen3 build ignores a vocabulary list on both. The effect was
 * three chips that changed the pool by zero models between them.
 */
export function meetsRequirements(
  model: Model,
  platform: Platform,
  language: LanguageNeed,
  requirements: Requirements,
): boolean {
  if (!LANGUAGE_NEED_SCOPES[language].includes(model.languageScope)) {
    return false;
  }
  if (requirements.streaming && !supportsStreaming(model, platform)) {
    return false;
  }
  if (
    requirements.customVocabulary &&
    !supportsCustomVocabulary(model, platform)
  ) {
    return false;
  }
  // On-device models ship from the app's own registry rather than a vendor
  // preview channel, so there is no preview build to exclude.
  if (requirements.stableOnly && isCloud(model) && model.preview) {
    return false;
  }
  return true;
}

/**
 * Narrows a list of models to the ones a reader could actually use. The caller
 * supplies the list — `modelsForPlatform(platform)` in the page — so this stays
 * independent of the catalog, and passes the platform alongside it because the
 * requirements are answered per platform.
 */
export function buildPool(
  models: readonly Model[],
  platform: Platform,
  language: LanguageNeed,
  requirements: Requirements,
): readonly Model[] {
  return models.filter((model) =>
    meetsRequirements(model, platform, language, requirements),
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

  const timings = pool.map(estimateSeconds);

  const wers = pool
    .map((model) => model.wer)
    .filter((wer): wer is number => wer !== null);
  const werLow = wers.length ? Math.min(...wers) : 0;
  const werHigh = wers.length ? Math.max(...wers) : 1;

  const seconds = timings.filter((value): value is number => value !== null);
  const secondsLow = seconds.length ? Math.min(...seconds) : 0;
  const secondsHigh = seconds.length ? Math.max(...seconds) : 1;

  const costs = pool.map(creditsPerMinute);
  const costLow = Math.min(...costs);
  const costHigh = Math.max(...costs);

  const totalWeight =
    PRIORITIES.reduce((sum, key) => sum + input.weights[key], 0) || 1;

  const scored = pool.map((model, index) => {
    const estimatedSeconds = timings[index];

    const accuracy =
      model.wer !== null
        ? 1 - normalise(model.wer, werLow, werHigh)
        : isDevice(model)
          ? // 1-5 rating mapped into 0.1-0.9, so a rating of 1 still scores
            // clearly worse than a benchmarked mid-table model.
            (model.accuracyRating - 0.5) / 5
          : 0.5;

    const latency =
      estimatedSeconds === null
        ? 0.5
        : 1 - normalise(estimatedSeconds, secondsLow, secondsHigh);

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
      estimatedSeconds,
      measuredClipMs: measuredClipMs(model, input.measured),
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
