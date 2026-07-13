//
//  ModeEditorView.swift
//  HyperWhisper
//
//  Unified view for creating and editing modes.
//  Consolidates CreateModeView and EditModeView into a single component.
//

import SwiftUI

// MARK: - Configuration

/// Configuration for ModeEditorView behavior.
/// Determines whether the view is in create or edit mode.
enum ModeEditorConfiguration {
    case create
    case edit(mode: Mode, onDelete: (() -> Void)?)

    /// Returns true if in edit mode
    var isEditMode: Bool {
        if case .edit = self { return true }
        return false
    }

    /// Returns the Mode being edited, or nil if creating
    var mode: Mode? {
        if case .edit(let mode, _) = self { return mode }
        return nil
    }

    /// Returns the delete callback, or nil if creating
    var onDelete: (() -> Void)? {
        if case .edit(_, let callback) = self { return callback }
        return nil
    }
}

// MARK: - ModeEditorView

/// Unified view for creating and editing modes.
/// Uses ModeEditorConfiguration to handle create vs edit mode differences.
struct ModeEditorView: View {
    let configuration: ModeEditorConfiguration
    let availableModelIds: [String]
    let onSave: (ModeData) -> Void

    @Environment(\.dismiss) var dismiss
    @EnvironmentObject var appState: AppState
    @EnvironmentObject var settingsManager: SettingsManager
    @EnvironmentObject var whisperModelManager: WhisperModelManager
    @EnvironmentObject var parakeetModelManager: ParakeetModelManager
    @EnvironmentObject var cloudHealth: CloudProviderHealthManager

    // MARK: - State Properties

    @State private var name: String
    @State private var preset: String
    @State private var language: String
    @State private var model: String
    @State private var provider: ProviderType
    @State private var punctuation: Bool
    @State private var capitalization: Bool
    @State private var profanityFilter: Bool
    @State private var customInstructions: String
    @State private var languageModel: String
    @State private var postProcessingMode: PostProcessingMode
    @State private var postProcessingProvider: String
    @State private var showCloudModelInfo = false
    @State private var cloudProvider: String
    @State private var cloudAccuracyTier: String
    @State private var cloudPostProcessingModel: String
    @State private var cloudTranscriptionModel: String
    @State private var cloudTranscriptionDomain: String?
    @State private var showAllCloudTranscriptionModels: Bool
    @State private var englishSpelling: EnglishSpelling
    @State private var userSystemPrompt: String
    @State private var removeTrailingPeriod: Bool
    @State private var enableScreenOCR: Bool
    @State private var geminiCustomPrompt: String

    // MARK: - Source-toggle memory
    //
    // The 3-way transcription Source toggle re-derives the persisted
    // provider/cloudProvider fields. Without these, briefly toggling Source
    // away from a saved selection and back would clobber it with a freshly
    // seeded default. We remember the prior selection per-source for the
    // lifetime of the edit session and restore it on toggle-back, only
    // seeding a fresh default when there is genuinely no prior selection.

    /// Last BYOK ("Your provider") transcription provider/model the user had
    /// selected, so toggling away to HW Cloud / On-device and back restores it.
    @State private var lastDirectCloudProvider: String?
    @State private var lastDirectCloudTranscriptionModel: String?

    /// Last HyperWhisper Cloud transcription model + medical domain, so toggling
    /// away and back doesn't reset to the tier default / clear the domain.
    @State private var lastHyperwhisperCloudTranscriptionModel: String?
    @State private var lastHyperwhisperCloudTranscriptionDomain: String?

    // MARK: - Initialization

