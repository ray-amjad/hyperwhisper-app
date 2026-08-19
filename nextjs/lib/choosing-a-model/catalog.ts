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

/** Where the transcription runs. The page's most important distinction. */
export type ModelPlacement = "cloud" | "device";

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
  languages: number;
};

export type Model = CloudModel | DeviceModel;

/**
 * Mirrored from `cloud-stt-catalog.json` v7 (2026-08-13). Benchmark columns
 * from the Artificial Analysis leaderboard, pulled 2026-08-19.
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
  { id: "assemblyAI:universal-3-pro", name: "Universal-3 Pro", vendorLabel: "AssemblyAI", vendor: "AssemblyAI", sttProvider: "assemblyai", modelId: "universal-3-pro", credits: 3.5, wer: 3.1, speedFactor: 112.1, languages: 98, streaming: true, customVocabulary: true, preview: false, isDefault: false, byok: true },
  { id: "assemblyAI:universal-3-5-pro", name: "Universal-3.5 Pro", vendorLabel: "AssemblyAI", vendor: "AssemblyAI", sttProvider: "assemblyai", modelId: "universal-3-5-pro", credits: 3.5, wer: 3.0, speedFactor: null, languages: 98, streaming: true, customVocabulary: true, preview: false, isDefault: true, byok: true },
  { id: "assemblyAI:universal-2", name: "Universal-2", vendorLabel: "AssemblyAI", vendor: "AssemblyAI", sttProvider: "assemblyai", modelId: "universal-2", credits: 2.5, wer: 3.8, speedFactor: 123.1, languages: 98, streaming: true, customVocabulary: true, preview: false, isDefault: false, byok: true },
  { id: "mistralVoxtral:voxtral-mini-latest", name: "Voxtral Mini", vendorLabel: "Mistral Voxtral", vendor: "Mistral", sttProvider: "mistral", modelId: "voxtral-mini-latest", credits: 3.0, wer: 3.8, speedFactor: 78.1, languages: 13, streaming: true, customVocabulary: true, preview: false, isDefault: true, byok: true },
  { id: "soniox:stt-async-v4", name: "Async v4", vendorLabel: "Soniox", vendor: "Soniox", sttProvider: "soniox", modelId: "stt-async-v4", credits: 1.67, wer: 3.9, speedFactor: 19.0, languages: 60, streaming: true, customVocabulary: true, preview: false, isDefault: false, byok: true },
  { id: "soniox:stt-async-v5", name: "Async v5", vendorLabel: "Soniox", vendor: "Soniox", sttProvider: "soniox", modelId: "stt-async-v5", credits: 1.67, wer: 3.8, speedFactor: 18.8, languages: 60, streaming: true, customVocabulary: true, preview: false, isDefault: true, byok: true },
  { id: "gemini:gemini-2.5-flash", name: "Gemini 2.5 Flash", vendorLabel: "Google Gemini", vendor: "Google", sttProvider: "gemini", modelId: "gemini-2.5-flash", credits: 2.4, wer: 5.1, speedFactor: 73.7, languages: null, streaming: false, customVocabulary: true, preview: false, isDefault: true, byok: true },
  { id: "gemini:gemini-2.5-flash-lite", name: "Gemini 2.5 Flash Lite", vendorLabel: "Google Gemini", vendor: "Google", sttProvider: "gemini", modelId: "gemini-2.5-flash-lite", credits: 0.8, wer: 5.2, speedFactor: 70.7, languages: null, streaming: false, customVocabulary: true, preview: false, isDefault: false, byok: true },
  { id: "gemini:gemini-2.5-pro", name: "Gemini 2.5 Pro", vendorLabel: "Google Gemini", vendor: "Google", sttProvider: "gemini", modelId: "gemini-2.5-pro", credits: 7.5, wer: 2.9, speedFactor: 13.3, languages: null, streaming: false, customVocabulary: true, preview: false, isDefault: false, byok: true },
  { id: "gemini:gemini-3-flash-preview", name: "Gemini 3 Flash", vendorLabel: "Google Gemini", vendor: "Google", sttProvider: "gemini", modelId: "gemini-3-flash-preview", credits: 3.0, wer: 2.9, speedFactor: 16.1, languages: null, streaming: false, customVocabulary: true, preview: true, isDefault: false, byok: true },
  { id: "gemini:gemini-3.1-pro-preview", name: "Gemini 3.1 Pro", vendorLabel: "Google Gemini", vendor: "Google", sttProvider: "gemini", modelId: "gemini-3.1-pro-preview", credits: 10.0, wer: 2.8, speedFactor: 7.0, languages: null, streaming: false, customVocabulary: true, preview: true, isDefault: false, byok: true },
] as const;

export const CLOUD_MODELS: readonly CloudModel[] = CLOUD_MODELS_RAW.map(
  (model) => ({
    ...model,
    placement: "cloud" as const,
    accuracyBasis: model.wer === null ? ("none" as const) : ("measured" as const),
  }),
);

/**
 * Mirrored from the macOS (`ModelLibraryManager.swift`, `WhisperModel.swift`)
 * and Windows (`ModelLibraryManager.cs`, `WhisperModelInfo.cs`) model
 * libraries, including their 1-5 speed and accuracy ratings.
 *
 * Whisper and Parakeet carry a leaderboard word error rate because the
 * leaderboard measured those same open weights on a hosted runner. The rest
 * have no published figure and fall back to the app's own accuracy rating —
 * which is what keeps Whisper Tiny from scoring like a frontier model.
 */
