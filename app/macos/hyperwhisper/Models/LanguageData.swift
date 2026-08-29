//
//  LanguageData.swift
//  hyperwhisper
//
//  The macOS facade over the shared language catalog (issue #285).
//
//  The 126-row table, the BCP-47 canonicalizer and the alias map that used to
//  live here are gone. They now live once, in Rust, at
//  `shared-core-rs/crates/hw-catalog/src/language.rs`, reached through the
//  UniFFI binding (`languageAll`, `languageInfo`, `languageCanonicalize`,
//  `languageCanonicalCode`, `languageNormalize`, `languageIsEnglish`,
//  `languageResolve`, `languagePopularCodes`, `languagePrioritizeAutomatic`).
//  Windows reads the same rows, so a code stored by one platform now resolves
//  on the other. `shared-conformance/language-vectors.json` pins the answers;
//  `LanguageConformanceVectorTests` runs them through this type.
//
//  Every member below keeps the name, shape and behaviour its call sites
//  already depend on. Two things did change for the user:
//
//  - `zh-TW` reads "Chinese (Traditional, Taiwan)" instead of "Chinese
//    (Traditional)", which `zh-Hant` also said. The picker showed the same
//    words twice and the tail of `allLanguages` is sorted by display name, so
//    the duplicate also made that order depend on a hash map.
//  - Lookups are canonical-then-case-insensitive for every code, so a stored
//    `en_GB` or `zh-hant` resolves rather than falling through to the native
//    fallback.
//
//  # What stays native
//
//  `fallbackDisplayName(for:)` only. A code the catalog does not know gets its
//  name from `Locale.localizedString`, a system database the shared crate has
//  no business carrying: the core returns `displayName == nil` and this file
//  fills it in. That is the whole of the deliberate split.
//

import Foundation

/// Language data for speech-to-text providers (Whisper-compatible + extensions)
struct LanguageData {
    struct LanguageInfo: Identifiable, Hashable {
        let code: String
        let displayName: String

        var id: String { code }
    }

    static let automaticCode = "auto"

    /// All supported languages with display names in canonical order
    ///
    /// Cached: the order and the rows are fixed for the life of the process,
    /// and SwiftUI re-reads this on every body evaluation of a language picker.
    /// One crossing at first use, none after.
    static let allLanguages: [LanguageInfo] = languageAll().map { info(from: $0) }

    /// Popular languages that appear at the top of the list
    static let popularLanguageCodes: [String] = languagePopularCodes()

    /// The region and script variants that are in the catalog but not in the
    /// Whisper universal set: `whisperUniversalCodes` is the catalog minus
    /// these, which is exactly the row set both native lists carried before
    /// #285 (`shared-conformance/language-vectors.json` marks them
    /// `decision: "macos"`, and `LanguageConformanceVectorTests` asserts this
    /// set still matches that bucket).
    ///
    /// `zh-TW` is deliberately absent: it is a variant tag, but Whisper has
    /// always advertised it and it is one of the popular picker rows.
    private static let regionalVariantCodes: Set<String> = [
        "en-US", "en-GB", "en-AU", "en-IN", "en-NZ", "en-CA", "en-IE",
        "es-419", "es-latam",
        "pt-BR", "pt-PT",
        "fr-CA", "da-DK", "sv-SE", "nl-BE", "de-CH", "ko-KR", "th-TH",
        "zh-CN", "zh-Hans", "zh-Hant", "zh-HK",
        "hi-Latn", "taq",
    ]

    /// Language codes that Whisper and most providers support (includes "auto")
    ///
    /// Derived from the shared catalog rather than re-listed, and cached for
    /// the same reason `allLanguages` is: it backs `STTLanguageTemplates`
    /// and `LibraryLanguageFilter.allCodes`, both read per picker row.
    static let whisperUniversalCodes: [String] = allLanguages
        .map { $0.code }
        .filter { !regionalVariantCodes.contains($0) }

