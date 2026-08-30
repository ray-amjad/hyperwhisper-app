//! The language catalog: BCP-47 canonicalization, the alias map, and the one
//! reconciled row set (#285).
//!
//! Two copies existed. `LanguageData.swift` carried 126 rows, a canonicalizer
//! and an alias map; `LanguageInfo.cs` carried 102 of those rows, no
//! canonicalizer and a linear `Code == code` lookup, so a stored `en_GB` or
//! `zh-hant` matched nothing on Windows and the picker fell back to printing the
//! raw code. The 24 rows Windows was missing are every region and script variant
//! — `en-US`, `pt-BR`, `zh-Hans`, `zh-Hant`, `es-419` and the rest.
//!
//! The reconciled set is the superset: macOS drops nothing, Windows gains those
//! 24. Every row is listed in `shared-conformance/language-vectors.json`.
//!
//! # The display-name fallback stays native
//!
//! A code that is not in the table gets no display name from here. macOS asks
//! `Locale.localizedString(forIdentifier:)` for one, which is a system database
//! this crate has no business carrying. [`resolve`] therefore returns
//! `display_name: None` for an unknown code and the host fills it in — the same
//! split the statistics take with the time-zone database.
//!
//! # One duplicate display name, fixed
//!
//! `zh-TW` and `zh-Hant` both read "Chinese (Traditional)", so the macOS picker
//! showed the same words twice and the user could not tell which row they were
//! choosing. `zh-TW` is now "Chinese (Traditional, Taiwan)", matching `zh-CN`'s
//! existing "Chinese (Simplified, China)". That also makes [`all_languages`]
//! deterministic: its tail is sorted by display name, and two equal names left
//! the order up to a hash map's iteration order.

/// The catalog, in the order the two native lists declared it: the popular rows
/// first, then the rest alphabetically by English name, then the region and
/// script variants. [`all_languages`] re-orders it for display; this is the
/// storage order and the review order.
///
/// Codes are already canonical. `es-latam` looks wrong next to `es-419` and is
/// not — [`canonicalize`] lowercases any subtag that is not 2 or 4 characters,
/// so the macOS key for `es-LATAM` has always been `es-latam`.
const LANGUAGES: &[(&str, &str)] = &[
    ("en", "English"),
    ("ja", "Japanese"),
    ("es", "Spanish"),
    ("zh", "Chinese"),
    ("zh-TW", "Chinese (Traditional, Taiwan)"),
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
    ("yo", "Yoruba"),
    ("en-US", "English (United States)"),
    ("en-GB", "English (United Kingdom)"),
    ("en-AU", "English (Australia)"),
    ("en-IN", "English (India)"),
    ("en-NZ", "English (New Zealand)"),
    ("en-CA", "English (Canada)"),
    ("en-IE", "English (Ireland)"),
    ("es-419", "Spanish (Latin America)"),
    ("es-latam", "Spanish (LatAm)"),
    ("pt-BR", "Portuguese (Brazil)"),
    ("pt-PT", "Portuguese (Portugal)"),
    ("fr-CA", "French (Canada)"),
    ("da-DK", "Danish (Denmark)"),
    ("sv-SE", "Swedish (Sweden)"),
    ("nl-BE", "Dutch (Belgium)"),
    ("de-CH", "German (Switzerland)"),
    ("ko-KR", "Korean (South Korea)"),
    ("th-TH", "Thai (Thailand)"),
    ("zh-CN", "Chinese (Simplified, China)"),
    ("zh-Hans", "Chinese (Simplified)"),
    ("zh-Hant", "Chinese (Traditional)"),
    ("zh-HK", "Chinese (Hong Kong)"),
    ("hi-Latn", "Hindi (Latin)"),
    ("taq", "Tamasheq"),
];

/// The code that means "let the provider decide".
pub const AUTOMATIC_CODE: &str = "auto";

/// The rows the pickers float to the top, in the order they appear there.
pub const POPULAR_CODES: &[&str] = &[
    "en", "ja", "es", "zh", "zh-TW", "nl", "hi", "ru", "ko", "it", "uk", "pl", "pt", "el", "cs",
    "sv", "no", "da", "id",
];

/// One catalog row.
#[derive(Clone, PartialEq, Eq, Debug)]
pub struct Language {
    /// The canonical BCP-47 tag.
    pub code: String,
    /// The English display name, or `None` when the code is not in the catalog
    /// and the host has to localize it itself.
    pub display_name: Option<String>,
}

