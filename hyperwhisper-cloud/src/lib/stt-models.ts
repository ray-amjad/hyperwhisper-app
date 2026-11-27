// SERVER-SIDE STT MODEL REGISTRY
// Single source of truth for which (provider, model) pairs HyperWhisper Cloud
// will route to, their preflight credit rate, preview status, and vocabulary
// support. The transcribe route validates every request against this registry
// BEFORE spending any upstream money, so an unknown or spoofed provider/model
// is rejected with a 400 rather than silently falling back to a paid default.
//
// The client-facing catalog (credits/min captions, language lists, accuracy
// tiers) lives in `shared-app-classification/cloud-stt-catalog.json`. This file
// is the backend's narrower, security-critical view: what is actually routable
// and how to meter it.

import {
  GEMINI_2_5_FLASH_LITE_PRICE,
  GEMINI_3_8_FLASH_PRICE,
} from './gemini-prices';

export type SttProviderId =
  | 'deepgram'
  | 'groq'
  | 'elevenlabs'
  | 'grok'
  | 'azure-mai'
  | 'google-chirp'
  | 'openai'
  | 'gemini'
  | 'gemini-transcribe'
  | 'assemblyai'
  | 'mistral'
  | 'soniox'
  | 'meta';

/**
 * How a model is billed. This is the ONE place a model's price lives: actual
 * billing (`cost-calculator.ts`) reads it, and the preflight reservation rate
 * (`estimatedUsdPerMinute`) is derived from it, so the two cannot drift.
 *
 *   * `perMinute` — flat USD per audio minute.
 *   * `perHour`   — flat USD per audio hour, optionally with a minimum billable
 *                   duration per request (Groq bills at least 10 s).
 *   * `tokens`    — billed from the upstream `usage` object at the row's
 *                   per-token rates (the token formulas are in
 *                   `cost-calculator.ts`); the row also authors a reservation
 *                   estimate (`reserveUsdPerMinute`), since no flat rate exists.
 */
export type SttBilling =
  | { kind: 'perMinute'; usdPerMinute: number }
  | { kind: 'perHour'; usdPerHour: number; minBillableSeconds?: number }
  | SttTokenBilling;

/** USD per token, one figure per input modality and one for output. */
export interface SttTokenRates {
  textInputPerToken: number;
  audioInputPerToken: number;
  outputPerToken: number;
}

export interface SttTokenBilling extends SttTokenRates {
  kind: 'tokens';
  /**
   * Override rates for prompts above GEMINI_LONG_CONTEXT_THRESHOLD input tokens
   * (`cost-calculator.ts`). Present only for models with tiered (Pro) pricing;
   * flat models omit it.
   */
  longContext?: SttTokenRates;
  /**
   * Per-minute floor billed only when the token `usage` object is
   * missing/changed, so a token-billed transcription never bills $0
   * (fail-closed). OpenAI rows author it; the Gemini paths derive theirs from
   * the audio-token rate and the audio duration instead.
   */
  missingUsageUsdPerMinute?: number;
}

const M = 1_000_000;

/**
 * Optional add-ons a model meters on top of its base rate. A model without a
 * given field does not charge for that option, in preflight or in billing.
 */
export interface SttAddOns {
  /** Added to the per-minute rate when the request asks for the medical domain. */
  medicalUsdPerMinute?: number;
  /** Added to the per-minute rate when the request carries keyterms. */
  keytermsUsdPerMinute?: number;
  /** Fractional surcharge on the whole rate when the request carries keyterms. */
  keytermsSurcharge?: number;
}

/** The authored half of a model row; `estimatedUsdPerMinute` is derived from it. */
interface SttModelSpec {
  /** Upstream model id. */
  id: string;
  /** Marks a preview/experimental model so clients can badge it. */
  isPreview?: boolean;
  /** Whether the upstream honours custom-vocabulary / keyterm biasing. */
  supportsVocabulary: boolean;
  /** The model's price. See `SttBilling`. */
  billing: SttBilling;
  addOns?: SttAddOns;
  /**
   * Prompt/context TEXT tokens a non-`tokens` row bills ON TOP of `billing`
   * (Soniox's custom context). `tokensPerChar` is the upstream tokenizer's
   * ratio, which differs from the service's 4-chars/token heuristic. A
   * `tokens` row bills its prompt at `billing.textInputPerToken` instead, and
   * a row with neither does not bill prompt text at all.
   */
  contextText?: { usdPerToken: number; tokensPerChar: number };
  /**
   * Preflight USD/min, authored only where it is NOT simply the billing rate:
   * every `tokens` row (no flat rate exists), and a flat row whose reservation
   * is the billing rate rounded UP. Leave it out otherwise.
   */
  reserveUsdPerMinute?: number;
  /**
   * `gemini` rows only (required there, checked at module load): the
   * generationConfig.thinkingConfig `providers/gemini.ts` sends for this model.
   */
  thinking?: { thinkingLevel: 'minimal' | 'low' } | { thinkingBudget: number };
}

