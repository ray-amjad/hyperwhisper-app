//! Fuzz both `hw-phonetic` vocabulary entry points over arbitrary input.
//!
//! What this proves: neither pass ever panics. That matters here because the
//! shared-core release profile sets `panic = "abort"` and both passes run in the
//! middle of a transcription — a panic is the app going away with the user's
//! dictation in it, not a failed vocabulary pass.
//!
//! `apply_substring_vocabulary` is the reason this target exists. It folds the
//! transcript (NFD, drop combining marks, lowercase), searches the folded copy,
//! and maps each match's folded byte range back to a byte range in the
//! ORIGINAL — which means real index arithmetic across two different strings,
//! with `indexing_slicing` denied precisely because getting it wrong is easy.
//! The vocabulary is the user's own, but it is arbitrary text: a combining mark
//! on its own, an astral character, a lone surrogate's replacement char, a word
//! that folds to nothing.
//!
//! Run:
//!   cargo +nightly fuzz run vocab -- -max_total_time=300
//!
//! CI never runs this. See fuzz/Cargo.toml for why this package sits outside the
//! shared-core workspace.

#![no_main]

use libfuzzer_sys::fuzz_target;
use unicode_normalization::UnicodeNormalization;

use hw_phonetic::{apply_substring_vocabulary, apply_vocabulary, VocabularyEntry};

/// A replacement field of exactly this marks the row as having NO replacement —
/// a spelling-hint row, which is what the phonetic matcher acts on. Anything
/// else, including the empty string, is `Some`. Both shapes have to be
/// reachable: `Some("")` and `None` take different branches.
const NO_REPLACEMENT: &str = "\u{1}";

fuzz_target!(|data: &[u8]| {
    // Non-UTF-8 bytes are not reachable through the FFI boundary (UniFFI hands
    // Rust a `String`), so lossy-decode rather than discard the input: it keeps
    // every mutation the engine produces useful, and U+FFFD is itself a scalar
    // both passes must survive.
    let decoded = String::from_utf8_lossy(data);

    // `\0` is the field separator, not `\n` — a newline in the TEXT is the
    // whole point of one of the unified rules, so the corpus has to be able to
    // carry one.
    let mut fields = decoded.split('\u{0}');
    let text = fields.next().unwrap_or("");

    let rest: Vec<&str> = fields.collect();
    let mut entries: Vec<VocabularyEntry> = Vec::new();
    let mut index = 0;
    while index < rest.len() {
        let word = rest.get(index).copied().unwrap_or("");
        let replacement = rest.get(index + 1).copied();
        entries.push(VocabularyEntry {
            word: word.to_string(),
            replacement: match replacement {
                None | Some(NO_REPLACEMENT) => None,
                Some(value) => Some(value.to_string()),
            },
        });
        index += 2;
    }

    // ---------------------------------------------------------------------
    // The phonetic matcher.
    // ---------------------------------------------------------------------
    let result = apply_vocabulary(text, &entries);

    // Pure: the process-wide code cache must not change the answer.
    let again = apply_vocabulary(text, &entries);
    assert_eq!(result.text, again.text, "apply_vocabulary is not pure");
    assert_eq!(result.matches, again.matches);
    assert_eq!(result.entry_count, again.entry_count);

    // The build filters only ever DROP rows.
    assert!(
        result.entry_count as usize <= entries.len(),
        "entry_count {} exceeds the {} rows given",
        result.entry_count,
        entries.len()
    );

    // Every correction replaced a token with one of the vocabulary words, as
    // the core normalized it. A replacement that is not in the vocabulary would
    // mean the matcher invented text.
    let words: Vec<String> = entries
        .iter()
        .map(|entry| entry.word.trim().nfc().collect())
        .collect();
    for applied in &result.matches {
        assert!(
            words.contains(&applied.replacement),
            "match replacement {:?} is not a vocabulary word",
            applied.replacement
        );
        assert!(!applied.token.is_empty(), "an empty token was reported");
    }

    // No usable row means no correction, and the only change to the text is the
    // NFC normalization the matcher documents.
    if result.entry_count == 0 {
        assert!(result.matches.is_empty());
        let normalized: String = text.nfc().collect();
        assert_eq!(result.text, normalized, "text changed with no entries");
    }

    // An empty vocabulary is the same statement, from the other side.
    let empty = apply_vocabulary(text, &[]);
    assert!(empty.matches.is_empty());
    assert_eq!(empty.entry_count, 0);

    // ---------------------------------------------------------------------
    // The diacritic-insensitive substring pass.
    // ---------------------------------------------------------------------
    let substituted = apply_substring_vocabulary(text, &entries);
    assert_eq!(
        substituted,
        apply_substring_vocabulary(text, &entries),
        "apply_substring_vocabulary is not pure"
    );

    // THE INVARIANT THIS TARGET IS FOR. When no row can do anything — no
    // replacement, or a word or replacement that is blank once trimmed — the
    // text comes back BYTE-IDENTICAL. Not NFC-normalized, not case-folded, not
    // stripped of its accents. Foundation returns the original for everything
    // outside a match, and the folded copy is a search index, never an output.
    let usable = entries.iter().any(|entry| {
        entry
            .replacement
            .as_deref()
            .is_some_and(|replacement| !replacement.trim().is_empty())
            && !entry.word.trim().is_empty()
    });
    if !usable {
        assert_eq!(
            substituted, text,
            "the substring pass rewrote text no row could match"
        );
    }

    // And an empty vocabulary, from the other side.
    assert_eq!(apply_substring_vocabulary(text, &[]), text);
});