    /// Catalog rows by canonical code, for the per-row lookups the pickers do.
    ///
    /// `info(for:)` and `displayName(for:)` are called once per row while a
    /// picker builds, so the common case — a code that is already canonical —
    /// answers from this map with no FFI crossing at all. Anything else
    /// (`en_GB`, `EN-GB`, a padded tag, a code outside the catalog) falls
    /// through to `languageInfo(code:)`, which canonicalizes and does the
    /// case-insensitive second pass in the core. The two paths cannot
    /// disagree: the map is built from `languageAll()`, keyed by the codes the
    /// core itself considers canonical.
    private static let catalogByCode: [String: LanguageInfo] = Dictionary(
        uniqueKeysWithValues: allLanguages.map { ($0.code, $0) })

    /// Get display name for a language code
    static func displayName(for code: String) -> String {
        if let info = info(for: code) {
            return info.displayName
        }

        return fallbackDisplayName(for: code)
    }

    /// Check if a language code represents English
    static func isEnglish(_ code: String?) -> Bool {
        languageIsEnglish(code: code)
    }

    /// Normalize a language code to 2-letter ISO 639 format
    /// This helps prevent issues with Apple frameworks that expect 2-letter codes
    /// - Parameter code: The language code to normalize (e.g., "en-GB", "en-US")
    /// - Returns: The normalized 2-letter code (e.g., "en")
    static func normalizeLanguageCode(_ code: String?) -> String {
        languageNormalize(code: code)
    }

    /// Lookup canonical language info (if available)
    static func info(for code: String) -> LanguageInfo? {
        if let cached = catalogByCode[code] {
            return cached
        }
        return languageInfo(code: code).map { info(from: $0) }
    }

    /// Return canonical codes + display names for a given list (deduplicated)
    static func languages(for codes: [String], context: String? = nil) -> [LanguageInfo] {
        languageResolve(codes: codes).map { language in
            if let displayName = language.displayName {
                return LanguageInfo(code: language.code, displayName: displayName)
            }

            // The core does not know the code, so there is no shared answer to
            // read: name it from the system database and say so.
            assertionFailure(
                "Unknown language code \(language.code) encountered\(context.map { " in \($0)" } ?? "")")
            return LanguageInfo(
                code: language.code, displayName: fallbackDisplayName(for: language.code))
        }
    }

    /// Ensure "Automatic" stays at the top of picker lists
    static func prioritizeAutomatic(_ languages: [LanguageInfo]) -> [LanguageInfo] {
        let ordered = languagePrioritizeAutomatic(
            languages: languages.map { HwLanguage(code: $0.code, displayName: $0.displayName) })
        return ordered.map { info(from: $0) }
    }

    /// Canonical BCP-47 language code for storage
    static func canonicalLanguageCode(_ code: String?) -> String {
        languageCanonicalCode(code: code)
    }

    /// Display tuple helper for pickers that still expect (code, name)
    static func pickerTuples(from languages: [LanguageInfo]) -> [(code: String, name: String)] {
        languages.map { ($0.code, $0.displayName) }
    }

    /// A core row as the view-facing struct. A `nil` display name means the
    /// catalog does not know the code; that is the native fallback's job.
    private static func info(from language: HwLanguage) -> LanguageInfo {
        LanguageInfo(
            code: language.code,
            displayName: language.displayName ?? fallbackDisplayName(for: language.code))
    }

    private static func fallbackDisplayName(for code: String) -> String {
        let canonical = languageCanonicalize(code: code)

        let locale = Locale(identifier: "en")
        if let localized = locale.localizedString(forIdentifier: canonical), !localized.isEmpty {
            return localized.capitalized(with: locale)
        }

        if let languageComponent = canonical.split(separator: "-").first,
           let localized = locale.localizedString(forLanguageCode: String(languageComponent)),
           !localized.isEmpty {
            return localized.capitalized(with: locale)
        }

        return canonical
    }
}
