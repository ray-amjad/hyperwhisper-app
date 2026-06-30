import { describe, expect, test } from 'bun:test';
import { geminiMimeType, resolveAudioInputTokens, type GeminiUsageMetadata } from './gemini';

// Mirror the module constants used by the byte-estimate tier so the expected
// value tracks the implementation: BYTES_PER_MINUTE_ESTIMATE = 480_000 (lib/
// constants.ts) and AUDIO_TOKENS_PER_SECOND = 32 (gemini.ts).
const BYTES_PER_MINUTE_ESTIMATE = 480_000;
const AUDIO_TOKENS_PER_SECOND = 32;
function byteEstimateTokens(bytes: number): number {
  return Math.round(((bytes / BYTES_PER_MINUTE_ESTIMATE) * 60) * AUDIO_TOKENS_PER_SECOND);
}

describe('resolveAudioInputTokens (Gemini audio-token billing)', () => {
  const ANY_BYTES = 480_000; // 1 minute by the estimate → 1920 tokens

  // ── Tier 1: explicit AUDIO modality breakdown is authoritative ──
  test('tier 1: uses the AUDIO modality tokenCount when present', () => {
    const usage: GeminiUsageMetadata = {
      promptTokenCount: 2000,
      promptTokensDetails: [
        { modality: 'TEXT', tokenCount: 80 },
        { modality: 'AUDIO', tokenCount: 1920 },
      ],
    };
    expect(resolveAudioInputTokens(usage, ANY_BYTES)).toBe(1920);
  });

  test('tier 1: a zero AUDIO breakdown is honored (not treated as missing)', () => {
    const usage: GeminiUsageMetadata = {
      promptTokenCount: 80,
      promptTokensDetails: [{ modality: 'AUDIO', tokenCount: 0 }],
    };
    expect(resolveAudioInputTokens(usage, ANY_BYTES)).toBe(0);
  });

  // ── Tier 2: subtract non-AUDIO components, but only when plausible ──
  test('tier 2: backs audio out of promptTokenCount via the non-AUDIO components', () => {
    const usage: GeminiUsageMetadata = {
      promptTokenCount: 2000,
      // No AUDIO entry, but a TEXT entry is present → 2000 - 80 = 1920.
      promptTokensDetails: [{ modality: 'TEXT', tokenCount: 80 }],
    };
    expect(resolveAudioInputTokens(usage, ANY_BYTES)).toBe(1920);
  });

  test('tier 2 guard: a sparse breakdown that omits TEXT (remainder ≈ whole prompt) falls through to the promptTokenCount tier', () => {
    const usage: GeminiUsageMetadata = {
      promptTokenCount: 2000,
      // The only non-AUDIO entry is tiny, so remainder (1999) ≈ promptTokenCount;
      // the bogus breakdown is NOT trusted. We then back out our OWN text estimate
      // (80 tok) from the documented total → 1920 audio tokens.
      promptTokensDetails: [{ modality: 'TEXT', tokenCount: 1 }],
    };
    expect(resolveAudioInputTokens(usage, ANY_BYTES, 80)).toBe(1920);
  });

  // ── Tier 3: no trustworthy breakdown → promptTokenCount minus our text estimate ──
  test('tier 3: no promptTokensDetails → promptTokenCount minus the text estimate (documented total beats byte heuristic)', () => {
    const usage: GeminiUsageMetadata = { promptTokenCount: 5000 };
    const result = resolveAudioInputTokens(usage, ANY_BYTES, 80);
    expect(result).toBe(4920); // 5000 - 80, NOT the byte estimate
    expect(result).not.toBe(byteEstimateTokens(ANY_BYTES));
  });

  test('tier 3: low-bitrate audio — byte heuristic would under-bill, promptTokenCount is used', () => {
    // 1 min of 24 kbps Opus ≈ 180 KB, but Gemini bills it as ~1920 audio tokens.
    // The byte estimate (~720 tok) under-bills by ~60%; promptTokenCount fixes it.
    const lowBitrateBytes = 180_000;
    const usage: GeminiUsageMetadata = { promptTokenCount: 2000 };
    const result = resolveAudioInputTokens(usage, lowBitrateBytes, 80);
    expect(result).toBe(1920); // 2000 - 80
    expect(result).toBeGreaterThan(byteEstimateTokens(lowBitrateBytes));
  });

  // ── Tier 4: no usage total at all → byte-based estimate (last resort) ──
  test('tier 4: a tiny clip whose text estimate exceeds the total falls through to the byte estimate', () => {
    // promptTokenCount (50) minus a big vocab prompt estimate (80) nets ≤ 0 →
    // don't return ~0, fall through to the byte estimate instead.
    const usage: GeminiUsageMetadata = { promptTokenCount: 50 };
    expect(resolveAudioInputTokens(usage, ANY_BYTES, 80)).toBe(byteEstimateTokens(ANY_BYTES));
  });

  test('tier 4: undefined usage → byte estimate', () => {
    expect(resolveAudioInputTokens(undefined, ANY_BYTES)).toBe(byteEstimateTokens(ANY_BYTES));
  });

  test('tier 4: no promptTokenCount at all → byte estimate scales with payload size', () => {
    const small = resolveAudioInputTokens(undefined, 240_000); // ~30s → ~960 tok
    const large = resolveAudioInputTokens(undefined, 960_000); // ~120s → ~3840 tok
    expect(small).toBe(byteEstimateTokens(240_000));
    expect(large).toBe(byteEstimateTokens(960_000));
    expect(large).toBeGreaterThan(small);
  });
});

describe('geminiMimeType', () => {
  test('maps known extensions to Gemini audio MIME types', () => {
    expect(geminiMimeType('audio/wav')).toBe('audio/wav');
    expect(geminiMimeType('audio/mpeg')).toBe('audio/mp3');
    expect(geminiMimeType('audio/mp4')).toBe('audio/m4a');
    expect(geminiMimeType('audio/flac')).toBe('audio/flac');
  });

  test('forwards an unmapped audio/* type instead of mislabeling it as WAV', () => {
    // A browser "audio/webm" recording must NOT be relabeled audio/wav — that
    // would mis-decode. Forward the true type (params stripped).
    expect(geminiMimeType('audio/webm')).toBe('audio/webm');
    expect(geminiMimeType('audio/webm;codecs=opus')).toBe('audio/webm');
    expect(geminiMimeType('audio/opus')).toBe('audio/opus');
  });

  test('falls back to audio/wav only when there is no usable audio/* type', () => {
    expect(geminiMimeType('')).toBe('audio/wav');
    expect(geminiMimeType('application/octet-stream')).toBe('audio/wav');
  });
});
