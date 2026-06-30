//! Beider-Morse phonetic encoding.
//!
//! Ported verbatim from the retired `app/macos/rphonetic-ffi` crate. The only
//! behavioural change is the return shape: the old C-ABI `bm_encode` returned a
//! pipe-separated `char*` that the Swift wrapper (`BeiderMorse.swift`) split on
//! `|`. That split now happens here, so the FFI surface (`hw-core`) can expose a
//! plain `Vec<String>` and retire the manual `bm_free` memory dance.

use rphonetic::{BeiderMorseBuilder, ConfigFiles, Encoder};
use std::sync::OnceLock;

/// The embedded Beider-Morse rule set, built once and reused. `ConfigFiles`
/// parses the embedded `any`/`common` language rules on construction — doing that
/// per call (per word, on the local-transcription hot path) is wasteful. It holds
/// only plain data (no interior mutability), so it is `Send + Sync` and safe to
/// cache in a `static`. The `BeiderMorse` encoder borrows `ConfigFiles`, so it is
/// built fresh from the cached config on each call (cheap — it just copies a few
/// references and scalar settings; the expensive rule parsing is what we cache).
fn config() -> &'static ConfigFiles {
    static CONFIG: OnceLock<ConfigFiles> = OnceLock::new();
    CONFIG.get_or_init(ConfigFiles::default)
}

/// Encode a word into its Beider-Morse phonetic representations.
///
/// Returns the list of phonetic codes (the algorithm may produce several
/// alternatives). Returns an empty `Vec` for empty input — mirroring the old
/// `bm_encode` NULL / `BeiderMorse.encode` empty-array contract.
pub fn encode(word: &str) -> Vec<String> {
    if word.is_empty() {
        return Vec::new();
    }

    let bm = BeiderMorseBuilder::new(config()).build();
    let encoded = bm.encode(word);

    if encoded.is_empty() {
        return Vec::new();
    }

    split_codes(&encoded)
}

/// Split a pipe-separated Beider-Morse encoding into individual codes, dropping
/// empty segments. Swift `String.split(separator:)` drops empty subsequences by
/// default; Rust's `str::split` keeps them, so a `code1||code2` (or a leading /
/// trailing `|`) would otherwise yield a stray empty code that spuriously matches
/// any word whose encoding is also empty. Filtering empties restores parity.
fn split_codes(encoded: &str) -> Vec<String> {
    encoded
        .split('|')
        .filter(|s| !s.is_empty())
        .map(String::from)
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn empty_input_returns_empty() {
        assert!(encode("").is_empty());
    }

    #[test]
    fn known_word_produces_codes() {
        let codes = encode("smith");
        assert!(!codes.is_empty());
        // No pipe characters should survive the split.
        assert!(codes.iter().all(|c| !c.contains('|')));
    }

    #[test]
    fn split_codes_drops_empty_segments() {
        // Crafted input with adjacent / leading / trailing pipes — no empty code
        // must survive (Swift split(separator:) dropped these).
        let codes = split_codes("|code1||code2|");
        assert_eq!(codes, vec!["code1".to_string(), "code2".to_string()]);
        assert!(codes.iter().all(|c| !c.is_empty()));
        // An all-pipe (or empty) string yields no codes at all.
        assert!(split_codes("||").is_empty());
    }
}
