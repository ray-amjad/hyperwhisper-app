// PROVIDER GEO-AVAILABILITY
//
// One question, one answer: can this Fly machine reach this provider from the
// region it is running in, and if not, what should the route do instead?
//
// This module exists so the transcribe route does not have to know FIVE
// ElevenLabs internals it previously carried inline:
//   1. that ElevenLabs is geo-blocked at all,
//   2. which Fly regions it is blocked from (`nrt`, `bom`, `maa`),
//   3. which region a blocked request has to be replayed to (`iad`),
//   4. that Fly only honours `fly-replay` for small bodies, so an oversized
//      upload cannot take that escape hatch,
//   5. that the fallback for an oversized upload is to drop the literal
//      provider id `'elevenlabs'` out of the chain.
//
// Adding a geo-blocked provider is a row in the table below, not a new `if` arm
// in the route. A provider with no entry is reachable from every region.
//
// This is a ROUTING gate, not an enforcement point. The block itself surfaces
// as a 200 OK carrying a text/html FAQ page rather than a JSON error, so the
// adapter cannot recognise it; avoiding the region is the only defence.

import { FLY_REPLAY_MAX_BODY_BYTES } from '../lib/constants';
import type { SttProviderId } from '../lib/stt-models';

interface GeoBlockDef {
  /** Fly regions that serve the provider's geo-block page instead of the API. */
  blockedRegions: ReadonlySet<string>;
  /** Fly region a blocked request is replayed to so the provider is reachable. */
  replayRegion: string;
  /** Stable label for the `reason` field on the route's log events. */
  reason: string;
}

const GEO_BLOCKS: Partial<Record<SttProviderId, GeoBlockDef>> = {
  // ElevenLabs blocks API access from Japan and India — the block surfaces as a
  // 200 OK with a text/html FAQ page ("Do you restrict access ... for any
  // specific countries?") instead of JSON. Verified per-region 2026-06-07.
  elevenlabs: {
    blockedRegions: new Set(['nrt', 'bom', 'maa']),
    replayRegion: 'iad',
    reason: 'elevenlabs_geo_block',
  },
};

/**
 * Read per call rather than once at module load: a Fly machine's region is
 * fixed, but the tests set `FLY_REGION` per case and must not need a fresh
 * import to be seen.
 */
function currentRegion(): string {
  return process.env.FLY_REGION || '';
}

function blockFor(provider: SttProviderId, region: string): GeoBlockDef | undefined {
  const def = GEO_BLOCKS[provider];
  return def && def.blockedRegions.has(region) ? def : undefined;
}

/**
 * What the route should do about `provider` before it spends any auth or credit
 * work on the request.
 *
 * - `proceed` — the provider is reachable from here; nothing to do.
 * - `replay` — hand the request back to Fly's edge for `toRegion`. Costs
 *   ~50-80 ms against ~6 s of certain failure.
 * - `drop_from_chain` — the body is too large for Fly to replay (Fly silently
 *   runs an oversized replay in the ORIGINAL region), so the request stays here
 *   and the route must run the rest of the chain without the blocked provider.
 */
export type GeoRoutingPlan =
  | { action: 'proceed' }
  | { action: 'replay'; fromRegion: string; toRegion: string; reason: string }
  | { action: 'drop_from_chain'; fromRegion: string; reason: string; replayMaxBytes: number };

export function planGeoRouting(provider: SttProviderId, contentLength: number): GeoRoutingPlan {
  const fromRegion = currentRegion();
  const block = blockFor(provider, fromRegion);
  if (!block) {
    return { action: 'proceed' };
  }

  if (contentLength <= FLY_REPLAY_MAX_BODY_BYTES) {
    return { action: 'replay', fromRegion, toRegion: block.replayRegion, reason: block.reason };
  }

  return {
    action: 'drop_from_chain',
    fromRegion,
    reason: block.reason,
    replayMaxBytes: FLY_REPLAY_MAX_BODY_BYTES,
  };
}

/**
 * The members of `chain` that this machine can actually reach. Ask this instead
 * of filtering a provider id out by hand: the caller then never names a
 * geo-blocked provider, and a second blocked provider needs no caller change.
 *
 * Returns a fresh array, like `fallbackChainFor()`.
 */
export function reachableFromRegion(chain: readonly SttProviderId[]): SttProviderId[] {
  const region = currentRegion();
  return chain.filter((provider) => !blockFor(provider, region));
}
