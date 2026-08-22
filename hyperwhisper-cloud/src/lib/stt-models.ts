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

import { ASSEMBLYAI_SYNC_COST_PER_AUDIO_MINUTE } from './cost-calculator';

export type SttProviderId =
  | 'deepgram'
  | 'groq'
  | 'elevenlabs'
  | 'grok'
  | 'azure-mai'
  | 'google-chirp'
  | 'openai'
  | 'gemini'
  | 'assemblyai'
  | 'mistral'
  | 'soniox';

export interface SttModelDef {
  /** Upstream model id. Empty string for single-model providers (grok). */
  id: string;
  /** Marks a preview/experimental model so clients can badge it. */
  isPreview?: boolean;
  /** Whether the upstream honours custom-vocabulary / keyterm biasing. */
  supportsVocabulary: boolean;
  /**
   * Conservative USD-per-audio-minute figure used ONLY for the preflight
   * credit reservation. Actual billing is computed from the upstream response
   * (token usage or returned audio seconds) in the provider adapter, so this
   * just has to be a safe upper-ish bound to gate low-balance abuse.
   */
  estimatedUsdPerMinute: number;
}

export interface SttProviderDef {
  id: SttProviderId;
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
type SttProviderSpec = Omit<SttProviderDef, 'selfOnly'>;

// Medical add-on multiplier surface — only AssemblyAI meters it today.
export const MEDICAL_DOMAIN = 'medical';
const ASSEMBLYAI_MEDICAL_ADDON_USD_PER_MINUTE = 0.15 / 60;
// Keyterms add-on ($0.05/hr) applies only to the Universal-3.5 Pro tier;
// universal-2 is free/beta.
const ASSEMBLYAI_KEYTERMS_ADDON_USD_PER_MINUTE = 0.05 / 60;
// AssemblyAI's separate sync product (<=120s clips, single blocking request)
// always runs universal-3-5-pro at its own published rate ("the same rate as
// Universal-3.5 Pro Realtime") — HIGHER than either async tier below and not
// modeled by a `models[]` entry (sync isn't a selectable model; it's a
// routing decision the sync-eligibility gate makes for a request that could
// have asked for either async model). Exported so the preflight reservation
// (estimateCreditsForProviderFallbacks in routes/transcribe.ts) can reserve
// against it for a request that could route through sync, instead of only
// ever reserving the lower async catalog rate for the requested model — a
// short clip (sync's target case) must not be able to deduct more than was
// reserved. Re-exports cost-calculator.ts's `ASSEMBLYAI_SYNC_COST_PER_AUDIO_MINUTE`
// (the single source of truth for this rate, used for ACTUAL billing) instead
// of a second hardcoded copy of the same literal, so the reserved amount can
// never silently drift from what's actually billed.
export const ASSEMBLYAI_SYNC_ESTIMATED_USD_PER_MINUTE = ASSEMBLYAI_SYNC_COST_PER_AUDIO_MINUTE;
// ElevenLabs keyterm prompting carries a +20% surcharge on base (scribe_v2 only).
const ELEVENLABS_KEYTERMS_SURCHARGE = 0.20;

const PROVIDER_SPECS: Record<SttProviderId, SttProviderSpec> = {
  // ── Original cheap trio: cross-provider fallback retained ──
  deepgram: {
    id: 'deepgram',
    defaultModel: 'nova-3-general',
    fallbackChain: ['deepgram', 'groq', 'elevenlabs'],
    async: false,
    models: [
      { id: 'nova-3-general', supportsVocabulary: true, estimatedUsdPerMinute: 0.0055 },
      { id: 'nova-3-medical', supportsVocabulary: true, estimatedUsdPerMinute: 0.0055 },
      { id: 'nova-2-general', supportsVocabulary: true, estimatedUsdPerMinute: 0.0055 },
      { id: 'nova-2-medical', supportsVocabulary: true, estimatedUsdPerMinute: 0.0055 },
    ],
  },
  groq: {
    id: 'groq',
    defaultModel: 'whisper-large-v3-turbo',
    fallbackChain: ['groq', 'deepgram', 'elevenlabs'],
    async: false,
    models: [
      { id: 'whisper-large-v3-turbo', supportsVocabulary: true, estimatedUsdPerMinute: 0.000667 }, // $0.04/hr ÷ 60
      { id: 'whisper-large-v3', supportsVocabulary: true, estimatedUsdPerMinute: 0.00185 },      // $0.111/hr ÷ 60
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
      { id: 'scribe_v2', supportsVocabulary: true, estimatedUsdPerMinute: 0.00983 },
    ],
  },

  // ── Single-model self-only providers ──
  grok: {
    id: 'grok',
    defaultModel: '',
    // grok keeps its historical cross-provider fallback chain.
    fallbackChain: ['grok', 'deepgram', 'groq', 'elevenlabs'],
    async: false,
    models: [{ id: '', supportsVocabulary: true, estimatedUsdPerMinute: 0.00167 }],
  },
  'azure-mai': {
    id: 'azure-mai',
    defaultModel: 'mai-transcribe-1.5',
    fallbackChain: ['azure-mai'],
    async: false,
    models: [{ id: 'mai-transcribe-1.5', supportsVocabulary: true, estimatedUsdPerMinute: 0.006 }],
  },
  'google-chirp': {
    id: 'google-chirp',
    defaultModel: 'chirp_3',
    fallbackChain: ['google-chirp'],
    async: true,
    models: [{ id: 'chirp_3', supportsVocabulary: false, estimatedUsdPerMinute: 0.016 }],
  },

  // ── New synchronous proxy providers ──
  openai: {
    id: 'openai',
    defaultModel: 'gpt-4o-transcribe',
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
    models: [
      { id: 'gpt-4o-transcribe', supportsVocabulary: true, estimatedUsdPerMinute: 0.009 },
      { id: 'gpt-4o-mini-transcribe', supportsVocabulary: true, estimatedUsdPerMinute: 0.0045 },
      { id: 'whisper-1', supportsVocabulary: true, estimatedUsdPerMinute: 0.006 },
      { id: 'gpt-transcribe', supportsVocabulary: true, estimatedUsdPerMinute: 0.0045 },
      { id: 'gpt-live-transcribe', supportsVocabulary: true, estimatedUsdPerMinute: 0.017 },
    ],
  },
  gemini: {
    id: 'gemini',
    defaultModel: 'gemini-2.5-flash',
    fallbackChain: ['gemini'],
    async: false,
    // No dedicated vocabulary API — prompt-only biasing, so supportsVocabulary
    // is false (clients shouldn't promise keyterm accuracy).
    models: [
      { id: 'gemini-2.5-flash', supportsVocabulary: false, estimatedUsdPerMinute: 0.0024 },
      { id: 'gemini-2.5-flash-lite', supportsVocabulary: false, estimatedUsdPerMinute: 0.0008 },
      { id: 'gemini-2.5-pro', supportsVocabulary: false, estimatedUsdPerMinute: 0.0075 },
      { id: 'gemini-3-flash-preview', isPreview: true, supportsVocabulary: false, estimatedUsdPerMinute: 0.0030 },
      { id: 'gemini-3.1-pro-preview', isPreview: true, supportsVocabulary: false, estimatedUsdPerMinute: 0.0100 },
    ],
  },
  mistral: {
    id: 'mistral',
    defaultModel: 'voxtral-mini-latest',
    fallbackChain: ['mistral'],
    async: false,
    models: [
      { id: 'voxtral-mini-latest', supportsVocabulary: true, estimatedUsdPerMinute: 0.003 },
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
      { id: 'universal-3-5-pro', supportsVocabulary: true, estimatedUsdPerMinute: 0.0035 },
      { id: 'universal-2', supportsVocabulary: true, estimatedUsdPerMinute: 0.0025 },
    ],
  },
  soniox: {
    id: 'soniox',
    // v4 is retained only as a compatibility alias for older callers.
    defaultModel: 'stt-async-v5',
    fallbackChain: ['soniox'],
    async: true,
    aliases: { 'stt-async-v4': 'stt-async-v5' },
    models: [
      { id: 'stt-async-v5', supportsVocabulary: true, estimatedUsdPerMinute: 0.00167 },
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
    { ...spec, selfOnly: spec.fallbackChain.length === 1 },
  ]),
) as Record<SttProviderId, SttProviderDef>;

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
 * Preflight USD/min for a (provider, model), including the medical add-on where
 * the provider meters it. Used only for the credit reservation; actual cost is
 * computed from the upstream response in the adapter.
 */
export function estimatedUsdPerMinute(
  provider: SttProviderId,
  model?: string,
  medical: boolean = false,
  keyterms: boolean = false,
): number {
  const resolution = resolveModel(provider, model);
  const base = resolution.ok
    ? resolution.model.estimatedUsdPerMinute
    : PROVIDERS[provider].models[0].estimatedUsdPerMinute;

  if (provider === 'elevenlabs') {
    // Keyterm prompting adds +20% on base, scribe_v2 only (scribe_v1 has no biasing).
    const resolvedModel = resolution.ok ? resolution.model.id : PROVIDERS.elevenlabs.models[0].id;
    return (keyterms && resolvedModel === 'scribe_v2') ? base * (1 + ELEVENLABS_KEYTERMS_SURCHARGE) : base;
  }

  if (provider !== 'assemblyai') {
    return base;
  }

  const resolvedModel = resolution.ok ? resolution.model.id : PROVIDERS.assemblyai.models[0].id;
  const medicalAddon = medical ? ASSEMBLYAI_MEDICAL_ADDON_USD_PER_MINUTE : 0;
  // Keyterms add-on only applies to the Universal-3.x Pro tier (free/beta on universal-2).
  const keytermsAddon = (keyterms && resolvedModel === 'universal-3-5-pro')
    ? ASSEMBLYAI_KEYTERMS_ADDON_USD_PER_MINUTE
    : 0;
  return base + medicalAddon + keytermsAddon;
}
