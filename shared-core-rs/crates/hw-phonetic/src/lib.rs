//! `hw-phonetic` — Beider-Morse encoding, and the vocabulary passes above it.
//!
//! The encoder ([`encode`]) was always shared: `BeiderMorse.swift` and
//! `BeiderMorse.cs` are both thin wrappers over it. The ~90 lines of policy that
//! sat on top of it were not — `PhoneticVocabularyMatcher.swift` and
//! `PhoneticVocabularyMatcher.cs` were the same program written twice, and Linux
//! had neither. Issue #283 moves that policy here.
//!
//! Two entry points, and they are deliberately different rule sets:
//!
//! * [`apply_vocabulary`] — the phonetic matcher. Whole-word, `\b`-anchored,
//!   case-insensitive, NOT diacritic-insensitive. It corrects a misrecognition
//!   ("wisper" → "Whisper") towards a vocabulary row that carries no
//!   replacement.
//! * [`apply_substring_vocabulary`] — the local providers' pass. Unanchored
//!   substring, case-insensitive AND diacritic-insensitive, over the rows that
//!   DO carry a replacement.
//!
//! Keeping both here, next to each other, is the point: macOS already put the
//! same two rule sets side by side in `VocabularyProcessor.swift` (commit
//! 136071d) after finding four byte-identical private copies of the second one,
//! for exactly this reason.
//!
//! # Panic-free by construction
//!
//! `indexing_slicing`, `unwrap_used` and `expect_used` are denied for this
//! package (see `Cargo.toml`), and the crate root re-allows them under
//! `cfg(test)`. The workspace release profile sets `panic = "abort"`: a panic in
//! here is not a failed vocabulary pass, it is the app dying mid-transcription.
//! The inputs are the user's own transcript and the user's own vocabulary rows,
//! which is a weaker threat model than the appcast feed `hw-releasenotes`
//! parses — but [`substring`] does byte-offset arithmetic over a folded copy of
//! the transcript, which is precisely the kind of code that gets an index wrong.
//! A cargo-fuzz target covers both entry points; see `fuzz/`.

#![cfg_attr(
    test,
    allow(clippy::indexing_slicing, clippy::unwrap_used, clippy::expect_used)
)]

mod encode;
mod fold;
mod matcher;
mod substring;

pub use encode::encode;
pub use matcher::{apply_vocabulary, AppliedMatch, PhoneticApplyResult, VocabularyEntry};
pub use substring::apply_substring_vocabulary;