    init(configuration: ModeEditorConfiguration, availableModelIds: [String], onSave: @escaping (ModeData) -> Void) {
        self.configuration = configuration
        self.availableModelIds = availableModelIds
        self.onSave = onSave

        if let mode = configuration.mode {
            // EDIT MODE: Initialize from existing Mode object
            _name = State(initialValue: mode.name ?? "Default")
            // Migrate removed "voiceToText" preset → post-processing off
            let rawPreset = mode.preset ?? "hyper"
            let isLegacyVoiceToText = rawPreset == "voiceToText"
            _preset = State(initialValue: isLegacyVoiceToText ? "hyper" : rawPreset)
            _language = State(initialValue: LanguageData.canonicalLanguageCode(mode.language))

            // Resolve model and provider
            let initialModel = mode.model ?? "base"
            let initialProvider: ProviderType = (mode.model ?? "base").lowercased() == "cloud" ? .cloud : .local
            var resolvedProvider = initialProvider
            var resolvedModel = initialModel

            if resolvedProvider == .local {
                if availableModelIds.isEmpty {
                    resolvedProvider = .cloud
                } else if !availableModelIds.contains(initialModel) {
                    // Sort according to WhisperModel enum order
                    let canonicalOrder = WhisperModel.allCases.map { $0.rawValue }
                    let sorted = availableModelIds.sorted { first, second in
                        let firstIndex = canonicalOrder.firstIndex(of: first) ?? Int.max
                        let secondIndex = canonicalOrder.firstIndex(of: second) ?? Int.max
                        return firstIndex < secondIndex
                    }
                    resolvedModel = sorted.first ?? initialModel
                }
            }

            _model = State(initialValue: resolvedModel)
            _provider = State(initialValue: resolvedProvider)
            _punctuation = State(initialValue: mode.punctuation)
            _capitalization = State(initialValue: mode.capitalization)
            _profanityFilter = State(initialValue: mode.profanityFilter)
            _customInstructions = State(initialValue: mode.customInstructions ?? "")
            _languageModel = State(initialValue: mode.languageModel ?? "gpt-4.1-nano")
            let processingMode = isLegacyVoiceToText ? PostProcessingMode.off : (PostProcessingMode(rawValue: mode.postProcessingMode) ?? .cloud)
            _postProcessingMode = State(initialValue: processingMode)

            let initialCloudTranscriptionModel = CloudTranscriptionModels.resolveAssemblyAIModelAlias(mode.cloudTranscriptionModel ?? "whisper-1")

            // Azure MAI / Google Chirp legacy provider values are folded into
            // HyperWhisper Cloud accuracy tiers via the catalog. If the saved
            // cloudProvider is a legacy standalone-provider alias, surface that
            // entry as the accuracy tier and snap cloudProvider to "hyperwhisper".
            let savedCloudProvider = mode.cloudProvider
            let normalizedCloudProvider = CloudSTTCatalog.shared.normalizeCloudProvider(savedCloudProvider)
            let legacyProviderTier: CloudAccuracyTier? = normalizedCloudProvider.accuracyTier
                .flatMap { CloudAccuracyTier(rawValue: $0) }
            let migratedCloudProviderRaw = legacyProviderTier != nil
                ? CloudProvider.hyperwhisper.rawValue
                : (savedCloudProvider ?? "hyperwhisper")
            let migratedAccuracyTierRaw = legacyProviderTier?.rawValue
                ?? CloudAccuracyTier.fromStorageValue(mode.cloudAccuracyTier).rawValue
            let initialCloudProvider = CloudProvider(rawValue: migratedCloudProviderRaw) ?? .hyperwhisper

            // Resolve post-processing provider — compute against the migrated
            // cloudProvider so legacy Azure/Google modes get the HW Cloud
            // post-processing default after migration.
            let providerValue = mode.postProcessingProvider ?? processingMode.defaultProvider?.rawValue ?? "hyperwhisper"
            let resolvedPostProvider: String = {
                if initialCloudProvider == .hyperwhisper,
                   processingMode == .cloud,
                   mode.postProcessingProvider == nil {
                    return PostProcessingProvider.hyperwhisper.rawValue
                }
                return providerValue
            }()

            _postProcessingProvider = State(initialValue: resolvedPostProvider)
            _cloudProvider = State(initialValue: migratedCloudProviderRaw)
            _cloudTranscriptionModel = State(initialValue: initialCloudTranscriptionModel)
            _showAllCloudTranscriptionModels = State(initialValue: Self.shouldShowAllCloudTranscriptionModels(
                provider: initialCloudProvider,
                selectedModelId: initialCloudTranscriptionModel
            ))
            _englishSpelling = State(initialValue: EnglishSpelling(rawValue: mode.englishSpelling ?? "american") ?? .american)
            _userSystemPrompt = State(initialValue: mode.userSystemPrompt ?? "")
            _cloudAccuracyTier = State(initialValue: migratedAccuracyTierRaw)
            _cloudPostProcessingModel = State(initialValue: CloudPostProcessingModel.fromStorageValue(mode.cloudPostProcessingModel).rawValue)
            _removeTrailingPeriod = State(initialValue: mode.removeTrailingPeriod)
            _enableScreenOCR = State(initialValue: mode.enableScreenOCR)
            _geminiCustomPrompt = State(initialValue: mode.geminiCustomPrompt ?? "")
            let storedDomain = mode.cloudTranscriptionDomain?.trimmingCharacters(in: .whitespacesAndNewlines)
            _cloudTranscriptionDomain = State(initialValue: (storedDomain?.isEmpty == false) ? storedDomain : nil)
        } else {
            // CREATE MODE: Initialize with defaults
            _name = State(initialValue: "")
            _preset = State(initialValue: "hyper")
            _language = State(initialValue: LanguageData.automaticCode)
            _punctuation = State(initialValue: true)
            _capitalization = State(initialValue: true)
            _profanityFilter = State(initialValue: false)
            _customInstructions = State(initialValue: "")
            _languageModel = State(initialValue: PostProcessingModels.defaultModel(for: .openai)?.id ?? "gpt-4.1-nano")
            _postProcessingMode = State(initialValue: .cloud)
            _postProcessingProvider = State(initialValue: PostProcessingProvider.hyperwhisper.rawValue)
            _cloudProvider = State(initialValue: "hyperwhisper")
            // Recommended HyperWhisper Cloud defaults: ElevenLabs Scribe v2
            // transcription + Anthropic Claude Haiku 4.5 post-processing. Seed the
            // transcription model to the recommended tier's catalog default so the
            // Model dropdown opens on "Scribe v2 (Recommended)".
            _cloudTranscriptionModel = State(initialValue: CloudAccuracyTier.elevenLabsScribeV2.defaultModelId)
            _showAllCloudTranscriptionModels = State(initialValue: false)
            _englishSpelling = State(initialValue: .american)
            _userSystemPrompt = State(initialValue: "")
            _cloudAccuracyTier = State(initialValue: CloudAccuracyTier.elevenLabsScribeV2.rawValue)
            _cloudPostProcessingModel = State(initialValue: CloudPostProcessingModel.claudeHaiku.rawValue)
            _removeTrailingPeriod = State(initialValue: false)
            _enableScreenOCR = State(initialValue: false)
            _geminiCustomPrompt = State(initialValue: "")
            _cloudTranscriptionDomain = State(initialValue: nil)

            // Default to cloud provider with HyperWhisper Cloud
            _provider = State(initialValue: .cloud)

            // Initialize model for local fallback
            if !availableModelIds.isEmpty {
                let canonicalOrder = WhisperModel.allCases.map { $0.rawValue }
                let sorted = availableModelIds.sorted { first, second in
                    let firstIndex = canonicalOrder.firstIndex(of: first) ?? Int.max
                    let secondIndex = canonicalOrder.firstIndex(of: second) ?? Int.max
                    return firstIndex < secondIndex
                }
                _model = State(initialValue: sorted.first ?? "base")
            } else {
                _model = State(initialValue: "base")
            }
        }
    }

    // MARK: - Computed Properties

    // Non-Whisper models get explicit positions before Whisper models
    private static let nonWhisperOrder: [String: Int] = [
        "apple-speech-analyzer": 0,
        "parakeet-tdt-0.6b-v3": 1,
        "qwen3-asr-0.6b": 2,
        NemotronModelManager.Constants.latinModelId: 3,
        NemotronModelManager.Constants.multilingualModelId: 4
    ]
    private static let canonicalOrder: [String] = WhisperModel.allCases.map { $0.rawValue }

    /// Sort model IDs according to WhisperModel enum order
    private func sortedModelIds() -> [String] {
        let whisperOffset = Self.nonWhisperOrder.count
        return availableModelIds.sorted { first, second in
            let firstIndex = Self.nonWhisperOrder[first] ?? ((Self.canonicalOrder.firstIndex(of: first).map { $0 + whisperOffset }) ?? Int.max)
            let secondIndex = Self.nonWhisperOrder[second] ?? ((Self.canonicalOrder.firstIndex(of: second).map { $0 + whisperOffset }) ?? Int.max)
            if firstIndex != secondIndex { return firstIndex < secondIndex }
            return first < second
        }
    }

    /// Get display name for a model ID
    private func displayName(for id: String) -> String {
        TranscriptionModelCatalog(whisper: whisperModelManager, parakeet: parakeetModelManager)
            .displayName(for: id)
    }

