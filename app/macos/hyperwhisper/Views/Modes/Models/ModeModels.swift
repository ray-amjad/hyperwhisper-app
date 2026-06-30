//
//  ModeModels.swift
//  HyperWhisper
//
//  Data models and enums for mode configuration.
//

import SwiftUI

// MARK: - Preset Type

/// Defines the available preset types for transcription modes
enum PresetType: String, CaseIterable, Identifiable {
    case hyper = "hyper"
    case message = "message"
    case mail = "mail"
    case note = "note"
    case meeting = "meeting"
    case code = "code"
    case custom = "custom"

    var id: String { rawValue }

    /// Display name for the preset
    var displayName: String {
        switch self {
        case .hyper:
            return "modes.preset.hyper.name".localized
        case .message:
            return "modes.preset.message.name".localized
        case .mail:
            return "modes.preset.mail.name".localized
        case .note:
            return "modes.preset.note.name".localized
        case .meeting:
            return "modes.preset.meeting.name".localized
        case .code:
            return "modes.preset.code.name".localized
        case .custom:
            return "modes.preset.custom.name".localized
        }
    }

    /// Recommended badge for default preset
    var isRecommended: Bool {
        self == .hyper
    }

    /// Tooltip description for each preset
    var tooltipDescription: String {
        switch self {
        case .hyper:
            return "modes.preset.hyper.tooltip".localized
        case .message:
            return "modes.preset.message.tooltip".localized
        case .mail:
            return "modes.preset.mail.tooltip".localized
        case .note:
            return "modes.preset.note.tooltip".localized
        case .meeting:
            return "modes.preset.meeting.tooltip".localized
        case .code:
            return "modes.preset.code.tooltip".localized
        case .custom:
            return "modes.preset.custom.tooltip".localized
        }
    }

    /// Human-readable description of what the AI instructions do for this preset
    var previewDescription: String {
        switch self {
        case .hyper:
            return "modes.preset.hyper.preview".localized
        case .message:
            return "modes.preset.message.preview".localized
        case .mail:
            return "modes.preset.mail.preview".localized
        case .note:
            return "modes.preset.note.preview".localized
        case .meeting:
            return "modes.preset.meeting.preview".localized
        case .code:
            return "modes.preset.code.preview".localized
        case .custom:
            return "modes.preset.custom.preview".localized
        }
    }

}

// MARK: - Helper Functions

/// Check if a model is English-only
/// - Whisper models: has .en suffix (e.g., base.en, small.en)
/// - Parakeet V2: contains "v2" in the name (English-only, highest recall)
func isEnglishOnlyModel(provider: ProviderType, model: String) -> Bool {
    guard provider == .local else { return false }
    // Whisper English-only models have .en suffix
    if model.hasSuffix(".en") { return true }
    // Parakeet V2 is English-only (V3 is multilingual)
    if model.lowercased().hasPrefix("parakeet") && model.lowercased().contains("v2") { return true }
    return false
}

// Maximum number of characters allowed for user-supplied system prompts
let userSystemPromptCharacterLimit = 2000

// Maximum number of characters allowed for Gemini custom transcription prompts
let geminiCustomPromptCharacterLimit = 2000

// MARK: - Mode Data Transfer Object

