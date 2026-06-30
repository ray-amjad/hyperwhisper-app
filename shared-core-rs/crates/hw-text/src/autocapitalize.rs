//! First-character de-capitalization when inserting mid-sentence. Ported from
//! macOS `AutocapitalizeInsert.swift` / Windows `AutocapitalizeInsert.cs`.
//!
//! Unification note: the macOS version is the **superset** — it additionally
//! guards first-person pronouns ("I", "I'm", "I'll", "I've", "I'd", incl. curly
//! apostrophes) from being lowercased. Windows lacked this and would demote
//! "I think" → "i think" mid-sentence. We adopt the macOS behavior, so Windows
//! gains the pronoun guard. The acronym guard (leading run of capitals) is
//! identical on both platforms.
//!
//! The cursor-context probe (Accessibility API / Win32) stays native; only this
//! pure transform crosses the FFI. `CursorContext` is plain data.

/// Where the caret sits relative to sentence boundaries, as determined by the
/// platform's native cursor probe.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CursorContext {
    /// At the start of a new sentence — leave capitalization as-is.
    StartOfSentence,
    /// Mid-sentence — de-capitalize a stray leading capital (the work below).
    MidSentence,
    /// Couldn't determine — leave as-is.
    Unknown,
}

const FIRST_PERSON_PRONOUNS: &[&str] = &["i", "i'm", "i'll", "i've", "i'd"];
/// Trailing punctuation stripped before the pronoun check (matches macOS).
const PRONOUN_TRIM: &[char] = &['.', ',', '!', '?', ';', ':', '…'];

fn is_first_person_pronoun(token: &str) -> bool {
    if !token.starts_with('I') {
        return false;
    }
    // Normalize curly apostrophe U+2019 -> straight, strip trailing punctuation.
    let normalized = token.replace('\u{2019}', "'");
    let stripped = normalized.trim_end_matches(PRONOUN_TRIM);
    FIRST_PERSON_PRONOUNS.contains(&stripped.to_lowercase().as_str())
}

/// Lowercase the first non-whitespace character when inserting mid-sentence,
/// unless it's an acronym (next char also uppercase letter) or a first-person
/// pronoun. No-op for any other context.
pub fn apply_autocapitalize(text: &str, context: CursorContext) -> String {
    if context != CursorContext::MidSentence {
        return text.to_string();
    }

    // First non-whitespace char and its byte offset.
    let Some((first_idx, first_char)) = text.char_indices().find(|(_, c)| !c.is_whitespace())
    else {
        return text.to_string();
    };
    if !first_char.is_uppercase() {
        return text.to_string();
    }

    // Acronym guard: next char also an uppercase letter => leave untouched.
    let after_first = first_idx + first_char.len_utf8();
    if let Some(next) = text[after_first..].chars().next() {
        if next.is_alphabetic() && next.is_uppercase() {
            return text.to_string();
        }
    }

    // First-person pronoun guard: extract the leading token (to next whitespace).
    let token_end = text[first_idx..]
        .char_indices()
        .find(|(_, c)| c.is_whitespace())
        .map(|(rel, _)| first_idx + rel)
        .unwrap_or(text.len());
    if is_first_person_pronoun(&text[first_idx..token_end]) {
        return text.to_string();
    }

    // Lowercase just the first character.
    let lowered: String = first_char.to_lowercase().collect();
    let mut result = String::with_capacity(text.len());
    result.push_str(&text[..first_idx]);
    result.push_str(&lowered);
    result.push_str(&text[after_first..]);
    result
}