/// Canonicalize a BCP-47 tag: trim it, turn `_` into `-`, lowercase the primary
/// subtag, uppercase a 2-character subtag, title-case a 4-character one, and
/// lowercase anything else. An empty tag means [`AUTOMATIC_CODE`].
///
/// This is the macOS rule, unchanged. Windows had none, which is why a stored
/// `en_GB` never matched its picker.
pub fn canonicalize(code: &str) -> String {
    let trimmed = code.trim();
    if trimmed.is_empty() {
        return AUTOMATIC_CODE.to_string();
    }

    let normalized = trimmed.replace('_', "-");
    let parts: Vec<&str> = normalized.split('-').filter(|part| !part.is_empty()).collect();
    if parts.is_empty() {
        return normalized.to_lowercase();
    }

    parts
        .iter()
        .enumerate()
        .map(|(index, part)| {
            if index == 0 {
                part.to_lowercase()
            } else {
                match part.chars().count() {
                    2 => part.to_uppercase(),
                    4 => title_case(part),
                    _ => part.to_lowercase(),
                }
            }
        })
        .collect::<Vec<String>>()
        .join("-")
}

/// First character upper, the rest lower. Swift's `String.capitalized` on a
/// single 4-character subtag comes to the same thing, and the only subtags this
/// reaches are ISO 15924 scripts (`Hans`, `Hant`, `Latn`).
fn title_case(part: &str) -> String {
    let mut characters = part.chars();
    match characters.next() {
        Some(first) => first.to_uppercase().collect::<String>() + &characters.as_str().to_lowercase(),
        None => String::new(),
    }
}

/// Whether a code means English. A missing code counts as English, which is the
/// macOS default and the behaviour every caller already relies on.
pub fn is_english(code: Option<&str>) -> bool {
    match code {
        None => true,
        Some(code) => {
            let canonical = canonicalize(code);
            canonical == "en" || canonical.starts_with("en-")
        }
    }
}

/// The 2-letter ISO 639 code, for the frameworks that refuse anything longer.
/// [`AUTOMATIC_CODE`] survives; a missing code becomes `en`.
pub fn normalize_language_code(code: Option<&str>) -> String {
    let Some(code) = code else {
        return "en".to_string();
    };

    let canonical = canonicalize(code);
    if canonical == AUTOMATIC_CODE {
        return AUTOMATIC_CODE.to_string();
    }

    match canonical.split('-').next() {
        Some(primary) => primary.to_lowercase(),
        None => canonical,
    }
}

/// The canonical tag to persist. A missing or empty code becomes `en`.
pub fn canonical_language_code(code: Option<&str>) -> String {
    match code {
        Some(code) if !code.is_empty() => canonicalize(code),
        _ => "en".to_string(),
    }
}

/// Look a code up in the catalog. Canonical first, then case-insensitively —
/// that second pass is the whole of the macOS alias map, which held every key
/// twice, once as written and once lowercased.
pub fn info(code: &str) -> Option<Language> {
    let canonical = canonicalize(code);
    if canonical == AUTOMATIC_CODE {
        return Some(Language {
            code: AUTOMATIC_CODE.to_string(),
            display_name: Some("Automatic".to_string()),
        });
    }

    LANGUAGES
        .iter()
        .find(|(catalog_code, _)| *catalog_code == canonical)
        .or_else(|| {
            LANGUAGES
                .iter()
                .find(|(catalog_code, _)| catalog_code.eq_ignore_ascii_case(&canonical))
        })
        .map(|(catalog_code, name)| Language {
            code: (*catalog_code).to_string(),
            display_name: Some((*name).to_string()),
        })
}

/// The whole catalog in picker order: `auto`, then the popular rows in their
/// declared order, then everything else alphabetically by display name.
pub fn all_languages() -> Vec<Language> {
    let mut ordered: Vec<Language> = Vec::with_capacity(LANGUAGES.len() + 1);
    let mut seen: Vec<String> = Vec::with_capacity(LANGUAGES.len() + 1);

    if let Some(automatic) = info(AUTOMATIC_CODE) {
        seen.push(automatic.code.clone());
        ordered.push(automatic);
    }

    for popular in POPULAR_CODES {
        if let Some(language) = info(popular) {
            if !seen.contains(&language.code) {
                seen.push(language.code.clone());
                ordered.push(language);
            }
        }
    }

    let mut remaining: Vec<Language> = LANGUAGES
        .iter()
        .filter(|(code, _)| !seen.iter().any(|taken| taken == code))
        .map(|(code, name)| Language {
            code: (*code).to_string(),
            display_name: Some((*name).to_string()),
        })
        .collect();
    // Display names are unique across the catalog, so this is a total order.
    remaining.sort_by(|left, right| left.display_name.cmp(&right.display_name));
    ordered.append(&mut remaining);

    ordered
}