    /// Current cloud provider enum
    private var currentCloudProvider: CloudProvider {
        CloudProvider(rawValue: cloudProvider) ?? .hyperwhisper
    }

    /// Cloud transcription providers shown in the picker. Always
    /// includes providers that don't require an API key (HyperWhisper
    /// Cloud) and any provider with a healthy probe; preserves the
    /// currently-selected provider so the saved selection isn't yanked
    /// away even when its key has gone bad.
    private var availableCloudProviders: [CloudProvider] {
        let current = currentCloudProvider
        return CloudProvider.allCases.filter { provider in
            // Azure MAI + Google Chirp are HyperWhisper-Cloud-routed only and
            // surfaced via the Accuracy tier picker; don't list them as
            // standalone provider choices.
            if provider == .microsoftAzureSpeech || provider == .googleSpeech {
                return false
            }
            if !provider.requiresAPIKey { return true }
            if provider == current { return true }
            return cloudHealth.status(for: provider) == .healthy
        }
    }

    /// BYOK ("Your provider") transcription providers — `availableCloudProviders`
    /// minus HyperWhisper Cloud, which is its own Source segment.
    private var availableDirectCloudProviders: [CloudProvider] {
        availableCloudProviders.filter { $0 != .hyperwhisper }
    }

    /// 3-way Source axis derived from the persisted `provider`/`cloudProvider`
    /// fields. The setter re-applies the choice and seeds dependent fields
    /// (mirrors the old `cloudProvider` onChange seeding logic).
    private var transcriptionSource: Binding<TranscriptionSource> {
        Binding(
            get: {
                if provider == .local { return .onDevice }
                return currentCloudProvider == .hyperwhisper ? .hyperwhisperCloud : .yourProvider
            },
            set: { newSource in
                // Remember the selection we're leaving so toggling back restores
                // it instead of reseeding a default. The transcription Source must
                // NOT touch the post-processing provider — post-processing source is
                // an independent axis owned by ModePostProcessingSettings.
                rememberCurrentTranscriptionSelection()

                switch newSource {
                case .onDevice:
                    provider = .local
                    if !availableModelIds.isEmpty, !availableModelIds.contains(model) {
                        model = sortedModelIds().first ?? model
                    }
                case .hyperwhisperCloud:
                    provider = .cloud
                    cloudProvider = CloudProvider.hyperwhisper.rawValue
                    showAllCloudTranscriptionModels = false
                    // Restore a previously-chosen HW Cloud model/domain if we have
                    // one; otherwise seed the selected tier's catalog default and
                    // clear any medical domain (mirrors the cloudProvider onChange).
                    if let savedModel = lastHyperwhisperCloudTranscriptionModel {
                        cloudTranscriptionModel = savedModel
                        cloudTranscriptionDomain = lastHyperwhisperCloudTranscriptionDomain
                    } else {
                        cloudTranscriptionModel = CloudAccuracyTier.fromStorageValue(cloudAccuracyTier).defaultModelId
                        cloudTranscriptionDomain = nil
                    }
                case .yourProvider:
                    provider = .cloud
                    showAllCloudTranscriptionModels = false
                    // Restore a previously-chosen BYOK provider/model if we have a
                    // usable one; otherwise seed the first available direct provider.
                    // This keeps a saved Deepgram/Groq/etc. choice intact across a
                    // harmless Source toggle away and back.
                    if let savedProviderRaw = lastDirectCloudProvider,
                       let savedProvider = CloudProvider(rawValue: savedProviderRaw),
                       availableDirectCloudProviders.contains(savedProvider) {
                        cloudProvider = savedProviderRaw
                        cloudTranscriptionModel = lastDirectCloudTranscriptionModel
                            ?? CloudTranscriptionModels.defaultModel(for: savedProvider)
                    } else {
                        let current = currentCloudProvider
                        if current == .hyperwhisper || !availableDirectCloudProviders.contains(current) {
                            let firstDirect = availableDirectCloudProviders.first ?? .openai
                            cloudProvider = firstDirect.rawValue
                            cloudTranscriptionModel = CloudTranscriptionModels.defaultModel(for: firstDirect)
                        }
                    }
                    cloudTranscriptionDomain = nil
                }
            }
        )
    }

    /// Snapshot the active transcription selection into the per-source memory
    /// before the Source toggle switches away, so toggling back restores it.
    private func rememberCurrentTranscriptionSelection() {
        guard provider == .cloud else { return }
        if currentCloudProvider == .hyperwhisper {
            lastHyperwhisperCloudTranscriptionModel = cloudTranscriptionModel
            lastHyperwhisperCloudTranscriptionDomain = cloudTranscriptionDomain
        } else {
            lastDirectCloudProvider = cloudProvider
            lastDirectCloudTranscriptionModel = cloudTranscriptionModel
        }
    }

    /// True when the currently-selected provider's key is missing or
    /// unhealthy. Drives the inline "Reconnect" affordance.
    private var currentProviderNeedsAttention: Bool {
        let provider = currentCloudProvider
        guard provider.requiresAPIKey else { return false }
        return cloudHealth.status(for: provider) != .healthy
    }

    private var cloudTranscriptionModelsForPicker: [CloudTranscriptionModel] {
        showAllCloudTranscriptionModels
            ? CloudTranscriptionModels.models(for: currentCloudProvider)
            : CloudTranscriptionModels.popularModels(for: currentCloudProvider)
    }

    private var canShowAllCloudTranscriptionModels: Bool {
        CloudTranscriptionModels.models(for: currentCloudProvider).count >
            CloudTranscriptionModels.popularModels(for: currentCloudProvider).count
    }

    private var selectedCloudTranscriptionModelSummary: String? {
        guard let model = CloudTranscriptionModels.model(withId: cloudTranscriptionModel) else {
            return nil
        }

        if let pricePerMinute = model.pricePerMinute {
            let formattedPrice = String(format: "$%.4f/min", pricePerMinute)
            return "\(model.description) - \(formattedPrice)"
        }

        return model.description
    }

    private static func shouldShowAllCloudTranscriptionModels(
        provider: CloudProvider,
        selectedModelId: String
    ) -> Bool {
        guard !selectedModelId.isEmpty,
              CloudTranscriptionModels.models(for: provider).contains(where: { $0.id == selectedModelId })
        else {
            return false
        }

        return !CloudTranscriptionModels.popularModels(for: provider).contains { $0.id == selectedModelId }
    }

