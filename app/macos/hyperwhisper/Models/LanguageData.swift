//
//  LanguageData.swift
//  hyperwhisper
//
//  Centralized language data for speech-to-text providers
//  Defines supported language codes, display names, and helper APIs
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

    private static let baseLanguageTuples: [(code: String, name: String)] = [
        // Popular languages (most commonly used)
        ("en", "English"),
        ("ja", "Japanese"),
        ("es", "Spanish"),
        ("zh", "Chinese"),
        ("zh-TW", "Chinese (Traditional)"),
        ("nl", "Dutch"),
        ("hi", "Hindi"),
        ("ru", "Russian"),
        ("ko", "Korean"),
        ("it", "Italian"),
        ("uk", "Ukrainian"),
        ("pl", "Polish"),
        ("pt", "Portuguese"),
        ("el", "Greek"),
        ("cs", "Czech"),
        ("sv", "Swedish"),
        ("no", "Norwegian"),
        ("da", "Danish"),
        ("id", "Indonesian"),

        // Alphabetical list of ALL other Whisper-supported languages
        ("af", "Afrikaans"),
        ("sq", "Albanian"),
        ("am", "Amharic"),
        ("ar", "Arabic"),
        ("hy", "Armenian"),
        ("as", "Assamese"),
        ("az", "Azerbaijani"),
        ("ba", "Bashkir"),
        ("eu", "Basque"),
        ("be", "Belarusian"),
        ("bn", "Bengali"),
        ("bs", "Bosnian"),
        ("br", "Breton"),
        ("bg", "Bulgarian"),
        ("yue", "Cantonese"),
        ("ca", "Catalan"),
        ("hr", "Croatian"),
        ("et", "Estonian"),
        ("fo", "Faroese"),
        ("fi", "Finnish"),
        ("fr", "French"),
        ("gl", "Galician"),
        ("ka", "Georgian"),
        ("de", "German"),
        ("gu", "Gujarati"),
        ("ht", "Haitian"),
        ("ha", "Hausa"),
        ("haw", "Hawaiian"),
        ("he", "Hebrew"),
        ("hu", "Hungarian"),
        ("is", "Icelandic"),
        ("jw", "Javanese"),
        ("kn", "Kannada"),
        ("kk", "Kazakh"),
        ("km", "Khmer"),
        ("lo", "Lao"),
        ("la", "Latin"),
        ("lv", "Latvian"),
        ("ln", "Lingala"),
        ("lt", "Lithuanian"),
        ("lb", "Luxembourgish"),
        ("mk", "Macedonian"),
        ("mg", "Malagasy"),
        ("ms", "Malay"),
        ("ml", "Malayalam"),
        ("mt", "Maltese"),
        ("mi", "Maori"),
        ("mr", "Marathi"),
        ("mn", "Mongolian"),
        ("my", "Myanmar"),
        ("ne", "Nepali"),
        ("nn", "Nynorsk"),
        ("oc", "Occitan"),
        ("ps", "Pashto"),
        ("fa", "Persian"),
        ("pa", "Punjabi"),
        ("ro", "Romanian"),
        ("sa", "Sanskrit"),
        ("sr", "Serbian"),
        ("sn", "Shona"),
        ("sd", "Sindhi"),
        ("si", "Sinhala"),
        ("sk", "Slovak"),
        ("sl", "Slovenian"),
        ("so", "Somali"),
        ("su", "Sundanese"),
        ("sw", "Swahili"),
        ("tl", "Tagalog"),
        ("tg", "Tajik"),
        ("ta", "Tamil"),
        ("tt", "Tatar"),
        ("te", "Telugu"),
        ("th", "Thai"),
        ("bo", "Tibetan"),
        ("tr", "Turkish"),
        ("tk", "Turkmen"),
        ("ur", "Urdu"),
        ("uz", "Uzbek"),
        ("vi", "Vietnamese"),
        ("cy", "Welsh"),
        ("yi", "Yiddish"),
        ("yo", "Yoruba")
    ]

    private static let baseLanguages: [LanguageInfo] = baseLanguageTuples.map { LanguageInfo(code: canonicalize($0.code), displayName: $0.name) }

    /// Language codes that Whisper and most providers support (includes "auto")
    static let whisperUniversalCodes: [String] = [automaticCode] + baseLanguages.map { $0.code }

    /// Preferred display names for locale variants not covered in the base list
    private static let preferredDisplayNames: [String: String] = [
        automaticCode: "Automatic",
        "en-US": "English (United States)",
        "en-GB": "English (United Kingdom)",
        "en-AU": "English (Australia)",
        "en-IN": "English (India)",
        "en-NZ": "English (New Zealand)",
        "en-CA": "English (Canada)",
        "en-IE": "English (Ireland)",
        "es-419": "Spanish (Latin America)",
        "es-LATAM": "Spanish (LatAm)",
        "pt-BR": "Portuguese (Brazil)",
        "pt-PT": "Portuguese (Portugal)",
        "fr-CA": "French (Canada)",
        "da-DK": "Danish (Denmark)",
        "sv-SE": "Swedish (Sweden)",
        "nl-BE": "Dutch (Belgium)",
        "de-CH": "German (Switzerland)",
        "ko-KR": "Korean (South Korea)",
        "th-TH": "Thai (Thailand)",
        "zh-CN": "Chinese (Simplified, China)",
        "zh-Hans": "Chinese (Simplified)",
        "zh-Hant": "Chinese (Traditional)",
        "zh-HK": "Chinese (Hong Kong)",
        "hi-Latn": "Hindi (Latin)",
        "taq": "Tamasheq"
    ]

    private static let canonicalLanguageMap: [String: LanguageInfo] = {
        var map: [String: LanguageInfo] = [:]

        func insert(_ code: String, name: String) {
            let canonical = canonicalize(code)
            map[canonical] = LanguageInfo(code: canonical, displayName: name)
        }

        insert(automaticCode, name: "Automatic")
        baseLanguages.forEach { map[$0.code] = $0 }
        for (code, name) in preferredDisplayNames {
            insert(code, name: name)
        }
        return map
    }()

    private static let aliasToCanonical: [String: String] = {
        var map: [String: String] = [:]
        for key in canonicalLanguageMap.keys {
            map[key] = key
            map[key.lowercased()] = key
        }
        return map
    }()

    /// All supported languages with display names in canonical order
    static let allLanguages: [LanguageInfo] = {
        var ordered: [LanguageInfo] = []

        if let automatic = info(for: automaticCode) {
            ordered.append(automatic)
        }

        var seen = Set(ordered.map { $0.code })
        for popular in popularLanguageCodes {
            if let info = info(for: popular), !seen.contains(info.code) {
                ordered.append(info)
                seen.insert(info.code)
            }
        }

        // Append remaining languages alphabetically by display name
        let remaining = canonicalLanguageMap.values.filter { !seen.contains($0.code) }
            .sorted { $0.displayName < $1.displayName }
        ordered.append(contentsOf: remaining)

        return ordered
    }()

    /// Get display name for a language code
    static func displayName(for code: String) -> String {
        if let info = info(for: code) {
            return info.displayName
        }

        return fallbackDisplayName(for: code)
    }
    
    /// Check if a language code represents English
    static func isEnglish(_ code: String?) -> Bool {
        guard let code = code else { return true } // Default to English if nil
        // Handle various English locale codes
        return code == "en" || code == "en-US" || code == "en-GB" || code.hasPrefix("en-")
    }
    
    /// Normalize a language code to 2-letter ISO 639 format
    /// This helps prevent issues with Apple frameworks that expect 2-letter codes
    /// - Parameter code: The language code to normalize (e.g., "en-GB", "en-US")
    /// - Returns: The normalized 2-letter code (e.g., "en")
    static func normalizeLanguageCode(_ code: String?) -> String {
        guard let code = code else { return "en" }

        // Handle special case for automatic detection
        if canonicalize(code) == automaticCode { return automaticCode }
        
        // Extract the 2-letter language code from locale codes like "en-GB"
        if code.contains("-") || code.contains("_") {
            let components = canonicalize(code).split(separator: "-").map(String.init)
            if let firstComponent = components.first {
                return firstComponent.lowercased()
            }
        }

        return code.lowercased()
    }

    /// Popular languages that appear at the top of the list
    static let popularLanguageCodes = ["en", "ja", "es", "zh", "zh-TW", "nl", "hi", "ru", "ko", "it", "uk", "pl", "pt", "el", "cs", "sv", "no", "da", "id"]

    /// Index where "Automatic" appears in the full list
    static let automaticIndex = 0

    /// Helper to canonicalize BCP-47 language tags (replace underscores, enforce casing)
    private static func canonicalize(_ code: String) -> String {
        let trimmed = code.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return automaticCode }

        let normalized = trimmed.replacingOccurrences(of: "_", with: "-")
        let parts = normalized.split(separator: "-")
        guard !parts.isEmpty else { return normalized.lowercased() }

        var canonicalParts: [String] = []
        for (index, part) in parts.enumerated() {
            if index == 0 {
                canonicalParts.append(part.lowercased())
            } else if part.count == 2 {
                canonicalParts.append(part.uppercased())
            } else if part.count == 4 {
                canonicalParts.append(part.capitalized)
            } else {
                canonicalParts.append(part.lowercased())
            }
        }

        return canonicalParts.joined(separator: "-")
    }

    /// Lookup canonical language info (if available)
    static func info(for code: String) -> LanguageInfo? {
        let canonical = canonicalize(code)
        if let key = aliasToCanonical[canonical] ?? aliasToCanonical[canonical.lowercased()] {
            return canonicalLanguageMap[key]
        }
        return nil
    }

    /// Return canonical codes + display names for a given list (deduplicated)
    static func languages(for codes: [String], context: String? = nil) -> [LanguageInfo] {
        var seen = Set<String>()
        return codes.compactMap { code in
            let canonical = canonicalize(code)
            guard !seen.contains(canonical) else { return nil }
            seen.insert(canonical)

            if let info = info(for: canonical) {
                return info
            }

            assertionFailure("Unknown language code \(canonical) encountered\(context.map { " in \($0)" } ?? "")")
            return LanguageInfo(code: canonical, displayName: fallbackDisplayName(for: canonical))
        }
    }

    /// Ensure "Automatic" stays at the top of picker lists
    static func prioritizeAutomatic(_ languages: [LanguageInfo]) -> [LanguageInfo] {
        guard let index = languages.firstIndex(where: { $0.code == automaticCode }), index != 0 else {
            return languages
        }

        var reordered = languages
        let automatic = reordered.remove(at: index)
        reordered.insert(automatic, at: 0)
        return reordered
    }

    /// Canonical BCP-47 language code for storage
    static func canonicalLanguageCode(_ code: String?) -> String {
        guard let code = code, !code.isEmpty else { return "en" }
        return canonicalize(code)
    }

    /// Display tuple helper for pickers that still expect (code, name)
    static func pickerTuples(from languages: [LanguageInfo]) -> [(code: String, name: String)] {
        languages.map { ($0.code, $0.displayName) }
    }

    private static func fallbackDisplayName(for code: String) -> String {
        let canonical = canonicalize(code)

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
