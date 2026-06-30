import { describe, expect, test } from 'bun:test';
import {
  computeAssemblyAITranscriptionCost,
  computeElevenLabsTranscriptionCost,
  computeGeminiTranscriptionCost,
  computeGroqTranscriptionCost,
  computeMistralTranscriptionCost,
  computeOpenAITranscriptionCost,
  computeSonioxTranscriptionCost,
  estimatePromptInputReservationUsd,
} from './cost-calculator';

describe('new STT provider cost functions', () => {
  test('Mistral Voxtral bills $0.003/min', () => {
    expect(computeMistralTranscriptionCost(120)).toBeCloseTo(0.006, 6);
  });

  test('Soniox bills the blended ~$0.10/hr rate (audio + output, no context)', () => {
    expect(computeSonioxTranscriptionCost(3600)).toBeCloseTo(0.10, 6);
  });

  test('Soniox adds the custom-context input-text token cost on top of the audio blend', () => {
    const base = computeSonioxTranscriptionCost(3600);
    // 1000 context tokens @ $3.50/1M async = +$0.0035.
    const withContext = computeSonioxTranscriptionCost(3600, 1000);
    expect(withContext).toBeCloseTo(base + 1000 * (3.50 / 1e6), 6);
    expect(withContext).toBeGreaterThan(base);
  });

  test('ElevenLabs keyterm prompting adds a +20% surcharge on base', () => {
    const base = computeElevenLabsTranscriptionCost(60);
    const withKeyterms = computeElevenLabsTranscriptionCost(60, true);
    expect(base).toBeCloseTo(0.00983, 6);
    expect(withKeyterms).toBeCloseTo(0.00983 * 1.2, 6);
  });

  test('AssemblyAI medical add-on stacks on the base model', () => {
    const base = computeAssemblyAITranscriptionCost(60, 'universal-3-pro', false);
    const medical = computeAssemblyAITranscriptionCost(60, 'universal-3-pro', true);
    expect(base).toBeCloseTo(0.0035, 6);
    expect(medical).toBeCloseTo(0.0060, 6); // 0.0035 + 0.0025 add-on
  });

  test('AssemblyAI keyterms add-on charges for universal-3-pro but is free on universal-2', () => {
    // universal-3-pro: keyterms layers the ~$0.05/hr prompt add-on on top of base.
    const proBase = computeAssemblyAITranscriptionCost(60, 'universal-3-pro', false, false);
    const proKeyterms = computeAssemblyAITranscriptionCost(60, 'universal-3-pro', false, true);
    expect(proKeyterms).toBeGreaterThan(proBase);
    // 60s @ $0.05/hr = $0.05/60 ≈ $0.000833 add-on.
    expect(proKeyterms - proBase).toBeCloseTo(0.05 / 60, 6);

    // universal-2: keyterms are free/beta — must NOT be charged.
    const u2Base = computeAssemblyAITranscriptionCost(60, 'universal-2', false, false);
    const u2Keyterms = computeAssemblyAITranscriptionCost(60, 'universal-2', false, true);
    expect(u2Keyterms).toBe(u2Base);
  });

  test('OpenAI whisper-1 is duration-billed; gpt-4o is token-billed', () => {
    expect(computeOpenAITranscriptionCost('whisper-1', { durationSeconds: 60 })).toBeCloseTo(0.006, 6);

    const gpt4o = computeOpenAITranscriptionCost('gpt-4o-transcribe', {
      durationSeconds: 60, inputTokens: 1_000_000, outputTokens: 0,
    });
    expect(gpt4o).toBeCloseTo(2.50, 6); // 1M input tokens @ $2.50/1M
  });

  test('OpenAI gpt-4o fails closed to a per-minute floor when usage is missing', () => {
    // No token counts → must NOT bill $0; falls back to duration estimate.
    const floored = computeOpenAITranscriptionCost('gpt-4o-transcribe', { durationSeconds: 60 });
    expect(floored).toBeCloseTo(0.006, 6);
    const miniFloored = computeOpenAITranscriptionCost('gpt-4o-mini-transcribe', { durationSeconds: 60 });
    expect(miniFloored).toBeCloseTo(0.003, 6);
  });

  test('Gemini bills from audio+output tokens, fails closed when usage absent', () => {
    // 1 minute of audio = 1920 audio tokens; 2.5-flash audio @ $1.00/1M.
    const exact = computeGeminiTranscriptionCost('gemini-2.5-flash', {
      audioInputTokens: 1920, outputTokens: 0,
    });
    expect(exact).toBeCloseTo(0.00192, 6);

    // Missing usage → fall back to duration estimate (never $0).
    const floored = computeGeminiTranscriptionCost('gemini-2.5-flash', {
      audioInputTokens: 0, outputTokens: 0, fallbackDurationSeconds: 60,
    });
    expect(floored).toBeCloseTo(0.00192, 6);
  });

  test('Gemini bills text-input tokens at the text rate (not $0, not the audio rate)', () => {
    // 2.5-flash: audio $1.00/1M, text $0.30/1M. A vocab-heavy prompt's text
    // tokens must be charged — at the cheaper text rate, not dropped and not
    // billed as audio.
    const audioOnly = computeGeminiTranscriptionCost('gemini-2.5-flash', {
      audioInputTokens: 1920, textInputTokens: 0, outputTokens: 0,
    });
    const withText = computeGeminiTranscriptionCost('gemini-2.5-flash', {
      audioInputTokens: 1920, textInputTokens: 1000, outputTokens: 0,
    });
    expect(audioOnly).toBeCloseTo(0.00192, 6);
    // +1000 text tokens @ $0.30/1M = +0.0003, NOT +0.001 (audio rate).
    expect(withText).toBeCloseTo(0.00192 + 0.0003, 6);
  });

  test('Gemini Pro applies the >200k long-context tier (input + output rates rise)', () => {
    // 2.5-pro: <=200k input $1.25/1M, output $10/1M; >200k input $2.50/1M, output $15/1M.
    // Prompt = audio + text; cross the 200k boundary and the whole bill switches tier.
    const under = computeGeminiTranscriptionCost('gemini-2.5-pro', {
      audioInputTokens: 100_000, textInputTokens: 0, outputTokens: 1000,
    });
    expect(under).toBeCloseTo(100_000 * (1.25 / 1e6) + 1000 * (10 / 1e6), 8);

    const over = computeGeminiTranscriptionCost('gemini-2.5-pro', {
      audioInputTokens: 250_000, textInputTokens: 0, outputTokens: 1000,
    });
    expect(over).toBeCloseTo(250_000 * (2.50 / 1e6) + 1000 * (15 / 1e6), 8);
    // Per-token cost is strictly higher above the threshold.
    expect(over / 251_000).toBeGreaterThan(under / 101_000);
  });

  test('Gemini flat (non-Pro) models have no long-context tier', () => {
    // 2.5-flash stays at $1.00/1M audio even past 200k tokens (1M context, flat).
    const big = computeGeminiTranscriptionCost('gemini-2.5-flash', {
      audioInputTokens: 300_000, textInputTokens: 0, outputTokens: 0,
    });
    expect(big).toBeCloseTo(300_000 * (1.00 / 1e6), 8);
  });

  test('estimatePromptInputReservationUsd charges token-billed providers and not others', () => {
    const prompt = 'a'.repeat(400); // ~100 tokens at 4 chars/token
    // Gemini: charged at the model's text-input rate (2.5-flash $0.30/1M).
    expect(estimatePromptInputReservationUsd('gemini', 'gemini-2.5-flash', prompt)).toBeCloseTo(100 * (0.30 / 1e6), 9);
    // OpenAI gpt-4o-transcribe: $2.50/1M input. mini: $1.25/1M.
    expect(estimatePromptInputReservationUsd('openai', 'gpt-4o-transcribe', prompt)).toBeCloseTo(100 * (2.50 / 1e6), 9);
    expect(estimatePromptInputReservationUsd('openai', 'gpt-4o-mini-transcribe', prompt)).toBeCloseTo(100 * (1.25 / 1e6), 9);
    // whisper-1 is duration-billed → no prompt-token charge.
    expect(estimatePromptInputReservationUsd('openai', 'whisper-1', prompt)).toBe(0);
    // Soniox charges custom-context as async input-text tokens (~0.3 tok/char @ $3.50/1M).
    expect(estimatePromptInputReservationUsd('soniox', 'stt-async-v4', prompt))
      .toBeCloseTo(Math.ceil(prompt.length * 0.3) * (3.50 / 1e6), 9);
    // Duration-billed providers and absent prompts → 0.
    expect(estimatePromptInputReservationUsd('deepgram', 'nova-3-general', prompt)).toBe(0);
    expect(estimatePromptInputReservationUsd('mistral', undefined, prompt)).toBe(0);
    expect(estimatePromptInputReservationUsd('gemini', 'gemini-2.5-flash', undefined)).toBe(0);
  });

  test('Gemini bills output tokens (which include upstream thinking tokens)', () => {
    // The adapter sums candidatesTokenCount + thoughtsTokenCount into
    // outputTokens, so the cost fn must charge for output. With output tokens
    // present the bill is strictly higher than audio-input alone.
    const withOutput = computeGeminiTranscriptionCost('gemini-2.5-pro', {
      audioInputTokens: 1920, outputTokens: 1000,
    });
    const withoutOutput = computeGeminiTranscriptionCost('gemini-2.5-pro', {
      audioInputTokens: 1920, outputTokens: 0,
    });
    expect(withOutput).toBeGreaterThan(withoutOutput);
  });

  test('Groq turbo is billed at $0.04/hr, large-v3 at $0.111/hr', () => {
    // 1 hour of audio — exact rate check.
    expect(computeGroqTranscriptionCost(3600, 'whisper-large-v3-turbo')).toBeCloseTo(0.04, 6);
    expect(computeGroqTranscriptionCost(3600, 'whisper-large-v3')).toBeCloseTo(0.111, 6);
    // turbo is ~2.8x cheaper than large-v3.
    expect(computeGroqTranscriptionCost(3600, 'whisper-large-v3-turbo'))
      .toBeLessThan(computeGroqTranscriptionCost(3600, 'whisper-large-v3'));
  });

  test('Groq with no model defaults to the turbo rate', () => {
    // Omitting the model should use the turbo rate (provider default = whisper-large-v3-turbo).
    expect(computeGroqTranscriptionCost(3600)).toBeCloseTo(0.04, 6);
    expect(computeGroqTranscriptionCost(3600, undefined)).toBeCloseTo(0.04, 6);
  });

  test('Groq enforces the 10-second minimum billable floor', () => {
    // A 5-second clip is billed as if it were 10 seconds.
    const floor = computeGroqTranscriptionCost(10, 'whisper-large-v3-turbo');
    const shorter = computeGroqTranscriptionCost(5, 'whisper-large-v3-turbo');
    expect(shorter).toBe(floor);
  });
});
