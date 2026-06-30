// COST CALCULATION MODULE
// Handles pricing calculations for STT providers and LLM post-processing

import { roundUpToTenth } from './utils';

// =============================================================================
// PRICING CONSTANTS
// =============================================================================

// ElevenLabs Scribe v2 Pricing (USD)
const ELEVENLABS_COST_PER_AUDIO_MINUTE = 0.00983;
const ELEVENLABS_KEYTERMS_SURCHARGE = 0.20;   // +20% on base when keyterm prompting is used (scribe_v2)

// Deepgram Nova-3 Pricing (USD)
// Base $0.0043 + features $0.0012
const DEEPGRAM_COST_PER_AUDIO_MINUTE = 0.0055;

// Groq Whisper Pricing (USD) — per-model: turbo is ~2.8x cheaper than large-v3.
// https://groq.com/pricing — whisper-large-v3 $0.111/hr, turbo $0.04/hr.
const GROQ_WHISPER_COST_PER_AUDIO_HOUR = 0.111; // $0.111/hour (whisper-large-v3)
const GROQ_WHISPER_TURBO_COST_PER_AUDIO_HOUR = 0.04; // $0.04/hour (whisper-large-v3-turbo)
const GROQ_WHISPER_MIN_BILLABLE_SECONDS = 10;

// xAI Grok STT Pricing (USD)
const XAI_STT_COST_PER_AUDIO_HOUR = 0.10;

// Microsoft MAI-Transcribe 1.5 (Azure Speech) Pricing (USD)
const AZURE_MAI_COST_PER_AUDIO_MINUTE = 0.006;

// Google Cloud Speech-to-Text V2 Chirp 3 Pricing (USD)
const GOOGLE_CHIRP_COST_PER_AUDIO_MINUTE = 0.016;

// ── New HyperWhisper Cloud STT proxy providers (per-model) ──────────────────
// Each provider exposes one or more models with distinct pricing. Token-billed
// providers (OpenAI gpt-4o-*, Gemini) bill from the response `usage` object so
// the charge is exact; duration-billed providers bill from returned audio
// seconds. See shared-app-classification/cloud-stt-catalog.json for the
// client-facing credits/min surface.

// OpenAI — whisper-1 is duration-billed; gpt-4o-* are token-billed.
const OPENAI_WHISPER1_COST_PER_AUDIO_MINUTE = 0.006;
const OPENAI_GPT4O_TRANSCRIBE_INPUT_COST_PER_TOKEN = 2.50 / 1_000_000;
const OPENAI_GPT4O_TRANSCRIBE_OUTPUT_COST_PER_TOKEN = 10.00 / 1_000_000;
const OPENAI_GPT4O_MINI_TRANSCRIBE_INPUT_COST_PER_TOKEN = 1.25 / 1_000_000;
const OPENAI_GPT4O_MINI_TRANSCRIBE_OUTPUT_COST_PER_TOKEN = 5.00 / 1_000_000;
// Per-minute floors used only when the token `usage` object is missing/changed,
// so a gpt-4o transcription never bills $0 (fail-closed).
const OPENAI_GPT4O_TRANSCRIBE_FLOOR_PER_MINUTE = 0.006;
const OPENAI_GPT4O_MINI_TRANSCRIBE_FLOOR_PER_MINUTE = 0.003;

// Gemini — audio billed at 32 tokens/sec; cost read from usageMetadata. Input
// is multi-modal: TEXT (the instruction+vocab prompt) and AUDIO bill at
// DIFFERENT per-token rates on Flash models, so both must be tracked. Pro models
// (2.5-pro, 3.1-pro) bill every input modality at one rate but switch to a
// higher long-context tier above 200k prompt tokens. Rates: Standard paid tier,
// per-1M ÷ 1e6. Ref: https://ai.google.dev/gemini-api/docs/pricing
interface GeminiRate {
  textInputPerToken: number;
  audioInputPerToken: number;
  outputPerToken: number;
  // Override rates for prompts above GEMINI_LONG_CONTEXT_THRESHOLD tokens.
  // Present only for models with tiered (Pro) pricing; flat models omit it.
  longContext?: { textInputPerToken: number; audioInputPerToken: number; outputPerToken: number };
}
const GEMINI_LONG_CONTEXT_THRESHOLD = 200_000;
const M = 1_000_000;
const GEMINI_RATES: Record<string, GeminiRate> = {
  'gemini-2.5-flash': { textInputPerToken: 0.30 / M, audioInputPerToken: 1.00 / M, outputPerToken: 2.50 / M },
  'gemini-2.5-flash-lite': { textInputPerToken: 0.10 / M, audioInputPerToken: 0.30 / M, outputPerToken: 0.40 / M },
  'gemini-2.5-pro': {
    textInputPerToken: 1.25 / M, audioInputPerToken: 1.25 / M, outputPerToken: 10.00 / M,
    longContext: { textInputPerToken: 2.50 / M, audioInputPerToken: 2.50 / M, outputPerToken: 15.00 / M },
  },
  'gemini-3-flash-preview': { textInputPerToken: 0.50 / M, audioInputPerToken: 1.00 / M, outputPerToken: 3.00 / M },
  'gemini-3.1-pro-preview': {
    textInputPerToken: 2.00 / M, audioInputPerToken: 2.00 / M, outputPerToken: 12.00 / M,
    longContext: { textInputPerToken: 4.00 / M, audioInputPerToken: 4.00 / M, outputPerToken: 18.00 / M },
  },
};
const GEMINI_FALLBACK_RATE = GEMINI_RATES['gemini-2.5-flash'];

