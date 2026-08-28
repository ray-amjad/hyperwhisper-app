//
//  SharedModelsCatalog.swift
//  hyperwhisper
//
//  macOS facade over shared-models/models-catalog.json — the cross-platform
//  source of truth for per-model metadata (custom-vocabulary support,
//  HyperWhisper Cloud routability, cloud language sets). See shared-models/CLAUDE.md.
//
//  The native decoder that used to live here (`Entry`, `CatalogFile`, the
//  bundle loader, the `LoadState` machine and its assert + Sentry reporting) is
//  gone: every real lookup already delegated to the Rust core, and only the
//  parity tests still read the local copy (issue #280). The catalog JSON is
//  include_str!'d into the core at compile time, so there is nothing to load
//  from the bundle and no load failure to report. Tests that need to scan the
//  catalog call `modelsAllEntries()` on the shared core instead.
//

import Foundation

enum SharedModelsCatalog {

    /// Voice vs text disambiguates IDs that exist as both a transcription
    /// model and a post-processing LLM (the Gemini family is the canonical
    /// example). Lookups must pass the kind to avoid inheriting the wrong
    /// row's flags.
    enum Kind: String, Hashable {
        case voice
        case text
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

    // MARK: - Public API

    /// Map the native `Kind` to the shared-core `HwKind`.
    private static func hwKind(_ kind: Kind) -> HwKind {
        switch kind {
        case .voice: return .voice
        case .text:  return .text
        }
    }

    /// Look up a row by `(provider, kind, id)`, falling back to the
    /// provider/kind wildcard row (`id == "*"`) when the exact id isn't
    /// catalogued — used for local providers (Apple Speech, Whisper, Parakeet)
    /// where every model shares the same flags. Nil on a miss.
    static func entry(provider: String, kind: Kind, id: String) -> ModelsEntry? {
        modelsEntry(provider: provider, kind: hwKind(kind), id: id)
    }

    /// Every catalogued row, ordered by `(provider, kind, id)`. For parity
    /// guards and tests that scan the catalog rather than look up one key.
    static func allEntries() -> [ModelsEntry] {
        modelsAllEntries()
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
        // Catalog key is camelCase (the enum's rawValue is lowercased so it can
        // round-trip through the Local API's `engine.lowercased()`).
        case .geminiTranscribe:    return "geminiTranscribe"
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

}