/// Used by CreateModeView and EditModeView
struct ModeData {
    let id: UUID
    let name: String
    let preset: String
    let language: String
    let model: String
    let punctuation: Bool
    let capitalization: Bool
    let profanityFilter: Bool
    let customInstructions: String
    let languageModel: String  // Language model (e.g., "gpt-4.1-nano")
    let cloudProvider: String  // Cloud provider (e.g., "openai")
    let cloudTranscriptionModel: String  // Cloud transcription model (e.g., "whisper-1")
    let postProcessingMode: PostProcessingMode  // Post-processing mode (off/cloud/local)
    let postProcessingProvider: String  // Post-processing provider (e.g., "openai", "anthropic", "gemini")
    let englishSpelling: EnglishSpelling  // English spelling variant (american/british/australian/canadian)
    let userSystemPrompt: String  // Optional user-supplied system prompt append
    let useStreamingTranscription: Bool  // Whether to use real-time streaming transcription (HyperWhisper Cloud only)
    let cloudAccuracyTier: CloudAccuracyTier  // Accuracy tier for HyperWhisper Cloud (medium/high/highest)
    let removeTrailingPeriod: Bool  // Strip trailing period from transcriptions (useful for casual contexts)
    let enableScreenOCR: Bool  // Capture visible screen text to improve accuracy of names and terms
    let geminiCustomPrompt: String  // Custom instructions for Gemini transcription prompt
    let cloudPostProcessingModel: CloudPostProcessingModel  // LLM for HyperWhisper Cloud post-processing
    let cloudTranscriptionDomain: String?  // X-STT-Domain value ("medical") or nil — HyperWhisper Cloud only

    init(id: UUID = UUID(),
         name: String,
         preset: String = "hyper",
         language: String,
         model: String,
         punctuation: Bool,
         capitalization: Bool,
         profanityFilter: Bool,
         customInstructions: String = "",
         languageModel: String = PostProcessingModels.defaultModel(for: .openai)?.id ?? "gpt-4.1-nano",
         cloudProvider: String = "hyperwhisper",
         cloudTranscriptionModel: String = "whisper-1",
         postProcessingMode: PostProcessingMode = .cloud,
         postProcessingProvider: String? = nil,
         englishSpelling: EnglishSpelling = .american,
         userSystemPrompt: String = "",
         useStreamingTranscription: Bool = false,
         cloudAccuracyTier: CloudAccuracyTier = .deepgramNova3,
         removeTrailingPeriod: Bool = false,
         enableScreenOCR: Bool = false,
         geminiCustomPrompt: String = "",
         cloudPostProcessingModel: CloudPostProcessingModel = .grokFast,
         cloudTranscriptionDomain: String? = nil) {
        self.id = id
        self.name = name
        self.preset = preset
        self.language = LanguageData.canonicalLanguageCode(language)
        self.model = model
        self.punctuation = punctuation
        self.capitalization = capitalization
        self.profanityFilter = profanityFilter
        self.customInstructions = customInstructions
        self.languageModel = languageModel
        self.cloudProvider = cloudProvider
        self.cloudTranscriptionModel = cloudTranscriptionModel
        self.postProcessingMode = postProcessingMode
        self.englishSpelling = englishSpelling
        self.userSystemPrompt = String(userSystemPrompt.prefix(userSystemPromptCharacterLimit))
        self.useStreamingTranscription = useStreamingTranscription
        self.cloudAccuracyTier = cloudAccuracyTier
        self.removeTrailingPeriod = removeTrailingPeriod
        self.enableScreenOCR = enableScreenOCR
        self.geminiCustomPrompt = String(geminiCustomPrompt.prefix(geminiCustomPromptCharacterLimit))
        self.cloudPostProcessingModel = cloudPostProcessingModel
        // Normalize empty → nil so an unset domain is consistently nil downstream.
        let trimmedDomain = cloudTranscriptionDomain?.trimmingCharacters(in: .whitespacesAndNewlines)
        self.cloudTranscriptionDomain = (trimmedDomain?.isEmpty == false) ? trimmedDomain : nil
        if let provider = postProcessingProvider {
            self.postProcessingProvider = provider
        } else if let defaultProvider = postProcessingMode.defaultProvider {
            self.postProcessingProvider = defaultProvider.rawValue
        } else {
            self.postProcessingProvider = PostProcessingProvider.hyperwhisper.rawValue
        }
    }