export interface SttModelDef extends SttModelSpec {
  /**
   * Conservative USD-per-audio-minute figure used ONLY for the preflight
   * credit reservation. DERIVED from `billing` (or the row's authored
   * `reserveUsdPerMinute`), never authored in the table.
   */
  estimatedUsdPerMinute: number;
}

export interface SttProviderDef {
  id: SttProviderId;
  /**
   * Public label for this provider in the `X-STT-Provider` response header, the
   * deduction metadata and every user-facing error message. Present only where
   * it differs from the id (`grok` is served as `xai-grok`); ask
   * `servedNameFor()` rather than reading it, so a caller never has to know
   * which providers have one.
   */
  servedName?: string;
  /** Model used when the caller omits an explicit model. */
  defaultModel: string;
  /**
   * Ordered providers to attempt for a request that asked for this provider.
   * The first entry is always the provider itself. The original cheap trio
   * (deepgram/groq/elevenlabs) plus grok cascade through alternatives, with
   * ElevenLabs (most expensive) as the last resort; every other provider is a
   * chain of one.
   *
   * This is the single authored source of the fallback policy — `selfOnly`
   * below is derived from it, so the two can never disagree.
   */
  fallbackChain: readonly SttProviderId[];
  /**
   * Self-only providers never fall back to a sibling: the caller picked this
   * model for a reason, so on failure we surface an error rather than silently
   * substituting a different model/price. All new proxy providers are self-only;
   * the original cheap trio (deepgram/groq/elevenlabs) keep cross-fallback.
   *
   * DERIVED from `fallbackChain` — a chain of one has nobody to fall back to.
   * Do not author it in the table below.
   */
  selfOnly: boolean;
  /** Whether this provider's transcription flow is async (upload + poll). */
  async: boolean;
  /** Retired caller-facing ids that are accepted and canonicalized before dispatch. */
  aliases?: Record<string, string>;
  models: SttModelDef[];
}

/** The authored half of a provider entry; `selfOnly` is computed from it. */
type SttProviderSpec = Omit<SttProviderDef, 'selfOnly' | 'models'> & { models: SttModelSpec[] };

// Medical add-on multiplier surface — only AssemblyAI meters it today.
export const MEDICAL_DOMAIN = 'medical';
// AssemblyAI medical is a +$0.15/hr add-on (not a model) on either async tier.
const ASSEMBLYAI_MEDICAL_ADDON_USD_PER_MINUTE = 0.15 / 60;
// AssemblyAI's separate sync product (<=120s clips, single blocking request)
// always runs universal-3-5-pro at its own published rate ("the same rate as
// Universal-3.5 Pro Realtime") — HIGHER than either async tier below and not
// modeled by a `models[]` entry (sync isn't a selectable model; it's a
// routing decision the sync-eligibility gate makes for a request that could
// have asked for either async model). No medical/keyterms add-ons: the sync
// API doesn't support them. The one copy of this rate: actual billing
// (`computeAssemblyAISyncTranscriptionCost`) and the preflight reservation
// (`maxReservationUsdPerMinute` in providers/reservation.ts) both read it, so a
// short clip (sync's target case) cannot deduct more than was reserved.
export const ASSEMBLYAI_SYNC_USD_PER_MINUTE = 0.45 / 60; // $0.0075/min

// OpenAI gpt-4o-* transcribe tiers, token-billed. Named because two rows share
// the mini tier (the retiring id and its dated snapshot). The per-minute floors
// apply only when the token `usage` object is missing/changed.
const GPT4O_TRANSCRIBE_BILLING: SttTokenBilling = {
  kind: 'tokens',
  textInputPerToken: 2.50 / M, audioInputPerToken: 2.50 / M, outputPerToken: 10.00 / M,
  missingUsageUsdPerMinute: 0.006,
};
const GPT4O_MINI_TRANSCRIBE_BILLING: SttTokenBilling = {
  kind: 'tokens',
  textInputPerToken: 1.25 / M, audioInputPerToken: 1.25 / M, outputPerToken: 5.00 / M,
  missingUsageUsdPerMinute: 0.003,
};

