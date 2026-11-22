// COST CALCULATION MODULE
// Handles pricing calculations for STT providers and LLM post-processing

import { roundUpToTenth } from './utils';
import {
  applyKeytermsSurcharge,
  ASSEMBLYAI_SYNC_USD_PER_MINUTE,
  dearestModel,
  defaultModelRow,
  findModelRow,
  tokenBillingFor,
  withAdditiveAddOns,
  type SttModelDef,
  type SttTokenRates,
} from './stt-models';

// =============================================================================
// PRICING CONSTANTS
// =============================================================================

// STT duration prices (per-minute and per-hour rates, minimum billable
// seconds, medical/keyterms add-ons) live on the model rows in stt-models.ts,
// which the preflight reservation reads too, and so do the token rates of the
// token-billed rows. This file holds the formulas.

// ── New HyperWhisper Cloud STT proxy providers (per-model) ──────────────────
// Each provider exposes one or more models with distinct pricing. Token-billed
// providers (OpenAI gpt-4o-*, Gemini) bill from the response `usage` object so
// the charge is exact; duration-billed providers bill from returned audio
// seconds. See shared-app-classification/cloud-stt-catalog.json for the
// client-facing credits/min surface.

// OpenAI — whisper-1, gpt-transcribe and gpt-live-transcribe are flat
// per-audio-minute billed (their rows in stt-models.ts carry the rate; verified
// against developers.openai.com/api/docs/pricing); gpt-4o-* are token-billed,
// at the token rates on their rows.
//
// Gemini — audio billed at 32 tokens/sec; cost read from usageMetadata at the
// rates on each model's row in stt-models.ts (text and audio input bill at
// different rates on Flash models; Pro models switch to a higher long-context
// tier above this many prompt tokens).
const GEMINI_LONG_CONTEXT_THRESHOLD = 200_000;

// ── Gemini 3.5 Transcribe (the dedicated speech models) ─────────────────────
// A DIFFERENT product from the Gemini LLM rates above, on a different endpoint
// (`/v1beta/interactions`, not `:generateContent`), with a different audio
// tokenisation. Nothing here may be expressed in terms of the `gemini` rows' rates
// or GEMINI_AUDIO_TOKENS_PER_MINUTE:
//
//   * Audio bills at 25 tokens/sec, NOT the 32 tok/s of the multimodal models.
//     Measured against the live API: a 9.456 s clip reports exactly 236 audio
//     tokens (24.96 tok/s) in `usage.input_tokens_by_modality`.
//   * `usage.total_output_tokens` comes back as **0 on every response**, verified
//     live across auto-detect / language-pinned / vocabulary requests. The
//     endpoint simply does not report output tokens, so the transcript's output
//     cost CANNOT be read from the usage object — and billing only the audio
//     input would systematically under-charge by ~45%.
//
//     DECISION (not an oversight): estimate the output tokens from the returned
//     transcript's length at the same ~4 chars/token heuristic this file already
//     uses twice (RESERVATION_CHARS_PER_TOKEN / FALLBACK_CHARS_PER_TOKEN). The
//     transcript is the only output the model produced, we hold it in full when
//     we bill, and the estimate lands within a few percent of a real tokeniser
//     for LATIN-SCRIPT text at dictation length. It is NOT accurate for every
//     script: CJK tokenizes at roughly 1–1.5 chars/token, several times denser,
//     so `estimateGeminiTranscribeOutputTokens` counts those characters
//     separately (GEMINI_TRANSCRIBE_CJK_CHARS_PER_TOKEN). If Google ever starts
//     populating `total_output_tokens`, prefer it and delete the estimate —
//     `estimateGeminiTranscribeOutputTokens` is the single place to change.
//
// Rates: $2.00/1M input tokens (audio and text alike — one input price), $12.00/1M
// output tokens for the pre-recorded model; $3.50 / $21.00 for the live model.
// At ~150 wpm that is $0.0030 audio + ~$0.0023 output ≈ $0.0053/min pre-recorded
// and ≈ $0.0092/min live, which is what `cloudTier.creditsPerMinute` 5.5 / 9.6 in
// shared-app-classification/cloud-stt-catalog.json is derived from (both inside
// that file's AGENTS.md ≤10% drift rule).
// The rates are on the two models' rows in stt-models.ts (one input rate for
// every modality there).
export const GEMINI_TRANSCRIBE_AUDIO_TOKENS_PER_SECOND = 25;
// Output tokens per second of audio, used ONLY where no transcript is in hand
// (the live path bills per second as the session runs). 12.5 chars/sec ÷
// RESERVATION_CHARS_PER_TOKEN — i.e. the same char estimate applied to ~150 wpm
// speech, so the two paths cannot disagree about what a minute of speech costs.
const GEMINI_TRANSCRIBE_OUTPUT_TOKENS_PER_AUDIO_SECOND = 3.125;