// AssemblyAI — duration-billed; medical is a +$0.15/hr add-on, not a model.
const ASSEMBLYAI_UNIVERSAL2_COST_PER_AUDIO_MINUTE = 0.15 / 60;       // $0.0025/min
const ASSEMBLYAI_UNIVERSAL3_PRO_COST_PER_AUDIO_MINUTE = 0.21 / 60;   // $0.0035/min
const ASSEMBLYAI_MEDICAL_ADDON_COST_PER_AUDIO_MINUTE = 0.15 / 60;    // +$0.0025/min
const ASSEMBLYAI_KEYTERMS_ADDON_COST_PER_AUDIO_MINUTE = 0.05 / 60;   // +~$0.05/hr (universal-3-pro only)

// Mistral Voxtral — per-audio-minute billing.
const MISTRAL_VOXTRAL_COST_PER_AUDIO_MINUTE = 0.003;

// Soniox — token-billed. The ≈ $0.10/hr async equivalence covers input-AUDIO +
// OUTPUT-text tokens for typical speech; custom-context INPUT-text tokens are
// charged ON TOP at $3.50/1M (async). Soniox bills ~0.3 tokens per character.
// Ref: https://soniox.com/pricing
const SONIOX_COST_PER_AUDIO_MINUTE = 0.10 / 60;                       // ≈ $0.001667/min
const SONIOX_INPUT_TEXT_COST_PER_TOKEN = 3.50 / 1_000_000;            // async context tokens
const SONIOX_TOKENS_PER_CHAR = 0.3;
export function estimateSonioxContextTokens(contextText: string | undefined): number {
  if (!contextText) return 0;
  return Math.ceil(contextText.length * SONIOX_TOKENS_PER_CHAR);
}

// Anthropic Claude Haiku 4.5 Pricing (USD)
const ANTHROPIC_HAIKU_PROMPT_COST_PER_TOKEN = 1.00 / 1_000_000;
const ANTHROPIC_HAIKU_COMPLETION_COST_PER_TOKEN = 5.00 / 1_000_000;
// Prompt caching: writes bill at 1.25x input, reads at 0.10x input (5-minute TTL).
const ANTHROPIC_HAIKU_CACHE_WRITE_COST_PER_TOKEN = ANTHROPIC_HAIKU_PROMPT_COST_PER_TOKEN * 1.25;
const ANTHROPIC_HAIKU_CACHE_READ_COST_PER_TOKEN = ANTHROPIC_HAIKU_PROMPT_COST_PER_TOKEN * 0.10;

// Cerebras GPT-OSS-120B Pricing (USD)
const CEREBRAS_PROMPT_COST_PER_TOKEN = 0.35 / 1_000_000;
const CEREBRAS_COMPLETION_COST_PER_TOKEN = 0.75 / 1_000_000;

// Groq GPT-OSS-120B Pricing (USD)
const GROQ_PROMPT_COST_PER_TOKEN = 0.15 / 1_000_000;
const GROQ_COMPLETION_COST_PER_TOKEN = 0.60 / 1_000_000;

// xAI Grok 4.1 Fast Pricing (USD)
const XAI_GROK_FAST_PROMPT_COST_PER_TOKEN = 1.25 / 1_000_000;
const XAI_GROK_FAST_COMPLETION_COST_PER_TOKEN = 2.50 / 1_000_000;

