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
  /**
   * Whether HyperWhisper can transcribe live with THIS model — the "Live
   * streaming" chip.
   *
   * An app fact, not a vendor fact, so it is not mirrored from the catalog's
   * `features.streaming`. `CloudSTTCatalog.swift` says why that field cannot
   * answer this question: it is "the entry-level hint, which is true for six
   * vendors we serve no WebSocket route for". Reading it here published a live
   * claim for AssemblyAI, Mistral and Soniox, which neither app can stream, and
   * for `gemini-3.5-transcribe`, the pre-recorded Interactions API model, whose
   * live sibling is a separate row.
   *
   * The rule, applied row by row: true when a reader of THIS page can reach
   * this model live in BOTH apps it describes — macOS and Windows. An
   * intersection, not a union, and `supportsStreaming` in `scoring.ts` takes no
   * platform for cloud rows because of it. Linux is outside the page's scope and
   * outside this rule: its streaming model box is free text
   * (`LinuxLiveStreamingAdapters.cs` forwards whatever is typed), so a union
   * rule would turn every Deepgram model into a live claim that the two apps
   * this page names cannot honour.
   *
   * Five routes live in `shared-core-rs/crates/hw-net/src/live/` (`deepgram.rs`,
   * `elevenlabs.rs`, `openai.rs`, `xai.rs`, `gemini.rs`) with the native
   * strategies beside them, and two HyperWhisper Cloud proxies in
   * `hyperwhisper-cloud/src/routes/`. A route existing is not enough — what the
   * picker in front of it can select is what decides the flag:
   *
   * - Deepgram: two rows only. Both desktop pickers offer `nova-3-general` and
   *   `nova-3-medical` and nothing else (`StreamingView.swift`, and
   *   `SettingsService.StreamingDeepgramModel` on Windows rewrites any other
   *   value to `nova-3-general`), and `ws-streaming-deepgram.ts` hard-codes
   *   `model: 'nova-3'`. The two Nova 2 rows are batch-only on both.
   * - xAI's live endpoint takes no model parameter, so its single row streams.
   * - Gemini Transcribe: the live row only. No caller sends a model id on this
   *   path — macOS passes nil for `.gemini` deliberately — and each side
   *   substitutes its own live constant when the id is empty: `LIVE_MODEL` in
   *   `providers/gemini_transcribe.rs`, reached through `live/gemini.rs`, and a
   *   duplicate `liveModel` literal in `GeminiStreamingStrategy`. Nothing on
   *   the path would reject the pre-recorded id; nothing sends it either.
   * - OpenAI and ElevenLabs have live routes, but they run
   *   `gpt-realtime-whisper` and `scribe_v2_realtime` — ids this page does not
   *   list — so no row of either vendor claims live. `gpt-live-transcribe` in
   *   particular is `IsAvailable = false` in `CloudTranscriptionModel.cs` and
   *   runs on neither the batch nor the live path.
   *
   * The drift test holds this column to the same list, so changing a row here
   * alone fails it.
   */
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
 * Mirrored from `cloud-stt-catalog.json` v12 (2026-09-04). Benchmark columns
 * from the Artificial Analysis non-streaming leaderboard, pulled 2026-08-19,
 * and 2026-08-28 for the `gemini-3.5-transcribe` row — a model that leaderboard
 * has not measured carries `wer: null` / `speedFactor: null` rather than a
 * guess.
 *
 * One leaderboard, deliberately. The page links
 * `artificialanalysis.ai/speech-to-text/non-streaming` beside this column, so
 * every number in it is read from that board and from no other. Artificial
 * Analysis also publishes a streaming board, scored on its own audio; a figure
 * lifted from there would be ranked against these as though the two were one
 * measurement. `gemini-3.5-transcribe-live` is not on the non-streaming board,
 * so it keeps a null `wer` — see the note on its row.
 */