// Soniox — the flat ≈ $0.10/hr rate is on its stt-models.ts row; custom-context
// INPUT-text tokens are charged ON TOP at $3.50/1M (async). Soniox bills ~0.3
// tokens per character. Ref: https://soniox.com/pricing
const SONIOX_INPUT_TEXT_COST_PER_TOKEN = 3.50 / 1_000_000;            // async context tokens
const SONIOX_TOKENS_PER_CHAR = 0.3;
export function estimateSonioxContextTokens(contextText: string | undefined): number {
  if (!contextText) return 0;
  return Math.ceil(contextText.length * SONIOX_TOKENS_PER_CHAR);
}

// LLM post-processing rates live on the model rows in llm-models.ts.

// Credit model: 1 credit = $0.001
const USD_PER_CREDIT = 0.001;

// =============================================================================
// TYPES
// =============================================================================

export interface GroqUsage {
  prompt_tokens: number;
  completion_tokens: number;
  total_tokens: number;
}

// =============================================================================
// STT COSTS
// =============================================================================

/**
 * Bill `durationSeconds` at a duration-priced registry row, with the add-ons
 * the row meters for this request. The one billing formula for every
 * flat-rate STT model; the preflight rate is derived from the same row.
 */
function computeRowDurationCost(
  row: SttModelDef,
  durationSeconds: number,
  medical: boolean = false,
  keyterms: boolean = false,
): number {
  const { billing } = row;
  if (billing.kind === 'perHour') {
    const billableSeconds = billing.minBillableSeconds === undefined
      ? durationSeconds
      : Math.max(durationSeconds, billing.minBillableSeconds);
    // Per-minute add-ons do not apply to a per-hour row; no such row has any.
    return roundUsd(applyKeytermsSurcharge((billableSeconds / 3600) * billing.usdPerHour, row.addOns, keyterms));
  }
  if (billing.kind === 'perMinute') {
    const perMinute = withAdditiveAddOns(billing.usdPerMinute, row.addOns, medical, keyterms);
    return roundUsd(applyKeytermsSurcharge((durationSeconds / 60) * perMinute, row.addOns, keyterms));
  }
  throw new Error(`STT model ${row.id} is token-billed; it has no duration price`);
}

/** The flat per-minute rate of a duration-priced row. */
function perMinuteRate(row: SttModelDef): number {
  if (row.billing.kind !== 'perMinute') {
    throw new Error(`STT model ${row.id} is not billed per minute`);
  }
  return row.billing.usdPerMinute;
}

export function computeElevenLabsTranscriptionCost(durationSeconds: number, keyterms: boolean = false): number {
  // The adapter decides whether keyterms were sent (scribe_v2 only).
  return computeRowDurationCost(defaultModelRow('elevenlabs'), durationSeconds, false, keyterms);
}

export function computeDeepgramTranscriptionCost(durationSeconds: number): number {
  return computeRowDurationCost(defaultModelRow('deepgram'), durationSeconds);
}