const PROVIDER_SPECS: Record<SttProviderId, SttProviderSpec> = {
  // ── Original cheap trio: cross-provider fallback retained ──
  deepgram: {
    id: 'deepgram',
    defaultModel: 'nova-3-general',
    fallbackChain: ['deepgram', 'groq', 'elevenlabs'],
    async: false,
    models: [
      // Nova-3: base $0.0043 + features $0.0012 per audio minute.
      { id: 'nova-3-general', supportsVocabulary: true, billing: { kind: 'perMinute', usdPerMinute: 0.0055 } },
      { id: 'nova-3-medical', supportsVocabulary: true, billing: { kind: 'perMinute', usdPerMinute: 0.0055 } },
      { id: 'nova-2-general', supportsVocabulary: true, billing: { kind: 'perMinute', usdPerMinute: 0.0055 } },
      { id: 'nova-2-medical', supportsVocabulary: true, billing: { kind: 'perMinute', usdPerMinute: 0.0055 } },
    ],
  },
  groq: {
    id: 'groq',
    defaultModel: 'whisper-large-v3-turbo',
    fallbackChain: ['groq', 'deepgram', 'elevenlabs'],
    async: false,
    models: [
      // https://groq.com/pricing — turbo $0.04/hr, large-v3 $0.111/hr, each
      // with a 10 s minimum billable duration per request. Turbo reserves at
      // its per-minute rate rounded up.
      {
        id: 'whisper-large-v3-turbo',
        supportsVocabulary: true,
        billing: { kind: 'perHour', usdPerHour: 0.04, minBillableSeconds: 10 },
        reserveUsdPerMinute: 0.000667,
      },
      {
        id: 'whisper-large-v3',
        supportsVocabulary: true,
        billing: { kind: 'perHour', usdPerHour: 0.111, minBillableSeconds: 10 },
      },
    ],
  },
  elevenlabs: {
    id: 'elevenlabs',
    defaultModel: 'scribe_v2',
    fallbackChain: ['elevenlabs', 'deepgram', 'groq'],
    async: false,
    // scribe_v1 was retired by ElevenLabs on 2026-07-09 (deprecated in favor of
    // scribe_v2 / scribe_v2_realtime — see ElevenLabs changelog 2026-6-8) and is
    // deliberately absent here: this registry is fail-closed, so an explicit
    // request for it now gets rejected with a 400 rather than silently routed.
    models: [
      // Keyterm prompting carries a documented +20% surcharge on base.
      {
        id: 'scribe_v2',
        supportsVocabulary: true,
        billing: { kind: 'perMinute', usdPerMinute: 0.00983 },
        addOns: { keytermsSurcharge: 0.20 },
      },
    ],
  },

  // ── Single-model self-only providers ──
  grok: {
    id: 'grok',
    // The only provider whose public label differs from its id.
    servedName: 'xai-grok',
    // `/v1/stt` took no `model` parameter until 2026-09-19, so this row and the
    // one model below both carried the empty id. A shipped client that sends no
    // `X-STT-Model` — which is every client built before that date — still
    // lands here through `resolveModel`'s empty-request branch.
    defaultModel: 'grok-voice-transcribe-2.0',
    // grok keeps its historical cross-provider fallback chain.
    fallbackChain: ['grok', 'deepgram', 'groq', 'elevenlabs'],
    async: false,
    models: [
      // $0.10 per audio hour; reserves at the per-minute rate rounded up.
      {
        id: 'grok-voice-transcribe-2.0',
        supportsVocabulary: true,
        billing: { kind: 'perHour', usdPerHour: 0.10 },
        reserveUsdPerMinute: 0.00167,
      },
    ],
    // xAI ended `grok-voice-transcribe-1.0` on 2026-10-02 and routes every
    // request for it to 2.0 at the same price, so its row is gone (#1908). A
    // shipped client that still sends it as `X-STT-Model` is served and billed
    // as 2.0 instead of getting a 400. 2.0's rate equals 1.0's ($0.00167/min),
    // so the alias cannot raise a bill. PARITY: hw-catalog GROK_ALIASES, macOS
    // legacyGrokAliases.
    aliases: { 'grok-voice-transcribe-1.0': 'grok-voice-transcribe-2.0' },
  },
  'azure-mai': {
    id: 'azure-mai',
    // What an already-shipped client that sends no `X-STT-Model` gets. Flipped
    // in the same change as the catalog row and every native registry, never
    // ahead of them — moving it alone would migrate those users onto v2 before
    // any client, doc or price table said so.
    defaultModel: 'mai-transcribe-2',
    fallbackChain: ['azure-mai'],
    async: false,
    // Each row reserves at the rate it actually bills (both derive from
    // `billing`). 1.5 bills $0.006/min. v2 is published at $0.10 per hour of
    // audio (≈ $0.001667/min), which Microsoft describes as a LIMITED-TIME OFFER
    // running until the end of the year — revisit this rate, and the 1.67
    // credits/min it backs in cloud-stt-catalog.json, when the offer ends.
    // Refs: https://microsoft.ai/models/mai-transcribe-2/
    //       https://microsoft.ai/news/mai-transcribe-2-is-the-fastest-most-accurate-and-cheapest-speech-recognition-model-in-the-world/
    //
    // v2 used to reserve at 1.5's 0.006 as insurance against the offer ending.
    // That bought nothing (the charge would have been wrong regardless) and cost
    // something real: `validateCredits` REFUSES when the balance is under the
    // estimate, so a user with 15 credits was turned away from a 4-minute
    // request that bills ~6.7 — at the 1.67 credits/min the catalog and the docs
    // advertise for this exact model. Fail-closed for an UNKNOWN model id is now
    // `dearestUsdPerMinute`, which does not need this array in any order.
    models: [
      { id: 'mai-transcribe-1.5', supportsVocabulary: true, billing: { kind: 'perMinute', usdPerMinute: 0.006 } },
      { id: 'mai-transcribe-2', supportsVocabulary: true, billing: { kind: 'perMinute', usdPerMinute: 0.10 / 60 } },
    ],
  },
  'google-chirp': {
    id: 'google-chirp',
    defaultModel: 'chirp_3',
    fallbackChain: ['google-chirp'],
    async: true,
    // Google Cloud Speech-to-Text V2 Chirp 3.
    models: [{ id: 'chirp_3', supportsVocabulary: false, billing: { kind: 'perMinute', usdPerMinute: 0.016 } }],
  },
  meta: {
    id: 'meta',
    defaultModel: 'muse-voice-transcribe-1.0',
    fallbackChain: ['meta'],
    async: false,
    models: [
      // $3 per 1,000 audio minutes.
      { id: 'muse-voice-transcribe-1.0', supportsVocabulary: true, billing: { kind: 'perMinute', usdPerMinute: 0.003 } },
    ],
  },

  // ── New synchronous proxy providers ──
  openai: {
    id: 'openai',
    // gpt-4o-transcribe until 2026-09-11. OpenAI deprecated it on 2026-08-26
    // (shutdown 2027-02-26) and names gpt-transcribe as the replacement, so the
    // cloud STT default for OpenAI must not stay on it. gpt-transcribe is also
    // cheaper here ($0.0045/min flat vs $0.009/min token-billed).
    defaultModel: 'gpt-transcribe',
    fallbackChain: ['openai'],
    async: false,
    // gpt-4o-* are token-billed (input audio + OUTPUT transcript tokens), so the
    // preflight rate adds a conservative output allowance on top of the input
    // floor: ~300 output tokens/min (fast speech ≈ 200 wpm × ~1.3 tok/word, plus
    // headroom) at the model output rate — $10/1M for transcribe (+$0.003/min),
    // $5/1M for mini (+$0.0015/min) — so a verbose transcript can't out-bill the
    // reservation. whisper-1 is duration-billed (no output-token charge).
    //
    // gpt-transcribe / gpt-live-transcribe (launched 2026-07-29) are flat
    // per-audio-minute billed like whisper-1 — NOT token-billed like gpt-4o-* —
    // so their estimatedUsdPerMinute is the vendor's published rate directly,
    // no output-token surcharge added. Verified against OpenAI's pricing docs:
    // gpt-transcribe $0.0045/min, gpt-live-transcribe $0.017/min. Note:
    // gpt-live-transcribe's only supported endpoint is a Realtime WebSocket
    // transcription session — the synchronous `providers/openai.ts` adapter in
    // this backend posts to `v1/audio/transcriptions`, which does NOT serve
    // this model, so it is registered here (routable per this file's validation
    // contract) but not yet actually wired end-to-end; see
    // `shared-models/models-catalog.json`'s note on this entry.
    //
    // OpenAI deprecated whisper-1, gpt-4o-transcribe and gpt-4o-mini-transcribe
    // on 2026-08-26 (shutdown 2027-02-26). This map mirrors the shared Rust
    // core's `OPENAI_ALIASES` (shared-core-rs/crates/hw-catalog/src/
    // model_alias.rs) so a cloud request for a retiring id is served by its
    // named replacement instead of reaching OpenAI. Every target must be a row
    // in `models` below (pinned by a test). The three retiring rows stay so
    // nothing fails closed if this map is ever edited; with the map in place no
    // request resolves to them.
    aliases: {
      'whisper-1': 'gpt-transcribe',
      'gpt-4o-transcribe': 'gpt-transcribe',
      'gpt-4o-mini-transcribe': 'gpt-4o-mini-transcribe-2025-12-15',
    },
    //
    // The gpt-4o-* `prompt` text input tokens bill at the (higher) audio-input
    // rate — they're a handful of tokens, so erring high there is both simpler
    // and conservative: those rows author one input rate for both modalities.
    models: [
      { id: 'gpt-4o-transcribe', supportsVocabulary: true, billing: GPT4O_TRANSCRIBE_BILLING, reserveUsdPerMinute: 0.009 },
      { id: 'gpt-4o-mini-transcribe', supportsVocabulary: true, billing: GPT4O_MINI_TRANSCRIBE_BILLING, reserveUsdPerMinute: 0.0045 },
      { id: 'whisper-1', supportsVocabulary: true, billing: { kind: 'perMinute', usdPerMinute: 0.006 } },
      { id: 'gpt-transcribe', supportsVocabulary: true, billing: { kind: 'perMinute', usdPerMinute: 0.0045 } },
      { id: 'gpt-live-transcribe', supportsVocabulary: true, billing: { kind: 'perMinute', usdPerMinute: 0.017 } },
      // The dated snapshot OpenAI names as the mini tier's replacement. It is
      // token-billed at the gpt-4o-mini-transcribe rates, so it carries that
      // row's billing and reservation rate: the alias cannot make a mode dearer,
      // and it cannot fall through to the dearer gpt-4o-transcribe rates.
      { id: 'gpt-4o-mini-transcribe-2025-12-15', supportsVocabulary: true, billing: GPT4O_MINI_TRANSCRIBE_BILLING, reserveUsdPerMinute: 0.0045 },
    ],
  },
  gemini: {
    id: 'gemini',
    // #1019: follows cloud-stt-catalog.json's gemini isDefault row, so a client
    // that sends no X-STT-Model gets the same model as one that sends the
    // catalog default. Google limits gemini-2.5-flash to past users now.
    // stt-models.test.ts reads the catalog and pins the two together.
    defaultModel: 'gemini-3.8-flash',
    fallbackChain: ['gemini'],
    async: false,
    // gemini-3-flash-preview left the pickers in 2026-10. A client still sending
    // it is served the catalog default at the same 3.0 credits/min, never a 400.
    // The native aliases (hw-catalog GEMINI_ALIASES, macOS, Windows, shared-dotnet)
    // target the same id, so the Cloud and BYOK routes cannot diverge.
    //
    // Google deprecated gemini-2.5-pro on 2026-11-02 and auto-routes it to 3.8
    // Flash from 2026-12-01, billed at 3.8 Flash's rates (#3009). Its row is gone,
    // so a client still sending it is served AND billed as 3.8 Flash (reserved
    // 0.0030/min, below 2.5 Pro's 0.0075), never a 400. PARITY: hw-catalog
    // GEMINI_ALIASES, macOS legacyGeminiAliases.
    // gemini-2.5-flash was deprecated the same day. Ray chose 3.8 Flash for it
    // too (#3009, 2026-11-25), at 3.8 Flash's existing rate: a mode on 2.5 Flash
    // now reserves 0.0030/min instead of 0.0024 — a decided increase, not drift.
    aliases: {
      'gemini-3-flash-preview': 'gemini-3.8-flash',
      'gemini-2.5-pro': 'gemini-3.8-flash',
      'gemini-2.5-flash': 'gemini-3.8-flash',
    },
    // No dedicated vocabulary API — prompt-only biasing, so supportsVocabulary
    // is false (clients shouldn't promise keyterm accuracy).
    //
    // Audio bills at 32 tokens/sec; cost is read from usageMetadata. Input is
    // multi-modal: TEXT (the instruction+vocab prompt) and AUDIO bill at
    // DIFFERENT per-token rates on Flash models, so both are authored. Pro models
    // (3.1-pro) bill every input modality at one rate but switch to a
    // higher long-context tier above 200k prompt tokens. The prices the
    // /post-process LLM rows bill too come from gemini-prices.ts. Rates:
    // Standard paid tier, per-1M ÷ 1e6. Ref: https://ai.google.dev/gemini-api/docs/pricing
    //
    // `thinking` is the generationConfig.thinkingConfig the adapter sends: keep
    // thinking as low as each model allows (only 2.5 Flash Lite can zero
    // it). Every row here must author it, because a wrong guess is a 400:
    // thinkingBudget 0 is invalid on Pro, and 3.8 Flash has no 'minimal' level.
    models: [
      // #1019: the clients' Gemini default (cloud-stt-catalog.json isDefault). A
      // HyperWhisper Cloud mode on the gemini tier with no model chosen sends it as
      // X-STT-Model, so it MUST be listed here or resolveModel 400s that mode.
      // Rate: $0.75/1M input (audio included) + $3.75/1M output, introductory until
      // 2026-12-31, then $1.50/$7.50. ~1,920 audio tok/min + ~200 out tok/min is
      // ~$0.0022/min now and ~$0.0044/min in 2027; reserved at 0.0030, the same
      // figure as the catalog's 3.0 credits/min.
      // 3.8 Flash accepts low/medium/high only: no 'minimal', and thinking cannot be
      // turned off (https://ai.google.dev/gemini-api/docs/thinking, read 2026-10-07).
      // Sending the gemini-3 'minimal' would be refused, so pin its floor.
      {
        id: 'gemini-3.8-flash', supportsVocabulary: false,
        billing: { kind: 'tokens', ...GEMINI_3_8_FLASH_PRICE },
        reserveUsdPerMinute: 0.0030, thinking: { thinkingLevel: 'low' },
      },
      {
        id: 'gemini-2.5-flash-lite', supportsVocabulary: false,
        billing: { kind: 'tokens', ...GEMINI_2_5_FLASH_LITE_PRICE },
        reserveUsdPerMinute: 0.0008, thinking: { thinkingBudget: 0 },
      },
      {
        id: 'gemini-3.1-pro-preview', isPreview: true, supportsVocabulary: false,
        billing: {
          kind: 'tokens',
          textInputPerToken: 2.00 / M, audioInputPerToken: 2.00 / M, outputPerToken: 12.00 / M,
          longContext: { textInputPerToken: 4.00 / M, audioInputPerToken: 4.00 / M, outputPerToken: 18.00 / M },
        },
        reserveUsdPerMinute: 0.0100, thinking: { thinkingLevel: 'low' },
      },
    ],
  },
  // Google's DEDICATED speech models, on /v1beta/interactions — a different
  // upstream product from the `gemini` entry above (which is the multimodal LLM
  // on :generateContent) and deliberately a separate provider id, not extra
  // model rows on it: :generateContent accepts these ids, bills the audio and
  // returns an empty transcript. See providers/gemini-transcribe.ts (TRAP 1).
  //
  // Unlike the LLM entry, these have a real `custom_vocabulary` field, so
  // supportsVocabulary is true.
  //
  // `gemini-3.5-transcribe-live` is registered so a client naming it gets a
  // model-appropriate answer rather than the pre-recorded model's price, but it
  // is served by the WebSocket route, NOT by /transcribe — the HTTP adapter
  // rejects it with a 400 naming the reason.
  'gemini-transcribe': {
    id: 'gemini-transcribe',
    defaultModel: 'gemini-3.5-transcribe',
    fallbackChain: ['gemini-transcribe'],
    async: false,
    // $2.00/1M in at 1,500 audio tokens/min + ~$12.00/1M out on an estimated
    // ~190 output tokens/min of speech ⇒ ~$0.0053/min; ~$0.0092/min live,
    // rounded up a touch below.
    //
    // These are ~150 wpm figures and a fast talker DOES out-bill them: the
    // output half tracks words, not seconds, so 250 wpm bills roughly 8.4
    // credits/min pre-recorded and 14.7 live against the 5.5 / 9.6 here. That
    // is what `estimatedUsdPerMinute` is for — a preflight sizing estimate, not
    // a cap. The cap is enforced where the money moves: the live route prices
    // its mid-session cutoff with the transcript seen so far and clamps the
    // final deduction to the balance seen at auth (`ws-streaming-shared.ts`).
    //
    // One input price for every modality — audio and text bill the same here.
    // A DIFFERENT product from the `gemini` rates above; see the billing block
    // for these models in cost-calculator.ts (25 tok/s audio, output estimated).
    models: [
      {
        id: 'gemini-3.5-transcribe', isPreview: true, supportsVocabulary: true,
        billing: { kind: 'tokens', textInputPerToken: 2.00 / M, audioInputPerToken: 2.00 / M, outputPerToken: 12.00 / M },
        reserveUsdPerMinute: 0.0055,
      },
      {
        id: 'gemini-3.5-transcribe-live', isPreview: true, supportsVocabulary: true,
        billing: { kind: 'tokens', textInputPerToken: 3.50 / M, audioInputPerToken: 3.50 / M, outputPerToken: 21.00 / M },
        reserveUsdPerMinute: 0.0096,
      },
    ],
  },
  mistral: {
    id: 'mistral',
    defaultModel: 'voxtral-mini-latest',
    fallbackChain: ['mistral'],
    async: false,
    models: [
      { id: 'voxtral-mini-latest', supportsVocabulary: true, billing: { kind: 'perMinute', usdPerMinute: 0.003 } },
    ],
  },

  // ── New asynchronous (upload + poll) proxy providers ──
  assemblyai: {
    id: 'assemblyai',
    // Universal-3.5 Pro is the canonical successor. Old Pro callers are
    // redirected instead of forwarding an id that now errors upstream.
    defaultModel: 'universal-3-5-pro',
    fallbackChain: ['assemblyai'],
    async: true,
    aliases: {
      'universal-3-pro': 'universal-3-5-pro',
      'slam-1': 'universal-3-5-pro',
    },
    models: [
      // Universal-3.5 Pro (GA 2026-07-01, now the default) publishes the same
      // $0.21/hr pre-recorded rate as its Universal-3 Pro predecessor —
      // confirmed unchanged. The keyterms add-on (~$0.05/hr) applies only to
      // this Pro tier; universal-2 keyterms are free/beta and must not be
      // charged.
      {
        id: 'universal-3-5-pro',
        supportsVocabulary: true,
        billing: { kind: 'perMinute', usdPerMinute: 0.21 / 60 },
        addOns: { medicalUsdPerMinute: ASSEMBLYAI_MEDICAL_ADDON_USD_PER_MINUTE, keytermsUsdPerMinute: 0.05 / 60 },
      },
      {
        id: 'universal-2',
        supportsVocabulary: true,
        billing: { kind: 'perMinute', usdPerMinute: 0.15 / 60 },
        addOns: { medicalUsdPerMinute: ASSEMBLYAI_MEDICAL_ADDON_USD_PER_MINUTE },
      },
      // A separate product with no add-ons.
      { id: 'dictation', supportsVocabulary: true, billing: { kind: 'perMinute', usdPerMinute: 0.62 / 60 } },
    ],
  },
  soniox: {
    id: 'soniox',
    // Soniox removes stt-async-v5 on 2026-12-15 and routes it to v6, "fully
    // compatible" (#3030). v4 and v5 are compatibility aliases for older callers,
    // both one hop to v6. v6 keeps v5's rate below (Ray, #3030: Soniox publishes
    // no separate v6 price; flag it if one appears). PARITY: hw-catalog
    // SONIOX_ALIASES.
    defaultModel: 'stt-async-v6',
    fallbackChain: ['soniox'],
    async: true,
    aliases: { 'stt-async-v4': 'stt-async-v6', 'stt-async-v5': 'stt-async-v6' },
    models: [
      // Token-billed upstream, but the ≈ $0.10/hr async equivalence covers
      // input-AUDIO + OUTPUT-text tokens for typical speech, so it bills as a
      // flat rate; custom-context input-text tokens are charged ON TOP (see
      // `computeSonioxTranscriptionCost`). Ref: https://soniox.com/pricing.
      // Reserves at the per-minute rate rounded up.
      {
        id: 'stt-async-v6',
        supportsVocabulary: true,
        billing: { kind: 'perMinute', usdPerMinute: 0.10 / 60 },
        reserveUsdPerMinute: 0.00167,
        // Custom-context input-text tokens at $3.50/1M (async); Soniox bills
        // ~0.3 tokens per character.
        contextText: { usdPerToken: 3.50 / M, tokensPerChar: 0.3 },
      },
    ],
  },
};

