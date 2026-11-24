//
//  CloudAccuracyTier.swift
//  HyperWhisper
//
//  Accuracy tier for HyperWhisper Cloud transcription.
//

import SwiftUI

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
    /// Google's cloud-tier slot. Catalog v8 replaced `googleChirp3` with this
    /// entry at the same array index (so "Google" still defaults to the routed
    /// speech model rather than the BYOK LLM row). `googleChirp3` and its
    /// aliases live on in the entry's `migrateFrom`, and
    /// `PersistenceController.migrateGoogleChirp3TierIfNeeded` converges the
    /// persisted values — do NOT re-add the old case, or `fromStorageValue`'s
    /// canonical match at the top would win over the catalog alias and strand
    /// users on a tier with no catalog entry.
    case geminiTranscribe = "geminiTranscribe"
    case elevenLabsScribeV2 = "elevenLabsScribeV2"
    case openaiWhisper = "openaiWhisper"
    case assemblyAI = "assemblyAI"
    case mistralVoxtral = "mistralVoxtral"
    case soniox = "soniox"
    case gemini = "gemini"
    case metaMuse = "metaMuse"

    var id: String { rawValue }

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

    // MARK: - Vendor grouping (Provider dropdown)

    /// The catalog `vendor` key this tier belongs to — the Provider dropdown's
    /// selection tag. Two tiers can share one (Transcribe + Gemini are both
    /// `google`), which is what collapses them into a single Provider row.
    var vendorKey: String {
        CloudSTTCatalog.shared.entry(byId: rawValue)?.vendor ?? rawValue
    }

    /// Rows of the Provider dropdown. Normally the catalog's vendor groups;
    /// falls back to one row per tier if the shared catalog exposes none, so the
    /// UI never goes empty — the guarantee the old `pickerOrder` fallback
    /// carried. The fallback rows are keyed by the tier's own raw value, which
    /// is exactly what `vendorKey` degrades to without a catalog, so the binding
    /// still round-trips.
    static var pickerVendorGroups: [CloudSTTCatalog.VendorGroup] {
        let groups = CloudSTTCatalog.shared.cloudTierVendorGroups
        if !groups.isEmpty { return groups }
        return allCases.map {
            CloudSTTCatalog.VendorGroup(vendorKey: $0.rawValue, displayName: $0.displayName, entries: [])
        }
    }

    /// The tiers offered by the STREAMING cloud-tier picker — the vendors
    /// HyperWhisper Cloud serves a live WebSocket route for, in catalog order.
    ///
    /// A strict subset of `allCases`: the accuracy picker's tiers are about the
    /// batch route, and most of them have no live route at all. The set is
    /// single-sourced in Rust (`streaming_cloud_tier_entries`), so widening it is
    /// a catalog change made in the same commit as the backend route. Anything
    /// this list offers that the backend cannot serve is a 404 at dictation time,
    /// and the catalog has no `enabled` gate to hide a half-finished vendor.
    ///
    /// Empty only if the shared core failed to load; the picker's own default
    /// (`deepgramNova3`) still resolves, so the route never becomes underivable.
    static var streamingEligibleTiers: [CloudAccuracyTier] {
        CloudSTTCatalog.shared.streamingCloudTierEntries.compactMap {
            CloudAccuracyTier(rawValue: $0.id)
        }
    }

    /// The tier a fresh Provider selection lands on: the vendor group's first
    /// entry in catalog order. With no catalog the row's key IS a tier raw
    /// value (see `pickerVendorGroups`), so fall back to parsing it — otherwise
    /// the Provider row would render but refuse every selection. Nil only when
    /// the key matches neither.
    static func defaultTier(forVendorKey vendor: String) -> CloudAccuracyTier? {
        if let group = CloudSTTCatalog.shared.cloudTierVendorGroups.first(where: { $0.id == vendor }),
           let entry = group.defaultEntry,
           let tier = CloudAccuracyTier(rawValue: entry.id) {
            return tier
        }
        return CloudAccuracyTier(rawValue: vendor)
    }

    /// The tier that owns `modelId` within this tier's vendor group. Selecting a
    /// Gemini model under the merged "Google" row has to move the tier from
    /// `geminiTranscribe` to `gemini`, since the tier is what becomes the
    /// `X-STT-Provider` header. Falls back to `self` when nothing matches.
    func tierOwningModel(_ modelId: String) -> CloudAccuracyTier {
        guard let group = CloudSTTCatalog.shared.vendorGroup(forEntryId: rawValue) else { return self }
        let owner = group.models.first { $0.model.id.caseInsensitiveCompare(modelId) == .orderedSame }
        guard let owner, let tier = CloudAccuracyTier(rawValue: owner.entry.id) else { return self }
        return tier
    }

    /// Every model selectable under this tier's Provider row — the whole vendor
    /// group, so the merged "Google" row lists both Google tiers models together.
    var vendorGroupModels: [CloudSTTCatalog.Model] {
        guard let group = CloudSTTCatalog.shared.vendorGroup(forEntryId: rawValue) else { return models }
        return group.models.map(\.model)
    }

    /// `vendorGroupModels` minus anything HyperWhisper Cloud serves only over
    /// the live WebSocket route. This is what the mode editor's Model dropdown
    /// offers: a live-only model reaches `/transcribe` as an HTTP 400, so every
    /// dictation in a mode carrying one fails. See
    /// `CloudSTTCatalog.liveOnlyModelIds` for why the per-model `streaming` flag
    /// is not the right filter.
    var vendorGroupDictationModels: [CloudSTTCatalog.Model] {
        vendorGroupModels.filter { !CloudSTTCatalog.isLiveOnlyModel($0.id) }
    }

    /// This tier's own models, minus the live-only ones — the set the send path
    /// validates a stored `cloudTranscriptionModel` against.
    var dictationModels: [CloudSTTCatalog.Model] {
        models.filter { !CloudSTTCatalog.isLiveOnlyModel($0.id) }
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
        case .geminiTranscribe:
            return "modes.cloudAccuracy.geminiTranscribe.description".localized
        case .openaiWhisper, .assemblyAI, .mistralVoxtral, .soniox, .gemini, .metaMuse:
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
        case .geminiTranscribe:
            return "gemini-transcribe"
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
        case .metaMuse:
            return "meta"
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