export function computeGroqTranscriptionCost(durationSeconds: number, model?: string): number {
  // An id with no row (or none) bills at the default turbo rate.
  return computeRowDurationCost(findModelRow('groq', model) ?? defaultModelRow('groq'), durationSeconds);
}

export function computeXaiTranscriptionCost(durationSeconds: number): number {
  return computeRowDurationCost(defaultModelRow('grok'), durationSeconds);
}

export function computeAzureMaiTranscriptionCost(durationSeconds: number, model?: string): number {
  // Unknown/absent model bills at the dearest row (1.5), so a model string we
  // don't recognise cannot under-bill. `findModelRow` compares ids, so a
  // prototype key such as 'constructor' cannot turn the rate into a function.
  return computeRowDurationCost(findModelRow('azure-mai', model) ?? dearestModel('azure-mai'), durationSeconds);
}

export function computeGoogleChirpTranscriptionCost(durationSeconds: number): number {
  return computeRowDurationCost(defaultModelRow('google-chirp'), durationSeconds);
}

export function computeMetaMuseTranscriptionCost(durationSeconds: number): number {
  return computeRowDurationCost(defaultModelRow('meta'), durationSeconds);
}

// ── New cloud STT proxy providers ───────────────────────────────────────────

export interface OpenAITranscriptionUsage {
  durationSeconds: number;
  inputTokens?: number;
  outputTokens?: number;
}

/**
 * OpenAI: the duration-billed rows bill on audio duration; the `gpt-4o-*` transcribe models
 * bill on tokens from the response `usage` object. The gpt-4o `prompt` text
 * input tokens are billed at the (higher) audio-input rate — they're a handful
 * of tokens, so erring high there is both simpler and conservative.
 */
export function computeOpenAITranscriptionCost(model: string, usage: OpenAITranscriptionUsage): number {
  // A row billed by duration (whisper-1, gpt-transcribe, gpt-live-transcribe)
  // bills its flat rate. Everything else — the gpt-4o-* rows and an id with no
  // row — takes the token-billed path below.
  const row = findModelRow('openai', model);
  if (row && row.billing.kind !== 'tokens') {
    return computeRowDurationCost(row, usage.durationSeconds);
  }

  // An id with no row bills at the dearest token-billed row (gpt-4o-transcribe).
  const rate = tokenBillingFor('openai', model);
  const inputTokens = Math.max(0, usage.inputTokens ?? 0);
  const outputTokens = Math.max(0, usage.outputTokens ?? 0);

  const tokenCost = inputTokens * rate.audioInputPerToken + outputTokens * rate.outputPerToken;

  if (tokenCost > 0) {
    return roundUsd(tokenCost);
  }

  // Missing/changed `usage` schema would otherwise bill $0 — fall back to a
  // duration-based estimate so token-billed models fail closed, mirroring the
  // LLM `estimateUsageFromChars` safety net.
  // A token row that authors no floor bills the provider's dearest rate: never $0.
  const floorPerMinute = rate.missingUsageUsdPerMinute
    ?? dearestModel('openai').estimatedUsdPerMinute;
  return roundUsd((usage.durationSeconds / 60) * floorPerMinute);
}

export interface GeminiTranscriptionUsage {
  audioInputTokens: number;
  /**
   * Non-audio prompt-input tokens (the instruction + vocabulary text we send).
   * Billed at the model's text-input rate — NOT the audio rate. The adapter
   * derives this as promptTokenCount − audioInputTokens so audio + text always
   * sums to the documented input total (no double-count, no unbilled remainder).
   */
  textInputTokens?: number;
  outputTokens: number;
  /** Duration estimate (from payload size) used only if usageMetadata is absent. */
  fallbackDurationSeconds?: number;
}

// Gemini represents 1 minute of audio as 1,920 input tokens (32 tokens/sec).
const GEMINI_AUDIO_TOKENS_PER_MINUTE = 1_920;