const DEVICE_MODELS_RAW = [
  { id: "device:whisper-large-v3-turbo", name: "Whisper Large v3 Turbo", vendorLabel: "Whisper", platforms: ["macos", "windows"], size: "809 MB", sizeWindows: "1.5 GB", wer: 4.6, accuracyBasis: "sameWeights", speedRating: 4, accuracyRating: 3, languages: 100 },
  { id: "device:whisper-large-v3", name: "Whisper Large v3", vendorLabel: "Whisper", platforms: ["macos", "windows"], size: "3.1 GB", wer: 4.1, accuracyBasis: "sameWeights", speedRating: 3, accuracyRating: 3, languages: 100 },
  { id: "device:whisper-large-v2", name: "Whisper Large v2", vendorLabel: "Whisper", platforms: ["macos", "windows"], size: "2.9 GB", wer: 4.1, accuracyBasis: "sameWeights", speedRating: 3, accuracyRating: 3, languages: 100 },
  { id: "device:whisper-medium", name: "Whisper Medium", vendorLabel: "Whisper", platforms: ["macos", "windows"], size: "1.5 GB", wer: null, accuracyBasis: "appRating", speedRating: 4, accuracyRating: 3, languages: 100 },
  { id: "device:whisper-small", name: "Whisper Small", vendorLabel: "Whisper", platforms: ["macos", "windows"], size: "466 MB", wer: null, accuracyBasis: "appRating", speedRating: 4, accuracyRating: 2, languages: 100 },
  { id: "device:whisper-base", name: "Whisper Base", vendorLabel: "Whisper", platforms: ["macos", "windows"], size: "142 MB", wer: null, accuracyBasis: "appRating", speedRating: 5, accuracyRating: 1, languages: 100 },
  { id: "device:whisper-tiny", name: "Whisper Tiny", vendorLabel: "Whisper", platforms: ["macos", "windows"], size: "39 MB", wer: null, accuracyBasis: "appRating", speedRating: 5, accuracyRating: 1, languages: 100 },
  { id: "device:parakeet-v3", name: "Parakeet V3", vendorLabel: "Parakeet", platforms: ["macos", "windows"], size: "494 MB", wer: 4.5, accuracyBasis: "sameWeights", speedRating: 5, accuracyRating: 3, languages: 25 },
  { id: "device:parakeet-v2", name: "Parakeet V2", vendorLabel: "Parakeet", platforms: ["macos", "windows"], size: "474 MB", wer: 6.4, accuracyBasis: "sameWeights", speedRating: 5, accuracyRating: 3, languages: 1 },
  { id: "device:nemotron-multilingual", name: "Nemotron 3.5 Multilingual", vendorLabel: "Nemotron", platforms: ["macos"], size: "1.3 GB", wer: null, accuracyBasis: "appRating", speedRating: 5, accuracyRating: 4, languages: 40 },
  { id: "device:nemotron-latin", name: "Nemotron 3.5 Latin", vendorLabel: "Nemotron", platforms: ["macos"], size: "350 MB", wer: null, accuracyBasis: "appRating", speedRating: 5, accuracyRating: 4, languages: 6 },
  { id: "device:nemotron-streaming", name: "Nemotron 3.5 Streaming", vendorLabel: "Nemotron", platforms: ["windows"], size: "660 MB", wer: null, accuracyBasis: "appRating", speedRating: 5, accuracyRating: 4, languages: 2 },
  { id: "device:qwen3-asr", name: "Qwen3 ASR", vendorLabel: "Qwen3", platforms: ["macos"], size: "1.3 GB", wer: null, accuracyBasis: "appRating", speedRating: 4, accuracyRating: 1, languages: 11 },
  { id: "device:qwen3-asr-0.6b", name: "Qwen3 ASR 0.6B", vendorLabel: "Qwen3", platforms: ["windows"], size: "985 MB", wer: null, accuracyBasis: "appRating", speedRating: 3, accuracyRating: 4, languages: 11 },
  { id: "device:apple-speech", name: "Apple Speech", vendorLabel: "Apple", platforms: ["macos"], size: "Built in", wer: null, accuracyBasis: "appRating", speedRating: 5, accuracyRating: 3, languages: 60 },
] as const;

export const DEVICE_MODELS: readonly DeviceModel[] = DEVICE_MODELS_RAW.map(
  (model) => ({ ...model, placement: "device" as const }),
);

export const ALL_MODELS: readonly Model[] = [...CLOUD_MODELS, ...DEVICE_MODELS];

/** 1,000 credits buy $1 of transcription, so credits/min is also $/1,000 min. */
export const CREDITS_PER_DOLLAR = 1000;

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
