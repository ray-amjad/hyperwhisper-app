//! `hw-phonetic` — Beider-Morse encoding, and the vocabulary matcher above it.
//!
//! The encoder ([`encode`]) was always shared: `BeiderMorse.swift` and
//! `BeiderMorse.cs` are both thin wrappers over it. The ~90 lines of policy that
//! sat on top of it were not — `PhoneticVocabularyMatcher.swift` and
//! `PhoneticVocabularyMatcher.cs` were the same program written twice, and Linux
//! had neither. Issue #283 moves that policy here, as [`apply_vocabulary`]:
//! whole-word, `\b`-anchored, case-insensitive, NOT diacritic-insensitive, over
//! the vocabulary rows that carry no replacement.
//!
//! # Panic-free by construction
//!
//! `indexing_slicing`, `unwrap_used` and `expect_used` are denied for this
//! package (see `Cargo.toml`), and the crate root re-allows them under
//! `cfg(test)`. The workspace release profile sets `panic = "abort"`: a panic in
//! here is not a failed vocabulary pass, it is the app dying mid-transcription.

#![cfg_attr(
    test,
    allow(clippy::indexing_slicing, clippy::unwrap_used, clippy::expect_used)
)]

mod encode;
mod matcher;

pub use encode::encode;
pub use matcher::{apply_vocabulary, AppliedMatch, PhoneticApplyResult, VocabularyEntry};
