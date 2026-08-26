//! HyperWhisper Cloud's own `/ws/streaming-deepgram` relay — the DEFAULT
//! provider, and the only one that bills.
//!
//! Two consequences of it being ours:
//!
//! - **`credits_used` is billing data.** It arrives once, on `session_complete`,
//!   after the stop frame. Everything about the stop path below exists to not
//!   lose it.
//! - **The host is configurable.** `LiveConfig::base_url` points a build at a
//!   different backend; macOS's `#if DEBUG` build talks to staging. A hardcoded
//!   production host here would silently re-point a debug build at production
//!   and bill real credits against a developer's key.
//!
//! Auth is a query parameter, not a header, because a browser websocket client
//! cannot set headers — the same reason Deepgram uses a subprotocol.

use super::config::{
    bool_field, num_field, str_field, text_field, LiveConfig, LiveConnect, LiveError, LiveEvent,
    Query, StopStep,
};
use super::AudioFraming;
use crate::helpers::keyword_boost_terms;

/// The production relay. Matches Windows' `StreamingEndpoint` and
/// `shared-dotnet`'s literal.
const DEFAULT_BASE_URL: &str = "wss://transcribe-prod-v2.hyperwhisper.com";
const PATH: &str = "/ws/streaming-deepgram";

/// How long to wait for `session_complete` after sending `stop`.
///
/// Ten seconds, and a wait on the *event*, so a prompt completion returns
/// immediately. macOS instead hard-closes 500 ms after `stop` and therefore
/// loses a late `session_complete` — with the `credits_used` it carries. The
/// .NET behaviour wins: dropping billing data to save a few hundred
/// milliseconds on the rare slow completion is not a trade worth making, and
/// the wait is on an event so the common case costs nothing.
const SESSION_COMPLETE_TIMEOUT_MS: u64 = 10_000;

/// Vocabulary terms go out as one comma-joined `vocabulary=` parameter.
const MAX_TERMS: usize = 100;

pub(super) fn connect(config: &LiveConfig) -> Result<LiveConnect, LiveError> {
    let mut query = Query::default();
    // License key first, device id as the trial fallback, and neither is a
    // hard failure — the relay has nothing to charge against.
    if let Some(license_key) = super::config::present(&config.license_key) {
        query.push("license_key", license_key);
    } else if let Some(device_id) = super::config::present(&config.device_id) {
        query.push("device_id", device_id);
    } else {
        return Err(LiveError::MissingCredential);
    }

    // Vocabulary is gated on an explicit language for the same reason it is on
    // Deepgram: this endpoint relays to Deepgram, which ignores keyterms under
    // auto-detect.
    if let Some(language) = super::normalize_language(config.language.as_deref()) {
        query.push("language", &language);
        let terms = keyword_boost_terms(&config.vocabulary, Some(MAX_TERMS));
        if !terms.is_empty() {
            query.push("vocabulary", &terms.join(", "));
        }
    }

    Ok(LiveConnect {
        url: format!("{}{PATH}{}", base_url(config), query.suffix()),
        // No headers. The client-identity headers the heads add
        // (`X-HyperWhisper-Platform`, `X-HyperWhisper-Version`) are the
        // platform's to know, not the core's — see `LiveConnect::headers`.
        headers: Vec::new(),
        subprotocols: Vec::new(),
        sample_rate: super::required_sample_rate(super::LiveProvider::HyperWhisperCloud),
        framing: AudioFraming::Binary,
        start_frames: Vec::new(),
        session_starts_on_open: false,
    })
}

/// The configured base URL with an HTTP scheme mapped to its websocket
/// equivalent, trailing slash removed.
///
/// The heads store one backend URL and use it for both the REST and the
/// websocket path, so `https://…` is the shape that arrives here. macOS does
/// this same substitution inline.
fn base_url(config: &LiveConfig) -> String {
    let raw = match super::config::present(&config.base_url) {
        Some(raw) => raw.trim(),
        None => return DEFAULT_BASE_URL.to_string(),
    };
    let trimmed = raw.trim_end_matches('/');
    if let Some(rest) = trimmed.strip_prefix("https://") {
        format!("wss://{rest}")
    } else if let Some(rest) = trimmed.strip_prefix("http://") {
        format!("ws://{rest}")
    } else {
        trimmed.to_string()
    }
}

pub(super) fn stop_sequence() -> Vec<StopStep> {
    vec![
        StopStep::SendText {
            text: r#"{"type":"stop"}"#.to_string(),
        },
        StopStep::WaitForSessionComplete {
            timeout_ms: SESSION_COMPLETE_TIMEOUT_MS,
        },
        StopStep::Close,
    ]
}

pub(super) fn parse(root: &serde_json::Value) -> LiveEvent {
    match str_field(root, "type") {
        Some("ready") => LiveEvent::SessionStarted {
            session_id: str_field(root, "sessionId").map(str::to_string),
        },
        Some("transcript") => match text_field(root, "text") {
            Some(text) if bool_field(root, "is_final") == Some(true) => LiveEvent::FinalTranscript {
                text: text.to_string(),
            },
            Some(text) => LiveEvent::PartialTranscript {
                text: text.to_string(),
            },
            None => LiveEvent::Ignore,
        },
        Some("session_complete") => LiveEvent::SessionComplete {
            duration_seconds: num_field(root, "duration_seconds"),
            credits_used: num_field(root, "credits_used"),
        },
        Some("error") => LiveEvent::Error {
            message: super::config::error_message(root, "Unknown server error"),
        },
        // The only provider that warns. `remaining_seconds` rides along on the
        // wire and no head has ever read it, so it is not carried here.
        Some("warning") => LiveEvent::Warning {
            message: text_field(root, "message")
                .unwrap_or("Server warning")
                .to_string(),
        },
        _ => LiveEvent::Ignore,
    }
}
