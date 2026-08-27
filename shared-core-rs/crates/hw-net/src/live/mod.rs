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
//! The terminal-error policy shipped on macOS only. The `shared-dotnet` head
//! had no equivalent, so a mid-session "Credit balance exhausted" from
//! HyperWhisper Cloud — the default provider — still drove the doomed reconnect
//! fan-out that macOS specifically fixed (HYPERWHISPER-MH → -MG → -RW). Moving
//! the policy here gives that head the fix as a side effect of not writing it
//! again.
//!
//! Windows is the head that does NOT take it, on purpose:
//! `StreamingTranscriptionClient` ends the session on every provider error
//! frame, terminal or not, so it has no fan-out to suppress and classifying
//! would only loosen what it already does. It is a caller of
//! [`is_terminal_close_code`] and [`upgrade_refusal`], and deliberately not of
//! [`classify_error_message`].
//!
//! ## What stays native, deliberately
//!
//! - **Transport.** Reconnect state machines, backoff, socket lifecycle and
//!   timeouts stay in each head. Sans-I/O means Rust builds frames and reads
//!   messages, full stop.
//! - **Provider-specific close codes.** [`is_terminal_close_code`] is the
//!   RFC-6455 set and nothing else. A provider's own codes are a layer on top,
//!   and they stay in each head. macOS is the head that has such codes today —
//!   HyperWhisper Cloud's 4001/4002, special-cased inline in
//!   `StreamingTranscriptionClient` — and it does **not** also consult this
//!   function; nothing in Swift calls it. Only the two .NET heads do.
//! - **What a terminal outcome *does*.** The classifiers say what a message or
//!   status means. Suppressing a reconnect, marking a close expected and
//!   choosing whether to raise a Sentry issue are client decisions with three
//!   different shapes on three platforms.
//!
//! # The five protocols
//!
//! [`LiveSession`] is the module's one stateful type. It takes a [`LiveConfig`]
//! and answers values: a [`LiveConnect`] descriptor once, [`LiveFrame`]s to
//! send, [`LiveEvent`]s parsed out of what arrived, and an ordered
//! [`StopStep`] list to end on. The five protocol modules behind it
//! (`deepgram`, `elevenlabs`, `openai`, `xai`, `hw_cloud`) are the port of
//! fifteen shipped implementations — five wire protocols written three times.
//!
//! ## Audio never crosses this boundary
//!
//! [`crate::contract`] states the rule for the batch path: Rust never handles
//! samples, and a request references audio by path. The live path keeps it with
//! two devices.
//!
//! - [`LiveSession::note_audio`] takes a **byte count**. Only OpenAI's commit
//!   gate needs to know how much audio is outstanding, and a number answers it.
//! - [`AudioFraming`] is returned once, at connect time, and says how to wrap a
//!   chunk: either raw binary, or `prefix + base64(pcm) + suffix`. Both JSON
//!   framers among the five are a single mid-string substitution into a fixed
//!   envelope, so two literals describe them exactly. The base64 and the
//!   concatenation happen natively, on bytes the platform already holds.
//!
//! One consequence is worth naming because it changes wire bytes on the .NET
//! heads. They build those frames with `System.Text.Json`, whose default
//! encoder escapes a plus sign to its six-character `+` form — and the
//! plus sign is in the base64 alphabet, so it is in most chunks. A
//! `prefix + base64 + suffix` concatenation emits the `+` literally, as macOS's
//! string interpolation already does. Both are the same JSON string value and no
//! provider can tell them apart; the frame is shorter and one fewer allocation.
//!
//! ## Time is a parameter
//!
//! Nothing here reads a clock. [`LiveSession::control_frames`] and
//! [`LiveSession::stop_sequence`] take `now_ms` from the caller. That is what
//! makes OpenAI's 1.2 s commit interval and Deepgram's 3 s keepalive testable
//! without sleeping — the property `OpenAIStreamingCommitGateTests.swift` buys
//! on macOS by injecting a clock, available here for free.
//!
//! ## Resolved divergences
//!
//! Fifteen implementations of five protocols had drifted. Collapsing them forces
//! a winner for each; these are the eight that changed observable behaviour on
//! at least one head.
//!
//! | Decision | Winner | Loser |
//! |---|---|---|
//! | Deepgram query parameters | the thirteen the .NET heads send | macOS's ten — no `filler_words`, `utterance_end_ms`, `vad_events` |
//! | Deepgram auto-detect | `detect_language=true`, which BOTH .NET heads ship — and which Deepgram does not support on streaming (see below) | macOS omits it, leaving the account default |
//! | HyperWhisper Cloud stop | `WaitForSessionComplete(10 s)` | macOS's hard close 500 ms after `stop`, which loses `credits_used` |
//! | OpenAI commit floor | re-derived from `100 ms × 24 kHz × 2 bytes` | `shared-dotnet`'s hardcoded `4800` with the derivation lost |
//! | Vocabulary caps | one shared normalizer + xAI's vendor limit | four different caps for one concept |
//! | Deepgram model alias | resolved, and never emits a bare `model=` | Linux sends `config.Model` verbatim |
//! | ElevenLabs `auth_error` wording | a third sentence that names no screen | both shipped ones name a settings surface the other heads do not have |
//! | ElevenLabs `auth_error`/`quota_exceeded`/`rate_limited` codes | carried across the FFI as a `kind` | collapsing them to one code cost `rate_limited` its "no reconnect" verdict on Linux |
//!
//! Five of them need more than a row.
//!
//! **Deepgram auto-detect is not a win — it is a shipped bug, preserved.** The
//! table row records which behaviour this module reproduces, not which one is
//! correct. `shared-app-classification/cloud-stt-catalog.json` states the
//! vendor's rule under Deepgram's `languages.autoDetectCaveat`:
//! *"Pre-recorded only — `detect_language=true` is NOT supported on streaming.
//! For streaming multilingual use `language=multi`."* So on the auto-detect path
//! neither the .NET spelling nor macOS's omission detects anything; both leave
//! the account default. `detect_language=true` is kept here only because both
//! .NET heads shipped it and issue #281 is a move, not a behaviour change — a
//! port that silently changed the wire would make any regression unattributable.
//! **NOT COVERED, and needs a follow-up issue:** sending `language=multi`
//! instead. That is a real behaviour change on all three heads, it interacts
//! with the `keyterm` gate below (Deepgram ignores keyterms in multilingual
//! mode, so the gate would have to stay), and it needs a live call to verify.
//!
//! **The vocabulary cap.** One concept had four policies. It is now one:
//! [`crate::helpers::keyword_boost_terms`] sanitizes (strips `<`/`>`, collapses
//! whitespace runs, truncates to 80 characters), drops empties and de-duplicates
//! case-insensitively; each protocol then takes at most 100 terms. The shipped
//! *100-character* filters on Deepgram and HyperWhisper Cloud are not
//! reproduced, because they are unreachable — the sanitizer's own 80-character
//! ceiling has already run. xAI's 50-character drop **is** reproduced: that one
//! is a documented vendor limit (docs.x.ai), and it is applied by the same
//! [`crate::providers::grok::keyterms`] the batch path uses, so the two paths
//! cannot drift. The filter runs before the count cap, never after — capping
//! first would let a run of over-long terms silently shorten the result, which
//! is the trap `LiveTranscriptionProtocolFactory.Vocabulary` documents.
//!
//! **The ElevenLabs wording.** Only `auth_error` diverged; `quota_exceeded` and
//! `rate_limited` were character-identical and move unchanged. The two
//! `auth_error` sentences differ in where they send the user, and a shared core
//! cannot name a platform's own UI: Windows' "the Model Library API keys
//! manager" is a Windows surface, and macOS's "check your API key in Settings"
//! dead-ends on the other two — Windows' streaming settings page only *reports*
//! whether a key is configured (the field is on the API-keys page) and the
//! `shared-dotnet` head has no settings screen at all. So neither shipped
//! spelling survives: the sentence in [`elevenlabs`] names the action and no
//! screen. This is the one place the module does not pick a shipped winner, and
//! it is a user-visible string change on all three heads.
//!
//! **The ElevenLabs error kinds.** ElevenLabs is the only provider whose error
//! frames carry a machine-readable kind and no wording, which means the three
//! sentences above are ours and [`classify_error_message`] reading them back is
//! the core classifying its own prose. It gets one of the three wrong for a head
//! that decides on a failure code: "rate limit reached" matches no terminal
//! marker, so `rate_limited` reads transient — right for macOS, whose client
//! backs off and retries, wrong for `shared-dotnet`, which shipped a
//! `RateLimited` code and refused a reconnect. So [`LiveErrorKind`] rides along
//! with the message and each head keeps its own verdict. The four providers that
//! send real wording carry `None` and change nothing.
//!
//! **The stop shape.** There is no `drain_timeout_ms` anywhere in this module,
//! and that is deliberate. The ordered [`StopStep`] list carries every wait —
//! including the two event waits a flat duration cannot express — so a separate
//! drain timeout would be a second source of truth for one behaviour. Windows
//! already runs an ordered step list and bounds nothing else; the other two
//! heads gain the ordering.

