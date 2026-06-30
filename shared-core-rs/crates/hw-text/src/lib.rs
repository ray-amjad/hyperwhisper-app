//! `hw-text` — pure, zero-I/O text logic shared across platforms.
//!
//! Every function here is string-in → string-out (no platform state, no I/O).
//! The platform probes (cursor context, language selection, vocabulary list) stay
//! native and feed plain data in. Re-exported through `hw-core` for FFI.
//!
//! Milestone 1 scope: autocapitalize, smart spacing, text processing, vocab
//! replacement primitive. The prompt builder (`build_system_prompt` /
//! `build_system_info`) is M1b — it needs platform runtime values (time, locale,
//! host) passed in and has its own cross-platform divergences.

mod autocapitalize;
pub mod prompt;
mod smart_spacing;
mod text_processing;
mod vocab;

pub use autocapitalize::{apply_autocapitalize, CursorContext};
pub use prompt::{
    build_system_info, build_system_prompt, sanitize_vocabulary_word, AppType, EnglishSpelling,
    Preset, PromptContext,
};
pub use smart_spacing::{append_trailing_space, contains_cjk};
pub use text_processing::{
    extract_cleaned_from_wrapped, finalize_streaming_text, process_voice_commands,
    remove_filler_words, remove_trailing_period, sanitize_streaming_buffer,
    strip_wrapper_markers,
};
pub use vocab::apply_hardened_replacement;