    /// Determines if this mode can work offline (no internet required)
    var isOfflineCapable: Bool {
        // Mode is offline-capable when ALL components can work offline:
        // 1. ASR (Automatic Speech Recognition) provider is local
        // 2. Post-processing is either off or local (not cloud)
        let asrProvider = ProviderType(rawValue: model == "cloud" ? "cloud" : "local") ?? .local
        let asrIsLocal = asrProvider == .local
        let postProcessingIsOffline = postProcessingMode != .cloud

        return asrIsLocal && postProcessingIsOffline
    }

    /// Create ModeData from Core Data Mode entity
    init(from mode: Mode) {
        self.id = mode.id ?? UUID()
        self.name = mode.name ?? "Default"
        // Migrate removed "voiceToText" preset → set post-processing to off
        let rawPreset = mode.preset ?? "hyper"
        let isLegacyVoiceToText = rawPreset == "voiceToText"
        self.preset = isLegacyVoiceToText ? "hyper" : rawPreset
        self.language = LanguageData.canonicalLanguageCode(mode.language)
        self.model = mode.model ?? "base"
        self.cloudProvider = mode.cloudProvider ?? "hyperwhisper"
        self.cloudTranscriptionModel = CloudTranscriptionModels.resolveAssemblyAIModelAlias(mode.cloudTranscriptionModel ?? "whisper-1")
        // Convert from Core Data Int16 to enum, default to cloud for backward compatibility
        let processingMode = isLegacyVoiceToText ? .off : (PostProcessingMode(rawValue: mode.postProcessingMode) ?? .cloud)
        self.postProcessingMode = processingMode

        if processingMode == .local {
            let stored = PostProcessingProvider(rawValue: mode.postProcessingProvider ?? "")
            self.languageModel = mode.languageModel ?? PostProcessingProvider.localLLM.defaultModel
            self.postProcessingProvider = PostProcessingProvider.localLLM.rawValue
        } else {
            self.languageModel = mode.languageModel ?? "gpt-4.1-nano"
            if let provider = mode.postProcessingProvider {
                self.postProcessingProvider = provider
            } else if let defaultProvider = processingMode.defaultProvider {
                self.postProcessingProvider = defaultProvider.rawValue
            } else {
                self.postProcessingProvider = PostProcessingProvider.hyperwhisper.rawValue
            }
        }
        self.punctuation = mode.punctuation
        self.capitalization = mode.capitalization
        self.profanityFilter = mode.profanityFilter
        self.customInstructions = mode.customInstructions ?? ""
        self.englishSpelling = EnglishSpelling(rawValue: mode.englishSpelling ?? "american") ?? .american
        if let prompt = mode.userSystemPrompt {
            self.userSystemPrompt = String(prompt.prefix(userSystemPromptCharacterLimit))
        } else {
            self.userSystemPrompt = ""
        }
        self.useStreamingTranscription = mode.useStreamingTranscription
        self.cloudAccuracyTier = CloudAccuracyTier.fromStorageValue(mode.cloudAccuracyTier)
        self.removeTrailingPeriod = mode.removeTrailingPeriod
        self.enableScreenOCR = mode.enableScreenOCR
        if let prompt = mode.geminiCustomPrompt {
            self.geminiCustomPrompt = String(prompt.prefix(geminiCustomPromptCharacterLimit))
        } else {
            self.geminiCustomPrompt = ""
        }
        self.cloudPostProcessingModel = CloudPostProcessingModel.fromStorageValue(mode.cloudPostProcessingModel)
        let storedDomain = mode.cloudTranscriptionDomain?.trimmingCharacters(in: .whitespacesAndNewlines)
        self.cloudTranscriptionDomain = (storedDomain?.isEmpty == false) ? storedDomain : nil
    }
}

// MARK: - English Spelling

/// English spelling variant for post-processing
/// Only applies when post-processing is enabled and language is English
enum EnglishSpelling: String, CaseIterable, Identifiable {
    case american = "american"
    case british = "british"
    case australian = "australian"
    case canadian = "canadian"

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .american:
            return "modes.englishSpelling.american".localized
        case .british:
            return "modes.englishSpelling.british".localized
        case .australian:
            return "modes.englishSpelling.australian".localized
        case .canadian:
            return "modes.englishSpelling.canadian".localized
        }
    }

    var description: String {
        switch self {
        case .american:
            return "modes.englishSpelling.american.description".localized
        case .british:
            return "modes.englishSpelling.british.description".localized
        case .australian:
            return "modes.englishSpelling.australian.description".localized
        case .canadian:
            return "modes.englishSpelling.canadian.description".localized
        }
    }
}