/**
 * Gemini bills audio-input + text-input + output tokens from `usageMetadata`.
 * Text and audio input have different per-token rates on Flash models, so both
 * are billed at their own rate. Pro models switch to a higher long-context tier
 * once the prompt (audio + text input) exceeds 200k tokens. Output tokens
 * include any thinking tokens (only 2.5-flash/flash-lite can zero thinking;
 * Pro/3.x always charge some). If usageMetadata is missing we fall back to a
 * duration-based estimate so a transcription never bills $0 (fail-closed).
 */
export function computeGeminiTranscriptionCost(model: string, usage: GeminiTranscriptionUsage): number {
  // An id with no row bills at the dearest row, never a cheaper one.
  const rateDef = tokenBillingFor('gemini', model);
  const audioTokens = Math.max(0, usage.audioInputTokens);
  const textTokens = Math.max(0, usage.textInputTokens ?? 0);
  const outputTokens = Math.max(0, usage.outputTokens);

  // Long-context tier is keyed on the total prompt (input) token count.
  const promptTokens = audioTokens + textTokens;
  const rate = (rateDef.longContext && promptTokens > GEMINI_LONG_CONTEXT_THRESHOLD)
    ? rateDef.longContext
    : rateDef;

  const tokenCost = audioTokens * rate.audioInputPerToken
    + textTokens * rate.textInputPerToken
    + outputTokens * rate.outputPerToken;

  if (tokenCost > 0) {
    return roundUsd(tokenCost);
  }

  const seconds = Math.max(0, usage.fallbackDurationSeconds ?? 0);
  return roundUsd((seconds / 60) * GEMINI_AUDIO_TOKENS_PER_MINUTE * rateDef.audioInputPerToken);
}

// ~4 chars/token — matches the Gemini adapter's prompt-text estimate.
const RESERVATION_CHARS_PER_TOKEN = 4;

/**
 * Conservative USD for the prompt-text INPUT tokens a TOKEN-BILLED provider will
 * charge for `initial_prompt` (Gemini, all models; OpenAI gpt-4o-transcribe /
 * gpt-4o-mini-transcribe). Used ONLY by the preflight reservation so a large
 * vocabulary prompt on a short clip can't be deducted beyond what was reserved.
 * Duration-billed providers (whisper-1, deepgram/groq/grok/elevenlabs/azure/
 * soniox/mistral) don't bill prompt tokens → 0; the ElevenLabs /
 * AssemblyAI keyterm add-ons are reserved separately via the per-minute surcharge.
 */
export function estimatePromptInputReservationUsd(
  provider: string,
  model: string | undefined,
  initialPrompt: string | undefined,
): number {
  if (!initialPrompt) return 0;
  const tokens = Math.ceil(initialPrompt.length / RESERVATION_CHARS_PER_TOKEN);
  if (provider === 'gemini') {
    return tokens * tokenBillingFor('gemini', model).textInputPerToken;
  }
  if (provider === 'openai') {
    // A duration-billed row (whisper-1, gpt-transcribe, gpt-live-transcribe)
    // has no separate prompt-token charge. An absent/unknown id and the
    // explicit gpt-4o-transcribe use the (more expensive) transcribe input rate.
    const row = findModelRow('openai', model);
    if (row && row.billing.kind !== 'tokens') return 0;
    return tokens * tokenBillingFor('openai', model).textInputPerToken;
  }
  if (provider === 'gemini-transcribe') {
    // The vocabulary goes upstream as `custom_vocabulary`, a structured field
    // rather than prose, and bills as TEXT input tokens at the same per-token
    // rate as the audio. (Live traffic reports only ~1 text token even with
    // terms attached, so this over-reserves — which is the safe direction.)
    return tokens * tokenBillingFor('gemini-transcribe', model).textInputPerToken;
  }
  if (provider === 'soniox') {
    // Soniox charges the custom-context terms as async input-text tokens on top
    // of the audio/output blend. Its tokenizer (~0.3 tok/char) differs from the
    // 4-chars/token heuristic above, so use the Soniox-specific estimate.
    return estimateSonioxContextTokens(initialPrompt) * SONIOX_INPUT_TEXT_COST_PER_TOKEN;
  }
  return 0;
}

