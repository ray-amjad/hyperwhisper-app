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
//! raw replacement to `Regex.Replace`, where .NET expands the substitution
//! tokens `$0`, `$$`, `$&`, `$'`, `` $` ``, `$+`, `$_` and `${name}` (a bare
//! `$5` is only expanded when a group 5 exists — these literal-word patterns
//! have none, so it passed through). We adopt the macOS (literal) behavior: the
//! replacement is pushed into the output as written, fixing the Windows
//! divergence for those tokens.

use std::collections::HashMap;
use std::sync::{Arc, Mutex, OnceLock};

use regex::{Regex, RegexBuilder};

/// Whether `c` is a word character: the regex engine's own Unicode `\w`, the
/// class `\b` is defined by.
fn is_word_char(c: char) -> bool {
    static WORD_CHAR: OnceLock<Regex> = OnceLock::new();
    let word_char = WORD_CHAR.get_or_init(|| Regex::new(r"\A\w\z").unwrap());
    word_char.is_match(c.encode_utf8(&mut [0; 4]))
}

/// The compiled whole-word matcher for one vocabulary word.
///
/// "Whole word" is checked per side, by what the word's first and last
/// character are:
///
/// - **A word-character edge** (`c` in `c#`, `t` in `.net`) is anchored with
///   `\b` in the regex: no word character may sit next to it, so `cat` still
///   leaves `category` alone and `c#` does not match inside `abc#`.
/// - **A symbol edge** (`#` in `c#`, `.` in `.net`, `%` in `5%`) cannot use
///   `\b`: `\b` needs a word character on exactly one side, so `\bC\+\+\b` never
///   matched `I write C++ daily` and every such entry was silently skipped
///   (#2612). Instead the neighbour on that side, if any, must be neither a word
///   character nor the same symbol. That keeps a symbol-only entry (`+`, `%`,
///   `#`) out of `C++`, `5%` and `#tag`, and `c#` out of `c##`, while `c#,`,
///   `(c++)` and `.net.` still match. The `regex` crate has no lookbehind, so
///   this side is checked on each candidate match in [`Matcher::replace_all`].
struct Matcher {
    re: Regex,
    /// The word's first character, when it is a symbol.
    lead_symbol: Option<char>,
    /// The word's last character, when it is a symbol.
    trail_symbol: Option<char>,
}

impl Matcher {
    fn new(word: &str) -> Option<Self> {
        let first = word.chars().next()?;
        let last = word.chars().next_back()?;
        let lead_symbol = (!is_word_char(first)).then_some(first);
        let trail_symbol = (!is_word_char(last)).then_some(last);
        let pattern = format!(
            "{}{}{}",
            if lead_symbol.is_none() { r"\b" } else { "" },
            regex::escape(word),
            if trail_symbol.is_none() { r"\b" } else { "" },
        );
        let re = RegexBuilder::new(&pattern)
            .case_insensitive(true)
            .build()
            .ok()?;
        Some(Self {
            re,
            lead_symbol,
            trail_symbol,
        })
    }

    /// Whether a symbol edge's neighbour (`None` at the text's edge) lets the
    /// match stand.
    fn symbol_neighbour_ok(symbol: Option<char>, neighbour: Option<char>) -> bool {
        match (symbol, neighbour) {
            (Some(s), Some(n)) => !is_word_char(n) && n != s,
            _ => true,
        }
    }

    /// Replace every whole-word occurrence with `replacement`, literally. A
    /// candidate whose symbol edge fails its neighbour check is skipped, and the
    /// search resumes one character later, so a whole-word occurrence that
    /// overlaps a rejected candidate is still found.
    fn replace_all(&self, text: &str, replacement: &str) -> String {
        let mut out = String::with_capacity(text.len());
        let mut copied = 0;
        let mut from = 0;
        while from <= text.len() {
            let Some(m) = self.re.find_at(text, from) else {
                break;
            };
            let before = text[..m.start()].chars().next_back();
            let after = text[m.end()..].chars().next();
            if Self::symbol_neighbour_ok(self.lead_symbol, before)
                && Self::symbol_neighbour_ok(self.trail_symbol, after)
            {
                out.push_str(&text[copied..m.start()]);
                out.push_str(replacement);
                copied = m.end();
                from = m.end().max(m.start() + 1);
            } else {
                from = m.start() + text[m.start()..].chars().next().map_or(1, char::len_utf8);
            }
        }
        out.push_str(&text[copied..]);
        out
    }
}

/// Cache compiled matchers by trimmed search word (case-insensitive, whole-word
/// per [`Matcher`]).
/// Mirrors the Windows compiled-regex cache; macOS rebuilt per call (behaviour is
/// identical, this is purely a speed win that both platforms now share).
fn matcher_for(word: &str) -> Option<Arc<Matcher>> {
    static CACHE: OnceLock<Mutex<HashMap<String, Arc<Matcher>>>> = OnceLock::new();
    let cache = CACHE.get_or_init(|| Mutex::new(HashMap::new()));
    let mut map = cache.lock().unwrap();
    if let Some(m) = map.get(word) {
        return Some(m.clone());
    }
    let m = Arc::new(Matcher::new(word)?);
    map.insert(word.to_string(), m.clone());
    Some(m)
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
    let Some(matcher) = matcher_for(trimmed_word) else {
        return text.to_string();
    };
    matcher.replace_all(text, trimmed_replacement)
}