// `selfOnly` is computed, never authored: it used to be a hand-written flag here
// while the actual chains lived in routes/transcribe.ts, so nothing stopped the
// two from disagreeing — and the flag was documentation only, since the route
// re-derived self-only-ness from its own chain length. Deriving it from the one
// authored chain makes that class of drift unrepresentable.
//
// The callback's return type is annotated on purpose. Without it the trailing
// `as` would happily accept an entry that never added `selfOnly` at all — the
// spec type is a supertype of the def, so tsc reads the assertion as a legal
// narrowing — and `isSelfOnly()` would return undefined for every provider,
// turning every self-only 502 into a misleading 429. With the annotation that
// is a compile error.
const PROVIDERS: Record<SttProviderId, SttProviderDef> = Object.fromEntries(
  Object.entries(PROVIDER_SPECS).map(([id, spec]): [SttProviderId, SttProviderDef] => [
    id as SttProviderId,
    { ...spec, selfOnly: spec.fallbackChain.length === 1, models: spec.models.map(deriveModelDef) },
  ]),
) as Record<SttProviderId, SttProviderDef>;

// A gemini row with no `thinking` would have to guess one, and the old guess
// (thinkingBudget 0) is a 400 on Pro models. Fail at module load instead.
for (const model of PROVIDERS.gemini.models) {
  if (!model.thinking) {
    throw new Error(`Gemini STT model ${model.id} authors no thinking config`);
  }
}

