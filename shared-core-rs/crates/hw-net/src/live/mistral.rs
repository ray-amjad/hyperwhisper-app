//! Mistral Voxtral Mini Transcribe Realtime 3 (BYOK) over
//! `wss://api.mistral.ai/v1/audio/transcriptions/realtime`.
//!
//! Wire shape, from Mistral's "Transcribe in realtime" page
//! (docs.mistral.ai/studio/audio/speech-to-text/transcribe-in-realtime):
//!
//! - **Connect** with `?model=<id>` and `Authorization: Bearer <key>`. The model
//!   is the only query parameter; every other setting rides in a frame.
//! - **Start**: one `session.update` carrying `audio_format`, `language` and
//!   `context_bias`. Settings lock when the first audio chunk arrives (a later
//!   `session.update` is an error event), which is why the frame is a start
//!   frame: it is on the wire before any audio can be.
//! - **Audio**: `{"type":"input_audio.append","audio":"<base64 PCM>"}`, 16-bit
//!   little-endian mono at 16 kHz (`pcm_s16le`, the server's own default rate).
//! - **Stop**: `{"type":"input_audio.end"}`; the server then sends its last
//!   deltas and `transcription.done`, whose `text` is the transcript of record.
//!
//! ## Deltas are append-only fragments, not utterances
//!
//! `transcription.text.delta` carries a fragment to append verbatim, with its
//! own spaces and punctuation ("a delta never corrects earlier text"), and a
//! fragment can be part of a word. [`LiveEvent::FinalTranscript`] means "a
//! committed segment", and every head joins committed segments with a space,
//! so committing fragments (or sentences cut from them) would rewrite the
//! whitespace between them: "Hello.\nWorld" would come out "Hello. World".
//!
//! So the session commits nothing mid-stream. It concatenates the fragments
//! as-is and shows the whole buffer as the partial; `transcription.done` is
//! the one final, and its `text` is what is committed, because Mistral's page
//! says to "save the text of transcription.done as the transcript of record".
//! The buffered deltas are the fallback only when `done` carries no text.
//!
//! ## Not verified against the live service
//!
//! This module is built from Mistral's published protocol and pinned by unit
//! tests; no head opens it yet and it has not been run against Mistral's socket.
//! The one shape most worth checking on the first live run: the page says to
//! wait for `session.created` before sending anything, and this module, like
//! OpenAI's, sends its `session.update` as a start frame the moment the socket
//! opens.

use super::config::{
    str_field, text_field, LiveConfig, LiveConnect, LiveError, LiveEvent, LiveFrame, StopStep,
};
use super::session::SessionState;
use super::AudioFraming;

const ENDPOINT: &str = "wss://api.mistral.ai/v1/audio/transcriptions/realtime";

/// The realtime model, by Mistral's documented alias for
/// `voxtral-mini-transcribe-realtime-3-0`.
pub const DEFAULT_MODEL: &str = "voxtral-mini-transcribe-realtime-3";

/// The PCM rate this module asks for. Mistral's default, and the rate four of
/// the other providers already capture at.
const SAMPLE_RATE: u32 = 16_000;

/// How long to wait for `transcription.done` after `input_audio.end`. An event
/// wait, like xAI's: it returns the moment the completion lands.
const SESSION_COMPLETE_TIMEOUT_MS: u64 = 10_000;

/// The same 100-term ceiling the batch `context_bias` field uses
/// ([`crate::providers::mistral::MAX_CONTEXT_BIAS_TERMS`]). Voxtral 3 states no
/// fixed count (entries share the context window with the audio), so keeping
/// the batch cap keeps one number for one concept.
const MAX_CONTEXT_BIAS_TERMS: usize = crate::providers::mistral::MAX_CONTEXT_BIAS_TERMS;

/// Used when an error frame carries no wording. Names no account state, so
/// [`super::classify_error_message`] reads it as transient.
const ERROR_FALLBACK: &str = "Mistral streaming transcription failed";

/// The model to connect with.
///
/// Only a Voxtral realtime id passes through. The heads fill
/// `LiveConfig::model` from a streaming-model setting that was Deepgram's, so
/// anything else (a Deepgram id, a batch Voxtral id, a blank) is the default,
/// for the same reason as [`super::elevenlabs::resolve_model`].
pub fn resolve_model(model: Option<&str>) -> String {
    match model.map(str::trim) {
        Some(id) if id.starts_with("voxtral-mini-transcribe-realtime-") => id.to_string(),
        _ => DEFAULT_MODEL.to_string(),
    }
}

