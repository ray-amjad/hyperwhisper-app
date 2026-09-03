//! Gemini 3.5 Transcribe Live (BYOK) over Google's `BidiGenerateContent`
//! websocket.
//!
//! Unlike the other five modules here, this one writes no wire bytes of its own.
//! Every frame and every parse already lives in
//! [`crate::providers::gemini_transcribe`], because the pre-recorded and the
//! live path share the `transcription_config` object and Google's two
//! incompatibility traps apply to both. Duplicating the builders here to match
//! the shape of `deepgram.rs` would re-create exactly the drift issue #281 set
//! out to remove — so this module is an **adapter**: it maps a [`LiveConfig`]
//! onto those builders and their [`LiveServerMessage`] back onto [`LiveEvent`].
//!
//! ## The three traps, and where they are enforced
//!
//! All three are enforced in `providers::gemini_transcribe`, not here, so the
//! REST path cannot drift from the socket:
//!
//! 1. **Auth is a query parameter.** Google rejects the handshake outright if an
//!    `Authorization` header is present, so `connect` sets no headers at all.
//! 2. **`custom_vocabulary` cannot be combined** with `diarization_mode` or
//!    `timestamp_granularities`. The live setup frame hard-codes
//!    `TranscriptionExtras::none`, so on this path the conflict cannot arise.
//! 3. **The live transcription config sits at
//!    `setup.input_audio_transcription`**, not at the pre-recorded
//!    `setup.generation_config.transcription_config`. The wrong position closes
//!    the socket with 1007 — the same object at two paths for two models of one
//!    family.
//!
//! ## Why this is Deepgram-shaped, not xAI-shaped
//!
//! The JSON framing resembles xAI's, and that resemblance is a trap.
//! `interimInputTranscription` is cumulative only *within a turn* and restarts
//! after each final, and `inputTranscription` carries only that turn's committed
//! text. So the interim is a replacement preview and the final is an append-me
//! delta — exactly Deepgram's shape. Running these frames through
//! [`super::config::commit_delta`], as [`super::xai`] does, would chop the head
//! off every utterance after the first, because xAI's transcript is cumulative
//! across the whole *session*. This module therefore keeps no transcript state,
//! and [`super::session::SessionState`] gains no field for it.
//!
//! ## `generationComplete` is a TURN boundary
//!
//! The one behaviour that separates this provider from every other one here.
//! Google emits `serverContent.generationComplete` each time it finishes
//! generating for an utterance, so a two-sentence dictation sees it mid-stream
//! with more audio still to come. Read as terminal it ends the session at the
//! first pause and the last utterance's final never arrives. The parse below
//! still answers [`LiveEvent::SessionComplete`] — it is a faithful reading of
//! the frame — and what a client does with it is decided by
//! [`super::complete_ends_session_before_stop`], which answers `false` for this
//! provider alone.
//!
//! It can also arrive on the SAME frame as the turn's committed text, which is
//! the shape Google answers `audio_stream_end` with. That frame parses to
//! [`LiveEvent::FinalTranscriptAndSessionComplete`] — both halves, because the
//! completion half is the only one a stop's `WaitForSessionComplete` is
//! listening for — and `complete_ends_session_before_stop` governs it exactly
//! as it governs the standalone completion above.

use super::config::{LiveConfig, LiveConnect, LiveError, LiveEvent, LiveFrame, StopStep};
use super::AudioFraming;
use crate::providers::gemini_transcribe as gt;

/// How long to wait for a completion after `audio_stream_end`.
///
/// Five seconds, not the ten the other event-waiting providers use, and shorter
/// on purpose. Google does **not** close the socket after `audio_stream_end` —
/// measured at 54 s of silence — so this wait is the whole stop budget rather
/// than a courtesy pause on an upstream close that is already coming. Waiting
/// longer buys nothing here.
const SESSION_COMPLETE_TIMEOUT_MS: u64 = 5_000;

/// A sentinel that survives JSON encoding unchanged, used to split the audio
/// envelope into the prefix/suffix pair [`AudioFraming::Base64Json`] wants.
///
/// Plain ASCII letters, so `serde_json` escapes nothing and the split lands
/// exactly where the base64 payload would. Deriving the two literals from the
/// real builder rather than writing them out again is what stops this envelope
/// drifting from [`gt::build_live_audio_frame_base64`]; `audio_framing_matches_the_builder`
/// in `live::tests` pins it.
const AUDIO_SENTINEL: &str = "PCMPAYLOADSENTINEL";