/**
 * The preflight rate is computed from the row's price, the same way `selfOnly`
 * is computed from the chain. A `tokens` row has no flat rate, so it must
 * author `reserveUsdPerMinute`; a row that forgets fails at module load.
 */
function deriveModelDef(spec: SttModelSpec): SttModelDef {
  const { billing } = spec;
  const flat = billing.kind === 'perMinute'
    ? billing.usdPerMinute
    : billing.kind === 'perHour' ? billing.usdPerHour / 60 : undefined;
  const estimated = spec.reserveUsdPerMinute ?? flat;
  if (estimated === undefined) {
    throw new Error(`STT model ${spec.id} is token-billed and authors no reserveUsdPerMinute`);
  }
  return { ...spec, estimatedUsdPerMinute: estimated };
}

const PROVIDER_IDS = new Set<string>(Object.keys(PROVIDERS));

// Canonical runtime list of every routable provider. Derived from the registry
// so it can never drift from it. The deploy smoke test imports this to assert it
// exercises every provider — a new provider added above can't ship untested.
export const ALL_STT_PROVIDER_IDS = Object.keys(PROVIDERS) as SttProviderId[];

export function isValidProviderId(value: string): value is SttProviderId {
  return PROVIDER_IDS.has(value);
}

export function getProviderDef(provider: SttProviderId): SttProviderDef {
  return PROVIDERS[provider];
}