/// The vocabulary as `context_bias` entries: the shared sanitizer and
/// de-duplication, capped at [`MAX_CONTEXT_BIAS_TERMS`]. Voxtral 3 accepts
/// spaces inside an entry, so a multi-word term is sent as written.
fn context_bias(vocabulary: &[String]) -> Vec<String> {
    crate::helpers::keyword_boost_terms(vocabulary, Some(MAX_CONTEXT_BIAS_TERMS))
}

/// The `session.update` start frame.
pub(super) fn session_update_frame(config: &LiveConfig) -> String {
    let mut session = serde_json::Map::new();
    session.insert(
        "audio_format".to_string(),
        serde_json::json!({ "encoding": "pcm_s16le", "sample_rate": SAMPLE_RATE }),
    );
    if let Some(language) = super::normalize_language(config.language.as_deref()) {
        session.insert("language".to_string(), serde_json::Value::String(language));
    }
    let bias = context_bias(&config.vocabulary);
    if !bias.is_empty() {
        session.insert("context_bias".to_string(), serde_json::json!(bias));
    }
    serde_json::json!({ "type": "session.update", "session": session }).to_string()
}

pub(super) fn connect(config: &LiveConfig) -> Result<LiveConnect, LiveError> {
    let api_key = super::config::present(&config.api_key).ok_or(LiveError::MissingCredential)?;

    let mut query = super::config::Query::default();
    query.push("model", &resolve_model(config.model.as_deref()));

    Ok(LiveConnect {
        url: format!("{ENDPOINT}{}", query.suffix()),
        headers: vec![crate::contract::Header::new(
            "Authorization",
            format!("Bearer {api_key}"),
        )],
        subprotocols: Vec::new(),
        sample_rate: super::required_sample_rate(super::LiveProvider::Mistral),
        framing: AudioFraming::Base64Json {
            prefix: r#"{"type":"input_audio.append","audio":""#.to_string(),
            suffix: r#""}"#.to_string(),
        },
        start_frames: vec![LiveFrame::text(session_update_frame(config))],
        session_starts_on_open: false,
    })
}

pub(super) fn stop_sequence() -> Vec<StopStep> {
    vec![
        StopStep::SendText {
            text: r#"{"type":"input_audio.end"}"#.to_string(),
        },
        StopStep::WaitForSessionComplete {
            timeout_ms: SESSION_COMPLETE_TIMEOUT_MS,
        },
        StopStep::Close,
    ]
}

pub(super) fn parse(state: &mut SessionState, root: &serde_json::Value) -> LiveEvent {
    match str_field(root, "type") {
        Some("session.created") => LiveEvent::SessionStarted {
            session_id: root
                .get("session")
                .and_then(|s| str_field(s, "id"))
                .map(str::to_string),
        },
        Some("transcription.text.delta") => parse_delta(state, root),
        Some("transcription.done") => parse_done(state, root),
        Some("error") => LiveEvent::Error {
            message: super::config::error_message(root, ERROR_FALLBACK),
            kind: None,
        },
        _ => LiveEvent::Ignore,
    }
}

fn parse_delta(state: &mut SessionState, root: &serde_json::Value) -> LiveEvent {
    let Some(fragment) = str_field(root, "text") else {
        return LiveEvent::Ignore;
    };
    if fragment.is_empty() {
        return LiveEvent::Ignore;
    }
    // Concatenated verbatim: the fragments carry their own separators.
    state.pending_delta.push_str(fragment);
    match state.pending_delta.trim() {
        "" => LiveEvent::Ignore,
        partial => LiveEvent::PartialTranscript {
            text: partial.to_string(),
        },
    }
}

fn parse_done(state: &mut SessionState, root: &serde_json::Value) -> LiveEvent {
    let duration_seconds = root
        .get("usage")
        .map(|usage| super::config::num_field(usage, "prompt_audio_seconds"))
        .unwrap_or(0.0);
    // A BYOK session bills the user's own Mistral account, not ours.
    let credits_used = 0.0;

    // The transcript of record wins over the buffered deltas, whitespace and
    // all; the buffer answers only when the record is missing or blank.
    let buffered = std::mem::take(&mut state.pending_delta);
    let tail = match text_field(root, "text").map(str::trim) {
        Some(record) if !record.is_empty() => record.to_string(),
        _ => buffered.trim().to_string(),
    };
    if tail.is_empty() {
        return LiveEvent::SessionComplete {
            duration_seconds,
            credits_used,
        };
    }
    state.committed_transcript.push_str(&tail);
    LiveEvent::FinalTranscriptAndSessionComplete {
        text: tail,
        duration_seconds,
        credits_used,
    }
}