pub(super) fn connect(config: &LiveConfig) -> Result<LiveConnect, LiveError> {
    let api_key = super::config::present(&config.api_key).ok_or(LiveError::MissingCredential)?;

    Ok(LiveConnect {
        // `base_url` is deliberately not forwarded: it exists so a head can
        // point HyperWhisper Cloud at its own backend, and this endpoint is
        // Google's, not ours to move. See `LiveConfig::base_url`.
        url: gt::live_ws_url(api_key, None),
        // No headers. Google authenticates the live socket by query parameter
        // and rejects the handshake if an `Authorization` header is present.
        headers: Vec::new(),
        subprotocols: Vec::new(),
        sample_rate: super::required_sample_rate(super::LiveProvider::GeminiTranscribe),
        framing: audio_framing(),
        start_frames: vec![LiveFrame::text(gt::build_live_setup_frame(
            config.model.as_deref().unwrap_or(""),
            config.language.as_deref(),
            &config.vocabulary,
        ))],
        // Audio sent before `setupComplete` arrives is dropped by the server,
        // so the session is not live until it does.
        session_starts_on_open: false,
    })
}

/// The audio envelope, split out of the real builder around [`AUDIO_SENTINEL`].
fn audio_framing() -> AudioFraming {
    let envelope = gt::build_live_audio_frame_base64(AUDIO_SENTINEL);
    match envelope.split_once(AUDIO_SENTINEL) {
        Some((prefix, suffix)) => AudioFraming::Base64Json {
            prefix: prefix.to_string(),
            suffix: suffix.to_string(),
        },
        // Unreachable: the builder interpolates the sentinel verbatim. Falling
        // back to binary would silently send raw PCM to a JSON-only socket, so
        // an empty-envelope Base64Json is the less wrong of two impossible
        // branches and the test above is what keeps it impossible.
        None => AudioFraming::Base64Json {
            prefix: String::new(),
            suffix: String::new(),
        },
    }
}

pub(super) fn stop_sequence() -> Vec<StopStep> {
    vec![
        StopStep::SendText {
            text: gt::build_live_audio_stream_end_frame(),
        },
        StopStep::WaitForSessionComplete {
            timeout_ms: SESSION_COMPLETE_TIMEOUT_MS,
        },
        StopStep::Close,
    ]
}

/// The provider's own generic sentence, used when an error frame carries no
/// wording. Never empty — an error event with an empty message reaches a user as
/// a blank alert. It names no account state, so
/// [`super::classify_error_message`] reads it as transient and the reconnect
/// path is left alone, which is right for a frame nobody has decoded.
const ERROR_FALLBACK: &str = "Gemini streaming transcription failed";

pub(super) fn parse(root: &serde_json::Value, text: &str) -> LiveEvent {
    let Ok(message) = gt::parse_live_server_message(text) else {
        return LiveEvent::Ignore;
    };
    match message {
        gt::LiveServerMessage::SetupComplete => LiveEvent::SessionStarted { session_id: None },
        gt::LiveServerMessage::PartialTranscript { text } => LiveEvent::PartialTranscript { text },
        gt::LiveServerMessage::FinalTranscript { text } => LiveEvent::FinalTranscript { text },
        // A turn boundary before the client asks to stop, the end of the session
        // after it — see the module docs and
        // `complete_ends_session_before_stop`. Google reports no duration or
        // credit figures on this route, and a BYOK session is not metered by
        // HyperWhisper either way, so zero is the honest answer.
        gt::LiveServerMessage::Complete => LiveEvent::SessionComplete {
            duration_seconds: 0.0,
            credits_used: 0.0,
        },
        // The same turn boundary, riding on the frame that carries the turn's
        // committed text. Google answers `audio_stream_end` in exactly this
        // shape, so a client that reports only the text half is left waiting
        // out its whole `WaitForSessionComplete` budget for a completion that
        // has already been and gone.
        //
        // `complete_ends_session_before_stop` still decides whether it ends the
        // SESSION, and it decides it for this event as much as for
        // `SessionComplete` above: a client that answers `false` for this
        // provider must commit the text and keep the socket open until it has
        // asked to stop. Both .NET consumers already collapse the two events
        // onto one `Complete` kind and gate that, and
        // `StreamingTranscriptionClient` on both native heads gates this arm
        // the same way.
        gt::LiveServerMessage::FinalTranscriptAndComplete { text } => {
            LiveEvent::FinalTranscriptAndSessionComplete {
                text,
                duration_seconds: 0.0,
                credits_used: 0.0,
            }
        }
        gt::LiveServerMessage::Error { message } => LiveEvent::Error {
            message,
            kind: None,
        },
        // The builder only reports an error when the frame carries wording, so a
        // shaped-but-wordless `{"error":{}}` arrives here. It is still a
        // failure, and reporting it with the fallback beats swallowing it.
        gt::LiveServerMessage::Unhandled if root.get("error").is_some() => LiveEvent::Error {
            message: super::config::error_message(root, ERROR_FALLBACK),
            kind: None,
        },
        gt::LiveServerMessage::Unhandled => LiveEvent::Ignore,
    }
}
