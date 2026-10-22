//
//  CloudPostProcessingModel.swift
//  HyperWhisper
//
//  A catalog-backed HyperWhisper Cloud post-processing model reference.
//

import SwiftUI

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
/// `openai:gpt-5.6-luna`) so Groq and Cerebras don't collide on the shared
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
    static var claudeHaiku: CloudPostProcessingModel { .init(engineId: "anthropic", modelId: "claude-haiku-5-5") }

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
        case "anthropic", "claude-haiku-5-5", "claude-haiku-4-5", "claude-haiku-4.5", "claudehaiku":
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
