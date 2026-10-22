//! Live: `BidiGenerateContent` frame builders (pure Rust, not exposed over
//! UniFFI). See the parent module's docs for why the live config path differs.

use super::{transcription_config, TranscriptionExtras, LIVE_AUDIO_MIME, LIVE_MODEL, LIVE_WS_ROOT};
use crate::contract::TranscriptionError;

/// The live WebSocket URL. The live API authenticates with a `?key=` query
/// parameter — the `x-goog-api-key` header form of the REST endpoint is not
/// available on a browser-style WebSocket handshake.
pub fn live_ws_url(api_key: &str, base_url: Option<&str>) -> String {
    let root = base_url
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .unwrap_or(LIVE_WS_ROOT)
        .trim_end_matches('/');
    format!("{root}?key={api_key}")
}

/// Build the live **setup** frame.
///
/// TRAP 3: the transcription config goes at `setup.input_audio_transcription`.
/// The pre-recorded location (`setup.generation_config.transcription_config`)
/// makes the server close the socket with code 1007 — verified live, with the
/// reason `Unknown name "transcription_config" at 'setup.generation_config'`.
///
/// The *contents* of the object overlap with the pre-recorded one, which is why
/// both go through [`transcription_config`]. The live object is the **narrower**
/// of the two: it takes `language_codes` and `custom_vocabulary` only, and
/// rejects `timestamp_granularities` with a 1007 close ("Cannot find field").
/// That is why this builder hard-codes [`TranscriptionExtras::none`] rather than
/// letting a caller pass extras through — on this path there is no version of
/// them that works, vocabulary present or not.
pub fn build_live_setup_frame(
    model: &str,
    language: Option<&str>,
    vocabulary: &[String],
) -> String {
    let model = {
        let t = model.trim();
        let t = if t.is_empty() { LIVE_MODEL } else { t };
        if t.starts_with("models/") {
            t.to_string()
        } else {
            format!("models/{t}")
        }
    };
    let frame = serde_json::json!({
        "setup": {
            "model": model,
            "input_audio_transcription":
                transcription_config(language, vocabulary, &TranscriptionExtras::none()),
        }
    });
    frame.to_string()
}

/// Build a live **audio** frame from audio the caller has already base64-encoded
/// (the native streaming strategies encode as they read from the capture ring,
/// so they hold the string, not the bytes).
pub fn build_live_audio_frame_base64(pcm_base64: &str) -> String {
    serde_json::json!({
        "realtime_input": {
            "audio": {
                "data": pcm_base64,
                "mime_type": LIVE_AUDIO_MIME,
            }
        }
    })
    .to_string()
}

/// Build the live **end-of-stream** frame.
pub fn build_live_audio_stream_end_frame() -> String {
    serde_json::json!({ "realtime_input": { "audio_stream_end": true } }).to_string()
}

/// A normalized event decoded from one live server frame.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum LiveServerMessage {
    /// `setupComplete` — the socket is ready for audio. Audio sent before this
    /// arrives must be buffered.
    SetupComplete,
    /// An in-progress hypothesis (`serverContent.interimInputTranscription`).
    PartialTranscript { text: String },
    /// A committed segment (`serverContent.inputTranscription`).
    FinalTranscript { text: String },
    /// ONE frame carrying a committed segment AND the turn's completion —
    /// `serverContent.inputTranscription` together with `generationComplete` /
    /// `turnComplete`.
    ///
    /// Reported separately from [`Self::FinalTranscript`] because dropping the
    /// completion half is not free: after the client has sent
    /// `audio_stream_end` this frame IS the answer its stop wait is blocked on,
    /// and a decoder that reports only the text leaves that wait to burn its
    /// whole budget and then report a stop failure. It is still a TURN
    /// completion and not a session completion — which of the two it is remains
    /// the client's call, exactly as for [`Self::Complete`].
    FinalTranscriptAndComplete { text: String },
    /// `serverContent.generationComplete` / `turnComplete`.
    Complete,
    /// A `goAway` / error payload from the server.
    Error { message: String },
    /// A frame this client does not model (keep-alives, usage updates).
    Unhandled,
}

