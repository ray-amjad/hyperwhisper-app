//! Beider-Morse phonetic encoding.
//!
//! Ported verbatim from the retired `app/macos/rphonetic-ffi` crate. The only
//! behavioural change is the return shape: the old C-ABI `bm_encode` returned a
//! pipe-separated `char*` that the Swift wrapper (`BeiderMorse.swift`) split on
//! `|`. That split now happens here, so the FFI surface (`hw-core`) can expose a
//! plain `Vec<String>` and retire the manual `bm_free` memory dance.

use rphonetic::{BeiderMorseBuilder, ConfigFiles, Encoder};
use std::collections::HashMap;
use std::sync::{Mutex, OnceLock};

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

/// How many distinct words the process-wide code cache holds before it stops
/// growing.
///
/// The cache exists because [`encode`] is the expensive half of the matcher and
/// the same words recur: every vocabulary entry is re-encoded on every
/// transcription, and a transcript repeats its own tokens. Issue #283 asks for
/// the `OnceLock<Mutex<HashMap<..>>>` shape `hw-text/src/vocab.rs` uses.
///
/// The cap is the one thing added on top of that shape, and it is there because
/// the keys here are not all vocabulary words — they are also arbitrary
/// transcript tokens, which are unbounded over a process lifetime. The retired
/// Windows matcher made exactly that objection in a comment ("an unbounded
/// static cache would grow for the process lifetime") and answered it with a
/// per-call memo instead. Both answers are kept: the per-call memo still runs in
/// [`crate::matcher`], and this cache is bounded so the cross-call win survives
/// without the unbounded growth. Once full it simply stops inserting — lookups
/// still hit for everything already in it, and a miss just costs a fresh encode,
/// which is what the un-cached matcher did on every call anyway.
///
/// 4096 words is far above any real vocabulary (the egress cap is 100 terms) and
/// costs on the order of a few hundred kilobytes.
const CODE_CACHE_CAPACITY: usize = 4096;

/// [`encode`], memoised process-wide.
///
/// Same contract as [`encode`] — this is purely a speed win, and the matcher is
/// the only caller. The FFI's `phonetic_encode` stays on the uncached [`encode`]
/// so a host that still encodes word-by-word cannot fill the cache with
/// transcript tokens.
pub(crate) fn encode_cached(word: &str) -> Vec<String> {
    if word.is_empty() {
        return Vec::new();
    }

    static CACHE: OnceLock<Mutex<HashMap<String, Vec<String>>>> = OnceLock::new();
    let cache = CACHE.get_or_init(|| Mutex::new(HashMap::new()));

    // A poisoned mutex means some other thread panicked while holding it. The
    // map is still structurally sound (nothing here can leave it half-written),
    // and `panic = "abort"` means a panic in release never gets here at all —
    // so recover the guard rather than propagate a panic into a transcription.
    // `unwrap()`/`expect()` are denied in this crate for the same reason.
    {
        let map = match cache.lock() {
            Ok(guard) => guard,
            Err(poisoned) => poisoned.into_inner(),
        };
        if let Some(codes) = map.get(word) {
            return codes.clone();
        }
    }

    let codes = encode(word);

    let mut map = match cache.lock() {
        Ok(guard) => guard,
        Err(poisoned) => poisoned.into_inner(),
    };
    if map.len() < CODE_CACHE_CAPACITY {
        map.insert(word.to_string(), codes.clone());
    }
    codes
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

    #[test]
    fn cached_encode_agrees_with_uncached() {
        for word in ["smith", "hyperwhisper", "parakeet", "smith"] {
            assert_eq!(encode_cached(word), encode(word));
        }
        assert!(encode_cached("").is_empty());
    }
}
