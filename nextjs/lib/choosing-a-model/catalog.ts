/**
 * The models the /choosing-a-model calculator ranks.
 *
 * Two sources, kept apart because they are different kinds of fact:
 *
 *  - **What we ship.** Cloud models, their prices and their capabilities are
 *    mirrored from `shared-app-classification/cloud-stt-catalog.json`; on-device
 *    models are mirrored from the macOS and Windows model registries. Both sit
 *    outside this Next.js app's build root, so they are copied here rather than
 *    imported — the same arrangement `lib/latency/providers.ts` uses, and
 *    `tests/choosing-a-model-catalog.test.ts` reads the real catalog as data and
 *    fails when this copy drifts from it.
 *  - **How good they are.** Word error rate and speed factor come from the
 *    Artificial Analysis speech-to-text leaderboard, which is a third party
 *    measuring published models. Nothing here is our own accuracy claim.
 *
 * A model with no published benchmark keeps `wer: null` rather than a guess.
 * The scoring treats that as "unknown", not as "average" — see `scoring.ts`.
 *
 * @see https://artificialanalysis.ai/speech-to-text/non-streaming
 */

/** Which desktop app can run an on-device model. */
export type Platform = "macos" | "windows";

/**
 * How we know a model's word error rate. Shown to the reader, because
 * "benchmarked" and "inherited from identical weights" are not the same claim.
 */
export type AccuracyBasis =
  /** The leaderboard measured this exact hosted model. */
  | "measured"
  /**
   * The leaderboard measured these exact open weights on a hosted runner. The
   * weights are the same file we download, so the error rate carries over;
   * throughput does not, which is why `speedFactor` stays null for these.
   */
  | "sameWeights"
  /** No published benchmark. Local models fall back to the app's own rating. */
  | "appRating"
  /** No published benchmark and no rating either. */
  | "none";

/**
 * How broad a model's language support is, as a rung rather than a count.
 *
 * A raw count is a bad proxy and the page shipped proving it: Nemotron 3.5
 * Latin's six languages are six European ones, which is exactly what a European
 * reader wants, while thirteen languages that happen to include Hindi and
 * Arabic are not "wide multilingual". Counting alone ranked the second above the
 * first. What the filter actually asks is "can I dictate the languages I speak",
 * so that is what the rung answers.
 *
 *  - `narrow` — one language, or a couple that share no region. Serves English.
 *  - `european` — enough European languages to work across Europe, and not
 *    much beyond it.
 *  - `wide` — many languages, reaching past Europe into other families.
 *  - `unknown` — the vendor publishes nothing.
 *
 * `unknown` is NOT treated as `wide`. `shared-app-classification/CLAUDE.md`
 * requires the UI to read an unverified figure as the conservative default, and
 * the page shipped doing the opposite: five Gemini rows carry a null count
 * mirrored from a literal `"count": "unverified"`, and survived a "wide
 * multilingual" filter that dropped models with a published 25.
 */
export type LanguageScope = "narrow" | "european" | "wide" | "unknown";

/** At least this many languages before "wide multilingual" means anything. */
export const WIDE_LANGUAGE_MINIMUM = 20;

/** At least this many European ones before a model serves a European reader. */
export const EUROPEAN_LANGUAGE_MINIMUM = 5;

/**
 * Base codes we count as European. Only needs to cover what the desktop model
 * registries actually declare, plus room to grow; anything unlisted counts as
 * reaching beyond Europe, which is the direction that makes `scopeForCodes`
 * harder to satisfy rather than easier.
 */
export const EUROPEAN_LANGUAGE_CODES: readonly string[] = [
  "be", "bg", "bs", "ca", "cs", "cy", "da", "de", "el", "en", "es", "et", "eu",
  "fi", "fr", "ga", "hr", "hu", "is", "it", "lt", "lv", "mk", "mt", "nl", "no",
  "pl", "pt", "ro", "ru", "sk", "sl", "sq", "sr", "sv", "tr", "uk",
];

