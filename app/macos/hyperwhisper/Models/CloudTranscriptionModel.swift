import Foundation

/// Cloud Transcription Models Configuration
/// This file defines all available cloud transcription models from various providers for speech-to-text
/// Each model has an ID (used by API) and a display name (shown in UI)
struct CloudTranscriptionModel {
    /// The model identifier used by the API
    let id: String

    /// Whether this model is available for general use
    let isAvailable: Bool

    /// Model description for tooltips
    let description: String

    /// Which provider this model belongs to
    let provider: CloudProvider

    /// Whether this model should appear in the shortened default picker
    let isPopular: Bool

    /// Billing price per second in USD (nil if unknown)
    let pricePerSecond: Double?

    /// The user-visible model name.
    ///
    /// Read from `shared-models/models-catalog.json` through the shared core,
    /// never stored here. This file used to carry its own 33 literals and they
    /// had drifted: Windows said `Whisper Large V3` where this file said
    /// `Whisper Large v3`, and 4 rows appended `(Preview)` beside the
    /// `previewStatus` field that already said so (#837).
    ///
    /// The `?? id` fallback is never reached in a shipped build:
    /// `CloudSttTierParityTests.every_registry_row_resolves_a_catalog_name`
    /// fails when a row has no catalog entry, so a raw id cannot reach a user.
    var displayName: String {
        SharedModelsCatalog
            .entry(provider: SharedModelsCatalog.providerKey(provider), kind: .voice, id: id)?
            .displayName ?? id
    }

    init(
        id: String,
        isAvailable: Bool,
        description: String,
        provider: CloudProvider,
        isPopular: Bool = false,
        pricePerSecond: Double?
    ) {
        self.id = id
        self.isAvailable = isAvailable
        self.description = description
        self.provider = provider
        self.isPopular = isPopular
        self.pricePerSecond = pricePerSecond
    }
}

extension CloudTranscriptionModel {
    /// Convenience computed price per minute
    var pricePerMinute: Double? { pricePerSecond.map { $0 * 60.0 } }
}
