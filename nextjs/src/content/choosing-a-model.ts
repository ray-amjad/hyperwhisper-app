import "server-only";

import { getLatencyMatrix } from "@/src/content/latency";
import {
  DEFAULT_BUCKET,
  MIN_SAMPLES_PER_CELL,
  type LatencyCell,
} from "@/lib/latency/types";
import type { MeasuredLatency } from "@/lib/choosing-a-model/scoring";

/**
 * Median provider response time, per region, for the /choosing-a-model
 * calculator.
 *
 * Shape is `region -> providerKey -> milliseconds`, where `providerKey` is
 * either `sttProvider` or `sttProvider:modelId`. Both levels are emitted so the
 * calculator can prefer a model-level measurement and fall back to its
 * provider's when that model is too young to have its own.
 */
export type MeasuredLatencyByRegion = Record<string, MeasuredLatency>;

/**
 * A cell is only worth showing if enough calls went into it. This is the same
 * floor /latency prints its dashes at, so the two pages never disagree about
 * whether a number exists.
 */
function usable(cell: LatencyCell): boolean {
  return cell.samples >= MIN_SAMPLES_PER_CELL;
}

/**
 * Reads the short-clip bucket, because that is what dictation is: a few
 * seconds of speech, not a podcast. Ranking a model for a HyperWhisper user on
 * its five-minute-file timings would flatter whichever provider streams large
 * uploads best, which is not the job this page is doing.
 *
 * Never throws. The calculator degrades to the published speed factors when
 * this comes back empty, so a database blip costs the page its measured
 * timings, not its ranking.
 */
export async function getMeasuredLatency(): Promise<MeasuredLatencyByRegion> {
  const byRegion: MeasuredLatencyByRegion = {};
  // Sample count behind each provider-level entry we have written so far, so a
  // busier model can replace a quieter one's number as the provider fallback.
  const providerSamples: Record<string, number> = {};

  try {
    const matrix = await getLatencyMatrix(DEFAULT_BUCKET);

    for (const vendor of matrix.vendors) {
      for (const model of vendor.models) {
        for (const cell of model.cells) {
          if (!usable(cell)) continue;

          const region = (byRegion[cell.region] ??= {});
          const providerKey = model.provider;
          const modelKey =
            model.model === null
              ? providerKey
              : `${providerKey}:${model.model}`;

          region[modelKey] = cell.p50;

          // The provider-level fallback takes the timing of whichever of its
          // models ran most in this region — the one most people actually get.
          const seen = `${cell.region}/${providerKey}`;
          if (cell.samples > (providerSamples[seen] ?? 0)) {
            providerSamples[seen] = cell.samples;
            region[providerKey] = cell.p50;
          }
        }
      }
    }
  } catch (error) {
    console.error(
      "[choosing-a-model] could not read measured latency; " +
        "falling back to published speed factors:",
      error,
    );
    return {};
  }

  return byRegion;
}
