import Foundation
import os

/// Loads and exposes `shared-app-classification/cloud-stt-catalog.json` — the
/// cross-platform source of truth for cloud STT provider capabilities driving
/// UI affordances (custom-vocab field visibility, credits/min caption,
/// cloud-tier-vs-BYOK list filtering, supported-language hints).
///
/// Mirrors the consumption pattern used by `AppTypeClassifier.loadCatalog()`.
struct CloudSTTCatalog: Decodable {
    let version: Int
    let updated: String
    let providers: [Entry]

    struct Entry: Decodable, Identifiable {
        let id: String
        let displayName: String
        let displayModel: String?
        let vendor: String
        /// The `X-STT-Provider` header value the backend routes on
        /// (e.g. `deepgram`, `azure-mai`, `assemblyai`). Catalog v6+. Optional
        /// so older catalogs still decode; callers fall back conservatively.
        let sttProvider: String?
        let access: Access
        /// Per-provider model list (catalog v6+). Each entry is a routable
        /// `(provider, model)` pair that maps to the `X-STT-Model` header.
        let models: [Model]?
        let cloudTier: CloudTier?
        let customVocabulary: CustomVocabulary?
        let languages: Languages
        let previewStatus: Bool?
        let migrateFrom: [String]?
        let legacyCloudProviderAliases: [String]?
    }

    /// A single selectable model within a provider — drives the Model dropdown
    /// (level 2) of the HyperWhisper Cloud picker and the `X-STT-Model` header.
    struct Model: Decodable, Identifiable {
        let id: String
        let displayName: String
        let creditsPerMinute: Double?
        let isDefault: Bool?
        let previewStatus: Bool?
        let supportsCustomVocabulary: Bool?
    }

    struct Access: Decodable {
        let cloudTierEligible: Bool
        let byokEligible: Bool
    }

    struct CloudTier: Decodable {
        /// "medium" | "high" | "highest"
        let accuracy: String
        let creditsPerMinute: Double
    }

    struct CustomVocabulary: Decodable {
        /// Stored as either Bool or the string `"unverified"`. We expose
        /// a tri-state so the UI can treat unverified as the conservative
        /// default (vocabulary field hidden). Unknown strings default to `.no`
        /// with a `.error` log entry — a single catalog typo must not brick
        /// the whole catalog (aligns with `ArrayOrString` / `IntOrString` /
        /// `BoolOrString` fallthrough behaviour).
        enum Support: Decodable, Equatable {
            case yes, no, unverified

            init(from decoder: Decoder) throws {
                let container = try decoder.singleValueContainer()
                if let bool = try? container.decode(Bool.self) {
                    self = bool ? .yes : .no
                    return
                }
                let str = try container.decode(String.self)
                switch str {
                case "unverified":
                    self = .unverified
                default:
                    CloudSTTCatalog.logger.error(
                        "customVocabulary.supported invalid value=\(str, privacy: .public) — defaulting to .no"
                    )
                    self = .no
                }
            }
        }

        let supported: Support
        let fieldName: String?
        let caveats: String?
    }

    struct Languages: Decodable {
        let count: IntOrString?
        let autoDetect: BoolOrString?
        let codes: ArrayOrString<String>?
        let notes: String?
    }

    /// `count: 60` or `count: "unverified"` — accept either.
    enum IntOrString: Decodable {
        case int(Int)
        case unverified

        init(from decoder: Decoder) throws {
            let c = try decoder.singleValueContainer()
            if let n = try? c.decode(Int.self) { self = .int(n); return }
            if let s = try? c.decode(String.self), s != "unverified" {
                CloudSTTCatalog.logger.error("IntOrString invalid value=\(s, privacy: .public) — defaulting to .unverified")
            }
            self = .unverified
        }

        var asInt: Int? { if case .int(let n) = self { return n } else { return nil } }
    }

    /// `autoDetect: true` or `autoDetect: "unverified"` — accept either.
    enum BoolOrString: Decodable {
        case bool(Bool)
        case unverified

        init(from decoder: Decoder) throws {
            let c = try decoder.singleValueContainer()
            if let b = try? c.decode(Bool.self) { self = .bool(b); return }
            if let s = try? c.decode(String.self), s != "unverified" {
                CloudSTTCatalog.logger.error("BoolOrString invalid value=\(s, privacy: .public) — defaulting to .unverified")
            }
            self = .unverified
        }

        var asBool: Bool? { if case .bool(let b) = self { return b } else { return nil } }
    }

    /// `codes: ["en","es"]` or `codes: "unverified"` — accept either. The
    /// documented `"unverified"` literal is treated as nil so callers fall back
    /// to conservative defaults instead of an empty-but-typed array.
    enum ArrayOrString<Element: Decodable>: Decodable {
        case array([Element])
        case unverified

        init(from decoder: Decoder) throws {
            let c = try decoder.singleValueContainer()
            if let a = try? c.decode([Element].self) { self = .array(a); return }
            if let s = try? c.decode(String.self), s != "unverified" {
                CloudSTTCatalog.logger.error("ArrayOrString invalid value=\(s, privacy: .public) — defaulting to .unverified")
            }
            self = .unverified
        }

        var asArray: [Element]? { if case .array(let a) = self { return a } else { return nil } }
    }
}

// MARK: - Loader + lookups