/// Canonical rows for a provider's advertised code list, deduplicated, in the
/// order given. A code the catalog does not know keeps its canonical form and
/// comes back with no display name for the host to localize.
pub fn resolve(codes: &[String]) -> Vec<Language> {
    let mut resolved: Vec<Language> = Vec::with_capacity(codes.len());
    for code in codes {
        let canonical = canonicalize(code);
        if resolved.iter().any(|language| language.code == canonical) {
            continue;
        }
        resolved.push(match info(&canonical) {
            Some(language) => language,
            None => Language {
                code: canonical,
                display_name: None,
            },
        });
    }
    resolved
}

/// Move `auto` to the front if it is present and not already there.
pub fn prioritize_automatic(languages: Vec<Language>) -> Vec<Language> {
    let Some(index) = languages
        .iter()
        .position(|language| language.code == AUTOMATIC_CODE)
    else {
        return languages;
    };
    if index == 0 {
        return languages;
    }

    let mut reordered = languages;
    let automatic = reordered.remove(index);
    reordered.insert(0, automatic);
    reordered
}

#[cfg(test)]
#[allow(clippy::indexing_slicing, clippy::unwrap_used, clippy::expect_used)]
mod tests {
    use super::*;

    #[test]
    fn the_catalog_is_the_reconciled_superset() {
        // 125 rows plus `auto`. macOS carried all of them; Windows carried 102.
        assert_eq!(LANGUAGES.len(), 125);
        assert_eq!(all_languages().len(), 126);
    }

    #[test]
    fn every_code_is_already_canonical_and_unique() {
        for (code, _) in LANGUAGES {
            assert_eq!(&canonicalize(code), code, "{code} is not canonical");
            assert_eq!(
                LANGUAGES.iter().filter(|(other, _)| other == code).count(),
                1,
                "{code} appears twice"
            );
        }
    }

    #[test]
    fn every_display_name_is_unique() {
        // Not cosmetic: `all_languages` sorts its tail by display name, and two
        // equal names would leave that order undefined. It is also what the
        // user reads in the picker — `zh-TW` and `zh-Hant` both said "Chinese
        // (Traditional)" before this change.
        for (code, name) in LANGUAGES {
            assert_eq!(
                LANGUAGES.iter().filter(|(_, other)| other == name).count(),
                1,
                "{name} is the display name of more than one row, one of them {code}"
            );
        }
    }

    #[test]
    fn the_two_traditional_chinese_rows_now_read_differently() {
        assert_eq!(
            info("zh-TW").and_then(|language| language.display_name),
            Some("Chinese (Traditional, Taiwan)".to_string())
        );
        assert_eq!(
            info("zh-Hant").and_then(|language| language.display_name),
            Some("Chinese (Traditional)".to_string())
        );
    }

    #[test]
    fn canonicalize_follows_the_subtag_length_rules() {
        assert_eq!(canonicalize("EN"), "en");
        assert_eq!(canonicalize("en_gb"), "en-GB");
        assert_eq!(canonicalize("  zh-hant  "), "zh-Hant");
        assert_eq!(canonicalize("ZH-HANT"), "zh-Hant");
        assert_eq!(canonicalize("es-419"), "es-419");
        // Not 2 and not 4 characters, so it lowercases. This is why the macOS
        // key for `es-LATAM` has always been `es-latam`.
        assert_eq!(canonicalize("es-LATAM"), "es-latam");
        assert_eq!(canonicalize("hi-latn"), "hi-Latn");
    }

    #[test]
    fn an_empty_code_means_automatic() {
        assert_eq!(canonicalize(""), AUTOMATIC_CODE);
        assert_eq!(canonicalize("   "), AUTOMATIC_CODE);
        // A tag of nothing but separators is NOT automatic — it is not empty,
        // it just has no subtags, and it comes back lowercased and unmatched.
        // The macOS canonicalizer did the same; pinned here so a tidy-up does
        // not quietly turn a junk stored code into "detect the language".
        assert_eq!(canonicalize("-"), "-");
        assert_eq!(info("-"), None);
    }