    /// Whether the current cloud provider has an API key configured
    private var hasAPIKey: Bool {
        settingsManager.hasAPIKey(for: currentCloudProvider)
    }

    /// Returns true if the selected provider/model does NOT support custom vocabulary.
    /// Cloud-tier vocab support is driven by `shared-app-classification/cloud-stt-catalog.json`
    /// (consumed via `CloudAccuracyTier.supportsCustomVocabulary`). Per-sub-model BYOK checks
    /// (Deepgram Base/Whisper, ElevenLabs Scribe v1) remain hardcoded because the catalog
    /// doesn't yet model sub-models within a provider.
    ///
    /// DEEPGRAM VOCABULARY SUPPORT:
    /// - Nova-3: ONLY supports 'keyterm' (monolingual, 90% KRR improvement when language is explicitly specified)
    ///           Does NOT support 'keywords' parameter - will return 400 error if used
    /// - Nova-2, Nova-1, Enhanced: Support 'keywords' only
    /// - Base models: NO vocabulary support
    /// - Whisper models: NO vocabulary support
    ///
    /// ELEVENLABS VOCABULARY SUPPORT:
    /// - Scribe v2: Supports 'keyterms' (up to 100 terms, each ≤50 characters)
    /// - Scribe v1: NO vocabulary support
    private var showVocabularyUnsupportedNotice: Bool {
        // Parakeet supports vocabulary via phonetic matching (Beider-Morse) + exact replacement
        // Qwen3 ASR doesn't support custom vocabulary
        let isQwen3Asr = provider == .local && model == Qwen3AsrModelManager.Constants.modelId
        // ElevenLabs Scribe v1 doesn't support custom vocabulary (v2 supports keyterms)
        let isElevenLabsV1 = provider == .cloud && currentCloudProvider == .elevenLabs && cloudTranscriptionModel == "scribe_v1"
        // Mistral doesn't support custom vocabulary or prompt parameters
        let isMistral = provider == .cloud && currentCloudProvider == .mistral
        // xAI Grok STT has no documented vocabulary, prompt, keyword, or phrase-hint parameter
        let isGrokDirect = provider == .cloud && currentCloudProvider == .grok
        // Cloud accuracy tiers under HyperWhisper Cloud where the shared
        // catalog flags vocabulary as unsupported (e.g. Grok SST — backend
        // doesn't forward initial_prompt; Google Chirp 3 — Speech V2
        // adaptation 404s on chirp_3).
        let isUnsupportedCloudTier: Bool = {
            guard provider == .cloud, currentCloudProvider == .hyperwhisper else { return false }
            // Model-aware: follows the SELECTED model's catalog vocab flag.
            return hyperwhisperCloudVocabularyUnsupported
        }()
        // Deepgram Base models don't support keywords or keyterms (high-volume, low-cost tier)
        let isDeepgramBase = provider == .cloud && currentCloudProvider == .deepgram && cloudTranscriptionModel.hasPrefix("base")
        // Deepgram Whisper models don't support keywords or keyterms
        let isDeepgramWhisper = provider == .cloud && currentCloudProvider == .deepgram && cloudTranscriptionModel.hasPrefix("whisper")
        return isQwen3Asr || isElevenLabsV1 || isMistral || isGrokDirect || isUnsupportedCloudTier || isDeepgramBase || isDeepgramWhisper
    }

    /// Returns true when Deepgram Nova-3 is selected with auto-detect language.
    /// Nova-3 doesn't support the 'keywords' parameter (returns 400 error), and 'keyterm' is silently ignored
    /// when using detect_language=true. Users must set a specific language to enable vocabulary boosting.
    private var showDeepgramNova3AutoDetectNotice: Bool {
        let isDeepgramNova3 = provider == .cloud && currentCloudProvider == .deepgram && cloudTranscriptionModel.hasPrefix("nova-3")
        let isHyperWhisperDeepgramTier = provider == .cloud && currentCloudProvider == .hyperwhisper && cloudAccuracyTier == CloudAccuracyTier.deepgramNova3.rawValue
        let isAutoDetect = language.isEmpty || language.lowercased() == "auto"
        return (isDeepgramNova3 || isHyperWhisperDeepgramTier) && isAutoDetect
    }

    // MARK: - HyperWhisper Cloud provider→model helpers

    /// The currently-selected HyperWhisper Cloud tier (Provider axis), resolved
    /// from the persisted `cloudAccuracyTier` storage string.
    private var selectedCloudTier: CloudAccuracyTier {
        CloudAccuracyTier.fromStorageValue(cloudAccuracyTier)
    }

    /// Provider id used to look up the supported-language set in `STTCapabilities`
    /// for the language picker. For a BYOK cloud provider this is the literal
    /// `cloudProvider` (e.g. "deepgram", "assemblyai"). For HyperWhisper Cloud the
    /// outer `cloudProvider` is "hyperwhisper" — which only registers nova-3 — so we
    /// resolve the SELECTED accuracy tier's routed upstream provider id instead (the
    /// same value sent in the X-STT-Provider header). Tiers whose upstream provider
    /// isn't in `STTCapabilities` (azure-mai / google-chirp / gemini) yield an empty
    /// spec list, which falls back to the full language list.
    private var languageFilterCloudProviderId: String {
        currentCloudProvider == .hyperwhisper ? selectedCloudTier.sttProvider : cloudProvider
    }

    /// Model id used to look up the supported-language set for the language picker.
    /// Normally the selected `cloudTranscriptionModel`, BUT when the AssemblyAI
    /// Medical Mode add-on is active the domain narrows transcription to EN/ES/DE/FR
    /// — represented in `STTCapabilities` by the `-medical` model variants
    /// (`universal-2-medical` / `universal-3-pro-medical`). Resolve that variant so
    /// the picker restricts to the medical language set instead of offering the base
    /// model's full list (e.g. pt/it). Depends on `cloudTranscriptionDomain`, so the
    /// picker re-filters reactively when the Medical toggle flips (LanguageSelectionView
    /// runs `enforceAllowedLanguage()` on `cloudModelId` change, resetting any stale
    /// out-of-set selection like "pt" to the first allowed code, i.e. "auto").
    private var languageFilterCloudModelId: String {
        if showsMedicalDomainToggle && cloudTranscriptionDomain == "medical" {
            return "\(cloudTranscriptionModel)-medical"
        }
        return cloudTranscriptionModel
    }

    /// Models available for the selected HyperWhisper Cloud tier (catalog order).
    private var hyperwhisperCloudModels: [CloudSTTCatalog.Model] {
        selectedCloudTier.models
    }