extension CloudSTTCatalog {
    static let shared: CloudSTTCatalog = loadCatalog()

    private static let logger = Logger(subsystem: "com.hyperwhisper.app", category: "catalog")

    private static func loadCatalog() -> CloudSTTCatalog {
        let urls = [
            Bundle.main.url(forResource: "cloud-stt-catalog", withExtension: "json", subdirectory: "shared-app-classification"),
            Bundle.main.url(forResource: "cloud-stt-catalog", withExtension: "json")
        ].compactMap { $0 }

        var lastError: Error?
        for url in urls {
            do {
                let data = try Data(contentsOf: url)
                return try JSONDecoder().decode(CloudSTTCatalog.self, from: data)
            } catch {
                lastError = error
            }
        }

        if let lastError {
            logger.fault("cloud-stt-catalog.json failed to load — UI falling back to empty catalog. error=\(String(describing: lastError), privacy: .public)")
        } else {
            logger.fault("cloud-stt-catalog.json not bundled — UI falling back to empty catalog")
        }
        assertionFailure("cloud-stt-catalog.json not bundled or malformed — UI will fall back to empty catalog")
        return CloudSTTCatalog(version: 0, updated: "missing", providers: [])
    }

    /// Look up an entry by `id` (matches `CloudAccuracyTier.rawValue` for cloud-tier entries).
    /// Case-insensitive for parity with the Windows `OrdinalIgnoreCase` lookup.
    func entry(byId id: String) -> Entry? {
        providers.first(where: { $0.id.caseInsensitiveCompare(id) == .orderedSame })
    }

    /// Look up an entry whose `migrateFrom` list contains the given alias
    /// (case-insensitive). Drives legacy `cloudAccuracyTier` resolution in
    /// `CloudAccuracyTier.fromStorageValue` — NOT `cloudProvider` rewriting.
    func entry(byMigrateFromAlias alias: String) -> Entry? {
        let needle = alias.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard !needle.isEmpty else { return nil }
        return providers.first { entry in
            guard let aliases = entry.migrateFrom else { return false }
            return aliases.contains { $0.lowercased() == needle }
        }
    }

    /// Look up an entry whose `legacyCloudProviderAliases` list contains the
    /// given alias (case-insensitive). Drives `normalizeCloudProvider` only —
    /// kept deliberately separate from `migrateFrom` so BYOK provider names
    /// never get misinterpreted as cloud-tier migrations.
    func entry(byLegacyCloudProviderAlias alias: String) -> Entry? {
        let needle = alias.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard !needle.isEmpty else { return nil }
        return providers.first { entry in
            guard let aliases = entry.legacyCloudProviderAliases else { return false }
            return aliases.contains { $0.lowercased() == needle }
        }
    }

    /// All entries surfaced under the HyperWhisper Cloud accuracy dropdown,
    /// in catalog order. These are the cloud providers the credit path routes to.
    var cloudTierEntries: [Entry] {
        providers.filter { $0.access.cloudTierEligible }
    }

    // MARK: - Provider → model helpers (catalog v6+)

    /// The `X-STT-Provider` header value for a cloud-tier entry id, sourced
    /// from the catalog `sttProvider` field so it can't drift from the backend.
    /// Returns nil when the entry or its `sttProvider` is missing.
    func sttProvider(forEntryId id: String) -> String? {
        entry(byId: id)?.sttProvider
    }

    /// The selectable models for a provider entry id (catalog order). Empty
    /// when the entry has no `models[]` (older catalog / unknown id).
    func models(forEntryId id: String) -> [Model] {
        entry(byId: id)?.models ?? []
    }

    /// The default model for a provider entry id — the `isDefault: true` model,
    /// falling back to the first listed model, or nil when none exist.
    func defaultModel(forEntryId id: String) -> Model? {
        let models = models(forEntryId: id)
        return models.first(where: { $0.isDefault == true }) ?? models.first
    }

    /// The default model *id* string for a provider entry id, or "" when the
    /// provider has no models (single implicit model — let the backend default).
    func defaultModelId(forEntryId id: String) -> String {
        defaultModel(forEntryId: id)?.id ?? ""
    }

    /// Look up a single model by (provider entry id, model id), case-insensitive
    /// on the model id for parity with the rest of the catalog lookups.
    func model(forEntryId entryId: String, modelId: String) -> Model? {
        models(forEntryId: entryId).first {
            $0.id.caseInsensitiveCompare(modelId) == .orderedSame
        }
    }

    /// Normalize a persisted `cloudProvider` storage value. If the value is a
    /// legacy standalone-provider alias for an entry that is now surfaced as a
    /// HyperWhisper Cloud accuracy tier (e.g. `microsoftazurespeech` →
    /// `azureMaiTranscribe`), returns `(provider: "hyperwhisper", accuracyTier:
    /// <new tier id>)`. Otherwise returns the input unchanged with
    /// `accuracyTier == nil` — critically, BYOK provider names like
    /// `"deepgram"` or `"groq"` pass through untouched even though they appear
    /// in `migrateFrom` for tier-alias resolution.
    func normalizeCloudProvider(_ value: String?) -> (provider: String?, accuracyTier: String?) {
        let normalized = cloudSttNormalizeCloudProvider(value: value)
        return (provider: normalized.provider, accuracyTier: normalized.accuracyTier)
    }
}