// ── HyperWhisper Cloud post-processing LLM providers (per-model) ────────────
// Each provider exposes one or more chat models with distinct input/output
// token pricing. These MUST match shared-app-classification/cloud-pp-catalog.json
// (the display source of truth) — they are the ACTUAL billing constants. Rates
// are USD per-1M ÷ 1e6. Re-confirmed against live provider pricing 2026-06-19.
interface LLMChatRate {
  promptPerToken: number;
  completionPerToken: number;
}

// OpenAI GPT-5 family (chat/completions). https://platform.openai.com/docs/pricing
const OPENAI_CHAT_RATES: Record<string, LLMChatRate> = {
  'gpt-5-mini': { promptPerToken: 0.25 / 1_000_000, completionPerToken: 2.00 / 1_000_000 },
  'gpt-5-nano': { promptPerToken: 0.05 / 1_000_000, completionPerToken: 0.40 / 1_000_000 },
};
const OPENAI_DEFAULT_CHAT_MODEL = 'gpt-5-mini';

// Google Gemini (OpenAI-compatible endpoint). https://ai.google.dev/gemini-api/docs/pricing
const GEMINI_CHAT_RATES: Record<string, LLMChatRate> = {
  'gemini-2.5-flash': { promptPerToken: 0.30 / 1_000_000, completionPerToken: 2.50 / 1_000_000 },
  'gemini-2.5-flash-lite': { promptPerToken: 0.10 / 1_000_000, completionPerToken: 0.40 / 1_000_000 },
};
const GEMINI_DEFAULT_CHAT_MODEL = 'gemini-2.5-flash';

// Mistral (chat/completions). https://mistral.ai/pricing + docs.mistral.ai
// `mistral-small-latest` resolves to Mistral Small 4: the model-specific docs
// list $0.15/$0.60, which conflicts with the lower pricing-tile figure — we
// bill at the higher (model-docs) rate, the billing-safe choice. `mistral-nemo`
// is not a valid chat id; the canonical API id is `open-mistral-nemo`.
// Re-confirm both against live provider pages before secrets go live.
const MISTRAL_CHAT_RATES: Record<string, LLMChatRate> = {
  'mistral-small-latest': { promptPerToken: 0.15 / 1_000_000, completionPerToken: 0.60 / 1_000_000 },
  'open-mistral-nemo': { promptPerToken: 0.15 / 1_000_000, completionPerToken: 0.15 / 1_000_000 },
};
const MISTRAL_DEFAULT_CHAT_MODEL = 'mistral-small-latest';

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

export function computeElevenLabsTranscriptionCost(durationSeconds: number, keyterms: boolean = false): number {
  const durationMinutes = durationSeconds / 60;
  const base = durationMinutes * ELEVENLABS_COST_PER_AUDIO_MINUTE;
  // ElevenLabs keyterm prompting (scribe_v2) carries a documented +20% surcharge
  // on the base transcription cost.
  const raw = keyterms ? base * (1 + ELEVENLABS_KEYTERMS_SURCHARGE) : base;
  return roundUsd(raw);
}

export function computeDeepgramTranscriptionCost(durationSeconds: number): number {
  const durationMinutes = durationSeconds / 60;
  const raw = durationMinutes * DEEPGRAM_COST_PER_AUDIO_MINUTE;
  return roundUsd(raw);
}

export function computeGroqTranscriptionCost(durationSeconds: number, model?: string): number {
  const billableSeconds = Math.max(durationSeconds, GROQ_WHISPER_MIN_BILLABLE_SECONDS);
  // whisper-large-v3 bills at the higher rate; everything else (including the
  // default whisper-large-v3-turbo) bills at the turbo rate (~2.8x cheaper).
  const costPerHour = model === 'whisper-large-v3'
    ? GROQ_WHISPER_COST_PER_AUDIO_HOUR
    : GROQ_WHISPER_TURBO_COST_PER_AUDIO_HOUR;
  const raw = (billableSeconds / 3600) * costPerHour;
  return roundUsd(raw);
}

export function computeXaiTranscriptionCost(durationSeconds: number): number {
  const raw = (durationSeconds / 3600) * XAI_STT_COST_PER_AUDIO_HOUR;
  return roundUsd(raw);
}

export function computeAzureMaiTranscriptionCost(durationSeconds: number): number {
  const durationMinutes = durationSeconds / 60;
  const raw = durationMinutes * AZURE_MAI_COST_PER_AUDIO_MINUTE;
  return roundUsd(raw);
}

export function computeGoogleChirpTranscriptionCost(durationSeconds: number): number {
  const durationMinutes = durationSeconds / 60;
  const raw = durationMinutes * GOOGLE_CHIRP_COST_PER_AUDIO_MINUTE;
  return roundUsd(raw);
}

