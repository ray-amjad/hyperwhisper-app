//! OpenAI Realtime, transcription intent.
//!
//! The only provider with a two-sided commit protocol: audio is *appended* to a
//! server-side buffer and then explicitly *committed*, and a commit that covers
//! too little audio is rejected outright. Everything stateful in this module
//! exists to get that one decision right.
//!
//! It is also the only provider that names its transcripts. `item_id` groups
//! deltas and completions, so both accumulators are keyed by it.

use super::config::{str_field, text_field, LiveConfig, LiveConnect, LiveError, LiveEvent, StopStep};
use super::session::SessionState;
use super::{AudioFraming, LiveFrame};

const ENDPOINT: &str = "wss://api.openai.com/v1/realtime?intent=transcription";
const MODEL: &str = "gpt-realtime-whisper";
const COMMIT_FRAME: &str = r#"{"type":"input_audio_buffer.commit"}"#;

/// Gap between periodic commits.
///
/// Not configurable: every test drives the periodic path by moving `now_ms`,
/// which clears this constant just as well, so a per-instance knob would only
/// be a way for a caller to get it wrong — zero would commit-storm the socket.
const COMMIT_INTERVAL_MS: u64 = 1_200;

/// Minimum amount of appended audio a commit frame is allowed to cover.
///
/// OpenAI Realtime rejects `input_audio_buffer.commit` with "buffer too small.
/// Expected at least 100ms of audio" when less than 100 ms has been appended
/// since the previous commit (HYPERWHISPER-S8 / HYPERWHISPER-S9). This is the
/// server's rule EXACTLY — no safety margin.
///
/// A margin would be actively harmful here. [`SessionState::pending_audio_bytes`]
/// is an exact running sum of appended PCM bytes, so there is no imprecision for
/// a margin to absorb; all a stricter threshold buys is a dead band in which the
/// client silently discards a tail the server would have accepted. That dead
/// band is not hypothetical: Windows' `StreamingAudioCapture` sets
/// `CaptureBufferMilliseconds = 100`, so every capture chunk is exactly 4800
/// bytes at 24 kHz — exactly one chunk, exactly on the line. And
/// `turn_detection` is null in the session update below, so there is no
/// server-side VAD auto-commit to rescue a dropped tail.
const MIN_COMMIT_MS: u64 = 100;

/// 16-bit mono PCM.
const BYTES_PER_SAMPLE: u64 = 2;

/// [`MIN_COMMIT_MS`] expressed in bytes at the provider's own sample rate:
/// exactly 4800 at 24 kHz, which is the server's floor.
///
/// Derived, not written down. `shared-dotnet` hardcodes `MinimumCommitBytes =
/// 4800` and the derivation is lost with it — so a future sample-rate change
/// would leave a stale constant that is wrong by exactly the ratio, and wrong
/// in the direction that drops audio. Integer arithmetic throughout, so the
/// boundary lands on 4800 and not on a float that truncates to 4799.
fn min_commit_bytes() -> u64 {
    u64::from(super::required_sample_rate(super::LiveProvider::OpenAi))
        * BYTES_PER_SAMPLE
        * MIN_COMMIT_MS
        / 1000
}