// MARK: - Post-Processing Mode

/// Post-processing mode for transcriptions
enum PostProcessingMode: Int16, CaseIterable {
    case off = 0      // No post-processing
    case cloud = 1    // Cloud-based AI post-processing (OpenAI)
    case local = 2    // Local LLM post-processing (Qwen)

    var displayName: String {
        switch self {
        case .off:
            return "Off"
        case .cloud:
            return "Cloud"
        case .local:
            return "Local"
        }
    }

    var requiresInternet: Bool {
        switch self {
        case .off:
            return false
        case .cloud:
            return true
        case .local:
            return false
        }
    }

    /// Default provider for this post-processing mode
    var defaultProvider: PostProcessingProvider? {
        switch self {
        case .off:
            return nil
        case .cloud:
            return .hyperwhisper  // HyperWhisper Cloud is the default for cloud post-processing
        case .local:
            return .localLLM
        }
    }

    /// Whether the user can pick a language model for this mode
    var allowsModelSelection: Bool {
        switch self {
        case .off:
            return false
        case .cloud, .local:
            return true
        }
    }

    /// Whether the app should run provider health checks before using this mode
    var requiresHealthCheck: Bool {
        switch self {
        case .cloud:
            return true
        case .off, .local:
            return false
        }
    }
}

// MARK: - Cloud Accuracy Tier

/// Accuracy tier for HyperWhisper Cloud transcription
/// Maps to different STT providers on the backend with varying speed/accuracy tradeoffs
/// The raw values match the `id` of each `cloudTierEligible` entry in
/// `shared-app-classification/cloud-stt-catalog.json` verbatim, so catalog
/// lookups (`CloudSTTCatalog.shared.entry(byId:)`) resolve directly. The enum
/// stays exhaustive (12 cases) because several call sites switch on it for
/// localized labels; the per-tier facts that come from the catalog
/// (`sttProvider`, credits, models, vocab) are sourced from the catalog so they
/// can't drift from the backend.
enum CloudAccuracyTier: String, CaseIterable, Identifiable {
    case groqWhisper = "groqWhisper"
    case deepgramNova3 = "deepgramNova3"
    case grokStt = "grokStt"
    case azureMaiTranscribe = "azureMaiTranscribe"
    case googleChirp3 = "googleChirp3"
    case elevenLabsScribeV2 = "elevenLabsScribeV2"
    case openaiWhisper = "openaiWhisper"
    case assemblyAI = "assemblyAI"
    case mistralVoxtral = "mistralVoxtral"
    case soniox = "soniox"
    case gemini = "gemini"

    var id: String { rawValue }

    /// Tiers shown in the Provider dropdown (level 1), in catalog order so the
    /// macOS list stays aligned with the shared source of truth and Windows.
    /// Falls back to `allCases` only if the catalog failed to load (so the UI
    /// never goes empty).
    static var pickerOrder: [CloudAccuracyTier] {
        let ordered = CloudSTTCatalog.shared.cloudTierEntries.compactMap {
            CloudAccuracyTier(rawValue: $0.id)
        }
        return ordered.isEmpty ? allCases : ordered
    }

    static func fromStorageValue(_ value: String?) -> CloudAccuracyTier {
        let normalized = value?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if normalized.isEmpty { return .deepgramNova3 }

        // Canonical raw value match first (case-insensitive).
        if let exact = CloudAccuracyTier.allCases.first(where: { $0.rawValue.lowercased() == normalized.lowercased() }) {
            return exact
        }

        // Catalog-driven legacy alias migration. Keeps the rename rules in
        // shared-app-classification/cloud-stt-catalog.json rather than scattered
        // hardcoded switch arms on each platform.
        if let migrated = CloudSTTCatalog.shared.entry(byMigrateFromAlias: normalized),
           let tier = CloudAccuracyTier(rawValue: migrated.id) {
            return tier
        }

        return .deepgramNova3
    }