/// Decode one live server frame.
pub fn parse_live_server_message(frame: &str) -> Result<LiveServerMessage, TranscriptionError> {
    let json: serde_json::Value =
        serde_json::from_str(frame).map_err(|e| TranscriptionError::Parse {
            message: format!("failed to decode live server frame: {e}"),
        })?;

    if json.get("setupComplete").is_some() || json.get("setup_complete").is_some() {
        return Ok(LiveServerMessage::SetupComplete);
    }

    if let Some(err) = json
        .get("error")
        .and_then(|e| e.get("message"))
        .and_then(|m| m.as_str())
    {
        return Ok(LiveServerMessage::Error {
            message: err.to_string(),
        });
    }

    let content = json
        .get("serverContent")
        .or_else(|| json.get("server_content"));
    if let Some(content) = content {
        // An OR over all four spellings, NOT first-present-wins. The `.or_else`
        // chain this replaces stopped at the first key that was PRESENT, so a
        // frame carrying `"generationComplete": false` alongside a true
        // `turnComplete` read as "not complete" and the completion was dropped
        // — the same stall as the combined-frame bug below, with a different
        // trigger. The cloud relay's decoder
        // (`hyperwhisper-cloud/src/routes/ws-streaming-gemini-transcribe.ts`)
        // has always been a genuine OR; this is the edit that stops the two
        // decoders disagreeing about that shape. Pinned by the
        // `a-false-generation-complete-does-not-mask-a-true-turn-complete`
        // conformance vector.
        let complete = [
            "generationComplete",
            "generation_complete",
            "turnComplete",
            "turn_complete",
        ]
        .iter()
        .any(|key| {
            content
                .get(*key)
                .and_then(serde_json::Value::as_bool)
                .unwrap_or(false)
        });

        // FINAL FIRST, deliberately. One `serverContent` frame may carry BOTH
        // `inputTranscription` and `interimInputTranscription`; checking the
        // interim first would emit only the preview and drop the committed
        // segment on the floor, so the user's text would end at the last
        // hypothesis. Windows' and macOS' streaming strategies and the shared
        // .NET client all check final first — this must match them.
        //
        // And when the completion rides along on that same frame, BOTH halves
        // are reported. Google answers the stop's `audio_stream_end` with one
        // frame carrying the last committed segment and `generationComplete`
        // together; returning the text alone loses the only completion the
        // stop's `WaitForSessionComplete` will ever be sent.
        if let Some(text) = transcription_text(content, "inputTranscription")
            .or_else(|| transcription_text(content, "input_transcription"))
        {
            return Ok(if complete {
                LiveServerMessage::FinalTranscriptAndComplete { text }
            } else {
                LiveServerMessage::FinalTranscript { text }
            });
        }
        // An interim carries no committed text, so there is no half worth
        // pairing a completion with, and the event vocabulary has no
        // "partial + complete" shape to report it in. The preview is reported
        // on its own, as it always has been.
        if let Some(text) = transcription_text(content, "interimInputTranscription")
            .or_else(|| transcription_text(content, "interim_input_transcription"))
        {
            return Ok(LiveServerMessage::PartialTranscript { text });
        }
        if complete {
            return Ok(LiveServerMessage::Complete);
        }
    }

    if let Some(reason) = json.get("goAway") {
        return Ok(LiveServerMessage::Error {
            message: format!("server going away: {reason}"),
        });
    }

    Ok(LiveServerMessage::Unhandled)
}

fn transcription_text(content: &serde_json::Value, key: &str) -> Option<String> {
    content
        .get(key)?
        .get("text")
        .and_then(|t| t.as_str())
        .map(str::to_string)
}