/**
 * The rung a documented language LIST earns. Used for on-device models, whose
 * registries name every code they accept, and by the drift test to re-derive
 * each mirrored scope from those same registries.
 */
export function scopeForCodes(codes: readonly string[]): LanguageScope {
  const european = new Set(EUROPEAN_LANGUAGE_CODES);
  // Regional variants collapse onto their base code, so `zh-TW` and `zh` are
  // one language rather than two — the same normalisation the desktop library
  // filters do.
  const base = Array.from(
    new Set(codes.map((code) => code.split("-")[0].toLowerCase())),
  );

  if (base.length <= 1) return "narrow";

  const europeanCount = base.filter((code) => european.has(code)).length;
  const reachesBeyondEurope = europeanCount < base.length;

  if (reachesBeyondEurope && base.length >= WIDE_LANGUAGE_MINIMUM) return "wide";
  if (europeanCount >= EUROPEAN_LANGUAGE_MINIMUM) return "european";
  return "narrow";
}

/**
 * The rung a documented language COUNT earns. Used for cloud models, where the
 * catalog publishes a number and no list.
 *
 * Weaker than `scopeForCodes` and knowingly so: a count cannot tell a European
 * set from a global one, so the threshold stands in for it. A vendor
 * advertising twenty or more languages is not selling a European product, and
 * one advertising a handful covers European languages with them — every
 * multi-language vendor in the catalog does.
 */
export function scopeForCount(count: number | null): LanguageScope {
  if (count === null) return "unknown";
  if (count <= 1) return "narrow";
  return count >= WIDE_LANGUAGE_MINIMUM ? "wide" : "european";
}

export type CloudModel = {
  placement: "cloud";
  /** `catalogProviderId:modelId`. Unique across the catalog. */
  id: string;
  name: string;
  /** The catalog's provider display name. */
  vendorLabel: string;
  vendor: string;
  /**
   * Backend provider id as the edge service reports it, and the model id
   * beside it. Together they join a row to its measured latency on /latency.
   */
  sttProvider: string;
  /** Empty for a provider whose endpoint takes no model id. */
  modelId: string;
  /** Credits per audio minute. 1,000 credits = $1. */
  credits: number;
  wer: number | null;
  accuracyBasis: AccuracyBasis;
  /** Audio seconds transcribed per second, from the leaderboard. */
  speedFactor: number | null;
  /** Documented language count. Null where the vendor does not publish one. */
  languages: number | null;
  /** Derived from `languages` by `scopeForCount`. */
  languageScope: LanguageScope;
  streaming: boolean;
  customVocabulary: boolean;
  preview: boolean;
  isDefault: boolean;
  /** Usable with your own vendor API key instead of our credits. */
  byok: boolean;
};

export type DeviceModel = {
  placement: "device";
  id: string;
  name: string;
  vendorLabel: string;
  platforms: readonly Platform[];
  /** Download size, per platform where the two differ. */
  size: string;
  sizeWindows?: string;
  wer: number | null;
  accuracyBasis: AccuracyBasis;
  /** The app's own 1-5 ratings, from its model library. */
  speedRating: number;
  accuracyRating: number;
  /** How many languages the platform registry names for this model. */
  languages: number;
  /**
   * The rung those languages earn. Stated here rather than derived, because
   * mirroring every code list would be more drift surface than it is worth —
   * the drift test re-derives it from the registries with `scopeForCodes`.
   */
  languageScope: LanguageScope;
  /**
   * Platforms whose app can transcribe live with this model.
   *
   * Per platform, and emphatically not a boolean: macOS exposes exactly two
   * local streaming providers, `parakeetLocal` and `nemotronLocal`
   * (`StreamingProviderStrategy.swift`), while the Windows enum
   * (`Models/StreamingTranscriptionProvider.cs`) is five cloud vendors and no
   * local member at all. The page previously kept every local model under a
   * "Live streaming" filter on both platforms.
   *
   * Windows ships a model NAMED "Nemotron 3.5 Streaming": the name describes a
   * cache-aware online transducer inside the daemon, and it is reached through
   * the ordinary record-then-transcribe path. It is not a streaming provider,
   * so it is not listed here.
   */
  streamingPlatforms: readonly Platform[];
  /**
   * Platforms whose app actually applies a vocabulary list to this model.
   *
   * Also per platform, and behaviour rather than catalog claim. Local Whisper
   * is `supportsCustomVocabulary: true` in `shared-models/models-catalog.json`
   * because whisper.cpp accepts an `initial_prompt`, and macOS does set one
   * (`LibWhisperProvider.swift`) — but the Windows path documents in its own
   * comment that it drops the argument (`TranscriptionService.cs`:
   * "the vocabulary parameter is ignored for local transcription"). Parakeet,
   * Nemotron and Qwen3 are `false` in the catalog on both platforms.
   */
  customVocabularyPlatforms: readonly Platform[];
};

