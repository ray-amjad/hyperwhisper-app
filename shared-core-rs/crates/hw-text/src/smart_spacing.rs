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
///
/// `yue` (Cantonese) comes from the parakeet daemons' own table (issue #286):
/// it is written in Chinese characters, so it joins without spaces, and it does
/// not fall out of the two-character prefix rule the way `zh-CN` does.
const NO_SPACE_LANGUAGE_CODES: &[&str] =
    &["ja", "zh", "zh-TW", "zh-Hans", "zh-Hant", "ko", "th", "yue"];

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

/// Case-insensitive membership test against [`NO_SPACE_LANGUAGE_CODES`].
///
/// The codes are ASCII, and the Windows table this was ported from used
/// `StringComparer.OrdinalIgnoreCase`, so a mode language of `"JA"` or
/// `"ZH-Hant"` must classify exactly like `"ja"` / `"zh-Hant"`.
fn is_no_space_code(code: &str) -> bool {
    NO_SPACE_LANGUAGE_CODES
        .iter()
        .any(|c| c.eq_ignore_ascii_case(code))
}

/// Whether `language_code` is written without spaces between words.
///
/// The single source of truth for the CJK join policy (issue #286). Callers that
/// concatenate transcription segments — the parakeet daemon, the Linux live
/// delivery path — pick their separator from this, so a language added here
/// changes every join at once instead of drifting per-platform table.
///
/// Matching is case-insensitive, surrounding whitespace is ignored, and a
/// regional variant falls back to its two-character prefix (`"zh-CN"` → `"zh"`).
/// `""` and `"auto"` are not no-space languages — with no declared language the
/// caller has nothing to go on, and [`append_trailing_space`] falls back to
/// [`contains_cjk`] instead.
pub fn is_no_space_language(language_code: &str) -> bool {
    // `append_trailing_space` already trims before it gets here; trimming again
    // is free and keeps a head that passes a raw settings value honest.
    let language_code = language_code.trim();
    if is_no_space_code(language_code) {
        return true;
    }
    // Prefix match for variants (e.g. "zh-CN" matches "zh"). Mirrors the macOS
    // `String(prefix(2))` logic — by Unicode scalars, not bytes.
    let prefix: String = language_code.chars().take(2).collect();
    is_no_space_code(&prefix)
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

    // STEP 3: decide based on language. An absent language is "auto": the
    // platforms pass an empty string when the mode has no language set (Windows
    // `SmartSpacing.cs` checks `string.IsNullOrEmpty`), and treating that as an
    // explicit code would append a space to CJK text.
    let language = mode_language.trim();
    let should_add_space = if language.is_empty() || language.eq_ignore_ascii_case(AUTOMATIC_CODE) {
        !contains_cjk(text)
    } else {
        !is_no_space_language(language)
    };

    // STEP 4: apply.
    if should_add_space {
        format!("{text} ")
    } else {
        text.to_string()
    }
}
