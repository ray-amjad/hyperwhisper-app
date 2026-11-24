//
//  CloudPostProcessingEngine.swift
//  HyperWhisper
//
//  A catalog-backed HyperWhisper Cloud post-processing engine.
//

import SwiftUI

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