pub(super) fn connect(config: &LiveConfig) -> Result<LiveConnect, LiveError> {
    let api_key = super::config::present(&config.api_key).ok_or(LiveError::MissingCredential)?;
    let sample_rate = super::required_sample_rate(super::LiveProvider::OpenAi);

    // Built by hand rather than through `serde_json::json!`, which sorts keys:
    // this reproduces the two .NET heads' `JsonSerializer` output byte for
    // byte, so a head that swaps its own frame for this one changes nothing on
    // the wire. `turn_detection: null` is load-bearing — it disables
    // server-side VAD, which is what makes the commit gate above ours to get
    // right.
    let language = match super::normalize_language(config.language.as_deref()) {
        Some(language) => format!(r#","language":"{}""#, json_escape(&language)),
        None => String::new(),
    };
    let start_frame = format!(
        concat!(
            r#"{{"type":"session.update","session":{{"type":"transcription","audio":"#,
            r#"{{"input":{{"format":{{"type":"audio/pcm","rate":{rate}}},"#,
            r#""transcription":{{"model":"{model}"{language}}},"turn_detection":null}}}}}}}}"#
        ),
        rate = sample_rate,
        model = MODEL,
        language = language,
    );

    Ok(LiveConnect {
        url: ENDPOINT.to_string(),
        headers: vec![crate::contract::Header::new(
            "Authorization",
            format!("Bearer {api_key}"),
        )],
        subprotocols: Vec::new(),
        sample_rate,
        framing: AudioFraming::Base64Json {
            prefix: r#"{"type":"input_audio_buffer.append","audio":""#.to_string(),
            suffix: r#""}"#.to_string(),
        },
        start_frames: vec![LiveFrame::text(start_frame)],
        session_starts_on_open: false,
    })
}

pub(super) fn control_frames(state: &mut SessionState, now_ms: u64) -> Vec<LiveFrame> {
    let Some(last) = state.last_commit_ms else {
        // First opportunity of the session: seed the clock mark the shipped
        // strategies set from their constructor, and commit nothing.
        state.last_commit_ms = Some(now_ms);
        return Vec::new();
    };
    if now_ms.saturating_sub(last) < COMMIT_INTERVAL_MS {
        return Vec::new();
    }
    // Deliberately leaves `last_commit_ms` stale when the byte gate is not met:
    // that is what makes the commit fire on the next chunk that clears it,
    // rather than waiting out another full interval. Nothing is lost — the
    // bytes stay pending.
    if !claim_committable(state) {
        return Vec::new();
    }
    state.last_commit_ms = Some(now_ms);
    vec![LiveFrame::text(COMMIT_FRAME)]
}

pub(super) fn stop_sequence(state: &mut SessionState, now_ms: u64) -> Vec<StopStep> {
    let mut steps = Vec::with_capacity(3);

    // COMMIT ONLY WHAT THE SERVER WILL ACCEPT. A stop that lands shortly after
    // a periodic commit leaves a tail of under 100 ms outstanding, and
    // committing that is rejected outright ("buffer too small") — which the
    // client surfaces as a spurious streaming-error toast. Dropping a tail that
    // short is the accepted trade: it is silence-or-a-syllable, and it was lost
    // to the rejection anyway.
    if claim_committable(state) {
        state.last_commit_ms = Some(now_ms);
        steps.push(StopStep::SendText {
            text: COMMIT_FRAME.to_string(),
        });
    }

    // KEEP THE WAIT EVEN WHEN NOTHING WAS COMMITTED. The receive loop is still
    // live at this point, and the `…transcription.completed` for the LAST
    // PERIODIC commit can still be in flight — exactly the timing window the
    // bug lives in. Closing immediately would trade the toast for a truncated
    // transcript.
    steps.push(StopStep::Wait { ms: 1_000 });
    steps.push(StopStep::Close);
    steps
}

/// Claim the accumulated audio for a commit frame: true, and zero the counter,
/// only once at least [`min_commit_bytes`] has accumulated — the server's "at
/// least 100 ms" rule, so exactly 100 ms qualifies.
///
/// Check and reset are one operation so the periodic path and the stop sequence
/// can never both claim the same bytes and emit two commits for one buffer.
fn claim_committable(state: &mut SessionState) -> bool {
    if state.pending_audio_bytes < min_commit_bytes() {
        return false;
    }
    state.pending_audio_bytes = 0;
    true
}

pub(super) fn parse(state: &mut SessionState, root: &serde_json::Value) -> LiveEvent {
    match str_field(root, "type") {
        Some("session.updated") => LiveEvent::SessionStarted {
            session_id: root
                .get("session")
                .and_then(|s| str_field(s, "id"))
                .map(str::to_string),
        },
        Some("error") => LiveEvent::Error {
            message: super::config::error_message(root, "OpenAI Realtime transcription failed"),
        },
        Some("conversation.item.input_audio_transcription.delta") => parse_delta(state, root),
        Some("conversation.item.input_audio_transcription.completed") => {
            parse_completed(state, root)
        }
        _ => LiveEvent::Ignore,
    }
}

/// Deltas arrive as fragments and are accumulated per item, because the client
/// contract for a partial is "the whole interim utterance", not "what changed".
///
/// A frame with no `item_id` is emitted on its own rather than dropped: it is
/// still the newest text the user has spoken, and the alternative is a partial
/// that never appears.
fn parse_delta(state: &mut SessionState, root: &serde_json::Value) -> LiveEvent {
    let Some(delta) = text_field(root, "delta") else {
        return LiveEvent::Ignore;
    };
    let Some(item) = str_field(root, "item_id").filter(|id| !id.is_empty()) else {
        return LiveEvent::PartialTranscript {
            text: delta.to_string(),
        };
    };
    let accumulated = state.partial_items.entry(item.to_string()).or_default();
    accumulated.push_str(delta);
    LiveEvent::PartialTranscript {
        text: accumulated.clone(),
    }
}

/// The per-item prefix delta.
///
/// Not [`super::config::commit_delta`]: that one is keyed on a single running
/// transcript and treats a shrinking transcript as a retraction to hold onto.
/// OpenAI re-sends the *same item* with more text, so the previous value must be
/// overwritten unconditionally, and two items in one session are unrelated
/// strings rather than a continuation. Merging the two would either lose xAI's
/// retraction handling or make OpenAI's items bleed into each other.
fn parse_completed(state: &mut SessionState, root: &serde_json::Value) -> LiveEvent {
    let Some(transcript) = text_field(root, "transcript").map(str::trim) else {
        return LiveEvent::Ignore;
    };
    let Some(item) = str_field(root, "item_id").filter(|id| !id.is_empty()) else {
        return LiveEvent::Ignore;
    };
    let previous = state
        .committed_items
        .insert(item.to_string(), transcript.to_string());
    state.partial_items.remove(item);

    let delta = match previous {
        Some(previous) if !previous.is_empty() => match transcript.strip_prefix(previous.as_str()) {
            Some(suffix) => suffix.trim(),
            None => transcript,
        },
        _ => transcript,
    };
    if delta.is_empty() {
        LiveEvent::Ignore
    } else {
        LiveEvent::FinalTranscript {
            text: delta.to_string(),
        }
    }
}

/// Escape a string for a JSON string literal.
///
/// Only reachable for the language code, which the normalizer has already
/// reduced to a lowercase primary subtag — but the frame is assembled by
/// `format!` rather than a serializer, so nothing else would catch a control
/// character if the picker ever supplied one.
fn json_escape(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if (c as u32) < 0x20 => out.push_str(&format!("\\u{:04x}", c as u32)),
            c => out.push(c),
        }
    }
    out
}