    /// Display name shown in the Provider dropdown. Every tier reads its plain
    /// provider/model name from the shared cloud-STT catalog so the list is
    /// consistent — no "Medium/High/Highest" accuracy-rank prefix (that signal
    /// lives in the per-row description and credits caption instead). The
    /// "(Recommended)" tag is appended by the picker, not here. Falls back to
    /// the raw value if the catalog failed to load.
    var displayName: String {
        CloudSTTCatalog.shared.entry(byId: rawValue)?.displayName ?? rawValue
    }

    /// Whether this engine (provider/tier) is the recommended default for
    /// HyperWhisper Cloud transcription. Drives the "(Recommended)" badge on the
    /// Engine dropdown. ElevenLabs Scribe v2 is the recommended engine.
    var isRecommended: Bool {
        self == .elevenLabsScribeV2
    }

    var description: String {
        switch self {
        case .groqWhisper:
            return "modes.cloudAccuracy.groqWhisper.description".localized
        case .deepgramNova3:
            return "modes.cloudAccuracy.deepgramNova3.description".localized
        case .elevenLabsScribeV2:
            return "modes.cloudAccuracy.elevenLabsScribeV2.description".localized
        case .grokStt:
            return "modes.cloudAccuracy.grokStt.description".localized
        case .azureMaiTranscribe:
            return "modes.cloudAccuracy.azureMaiTranscribe.description".localized
        case .googleChirp3:
            return "modes.cloudAccuracy.googleChirp3.description".localized
        case .openaiWhisper, .assemblyAI, .mistralVoxtral, .soniox, .gemini:
            // No localized description string for the catalog-v6 additions —
            // fall back to the provider display name.
            return displayName
        }
    }

    /// Maps accuracy tier to the `X-STT-Provider` header value for the backend.
    /// Sourced from the catalog `sttProvider` field so it can't drift; a
    /// hardcoded fallback covers a catalog-load failure for the original tiers.
    var sttProvider: String {
        if let fromCatalog = CloudSTTCatalog.shared.sttProvider(forEntryId: rawValue) {
            return fromCatalog
        }
        switch self {
        case .groqWhisper:
            return "groq"
        case .deepgramNova3:
            return "deepgram"
        case .elevenLabsScribeV2:
            return "elevenlabs"
        case .grokStt:
            return "grok"
        case .azureMaiTranscribe:
            return "azure-mai"
        case .googleChirp3:
            return "google-chirp"
        case .openaiWhisper:
            return "openai"
        case .assemblyAI:
            return "assemblyai"
        case .mistralVoxtral:
            return "mistral"
        case .soniox:
            return "soniox"
        case .gemini:
            return "gemini"
        }
    }

    /// Approximate cost in credits per minute of audio for the tier's *default*
    /// model. Sourced from the shared catalog
    /// (`shared-app-classification/cloud-stt-catalog.json`), which is the
    /// cross-platform truth and stays aligned with the backend
    /// `cost-calculator.ts`. Display-only — actual billed credits depend on
    /// rounding and per-provider minimum-billable-duration rules. Prefer
    /// `creditsPerMinute(forModelId:)` when a specific model is selected.
    var creditsPerMinute: Double {
        if let defaultCredits = CloudSTTCatalog.shared.defaultModel(forEntryId: rawValue)?.creditsPerMinute {
            return defaultCredits
        }
        // Fall back to the provider-level cloudTier credits for older catalogs.
        return CloudSTTCatalog.shared.entry(byId: rawValue)?.cloudTier?.creditsPerMinute ?? 0
    }