export type Model = CloudModel | DeviceModel;

/**
 * Mirrored from `cloud-stt-catalog.json` v7 (2026-08-13), plus one row that is
 * ahead of it — see the comment on the last entry. Benchmark columns from the
 * Artificial Analysis leaderboard, pulled 2026-08-19, and 2026-08-27 for that
 * last row.
 */
const CLOUD_MODELS_RAW = [
  { id: "groqWhisper:whisper-large-v3-turbo", name: "Whisper Large v3 Turbo", vendorLabel: "Groq Whisper", vendor: "Groq", sttProvider: "groq", modelId: "whisper-large-v3-turbo", credits: 0.667, wer: 4.6, speedFactor: 122.2, languages: 100, streaming: false, customVocabulary: true, preview: false, isDefault: true, byok: true },
  { id: "groqWhisper:whisper-large-v3", name: "Whisper Large v3", vendorLabel: "Groq Whisper", vendor: "Groq", sttProvider: "groq", modelId: "whisper-large-v3", credits: 1.85, wer: 4.1, speedFactor: 92.1, languages: 100, streaming: false, customVocabulary: true, preview: false, isDefault: false, byok: true },
  { id: "deepgramNova3:nova-3-general", name: "Nova 3 General", vendorLabel: "Deepgram Nova 3", vendor: "Deepgram", sttProvider: "deepgram", modelId: "nova-3-general", credits: 5.5, wer: 5.2, speedFactor: 541.1, languages: 64, streaming: true, customVocabulary: true, preview: false, isDefault: true, byok: true },
  { id: "deepgramNova3:nova-3-medical", name: "Nova 3 Medical", vendorLabel: "Deepgram Nova 3", vendor: "Deepgram", sttProvider: "deepgram", modelId: "nova-3-medical", credits: 5.5, wer: 5.2, speedFactor: 541.1, languages: 64, streaming: true, customVocabulary: true, preview: false, isDefault: false, byok: true },
  { id: "deepgramNova3:nova-2-general", name: "Nova 2 General", vendorLabel: "Deepgram Nova 3", vendor: "Deepgram", sttProvider: "deepgram", modelId: "nova-2-general", credits: 5.5, wer: null, speedFactor: null, languages: 64, streaming: true, customVocabulary: true, preview: false, isDefault: false, byok: true },
  { id: "deepgramNova3:nova-2-medical", name: "Nova 2 Medical", vendorLabel: "Deepgram Nova 3", vendor: "Deepgram", sttProvider: "deepgram", modelId: "nova-2-medical", credits: 5.5, wer: null, speedFactor: null, languages: 64, streaming: true, customVocabulary: true, preview: false, isDefault: false, byok: true },
  { id: "grokStt:", name: "Grok Speech-to-Text", vendorLabel: "Grok STT", vendor: "xAI", sttProvider: "grok", modelId: "", credits: 1.67, wer: 4.0, speedFactor: 230.1, languages: 25, streaming: true, customVocabulary: true, preview: false, isDefault: true, byok: true },
  { id: "azureMaiTranscribe:mai-transcribe-1.5", name: "MAI-Transcribe 1.5", vendorLabel: "Microsoft MAI-Transcribe 1.5", vendor: "Microsoft", sttProvider: "azure-mai", modelId: "mai-transcribe-1.5", credits: 6.0, wer: 2.4, speedFactor: 183.3, languages: 42, streaming: false, customVocabulary: true, preview: false, isDefault: true, byok: false },
  { id: "googleChirp3:chirp_3", name: "Chirp 3", vendorLabel: "Google Chirp 3", vendor: "Google", sttProvider: "google-chirp", modelId: "chirp_3", credits: 16.0, wer: 4.3, speedFactor: null, languages: 111, streaming: true, customVocabulary: true, preview: false, isDefault: true, byok: false },
  { id: "elevenLabsScribeV2:scribe_v2", name: "Scribe v2", vendorLabel: "ElevenLabs Scribe v2", vendor: "ElevenLabs", sttProvider: "elevenlabs", modelId: "scribe_v2", credits: 9.83, wer: 2.2, speedFactor: 57.0, languages: 99, streaming: false, customVocabulary: true, preview: false, isDefault: true, byok: true },
  { id: "openaiWhisper:gpt-4o-transcribe", name: "GPT-4o Transcribe", vendorLabel: "OpenAI Whisper", vendor: "OpenAI", sttProvider: "openai", modelId: "gpt-4o-transcribe", credits: 6.0, wer: 4.0, speedFactor: 38.1, languages: 100, streaming: false, customVocabulary: true, preview: false, isDefault: true, byok: true },
  { id: "openaiWhisper:gpt-4o-mini-transcribe", name: "GPT-4o Mini Transcribe", vendorLabel: "OpenAI Whisper", vendor: "OpenAI", sttProvider: "openai", modelId: "gpt-4o-mini-transcribe", credits: 3.0, wer: 4.5, speedFactor: 43.5, languages: 100, streaming: false, customVocabulary: true, preview: false, isDefault: false, byok: true },
  { id: "openaiWhisper:whisper-1", name: "Whisper", vendorLabel: "OpenAI Whisper", vendor: "OpenAI", sttProvider: "openai", modelId: "whisper-1", credits: 6.0, wer: 4.1, speedFactor: 29.1, languages: 100, streaming: false, customVocabulary: true, preview: false, isDefault: false, byok: true },
  { id: "openaiWhisper:gpt-transcribe", name: "GPT Transcribe", vendorLabel: "OpenAI Whisper", vendor: "OpenAI", sttProvider: "openai", modelId: "gpt-transcribe", credits: 4.5, wer: 3.3, speedFactor: 39.9, languages: 100, streaming: false, customVocabulary: true, preview: false, isDefault: false, byok: true },
  { id: "openaiWhisper:gpt-live-transcribe", name: "GPT Live Transcribe", vendorLabel: "OpenAI Whisper", vendor: "OpenAI", sttProvider: "openai", modelId: "gpt-live-transcribe", credits: 17.0, wer: null, speedFactor: null, languages: 100, streaming: false, customVocabulary: true, preview: false, isDefault: false, byok: true },
  { id: "assemblyAI:universal-3-5-pro", name: "Universal-3.5 Pro", vendorLabel: "AssemblyAI", vendor: "AssemblyAI", sttProvider: "assemblyai", modelId: "universal-3-5-pro", credits: 3.5, wer: 3.0, speedFactor: null, languages: 98, streaming: true, customVocabulary: true, preview: false, isDefault: true, byok: true },
  { id: "assemblyAI:universal-2", name: "Universal-2", vendorLabel: "AssemblyAI", vendor: "AssemblyAI", sttProvider: "assemblyai", modelId: "universal-2", credits: 2.5, wer: 3.8, speedFactor: 123.1, languages: 98, streaming: true, customVocabulary: true, preview: false, isDefault: false, byok: true },
  { id: "mistralVoxtral:voxtral-mini-latest", name: "Voxtral Mini", vendorLabel: "Mistral Voxtral", vendor: "Mistral", sttProvider: "mistral", modelId: "voxtral-mini-latest", credits: 3.0, wer: 3.8, speedFactor: 78.1, languages: 13, streaming: true, customVocabulary: true, preview: false, isDefault: true, byok: true },
  { id: "soniox:stt-async-v5", name: "Async v5", vendorLabel: "Soniox", vendor: "Soniox", sttProvider: "soniox", modelId: "stt-async-v5", credits: 1.67, wer: 3.8, speedFactor: 18.8, languages: 60, streaming: true, customVocabulary: true, preview: false, isDefault: true, byok: true },
  { id: "gemini:gemini-2.5-flash", name: "Gemini 2.5 Flash", vendorLabel: "Google Gemini", vendor: "Google", sttProvider: "gemini", modelId: "gemini-2.5-flash", credits: 2.4, wer: 5.1, speedFactor: 73.7, languages: null, streaming: false, customVocabulary: true, preview: false, isDefault: true, byok: true },
  { id: "gemini:gemini-2.5-flash-lite", name: "Gemini 2.5 Flash Lite", vendorLabel: "Google Gemini", vendor: "Google", sttProvider: "gemini", modelId: "gemini-2.5-flash-lite", credits: 0.8, wer: 5.2, speedFactor: 70.7, languages: null, streaming: false, customVocabulary: true, preview: false, isDefault: false, byok: true },
  { id: "gemini:gemini-2.5-pro", name: "Gemini 2.5 Pro", vendorLabel: "Google Gemini", vendor: "Google", sttProvider: "gemini", modelId: "gemini-2.5-pro", credits: 7.5, wer: 2.9, speedFactor: 13.3, languages: null, streaming: false, customVocabulary: true, preview: false, isDefault: false, byok: true },
  { id: "gemini:gemini-3-flash-preview", name: "Gemini 3 Flash", vendorLabel: "Google Gemini", vendor: "Google", sttProvider: "gemini", modelId: "gemini-3-flash-preview", credits: 3.0, wer: 2.9, speedFactor: 16.1, languages: null, streaming: false, customVocabulary: true, preview: true, isDefault: false, byok: true },
  { id: "gemini:gemini-3.1-pro-preview", name: "Gemini 3.1 Pro", vendorLabel: "Google Gemini", vendor: "Google", sttProvider: "gemini", modelId: "gemini-3.1-pro-preview", credits: 10.0, wer: 2.8, speedFactor: 7.0, languages: null, streaming: false, customVocabulary: true, preview: true, isDefault: false, byok: true },
  /**
   * **Ahead of the catalog, on purpose.** `cloud-stt-catalog.json` v7 does not
   * have this model. Every field below is a prediction of what the catalog PR
   * adding it will write, not a mirror of something already there, so this row
   * is waived out of the cloud drift tests by `PENDING_CATALOG_IDS` in
   * `tests/choosing-a-model-catalog.test.ts`. The waiver removes itself: the
   * moment the catalog gains the model, a test there fails and says to delete
   * the waiver and reconcile these fields against what the catalog actually
   * says. It stays last in the array because the catalog appends to the gemini
   * provider, and the drift test compares ORDERED arrays.
   *
   * **Until that PR lands, this row describes a model no app can select.** The
   * mirror has no availability field, and `preview: false` cannot stand in for
   * one — every field here is compared against whatever the catalog PR writes,
   * so inventing a value would only move the failure to rebase time. In that
   * window the page's own copy is false for this row: the models "HyperWhisper
   * ships", "the price here is the price the app charges you", and "every model
   * listed is switchable in Settings → Transcription". The "No preview models"
   * chip is the sharpest form — it keeps this row and drops the two shipping
   * Gemini previews. This is exactly why this PR must not merge first.
   *
   * **Where 5.1 credits comes from.** This model bills audio at 25 tokens per
   * second: 1,500 audio tok/min at $2.00/1M is $0.0030, plus 175 text-output
   * tok/min at $12.00/1M is $0.0021 — $0.0051 a minute, which is 5.1 credits.
   * Artificial Analysis quotes the same figure rounded, as "approximately $5
   * per 1,000 minutes". Those per-1M rates are the vendor's, and are the same
   * numbers `gemini-3.1-pro-preview` already carries in `GEMINI_RATES`
   * (`hyperwhisper-cloud/src/lib/cost-calculator.ts`), so the figure is a rate
   * card applied to this model and not a typo. Note that 25 tok/s is not the 32
   * tok/s that file assumes for Gemini audio, but that constant
   * (`GEMINI_AUDIO_TOKENS_PER_MINUTE`) only feeds the fail-closed fallback for a
   * response that carried no `usageMetadata`, never the priced path. The real
   * gap is the rate card: `GEMINI_RATES` has no `gemini-3.5-transcribe` key, so
   * until the catalog PR adds one the service prices this model at
   * `gemini-2.5-flash`'s rates — roughly 1.94 credits a minute of recorded cost
   * against the 5.1 this page prints.
   *
   * **`customVocabulary: true` claims something stronger here** than on the
   * rows above it. The 2.5 and 3.x rows carry the same boolean for the
   * system-prompt workaround the catalog's `customVocabulary.caveats` describes
   * — an instruction glued onto the prompt, with no vocabulary field on the
   * request. This model takes a real structured phrase list, in
   * `generation_config.transcription_config.custom_vocabulary[]`. One boolean
   * cannot express the difference, so this comment is the only place it exists.
   *
   * **`languages: null`, despite Google advertising 85+.** The catalog
   * publishes a language count per PROVIDER, not per model, and the gemini
   * provider's is the literal `"count": "unverified"` — which is why all five
   * sibling rows are null too. A model-level count is not something this mirror
   * can invent, and the page does not publish a breadth claim the catalog
   * declines to make. `scopeForCount(null)` is `unknown`, and the
   * `LanguageScope` doc comment above says why `unknown` is not read as `wide`.
   */
  { id: "gemini:gemini-3.5-transcribe", name: "Gemini 3.5 Transcribe", vendorLabel: "Google Gemini", vendor: "Google", sttProvider: "gemini", modelId: "gemini-3.5-transcribe", credits: 5.1, wer: 2.6, speedFactor: 84, languages: null, streaming: false, customVocabulary: true, preview: false, isDefault: false, byok: true },
] as const;

