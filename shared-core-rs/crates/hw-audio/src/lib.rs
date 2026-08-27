//! `hw-audio` — shared audio-signal logic for every HyperWhisper head.
//!
//! Milestone: the privacy-safe no-speech diagnostic (issue #291). The same dBFS
//! measurement and the same "is this worth reporting to Sentry" decision were
//! written twice — `TranscriptionDiagnosticsService.cs` (Windows) and
//! `TranscriptionDiagnosticsService.swift` (macOS) — and had already drifted:
//! Windows classifies on five arms, macOS on four, and the two round their
//! measurements with different tie-breaks. Comparing the two platforms in Sentry
//! is the whole point of the diagnostic, so the arithmetic has to be one
//! implementation.
//!
//! The crate is deliberately dependency-free and UniFFI-free. `hw-core`'s
//! `ffi_audio` module mirrors these types as UniFFI records/enums, the same way
//! `ffi_input` mirrors `hw-input`.
//!
//! What stays in the head: decoding the file, the per-sample loop, the container
//! metadata, the Sentry message and the fingerprint *root* (those are
//! deliberately platform-distinct — merging them would merge macOS events into
//! Windows' live issues).

pub mod no_speech;

pub use no_speech::*;
