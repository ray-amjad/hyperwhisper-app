//! `hw-core` — the single UniFFI surface for the HyperWhisper shared Rust core.
//!
//! Every platform (macOS/iOS Swift, Windows C#, Android Kotlin) links exactly
//! one artifact built from this crate (`libhyperwhisper_core.{a,so}` /
//! `hyperwhisper_core.dll`) and consumes the bindings generated from the
//! `#[uniffi::export]` items below.
//!
//! Milestone 0 exposes only the phonetic encoder (proving the UniFFI pipeline
//! end-to-end against the existing feature). Later milestones re-export
//! `hw-text`, `hw-net`, `hw-license`, `hw-backup` and `hw-catalog` here.

// Sets the binding namespace to `hyperwhisper_core`, so the generators emit
// `hyperwhisper_core.swift` / `hyperwhisper_core.cs` / Kotlin package
// `hyperwhisper_core`, matching the artifact name.
uniffi::setup_scaffolding!("hyperwhisper_core");

// hw-core is split into per-leaf FFI submodules for organization; this stays
// the single integration crate (`setup_scaffolding!` lives here in lib.rs).
mod ffi_backup;
mod ffi_catalog;
mod ffi_license;
mod ffi_net;
mod ffi_prompt;

/// Encode a word with the Beider-Morse phonetic algorithm.
///
/// Replaces the old C-ABI `bm_encode` (pipe-separated `char*` + manual
/// `bm_free`). Returns the phonetic codes directly; empty input -> empty list.
#[uniffi::export]
pub fn phonetic_encode(word: String) -> Vec<String> {
    hw_phonetic::encode(&word)
}

// ===========================================================================
// hw-text (Milestone 1): pure text logic. Thin wrappers over the dep-free
// `hw-text` crate; the FFI-facing enum is mirrored here so `hw-text` stays
// dependency-light (regex only).
// ===========================================================================

/// Where the caret sits relative to sentence boundaries (from the platform's
/// native cursor probe). Mirrors `hw_text::CursorContext`.
#[derive(uniffi::Enum)]
pub enum CursorContext {
    StartOfSentence,
    MidSentence,
    Unknown,
}

impl From<CursorContext> for hw_text::CursorContext {
    fn from(c: CursorContext) -> Self {
        match c {
            CursorContext::StartOfSentence => hw_text::CursorContext::StartOfSentence,
            CursorContext::MidSentence => hw_text::CursorContext::MidSentence,
            CursorContext::Unknown => hw_text::CursorContext::Unknown,
        }
    }
}

/// Lowercase a stray leading capital when inserting mid-sentence (guards
/// acronyms and first-person pronouns).
#[uniffi::export]
pub fn apply_autocapitalize(text: String, context: CursorContext) -> String {
    hw_text::apply_autocapitalize(&text, context.into())
}

/// Append a language-aware trailing space for consecutive transcriptions.
#[uniffi::export]
pub fn append_trailing_space(text: String, mode_language: String) -> String {
    hw_text::append_trailing_space(&text, &mode_language)
}

/// Detect whether text is primarily CJK (no word spaces).
#[uniffi::export]
pub fn contains_cjk(text: String) -> bool {
    hw_text::contains_cjk(&text)
}

/// Replace a single vocabulary word with its replacement (whole-word,
/// case-insensitive, literal replacement). Per-item primitive — the platform
/// loops its vocabulary list.
#[uniffi::export]
pub fn apply_hardened_replacement(text: String, word: String, replacement: String) -> String {
    hw_text::apply_hardened_replacement(&text, &word, &replacement)
}

/// Extract `<<CLEANED>>…<<END>>`-wrapped text from a post-processing response
/// (strict: empty if no start marker).
#[uniffi::export]
pub fn extract_cleaned_from_wrapped(text: String) -> String {
    hw_text::extract_cleaned_from_wrapped(&text)
}

/// Lenient wrapper handling for plain transcription text: extract wrapped content
/// if present, else return the text (stray end-tags stripped). For raw-transcript
/// sites where the strict `extract_cleaned_from_wrapped` would wipe a valid result.
#[uniffi::export]
pub fn strip_wrapper_markers(text: String) -> String {
    hw_text::strip_wrapper_markers(&text)
}

/// Streaming-display sanitizer: content after the first start marker, markers stripped.
#[uniffi::export]
pub fn sanitize_streaming_buffer(buffer: String) -> String {
    hw_text::sanitize_streaming_buffer(&buffer)
}

/// Remove a single trailing period (preserves an ellipsis).
#[uniffi::export]
pub fn remove_trailing_period(text: String) -> String {
    hw_text::remove_trailing_period(&text)
}

/// Remove English filler words ("uh", "um", "er"); other languages untouched.
#[uniffi::export]
pub fn remove_filler_words(text: String, language: Option<String>) -> String {
    hw_text::remove_filler_words(&text, language.as_deref())
}

/// Replace the spoken "new line" command with a paragraph break.
#[uniffi::export]
pub fn process_voice_commands(text: String) -> String {
    hw_text::process_voice_commands(&text)
}

/// Final cleanup before saving a completed streaming session.
#[uniffi::export]
pub fn finalize_streaming_text(text: String) -> String {
    hw_text::finalize_streaming_text(&text)
}

// ===========================================================================
// KeyValueStore foreign trait (platform-implemented persistence).
//
// `hw-license` needs native persistence: Rust holds the license/usage logic but
// the platform owns where bytes live (UserDefaults / Keychain on Apple,
// Credential Manager / JSON on Windows). UniFFI models this as a *foreign trait*
// (callback interface) — the platform implements it, Rust calls back. The
// `ffi_license` module's `KvAdapter` bridges this to the leaf crate's plain
// `hw_license::KeyValueStore` trait. (Validated end-to-end via a binding-gen
// spike before the license wrappers were written.)
// ===========================================================================

/// Native key-value persistence implemented by the platform (callback
/// interface). Keys/values are plain strings; `get` returns `None` for a missing
/// key. Mirrors `hw_license::KeyValueStore`.
#[uniffi::export(with_foreign)]
pub trait KeyValueStore: Send + Sync {
    fn get(&self, key: String) -> Option<String>;
    fn set(&self, key: String, value: String);
    fn delete(&self, key: String);
}