export const CLOUD_MODELS: readonly CloudModel[] = CLOUD_MODELS_RAW.map(
  (model) => ({
    ...model,
    placement: "cloud" as const,
    accuracyBasis: model.wer === null ? ("none" as const) : ("measured" as const),
    languageScope: scopeForCount(model.languages),
  }),
);

/**
 * Mirrored from the macOS (`WhisperModelManager.swift`,
 * `ParakeetModelManager.swift`, `NemotronModelManager.swift`,
 * `ModelLibraryManager.swift`) and Windows (`WhisperModelInfo.cs`,
 * `ParakeetModelInfo.cs`, `ModelLibraryManager.cs`) model libraries, including
 * their 1-5 speed and accuracy ratings.
 *
 * Whisper and Parakeet carry a leaderboard word error rate because the
 * leaderboard measured those same open weights on a hosted runner. The rest
 * have no published figure and fall back to the app's own accuracy rating —
 * which is what keeps Whisper Tiny from scoring like a frontier model. The
 * English-only Whisper builds carry none either: the leaderboard measured the
 * multilingual weights, and these are different files.
 *
 * **`sizeWindows` is not optional decoration.** The two apps download different
 * artifacts, and almost every Whisper row differs — Tiny is 39 MB on macOS and
 * 78 MB on Windows, Parakeet V3 494 MB against 671 MB. The page shipped with
 * one row carrying the override and the rest quietly showing macOS numbers to
 * Windows readers, on the very figure the on-device trade-off is argued from.
 * `tests/choosing-a-model-catalog.test.ts` now reads both platforms' registries
 * as data and fails when any of this drifts.
 *
 * Where the two platforms genuinely disagree and only one number fits, the
 * mirror takes macOS and the scope covers the gap: Parakeet V3 names 25
 * languages on macOS and 26 on Windows, both sets wholly European, so both earn
 * `european` and the count shown is the macOS one.
 */