    /// Credits/min for a specific model id within this tier. Falls back to the
    /// tier default when the model id is empty or unknown.
    func creditsPerMinute(forModelId modelId: String) -> Double {
        if !modelId.isEmpty,
           let model = CloudSTTCatalog.shared.model(forEntryId: rawValue, modelId: modelId),
           let credits = model.creditsPerMinute {
            return credits
        }
        return creditsPerMinute
    }

    /// All selectable models for this tier (catalog order).
    var models: [CloudSTTCatalog.Model] {
        CloudSTTCatalog.shared.models(forEntryId: rawValue)
    }

    /// The catalog default model id for this tier ("" when single-model /
    /// no models — the backend then applies its own default).
    var defaultModelId: String {
        CloudSTTCatalog.shared.defaultModelId(forEntryId: rawValue)
    }

    /// True when the catalog flags this tier's *default* model as supporting
    /// custom-vocabulary phrase biasing. Catalog `unverified` / missing is
    /// treated as unsupported (conservative default). Prefer
    /// `supportsCustomVocabulary(forModelId:)` once a model is selected.
    var supportsCustomVocabulary: Bool {
        if let model = CloudSTTCatalog.shared.defaultModel(forEntryId: rawValue) {
            return model.supportsCustomVocabulary == true
        }
        return CloudSTTCatalog.shared.entry(byId: rawValue)?.customVocabulary?.supported == .yes
    }

    /// True when the given model id within this tier supports custom vocabulary.
    /// Falls back to the tier default when the model id is empty or unknown.
    func supportsCustomVocabulary(forModelId modelId: String) -> Bool {
        if !modelId.isEmpty,
           let model = CloudSTTCatalog.shared.model(forEntryId: rawValue, modelId: modelId) {
            return model.supportsCustomVocabulary == true
        }
        return supportsCustomVocabulary
    }

    /// Localized "~X credits/min" caption shown under the picker, for the
    /// given model id (empty → tier default model).
    func creditsPerMinuteLabel(forModelId modelId: String) -> String {
        let credits = creditsPerMinute(forModelId: modelId)
        let formatted: String
        if credits >= 10 {
            formatted = String(format: "%.0f", credits)
        } else {
            formatted = String(format: "%.1f", credits)
        }
        return String(format: "modes.cloudAccuracy.creditsPerMinute".localized, formatted)
    }

    /// Localized "~X credits/min" caption for the tier's default model.
    var creditsPerMinuteLabel: String {
        creditsPerMinuteLabel(forModelId: "")
    }
}

// MARK: - Cloud Post-Processing Model

/// A catalog-backed reference to a HyperWhisper Cloud post-processing model —
/// the `(engine, model)` pair that drives the `/post-process` request via the
/// `X-LLM-Provider` (engine) / `X-LLM-Model` (model) headers.
///
/// Reads its facts (display name, header values, recommended flag) from
/// `CloudPPCatalog.shared`, mirroring how `CloudAccuracyTier` + `CloudSTTCatalog`
/// drive the transcription Engine/Model split. Persisted to the free-string
/// `Mode.cloudPostProcessingModel` Core Data field as a **provider-qualified
/// key** `"<engineId>:<modelId>"` (e.g. `cerebras:gpt-oss-120b`,
/// `openai:gpt-5-mini`) so Groq and Cerebras don't collide on the shared
/// `gpt-oss-120b` model id. `fromStorageValue` migrates every legacy value.
struct CloudPostProcessingModel: Identifiable, Hashable {
    /// Catalog engine (provider) id — the storage-key prefix and `X-LLM-Provider` source.
    let engineId: String
    /// Catalog model id within the engine — the storage-key suffix and `X-LLM-Model` source.
    let modelId: String

    /// Stable identity == the persisted storage key.
    var id: String { storageValue }

    /// Provider-qualified key persisted to `Mode.cloudPostProcessingModel`.
    var storageValue: String { "\(engineId):\(modelId)" }