    /// Whether the Medical domain toggle should be shown. Only assemblyAI uses a
    /// domain-based medical mode; Deepgram medical is a model selection instead.
    private var showsMedicalDomainToggle: Bool {
        currentCloudProvider == .hyperwhisper && selectedCloudTier == .assemblyAI
    }

    /// Binding for the Medical toggle → maps the nullable domain string to a Bool.
    private var medicalDomainBinding: Binding<Bool> {
        Binding(
            get: { cloudTranscriptionDomain == "medical" },
            set: { cloudTranscriptionDomain = $0 ? "medical" : nil }
        )
    }

    /// Custom-vocabulary visibility for the HyperWhisper Cloud path follows the
    /// SELECTED model's catalog flag (e.g. ElevenLabs scribe_v1 unsupported).
    private var hyperwhisperCloudVocabularyUnsupported: Bool {
        guard currentCloudProvider == .hyperwhisper else { return false }
        return !selectedCloudTier.supportsCustomVocabulary(forModelId: cloudTranscriptionModel)
    }

    // MARK: - Body

    var body: some View {
        VStack(spacing: 0) {
            editorHeader
            editorContent
                .padding(.top, 8)
            Divider()
            editorFooter
        }
        .frame(width: 480, height: 700)
        .background(Color(NSColor.windowBackgroundColor))
        .clipShape(RoundedRectangle(cornerRadius: 12))
        .onAppear {
            // Ensure model selection is valid to avoid Picker selection warnings
            if provider == .local {
                if availableModelIds.isEmpty {
                    provider = .cloud
                } else if !availableModelIds.contains(model) {
                    model = sortedModelIds().first ?? model
                }
            }

            if Self.shouldShowAllCloudTranscriptionModels(
                provider: currentCloudProvider,
                selectedModelId: cloudTranscriptionModel
            ) {
                showAllCloudTranscriptionModels = true
            }

            // HyperWhisper Cloud back-compat: existing modes have an accuracy
            // tier set but an empty (or BYOK-leftover) cloudTranscriptionModel.
            // Resolve it to the tier's catalog default so the Model dropdown has
            // a valid selection (avoids a SwiftUI Picker selection warning).
            if currentCloudProvider == .hyperwhisper {
                let tier = selectedCloudTier
                let modelIds = tier.models.map { $0.id }
                if !modelIds.contains(cloudTranscriptionModel) {
                    cloudTranscriptionModel = tier.defaultModelId
                }
                // A domain only makes sense for assemblyAI; clear stale values.
                if !showsMedicalDomainToggle {
                    cloudTranscriptionDomain = nil
                }
            }
        }
        .onChange(of: postProcessingMode) { _, newValue in
            switch newValue {
            case .cloud:
                if let provider = PostProcessingProvider(rawValue: postProcessingProvider), provider.isLocal {
                    postProcessingProvider = PostProcessingProvider.openai.rawValue
                }
                if let provider = PostProcessingProvider(rawValue: postProcessingProvider),
                   PostProcessingModels.model(withId: languageModel, provider: provider) == nil,
                   let defaultModel = PostProcessingModels.defaultModel(for: provider) {
                    languageModel = defaultModel.id
                }
            case .local:
                postProcessingProvider = PostProcessingProvider.localLLM.rawValue
                if PostProcessingModels.model(withId: languageModel, provider: .localLLM) == nil {
                    languageModel = PostProcessingProvider.localLLM.defaultModel
                }
            case .off:
                punctuation = false
                capitalization = false
            }
        }
        .onChange(of: model) { newModel in
            // AUTO-SET LANGUAGE FOR ENGLISH-ONLY MODELS
            // When user selects an .en model, automatically switch to English
            if isEnglishOnlyModel(provider: provider, model: newModel) {
                language = "en"
            }
        }
    }

    // MARK: - Header

