import Foundation
import os

/// Loads and exposes `shared-app-classification/cloud-pp-catalog.json` — the
/// cross-platform source of truth for HyperWhisper Cloud **post-processing**
/// (LLM) engines. Drives the credit-billed (no-key) post-processing Engine +
/// Model picker and the `X-LLM-Provider` / `X-LLM-Model` headers sent to the
/// backend `/post-process` route.
///
/// Mirrors `CloudSTTCatalog` (which does the same for transcription). Prices in
/// the catalog are display/estimate only — actual billing comes from the
/// backend `cost-calculator.ts`, which must be kept in sync (see the catalog's
/// `$schemaDoc` and `shared-app-classification/CLAUDE.md`).
struct CloudPPCatalog: Decodable {
    let version: Int
    let updated: String
    let providers: [Provider]

    /// A post-processing engine (provider). `id` is the provider-qualified key
    /// prefix persisted in `Mode.cloudPostProcessingModel` (`<id>:<modelId>`),
    /// chosen so Groq vs Cerebras don't collide on the shared `gpt-oss-120b`
    /// model id. `llmProvider` is the `X-LLM-Provider` header value.
    struct Provider: Decodable, Identifiable {
        let id: String
        let displayName: String
        /// The `X-LLM-Provider` header value the backend routes on.
        let llmProvider: String
        /// `"openai"` (OpenAI-compatible /chat/completions) or `"anthropic"`
        /// (native /v1/messages). Informational on the client — the request is
        /// routed through the HyperWhisper backend, not called directly.
        let apiStyle: String?
        /// Rollout gate. `nil` is treated as enabled (older catalogs). When
        /// `false`, the app hides the engine so its `X-LLM-Provider` value can't
        /// silently fall back to Cerebras on a backend that hasn't deployed it
        /// yet (wrong model + wrong billing). Flip to `true` once the backend is
        /// live with the provider's API key set.
        let enabled: Bool?
        /// The single engine flagged as the recommended default — drives the
        /// "(Recommended)" badge on the Engine dropdown. `nil`/`false` = not
        /// recommended.
        let isRecommended: Bool?
        let models: [Model]
    }

    /// A selectable model within an engine — drives the Model dropdown and the
    /// `X-LLM-Model` header (`llmModelHeader`, falling back to `id`).
    struct Model: Decodable, Identifiable {
        let id: String
        let displayName: String
        let llmModelHeader: String?
        let pricePerMInput: Double?
        let pricePerMOutput: Double?
        let isDefault: Bool?
        let isRecommended: Bool?
        let accuracy: Int?
        let speed: Int?
        let previewStatus: Bool?
        let enabled: Bool?

        /// The `X-LLM-Model` header value — explicit `llmModelHeader` or the id.
        var modelHeader: String { llmModelHeader ?? id }
    }
}

// MARK: - Loader + lookups

extension CloudPPCatalog {
    static let shared: CloudPPCatalog = loadCatalog()

    private static let logger = Logger(subsystem: "com.hyperwhisper.app", category: "catalog")

    private static func loadCatalog() -> CloudPPCatalog {
        let urls = [
            Bundle.main.url(forResource: "cloud-pp-catalog", withExtension: "json", subdirectory: "shared-app-classification"),
            Bundle.main.url(forResource: "cloud-pp-catalog", withExtension: "json")
        ].compactMap { $0 }

        var lastError: Error?
        for url in urls {
            do {
                let data = try Data(contentsOf: url)
                return try JSONDecoder().decode(CloudPPCatalog.self, from: data)
            } catch {
                lastError = error
            }
        }

        if let lastError {
            logger.fault("cloud-pp-catalog.json failed to load — cloud post-processing picker falling back to empty catalog. error=\(String(describing: lastError), privacy: .public)")
        } else {
            logger.fault("cloud-pp-catalog.json not bundled — cloud post-processing picker falling back to empty catalog")
        }
        assertionFailure("cloud-pp-catalog.json not bundled or malformed — cloud post-processing picker will fall back to empty catalog")
        return CloudPPCatalog(version: 0, updated: "missing", providers: [])
    }

    /// Look up an engine by `id` (case-insensitive, for parity with Windows).
    func provider(byId id: String) -> Provider? {
        providers.first { $0.id.caseInsensitiveCompare(id) == .orderedSame }
    }

    /// Engines surfaced in the Engine dropdown, in catalog order. Hides any
    /// engine gated off by `enabled == false` (un-deployed on the backend).
    /// A `nil` `enabled` is treated as enabled.
    var pickerProviders: [Provider] {
        providers.filter { $0.enabled != false }
    }

    /// Selectable models for an engine, in catalog order (hides `enabled == false`).
    func models(forProviderId id: String) -> [Model] {
        (provider(byId: id)?.models ?? []).filter { $0.enabled != false }
    }

    /// Default model for an engine — `isDefault: true`, else the first listed.
    func defaultModel(forProviderId id: String) -> Model? {
        let models = models(forProviderId: id)
        return models.first { $0.isDefault == true } ?? models.first
    }

    /// Look up a single model within an engine by its model id (case-insensitive).
    func model(forProviderId providerId: String, modelId: String) -> Model? {
        models(forProviderId: providerId).first {
            $0.id.caseInsensitiveCompare(modelId) == .orderedSame
        }
    }

    /// The `X-LLM-Provider` header value for an engine id, or nil if unknown.
    func llmProvider(forProviderId id: String) -> String? {
        provider(byId: id)?.llmProvider
    }
}
