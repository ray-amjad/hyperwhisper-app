import { describe, expect, test, afterEach } from 'bun:test';
import { preBufferMaxBytes } from './audio-limits';
import {
  GEMINI_INLINE_MAX_BYTES,
  GEMINI_TRANSCRIBE_INLINE_MAX_BYTES,
  GOOGLE_CHIRP_INLINE_MAX_BYTES,
  META_MUSE_MAX_BYTES,
  OPENAI_INLINE_MAX_BYTES,
} from '../lib/constants';
import { ALL_STT_PROVIDER_IDS, type SttProviderId } from '../lib/stt-models';

const originalBucket = process.env.GOOGLE_SPEECH_GCS_BUCKET;

function setBucket(value: string | undefined) {
  if (value === undefined) {
    delete process.env.GOOGLE_SPEECH_GCS_BUCKET;
  } else {
    process.env.GOOGLE_SPEECH_GCS_BUCKET = value;
  }
}

afterEach(() => setBucket(originalBucket));

describe('preBufferMaxBytes', () => {
  test('returns the inline cap for the providers that have one', () => {
    setBucket(undefined);

    expect(preBufferMaxBytes('gemini')).toBe(GEMINI_INLINE_MAX_BYTES);
    expect(preBufferMaxBytes('gemini-transcribe')).toBe(GEMINI_TRANSCRIBE_INLINE_MAX_BYTES);
    expect(preBufferMaxBytes('openai')).toBe(OPENAI_INLINE_MAX_BYTES);
    expect(preBufferMaxBytes('google-chirp')).toBe(GOOGLE_CHIRP_INLINE_MAX_BYTES);
    expect(preBufferMaxBytes('meta')).toBe(META_MUSE_MAX_BYTES);
  });

  test('returns null for every provider without a cap of its own', () => {
    setBucket(undefined);
    const capped = new Set<SttProviderId>(['gemini', 'gemini-transcribe', 'openai', 'google-chirp', 'meta']);

    // Drives off the registry, so a provider added there is covered here
    // without anyone remembering to extend this list.
    for (const provider of ALL_STT_PROVIDER_IDS.filter((p) => !capped.has(p))) {
      expect(preBufferMaxBytes(provider)).toBeNull();
    }
  });

  test('lifts the Chirp cap when a GCS scratch bucket is configured', () => {
    // With a bucket the provider uploads and runs batchRecognize, which has no
    // inline cap — so a 50 MB request is legitimate and must not be 413'd.
    setBucket('hyperwhisper-stt-scratch');

    expect(preBufferMaxBytes('google-chirp')).toBeNull();
  });

  test('treats a blank or whitespace-only bucket as not configured', () => {
    // Matches isGcsTranscriptionBucketConfigured(): an env var that was set but
    // left empty must fail closed to the inline cap, not lift it.
    for (const blank of ['', '   ']) {
      setBucket(blank);
      expect(preBufferMaxBytes('google-chirp')).toBe(GOOGLE_CHIRP_INLINE_MAX_BYTES);
    }
  });

  test('re-reads the bucket per call rather than caching it at import', () => {
    // A Fly machine picks up a newly-synced secret without a restart, and the
    // route calls this on every request.
    setBucket(undefined);
    expect(preBufferMaxBytes('google-chirp')).toBe(GOOGLE_CHIRP_INLINE_MAX_BYTES);

    setBucket('hyperwhisper-stt-scratch');
    expect(preBufferMaxBytes('google-chirp')).toBeNull();
  });

  test('the caps it reports are the ones the adapters enforce', () => {
    // The gate is a fast fail, not a second source of truth. If a cap here ever
    // drifted above an adapter's own AudioTooLargeError threshold, the route
    // would admit a payload the provider is guaranteed to reject.
    expect(preBufferMaxBytes('gemini')).toBe(GEMINI_INLINE_MAX_BYTES);
    expect(preBufferMaxBytes('gemini-transcribe')).toBe(GEMINI_TRANSCRIBE_INLINE_MAX_BYTES);
    expect(preBufferMaxBytes('openai')).toBe(OPENAI_INLINE_MAX_BYTES);
    expect(preBufferMaxBytes('meta')).toBe(META_MUSE_MAX_BYTES);
  });
});
