//
//  SharedModelsCatalog.swift
//  hyperwhisper
//
//  Loader for shared-models/models-catalog.json — cross-platform source of
//  truth for per-model metadata (custom-vocabulary support, HyperWhisper
//  Cloud routability). See shared-models/CLAUDE.md.
//

import Foundation
import os

enum SharedModelsCatalog {

    /// Voice vs text disambiguates IDs that exist as both a transcription
    /// model and a post-processing LLM (the Gemini family is the canonical
    /// example). Lookups must pass the kind to avoid inheriting the wrong
    /// row's flags.
    enum Kind: String, Hashable {
        case voice
        case text
    }

    struct Entry: Decodable {
        let provider: String
        let id: String
        let kind: String
        let supportsCustomVocabulary: Bool
        let availableViaHyperWhisperCloud: Bool
        let platforms: [String]
        let displayName: String?
        let notes: String?
        /// Base ISO language codes this CLOUD voice model supports (region/script
        /// stripped). `nil`/absent on local rows (their language sets live in the
        /// per-platform model registries) and on `supportsAllLanguages` rows.
        let supportedLanguages: [String]?
        let isEnglishOnly: Bool?
        /// When true the model passes every language filter (Whisper-family,
        /// Google Chirp, Gemini, Grok). See models-catalog schema.
        let supportsAllLanguages: Bool?
    }

    /// Resolved language filter capability for a single (cloud) model.
    struct LanguageSupport {
        /// Base ISO codes (region stripped). Empty when `supportsAll` is true.
        let codes: Set<String>
        let supportsAll: Bool

        /// Whether this model should pass the library filter for `baseCode`
        /// (already region-stripped, e.g. "es"). A prefix check tolerates any
        /// stray region-qualified entry that slipped past normalization.
        func supports(_ baseCode: String) -> Bool {
            supportsAll || codes.contains(baseCode) || codes.contains { $0.hasPrefix(baseCode + "-") }
        }
    }

    private struct CatalogFile: Decodable {
        let schemaVersion: Int
        let models: [Entry]
    }

    private static let logger = Logger(subsystem: "com.hyperwhisper.app", category: "SharedModelsCatalog")

    private struct Key: Hashable {
        let provider: String
        let kind: Kind
        let id: String
    }

    private enum LoadState {
        case loaded([Key: Entry])
        case absent
        case malformed(String)
    }

    /// Eager-loaded once at first access. The catalog is small (≤ 50 entries)
    /// and the lookup is on the hot path of `ModelLibraryManager.rebuild()`.
    private static let loadState: LoadState = {
        guard let data = loadData() else { return .absent }
        do {
            let decoded = try JSONDecoder().decode(CatalogFile.self, from: data)
            var map: [Key: Entry] = [:]
            map.reserveCapacity(decoded.models.count)
            for entry in decoded.models {
                let kind = Kind(rawValue: entry.kind) ?? .voice
                map[Key(provider: entry.provider, kind: kind, id: entry.id)] = entry
            }
            return .loaded(map)
        } catch {
            return .malformed(error.localizedDescription)
        }
    }()

    private static let reportLock = NSLock()
    private static var reportedLoadFailure = false

    private static func entriesMap() -> [Key: Entry]? {
        switch loadState {
        case .loaded(let map):
            return map
        case .absent:
            reportLoadFailureOnce("models-catalog.json not found in app bundle")
            return nil
        case .malformed(let detail):
            reportLoadFailureOnce("models-catalog.json failed to decode: \(detail)")
            return nil
        }
    }

    private static func reportLoadFailureOnce(_ message: String) {
        reportLock.lock()
        let alreadyReported = reportedLoadFailure
        reportedLoadFailure = true
        reportLock.unlock()
        guard !alreadyReported else { return }

        logger.error("\(message, privacy: .public)")

        // In DEBUG: trip an assertion so a developer notices immediately
        // during a clean build. The Xcode folder reference for
        // `shared-models/` is the usual culprit.
        assertionFailure("SharedModelsCatalog: \(message). Check that the shared-models folder reference is in the Resources build phase.")

        // In release: surface a single Sentry event so a regression in the
        // bundle layout isn't silent. Logger alone goes unnoticed in OSLog.
        SentryService.captureMessage("SharedModelsCatalog load failed: \(message)")
    }

