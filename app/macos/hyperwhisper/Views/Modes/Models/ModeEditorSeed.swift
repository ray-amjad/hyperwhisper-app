//
//  ModeEditorSeed.swift
//  HyperWhisper
//
//  The values the mode editor opens on when it EDITS a stored mode, as a pure
//  value (issue #730). `ModeEditorView.init` used to work all of this out in
//  place; it now copies these fields into its `@State` and holds no logic for
//  the EDIT branch. The CREATE branch still seeds from `ModeEditorDefaults`.
//

import Foundation

/// Every value the EDIT branch of `ModeEditorView` seeds, resolved from a
/// stored `Mode`: the legacy `voiceToText` migration, the canonical language
/// code, the stored local/cloud model, and the cloud provider, tier and
/// post-processing fields with the legacy Azure/Google providers folded into
/// HyperWhisper Cloud tiers.
struct ModeEditorSeed {
    let name: String
    let preset: String
    let language: String
    /// The mode's stored local model when it is not installed (issue #1434).
    let missingLocalModelId: String?
    let model: String
    let provider: ProviderType
    let punctuation: Bool
    let capitalization: Bool
    let profanityFilter: Bool
    let customInstructions: String
    let languageModel: String
    let postProcessingMode: PostProcessingMode
    let postProcessingProvider: String
    let cloudProvider: String
    let cloudTranscriptionModel: String
    let showAllCloudTranscriptionModels: Bool
    let englishSpelling: EnglishSpelling
    let userSystemPrompt: String
    let cloudAccuracyTier: String
    let cloudPostProcessingModel: String
    let removeTrailingPeriod: Bool
    let enableScreenOCR: Bool
    let geminiCustomPrompt: String
    let cloudTranscriptionDomain: String?