// ── New cloud STT proxy providers ───────────────────────────────────────────

export interface OpenAITranscriptionUsage {
  durationSeconds: number;
  inputTokens?: number;
  outputTokens?: number;
}

/**
 * OpenAI: `whisper-1` bills on audio duration; the `gpt-4o-*` transcribe models
 * bill on tokens from the response `usage` object. The gpt-4o `prompt` text
 * input tokens are billed at the (higher) audio-input rate — they're a handful
 * of tokens, so erring high there is both simpler and conservative.
 */
export function computeOpenAITranscriptionCost(model: string, usage: OpenAITranscriptionUsage): number {
  if (model === 'whisper-1') {
    return roundUsd((usage.durationSeconds / 60) * OPENAI_WHISPER1_COST_PER_AUDIO_MINUTE);
  }

  const inputTokens = Math.max(0, usage.inputTokens ?? 0);
  const outputTokens = Math.max(0, usage.outputTokens ?? 0);
  const isMini = model === 'gpt-4o-mini-transcribe';

  const tokenCost = isMini
    ? inputTokens * OPENAI_GPT4O_MINI_TRANSCRIBE_INPUT_COST_PER_TOKEN
      + outputTokens * OPENAI_GPT4O_MINI_TRANSCRIBE_OUTPUT_COST_PER_TOKEN
    : inputTokens * OPENAI_GPT4O_TRANSCRIBE_INPUT_COST_PER_TOKEN
      + outputTokens * OPENAI_GPT4O_TRANSCRIBE_OUTPUT_COST_PER_TOKEN;

  if (tokenCost > 0) {
    return roundUsd(tokenCost);
  }

  // Missing/changed `usage` schema would otherwise bill $0 — fall back to a
  // duration-based estimate so token-billed models fail closed, mirroring the
  // LLM `estimateUsageFromChars` safety net.
  const floorPerMinute = isMini
    ? OPENAI_GPT4O_MINI_TRANSCRIBE_FLOOR_PER_MINUTE
    : OPENAI_GPT4O_TRANSCRIBE_FLOOR_PER_MINUTE;
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
  const rateDef = GEMINI_RATES[model] ?? GEMINI_FALLBACK_RATE;
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
    const rate = GEMINI_RATES[model ?? ''] ?? GEMINI_FALLBACK_RATE;
    return tokens * rate.textInputPerToken;
  }
  if (provider === 'openai') {
    // whisper-1 is duration-billed — no prompt-token charge. Default + the
    // explicit gpt-4o-transcribe use the (more expensive) transcribe input rate.
    if (model === 'whisper-1') return 0;
    if (model === 'gpt-4o-mini-transcribe') return tokens * OPENAI_GPT4O_MINI_TRANSCRIBE_INPUT_COST_PER_TOKEN;
    return tokens * OPENAI_GPT4O_TRANSCRIBE_INPUT_COST_PER_TOKEN;
  }
  if (provider === 'soniox') {
    // Soniox charges the custom-context terms as async input-text tokens on top
    // of the audio/output blend. Its tokenizer (~0.3 tok/char) differs from the
    // 4-chars/token heuristic above, so use the Soniox-specific estimate.
    return estimateSonioxContextTokens(initialPrompt) * SONIOX_INPUT_TEXT_COST_PER_TOKEN;
  }
  return 0;
}

/**
 * AssemblyAI duration-billed; `medical` layers the +$0.15/hr add-on on top of
 * the chosen base model (universal-2 or universal-3-pro). `keyterms` layers the
 * ~$0.05/hr keyterms-prompt add-on, but ONLY for universal-3-pro — universal-2
 * keyterms are free/beta and must not be charged.
 */
export function computeAssemblyAITranscriptionCost(
  durationSeconds: number,
  model: string,
  medical: boolean = false,
  keyterms: boolean = false,
): number {
  const basePerMinute = model === 'universal-3-pro'
    ? ASSEMBLYAI_UNIVERSAL3_PRO_COST_PER_AUDIO_MINUTE
    : ASSEMBLYAI_UNIVERSAL2_COST_PER_AUDIO_MINUTE;
  const keytermsPerMinute = (keyterms && model === 'universal-3-pro')
    ? ASSEMBLYAI_KEYTERMS_ADDON_COST_PER_AUDIO_MINUTE
    : 0;
  const perMinute = basePerMinute
    + (medical ? ASSEMBLYAI_MEDICAL_ADDON_COST_PER_AUDIO_MINUTE : 0)
    + keytermsPerMinute;
  return roundUsd((durationSeconds / 60) * perMinute);
}