    private var editorHeader: some View {
        VStack(spacing: 8) {
            Text(localized: configuration.isEditMode ? "modes.edit.title" : "modes.create.title")
                .font(.title2)
                .fontWeight(.bold)
                .frame(maxWidth: .infinity, alignment: .leading)
            Text(localized: configuration.isEditMode ? "modes.edit.subtitle" : "modes.create.subtitle")
                .font(.caption)
                .foregroundColor(.secondary)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(20)
        .background(Color(NSColor.controlBackgroundColor))
    }

    // MARK: - Content

    private var editorContent: some View {
        ScrollView {
            VStack(spacing: 24) {
                editorBasicSettings

                editorTranscriptionModel

                LanguageProcessingSettingsView(
                    activeCloudProvider: provider == .cloud ? currentCloudProvider : nil,
                    preset: $preset,
                    customInstructions: $customInstructions,
                    postProcessingMode: $postProcessingMode,
                    postProcessingProvider: $postProcessingProvider,
                    languageModel: $languageModel,
                    profanityFilter: $profanityFilter,
                    userSystemPrompt: $userSystemPrompt,
                    language: $language,
                    englishSpelling: $englishSpelling,
                    enableScreenOCR: $enableScreenOCR,
                    cloudPostProcessingModel: $cloudPostProcessingModel
                )

                // Punctuation card
                GroupBox {
                    VStack(alignment: .leading, spacing: 16) {
                        Label(LocalizedStringKey("modes.section.punctuation"), systemImage: "textformat")
                            .font(.headline)
                            .foregroundColor(.secondary)

                        VStack(spacing: 12) {
                            // Punctuation & capitalization are LLM instructions — only show when post-processing is enabled
                            if postProcessingMode != .off {
                                SettingsToggleRow(
                                    title: LocalizedStringKey("modes.toggle.punctuation.title"),
                                    subtitle: LocalizedStringKey("modes.toggle.punctuation.subtitle"),
                                    isOn: $punctuation,
                                    standalone: false
                                )

                                if punctuation {
                                    Divider()

                                    SettingsToggleRow(
                                        title: LocalizedStringKey("modes.toggle.removeTrailingPeriod.title"),
                                        subtitle: LocalizedStringKey("modes.toggle.removeTrailingPeriod.subtitle"),
                                        isOn: $removeTrailingPeriod,
                                        standalone: false
                                    )
                                }

                                Divider()

                                SettingsToggleRow(
                                    title: LocalizedStringKey("modes.toggle.capitalization.title"),
                                    subtitle: LocalizedStringKey("modes.toggle.capitalization.subtitle"),
                                    isOn: $capitalization,
                                    standalone: false
                                )
                            } else {
                                // When post-processing is off, only show remove trailing period (string operation, works independently)
                                SettingsToggleRow(
                                    title: LocalizedStringKey("modes.toggle.removeTrailingPeriod.title"),
                                    subtitle: LocalizedStringKey("modes.toggle.removeTrailingPeriod.subtitle"),
                                    isOn: $removeTrailingPeriod,
                                    standalone: false
                                )
                            }
                        }
                    }
                    .padding(12)
                }
            }
            .padding(20)
        }
    }

    // MARK: - Basic Settings

    private var editorBasicSettings: some View {
        GroupBox {
            VStack(alignment: .leading, spacing: 16) {
                Label(LocalizedStringKey("modes.section.basic"), systemImage: "gear")
                    .font(.headline)
                    .foregroundColor(.secondary)

                HStack {
                    Text(localized: "modes.field.name")
                        .frame(width: 80, alignment: .leading)
                    TextField(
                        LocalizedStringKey(configuration.isEditMode ? "modes.field.name.editPlaceholder" : "modes.field.name.placeholder"),
                        text: $name
                    )
                    .textFieldStyle(.roundedBorder)
                    .disabled(configuration.mode?.name == "Default")
                }
            }
            .padding(12)
        }
    }

    // MARK: - Transcription Model

    private var editorTranscriptionModel: some View {
        GroupBox {
            VStack(alignment: .leading, spacing: 16) {
                Label(LocalizedStringKey("modes.section.transcription"), systemImage: "cpu")
                    .font(.headline)
                    .foregroundColor(.secondary)

                // 3-way Source segmented control:
                // On-device / HyperWhisper Cloud / Your provider.
                Picker("", selection: transcriptionSource) {
                    ForEach(TranscriptionSource.allCases) { source in
                        Text(source.label).tag(source)
                    }
                }
                .pickerStyle(.segmented)
                .labelsHidden()

                switch transcriptionSource.wrappedValue {
                case .onDevice:
                    editorLocalTranscription
                case .hyperwhisperCloud:
                    hyperwhisperCloudTranscriptionSection
                case .yourProvider:
                    yourProviderTranscriptionSection
                }
            }
            .padding(12)
        }
    }

    // MARK: - On-device transcription

    @ViewBuilder
    private var editorLocalTranscription: some View {
        if availableModelIds.isEmpty {
            HStack(spacing: 8) {
                Image(systemName: "exclamationmark.triangle.fill")
                    .foregroundColor(.orange)
                VStack(alignment: .leading, spacing: 2) {
                    Text(localized: "modes.notice.noLocalModels.title")
                        .font(.caption)
                        .fontWeight(.medium)
                    Text(localized: "modes.notice.noLocalModels.subtitle")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
                Spacer()
            }
            .padding(10)
            .background(
                RoundedRectangle(cornerRadius: 8)
                    .fill(Color.orange.opacity(0.1))
            )
        } else {
            VStack(alignment: .leading, spacing: 12) {
                // Model selection
                HStack {
                    Text(localized: "modes.field.model")
                        .frame(width: 80, alignment: .leading)
                    Picker("", selection: $model) {
                        ForEach(sortedModelIds(), id: \.self) { mid in
                            Text(displayName(for: mid)).tag(mid)
                        }
                    }
                    .pickerStyle(.menu)
                    .labelsHidden()
                    Spacer()
                }

                // Language selection for local
                LanguageSelectionView(
                    language: $language,
                    provider: .local,
                    model: model
                )

                if showVocabularyUnsupportedNotice {
                    VocabularyUnsupportedNotice()
                }
            }
        }
    }

    // MARK: - HyperWhisper Cloud transcription

    @ViewBuilder
    private var hyperwhisperCloudTranscriptionSection: some View {
        VStack(alignment: .leading, spacing: 12) {
            // Two-level Engine → Model picker. Engine (L1) is the accuracy tier
            // (X-STT-Provider); Model (L2) is the selected model within that
            // engine (X-STT-Model). Credits caption reflects the selected model.
            hyperwhisperCloudProviderModelPicker

            Divider()

            cloudTranscriptionLanguageAndNotices
        }
    }

    // MARK: - Your provider (BYOK) transcription

    @ViewBuilder
    private var yourProviderTranscriptionSection: some View {
        VStack(alignment: .leading, spacing: 12) {
            // Provider picker — BYOK providers only (HyperWhisper Cloud is its
            // own Source segment).
            HStack {
                Text(localized: "modes.field.provider")
                    .frame(width: 80, alignment: .leading)
                Picker("", selection: $cloudProvider) {
                    ForEach(availableDirectCloudProviders, id: \.id) { provider in
                        Text(provider.displayName).tag(provider.rawValue)
                    }
                }
                .pickerStyle(.menu)
                .labelsHidden()
                .onChange(of: cloudProvider) { _, newProvider in
                    guard let provider = CloudProvider(rawValue: newProvider) else { return }
                    showAllCloudTranscriptionModels = false
                    cloudTranscriptionModel = CloudTranscriptionModels.defaultModel(for: provider)
                    cloudTranscriptionDomain = nil

                    guard postProcessingMode == .cloud else { return }

                    if postProcessingProvider == PostProcessingProvider.hyperwhisper.rawValue {
                        postProcessingProvider = PostProcessingProvider.openai.rawValue
                    }
                }
                Spacer()
            }

            // Cloud transcription model row.
            // Grok exposes a single implicit STT model (no model parameter), so
            // it's shown as a read-only label rather than a one-item picker.
            if currentCloudProvider == .grok {
                HStack {
                    Text(localized: "modes.field.model")
                        .frame(width: 80, alignment: .leading)
                    Text("Grok Speech-to-Text")
                        .foregroundColor(.secondary)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
            } else {
                HStack {
                    Text(localized: "modes.field.model")
                        .frame(width: 80, alignment: .leading)
                    Picker("", selection: $cloudTranscriptionModel) {
                        ForEach(cloudTranscriptionModelsForPicker, id: \.id) { model in
                            Text(model.displayName)
                                .tag(model.id)
                        }
                    }
                    .pickerStyle(.menu)
                    .labelsHidden()

                    Button { showCloudModelInfo.toggle() } label: {
                        Image(systemName: "info.circle")
                            .foregroundColor(.secondary)
                    }
                    .buttonStyle(.plain)
                    .popover(isPresented: $showCloudModelInfo, arrowEdge: .trailing) {
                        if let modelInfo = CloudTranscriptionModels.model(withId: cloudTranscriptionModel) {
                            VStack(alignment: .leading, spacing: 8) {
                                Text(modelInfo.displayName)
                                    .font(.headline)
                                Text(modelInfo.description)
                                    .font(.caption)
                                    .foregroundColor(.secondary)
                                    .fixedSize(horizontal: false, vertical: true)
                                if let perSecond = modelInfo.pricePerSecond {
                                    let perMinute = perSecond * 60.0
                                    HStack(spacing: 6) {
                                        Image(systemName: "dollarsign.circle")
                                            .foregroundColor(.green)
                                        Text(String(format: "modes.model.pricing".localized, perMinute))
                                            .font(.caption)
                                    }
                                }
                            }
                            .padding()
                            .frame(width: 350)
                        }
                    }
                    .help("modes.help.model".localized)

                    Spacer()
                }

                if let modelSummary = selectedCloudTranscriptionModelSummary {
                    HStack(alignment: .top) {
                        Text("")
                            .frame(width: 80, alignment: .leading)
                        Text(modelSummary)
                            .font(.caption)
                            .foregroundColor(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                            .lineLimit(3)
                        Spacer()
                    }
                }

                if canShowAllCloudTranscriptionModels {
                    HStack {
                        Text("")
                            .frame(width: 80, alignment: .leading)
                        Toggle(isOn: $showAllCloudTranscriptionModels) {
                            Text("Show all models")
                                .font(.caption)
                                .foregroundColor(.secondary)
                        }
                        .toggleStyle(.checkbox)

                        Spacer()
                    }
                }
            }

            Divider()

            cloudTranscriptionLanguageAndNotices

            // Gemini custom transcription instructions
            if currentCloudProvider == .gemini {
                VStack(alignment: .leading, spacing: 6) {
                    Text(localized: "modes.gemini.customPrompt.title")
                        .font(.caption)
                        .fontWeight(.medium)
                        .foregroundColor(.secondary)

                    TextEditor(text: $geminiCustomPrompt)
                        .font(.body)
                        .frame(height: 80)
                        .padding(6)
                        .background(
                            RoundedRectangle(cornerRadius: 6)
                                .fill(Color(NSColor.textBackgroundColor))
                        )
                        .overlay(
                            RoundedRectangle(cornerRadius: 6)
                                .stroke(Color(NSColor.separatorColor), lineWidth: 1)
                        )
                        .onChange(of: geminiCustomPrompt) { _, newValue in
                            if newValue.count > geminiCustomPromptCharacterLimit {
                                geminiCustomPrompt = String(newValue.prefix(geminiCustomPromptCharacterLimit))
                            }
                        }

                    Text(localized: "modes.gemini.customPrompt.helper")
                        .font(.caption2)
                        .foregroundColor(.secondary)
                }
            }

            if currentProviderNeedsAttention {
                HStack(spacing: 8) {
                    Image(systemName: "exclamationmark.triangle.fill")
                        .font(.caption)
                        .foregroundColor(.orange)
                    Text("modes.provider.needsKey".localized(arguments: currentCloudProvider.displayName))
                        .font(.caption)
                        .foregroundColor(.primary)
                    Spacer()
                    Button {
                        dismiss()
                        DispatchQueue.main.async {
                            appState.navigateToModelLibraryAPIKeys()
                        }
                    } label: {
                        Text(localized: "modes.provider.manageInLibrary")
                    }
                    .buttonStyle(.link)
                    .font(.caption)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(10)
                .background(
                    RoundedRectangle(cornerRadius: 8)
                        .fill(Color.orange.opacity(0.15))
                )
            }
        }
    }

    // MARK: - Shared cloud language + notices

    /// Language picker + vocabulary / Deepgram notices shared by both cloud
    /// Source branches. For HyperWhisper Cloud the language context is derived
    /// from the SELECTED tier's routed upstream provider id (X-STT-Provider) and
    /// model id rather than the literal "hyperwhisper" wrapper, which only
    /// registers nova-3 in STTCapabilities.
    @ViewBuilder
    private var cloudTranscriptionLanguageAndNotices: some View {
        LanguageSelectionView(
            language: $language,
            provider: .cloud,
            model: cloudTranscriptionModel,
            cloudProviderId: languageFilterCloudProviderId,
            cloudModelId: languageFilterCloudModelId
        )

        // Show vocabulary warning for providers that don't support it
        if showVocabularyUnsupportedNotice {
            VocabularyUnsupportedNotice()
        }

        // Show warning when Deepgram Nova-3 is used with auto-detect language
        if showDeepgramNova3AutoDetectNotice {
            DeepgramNova3AutoDetectNotice()
        }
    }

    // MARK: - HyperWhisper Cloud Provider → Model picker

    @ViewBuilder
    private var hyperwhisperCloudProviderModelPicker: some View {
        VStack(alignment: .leading, spacing: 10) {
            // Level 1 — Engine (accuracy tier / provider). Bound to cloudAccuracyTier.
            HStack {
                Text(localized: "modes.field.provider")
                    .frame(width: 80, alignment: .leading)
                Picker("", selection: $cloudAccuracyTier) {
                    ForEach(CloudAccuracyTier.pickerOrder) { tier in
                        Text(hyperwhisperCloudEngineLabel(tier)).tag(tier.rawValue)
                    }
                }
                .pickerStyle(.menu)
                .labelsHidden()
                .onChange(of: cloudAccuracyTier) { _, newTier in
                    // Reset model to the new provider's default and clear the
                    // medical domain (mirrors the cloudProvider onChange reset).
                    let tier = CloudAccuracyTier.fromStorageValue(newTier)
                    cloudTranscriptionModel = tier.defaultModelId
                    cloudTranscriptionDomain = nil
                }
                Spacer()
            }

            // Level 2 — Model. A single-model provider (grok / azure-mai /
            // google-chirp) shows one disabled auto entry.
            let models = hyperwhisperCloudModels
            HStack(alignment: .top) {
                Text(localized: "modes.field.model")
                    .frame(width: 80, alignment: .leading)
                if models.count <= 1 {
                    // Single implicit model — show a disabled "Auto" entry.
                    Text(models.first?.displayName ?? "Auto")
                        .font(.body)
                        .foregroundColor(.secondary)
                        .frame(maxWidth: .infinity, alignment: .leading)
                } else {
                    Picker("", selection: $cloudTranscriptionModel) {
                        ForEach(models) { model in
                            Text(hyperwhisperCloudModelLabel(model)).tag(model.id)
                        }
                    }
                    .pickerStyle(.menu)
                    .labelsHidden()
                    Spacer()
                }
            }

            // Badges for the selected model (Preview / No custom vocabulary).
            // Only render the row when a badge actually applies — otherwise the
            // empty HStack adds a blank line and a double gap above the credits
            // caption (e.g. for models with neither a preview nor a vocab flag).
            if let selected = models.first(where: { $0.id == cloudTranscriptionModel }) ?? models.first,
               selected.previewStatus == true || selected.supportsCustomVocabulary == false {
                HStack(spacing: 6) {
                    if selected.previewStatus == true {
                        hyperwhisperCloudBadge(text: "mode.editor.cloudModel.previewHint".localized, color: .orange)
                    }
                    if selected.supportsCustomVocabulary == false {
                        hyperwhisperCloudBadge(text: "mode.editor.cloudModel.noVocabularyHint".localized, color: .secondary)
                    }
                    Spacer()
                }
                .padding(.leading, 88)
            }

            // Credits caption for the SELECTED model.
            let tier = selectedCloudTier
            let perMinute = tier.creditsPerMinute(forModelId: cloudTranscriptionModel)
            if perMinute > 0 {
                // Gate on > 0 so a catalog load failure hides the caption
                // instead of showing a misleading "~0.0 credits/min".
                HStack(spacing: 6) {
                    Circle()
                        .fill(Color.green)
                        .frame(width: 4, height: 4)
                    Text(tier.creditsPerMinuteLabel(forModelId: cloudTranscriptionModel))
                        .font(.caption)
                        .foregroundColor(.secondary)
                        .monospacedDigit()
                    // When the Medical Mode add-on is enabled it bills separately
                    // on top of the per-minute model rate — note it in the caption.
                    if showsMedicalDomainToggle && cloudTranscriptionDomain == "medical" {
                        Text(localized: "mode.editor.cloudModel.medicalAddOnNote")
                            .font(.caption)
                            .foregroundColor(.secondary)
                    }
                }
                .padding(.leading, 88)
                .help(tier.description)
            }

            // Medical domain toggle — assemblyAI only (Deepgram medical is a
            // model selection, handled by the Model dropdown above).
            if showsMedicalDomainToggle {
                HStack {
                    Text("")
                        .frame(width: 80, alignment: .leading)
                    Toggle(isOn: medicalDomainBinding) {
                        Text(localized: "mode.editor.cloudDomain.medical")
                            .font(.caption)
                    }
                    .toggleStyle(.checkbox)
                    Spacer()
                }
            }
        }
    }

    /// Label for a HyperWhisper Cloud model row — tags the catalog default with
    /// "(Recommended)" so users can see which model is the recommended default.
    private func hyperwhisperCloudModelLabel(_ model: CloudSTTCatalog.Model) -> String {
        model.isDefault == true
            ? "\(model.displayName) (\("modes.badge.recommended".localized))"
            : model.displayName
    }

    /// Label for a HyperWhisper Cloud engine (accuracy tier) row — tags the
    /// recommended engine (ElevenLabs Scribe v2) with "(Recommended)".
    private func hyperwhisperCloudEngineLabel(_ tier: CloudAccuracyTier) -> String {
        tier.isRecommended
            ? "\(tier.displayName) (\("modes.badge.recommended".localized))"
            : tier.displayName
    }

    /// Small pill badge used in the HyperWhisper Cloud model picker.
    private func hyperwhisperCloudBadge(text: String, color: Color) -> some View {
        Text(text)
            .font(.caption2)
            .foregroundColor(color)
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .background(
                RoundedRectangle(cornerRadius: 4)
                    .fill(color.opacity(0.12))
            )
    }

    // MARK: - Footer

    private var editorFooter: some View {
        HStack(spacing: 12) {
            if configuration.isEditMode {
                // Edit mode: Delete button on the left
                Button(role: .destructive) {
                    dismiss()
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) {
                        if let deleteAction = configuration.onDelete { deleteAction() }
                    }
                } label: {
                    Label(LocalizedStringKey("modes.button.delete"), systemImage: "trash")
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .tint(.red)
            }

            Spacer()

            // Cancel button
            Button {
                dismiss()
            } label: {
                Text(localized: "common.cancel")
            }
            .keyboardShortcut(.cancelAction)
            .controlSize(.large)

            // Save/Create button
            Button {
                let chosenModel = provider == .cloud ? "cloud" : (model.isEmpty ? (sortedModelIds().first ?? "base") : model)
                let finalLanguage = isEnglishOnlyModel(provider: provider, model: chosenModel) ? "en" : language
                let modeData = ModeData(
                    id: configuration.mode?.id ?? UUID(),
                    name: name,
                    preset: preset,
                    language: finalLanguage,
                    model: chosenModel,
                    punctuation: punctuation,
                    capitalization: capitalization,
                    profanityFilter: profanityFilter,
                    customInstructions: customInstructions,
                    languageModel: languageModel,
                    cloudProvider: cloudProvider,
                    cloudTranscriptionModel: cloudTranscriptionModel,
                    postProcessingMode: postProcessingMode,
                    postProcessingProvider: postProcessingProvider,
                    englishSpelling: englishSpelling,
                    userSystemPrompt: userSystemPrompt,
                    useStreamingTranscription: false,  // Streaming is now a separate feature, not part of modes
                    cloudAccuracyTier: CloudAccuracyTier.fromStorageValue(cloudAccuracyTier),
                    removeTrailingPeriod: removeTrailingPeriod,
                    enableScreenOCR: enableScreenOCR,
                    geminiCustomPrompt: geminiCustomPrompt,
                    cloudPostProcessingModel: CloudPostProcessingModel.fromStorageValue(cloudPostProcessingModel),
                    // Only persist a domain when the selected tier actually uses
                    // one (assemblyAI Medical Mode). Gating on the tier — not merely
                    // provider == hyperwhisper — keeps a stale "medical" domain from
                    // leaking to other tiers via backup/PATCH after the user switches.
                    cloudTranscriptionDomain: showsMedicalDomainToggle ? cloudTranscriptionDomain : nil
                )
                onSave(modeData)
            } label: {
                Text(localized: configuration.isEditMode ? "modes.button.save" : "modes.button.create")
            }
            .keyboardShortcut(.defaultAction)
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .disabled(name.isEmpty || (provider == .local && availableModelIds.isEmpty))
        }
        .padding(20)
        .background(Color(NSColor.controlBackgroundColor))
    }
}