    #[test]
    fn the_alias_map_is_a_case_insensitive_second_pass() {
        // Windows' `Code == code` scan matched none of these.
        for alias in ["en_GB", "EN-GB", "en-gb", " en-GB "] {
            assert_eq!(
                info(alias).map(|language| language.code),
                Some("en-GB".to_string()),
                "{alias}"
            );
        }
        assert_eq!(info("nope-nope"), None);
    }

    #[test]
    fn automatic_resolves_without_a_catalog_row() {
        let automatic = info("auto").expect("auto must resolve");
        assert_eq!(automatic.code, AUTOMATIC_CODE);
        assert_eq!(automatic.display_name.as_deref(), Some("Automatic"));
        assert!(!LANGUAGES.iter().any(|(code, _)| *code == AUTOMATIC_CODE));
    }

    #[test]
    fn the_picker_order_is_automatic_then_popular_then_alphabetical() {
        let languages = all_languages();
        assert_eq!(
            languages.first().map(|first| first.code.as_str()),
            Some("auto")
        );
        for (index, popular) in POPULAR_CODES.iter().enumerate() {
            assert_eq!(
                languages.get(index + 1).map(|language| language.code.as_str()),
                Some(*popular)
            );
        }
        let tail: Vec<&str> = languages
            .iter()
            .skip(1 + POPULAR_CODES.len())
            .filter_map(|language| language.display_name.as_deref())
            .collect();
        let mut sorted = tail.clone();
        sorted.sort_unstable();
        assert_eq!(tail, sorted);
    }

    #[test]
    fn resolve_deduplicates_and_keeps_the_given_order() {
        let resolved = resolve_codes(&["fr", "FR", "en_GB", "de"]);
        assert_eq!(
            resolved
                .iter()
                .map(|language| language.code.as_str())
                .collect::<Vec<&str>>(),
            vec!["fr", "en-GB", "de"]
        );
    }

    #[test]
    fn an_unknown_code_comes_back_canonical_with_no_display_name() {
        let resolved = resolve_codes(&["xx_yy"]);
        assert_eq!(resolved.len(), 1);
        let row = resolved.first().expect("one row");
        assert_eq!(row.code, "xx-YY");
        // The host localizes it — `Locale.localizedString` is not this crate's
        // to carry.
        assert_eq!(row.display_name, None);
    }

    #[test]
    fn normalize_reduces_to_the_primary_subtag() {
        assert_eq!(normalize_language_code(Some("en-GB")), "en");
        assert_eq!(normalize_language_code(Some("zh_Hant")), "zh");
        assert_eq!(normalize_language_code(Some("EN")), "en");
        assert_eq!(normalize_language_code(Some("auto")), "auto");
        assert_eq!(normalize_language_code(Some("")), "auto");
        assert_eq!(normalize_language_code(None), "en");
    }

    #[test]
    fn canonical_language_code_defaults_to_english() {
        assert_eq!(canonical_language_code(None), "en");
        assert_eq!(canonical_language_code(Some("")), "en");
        assert_eq!(canonical_language_code(Some("pt_br")), "pt-BR");
    }

    #[test]
    fn english_covers_every_english_variant() {
        assert!(is_english(None));
        assert!(is_english(Some("en")));
        assert!(is_english(Some("en-US")));
        assert!(is_english(Some("en_gb")));
        assert!(!is_english(Some("eng")));
        assert!(!is_english(Some("es")));
        assert!(!is_english(Some("auto")));
    }

    #[test]
    fn prioritize_automatic_only_moves_it_when_it_has_to() {
        let rows = resolve_codes(&["fr", "auto", "de"]);
        let moved = prioritize_automatic(rows);
        assert_eq!(
            moved
                .iter()
                .map(|language| language.code.as_str())
                .collect::<Vec<&str>>(),
            vec!["auto", "fr", "de"]
        );

        let already = prioritize_automatic(resolve_codes(&["auto", "fr"]));
        assert_eq!(
            already.first().map(|first| first.code.as_str()),
            Some("auto")
        );

        let absent = prioritize_automatic(resolve_codes(&["fr", "de"]));
        assert_eq!(absent.len(), 2);
        assert_eq!(absent.first().map(|first| first.code.as_str()), Some("fr"));
    }

    fn resolve_codes(codes: &[&str]) -> Vec<Language> {
        resolve(
            &codes
                .iter()
                .map(|code| (*code).to_string())
                .collect::<Vec<String>>(),
        )
    }
}