    /// The editor's opening values for `mode`. `availableModelIds` are the
    /// installed local model ids, so a stored local model that is not one of
    /// them is reported in `missingLocalModelId`.
    static func make(mode: Mode, availableModelIds: [String]) -> ModeEditorSeed {
        // Migrate removed "voiceToText" preset → post-processing off
        let rawPreset = mode.preset ?? "hyper"
        let isLegacyVoiceToText = rawPreset == "voiceToText"

        // Open on the STORED model and provider, installed or not. A missing
        // local model is listed in the picker as not installed, with a
        // warning, so Save with no change keeps it (issue #1434).
        let selection = ModeEditorDefaults.editModelSelection(
            storedModel: mode.model,
            availableModelIds: availableModelIds
        )
        let processingMode = isLegacyVoiceToText ? PostProcessingMode.off : (PostProcessingMode(rawValue: mode.postProcessingMode) ?? .cloud)

        // A mode can carry no transcription model at all (backup restore, or a
        // ModesEndpoint PATCH that sets only cloudProvider). Seed the SAVED
        // provider's own default rather than a blanket "whisper-1": Grok's
        // single implicit model has the empty-string id, so "whisper-1" would
        // match no picker tag and render the Model row as a blank menu button.
        // Only fall back to "whisper-1" when the provider itself is unknown.
        let savedCloudProviderEnum = CloudProvider.parse(mode.cloudProvider)
        let savedCloudModel = mode.cloudTranscriptionModel?
            .trimmingCharacters(in: .whitespacesAndNewlines)
        let seededCloudModel: String = {
            if let savedCloudModel, !savedCloudModel.isEmpty { return savedCloudModel }
            guard let savedCloudProviderEnum else { return "whisper-1" }
            return CloudTranscriptionModels.defaultModel(for: savedCloudProviderEnum)
        }()
        let initialCloudTranscriptionModel = CloudTranscriptionModels.resolveModelAlias(
            seededCloudModel,
            provider: savedCloudProviderEnum
        )

        // Azure MAI / Google Chirp legacy provider values are folded into
        // HyperWhisper Cloud accuracy tiers via the catalog. If the saved
        // cloudProvider is a legacy standalone-provider alias, surface that
        // entry as the accuracy tier and snap cloudProvider to "hyperwhisper".
        let savedCloudProvider = mode.cloudProvider
        let normalizedCloudProvider = CloudSTTCatalog.shared.normalizeCloudProvider(savedCloudProvider)
        let legacyProviderTier: CloudAccuracyTier? = normalizedCloudProvider.accuracyTier
            .flatMap { CloudAccuracyTier(rawValue: $0) }
        // Canonicalised, because this value is seeded straight into
        // `_cloudProvider` and the provider Picker tags its rows with
        // `CloudProvider.rawValue`. A camelCase id restored from a
        // Windows/Linux backup matches no tag and renders a BLANK menu.
        let migratedCloudProviderRaw = legacyProviderTier != nil
            ? CloudProvider.hyperwhisper.rawValue
            : (CloudProvider.canonicalStorageValue(savedCloudProvider) ?? "hyperwhisper")
        let migratedAccuracyTierRaw = legacyProviderTier?.rawValue
            ?? CloudAccuracyTier.fromStorageValue(mode.cloudAccuracyTier).rawValue
        let initialCloudProvider = CloudProvider.parse(migratedCloudProviderRaw) ?? .hyperwhisper

        let resolvedPostProvider = resolvedPostProcessingProvider(
            mode: mode,
            processingMode: processingMode,
            initialCloudProvider: initialCloudProvider
        )

        let storedDomain = mode.cloudTranscriptionDomain?.trimmingCharacters(in: .whitespacesAndNewlines)

        return ModeEditorSeed(
            name: mode.name ?? "Default",
            preset: isLegacyVoiceToText ? "hyper" : rawPreset,
            language: LanguageData.canonicalLanguageCode(mode.language),
            missingLocalModelId: selection.missingLocalModelId,
            model: selection.model,
            provider: selection.provider,
            punctuation: mode.punctuation,
            capitalization: mode.capitalization,
            profanityFilter: mode.profanityFilter,
            customInstructions: mode.customInstructions ?? "",
            languageModel: mode.languageModel ?? "gpt-5.6-luna",
            postProcessingMode: processingMode,
            postProcessingProvider: resolvedPostProvider,
            cloudProvider: migratedCloudProviderRaw,
            cloudTranscriptionModel: initialCloudTranscriptionModel,
            showAllCloudTranscriptionModels: Self.shouldShowAllCloudTranscriptionModels(
                provider: initialCloudProvider,
                selectedModelId: initialCloudTranscriptionModel
            ),
            // An existing mode with no stored value has never had one chosen, so
            // it opens on the region's variant too. A stored value is kept as is.
            englishSpelling: EnglishSpelling(rawValue: mode.englishSpelling ?? "")
                ?? .defaultForCurrentRegion,
            userSystemPrompt: mode.userSystemPrompt ?? "",
            cloudAccuracyTier: migratedAccuracyTierRaw,
            cloudPostProcessingModel: CloudPostProcessingModel.fromStorageValue(mode.cloudPostProcessingModel).rawValue,
            removeTrailingPeriod: mode.removeTrailingPeriod,
            enableScreenOCR: mode.enableScreenOCR,
            geminiCustomPrompt: mode.geminiCustomPrompt ?? "",
            cloudTranscriptionDomain: (storedDomain?.isEmpty == false) ? storedDomain : nil
        )
    }

    /// Resolve post-processing provider — compute against the migrated
    /// cloudProvider so legacy Azure/Google modes get the HW Cloud
    /// post-processing default after migration.
    private static func resolvedPostProcessingProvider(
        mode: Mode,
        processingMode: PostProcessingMode,
        initialCloudProvider: CloudProvider
    ) -> String {
        let providerValue = mode.postProcessingProvider
            ?? processingMode.defaultProvider?.storageValue
            ?? PostProcessingProvider.hyperwhisper.storageValue
        let resolvedPostProvider: String = {
            if initialCloudProvider == .hyperwhisper,
               processingMode == .cloud,
               mode.postProcessingProvider == nil {
                return PostProcessingProvider.hyperwhisper.storageValue
            }
            return providerValue
        }()
        return resolvedPostProvider
    }

    /// True when the selected cloud model is one the provider offers but not
    /// one of its popular models, so the Model picker must open on the full
    /// list for the selection to match a row. Also read by the editor's
    /// on-appear repair.
    static func shouldShowAllCloudTranscriptionModels(
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
}
