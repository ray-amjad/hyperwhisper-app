//! Sans-I/O logic for the five live-streaming websocket providers.
//!
//! Five wire protocols are implemented three times in this repo — the macOS
//! strategies under `Streaming/Providers/`, the Windows `*StreamingStrategy.cs`
//! set, and `shared-dotnet`'s `LiveTranscriptionProtocols.cs`. Issue #281
//! collapses them into one Rust module. This first slice owns the parts that
//! need no session state at all: the terminal-error policy, the close-code
//! table, language normalization and the per-provider capability table.
//!
//! The shape matches [`crate::providers::llm`] and the STT builders: Rust
//! answers a *value* — an outcome, a sample rate, a label — and the platform
//! decides what to do with it.
//!
//! ## Why this slice first
//!
//! The terminal-error policy shipped on macOS only. Windows and Linux had no
//! equivalent, so a mid-session "Credit balance exhausted" from HyperWhisper
//! Cloud — the default provider — still drove the doomed reconnect fan-out that
//! macOS specifically fixed (HYPERWHISPER-MH → -MG → -RW). Moving the policy
//! here gives the other two heads the fix as a side effect of not writing it
//! again.
//!
//! ## What stays native, deliberately
//!
//! - **Transport.** Reconnect state machines, backoff, socket lifecycle and
//!   timeouts stay in each head. Sans-I/O means Rust builds frames and reads
//!   messages, full stop.
//! - **Provider-specific close codes.** [`is_terminal_close_code`] is the
//!   RFC-6455 set and nothing else. macOS's `StreamingTranscriptionClient`
//!   additionally special-cases HyperWhisper Cloud's own 4001/4002 inline; that
//!   is a provider extension layered on top, not a replacement, and it stays
//!   where it is.
//! - **What a terminal outcome *does*.** The classifiers say what a message or
//!   status means. Suppressing a reconnect, marking a close expected and
//!   choosing whether to raise a Sentry issue are client decisions with three
//!   different shapes on three platforms.

mod capabilities;
mod language;
mod policy;

#[cfg(test)]
mod tests;

pub use capabilities::{provider_label, required_sample_rate, supports_vocabulary};
pub use language::normalize_language;
pub use policy::{
    classify_error_message, is_terminal_close_code, upgrade_refusal, LiveErrorOutcome,
    LiveUpgradeRefusal, TERMINAL_ERROR_MARKERS,
};

/// The five websocket transcription providers.
///
/// Local engines (Parakeet, Nemotron) are deliberately absent: they are not
/// websocket protocols and share none of this. The arm names match
/// `shared-dotnet`'s `LiveTranscriptionProvider`, and `Grok` matches
/// [`crate::contract::Provider::Grok`] — Windows spells the same vendor `Xai`
/// in its own type and maps across.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum LiveProvider {
    Deepgram,
    ElevenLabs,
    OpenAi,
    Grok,
    HyperWhisperCloud,
}

impl LiveProvider {
    /// Every arm, for exhaustive tests and for the FFI round-trip guard.
    pub const ALL: [LiveProvider; 5] = [
        LiveProvider::Deepgram,
        LiveProvider::ElevenLabs,
        LiveProvider::OpenAi,
        LiveProvider::Grok,
        LiveProvider::HyperWhisperCloud,
    ];
}