    private static func loadData() -> Data? {
        // Folder reference adds the file under a `shared-models/` subdirectory
        // in the bundle. Fall back to top-level for robustness.
        let candidates: [URL?] = [
            Bundle.main.url(forResource: "models-catalog", withExtension: "json", subdirectory: "shared-models"),
            Bundle.main.url(forResource: "models-catalog", withExtension: "json")
        ]
        for case let url? in candidates {
            if let data = try? Data(contentsOf: url) { return data }
        }
        return nil
    }

    // MARK: - Public API

    /// Look up an entry by `(provider, kind, id)`. Falls back to the
    /// provider/kind wildcard entry (`id == "*"`) if the exact id isn't
    /// catalogued — used for local providers (Apple Speech, Whisper,
    /// Parakeet, etc.) where every model shares the same flags.
    static func entry(provider: String, kind: Kind, id: String) -> Entry? {
        guard let map = entriesMap() else { return nil }
        if let exact = map[Key(provider: provider, kind: kind, id: id)] {
            return exact
        }
        return map[Key(provider: provider, kind: kind, id: "*")]
    }

    /// All catalogued entries. Primarily for tests (e.g. the language-parity
    /// guard) that need to iterate the catalog rather than look up one key.
    static func allEntries() -> [Entry] {
        guard let map = entriesMap() else { return [] }
        return Array(map.values)
    }

    /// Map the native `Kind` to the shared-core `HwKind`.
    private static func hwKind(_ kind: Kind) -> HwKind {
        switch kind {
        case .voice: return .voice
        case .text:  return .text
        }
    }

    static func supportsCustomVocabulary(provider: String, kind: Kind, id: String) -> Bool {
        modelsSupportsCustomVocabulary(provider: provider, kind: hwKind(kind), id: id)
    }

    static func availableViaHyperWhisperCloud(provider: String, kind: Kind, id: String) -> Bool {
        modelsAvailableViaHwCloud(provider: provider, kind: hwKind(kind), id: id)
    }

    /// Language filter capability for a CLOUD voice model. Local providers carry
    /// no language data in the catalog (their rows are wildcards), so callers
    /// resolve those in-code; for a cloud row with neither `supportedLanguages`
    /// nor `supportsAllLanguages` set, this returns `supportsAll: true` so an
    /// uncatalogued model is never wrongly hidden.
    static func languageSupport(provider: String, kind: Kind, id: String) -> LanguageSupport {
        let support = modelsLanguageSupport(provider: provider, kind: hwKind(kind), id: id)
        return LanguageSupport(codes: Set(support.codes), supportsAll: support.supportsAll)
    }
}

// MARK: - Provider key bridging
//
// The catalog uses string provider names; the Swift code uses several distinct
// enums (`CloudProvider`, `PostProcessingProvider`) plus standalone cases for
// local providers. These helpers make `ModelLibraryManager` call sites read
// naturally without sprinkling raw strings.
//
// Both `providerKey` switches are intentionally exhaustive (no `default`) so
// that adding a new enum case becomes a compile error here — the alternative
// is silently mapping a new provider to its rawValue and missing catalog
// rows because the casing doesn't match.

extension SharedModelsCatalog {
    static func providerKey(_ provider: CloudProvider) -> String {
        switch provider {
        case .hyperwhisper: return "hyperwhisper"
        case .openai:       return "openai"
        case .groq:         return "groq"
        case .deepgram:     return "deepgram"
        case .assemblyAI:   return "assemblyAI"
        case .elevenLabs:   return "elevenLabs"
        case .mistral:      return "mistral"
        case .soniox:       return "soniox"
        case .gemini:       return "gemini"
        case .grok:         return "grok"
        case .microsoftAzureSpeech: return "microsoftAzureSpeech"
        case .googleSpeech:        return "googleSpeech"
        }
    }

    static func providerKey(_ provider: PostProcessingProvider) -> String {
        switch provider {
        case .hyperwhisper: return "hyperwhisper"
        case .openai:       return "openai"
        case .anthropic:    return "anthropic"
        case .gemini:       return "gemini"
        case .groq:         return "groq"
        case .grok:         return "grok"
        case .cerebras:     return "cerebras"
        case .mistral:      return "mistral"
        case .localLLM:     return "localLLM"
        }
    }

    enum LocalProviderKey: String {
        case appleSpeech, localWhisper, parakeet, qwen3ASR, nemotron
    }
}