const CLOUD_MODELS_RAW = [
  { id: "groqWhisper:whisper-large-v3-turbo", name: "Whisper Large v3 Turbo", vendorLabel: "Groq Whisper", vendor: "Groq", sttProvider: "groq", modelId: "whisper-large-v3-turbo", credits: 0.667, wer: 4.6, speedFactor: 122.2, languages: 100, streaming: false, customVocabulary: true, preview: false, isDefault: true, byok: true },
  { id: "groqWhisper:whisper-large-v3", name: "Whisper Large v3", vendorLabel: "Groq Whisper", vendor: "Groq", sttProvider: "groq", modelId: "whisper-large-v3", credits: 1.85, wer: 4.1, speedFactor: 92.1, languages: 100, streaming: false, customVocabulary: true, preview: false, isDefault: false, byok: true },
  { id: "deepgramNova3:nova-3-general", name: "Nova 3 General", vendorLabel: "Deepgram Nova 3", vendor: "Deepgram", sttProvider: "deepgram", modelId: "nova-3-general", credits: 5.5, wer: 5.2, speedFactor: 541.1, languages: 64, streaming: true, customVocabulary: true, preview: false, isDefault: true, byok: true },
  { id: "deepgramNova3:nova-3-medical", name: "Nova 3 Medical", vendorLabel: "Deepgram Nova 3", vendor: "Deepgram", sttProvider: "deepgram", modelId: "nova-3-medical", credits: 5.5, wer: 5.2, speedFactor: 541.1, languages: 64, streaming: true, customVocabulary: true, preview: false, isDefault: false, byok: true },
  { id: "deepgramNova3:nova-2-general", name: "Nova 2 General", vendorLabel: "Deepgram Nova 3", vendor: "Deepgram", sttProvider: "deepgram", modelId: "nova-2-general", credits: 5.5, wer: null, speedFactor: null, languages: 64, streaming: false, customVocabulary: true, preview: false, isDefault: false, byok: true },
  { id: "deepgramNova3:nova-2-medical", name: "Nova 2 Medical", vendorLabel: "Deepgram Nova 3", vendor: "Deepgram", sttProvider: "deepgram", modelId: "nova-2-medical", credits: 5.5, wer: null, speedFactor: null, languages: 64, streaming: false, customVocabulary: true, preview: false, isDefault: false, byok: true },
  { id: "grokStt:", name: "Grok Speech-to-Text", vendorLabel: "Grok STT", vendor: "SpaceXAI", sttProvider: "grok", modelId: "", credits: 1.67, wer: 4.0, speedFactor: 230.1, languages: 25, streaming: true, customVocabulary: true, preview: false, isDefault: true, byok: true },
  // Not on the non-streaming leaderboard this column reads, so both figures
  // stay null and `rankModels` scores it a neutral 0.5 rather than inheriting
  // 1.5's numbers. Microsoft's own launch post claims it beats 1.5 on both, but
  // that is a vendor figure measured on vendor audio — the one thing the note
  // above CLOUD_MODELS_RAW forbids this column. Fill these in from the board
  // when Artificial Analysis publishes the row.
  { id: "azureMaiTranscribe:mai-transcribe-2", name: "MAI-Transcribe 2", vendorLabel: "Microsoft MAI-Transcribe", vendor: "Microsoft", sttProvider: "azure-mai", modelId: "mai-transcribe-2", credits: 1.67, wer: null, speedFactor: null, languages: 60, streaming: false, customVocabulary: true, preview: true, isDefault: true, byok: false },
  // 43, not the provider row's 60: `languages.codes` on `azureMaiTranscribe` is
  // the UNION of the two models' tables, and this column is per MODEL. The
  // catalog states each model's own figure in `models[].languageCount`, which is
  // what `choosing-a-model-catalog.test.ts` checks these two against.
  { id: "azureMaiTranscribe:mai-transcribe-1.5", name: "MAI-Transcribe 1.5", vendorLabel: "Microsoft MAI-Transcribe", vendor: "Microsoft", sttProvider: "azure-mai", modelId: "mai-transcribe-1.5", credits: 6.0, wer: 2.4, speedFactor: 183.3, languages: 43, streaming: false, customVocabulary: true, preview: false, isDefault: false, byok: false },
  // Both figures read from the non-streaming leaderboard on 2026-08-28, a later
  // pull than the rest of this column. Artificial Analysis re-runs the board, so
  // any row is only as current as its pull date and the column now holds two of
  // them. Re-syncing all of it is its own change; this row is the one the
  // ranking was missing, and it is left with no restated copy of its numbers —
  // a figure written twice is a figure that decays in one of the two places.
  { id: "geminiTranscribe:gemini-3.5-transcribe", name: "Gemini 3.5 Transcribe", vendorLabel: "Google Gemini 3.5 Transcribe", vendor: "Google", sttProvider: "gemini-transcribe", modelId: "gemini-3.5-transcribe", credits: 5.5, wer: 2.6, speedFactor: 82.5, languages: null, streaming: false, customVocabulary: true, preview: true, isDefault: true, byok: true },
  // Not on the non-streaming leaderboard, so both columns stay null and
  // `rankModels` scores it a neutral 0.5 on accuracy and latency rather than a
  // guess. The row above is NOT a stand-in for it: they are different models on
  // different APIs (Interactions vs the live WebSocket), and they are priced
  // apart. Fill these two fields only from the board this column already reads
  // — see the note above CLOUD_MODELS_RAW.
  { id: "geminiTranscribe:gemini-3.5-transcribe-live", name: "Gemini 3.5 Transcribe Live", vendorLabel: "Google Gemini 3.5 Transcribe", vendor: "Google", sttProvider: "gemini-transcribe", modelId: "gemini-3.5-transcribe-live", credits: 9.6, wer: null, speedFactor: null, languages: null, streaming: true, customVocabulary: true, preview: true, isDefault: false, byok: true },
  { id: "elevenLabsScribeV2:scribe_v2", name: "Scribe v2", vendorLabel: "ElevenLabs Scribe v2", vendor: "ElevenLabs", sttProvider: "elevenlabs", modelId: "scribe_v2", credits: 9.83, wer: 2.2, speedFactor: 57.0, languages: 99, streaming: false, customVocabulary: true, preview: false, isDefault: true, byok: true },
  { id: "openaiWhisper:gpt-4o-transcribe", name: "GPT-4o Transcribe", vendorLabel: "OpenAI Whisper", vendor: "OpenAI", sttProvider: "openai", modelId: "gpt-4o-transcribe", credits: 6.0, wer: 4.0, speedFactor: 38.1, languages: 100, streaming: false, customVocabulary: true, preview: false, isDefault: true, byok: true },
  { id: "openaiWhisper:gpt-4o-mini-transcribe", name: "GPT-4o Mini Transcribe", vendorLabel: "OpenAI Whisper", vendor: "OpenAI", sttProvider: "openai", modelId: "gpt-4o-mini-transcribe", credits: 3.0, wer: 4.5, speedFactor: 43.5, languages: 100, streaming: false, customVocabulary: true, preview: false, isDefault: false, byok: true },
  { id: "openaiWhisper:whisper-1", name: "Whisper", vendorLabel: "OpenAI Whisper", vendor: "OpenAI", sttProvider: "openai", modelId: "whisper-1", credits: 6.0, wer: 4.1, speedFactor: 29.1, languages: 100, streaming: false, customVocabulary: true, preview: false, isDefault: false, byok: true },
  { id: "openaiWhisper:gpt-transcribe", name: "GPT Transcribe", vendorLabel: "OpenAI Whisper", vendor: "OpenAI", sttProvider: "openai", modelId: "gpt-transcribe", credits: 4.5, wer: 3.3, speedFactor: 39.9, languages: 100, streaming: false, customVocabulary: true, preview: false, isDefault: false, byok: true },
  { id: "openaiWhisper:gpt-live-transcribe", name: "GPT Live Transcribe", vendorLabel: "OpenAI Whisper", vendor: "OpenAI", sttProvider: "openai", modelId: "gpt-live-transcribe", credits: 17.0, wer: null, speedFactor: null, languages: 100, streaming: false, customVocabulary: true, preview: false, isDefault: false, byok: true },
  { id: "assemblyAI:universal-3-5-pro", name: "Universal-3.5 Pro", vendorLabel: "AssemblyAI", vendor: "AssemblyAI", sttProvider: "assemblyai", modelId: "universal-3-5-pro", credits: 3.5, wer: 3.0, speedFactor: null, languages: 98, streaming: false, customVocabulary: true, preview: false, isDefault: true, byok: true },
  { id: "assemblyAI:universal-2", name: "Universal-2", vendorLabel: "AssemblyAI", vendor: "AssemblyAI", sttProvider: "assemblyai", modelId: "universal-2", credits: 2.5, wer: 3.8, speedFactor: 123.1, languages: 98, streaming: false, customVocabulary: true, preview: false, isDefault: false, byok: true },
  { id: "mistralVoxtral:voxtral-mini-latest", name: "Voxtral Mini", vendorLabel: "Mistral Voxtral", vendor: "Mistral", sttProvider: "mistral", modelId: "voxtral-mini-latest", credits: 3.0, wer: 3.8, speedFactor: 78.1, languages: 13, streaming: false, customVocabulary: true, preview: false, isDefault: true, byok: true },
  { id: "soniox:stt-async-v5", name: "Async v5", vendorLabel: "Soniox", vendor: "Soniox", sttProvider: "soniox", modelId: "stt-async-v5", credits: 1.67, wer: 3.8, speedFactor: 18.8, languages: 60, streaming: false, customVocabulary: true, preview: false, isDefault: true, byok: true },
  { id: "gemini:gemini-2.5-flash", name: "Gemini 2.5 Flash", vendorLabel: "Google Gemini", vendor: "Google", sttProvider: "gemini", modelId: "gemini-2.5-flash", credits: 2.4, wer: 5.1, speedFactor: 73.7, languages: null, streaming: false, customVocabulary: true, preview: false, isDefault: true, byok: true },
  { id: "gemini:gemini-2.5-flash-lite", name: "Gemini 2.5 Flash Lite", vendorLabel: "Google Gemini", vendor: "Google", sttProvider: "gemini", modelId: "gemini-2.5-flash-lite", credits: 0.8, wer: 5.2, speedFactor: 70.7, languages: null, streaming: false, customVocabulary: true, preview: false, isDefault: false, byok: true },
  { id: "gemini:gemini-2.5-pro", name: "Gemini 2.5 Pro", vendorLabel: "Google Gemini", vendor: "Google", sttProvider: "gemini", modelId: "gemini-2.5-pro", credits: 7.5, wer: 2.9, speedFactor: 13.3, languages: null, streaming: false, customVocabulary: true, preview: false, isDefault: false, byok: true },
  { id: "gemini:gemini-3-flash-preview", name: "Gemini 3 Flash", vendorLabel: "Google Gemini", vendor: "Google", sttProvider: "gemini", modelId: "gemini-3-flash-preview", credits: 3.0, wer: 2.9, speedFactor: 16.1, languages: null, streaming: false, customVocabulary: true, preview: true, isDefault: false, byok: true },
  { id: "gemini:gemini-3.1-pro-preview", name: "Gemini 3.1 Pro", vendorLabel: "Google Gemini", vendor: "Google", sttProvider: "gemini", modelId: "gemini-3.1-pro-preview", credits: 10.0, wer: 2.8, speedFactor: 7.0, languages: null, streaming: false, customVocabulary: true, preview: true, isDefault: false, byok: true },
  // Muse is ranked on Artificial Analysis's streaming board, not on the
  // non-streaming board used by this page. Do not put that streaming WER or
  // speed figure beside batch results. Meta supports streaming upstream, but
  // HyperWhisper has no Meta live relay, so this batch row is not live-selectable.
  { id: "metaMuse:muse-voice-transcribe-1.0", name: "Muse Voice Transcribe 1.0", vendorLabel: "Meta Muse Voice Transcribe", vendor: "Meta", sttProvider: "meta", modelId: "muse-voice-transcribe-1.0", credits: 3.0, wer: null, speedFactor: null, languages: 25, streaming: false, customVocabulary: true, preview: false, isDefault: true, byok: true },
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