mod capabilities;
mod config;
mod deepgram;
mod elevenlabs;
mod gemini;
mod hw_cloud;
mod language;
mod openai;
mod policy;
mod session;
mod xai;

#[cfg(test)]
mod tests;

pub use capabilities::{
    complete_ends_session_before_stop, provider_label, required_sample_rate, start_timeout_ms,
    supports_vocabulary, supports_vocabulary_without_language,
};
pub use config::{
    AudioFraming, LiveConfig, LiveConnect, LiveError, LiveErrorKind, LiveEvent, LiveFrame, StopStep,
};
pub use language::{language_tag, normalize_language};
pub use policy::{
    classify_error_message, is_terminal_close_code, upgrade_refusal, LiveErrorOutcome,
    LiveUpgradeRefusal, TERMINAL_ERROR_MARKERS,
};
pub use session::LiveSession;

/// The six websocket transcription providers.
///
/// Local engines (Parakeet, Nemotron) are deliberately absent: they are not
/// websocket protocols and share none of this. The arm names match
/// `shared-dotnet`'s `LiveTranscriptionProvider`, and `Grok` matches
/// [`crate::contract::Provider::Grok`] — Windows spells the same vendor `Xai`
/// in its own type and maps across.
///
/// [`LiveProvider::GeminiTranscribe`] is the BYOK Gemini 3.5 Transcribe Live
/// socket (issue #331) and is distinct from the same vendor reached through
/// [`LiveProvider::HyperWhisperCloud`]'s `geminiTranscribe` tier: the former
/// talks to Google with the user's own key, the latter to our relay with a
/// license key, and only the latter bills credits.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum LiveProvider {
    Deepgram,
    ElevenLabs,
    OpenAi,
    Grok,
    GeminiTranscribe,
    HyperWhisperCloud,
}

impl LiveProvider {
    /// Every arm, for exhaustive tests and for the FFI round-trip guard.
    pub const ALL: [LiveProvider; 6] = [
        LiveProvider::Deepgram,
        LiveProvider::ElevenLabs,
        LiveProvider::OpenAi,
        LiveProvider::Grok,
        LiveProvider::GeminiTranscribe,
        LiveProvider::HyperWhisperCloud,
    ];
}