/**
 * The ordered providers to attempt for a request that asked for `provider`.
 * The first entry is always `provider` itself.
 *
 * Returns a fresh array, so a caller may filter or reorder its own copy (the
 * transcribe route drops ElevenLabs when the request landed in a region where
 * ElevenLabs is geo-blocked) without editing the registry for every later
 * request on the machine.
 */
export function fallbackChainFor(provider: SttProviderId): SttProviderId[] {
  return [...PROVIDERS[provider].fallbackChain];
}

/**
 * True when `provider` has no sibling to fall back to. Ask this rather than
 * measuring the length of a chain you hold: a caller-filtered chain can be
 * short for reasons that have nothing to do with the provider's policy.
 */
export function isSelfOnly(provider: SttProviderId): boolean {
  return PROVIDERS[provider].selfOnly;
}

/**
 * The public label for `provider` — what callers see in the `X-STT-Provider`
 * header, the deduction metadata and the user-facing error messages. Equal to
 * the provider id except where the registry authors a `servedName` (`grok` →
 * `xai-grok`).
 *
 * Ask this rather than keeping a label table of your own. The route and the
 * deploy smoke test each held one, so a provider whose label is not its id was
 * a rule two files had to remember separately.
 */
export function servedNameFor(provider: SttProviderId): string {
  return PROVIDERS[provider].servedName ?? provider;
}