const DEVICE_MODELS_RAW = [
  { id: "device:whisper-large-v3-turbo", name: "Whisper Large v3 Turbo", vendorLabel: "Whisper", platforms: ["macos", "windows"], size: "809 MB", sizeWindows: "1.5 GB", wer: 4.6, accuracyBasis: "sameWeights", speedRating: 4, accuracyRating: 3, languages: 100, languageScope: "wide", streamingPlatforms: [], customVocabularyPlatforms: ["macos"] },
  { id: "device:whisper-large-v3", name: "Whisper Large v3", vendorLabel: "Whisper", platforms: ["macos", "windows"], size: "3.1 GB", wer: 4.1, accuracyBasis: "sameWeights", speedRating: 3, accuracyRating: 3, languages: 100, languageScope: "wide", streamingPlatforms: [], customVocabularyPlatforms: ["macos"] },
  { id: "device:whisper-large-v2", name: "Whisper Large v2", vendorLabel: "Whisper", platforms: ["macos", "windows"], size: "2.9 GB", sizeWindows: "3.1 GB", wer: 4.1, accuracyBasis: "sameWeights", speedRating: 3, accuracyRating: 3, languages: 100, languageScope: "wide", streamingPlatforms: [], customVocabularyPlatforms: ["macos"] },
  { id: "device:whisper-medium", name: "Whisper Medium", vendorLabel: "Whisper", platforms: ["macos", "windows"], size: "1.5 GB", wer: null, accuracyBasis: "appRating", speedRating: 4, accuracyRating: 3, languages: 100, languageScope: "wide", streamingPlatforms: [], customVocabularyPlatforms: ["macos"] },
  { id: "device:whisper-medium-en", name: "Whisper Medium (English)", vendorLabel: "Whisper", platforms: ["macos", "windows"], size: "1.5 GB", wer: null, accuracyBasis: "appRating", speedRating: 4, accuracyRating: 2, languages: 1, languageScope: "narrow", streamingPlatforms: [], customVocabularyPlatforms: ["macos"] },
  { id: "device:whisper-small", name: "Whisper Small", vendorLabel: "Whisper", platforms: ["macos", "windows"], size: "466 MB", sizeWindows: "488 MB", wer: null, accuracyBasis: "appRating", speedRating: 4, accuracyRating: 2, languages: 100, languageScope: "wide", streamingPlatforms: [], customVocabularyPlatforms: ["macos"] },
  { id: "device:whisper-small-en", name: "Whisper Small (English)", vendorLabel: "Whisper", platforms: ["macos", "windows"], size: "466 MB", sizeWindows: "488 MB", wer: null, accuracyBasis: "appRating", speedRating: 5, accuracyRating: 2, languages: 1, languageScope: "narrow", streamingPlatforms: [], customVocabularyPlatforms: ["macos"] },
  { id: "device:whisper-base", name: "Whisper Base", vendorLabel: "Whisper", platforms: ["macos", "windows"], size: "142 MB", sizeWindows: "148 MB", wer: null, accuracyBasis: "appRating", speedRating: 5, accuracyRating: 1, languages: 100, languageScope: "wide", streamingPlatforms: [], customVocabularyPlatforms: ["macos"] },
  { id: "device:whisper-base-en", name: "Whisper Base (English)", vendorLabel: "Whisper", platforms: ["macos", "windows"], size: "142 MB", sizeWindows: "148 MB", wer: null, accuracyBasis: "appRating", speedRating: 5, accuracyRating: 2, languages: 1, languageScope: "narrow", streamingPlatforms: [], customVocabularyPlatforms: ["macos"] },
  { id: "device:whisper-tiny", name: "Whisper Tiny", vendorLabel: "Whisper", platforms: ["macos", "windows"], size: "39 MB", sizeWindows: "78 MB", wer: null, accuracyBasis: "appRating", speedRating: 5, accuracyRating: 1, languages: 100, languageScope: "wide", streamingPlatforms: [], customVocabularyPlatforms: ["macos"] },
  { id: "device:whisper-tiny-en", name: "Whisper Tiny (English)", vendorLabel: "Whisper", platforms: ["macos", "windows"], size: "39 MB", sizeWindows: "78 MB", wer: null, accuracyBasis: "appRating", speedRating: 5, accuracyRating: 1, languages: 1, languageScope: "narrow", streamingPlatforms: [], customVocabularyPlatforms: ["macos"] },
  { id: "device:parakeet-v3", name: "Parakeet V3", vendorLabel: "Parakeet", platforms: ["macos", "windows"], size: "494 MB", sizeWindows: "671 MB", wer: 4.5, accuracyBasis: "sameWeights", speedRating: 5, accuracyRating: 3, languages: 25, languageScope: "european", streamingPlatforms: ["macos"], customVocabularyPlatforms: [] },
  { id: "device:parakeet-v2", name: "Parakeet V2", vendorLabel: "Parakeet", platforms: ["macos", "windows"], size: "474 MB", sizeWindows: "661 MB", wer: 6.4, accuracyBasis: "sameWeights", speedRating: 5, accuracyRating: 3, languages: 1, languageScope: "narrow", streamingPlatforms: ["macos"], customVocabularyPlatforms: [] },
  { id: "device:nemotron-multilingual", name: "Nemotron 3.5 Multilingual", vendorLabel: "Nemotron", platforms: ["macos"], size: "~1.3 GB", wer: null, accuracyBasis: "appRating", speedRating: 5, accuracyRating: 4, languages: 30, languageScope: "wide", streamingPlatforms: ["macos"], customVocabularyPlatforms: [] },
  { id: "device:nemotron-latin", name: "Nemotron 3.5 Latin", vendorLabel: "Nemotron", platforms: ["macos"], size: "~350 MB", wer: null, accuracyBasis: "appRating", speedRating: 5, accuracyRating: 4, languages: 6, languageScope: "european", streamingPlatforms: ["macos"], customVocabularyPlatforms: [] },
  { id: "device:nemotron-streaming", name: "Nemotron 3.5 Streaming", vendorLabel: "Nemotron", platforms: ["windows"], size: "~660 MB", wer: null, accuracyBasis: "appRating", speedRating: 5, accuracyRating: 4, languages: 2, languageScope: "narrow", streamingPlatforms: [], customVocabularyPlatforms: [] },
  { id: "device:qwen3-asr", name: "Qwen3 ASR", vendorLabel: "Qwen3", platforms: ["macos"], size: "~1.3 GB", wer: null, accuracyBasis: "appRating", speedRating: 4, accuracyRating: 1, languages: 30, languageScope: "wide", streamingPlatforms: [], customVocabularyPlatforms: [] },
  { id: "device:qwen3-asr-0.6b", name: "Qwen3 ASR 0.6B", vendorLabel: "Qwen3", platforms: ["windows"], size: "~985 MB", wer: null, accuracyBasis: "appRating", speedRating: 3, accuracyRating: 4, languages: 11, languageScope: "european", streamingPlatforms: [], customVocabularyPlatforms: [] },
  { id: "device:apple-speech", name: "Apple Speech", vendorLabel: "Apple", platforms: ["macos"], size: "Built-in", wer: null, accuracyBasis: "appRating", speedRating: 5, accuracyRating: 3, languages: 23, languageScope: "wide", streamingPlatforms: [], customVocabularyPlatforms: ["macos"] },
] as const;

export const DEVICE_MODELS: readonly DeviceModel[] = DEVICE_MODELS_RAW.map(
  (model) => ({ ...model, placement: "device" as const }),
);

export const ALL_MODELS: readonly Model[] = [...CLOUD_MODELS, ...DEVICE_MODELS];

export function isCloud(model: Model): model is CloudModel {
  return model.placement === "cloud";
}

export function isDevice(model: Model): model is DeviceModel {
  return model.placement === "device";
}

export function modelsForPlatform(platform: Platform): readonly Model[] {
  return ALL_MODELS.filter(
    (model) => isCloud(model) || model.platforms.includes(platform),
  );
}
