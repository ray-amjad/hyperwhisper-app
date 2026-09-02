import { describe, expect, test } from 'bun:test';
import {
  ASSEMBLYAI_SYNC_COST_PER_AUDIO_MINUTE,
  GEMINI_TRANSCRIBE_AUDIO_TOKENS_PER_SECOND,
  computeAnthropicCost,
  computeAssemblyAISyncTranscriptionCost,
  computeAssemblyAITranscriptionCost,
  computeAzureMaiTranscriptionCost,
  computeCerebrasChatCost,
  computeDeepgramTranscriptionCost,
  computeElevenLabsTranscriptionCost,
  computeGeminiChatCost,
  computeGeminiTranscribeCost,
  computeGeminiTranscribeLiveCost,
  computeGeminiTranscriptionCost,
  computeGoogleChirpTranscriptionCost,
  computeGroqChatCost,
  computeGroqTranscriptionCost,
  computeMistralChatCost,
  computeMistralTranscriptionCost,
  computeMetaMuseTranscriptionCost,
  computeOpenAIChatCost,
  computeOpenAITranscriptionCost,
  computeSonioxTranscriptionCost,
  computeXaiGrokFastChatCost,
  computeXaiTranscriptionCost,
  countCjkChars,
  creditsForCost,
  estimateGeminiTranscribeOutputTokens,
  estimatePromptInputReservationUsd,
  estimateSonioxContextTokens,
  estimateUsageFromChars,
  formatUsd,
  isGroqUsage,
  roundUsd,
  usdForCredits,
  usdToCredits,
  type GroqUsage,
} from './cost-calculator';