/**
 * The served `provider/model` pair for the response header and the metering
 * row, e.g. `deepgram/nova-3-medical`, `xai-grok/grok-voice-transcribe-2.0`.
 * An empty `model` leaves the label standing alone. No registry row answers an
 * empty model any more — grok was the last, until xAI gave `/v1/stt` a `model`
 * parameter on 2026-09-19 — but a caller can still pass one.
 */
export function formatProviderName(provider: SttProviderId, model: string): string {
  const base = servedNameFor(provider);
  return model ? `${base}/${model}` : base;
}

export type ModelResolution =
  | { ok: true; model: SttModelDef }
  | { ok: false; reason: string; validModels: string[] };

/**
 * Resolve and validate a caller-supplied model against a provider. An empty /
 * undefined request resolves to the provider default. An unrecognised model is
 * rejected (fail-closed) — the route turns this into a 400.
 */
export function resolveModel(provider: SttProviderId, requested?: string): ModelResolution {
  const def = PROVIDERS[provider];
  const validModels = def.models.map((m) => m.id);
  const trimmed = (requested ?? '').trim();

  // No model supplied → provider default.
  if (trimmed.length === 0) {
    const fallback = def.models.find((m) => m.id === def.defaultModel) ?? def.models[0];
    return { ok: true, model: fallback };
  }

  const canonical = def.aliases?.[trimmed] ?? trimmed;
  const match = def.models.find((m) => m.id === canonical);
  if (match) {
    return { ok: true, model: match };
  }

  return {
    ok: false,
    reason: `Model "${trimmed}" is not available for provider "${provider}"`,
    validModels,
  };
}

