//! ElevenLabs Scribe v2 realtime.
//!
//! The simplest of the five: no start frames, no control frames, no stop frames
//! and no session state. Every knob is a query parameter set at connect time,
//! including the commit strategy — `commit_strategy=vad` hands segmentation to
//! the server, which is why this protocol never sends a commit of its own the
//! way OpenAI's does.
//!
//! It is also the only provider whose error frames carry no wording, just a
//! `message_type`. The three user-facing sentences below are therefore ours,
//! not the vendor's, and are the strings the heads have shipped.

use super::config::{
    text_field, LiveConfig, LiveConnect, LiveError, LiveErrorKind, LiveEvent, Query, StopStep,
};
use super::AudioFraming;

const ENDPOINT: &str = "wss://api.elevenlabs.io/v1/speech-to-text/realtime";

/// Only one of the three shipped error strings ever diverged.
/// `quota_exceeded` and `rate_limited` are character-identical on macOS and
/// Windows, so they move here unchanged.
///
/// `AUTH_ERROR` is the one that did, and neither shipped spelling survives.
/// macOS said "…check your API key in **Settings**" and Windows named a
/// Windows-only settings page; this string is read by three heads that do not
/// agree on where a key lives. Windows' streaming settings page only *reports*
/// whether a key is configured — the field is on the separate API-keys page —
/// and the `shared-dotnet` head has no settings screen at all. So the sentence
/// names the ACTION and no screen. "authentication failed" is load-bearing: it
/// is the [`super::TERMINAL_ERROR_MARKERS`] entry that makes this frame stop a
/// reconnect, since ElevenLabs sends no wording of its own to match on.
const AUTH_ERROR: &str =
    "ElevenLabs authentication failed. Check that your ElevenLabs API key is correct and still active.";
const QUOTA_EXCEEDED: &str = "ElevenLabs quota exceeded. Please check your account billing.";
const RATE_LIMITED: &str = "ElevenLabs rate limit reached. Please try again in a moment.";

pub(super) fn connect(config: &LiveConfig) -> Result<LiveConnect, LiveError> {
    let api_key = super::config::present(&config.api_key).ok_or(LiveError::MissingCredential)?;

    let mut query = Query::default();
    query.push_literal("model_id=scribe_v2_realtime");
    query.push_literal("audio_format=pcm_16000");
    query.push_literal("commit_strategy=vad");
    query.push_literal("vad_silence_threshold_secs=1.5");
    query.push_literal("vad_threshold=0.4");
    if let Some(language) = super::normalize_language(config.language.as_deref()) {
        query.push("language_code", &language);
    }

    Ok(LiveConnect {
        url: format!("{ENDPOINT}{}", query.suffix()),
        headers: vec![crate::contract::Header::new("xi-api-key", api_key)],
        subprotocols: Vec::new(),
        sample_rate: super::required_sample_rate(super::LiveProvider::ElevenLabs),
        framing: AudioFraming::Base64Json {
            prefix: r#"{"message_type":"input_audio_chunk","audio_base_64":""#.to_string(),
            suffix: r#"","commit":false,"sample_rate":16000}"#.to_string(),
        },
        start_frames: Vec::new(),
        session_starts_on_open: false,
    })
}

/// Close, and nothing else.
///
/// `commit_strategy=vad` means the server has already committed everything it
/// intends to; there is no flush frame to send and nothing to drain for. macOS
/// and Windows both close immediately. `shared-dotnet` waits 1 s first with no
/// frame to wait for, which only delays every stop by a second.
pub(super) fn stop_sequence() -> Vec<StopStep> {
    vec![StopStep::Close]
}

pub(super) fn parse(root: &serde_json::Value) -> LiveEvent {
    let text = text_field(root, "text");
    match super::config::str_field(root, "message_type") {
        Some("session_started") => LiveEvent::SessionStarted { session_id: None },
        Some("partial_transcript") => match text {
            Some(text) => LiveEvent::PartialTranscript {
                text: text.to_string(),
            },
            None => LiveEvent::Ignore,
        },
        Some("committed_transcript") => match text {
            Some(text) => LiveEvent::FinalTranscript {
                text: text.to_string(),
            },
            None => LiveEvent::Ignore,
        },
        // The kind rides along with the wording. The three sentences are OURS —
        // the frame carries none — so a head that read them back to decide on a
        // reconnect would be reading the core's own prose. See
        // [`LiveErrorKind`] for the reconnect that cost.
        Some("auth_error") => LiveEvent::Error {
            message: AUTH_ERROR.to_string(),
            kind: Some(LiveErrorKind::Unauthorized),
        },
        Some("quota_exceeded") => LiveEvent::Error {
            message: QUOTA_EXCEEDED.to_string(),
            kind: Some(LiveErrorKind::QuotaExceeded),
        },
        Some("rate_limited") => LiveEvent::Error {
            message: RATE_LIMITED.to_string(),
            kind: Some(LiveErrorKind::RateLimited),
        },
        _ => LiveEvent::Ignore,
    }
}