describe('new STT provider cost functions', () => {
  test('Mistral Voxtral bills $0.003/min', () => {
    expect(computeMistralTranscriptionCost(120)).toBeCloseTo(0.006, 6);
  });

  test('Meta Muse bills $0.003/min', () => {
    expect(computeMetaMuseTranscriptionCost(60)).toBeCloseTo(0.003, 6);
    expect(computeMetaMuseTranscriptionCost(120)).toBeCloseTo(0.006, 6);
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
    const base = computeAssemblyAITranscriptionCost(60, 'universal-3-5-pro', false);
    const medical = computeAssemblyAITranscriptionCost(60, 'universal-3-5-pro', true);
    expect(base).toBeCloseTo(0.0035, 6);
    expect(medical).toBeCloseTo(0.0060, 6); // 0.0035 + 0.0025 add-on
  });

  test('AssemblyAI keyterms add-on charges for universal-3-5-pro but is free on universal-2', () => {
    // universal-3-5-pro: keyterms layers the ~$0.05/hr prompt add-on on top of base.
    const proBase = computeAssemblyAITranscriptionCost(60, 'universal-3-5-pro', false, false);
    const proKeyterms = computeAssemblyAITranscriptionCost(60, 'universal-3-5-pro', false, true);
    expect(proKeyterms).toBeGreaterThan(proBase);
    // 60s @ $0.05/hr = $0.05/60 ≈ $0.000833 add-on.
    expect(proKeyterms - proBase).toBeCloseTo(0.05 / 60, 6);

    // universal-2: keyterms are free/beta — must NOT be charged.
    const u2Base = computeAssemblyAITranscriptionCost(60, 'universal-2', false, false);
    const u2Keyterms = computeAssemblyAITranscriptionCost(60, 'universal-2', false, true);
    expect(u2Keyterms).toBe(u2Base);
  });

  test('AssemblyAI universal-3-pro compatibility id bills at the same Pro-tier rate as universal-3-5-pro', () => {
    // Defensive compatibility for callers that reach the calculator before
    // the registry canonicalizes the retired id.
    const legacy = computeAssemblyAITranscriptionCost(60, 'universal-3-pro', false, false);
    const current = computeAssemblyAITranscriptionCost(60, 'universal-3-5-pro', false, false);
    expect(legacy).toBeCloseTo(0.0035, 6);
    expect(legacy).toBe(current);
  });

  test('OpenAI whisper-1 is duration-billed; gpt-4o is token-billed', () => {
    expect(computeOpenAITranscriptionCost('whisper-1', { durationSeconds: 60 })).toBeCloseTo(0.006, 6);

    const gpt4o = computeOpenAITranscriptionCost('gpt-4o-transcribe', {
      durationSeconds: 60, inputTokens: 1_000_000, outputTokens: 0,
    });
    expect(gpt4o).toBeCloseTo(2.50, 6); // 1M input tokens @ $2.50/1M
  });

  test('OpenAI gpt-transcribe / gpt-live-transcribe are flat per-minute billed, not token-billed', () => {
    // $0.0045/min and $0.017/min respectively — verified against OpenAI's
    // pricing docs. A large (would-be-expensive-if-token-billed) input/output
    // token count must NOT affect the bill for these two models.
    const gptTranscribe = computeOpenAITranscriptionCost('gpt-transcribe', {
      durationSeconds: 60, inputTokens: 1_000_000, outputTokens: 1_000_000,
    });
    expect(gptTranscribe).toBeCloseTo(0.0045, 6);

    const gptLiveTranscribe = computeOpenAITranscriptionCost('gpt-live-transcribe', {
      durationSeconds: 60,
    });
    expect(gptLiveTranscribe).toBeCloseTo(0.017, 6);
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
    // whisper-1, gpt-transcribe, gpt-live-transcribe are duration-billed → no prompt-token charge.
    expect(estimatePromptInputReservationUsd('openai', 'whisper-1', prompt)).toBe(0);
    expect(estimatePromptInputReservationUsd('openai', 'gpt-transcribe', prompt)).toBe(0);
    expect(estimatePromptInputReservationUsd('openai', 'gpt-live-transcribe', prompt)).toBe(0);
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

  // ── Gemini 3.5 Transcribe (the dedicated speech models) ───────────────────
  // These numbers come from live calls to /v1beta/interactions with the sample
  // clip: 236 audio tokens + 1 text token for 9.456 s of speech, and
  // total_output_tokens: 0 on every response.

  test('Gemini 3.5 Transcribe bills audio at 25 tok/s — NOT the LLM path 32 tok/s', () => {
    // A minute of audio is 1,500 tokens here, 1,920 on :generateContent. Billing
    // this model with the LLM constant would over-charge by 28%.
    expect(GEMINI_TRANSCRIBE_AUDIO_TOKENS_PER_SECOND).toBe(25);
    const oneMinuteAudioOnly = computeGeminiTranscribeCost('gemini-3.5-transcribe', {
      audioInputTokens: 60 * GEMINI_TRANSCRIBE_AUDIO_TOKENS_PER_SECOND,
      outputTokens: 0,
    });
    expect(oneMinuteAudioOnly).toBeCloseTo(1500 * (2.00 / 1e6), 9); // $0.0030
  });

  test('the measured live response bills to ~5.3 credits/min, matching the catalog 5.5', () => {
    // Exactly what the API returned for the 9.456 s sample: 236 audio + 1 text
    // input tokens, no output tokens reported, 119-character transcript.
    const transcript = 'Hello, this is a test of HyperWhisper transcription. Let us meet on Tuesday, no, Wednesday, um, at the Kalamazoo office.';
    const outputTokens = estimateGeminiTranscribeOutputTokens(transcript);
    const costUsd = computeGeminiTranscribeCost('gemini-3.5-transcribe', {
      audioInputTokens: 236, textInputTokens: 1, outputTokens,
    });

    const perMinute = costUsd * (60 / (236 / GEMINI_TRANSCRIBE_AUDIO_TOKENS_PER_SECOND));
    // cloud-stt-catalog.json's cloudTier.creditsPerMinute is 5.5 (USD/min ×
    // 1000); shared-app-classification/AGENTS.md allows ≤10% drift from what
    // this file actually bills.
    expect(perMinute * 1000).toBeGreaterThan(5.5 * 0.9);
    expect(perMinute * 1000).toBeLessThan(5.5 * 1.1);
  });

  test('output tokens are estimated from the transcript, because the API reports 0', () => {
    // ~4 chars/token, the same heuristic as the reservation/fallback estimates.
    expect(estimateGeminiTranscribeOutputTokens('')).toBe(0);
    expect(estimateGeminiTranscribeOutputTokens('a'.repeat(400))).toBe(100);

    // Billing audio alone (what reading total_output_tokens would give) is far
    // cheaper than the truth — the estimate is what stops the under-charge.
    const audioOnly = computeGeminiTranscribeCost('gemini-3.5-transcribe', {
      audioInputTokens: 1500, outputTokens: 0,
    });
    const withOutput = computeGeminiTranscribeCost('gemini-3.5-transcribe', {
      audioInputTokens: 1500, outputTokens: 188,
    });
    expect(withOutput).toBeGreaterThan(audioOnly);
    expect(withOutput - audioOnly).toBeCloseTo(188 * (12.00 / 1e6), 9);
  });

  test('text-input tokens bill at the same rate as audio on this endpoint', () => {
    const withText = computeGeminiTranscribeCost('gemini-3.5-transcribe', {
      audioInputTokens: 1000, textInputTokens: 500, outputTokens: 0,
    });
    expect(withText).toBeCloseTo(1500 * (2.00 / 1e6), 9);
  });

  test('a missing usage object falls back to a duration estimate rather than $0', () => {
    const failClosed = computeGeminiTranscribeCost('gemini-3.5-transcribe', {
      audioInputTokens: 0, outputTokens: 0, fallbackDurationSeconds: 60,
    });
    expect(failClosed).toBeGreaterThan(0);
    // Same ~$0.0053/min the token path produces for a minute of speech.
    expect(failClosed).toBeCloseTo(0.0053, 4);
  });

  test('a PARTIAL usage object falls back too — a missing AUDIO count, not a zero total', () => {
    // The shape the production caller actually produces: output tokens are
    // ESTIMATED from the transcript, so they are >= 1 on every real response and
    // the total is never zero. Keying the fallback on the total made it dead
    // code and billed a minute of speech at 24x under.
    const estimatedOutputOnly = computeGeminiTranscribeCost('gemini-3.5-transcribe', {
      audioInputTokens: 0, textInputTokens: 1, outputTokens: 188, fallbackDurationSeconds: 60,
    });
    expect(estimatedOutputOnly).toBeCloseTo(0.0053, 4);

    // The fallback is a FLOOR, never a discount: a usage object that reports
    // more than the estimate keeps its own figure.
    const talkative = computeGeminiTranscribeCost('gemini-3.5-transcribe', {
      audioInputTokens: 0, outputTokens: 5000, fallbackDurationSeconds: 60,
    });
    expect(talkative).toBeCloseTo(5000 * (12.00 / 1e6), 9);
  });

  test('a reported audio-token count is trusted verbatim — the fallback stays out of it', () => {
    // A real 1-second clip must not be inflated to a 10-minute size estimate.
    const measured = computeGeminiTranscribeCost('gemini-3.5-transcribe', {
      audioInputTokens: 25, outputTokens: 4, fallbackDurationSeconds: 600,
    });
    expect(measured).toBeCloseTo(25 * (2.00 / 1e6) + 4 * (12.00 / 1e6), 9);
  });

  test('CJK output tokens are estimated denser than the Latin 4 chars/token', () => {
    // Gemini's tokenizer spends ~1 token per 1-1.5 Han/Kana characters. Pricing
    // Japanese at 4 chars/token under-bills the output half several times over;
    // the estimate below is a deliberate floor at 2 chars/token.
    const japanese = 'これはハイパーウィスパーの書き起こしのテストです。';
    expect(countCjkChars(japanese)).toBe(japanese.length);
    expect(estimateGeminiTranscribeOutputTokens(japanese))
      .toBe(Math.ceil(japanese.length / 2));
    expect(estimateGeminiTranscribeOutputTokens(japanese))
      .toBeGreaterThan(Math.ceil(japanese.length / 4));

    // Latin text is unchanged, and accented Latin / Cyrillic is NOT counted as
    // CJK — those tokenize close enough to the 4 chars/token figure.
    expect(estimateGeminiTranscribeOutputTokens('a'.repeat(400))).toBe(100);
    expect(countCjkChars('déjà vu, привет')).toBe(0);

    // A mixed transcript charges each script at its own ratio.
    expect(estimateGeminiTranscribeOutputTokens(`${'a'.repeat(400)}${japanese}`))
      .toBe(Math.ceil(100 + japanese.length / 2));
  });

  test('an unknown model falls back to the pre-recorded rate, never $0', () => {
    const known = computeGeminiTranscribeCost('gemini-3.5-transcribe', {
      audioInputTokens: 1500, outputTokens: 188,
    });
    expect(computeGeminiTranscribeCost('gemini-3.5-transcribe-vnext', {
      audioInputTokens: 1500, outputTokens: 188,
    })).toBe(known);
  });

  test('the live model is billed at its own higher rate (~1.75x)', () => {
    const live = computeGeminiTranscribeLiveCost(60);
    const prerecorded = computeGeminiTranscribeCost('gemini-3.5-transcribe', {
      audioInputTokens: 1500, outputTokens: 188,
    });
    expect(live).toBeGreaterThan(prerecorded);
    // 1,500 audio tokens at $3.50/1M + ~188 output tokens at $21.00/1M.
    expect(live).toBeCloseTo(1500 * (3.50 / 1e6) + 187.5 * (21.00 / 1e6), 6);
    // Catalog figure for the live entry is 9.6 credits/min (≤10% drift rule).
    expect(live * 1000).toBeGreaterThan(9.6 * 0.9);
    expect(live * 1000).toBeLessThan(9.6 * 1.1);
  });

  test('the live cost prefers a real transcript length when the session provides one', () => {
    const estimated = computeGeminiTranscribeLiveCost(60);
    // A near-silent minute produced almost no text — billing the per-second
    // output estimate anyway would over-charge.
    expect(computeGeminiTranscribeLiveCost(60, 8)).toBeLessThan(estimated);
    // A very talkative minute costs more than the estimate.
    expect(computeGeminiTranscribeLiveCost(60, 2000)).toBeGreaterThan(estimated);
  });

  test('the live cost never decreases as the transcript grows, and silence is the floor', () => {
    // Regression guard. Treating 0 chars as "unknown" and pricing it at the
    // ~150 wpm per-second estimate made the curve fall off a cliff at 1
    // character: 60 s billed 9.2 credits for silence and 5.3 for one letter, so
    // every session below 150 wpm cost less than saying nothing, and a stuck
    // push-to-talk minute over-billed by 74%.
    const audioOnly = 60 * 25 * (3.50 / 1e6);
    expect(computeGeminiTranscribeLiveCost(60, 0)).toBeCloseTo(audioOnly, 9);

    let previous = computeGeminiTranscribeLiveCost(60, 0);
    for (const chars of [1, 2, 3, 4, 5, 8, 40, 100, 748, 749, 750, 1000, 2000, 10_000]) {
      const cost = computeGeminiTranscribeLiveCost(60, chars);
      expect(cost).toBeGreaterThanOrEqual(previous);
      previous = cost;
    }

    // Silence is the cheapest a 60 s session can be, and specifically cheaper
    // than the same minute spoken at a normal 150 wpm.
    const spokenMinute = computeGeminiTranscribeLiveCost(60, 749);
    expect(computeGeminiTranscribeLiveCost(60, 0)).toBeLessThan(spokenMinute);
    // The omitted-argument form is the reservation estimate, and is unchanged:
    // it means "no transcript figure exists yet", not "zero characters".
    expect(computeGeminiTranscribeLiveCost(60)).toBeGreaterThan(computeGeminiTranscribeLiveCost(60, 0));
    expect(computeGeminiTranscribeLiveCost(60)).toBeCloseTo(spokenMinute, 4);
  });

  test('usdForCredits inverts usdToCredits, so a balance can clamp a charge', () => {
    expect(usdForCredits(4.6)).toBeCloseTo(0.0046, 9);
    expect(creditsForCost(usdForCredits(4.6))).toBe(4.6);
    expect(usdForCredits(0)).toBe(0);
    expect(usdForCredits(-5)).toBe(0);
    expect(usdForCredits(Number.NaN)).toBe(0);
  });

  test('gemini-transcribe reserves vocabulary tokens at the input rate', () => {
    const prompt = 'x'.repeat(400); // 100 tokens at 4 chars/token
    expect(estimatePromptInputReservationUsd('gemini-transcribe', 'gemini-3.5-transcribe', prompt))
      .toBeCloseTo(100 * (2.00 / 1e6), 9);
    expect(estimatePromptInputReservationUsd('gemini-transcribe', 'gemini-3.5-transcribe-live', prompt))
      .toBeCloseTo(100 * (3.50 / 1e6), 9);
    expect(estimatePromptInputReservationUsd('gemini-transcribe', 'gemini-3.5-transcribe', undefined)).toBe(0);
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

describe('flat per-audio-minute STT rates', () => {
  test('each duration-billed provider charges its own published rate', () => {
    // One audio minute at each provider's documented rate. These are the
    // numbers we actually pay upstream, so a silent edit here is a margin bug.
    expect(computeDeepgramTranscriptionCost(60)).toBeCloseTo(0.0055, 6);   // $0.0055/min
    expect(computeAzureMaiTranscriptionCost(60)).toBeCloseTo(0.006, 6);    // $0.006/min
    expect(computeGoogleChirpTranscriptionCost(60)).toBeCloseTo(0.016, 6); // $0.016/min
    expect(computeXaiTranscriptionCost(3600)).toBeCloseTo(0.10, 6);        // $0.10/hour
  });

  test('flat per-minute rates scale linearly with duration', () => {
    // No minimum-billable floor on these providers (unlike Groq), so half the
    // audio is half the cost and zero audio is free.
    expect(computeDeepgramTranscriptionCost(30)).toBeCloseTo(computeDeepgramTranscriptionCost(60) / 2, 6);
    expect(computeGoogleChirpTranscriptionCost(120)).toBeCloseTo(computeGoogleChirpTranscriptionCost(60) * 2, 6);
    expect(computeAzureMaiTranscriptionCost(0)).toBe(0);
  });

  test('AssemblyAI sync bills its own $0.45/hr rate, above every async tier', () => {
    // The sync product always runs universal-3-5-pro and publishes a higher
    // rate than the async tiers. Reserving at the async rate would under-bill.
    expect(computeAssemblyAISyncTranscriptionCost(3600)).toBeCloseTo(0.45, 6);
    expect(computeAssemblyAISyncTranscriptionCost(60))
      .toBeGreaterThan(computeAssemblyAITranscriptionCost(60, 'universal-3-5-pro', true, true));
  });

  test('the exported sync rate constant is the rate the sync cost fn charges', () => {
    // stt-models.ts imports ASSEMBLYAI_SYNC_COST_PER_AUDIO_MINUTE for its
    // preflight reservation instead of copying the literal. Pin the two
    // together so the amount reserved can never drift from the amount billed.
    expect(computeAssemblyAISyncTranscriptionCost(60)).toBeCloseTo(ASSEMBLYAI_SYNC_COST_PER_AUDIO_MINUTE, 9);
  });
});

describe('credit conversion', () => {
  test('1 credit is $0.001', () => {
    expect(usdToCredits(0.001)).toBeCloseTo(1, 9);
    expect(usdToCredits(0.005)).toBeCloseTo(5, 9);
    expect(usdToCredits(1)).toBeCloseTo(1000, 9);
  });

  test('usdToCredits fails closed to 0.1 credits on a non-positive or non-finite cost', () => {
    // A $0 / NaN / Infinity cost must never convert to 0 credits, or a broken
    // upstream usage object becomes free transcription.
    expect(usdToCredits(0)).toBe(0.1);
    expect(usdToCredits(-1)).toBe(0.1);
    expect(usdToCredits(Number.NaN)).toBe(0.1);
    expect(usdToCredits(Number.POSITIVE_INFINITY)).toBe(0.1);
  });

  test('creditsForCost rounds UP to the next tenth of a credit', () => {
    // $0.00551 = 5.51 credits → 5.6, never 5.5.
    expect(creditsForCost(0.00551)).toBeCloseTo(5.6, 9);
    // $0.00983 (one ElevenLabs minute) = 9.83 credits → 9.9.
    expect(creditsForCost(0.00983)).toBeCloseTo(9.9, 9);
  });

  test('creditsForCost leaves a value already on a tenth alone', () => {
    // The `- Number.EPSILON` in roundUpToTenth exists for this: an exact
    // 0.6/5.5 credits must not inflate to 0.7/5.6 through float dust.
    expect(creditsForCost(0.0006)).toBeCloseTo(0.6, 9);
    expect(creditsForCost(0.0055)).toBeCloseTo(5.5, 9);
  });

  test('creditsForCost floors at 0.1 credits', () => {
    expect(creditsForCost(0)).toBe(0.1);
    expect(creditsForCost(-0.5)).toBe(0.1);
    expect(creditsForCost(Number.NaN)).toBe(0.1);
    // A sub-tenth-of-a-credit cost still bills the 0.1 minimum.
    expect(creditsForCost(0.0000001)).toBe(0.1);
    expect(creditsForCost(0.0001)).toBe(0.1);
  });

  test('creditsForCost never bills less than the underlying cost', () => {
    // Property sweep across an irrational-ish step so tenth boundaries are
    // hit from both sides: credits * $0.001 must always cover the USD cost.
    for (let i = 1; i <= 5000; i++) {
      const costUsd = i * 0.0000137;
      const billedUsd = creditsForCost(costUsd) * 0.001;
      expect(billedUsd).toBeGreaterThanOrEqual(costUsd - 1e-12);
    }
  });

  test('creditsForCost is monotonic in cost', () => {
    // A more expensive transcription can never bill fewer credits.
    let previous = 0;
    for (let i = 0; i <= 400; i++) {
      const credits = creditsForCost(i * 0.00013);
      expect(credits).toBeGreaterThanOrEqual(previous);
      previous = credits;
    }
  });
});

describe('USD rounding and formatting', () => {
  test('roundUsd removes binary floating-point dust', () => {
    expect(roundUsd(0.1 + 0.2)).toBe(0.3);
    expect(roundUsd(0.0055 * 3)).toBe(0.0165);
  });

  test('roundUsd rounds at the 6th decimal', () => {
    expect(roundUsd(0.0000005)).toBe(0.000001);  // half rounds up
    expect(roundUsd(0.00000049)).toBe(0);        // below half rounds to zero
    expect(roundUsd(1.23456749)).toBe(1.234567);
  });

  test('formatUsd always emits exactly 6 decimal places', () => {
    // The value goes out on the X-Total-Cost-Usd response header, so the
    // width is part of the wire contract, not cosmetic.
    expect(formatUsd(0.006)).toBe('0.006000');
    expect(formatUsd(0)).toBe('0.000000');
    expect(formatUsd(12)).toBe('12.000000');
  });

  test('formatUsd rounds before formatting rather than truncating', () => {
    expect(formatUsd(0.1 + 0.2)).toBe('0.300000');
    expect(formatUsd(1.23456789)).toBe('1.234568');
  });
});

describe('LLM chat costs', () => {
  const usage = (prompt: number, completion: number): GroqUsage => ({
    prompt_tokens: prompt,
    completion_tokens: completion,
    total_tokens: prompt + completion,
  });

  test('Anthropic bills prompt and completion tokens at the Haiku 4.5 rates', () => {
    // $1.00/1M input, $5.00/1M output.
    expect(computeAnthropicCost(1_000_000, 0)).toBeCloseTo(1.00, 6);
    expect(computeAnthropicCost(0, 1_000_000)).toBeCloseTo(5.00, 6);
  });

  test('Anthropic prices cache writes at 1.25x input and cache reads at 0.10x input', () => {
    const input = computeAnthropicCost(1_000_000, 0);
    const cacheWrite = computeAnthropicCost(0, 0, 1_000_000, 0);
    const cacheRead = computeAnthropicCost(0, 0, 0, 1_000_000);
    expect(cacheWrite).toBeCloseTo(input * 1.25, 6);
    expect(cacheRead).toBeCloseTo(input * 0.10, 6);
    // A cache read must be the cheapest of the three, or caching costs money.
    expect(cacheRead).toBeLessThan(input);
    expect(input).toBeLessThan(cacheWrite);
  });

  test('Anthropic sums all four token buckets and defaults the cache buckets to 0', () => {
    // 1000 of each: 0.001 + 0.005 + 0.00125 + 0.0001.
    expect(computeAnthropicCost(1000, 1000, 1000, 1000)).toBeCloseTo(0.00735, 9);
    // Omitting the cache arguments must not add a charge.
    expect(computeAnthropicCost(1000, 1000)).toBeCloseTo(0.006, 9);
    expect(computeAnthropicCost(1000, 1000)).toBe(computeAnthropicCost(1000, 1000, 0, 0));
  });

  test('the single-model chat providers bill their published token rates', () => {
    // Cerebras $0.35/$0.75 per 1M, Groq $0.15/$0.60, xAI Grok 4.1 Fast $1.25/$2.50.
    expect(computeCerebrasChatCost(usage(1_000_000, 1_000_000))).toBeCloseTo(0.35 + 0.75, 6);
    expect(computeGroqChatCost(usage(1_000_000, 1_000_000))).toBeCloseTo(0.15 + 0.60, 6);
    expect(computeXaiGrokFastChatCost(usage(1_000_000, 1_000_000))).toBeCloseTo(1.25 + 2.50, 6);
    // Groq GPT-OSS is the cheapest of the three on identical usage.
    expect(computeGroqChatCost(usage(1000, 1000))).toBeLessThan(computeCerebrasChatCost(usage(1000, 1000)));
    expect(computeCerebrasChatCost(usage(1000, 1000))).toBeLessThan(computeXaiGrokFastChatCost(usage(1000, 1000)));
  });

  test('the multi-model chat providers price each model separately', () => {
    // gpt-5-nano is cheaper than gpt-5-mini; flash-lite cheaper than flash.
    expect(computeOpenAIChatCost('gpt-5-mini', usage(1_000_000, 0))).toBeCloseTo(0.25, 6);
    expect(computeOpenAIChatCost('gpt-5-nano', usage(1_000_000, 0))).toBeCloseTo(0.05, 6);
    expect(computeGeminiChatCost('gemini-2.5-flash', usage(1_000_000, 1_000_000))).toBeCloseTo(0.30 + 2.50, 6);
    expect(computeGeminiChatCost('gemini-2.5-flash-lite', usage(1_000_000, 1_000_000))).toBeCloseTo(0.10 + 0.40, 6);
    expect(computeMistralChatCost('mistral-small-latest', usage(1_000_000, 1_000_000))).toBeCloseTo(0.15 + 0.60, 6);
  });

  test('an unknown chat model falls back to the provider default rate, never $0', () => {
    // Catalog or response-header drift must fail closed: a model id we do not
    // recognise bills at the provider default, not free.
    const tokens = usage(1_000_000, 1_000_000);
    expect(computeOpenAIChatCost('gpt-9-imaginary', tokens)).toBe(computeOpenAIChatCost('gpt-5-mini', tokens));
    expect(computeGeminiChatCost('gemini-99-ultra', tokens)).toBe(computeGeminiChatCost('gemini-2.5-flash', tokens));
    // The retired Nemo id is no longer allowlisted; old clients still sending
    // it resolve to the Mistral default rate.
    expect(computeMistralChatCost('open-mistral-nemo', tokens)).toBe(computeMistralChatCost('mistral-small-latest', tokens));
    expect(computeOpenAIChatCost('', tokens)).toBeGreaterThan(0);
  });

  test('zero usage costs nothing at the chat layer', () => {
    // The 0.1-credit minimum is applied later by creditsForCost, not here —
    // the raw USD figure for zero tokens is genuinely 0.
    expect(computeGroqChatCost(usage(0, 0))).toBe(0);
    expect(computeOpenAIChatCost('gpt-5-mini', usage(0, 0))).toBe(0);
    expect(creditsForCost(computeGroqChatCost(usage(0, 0)))).toBe(0.1);
  });
});

describe('usage-object fallbacks and guards', () => {
  test('estimateUsageFromChars converts at ~4 chars per token, rounding up', () => {
    // 10 prompt chars → 3 tokens, 5 completion chars → 2 tokens.
    expect(estimateUsageFromChars(10, 5)).toEqual({
      prompt_tokens: 3,
      completion_tokens: 2,
      total_tokens: 5,
    });
    // Exact multiples do not gain a token.
    expect(estimateUsageFromChars(400, 400)).toEqual({
      prompt_tokens: 100,
      completion_tokens: 100,
      total_tokens: 200,
    });
    // A single character still costs one token.
    expect(estimateUsageFromChars(1, 0).prompt_tokens).toBe(1);
    expect(estimateUsageFromChars(0, 0)).toEqual({
      prompt_tokens: 0,
      completion_tokens: 0,
      total_tokens: 0,
    });
  });

  test('the char estimate is what makes a missing usage object bill non-zero', () => {
    // groq-llm.ts feeds this estimate to computeGroqChatCost when the upstream
    // response omits `usage`. A 2000-char prompt must produce a real charge.
    const estimated = estimateUsageFromChars(2000, 800);
    expect(computeGroqChatCost(estimated)).toBeGreaterThan(0);
  });

  test('isGroqUsage accepts a complete numeric usage object', () => {
    expect(isGroqUsage({ prompt_tokens: 1, completion_tokens: 2, total_tokens: 3 })).toBe(true);
    // Extra fields from a newer vendor schema do not disqualify it.
    expect(isGroqUsage({ prompt_tokens: 0, completion_tokens: 0, total_tokens: 0, cached_tokens: 5 })).toBe(true);
  });

  test('isGroqUsage rejects anything that would bill $0 by accident', () => {
    expect(isGroqUsage(null)).toBe(false);
    expect(isGroqUsage(undefined)).toBe(false);
    expect(isGroqUsage('usage')).toBe(false);
    expect(isGroqUsage(42)).toBe(false);
    expect(isGroqUsage([])).toBe(false);
    expect(isGroqUsage({})).toBe(false);
    // Missing one field.
    expect(isGroqUsage({ prompt_tokens: 1, completion_tokens: 2 })).toBe(false);
    // String-typed counts (a real vendor drift) must not pass.
    expect(isGroqUsage({ prompt_tokens: '1', completion_tokens: 2, total_tokens: 3 })).toBe(false);
  });

  test('estimateSonioxContextTokens converts at ~0.3 tokens per char, rounding up', () => {
    expect(estimateSonioxContextTokens('abcdefghij')).toBe(3); // ceil(10 * 0.3)
    expect(estimateSonioxContextTokens('a')).toBe(1);          // ceil(0.3)
    expect(estimateSonioxContextTokens('')).toBe(0);
    expect(estimateSonioxContextTokens(undefined)).toBe(0);
  });

  test('the Soniox token estimate is the same one the context charge uses', () => {
    // estimatePromptInputReservationUsd('soniox', ...) must reserve exactly
    // what computeSonioxTranscriptionCost later bills for the same terms.
    const contextText = 'kubernetes, kubectl, etcd, containerd';
    const tokens = estimateSonioxContextTokens(contextText);
    const reserved = estimatePromptInputReservationUsd('soniox', 'stt-async-v4', contextText);
    const billedContext = computeSonioxTranscriptionCost(60, tokens) - computeSonioxTranscriptionCost(60, 0);
    expect(reserved).toBeCloseTo(billedContext, 9);
  });
});