/**
 * The registry row whose id is exactly `model` — no alias or default applied.
 * Billing asks this for the id the upstream actually ran; a caller-supplied id
 * goes through `resolveModel` instead.
 */
export function findModelRow(provider: SttProviderId, model: string | undefined): SttModelDef | undefined {
  return PROVIDERS[provider].models.find((m) => m.id === model);
}

/** The registry row for `provider`'s default model. */
export function defaultModelRow(provider: SttProviderId): SttModelDef {
  const def = PROVIDERS[provider];
  return def.models.find((m) => m.id === def.defaultModel) ?? def.models[0];
}

/**
 * The dearest rate any of a provider's models can bill at.
 *
 * The fail-closed answer for a model id `resolveModel` could not resolve: hold
 * enough for the worst case, because we do not yet know what will run. This
 * used to read `models[0]`, which made ARRAY ORDER a billing invariant — every
 * multi-model provider had to keep its dearest model first or a bad model
 * string under-reserved, and an under-reservation lets a request deduct more
 * than was held for it. Taking the maximum states the property directly, so a
 * model row can be added or reordered without reading this function first.
 */
export function dearestModel(provider: SttProviderId): SttModelDef {
  return PROVIDERS[provider].models.reduce(
    (dearest, model) => (model.estimatedUsdPerMinute > dearest.estimatedUsdPerMinute ? model : dearest),
  );
}

/**
 * The token rates to bill `model` at on a token-billed path: its own row's,
 * or — for an id with no token-billed row — the dearest token-billed row of
 * the provider, the same fail-closed rule as `dearestModel`, so a model string
 * we don't recognise cannot under-bill. Throws for a provider with no
 * token-billed rows (a programming error, not a request error).
 */
export function tokenBillingFor(provider: SttProviderId, model: string | undefined): SttTokenBilling {
  const own = findModelRow(provider, model)?.billing;
  if (own?.kind === 'tokens') return own;
  const tokenRows = PROVIDERS[provider].models.filter((m) => m.billing.kind === 'tokens');
  if (tokenRows.length === 0) {
    throw new Error(`STT provider ${provider} has no token-billed model`);
  }
  const dearest = tokenRows.reduce(
    (best, m) => (m.estimatedUsdPerMinute > best.estimatedUsdPerMinute ? m : best),
  );
  return dearest.billing as SttTokenBilling;
}

/**
 * Preflight USD/min for a (provider, model), including the add-ons the row
 * meters (medical, keyterms). Used only for the credit reservation; actual cost
 * is computed from the upstream response in the adapter, from the same row.
 */
export function estimatedUsdPerMinute(
  provider: SttProviderId,
  model?: string,
  medical: boolean = false,
  keyterms: boolean = false,
): number {
  const resolution = resolveModel(provider, model);
  // One row answers both the rate and the add-on question, so the two can never
  // describe different models on the unresolved path.
  const priced = resolution.ok ? resolution.model : dearestModel(provider);
  return applyKeytermsSurcharge(
    withAdditiveAddOns(priced.estimatedUsdPerMinute, priced.addOns, medical, keyterms),
    priced.addOns,
    keyterms,
  );
}

// The one copy of the add-on rules. The preflight above and actual billing in
// `cost-calculator.ts` both apply them, in this order: the per-minute add-ons
// join the rate, then the keyterms surcharge scales the amount. They are two
// functions because billing scales the AMOUNT (minutes × rate), not the rate.

/** `usdPerMinute` plus the per-minute add-ons the row meters for this request. */
export function withAdditiveAddOns(
  usdPerMinute: number,
  addOns: SttAddOns | undefined,
  medical: boolean,
  keyterms: boolean,
): number {
  return usdPerMinute
    + (medical ? addOns?.medicalUsdPerMinute ?? 0 : 0)
    + (keyterms ? addOns?.keytermsUsdPerMinute ?? 0 : 0);
}

/** `amount` with the row's fractional keyterms surcharge, when it has one. */
export function applyKeytermsSurcharge(amount: number, addOns: SttAddOns | undefined, keyterms: boolean): number {
  const surcharge = keyterms ? addOns?.keytermsSurcharge : undefined;
  return surcharge === undefined ? amount : amount * (1 + surcharge);
}
