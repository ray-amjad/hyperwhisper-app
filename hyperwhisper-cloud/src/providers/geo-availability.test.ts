import { describe, expect, test, afterEach } from 'bun:test';
import { FLY_REPLAY_MAX_BODY_BYTES } from '../lib/constants';
import { planGeoRouting, reachableFromRegion } from './geo-availability';

const originalRegion = process.env.FLY_REGION;

function setRegion(region: string | undefined): void {
  if (region === undefined) {
    delete process.env.FLY_REGION;
  } else {
    process.env.FLY_REGION = region;
  }
}

afterEach(() => {
  setRegion(originalRegion);
});

describe('planGeoRouting', () => {
  test('proceeds when the region reaches the provider', () => {
    setRegion('iad');
    expect(planGeoRouting('elevenlabs', 1024)).toEqual({ action: 'proceed' });
  });

  test('proceeds off Fly, where FLY_REGION is unset', () => {
    setRegion(undefined);
    expect(planGeoRouting('elevenlabs', 1024)).toEqual({ action: 'proceed' });
  });

  test('proceeds for a provider with no geo block, even in a blocked region', () => {
    setRegion('nrt');
    expect(planGeoRouting('deepgram', 1024)).toEqual({ action: 'proceed' });
    expect(planGeoRouting('groq', 1024)).toEqual({ action: 'proceed' });
  });

  // nrt (JP), bom (IN) and maa (IN) all serve ElevenLabs's geo-block FAQ page.
  test.each(['nrt', 'bom', 'maa'])('replays a small ElevenLabs request out of %s', (region) => {
    setRegion(region);
    expect(planGeoRouting('elevenlabs', 1024)).toEqual({
      action: 'replay',
      fromRegion: region,
      toRegion: 'iad',
      reason: 'elevenlabs_geo_block',
    });
  });

  test('replays a request sitting exactly on the replay body cap', () => {
    setRegion('nrt');
    const plan = planGeoRouting('elevenlabs', FLY_REPLAY_MAX_BODY_BYTES);
    expect(plan.action).toBe('replay');
  });

  // Fly silently runs an oversized replay in the ORIGINAL region, so a body
  // over the cap must never get the header — it has to degrade instead.
  test('drops the provider from the chain when the body is over the replay cap', () => {
    setRegion('nrt');
    expect(planGeoRouting('elevenlabs', FLY_REPLAY_MAX_BODY_BYTES + 1)).toEqual({
      action: 'drop_from_chain',
      fromRegion: 'nrt',
      reason: 'elevenlabs_geo_block',
      replayMaxBytes: FLY_REPLAY_MAX_BODY_BYTES,
    });
  });

  // The region is read per call, not captured at import: a test (and a machine
  // reading a freshly-synced env) must see a change without a fresh import.
  test('reads the region per call', () => {
    setRegion('iad');
    expect(planGeoRouting('elevenlabs', 1024).action).toBe('proceed');
    setRegion('nrt');
    expect(planGeoRouting('elevenlabs', 1024).action).toBe('replay');
  });
});

describe('reachableFromRegion', () => {
  test('keeps every member in a region that blocks nothing', () => {
    setRegion('iad');
    expect(reachableFromRegion(['elevenlabs', 'deepgram', 'groq']))
      .toEqual(['elevenlabs', 'deepgram', 'groq']);
  });

  test('removes the blocked provider from any position in the chain', () => {
    setRegion('nrt');
    expect(reachableFromRegion(['elevenlabs', 'deepgram', 'groq'])).toEqual(['deepgram', 'groq']);
    expect(reachableFromRegion(['deepgram', 'groq', 'elevenlabs'])).toEqual(['deepgram', 'groq']);
  });

  test('returns a fresh array, so a caller cannot edit the chain it was given', () => {
    setRegion('iad');
    const chain = ['deepgram', 'groq'] as const;
    const filtered = reachableFromRegion(chain);
    expect(filtered).not.toBe(chain);
    filtered.push('elevenlabs');
    expect(chain).toEqual(['deepgram', 'groq']);
  });
});
