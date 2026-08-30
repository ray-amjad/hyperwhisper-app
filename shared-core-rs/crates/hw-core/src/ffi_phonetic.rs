//! UniFFI surface for the phonetic encoder and the vocabulary matcher above it
//! (`hw_phonetic`, #283).
//!
//! Follows the `ffi_releasenotes` / `ffi_catalog` shape: the leaf crate's types
//! are **mirrored** here as owned `uniffi::Record`s with `From` impls rather
//! than re-exported, so `hw-phonetic` stays a plain crate that can be fuzzed and
//! unit-tested with no UniFFI in the way.
//!
//! # Why the whole transcript crosses the boundary at once
//!
//! [`phonetic_encode`] is the old surface, and it is one word per call. The
//! matcher that sat on top of it in Swift and C# therefore made roughly one call
//! per vocabulary entry plus one per unique transcript token — ~340 round trips
//! for a 40-entry vocabulary over a 300-word transcript. Windows worked around
//! that twice (a cached matcher object, then a per-call token memo) and
//! `VocabularyProcessor.cs:19-21` recorded the reason it went no further: "no
//! batching (that would diverge from macOS and need new UniFFI surface)".
//!
//! This is that surface. [`phonetic_apply_vocabulary`] takes the transcript and
//! the vocabulary and returns the corrected transcript, so a transcription costs
//! ONE call. `phonetic_encode` stays exported: the golden fixture test drives it,
//! and it is the smaller contract to keep stable.
//!
//! # The `Hw` prefix is not cosmetic
//!
//! An unprefixed `VocabularyEntry` would generate `VocabularyEntry` in
//! `hyperwhisper_core.cs`, one namespace-import away from the heads' own
//! `VocabularyItem` / `Vocabulary` entities. `Hw` keeps the FFI record and the
//! host's persistence type visibly distinct at every call site.

/// One row of the user's vocabulary, as the host stores it.
///
/// `replacement` is `null` (or empty) for a spelling-hint row — those are what
/// [`phonetic_apply_vocabulary`] corrects towards. A row that carries a
/// replacement is skipped by the phonetic pass and handled by
/// [`apply_substring_vocabulary`] / `apply_hardened_replacement` instead, which
/// is exactly what both native matchers did.
#[derive(uniffi::Record)]
pub struct HwVocabularyEntry {
    pub word: String,
    pub replacement: Option<String>,
}

impl From<&HwVocabularyEntry> for hw_phonetic::VocabularyEntry {
    fn from(entry: &HwVocabularyEntry) -> Self {
        hw_phonetic::VocabularyEntry {
            word: entry.word.clone(),
            replacement: entry.replacement.clone(),
        }
    }
}

/// One correction the matcher made.
///
/// The result carries these instead of Rust logging them, so each head keeps its
/// own logger — `os.Logger` on macOS, `LoggingService` on Windows, `ILogger` on
/// Linux — and its own privacy annotations.
#[derive(uniffi::Record)]
pub struct HwPhoneticMatch {
    /// The transcript token as it appeared, trailing punctuation included.
    pub token: String,
    /// The vocabulary spelling it was replaced with.
    pub replacement: String,
}

impl From<hw_phonetic::AppliedMatch> for HwPhoneticMatch {
    fn from(applied: hw_phonetic::AppliedMatch) -> Self {
        HwPhoneticMatch {
            token: applied.token,
            replacement: applied.replacement,
        }
    }
}

/// The whole answer for one transcription.
#[derive(uniffi::Record)]
pub struct HwPhoneticApplyResult {
    /// The corrected transcript, NFC-normalized.
    pub text: String,
    /// Every correction, in the order they were applied.
    pub matches: Vec<HwPhoneticMatch>,
    /// How many vocabulary rows survived the build filters and were matched
    /// against — the number behind both platforms' "Phonetic matcher
    /// initialized with N vocabulary entries" log line, which would otherwise
    /// have no home once the matcher stopped being an object the host builds.
    pub entry_count: u32,
}

impl From<hw_phonetic::PhoneticApplyResult> for HwPhoneticApplyResult {
    fn from(result: hw_phonetic::PhoneticApplyResult) -> Self {
        HwPhoneticApplyResult {
            text: result.text,
            matches: result
                .matches
                .into_iter()
                .map(HwPhoneticMatch::from)
                .collect(),
            entry_count: result.entry_count,
        }
    }
}

/// Encode a word with the Beider-Morse phonetic algorithm.
///
/// Replaces the old C-ABI `bm_encode` (pipe-separated `char*` + manual
/// `bm_free`). Returns the phonetic codes directly; empty input -> empty list.
///
/// Kept for the golden fixture test and for any host that wants the raw codes.
/// A host correcting a transcript should call [`phonetic_apply_vocabulary`]
/// instead — one call rather than one per word.
#[uniffi::export]
pub fn phonetic_encode(word: String) -> Vec<String> {
    hw_phonetic::encode(&word)
}

/// Correct a whole transcript against a whole vocabulary, in one call.
///
/// Whole-word, `\b`-anchored, case-insensitive, literal replacement. Rows that
/// carry a replacement, and words of 2 Unicode scalars or fewer, are skipped. A
/// token that already equals ANY vocabulary word is left alone.
#[uniffi::export]
pub fn phonetic_apply_vocabulary(
    text: String,
    entries: Vec<HwVocabularyEntry>,
) -> HwPhoneticApplyResult {
    let entries: Vec<hw_phonetic::VocabularyEntry> = entries
        .iter()
        .map(hw_phonetic::VocabularyEntry::from)
        .collect();
    hw_phonetic::apply_vocabulary(&text, &entries).into()
}

/// The on-device providers' vocabulary pass: unanchored substring replacement,
/// case-insensitive AND diacritic-insensitive, over the rows that DO carry a
/// replacement, in list order.
///
/// This is deliberately NOT `apply_hardened_replacement`. That one anchors on
/// `\b…\b` and is diacritic-SENSITIVE, and it runs later over the pipeline's own
/// vocabulary list. This one runs first, inside the provider, over its own raw
/// output — the split macOS made explicit in `VocabularyProcessor.swift` (commit
/// 136071d) after finding four byte-identical copies of it.
///
/// Text outside a match comes back byte-identical: its case, its accents and its
/// normalization form are untouched, matching Foundation's
/// `replacingOccurrences(options: [.caseInsensitive, .diacriticInsensitive])`.
#[uniffi::export]
pub fn apply_substring_vocabulary(text: String, entries: Vec<HwVocabularyEntry>) -> String {
    let entries: Vec<hw_phonetic::VocabularyEntry> = entries
        .iter()
        .map(hw_phonetic::VocabularyEntry::from)
        .collect();
    hw_phonetic::apply_substring_vocabulary(&text, &entries)
}
