//! Language-aware trailing-space handling for consecutive transcriptions, plus
//! CJK detection. Ported from macOS `SmartSpacing.swift` / Windows
//! `SmartSpacing.cs`.
//!
//! Unification note: the CJK range table here is the macOS **superset** (it
//! includes CJK Extensions B–F, Compatibility Ideographs and Halfwidth/Fullwidth
//! Forms that the Windows port omitted). Adopting the superset only ever
//! classifies *more* text as CJK; Windows gains the wider coverage.

use std::sync::OnceLock;

use regex::Regex;

/// The language code that means "auto-detect" (macOS `LanguageData.automaticCode`).
const AUTOMATIC_CODE: &str = "auto";

/// Language codes that don't use spaces between words (continuous script).
const NO_SPACE_LANGUAGE_CODES: &[&str] =
    &["ja", "zh", "zh-TW", "zh-Hans", "zh-Hant", "ko", "th"];

/// Inclusive CJK Unicode ranges (scalar values). Superset of both platforms.
const CJK_RANGES: &[(u32, u32)] = &[
    (0x4E00, 0x9FFF),   // CJK Unified Ideographs
    (0x3400, 0x4DBF),   // Extension A
    (0x20000, 0x2A6DF), // Extension B
    (0x2A700, 0x2B73F), // Extension C
    (0x2B740, 0x2B81F), // Extension D
    (0x2B820, 0x2CEAF), // Extension E
    (0x2CEB0, 0x2EBEF), // Extension F
    (0xF900, 0xFAFF),   // CJK Compatibility Ideographs
    (0x3040, 0x309F),   // Hiragana
    (0x30A0, 0x30FF),   // Katakana
    (0xAC00, 0xD7AF),   // Hangul Syllables
    (0x1100, 0x11FF),   // Hangul Jamo
    (0xFF00, 0xFFEF),   // Halfwidth & Fullwidth Forms
];

/// Cached `\p{P}` matcher — Unicode punctuation, mirroring Swift
/// `CharacterSet.punctuationCharacters` / .NET `char.IsPunctuation`.
fn punctuation_re() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(r"^\p{P}$").unwrap())
}

fn is_punctuation(c: char) -> bool {
    let mut buf = [0u8; 4];
    punctuation_re().is_match(c.encode_utf8(&mut buf))
}

fn is_no_space_language(language_code: &str) -> bool {
    if NO_SPACE_LANGUAGE_CODES.contains(&language_code) {
        return true;
    }
    // Prefix match for variants (e.g. "zh-CN" matches "zh"). Mirrors the macOS
    // `String(prefix(2))` logic — by Unicode scalars, not bytes.
    let prefix: String = language_code.chars().take(2).collect();
    NO_SPACE_LANGUAGE_CODES.contains(&prefix.as_str())
}

/// Detect whether text *primarily* (>30% of non-space, non-punctuation chars)
/// contains CJK characters. Mixed content like "これはtestです" is still CJK.
pub fn contains_cjk(text: &str) -> bool {
    let mut cjk_count = 0usize;
    let mut total_count = 0usize;

    for c in text.chars() {
        if c.is_whitespace() || is_punctuation(c) {
            continue;
        }
        total_count += 1;
        let value = c as u32;
        if CJK_RANGES.iter().any(|&(lo, hi)| value >= lo && value <= hi) {
            cjk_count += 1;
        }
    }

    if total_count == 0 {
        return false;
    }
    (cjk_count as f64) / (total_count as f64) > 0.3
}

/// Append a trailing space unless the text already ends in whitespace, is empty,
/// or the language (explicit or auto-detected) doesn't use word spaces.
pub fn append_trailing_space(text: &str, mode_language: &str) -> String {
    // STEP 1: already ends with whitespace? Don't double up.
    if let Some(last) = text.chars().last() {
        if last.is_whitespace() {
            return text.to_string();
        }
    } else {
        // STEP 2: empty text.
        return text.to_string();
    }

    // STEP 3: decide based on language.
    let should_add_space = if mode_language == AUTOMATIC_CODE {
        !contains_cjk(text)
    } else {
        !is_no_space_language(mode_language)
    };

    // STEP 4: apply.
    if should_add_space {
        format!("{text} ")
    } else {
        text.to_string()
    }
}
