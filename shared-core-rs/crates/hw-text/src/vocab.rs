//! Vocabulary replacement primitive. Ported from macOS
//! `VocabularyProcessor.applyHardenedReplacement` / the inlined per-item loop in
//! Windows `VocabularyProcessor.ApplyReplacements`.
//!
//! This is the per-item primitive: the platform iterates its Core Data / EF
//! vocabulary list and calls this once per (word, replacement). Rust stays
//! query-agnostic; the final whole-text trim is the caller's job (matching the
//! native loop wrappers).
//!
//! Unification note: the replacement is applied **literally** — `$1`, `$&`, etc.
//! in the replacement are NOT expanded as capture-group references. macOS already
//! did this (`NSRegularExpression.escapedTemplate`); the Windows port passed the
//! raw replacement to `Regex.Replace`, so "$5" misbehaved. We adopt the macOS
//! (literal) behavior via `regex::NoExpand`, fixing the Windows bug.

use std::collections::HashMap;
use std::sync::{Mutex, OnceLock};

use regex::{NoExpand, Regex, RegexBuilder};

/// Cache compiled regexes by trimmed search word (case-insensitive, `\b`-anchored).
/// Mirrors the Windows compiled-regex cache; macOS rebuilt per call (behaviour is
/// identical, this is purely a speed win that both platforms now share).
fn regex_for(word: &str) -> Option<Regex> {
    static CACHE: OnceLock<Mutex<HashMap<String, Regex>>> = OnceLock::new();
    let cache = CACHE.get_or_init(|| Mutex::new(HashMap::new()));
    let mut map = cache.lock().unwrap();
    if let Some(re) = map.get(word) {
        return Some(re.clone());
    }
    let pattern = format!(r"\b{}\b", regex::escape(word));
    let re = RegexBuilder::new(&pattern)
        .case_insensitive(true)
        .build()
        .ok()?;
    map.insert(word.to_string(), re.clone());
    Some(re)
}

/// Replace every whole-word, case-insensitive occurrence of `word` with
/// `replacement` (taken literally). Empty/whitespace-only word or replacement is
/// a no-op. Does not trim the result.
pub fn apply_hardened_replacement(text: &str, word: &str, replacement: &str) -> String {
    let trimmed_word = word.trim();
    let trimmed_replacement = replacement.trim();
    if trimmed_word.is_empty() || trimmed_replacement.is_empty() {
        return text.to_string();
    }
    let Some(re) = regex_for(trimmed_word) else {
        return text.to_string();
    };
    re.replace_all(text, NoExpand(trimmed_replacement)).into_owned()
}
