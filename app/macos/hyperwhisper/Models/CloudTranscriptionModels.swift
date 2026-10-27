import Foundation

/// Central registry of all available cloud transcription models
struct CloudTranscriptionModels {
    /// Default model to use when creating new modes with cloud transcription.
    /// Derived, not pinned: this was a fifth copy of OpenAI's default model id
    /// (issue #580), and a literal here would drift from the catalog silently.
    static let defaultModelId = defaultModel(for: .openai)

    /// Get a model by its ID
    /// - Parameter id: The model ID to look up
    /// - Returns: The CloudTranscriptionModel if found, nil otherwise
    static func model(withId id: String) -> CloudTranscriptionModel? {
        // "" names no provider, so there is nothing to default to. Callers that
        // know the provider should use `model(withId:provider:)`.
        guard !id.isEmpty else { return nil }
        let resolved = resolveModelAlias(id, provider: nil)
        return availableModels.first { $0.id == resolved }
    }

    /// Look up a model within a known provider. Safer than the id-only lookup
    /// wherever the provider is already in hand, and the only overload that
    /// resolves an empty id.
    ///
    /// An empty id means "no model recorded" — what every Grok mode saved before
    /// xAI exposed a `model` parameter (2026-09-19) carries, and what any mode
    /// carries before the user opens the Model row. It resolves to the
    /// provider's default, which is the model the request will actually use.
    /// Grok used to answer it with an empty-id registry entry instead.
    static func model(withId id: String, provider: CloudProvider) -> CloudTranscriptionModel? {
        let resolved = id.isEmpty
            ? defaultModel(for: provider)
            : resolveModelAlias(id, provider: provider)
        guard !resolved.isEmpty else { return nil }
        return availableModels.first { $0.provider == provider && $0.id == resolved }
    }
    
    /// Get the display name for a model ID
    /// - Parameter id: The model ID to look up
    /// - Returns: The display name if found, or the ID itself as fallback
    static func displayName(for id: String) -> String {
        model(withId: id)?.displayName ?? id
    }

    /// Get the display name for a model ID within a known provider. Required
    /// wherever a mode may carry no model id — the id-only lookup rejects "" and
    /// falls back to the id, which renders as a blank name.
    /// - Parameters:
    ///   - id: The model ID to look up
    ///   - provider: The provider that owns the model
    /// - Returns: The display name if found, or the ID itself as fallback
    static func displayName(for id: String, provider: CloudProvider) -> String {
        model(withId: id, provider: provider)?.displayName ?? id
    }
    
    /// Get all available model IDs
    /// - Returns: Array of model IDs that are marked as available
    static var availableModelIds: [String] {
        availableModels.filter { $0.isAvailable }.map { $0.id }
    }
    
    /// Get models for a specific provider
    /// - Parameter provider: The cloud provider to filter by
    /// - Returns: Array of models for that provider
    static func models(for provider: CloudProvider) -> [CloudTranscriptionModel] {
        availableModels.filter { $0.provider == provider && $0.isAvailable }
    }

    /// Get the curated default model list for a provider. Falls back to all
    /// provider models when no popular metadata exists.
    static func popularModels(for provider: CloudProvider) -> [CloudTranscriptionModel] {
        let models = models(for: provider)
        let popular = models.filter { $0.isPopular }
        return popular.isEmpty ? models : popular
    }
    
    /// Get default model for a provider
    /// - Parameter provider: The cloud provider
    /// - Returns: The default model ID for that provider
    ///
    /// Read from `cloud-stt-catalog.json` through the shared core, not from a
    /// switch here. This function used to carry its own table and it disagreed
    /// with the catalog for OpenAI — the catalog said `gpt-4o-transcribe`, this
    /// said `whisper-1`, and after PR #597 moved this table (but not the
    /// catalog) off the deprecated ids, it said `gpt-transcribe` — so the same
    /// blank `cloudTranscriptionModel` column
    /// transcribed on a different model, at a different capability set,
    /// depending on which head served the request (issue #580).
    /// `shared-conformance/default-model-vectors.json` pins the answer and every
    /// head replays it through its own resolver.
    static func defaultModel(for provider: CloudProvider) -> String {
        guard let entryId = catalogEntryId(for: provider) else {
            switch provider {
            // HyperWhisper Cloud routes by accuracy tier; no client-side model.
            case .hyperwhisper: return ""
            // Chirp 3 lost its catalog entry in v8 but the provider stayed, so
            // its default is pinned — a catalog lookup on the dead id is nil and
            // we would post a request with an empty model.
            case .googleSpeech: return "chirp_3"
            default: return ""
            }
        }
        return CloudSTTCatalog.shared.defaultModelId(forEntryId: entryId)
    }

    /// The `cloud-stt-catalog.json` entry a provider's models live under — the
    /// key `defaultModel(for:)` answers from.
    ///
    /// Two providers deliberately have no entry. `googleChirp3` was retired in
    /// catalog v8 (geminiTranscribe took Google's tier slot) while the standalone
    /// provider stayed, and HyperWhisper Cloud is a routing service rather than a
    /// catalog vendor — it chooses by accuracy tier, so it sends no client-side
    /// model parameter at all. Mirrors Windows
    /// `CloudTranscriptionModels.CatalogEntryId` and the portable head's
    /// `ModeAwareTranscriptionRouter.CatalogTier`.
    static func catalogEntryId(for provider: CloudProvider) -> String? {
        switch provider {
        case .openai: return "openaiWhisper"
        case .groq: return "groqWhisper"
        case .deepgram: return "deepgramNova3"
        case .assemblyAI: return "assemblyAI"
        case .elevenLabs: return "elevenLabsScribeV2"
        case .mistral: return "mistralVoxtral"
        case .soniox: return "soniox"
        case .gemini: return "gemini"
        case .geminiTranscribe: return "geminiTranscribe"
        case .meta: return "metaMuse"
        case .grok: return "grokStt"
        case .microsoftAzureSpeech: return "azureMaiTranscribe"
        case .hyperwhisper, .googleSpeech: return nil
        }
    }
}
