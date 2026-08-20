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
 * A database failure propagates, deliberately, and this function holds no
 * try/catch of its own.
 *
 * `getLatencyMatrix` already draws the only distinction there is to draw, and
 * documents it at length: it swallows a failure while `next build` prerenders,
 * so a deploy never depends on Postgres being reachable from the builder, and
 * it re-throws at runtime because Next counts a returned value — empty data
 * included — as a successful render. Build time is therefore already handled
 * upstream, which means a catch here could only ever fire in the window
 * /latency reserves for throwing.
 *
 * It used to. One Postgres hiccup during an hourly revalidation returned `{}`,
 * which is not "no measurements yet" but a good page overwritten by a worse one
 * and cached for an hour: no region control, no measured column, no geo fetch.
 * Throwing leaves the last good page in the ISR cache, which is both correct
 * and fresher than anything this function could invent, and Next retries on the
 * next request. /en/latency survives that blip; there is no reason this page
 * should be the one that does not.
 *
 * An empty TABLE is still not a failure: no rows means no cells, the page falls
 * back to the published speed factors, and the region control hides itself.
 */
export async function getMeasuredLatency(): Promise<MeasuredLatencyByRegion> {
  const byRegion: MeasuredLatencyByRegion = {};
  // Sample count behind each provider-level entry we have written so far, so a
  // busier model can replace a quieter one's number as the provider fallback.
  const providerSamples: Record<string, number> = {};

  const matrix = await getLatencyMatrix(DEFAULT_BUCKET);

  for (const vendor of matrix.vendors) {
    for (const model of vendor.models) {
      for (const cell of model.cells) {
        if (!usable(cell)) continue;

        const region = (byRegion[cell.region] ??= {});
        const providerKey = model.provider;
        const modelKey =
          model.model === null ? providerKey : `${providerKey}:${model.model}`;

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

  return byRegion;
}
