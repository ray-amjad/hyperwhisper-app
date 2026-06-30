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
   * Self-only providers never fall back to a sibling: the caller picked this
   * model for a reason, so on failure we surface an error rather than silently
   * substituting a different model/price. All new proxy providers are self-only;
   * the original cheap trio (deepgram/groq/elevenlabs) keep cross-fallback.
   */
  selfOnly: boolean;
  /** Whether this provider's transcription flow is async (upload + poll). */
  async: boolean;
  models: SttModelDef[];
}

// Medical add-on multiplier surface — only AssemblyAI meters it today.
export const MEDICAL_DOMAIN = 'medical';
const ASSEMBLYAI_MEDICAL_ADDON_USD_PER_MINUTE = 0.15 / 60;
// Keyterms add-on ($0.05/hr) applies only to universal-3-pro; universal-2 is free/beta.
const ASSEMBLYAI_KEYTERMS_ADDON_USD_PER_MINUTE = 0.05 / 60;
// ElevenLabs keyterm prompting carries a +20% surcharge on base (scribe_v2 only).
const ELEVENLABS_KEYTERMS_SURCHARGE = 0.20;

const PROVIDERS: Record<SttProviderId, SttProviderDef> = {
  // ── Original cheap trio: cross-provider fallback retained ──
  deepgram: {
    id: 'deepgram',
    defaultModel: 'nova-3-general',
    selfOnly: false,
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
    selfOnly: false,
    async: false,
    models: [
      { id: 'whisper-large-v3-turbo', supportsVocabulary: true, estimatedUsdPerMinute: 0.000667 }, // $0.04/hr ÷ 60
      { id: 'whisper-large-v3', supportsVocabulary: true, estimatedUsdPerMinute: 0.00185 },      // $0.111/hr ÷ 60
    ],
  },
  elevenlabs: {
    id: 'elevenlabs',
    defaultModel: 'scribe_v2',
    selfOnly: false,
    async: false,
    models: [
      { id: 'scribe_v2', supportsVocabulary: true, estimatedUsdPerMinute: 0.00983 },
      // scribe_v1 has no vocabulary biasing — surfaced so clients can badge it.
      { id: 'scribe_v1', supportsVocabulary: false, estimatedUsdPerMinute: 0.00983 },
    ],
  },

  // ── Single-model self-only providers ──
  grok: {
    id: 'grok',
    defaultModel: '',
    // grok keeps its historical cross-provider fallback chain
    // (grok → deepgram → groq → elevenlabs) defined in transcribe.ts.
    selfOnly: false,
    async: false,
    models: [{ id: '', supportsVocabulary: false, estimatedUsdPerMinute: 0.00167 }],
  },
  'azure-mai': {
    id: 'azure-mai',
    defaultModel: 'mai-transcribe-1.5',
    selfOnly: true,
    async: false,
    models: [{ id: 'mai-transcribe-1.5', supportsVocabulary: true, estimatedUsdPerMinute: 0.006 }],
  },
  'google-chirp': {
    id: 'google-chirp',
    defaultModel: 'chirp_3',
    selfOnly: true,
    async: true,
    models: [{ id: 'chirp_3', supportsVocabulary: false, estimatedUsdPerMinute: 0.016 }],
  },

  // ── New synchronous proxy providers ──
  openai: {
    id: 'openai',
    defaultModel: 'gpt-4o-transcribe',
    selfOnly: true,
    async: false,
    // gpt-4o-* are token-billed (input audio + OUTPUT transcript tokens), so the
    // preflight rate adds a conservative output allowance on top of the input
    // floor: ~300 output tokens/min (fast speech ≈ 200 wpm × ~1.3 tok/word, plus
    // headroom) at the model output rate — $10/1M for transcribe (+$0.003/min),
    // $5/1M for mini (+$0.0015/min) — so a verbose transcript can't out-bill the
    // reservation. whisper-1 is duration-billed (no output-token charge).
    models: [
      { id: 'gpt-4o-transcribe', supportsVocabulary: true, estimatedUsdPerMinute: 0.009 },
      { id: 'gpt-4o-mini-transcribe', supportsVocabulary: true, estimatedUsdPerMinute: 0.0045 },
      { id: 'whisper-1', supportsVocabulary: true, estimatedUsdPerMinute: 0.006 },
    ],
  },
  gemini: {
    id: 'gemini',
    defaultModel: 'gemini-2.5-flash',
    selfOnly: true,
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
    selfOnly: true,
    async: false,
    models: [
      { id: 'voxtral-mini-latest', supportsVocabulary: true, estimatedUsdPerMinute: 0.003 },
    ],
  },

  // ── New asynchronous (upload + poll) proxy providers ──
  assemblyai: {
    id: 'assemblyai',
    defaultModel: 'universal-3-pro',
    selfOnly: true,
    async: true,
    models: [
      { id: 'universal-3-pro', supportsVocabulary: true, estimatedUsdPerMinute: 0.0035 },
      { id: 'universal-2', supportsVocabulary: true, estimatedUsdPerMinute: 0.0025 },
    ],
  },
  soniox: {
    id: 'soniox',
    defaultModel: 'stt-async-v4',
    selfOnly: true,
    async: true,
    models: [
      { id: 'stt-async-v4', supportsVocabulary: true, estimatedUsdPerMinute: 0.00167 },
      // v5 is API-compatible with v4; v4 auto-routes to it after 2026-06-30.
      { id: 'stt-async-v5', supportsVocabulary: true, estimatedUsdPerMinute: 0.00167 },
    ],
  },
};

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

  const match = def.models.find((m) => m.id === trimmed);
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
  // Keyterms add-on only applies to universal-3-pro (free/beta on universal-2).
  const keytermsAddon = (keyterms && resolvedModel === 'universal-3-pro')
    ? ASSEMBLYAI_KEYTERMS_ADDON_USD_PER_MINUTE
    : 0;
  return base + medicalAddon + keytermsAddon;
}