// CJK scripts tokenize FAR denser than the ~4 chars/token that holds for Latin
// text: Gemini's SentencePiece vocabulary spends roughly one token per 1–1.5
// Han/Kana/Hangul characters. Charging a Japanese transcript at 4 chars/token
// under-bills the output half of the bill several times over.
//
// The value below is deliberately a FLOOR (2 chars/token), not a best estimate:
// it removes most of the under-charge without risking an over-charge on a real
// user's dictation, because no measurement of this endpoint's own tokenizer is
// possible — `/v1beta/interactions` reports `total_output_tokens: 0`, so there
// is nothing to calibrate against short of a separate `countTokens` study.
// Tighten it toward 1 token/char only with numbers in hand.
const GEMINI_TRANSCRIBE_CJK_CHARS_PER_TOKEN = 2;
// Han, Hiragana, Katakana, Hangul, and the CJK punctuation/full-width forms that
// travel with them. Deliberately not "any non-ASCII": accented Latin, Cyrillic
// and Greek tokenize close enough to the 4 chars/token figure.
const CJK_PATTERN = new RegExp(
  '['
  + '\\u3000-\\u303F' // CJK symbols and punctuation
  + '\\u3040-\\u30FF' // Hiragana + Katakana
  + '\\u3400-\\u4DBF' // CJK unified ideographs, extension A
  + '\\u4E00-\\u9FFF' // CJK unified ideographs
  + '\\uAC00-\\uD7AF' // Hangul syllables
  + '\\uF900-\\uFAFF' // CJK compatibility ideographs
  + '\\uFF00-\\uFFEF' // Full-width and half-width forms
  + ']',
  'g',
);

/** How many characters of `text` belong to a CJK script. */
export function countCjkChars(text: string): number {
  return text.match(CJK_PATTERN)?.length ?? 0;
}

/**
 * Output-token estimate for a Gemini 3.5 Transcribe response.
 *
 * Exists because `usage.total_output_tokens` is always 0 on `/v1beta/interactions`
 * (see the block above) — this is the deliberate substitute, not a fallback for
 * a malformed response.
 *
 * Script-aware, following the per-vendor precedent of
 * {@link estimateSonioxContextTokens}: the ~4 chars/token heuristic is a Latin
 * figure and is several times too generous for CJK. The LIVE path
 * ({@link computeGeminiTranscribeLiveCost}) cannot use this — it meters a
 * running character COUNT, never the text — so a CJK live session keeps the
 * Latin ratio. Fixing that means carrying the text into the streaming route.
 */
export function estimateGeminiTranscribeOutputTokens(transcript: string): number {
  const cjkChars = countCjkChars(transcript);
  const otherChars = transcript.length - cjkChars;
  return Math.ceil(
    cjkChars / GEMINI_TRANSCRIBE_CJK_CHARS_PER_TOKEN + otherChars / RESERVATION_CHARS_PER_TOKEN,
  );
}

export interface GeminiTranscribeUsage {
  /** `usage.input_tokens_by_modality[modality == "audio"].tokens`. */
  audioInputTokens: number;
  /** `usage.input_tokens_by_modality[modality == "text"].tokens`. Same rate. */
  textInputTokens?: number;
  /** Estimated from the transcript — the API reports 0. */
  outputTokens: number;
  /** Duration estimate (from payload size) used only if `usage` is absent. */
  fallbackDurationSeconds?: number;
}