    /// Back-compat alias kept so existing call sites that wrote `.rawValue`
    /// (when this was a string enum) keep compiling — identical to `storageValue`.
    var rawValue: String { storageValue }

    private var catalogProvider: CloudPPCatalog.Provider? {
        CloudPPCatalog.shared.provider(byId: engineId)
    }
    private var catalogModel: CloudPPCatalog.Model? {
        CloudPPCatalog.shared.model(forProviderId: engineId, modelId: modelId)
    }

    var displayName: String { catalogModel?.displayName ?? modelId }

    /// Value for the `X-LLM-Provider` header (the catalog engine's `llmProvider`),
    /// or nil when the engine is unknown (let the backend apply its default).
    var llmProviderHeader: String? { catalogProvider?.llmProvider }

    /// Value for the `X-LLM-Model` header (the catalog model's header / id),
    /// or nil when the model is unknown.
    var llmModelHeader: String? { catalogModel?.modelHeader }

    // MARK: Legacy / default factory values

    static var cerebrasGptOss120B: CloudPostProcessingModel { .init(engineId: "cerebras", modelId: "gpt-oss-120b") }
    static var groqGptOss120B: CloudPostProcessingModel { .init(engineId: "groq", modelId: "openai/gpt-oss-120b") }
    static var grokFast: CloudPostProcessingModel { .init(engineId: "grok", modelId: "grok-4.3") }
    static var claudeHaiku: CloudPostProcessingModel { .init(engineId: "anthropic", modelId: "claude-haiku-4-5") }

    /// Fallback used when the stored value is empty/unknown. Preserves the
    /// historical default (Grok) so modes with an unset value don't silently
    /// change engine on upgrade.
    static let fallback: CloudPostProcessingModel = .grokFast

    /// Resolve a persisted `cloudPostProcessingModel` storage string. Accepts the
    /// new provider-qualified `"<engineId>:<modelId>"` form (validated against the
    /// catalog) plus every legacy enum raw value and alias.
    static func fromStorageValue(_ value: String?) -> CloudPostProcessingModel {
        let trimmed = value?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if trimmed.isEmpty { return fallback }

        // New provider-qualified format "<engineId>:<modelId>". Canonicalize the
        // engine id to the catalog's casing so the engine derived from this value
        // matches an entry in `CloudPostProcessingEngine.allCases` (the Picker tag
        // compares case-sensitively).
        if let colon = trimmed.firstIndex(of: ":") {
            let rawEngineId = String(trimmed[..<colon])
            let modelId = String(trimmed[trimmed.index(after: colon)...])
            if let provider = CloudPPCatalog.shared.provider(byId: rawEngineId) {
                if let model = CloudPPCatalog.shared.model(forProviderId: provider.id, modelId: modelId) {
                    return .init(engineId: provider.id, modelId: model.id)
                }
                // Known engine, unknown model → that engine's default model.
                if let def = CloudPPCatalog.shared.defaultModel(forProviderId: provider.id) {
                    return .init(engineId: provider.id, modelId: def.id)
                }
            }
            // Unknown engine → fall through to the legacy single-token table.
        }

        // Legacy single-token values (case-insensitive) — the pre-catalog enum
        // raw values and provider aliases.
        switch trimmed.lowercased() {
        case "cerebras", "cerebras-gpt-oss-120b", "cerebrasgptoss120b", "gpt-oss-120b", "default":
            return .cerebrasGptOss120B
        case "groq", "groq-gpt-oss-120b", "groqgptoss120b", "openai/gpt-oss-120b":
            return .groqGptOss120B
        case "anthropic", "claude-haiku-4-5", "claude-haiku-4.5", "claudehaiku":
            return .claudeHaiku
        case "grok", "grok-4.3", "grokfast",
             "grok-4-1-fast-non-reasoning", "grok-4.1-fast-non-reasoning",
             "grok-4-fast-non-reasoning", "grok-4-1-fast-reasoning", "grok-4-fast-reasoning":
            return .grokFast
        default:
            return fallback
        }
    }
}

