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

/// A bounded, thread-safe memo over an encoder.
///
/// This is the cache [`encode_cached`] uses, with its two hardcoded
/// dependencies — the capacity and the encoder — taken as parameters instead.
/// The production instance is the `static` inside [`encode_cached`], built with
/// [`CODE_CACHE_CAPACITY`] and [`encode`], so nothing about the shipped
/// behaviour moves.
///
/// It is a type rather than a free function because the rules worth pinning are
/// the ones that only show up ACROSS calls: that a repeat costs no second
/// encode, and that the cap stops insertion without breaking lookups. A
/// process-wide `static` sized at 4096 can express neither — a test cannot own
/// one, cannot reset one, and cannot fill one without encoding 4096 words into
/// state every other test in the binary then shares.
pub(crate) struct CodeCache {
    /// How many distinct words this cache holds before it stops inserting.
    capacity: usize,
    entries: Mutex<HashMap<String, Vec<String>>>,
}

impl CodeCache {
    /// An empty cache that stops inserting once it holds `capacity` words.
    pub(crate) fn new(capacity: usize) -> Self {
        Self {
            capacity,
            entries: Mutex::new(HashMap::new()),
        }
    }

    /// The cached codes for `word`, or `encode_word(word)` — inserted only while
    /// the cache is below [`Self::capacity`].
    ///
    /// The lock is released before `encode_word` runs, so a slow encode never
    /// blocks another thread's cache hit. That is the order the `static` version
    /// used, and it is the reason the length check happens after the encode
    /// rather than before it.
    pub(crate) fn get_or_encode(
        &self,
        word: &str,
        encode_word: impl FnOnce(&str) -> Vec<String>,
    ) -> Vec<String> {
        {
            let map = self.lock();
            if let Some(codes) = map.get(word) {
                return codes.clone();
            }
        }

        let codes = encode_word(word);

        let mut map = self.lock();
        if map.len() < self.capacity {
            map.insert(word.to_string(), codes.clone());
        }
        codes
    }

    /// A poisoned mutex means some other thread panicked while holding it. The
    /// map is still structurally sound (nothing here can leave it half-written),
    /// and `panic = "abort"` means a panic in release never gets here at all —
    /// so recover the guard rather than propagate a panic into a transcription.
    /// `unwrap()`/`expect()` are denied in this crate for the same reason.
    fn lock(&self) -> std::sync::MutexGuard<'_, HashMap<String, Vec<String>>> {
        match self.entries.lock() {
            Ok(guard) => guard,
            Err(poisoned) => poisoned.into_inner(),
        }
    }
}

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

    static CACHE: OnceLock<CodeCache> = OnceLock::new();
    CACHE
        .get_or_init(|| CodeCache::new(CODE_CACHE_CAPACITY))
        .get_or_encode(word, encode)
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

    /// An encoder that records every word it is asked for. Counting its calls is
    /// the only way to tell a cache hit from a cache miss: both return the same
    /// codes, so the return value alone cannot distinguish them.
    #[derive(Default)]
    struct CountingEncoder {
        calls: Mutex<Vec<String>>,
    }

    impl CountingEncoder {
        fn encode(&self, word: &str) -> Vec<String> {
            self.calls.lock().unwrap().push(word.to_string());
            vec![format!("{word}-code")]
        }

        fn calls(&self) -> Vec<String> {
            self.calls.lock().unwrap().clone()
        }
    }

    #[test]
    fn a_repeated_word_is_encoded_once() {
        let encoder = CountingEncoder::default();
        let cache = CodeCache::new(CODE_CACHE_CAPACITY);

        let first = cache.get_or_encode("smith", |word| encoder.encode(word));
        let second = cache.get_or_encode("smith", |word| encoder.encode(word));

        assert_eq!(first, second);
        assert_eq!(
            encoder.calls(),
            vec!["smith".to_string()],
            "the second call must be served from the memo"
        );
    }

    #[test]
    fn a_full_cache_stops_inserting_but_still_returns_codes() {
        let encoder = CountingEncoder::default();
        let cache = CodeCache::new(2);

        cache.get_or_encode("one", |word| encoder.encode(word));
        cache.get_or_encode("two", |word| encoder.encode(word));

        // The cache is full. A third word is still encoded correctly...
        let codes = cache.get_or_encode("three", |word| encoder.encode(word));
        assert_eq!(codes, vec!["three-code".to_string()]);

        // ...but it is not kept, so a repeat pays for a fresh encode. That is the
        // documented trade: a miss costs what the un-cached matcher always paid.
        assert_eq!(
            cache.get_or_encode("three", |word| encoder.encode(word)),
            codes
        );
        assert_eq!(
            encoder.calls(),
            vec![
                "one".to_string(),
                "two".to_string(),
                "three".to_string(),
                "three".to_string(),
            ]
        );
    }

    #[test]
    fn words_cached_before_the_cap_still_hit_after_it_is_full() {
        let encoder = CountingEncoder::default();
        let cache = CodeCache::new(2);

        cache.get_or_encode("one", |word| encoder.encode(word));
        cache.get_or_encode("two", |word| encoder.encode(word));
        cache.get_or_encode("three", |word| encoder.encode(word));

        let hit = cache.get_or_encode("one", |word| encoder.encode(word));

        assert_eq!(hit, vec!["one-code".to_string()]);
        assert_eq!(
            encoder.calls().len(),
            3,
            "a full cache must stop inserting, not start evicting"
        );
    }

    #[test]
    fn a_poisoned_lock_still_serves_the_cache() {
        let encoder = CountingEncoder::default();
        let cache = CodeCache::new(CODE_CACHE_CAPACITY);
        cache.get_or_encode("smith", |word| encoder.encode(word));

        // Panic while holding the lock. The guard's `Drop` poisons the mutex.
        let died = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            let _guard = cache.entries.lock().unwrap();
            panic!("a thread died holding the code cache lock");
        }));
        assert!(died.is_err());
        assert!(cache.entries.is_poisoned());

        // A poisoned lock must not take a transcription down with it: the cache
        // still answers, and still from the memo.
        assert_eq!(
            cache.get_or_encode("smith", |word| encoder.encode(word)),
            vec!["smith-code".to_string()]
        );
        assert_eq!(encoder.calls(), vec!["smith".to_string()]);
    }
}