/**
 * Gemini 3.5 Transcribe (pre-recorded). Bills the reported input tokens plus the
 * ESTIMATED output tokens; an absent/changed/PARTIAL `usage` object falls back to
 * a duration-based estimate so a transcription never bills less than the audio
 * was worth (fail-closed, matching the other token-billed adapters here).
 *
 * The trigger is a missing AUDIO-token count specifically, not a zero total.
 * `audioInputTokens` is the only field we cannot reconstruct: the output tokens
 * are already our own estimate (the endpoint reports 0), so a response whose
 * `usage` object is absent or has lost its audio modality still arrives here
 * with `outputTokens >= 1` from the transcript and would otherwise skip the
 * fallback entirely and bill a few cents per million — 24x under a real charge.
 * When the audio tokens are missing we take the GREATER of the two figures, so
 * a partial usage object can only ever raise the bill toward the truth.
 */
export function computeGeminiTranscribeCost(model: string, usage: GeminiTranscribeUsage): number {
  // An id with no row bills at the dearest row (the live model), never cheaper.
  const rate = tokenBillingFor('gemini-transcribe', model);
  const audioInputTokens = Math.max(0, usage.audioInputTokens);
  const textInputTokens = Math.max(0, usage.textInputTokens ?? 0);
  const outputTokens = Math.max(0, usage.outputTokens);

  const tokenCost = audioInputTokens * rate.audioInputPerToken
    + textInputTokens * rate.textInputPerToken
    + outputTokens * rate.outputPerToken;
  if (audioInputTokens > 0) {
    return roundUsd(tokenCost);
  }

  const seconds = Math.max(0, usage.fallbackDurationSeconds ?? 0);
  return roundUsd(Math.max(tokenCost, geminiTranscribeSecondsCost(seconds, rate)));
}

/** Per-second cost at `rate`, with output estimated from the audio duration. */
function geminiTranscribeSecondsCost(seconds: number, rate: SttTokenRates): number {
  return seconds * GEMINI_TRANSCRIBE_AUDIO_TOKENS_PER_SECOND * rate.audioInputPerToken
    + seconds * GEMINI_TRANSCRIBE_OUTPUT_TOKENS_PER_AUDIO_SECOND * rate.outputPerToken;
}

/**
 * Gemini 3.5 Transcribe Live (WebSocket). Billed from audio SECONDS rather than
 * a usage object: the live socket reports no token counts at all, and the
 * streaming route meters as the session runs. Audio tokens are the same 25/sec
 * as the pre-recorded model at the live model's higher rate.
 *
 * `transcriptChars` is optional. OMITTED means "no transcript figure exists yet"
 * — the reservation/minimum-balance estimate — and prices the output half from
 * the duration at ~150 wpm. SUPPLIED means "this is how much text the session
 * produced", and ZERO is a real answer: it prices the output half at zero.
 *
 * MONOTONICITY IS A REQUIREMENT, not an accident of the arithmetic. This
 * function must never charge more for a shorter transcript at the same
 * duration. Treating `0` as "unknown" broke that: at 60 s, 0 chars billed the
 * ~150 wpm estimate (9.2 credits) and 1 char billed 5.3, so every session under
 * 749 chars/min cost less than silence and a stuck push-to-talk minute
 * over-billed by 74%. A finals-less session now bills the audio it forwarded
 * and nothing more — an under-charge of the output half in a rare case, which
 * is the side to be wrong on when the alternative is charging a user more for
 * saying less.
 */
export function computeGeminiTranscribeLiveCost(durationSeconds: number, transcriptChars?: number): number {
  const rate = tokenBillingFor('gemini-transcribe', 'gemini-3.5-transcribe-live');
  const seconds = Math.max(0, durationSeconds);
  if (transcriptChars === undefined) {
    return roundUsd(geminiTranscribeSecondsCost(seconds, rate));
  }
  const outputTokens = Math.ceil(Math.max(0, transcriptChars) / RESERVATION_CHARS_PER_TOKEN);
  return roundUsd(
    seconds * GEMINI_TRANSCRIBE_AUDIO_TOKENS_PER_SECOND * rate.audioInputPerToken
      + outputTokens * rate.outputPerToken,
  );
}

