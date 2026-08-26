//! xAI Grok speech-to-text over `/v1/stt`.
//!
//! Two things separate this one from the rest:
//!
//! - **The language gate is a support filter, not a normalizer.** xAI enables
//!   inverse text normalization only for a fixed set of 25 codes, so an
//!   unsupported selection means *omit the parameter*, not send it and hope.
//!   That filter already exists in this crate for the batch endpoint
//!   ([`crate::providers::grok::supported_formatting_language`]) and is reused
//!   verbatim — the C# strategy's own comment already cites it as the reference.
//! - **`transcript.done` can carry the last words.** It is both a final
//!   transcript and the end of the session, which is why
//!   [`LiveEvent::FinalTranscriptAndSessionComplete`] exists at all. A client
//!   that treated the completion as a plain `SessionComplete` would drop the
//!   trailing text; that is the arm's whole reason for being.

use super::config::{
    bool_field, commit_delta, str_field, text_field, LiveConfig, LiveConnect, LiveError, LiveEvent,
    Query, StopStep,
};
use super::session::SessionState;
use super::AudioFraming;

const ENDPOINT: &str = "wss://api.x.ai/v1/stt";

/// How long to wait for `transcript.done` after asking for it.
///
/// A wait on an *event*, not a duration: the whole point is to return the
/// instant the completion lands, and only to give up after ten seconds. A flat
/// ten-second drain would add ten seconds to every single stop.
const SESSION_COMPLETE_TIMEOUT_MS: u64 = 10_000;

pub(super) fn connect(config: &LiveConfig) -> Result<LiveConnect, LiveError> {
    let api_key = super::config::present(&config.api_key).ok_or(LiveError::MissingCredential)?;

    let mut query = Query::default();
    query.push_literal("sample_rate=16000");
    query.push_literal("encoding=pcm");
    query.push_literal("interim_results=true");
    query.push_literal("endpointing=300");
    if let Some(language) =
        crate::providers::grok::supported_formatting_language(config.language.as_deref())
    {
        query.push("language", &language);
    }
    // No language gate on the terms, unlike Deepgram: xAI applies `keyterm`
    // under auto-detect too. `keyterms` is the batch path's builder — the same
    // 100-term / 50-character vendor caps, applied to the same shared,
    // sanitized, de-duplicated term list.
    for term in crate::providers::grok::keyterms(&config.vocabulary) {
        query.push("keyterm", &term);
    }

    Ok(LiveConnect {
        url: format!("{ENDPOINT}{}", query.suffix()),
        headers: vec![crate::contract::Header::new(
            "Authorization",
            format!("Bearer {api_key}"),
        )],
        subprotocols: Vec::new(),
        sample_rate: super::required_sample_rate(super::LiveProvider::Grok),
        framing: AudioFraming::Binary,
        start_frames: Vec::new(),
        session_starts_on_open: false,
    })
}

pub(super) fn stop_sequence() -> Vec<StopStep> {
    vec![
        StopStep::SendText {
            text: r#"{"type":"audio.done"}"#.to_string(),
        },
        StopStep::WaitForSessionComplete {
            timeout_ms: SESSION_COMPLETE_TIMEOUT_MS,
        },
        StopStep::Close,
    ]
}

pub(super) fn parse(state: &mut SessionState, root: &serde_json::Value) -> LiveEvent {
    match str_field(root, "type") {
        Some("transcript.created") => LiveEvent::SessionStarted { session_id: None },
        Some("error") => LiveEvent::Error {
            message: super::config::error_message(root, "xAI streaming transcription failed"),
        },
        Some("transcript.partial") => parse_partial(state, root),
        Some("transcript.done") => parse_done(state, root),
        _ => LiveEvent::Ignore,
    }
}

/// xAI re-sends the whole transcript so far on every `is_final` frame, so a
/// final is emitted as the delta against what was already committed. The
/// non-final case is emitted whole: a partial replaces the previous partial.
fn parse_partial(state: &mut SessionState, root: &serde_json::Value) -> LiveEvent {
    let Some(text) = text_field(root, "text") else {
        return LiveEvent::Ignore;
    };
    if bool_field(root, "is_final") != Some(true) {
        return LiveEvent::PartialTranscript {
            text: text.to_string(),
        };
    }
    match commit_delta(&mut state.committed_transcript, text) {
        Some(delta) => LiveEvent::FinalTranscript { text: delta },
        None => LiveEvent::Ignore,
    }
}

fn parse_done(state: &mut SessionState, root: &serde_json::Value) -> LiveEvent {
    let duration_seconds = super::config::num_field(root, "duration");
    // xAI bills through its own account, not ours, so there is no credit figure
    // on this wire. Zero is the honest answer, not a placeholder.
    let credits_used = 0.0;
    let delta = text_field(root, "text")
        .and_then(|text| commit_delta(&mut state.committed_transcript, text));
    match delta {
        Some(text) => LiveEvent::FinalTranscriptAndSessionComplete {
            text,
            duration_seconds,
            credits_used,
        },
        None => LiveEvent::SessionComplete {
            duration_seconds,
            credits_used,
        },
    }
}
