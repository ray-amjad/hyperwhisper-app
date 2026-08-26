//! Deepgram's realtime `/v1/listen` websocket.
//!
//! Wire notes worth keeping:
//!
//! - **The API key travels as a websocket subprotocol**, not a header:
//!   `Sec-WebSocket-Protocol: token, <key>`. Deepgram documents this because
//!   browser websocket clients cannot set request headers. The four other
//!   providers use headers or the query string.
//! - **The session is live at handshake.** Deepgram's only session-shaped frame
//!   (`Metadata`) does not arrive until after audio has been sent, so a client
//!   that waited for it before sending would deadlock. This is the one provider
//!   with [`LiveConnect::session_starts_on_open`] set.
//! - **`channel` is overloaded.** On `Results` it is an object; on
//!   `SpeechStarted` / `UtteranceEnd` it is an array of channel indices. A
//!   strict decode of the object shape throws on those frames and makes the
//!   metadata arm unreachable, which is exactly the bug the macOS strategy
//!   documents at `DeepgramStreamingStrategy.swift:392-405`. Reading through
//!   `serde_json::Value` makes the polymorphism a non-event.

use super::config::{
    bool_field, str_field, text_field, LiveConfig, LiveConnect, LiveError, LiveEvent, Query,
    StopStep,
};
use super::session::SessionState;
use super::{AudioFraming, LiveFrame};
use crate::helpers::keyword_boost_terms;

const ENDPOINT: &str = "wss://api.deepgram.com/v1/listen";

/// Idle time after which the socket needs a `KeepAlive` or Deepgram closes it.
const KEEPALIVE_AFTER_MS: u64 = 3_000;

/// Gap between `Finalize` and `CloseStream` on the stop path.
///
/// The Windows ordering, and it wins over `shared-dotnet`'s (which sends both
/// frames back to back and then drains for 2 s). `Finalize` asks Deepgram to
/// flush its buffered audio into one last `Results` frame; `CloseStream` tells
/// it no more audio is coming. Sent together, the close can be processed before
/// the flush completes and the finalized tail is lost.
const FINALIZE_DRAIN_MS: u64 = 500;

/// Vocabulary terms are `keyterm=` repeated, at most this many.
const MAX_KEYTERMS: usize = 100;

pub(super) fn connect(config: &LiveConfig) -> Result<LiveConnect, LiveError> {
    let api_key = super::config::present(&config.api_key).ok_or(LiveError::MissingCredential)?;

    let mut query = Query::default();
    // The thirteen constant parameters, in the order the two .NET heads send
    // them. macOS sends ten of these — it has never sent `filler_words`,
    // `utterance_end_ms` or `vad_events` — and the thirteen win: see the
    // "resolved divergences" table in the module docs.
    query.push(
        "model",
        &crate::providers::deepgram::resolve_model(config.model.as_deref().unwrap_or_default()),
    );
    query.push_literal("encoding=linear16");
    query.push_literal("sample_rate=16000");
    query.push_literal("channels=1");
    query.push_literal("smart_format=true");
    query.push_literal("punctuate=true");
    query.push_literal("filler_words=true");
    query.push_literal(if config.fast_formatting {
        "no_delay=true"
    } else {
        "no_delay=false"
    });
    query.push_literal("endpointing=300");
    query.push_literal("utterance_end_ms=1500");
    query.push_literal("interim_results=true");
    query.push_literal("vad_events=true");
    query.push_literal("mip_opt_out=true");

    // The caller's tag verbatim, NOT the primary subtag: `zh` and `zh-TW` are
    // different Deepgram codes (as are `en`/`en-GB`, `pt`/`pt-BR`, `zh-Hans`/
    // `zh-Hant`), and both shipped strategies sent what the picker stored. See
    // [`super::language`] for the per-provider split.
    match super::language_tag(config.language.as_deref()) {
        Some(language) => {
            query.push("language", &language);
            // Deepgram ignores `keyterm` under auto-detect, so the terms are
            // dropped rather than sent to be discarded. All three heads already
            // gate on the language this way.
            for term in keyword_boost_terms(&config.vocabulary, Some(MAX_KEYTERMS)) {
                query.push("keyterm", &term);
            }
        }
        // Auto-detect is spelled with a parameter here, not by omitting one.
        // macOS omits it, which leaves Deepgram on its account default rather
        // than detecting; the .NET wording wins.
        None => query.push_literal("detect_language=true"),
    }

    Ok(LiveConnect {
        url: format!("{ENDPOINT}{}", query.suffix()),
        headers: Vec::new(),
        subprotocols: vec!["token".to_string(), api_key.to_string()],
        sample_rate: super::required_sample_rate(super::LiveProvider::Deepgram),
        framing: AudioFraming::Binary,
        start_frames: Vec::new(),
        session_starts_on_open: true,
    })
}

pub(super) fn control_frames(state: &mut SessionState, now_ms: u64) -> Vec<LiveFrame> {
    let idle = match state.last_audio_ms {
        Some(last) => now_ms.saturating_sub(last) > KEEPALIVE_AFTER_MS,
        None => false,
    };
    state.last_audio_ms = Some(now_ms);
    if idle {
        vec![LiveFrame::text(r#"{"type":"KeepAlive"}"#)]
    } else {
        Vec::new()
    }
}

pub(super) fn stop_sequence() -> Vec<StopStep> {
    vec![
        StopStep::SendText {
            text: r#"{"type":"Finalize"}"#.to_string(),
        },
        StopStep::Wait {
            ms: FINALIZE_DRAIN_MS,
        },
        StopStep::SendText {
            text: r#"{"type":"CloseStream"}"#.to_string(),
        },
        StopStep::Close,
    ]
}

pub(super) fn parse(root: &serde_json::Value, raw: &str) -> LiveEvent {
    match str_field(root, "type") {
        // `Metadata` is the session acknowledgement, and `request_id` is what
        // Deepgram support asks for. It is NOT the `Metadata` event arm — the
        // two frames that map there are below.
        Some("Metadata") => LiveEvent::SessionStarted {
            session_id: str_field(root, "request_id").map(str::to_string),
        },
        Some("Results") => parse_results(root),
        // Voice-activity frames. Log-only on every head, but carried as an
        // event rather than dropped: they are what a "why did it not finalize"
        // investigation reads.
        Some("UtteranceEnd") | Some("SpeechStarted") => LiveEvent::Metadata {
            raw: raw.to_string(),
        },
        _ => LiveEvent::Ignore,
    }
}

fn parse_results(root: &serde_json::Value) -> LiveEvent {
    let Some(text) = root
        .get("channel")
        .filter(|c| c.is_object())
        .and_then(|c| c.get("alternatives"))
        .and_then(serde_json::Value::as_array)
        .and_then(|a| a.first())
        .and_then(|a| text_field(a, "transcript"))
    else {
        return LiveEvent::Ignore;
    };
    if bool_field(root, "is_final") == Some(true) {
        LiveEvent::FinalTranscript {
            text: text.to_string(),
        }
    } else {
        LiveEvent::PartialTranscript {
            text: text.to_string(),
        }
    }
}