/**
 * AssemblyAI duration-billed at the row for the model AssemblyAI actually ran,
 * with that row's add-ons: `medical` (both async tiers) and `keyterms`
 * (Universal-3.x Pro only — see the rows in stt-models.ts). An id with no row
 * bills at universal-2, as it always has.
 */
export function computeAssemblyAITranscriptionCost(
  durationSeconds: number,
  model: string,
  medical: boolean = false,
  keyterms: boolean = false,
): number {
  const row = findModelRow('assemblyai', model) ?? findModelRow('assemblyai', 'universal-2')!;
  return computeRowDurationCost(row, durationSeconds, medical, keyterms);
}

export function computeAssemblyAISyncTranscriptionCost(durationSeconds: number): number {
  return roundUsd((durationSeconds / 60) * ASSEMBLYAI_SYNC_USD_PER_MINUTE);
}

export function computeMistralTranscriptionCost(durationSeconds: number): number {
  return computeRowDurationCost(defaultModelRow('mistral'), durationSeconds);
}

export function computeSonioxTranscriptionCost(durationSeconds: number, contextTextTokens: number = 0): number {
  const audioAndOutput = (durationSeconds / 60) * perMinuteRate(defaultModelRow('soniox'));
  const contextCost = Math.max(0, contextTextTokens) * SONIOX_INPUT_TEXT_COST_PER_TOKEN;
  return roundUsd(audioAndOutput + contextCost);
}

// =============================================================================
// LLM COSTS
// =============================================================================

// The per-model LLM costs (computeLLMChatCost, computeAnthropicCost) are in
// llm-models.ts, keyed on the model row.

// Fallback token estimate (~4 chars/token) for when a provider response omits
// or changes the usage schema. Ensures vendor schema drift fails closed
// (billed via estimate) instead of open (costUsd = 0).
const FALLBACK_CHARS_PER_TOKEN = 4;

export function estimateUsageFromChars(promptChars: number, completionChars: number): GroqUsage {
  const promptTokens = Math.ceil(promptChars / FALLBACK_CHARS_PER_TOKEN);
  const completionTokens = Math.ceil(completionChars / FALLBACK_CHARS_PER_TOKEN);
  return {
    prompt_tokens: promptTokens,
    completion_tokens: completionTokens,
    total_tokens: promptTokens + completionTokens,
  };
}

export function isGroqUsage(value: unknown): value is GroqUsage {
  if (!value || typeof value !== 'object') {
    return false;
  }

  const usage: Record<string, unknown> = value as Record<string, unknown>;
  return typeof usage.prompt_tokens === 'number'
    && typeof usage.completion_tokens === 'number'
    && typeof usage.total_tokens === 'number';
}

// =============================================================================
// CREDITS + FORMATTING
// =============================================================================

export function usdToCredits(usd: number): number {
  if (!Number.isFinite(usd) || usd <= 0) {
    return 0.1;
  }

  return usd / USD_PER_CREDIT;
}

/**
 * Inverse of {@link usdToCredits} — what a credit balance is worth in USD.
 *
 * Used by the live-streaming shell to clamp an end-of-session charge to the
 * balance the mid-session cutoff was measured against, so a session can never
 * bill past the credits the user actually had when it opened.
 */
export function usdForCredits(credits: number): number {
  if (!Number.isFinite(credits) || credits <= 0) {
    return 0;
  }
  return credits * USD_PER_CREDIT;
}

export function creditsForCost(costUsd: number): number {
  if (!Number.isFinite(costUsd) || costUsd <= 0) {
    return 0.1;
  }

  const rawCredits = usdToCredits(costUsd);
  return Math.max(0.1, roundUpToTenth(rawCredits));
}

export function roundUsd(value: number): number {
  return Math.round((value + Number.EPSILON) * 1_000_000) / 1_000_000;
}

export function formatUsd(value: number): string {
  return roundUsd(value).toFixed(6);
}