// MARK: - Cloud Post-Processing Engine

/// Catalog-backed engine (provider) — the first axis of the HyperWhisper Cloud
/// post-processing picker, structurally identical to the transcription
/// Engine + Model split. The engine list, display names, recommended flag, and
/// per-engine models all come from `CloudPPCatalog.shared`. `enabled: false`
/// engines (un-deployed on the backend) are hidden from `allCases`.
struct CloudPostProcessingEngine: Identifiable, Hashable {
    /// Catalog provider id (e.g. `anthropic`).
    let id: String

    /// Engines shown in the Engine dropdown, in catalog order, enabled only.
    /// Falls back to the original four hardcoded engines if the catalog failed
    /// to load (so the picker never goes empty).
    static var allCases: [CloudPostProcessingEngine] {
        let fromCatalog = CloudPPCatalog.shared.pickerProviders.map { CloudPostProcessingEngine(id: $0.id) }
        return fromCatalog.isEmpty
            ? ["cerebras", "groq", "anthropic", "grok"].map { CloudPostProcessingEngine(id: $0) }
            : fromCatalog
    }

    private var catalogProvider: CloudPPCatalog.Provider? {
        CloudPPCatalog.shared.provider(byId: id)
    }

    /// Provider name shown in the Engine dropdown.
    var displayName: String { catalogProvider?.displayName ?? id }

    /// True for the single engine flagged `isRecommended` in the catalog
    /// (Anthropic / Claude Haiku 4.5 today).
    var isRecommended: Bool { catalogProvider?.isRecommended == true }

    /// Models available under this engine, in catalog order (enabled only).
    var models: [CloudPostProcessingModel] {
        CloudPPCatalog.shared.models(forProviderId: id).map {
            CloudPostProcessingModel(engineId: id, modelId: $0.id)
        }
    }

    /// Recommended/default model for this engine.
    var defaultModel: CloudPostProcessingModel {
        if let def = CloudPPCatalog.shared.defaultModel(forProviderId: id) {
            return CloudPostProcessingModel(engineId: id, modelId: def.id)
        }
        return models.first ?? .fallback
    }

    /// The engine that owns a given post-processing model.
    static func engine(for model: CloudPostProcessingModel) -> CloudPostProcessingEngine {
        CloudPostProcessingEngine(id: model.engineId)
    }

    /// True when a given model should be tagged "(Recommended)" in the Model
    /// dropdown — the recommended engine's default model.
    func isRecommendedModel(_ model: CloudPostProcessingModel) -> Bool {
        isRecommended && model == defaultModel
    }
}

// MARK: - Transcription Source

/// UI-only 3-way source axis for the transcription picker. Replaces the old
/// implicit Local/Cloud toggle + provider dropdown with one explicit choice.
/// Derived from (and applied back onto) the persisted `provider` + `cloudProvider`
/// fields — no new storage:
///   .onDevice          ⇄ provider == .local
///   .hyperwhisperCloud ⇄ provider == .cloud && cloudProvider == "hyperwhisper"
///   .yourProvider      ⇄ provider == .cloud && cloudProvider != "hyperwhisper" (BYOK)
enum TranscriptionSource: String, CaseIterable, Identifiable {
    case onDevice
    case hyperwhisperCloud
    case yourProvider

    var id: String { rawValue }

    var label: String {
        switch self {
        case .onDevice:
            return "modes.source.onDevice".localized
        case .hyperwhisperCloud:
            return "modes.source.hyperwhisperCloud".localized
        case .yourProvider:
            return "modes.source.yourProvider".localized
        }
    }
}

// MARK: - Provider Type

enum ProviderType: String, CaseIterable, Identifiable {
    case local
    case cloud
    var id: String { rawValue }
    var label: String {
        switch self {
        case .local:
            return "modes.provider.local".localized
        case .cloud:
            return "modes.provider.cloud".localized
        }
    }
}