export function computeMistralTranscriptionCost(durationSeconds: number): number {
  return roundUsd((durationSeconds / 60) * MISTRAL_VOXTRAL_COST_PER_AUDIO_MINUTE);
}

export function computeSonioxTranscriptionCost(durationSeconds: number, contextTextTokens: number = 0): number {
  const audioAndOutput = (durationSeconds / 60) * SONIOX_COST_PER_AUDIO_MINUTE;
  const contextCost = Math.max(0, contextTextTokens) * SONIOX_INPUT_TEXT_COST_PER_TOKEN;
  return roundUsd(audioAndOutput + contextCost);
}

// =============================================================================
// LLM COSTS
// =============================================================================

export function computeAnthropicCost(
  inputTokens: number,
  outputTokens: number,
  cacheCreationTokens: number = 0,
  cacheReadTokens: number = 0,
): number {
  // `inputTokens` is the uncached input delta; cache buckets are billed separately.
  const promptCost = inputTokens * ANTHROPIC_HAIKU_PROMPT_COST_PER_TOKEN;
  const completionCost = outputTokens * ANTHROPIC_HAIKU_COMPLETION_COST_PER_TOKEN;
  const cacheWriteCost = cacheCreationTokens * ANTHROPIC_HAIKU_CACHE_WRITE_COST_PER_TOKEN;
  const cacheReadCost = cacheReadTokens * ANTHROPIC_HAIKU_CACHE_READ_COST_PER_TOKEN;
  return roundUsd(promptCost + completionCost + cacheWriteCost + cacheReadCost);
}

export function computeCerebrasChatCost(usage: GroqUsage): number {
  const promptCost = usage.prompt_tokens * CEREBRAS_PROMPT_COST_PER_TOKEN;
  const completionCost = usage.completion_tokens * CEREBRAS_COMPLETION_COST_PER_TOKEN;
  return roundUsd(promptCost + completionCost);
}

export function computeGroqChatCost(usage: GroqUsage): number {
  const promptCost = usage.prompt_tokens * GROQ_PROMPT_COST_PER_TOKEN;
  const completionCost = usage.completion_tokens * GROQ_COMPLETION_COST_PER_TOKEN;
  return roundUsd(promptCost + completionCost);
}

export function computeXaiGrokFastChatCost(usage: GroqUsage): number {
  const promptCost = usage.prompt_tokens * XAI_GROK_FAST_PROMPT_COST_PER_TOKEN;
  const completionCost = usage.completion_tokens * XAI_GROK_FAST_COMPLETION_COST_PER_TOKEN;
  return roundUsd(promptCost + completionCost);
}

// Per-model chat cost for the OpenAI-compatible PP providers. Unknown models
// fall back to the provider default rate so a catalog/header drift never bills
// $0 (fail-closed, matching the duration-based STT floors above).
function computeChatCost(rates: Record<string, LLMChatRate>, defaultModel: string, model: string, usage: GroqUsage): number {
  const rate = rates[model] ?? rates[defaultModel];
  const promptCost = usage.prompt_tokens * rate.promptPerToken;
  const completionCost = usage.completion_tokens * rate.completionPerToken;
  return roundUsd(promptCost + completionCost);
}

export function computeOpenAIChatCost(model: string, usage: GroqUsage): number {
  return computeChatCost(OPENAI_CHAT_RATES, OPENAI_DEFAULT_CHAT_MODEL, model, usage);
}

export function computeGeminiChatCost(model: string, usage: GroqUsage): number {
  return computeChatCost(GEMINI_CHAT_RATES, GEMINI_DEFAULT_CHAT_MODEL, model, usage);
}

export function computeMistralChatCost(model: string, usage: GroqUsage): number {
  return computeChatCost(MISTRAL_CHAT_RATES, MISTRAL_DEFAULT_CHAT_MODEL, model, usage);
}

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

  const usage = value as GroqUsage;
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

  if (USD_PER_CREDIT <= 0) {
    return Math.max(0.1, roundUpToTenth(usd * 1000));
  }

  return usd / USD_PER_CREDIT;
}

export function creditsForCost(costUsd: number): number {
  if (!Number.isFinite(costUsd) || costUsd <= 0) {
    return 0.1;
  }

  const rawCredits = usdToCredits(costUsd);
  return Math.max(0.1, roundUpToTenth(rawCredits));
}

export function estimateCreditsForCost(costUsd: number): number {
  if (!Number.isFinite(costUsd) || costUsd <= 0) {
    return 0;
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
