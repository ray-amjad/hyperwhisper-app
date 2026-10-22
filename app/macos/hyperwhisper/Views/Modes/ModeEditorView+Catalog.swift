//
//  ModeEditorView+Catalog.swift
//  HyperWhisper
//
//  The catalog half of ModeEditorView (issue #730): which cloud providers
//  and models the pickers offer, which notices apply, the HyperWhisper
//  Cloud tier/model bindings, the name lock, and the on-appear repair of a
//  stored selection. No layout lives here.
//

import SwiftUI

extension ModeEditorView {

    // MARK: - Catalog lookups

    /// Get display name for a model ID
    func displayName(for id: String) -> String {
        TranscriptionModelCatalog(whisper: whisperModelManager, parakeet: parakeetModelManager)
            .displayName(for: id)
    }

    /// Current cloud provider enum
    var currentCloudProvider: CloudProvider {
        CloudProvider.forMode(cloudProvider)
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
            // Meta follows the shared cross-platform BYOK rollout gate.
            if provider == .meta && !CloudTranscriptionModels.isMetaBYOKCatalogEnabled {
                return false
            }
            if !provider.requiresAPIKey { return true }
            if provider == current { return true }
            return cloudHealth.status(for: provider).isHealthy
        }
    }

    /// BYOK ("Your provider") transcription providers — `availableCloudProviders`
    /// minus HyperWhisper Cloud, which is its own Source segment.
    var availableDirectCloudProviders: [CloudProvider] {
        availableCloudProviders.filter { $0 != .hyperwhisper }
    }

    /// True when the currently-selected provider's key is missing or
    /// unhealthy. Drives the inline "Reconnect" affordance.
    var currentProviderNeedsAttention: Bool {
        let provider = currentCloudProvider
        guard provider.requiresAPIKey else { return false }
        return !cloudHealth.status(for: provider).isHealthy
    }

    var cloudTranscriptionModelsForPicker: [CloudTranscriptionModel] {
        showAllCloudTranscriptionModels
            ? CloudTranscriptionModels.models(for: currentCloudProvider)
            : CloudTranscriptionModels.popularModels(for: currentCloudProvider)
    }

    var canShowAllCloudTranscriptionModels: Bool {
        CloudTranscriptionModels.models(for: currentCloudProvider).count >
            CloudTranscriptionModels.popularModels(for: currentCloudProvider).count
    }

    var selectedCloudTranscriptionModelSummary: String? {
        guard let model = CloudTranscriptionModels.model(
            withId: cloudTranscriptionModel,
            provider: currentCloudProvider
        ) else {
            return nil
        }

        if let pricePerMinute = model.pricePerMinute {
            let formattedPrice = String(format: "$%.4f/min", pricePerMinute)
            return "\(model.description) - \(formattedPrice)"
        }

        return model.description
    }

    /// Whether the current cloud provider has an API key configured
    private var hasAPIKey: Bool {
        settingsManager.hasAPIKey(for: currentCloudProvider)
    }

    /// Returns true if the selected provider/model does NOT support custom vocabulary.
    /// Cloud-tier vocab support is driven by `shared-app-classification/cloud-stt-catalog.json`
    /// (consumed via `CloudAccuracyTier.supportsCustomVocabulary`). BYOK (your own provider)
    /// support is read per model from `shared-models/models-catalog.json` through
    /// `byokVocabularyUnsupported(provider:modelId:)` — no model ids are hard-coded here (#681).
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
    var showVocabularyUnsupportedNotice: Bool {
        // Parakeet supports vocabulary via phonetic matching (Beider-Morse) + exact replacement
        // Qwen3 ASR doesn't support custom vocabulary
        let isQwen3Asr = provider == .local && model == Qwen3AsrModelManager.Constants.modelId
        // Cloud accuracy tiers under HyperWhisper Cloud where the shared
        // catalog flags vocabulary as unsupported for the selected model.
        let isUnsupportedCloudTier: Bool = {
            guard provider == .cloud, currentCloudProvider == .hyperwhisper else { return false }
            // Model-aware: follows the SELECTED model's catalog vocab flag.
            return hyperwhisperCloudVocabularyUnsupported
        }()
        // Your own provider (BYOK): the shared models catalog says per model.
        let isUnsupportedByokModel = provider == .cloud
            && Self.byokVocabularyUnsupported(provider: currentCloudProvider, modelId: cloudTranscriptionModel)
        return isQwen3Asr || isUnsupportedCloudTier || isUnsupportedByokModel
    }

    /// Whether a BYOK (your own provider) transcription model does NOT support
    /// custom vocabulary, read from `shared-models/models-catalog.json` (#681).
    ///
    /// True only when the catalog HAS a voice row for `(provider, modelId)` and
    /// that row says vocabulary is unsupported. `supportsCustomVocabulary`
    /// answers false on a catalog MISS, so asking it alone would warn on an
    /// empty or uncatalogued id; this suppresses the warning instead of
    /// guessing, the same rule the HyperWhisper Cloud branch follows. HyperWhisper
    /// Cloud is never BYOK and always answers false here.
    ///
    /// Static so hyperwhisperTests can check it against the catalog without
    /// standing up the view.
    static func byokVocabularyUnsupported(provider: CloudProvider, modelId: String) -> Bool {
        guard provider != .hyperwhisper, !modelId.isEmpty else { return false }
        let key = SharedModelsCatalog.providerKey(provider)
        guard SharedModelsCatalog.entry(provider: key, kind: .voice, id: modelId) != nil else {
            return false
        }
        return !SharedModelsCatalog.supportsCustomVocabulary(provider: key, kind: .voice, id: modelId)
    }

    /// Returns true when Deepgram Nova-3 is selected with auto-detect language.
    /// Nova-3 doesn't support the 'keywords' parameter (returns 400 error), and 'keyterm' is silently ignored
    /// when using detect_language=true. Users must set a specific language to enable vocabulary boosting.
    var showDeepgramNova3AutoDetectNotice: Bool {
        let isDeepgramNova3 = provider == .cloud && currentCloudProvider == .deepgram && cloudTranscriptionModel.hasPrefix("nova-3")
        let isHyperWhisperDeepgramTier = provider == .cloud && currentCloudProvider == .hyperwhisper && cloudAccuracyTier == CloudAccuracyTier.deepgramNova3.rawValue
        let isAutoDetect = language.isEmpty || language.lowercased() == "auto"
        return (isDeepgramNova3 || isHyperWhisperDeepgramTier) && isAutoDetect
    }

    var hasValidDictationLanguage: Bool {
        guard provider == .cloud,
              languageFilterCloudProviderId == "assemblyai",
              languageFilterCloudModelId == "dictation" else { return true }
        return AssemblyAIDictationAudio.languages.contains(language)
    }

    // MARK: - HyperWhisper Cloud provider→model helpers

    /// The currently-selected HyperWhisper Cloud tier (Provider axis), resolved
    /// from the persisted `cloudAccuracyTier` storage string.
    var selectedCloudTier: CloudAccuracyTier {
        CloudAccuracyTier.fromStorageValue(cloudAccuracyTier)
    }

    /// Provider id used to look up the supported-language set in `STTCapabilities`
    /// for the language picker. For a BYOK cloud provider this is the literal
    /// `cloudProvider` (e.g. "deepgram", "assemblyai"). For HyperWhisper Cloud the
    /// outer `cloudProvider` is "hyperwhisper" — which STTCapabilities does not register — so we
    /// resolve the SELECTED accuracy tier's routed upstream provider id instead (the
    /// same value sent in the X-STT-Provider header). A model with no
    /// `STTCapabilities` row falls back to the tier's catalog set
    /// (`languageFilterCloudTierId`), then to the full list when the catalog
    /// leaves the tier unverified.
    var languageFilterCloudProviderId: String {
        currentCloudProvider == .hyperwhisper ? selectedCloudTier.sttProvider : cloudProvider
    }

    /// Shared-catalog entry id for the language picker: the selected tier on
    /// HyperWhisper Cloud, and for BYOK the catalog entry that lists this
    /// provider's model (nil when the catalog does not list it). The picker
    /// uses that entry's catalog set when `STTCapabilities` has no row for the
    /// model (`gpt-transcribe`: #831 on Cloud, #1942 on BYOK).
    var languageFilterCloudTierId: String? {
        Self.languageFilterCatalogEntryId(
            cloudProvider: currentCloudProvider,
            cloudTier: selectedCloudTier,
            modelId: cloudTranscriptionModel
        )
    }

    /// `languageFilterCloudTierId` without the view state, so a test drives
    /// the exact entry id the picker receives.
    static func languageFilterCatalogEntryId(
        cloudProvider: CloudProvider,
        cloudTier: CloudAccuracyTier,
        modelId: String
    ) -> String? {
        if cloudProvider == .hyperwhisper {
            return cloudTier.rawValue
        }
        return CloudSTTCatalog.shared.byokEntryId(cloudProvider: cloudProvider.rawValue, modelId: modelId)
    }

    /// Model id used to look up the supported-language set for the language picker.
    /// Normally the selected `cloudTranscriptionModel`, BUT when the AssemblyAI
    /// Medical Mode add-on is active the domain narrows transcription to EN/ES/DE/FR
    /// — represented in `STTCapabilities` by the `-medical` model variants
    /// (`universal-2-medical` / `universal-3-5-pro-medical`). Resolve that variant so
    /// the picker restricts to the medical language set instead of offering the base
    /// model's full list (e.g. pt/it). Depends on `cloudTranscriptionDomain`, so the
    /// picker re-filters reactively when the Medical toggle flips (LanguageSelectionView
    /// runs `enforceAllowedLanguage()` on `cloudModelId` change, resetting any stale
    /// out-of-set selection like "pt" to the first allowed code, i.e. "auto").
    var languageFilterCloudModelId: String {
        if showsMedicalDomainToggle && cloudTranscriptionDomain == "medical" {
            return "\(cloudTranscriptionModel)-medical"
        }
        return cloudTranscriptionModel
    }

    /// Models offered by the selected Provider row. That row is a *company*, so
    /// the list spans every tier the company owns — "Google" lists Transcribe and
    /// Gemini models together.
    ///
    /// Live-only models are excluded: this dropdown picks a DICTATION model, and
    /// `gemini-3.5-transcribe-live` is served by the WebSocket route alone, so
    /// choosing it here makes every dictation in the mode fail with an HTTP 400.
    /// The live picker lives in Streaming settings and is fed separately from
    /// `CloudAccuracyTier.streamingEligibleTiers`.
    var hyperwhisperCloudModels: [CloudSTTCatalog.Model] {
        selectedCloudTier.vendorGroupDictationModels
    }

    /// What the Model dropdown must be showing for a stored (tier, model) pair —
    /// the stored id when the dropdown offers it, the tier default otherwise.
    ///
    /// Reads the same list the picker is built from (`vendorGroupDictationModels`,
    /// via `hyperwhisperCloudModels`). A clamp keyed on `tier.models` instead
    /// disagreed with the picker in both directions: it reset a perfectly valid
    /// model belonging to a sibling tier of the same company row, and it ACCEPTED
    /// a live-only id the picker refuses to list — leaving a blank menu button on
    /// a mode whose every dictation fails with HTTP 400.
    ///
    /// Static and pure so the repair is testable without a SwiftUI environment.
    static func clampedCloudTranscriptionModel(
        tier: CloudAccuracyTier,
        storedModelId: String
    ) -> String {
        tier.vendorGroupDictationModels.contains { $0.id == storedModelId }
            ? storedModelId
            : tier.defaultModelId
    }

    /// Provider dropdown selection — the catalog `vendor` key rather than the
    /// tier, so a company with several tiers occupies one row. Picking a company
    /// lands on its first tier and that tier's default model.
    var hyperwhisperCloudVendorBinding: Binding<String> {
        Binding(
            get: { selectedCloudTier.vendorKey },
            set: { newVendor in
                guard let tier = CloudAccuracyTier.defaultTier(forVendorKey: newVendor) else { return }
                cloudAccuracyTier = tier.rawValue
                cloudTranscriptionModel = tier.defaultModelId
                cloudTranscriptionDomain = nil
            }
        )
    }

    /// Model dropdown selection. The tier is what becomes the `X-STT-Provider`
    /// header, so choosing a model also moves the tier to whichever one owns
    /// that model — the only way a merged company row can route correctly.
    var hyperwhisperCloudModelBinding: Binding<String> {
        Binding(
            get: { cloudTranscriptionModel },
            set: { newModel in
                let owner = selectedCloudTier.tierOwningModel(newModel)
                if owner != selectedCloudTier {
                    cloudAccuracyTier = owner.rawValue
                    cloudTranscriptionDomain = nil
                }
                cloudTranscriptionModel = newModel
                if newModel == "dictation" { cloudTranscriptionDomain = nil }
            }
        )
    }

    /// Whether the Medical domain toggle should be shown. Only assemblyAI uses a
    /// domain-based medical mode; Deepgram medical is a model selection instead.
    var showsMedicalDomainToggle: Bool {
        currentCloudProvider == .hyperwhisper && selectedCloudTier == .assemblyAI && cloudTranscriptionModel != "dictation"
    }

    /// Binding for the Medical toggle → maps the nullable domain string to a Bool.
    var medicalDomainBinding: Binding<Bool> {
        Binding(
            get: { cloudTranscriptionDomain == "medical" },
            set: { cloudTranscriptionDomain = $0 ? "medical" : nil }
        )
    }

    /// Custom-vocabulary visibility for the HyperWhisper Cloud path follows the
    /// SELECTED model's catalog flag (e.g. ElevenLabs Scribe v1 unsupported).
    private var hyperwhisperCloudVocabularyUnsupported: Bool {
        guard currentCloudProvider == .hyperwhisper else { return false }
        return !selectedCloudTier.supportsCustomVocabulary(forModelId: cloudTranscriptionModel)
    }

    // MARK: - Name lock

    /// Whether this editor is open on the one mode whose name is fixed.
    ///
    /// The FLAG, not the name. This used to read `mode?.name == "Default"`, which is
    /// the mode's label rather than its identity, and got both directions wrong: a
    /// mode the user named "Default" themselves was locked, and the real default
    /// stopped being locked as soon as a restored backup carried another name
    /// (`PersistenceController` sets `isDefault` on a restored mode without
    /// constraining what it is called). Windows has always keyed the same rule on
    /// `IsDefault`; issue #494 was the two platforms disagreeing about it.
    ///
    /// #285 is the third reason the string compare cannot come back: the shared
    /// first-run seed (`hw_catalog::mode_seed`) names the mode `"Hyper"`, so
    /// `== "Default"` would now lock nothing at all on a fresh install. The seed
    /// writes `is_default: true`, so the flag still identifies that row.
    var isDefaultMode: Bool {
        Self.isNameLocked(configuration.mode)
    }

    /// Static so hyperwhisperTests can assert the rule without standing up the view;
    /// the regression it guards is precisely a return to the string compare.
    static func isNameLocked(_ mode: Mode?) -> Bool {
        mode?.isDefault == true
    }

    // MARK: - On-appear repair

    /// The cloud half of the editor's on-appear repair, run from `body`'s
    /// first `.onAppear` right after the On-device clamp: open the full model
    /// list when the saved model is not a popular one, and snap a stored cloud
    /// model the Model dropdown does not offer onto one it does.
    func repairCloudSelectionOnAppear() {
        if ModeEditorSeed.shouldShowAllCloudTranscriptionModels(
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
            // Validate against exactly what the Model dropdown OFFERS
            // (`hyperwhisperCloudModels`), not against `tier.models`. The two
            // disagreed in both directions: the dropdown spans the whole
            // vendor group, so a valid sibling-tier model was being reset to
            // the tier default, and it excludes the live-only ids, so
            // `gemini-3.5-transcribe-live` passed the clamp and then matched
            // no tag — a blank menu button on a mode whose every dictation
            // 400s. A clamp that accepts what the picker refuses to show is
            // not a clamp.
            cloudTranscriptionModel = Self.clampedCloudTranscriptionModel(
                tier: tier,
                storedModelId: cloudTranscriptionModel
            )
            // A domain only makes sense for assemblyAI; clear stale values.
            if !showsMedicalDomainToggle {
                cloudTranscriptionDomain = nil
            }
        } else if provider == .cloud,
                  !CloudTranscriptionModels.models(for: currentCloudProvider)
                      .contains(where: { $0.id == cloudTranscriptionModel }) {
            // Same repair for the BYOK path: a stored model that this provider
            // doesn't offer (retired id, leftover from another provider, or the
            // "whisper-1" default landing on Grok, whose only model id is "")
            // matches no picker tag, so SwiftUI logs "Picker: the selection is
            // invalid" and renders an empty menu button. Checking the FULL model
            // list — not the popular subset — so a valid but non-popular
            // selection is left alone.
            cloudTranscriptionModel = CloudTranscriptionModels.defaultModel(for: currentCloudProvider)
        }
    }
}
