//! Gemini 3.5 Transcribe (sans-I/O) via Google's dedicated speech API,
//! `POST /v1beta/interactions`.
//!
//! ## Why this is a separate module from [`crate::providers::gemini`]
//!
//! **Do not fold this into `gemini.rs`, and do not route this model through
//! `:generateContent`.** `gemini.rs` is the multimodal path: Files API upload +
//! `models/{model}:generateContent`. That endpoint *accepts*
//! `gemini-3.5-transcribe`, *bills the audio*, and then returns a content part
//! with **empty text and no error** — a silent, paid no-op. Verified against the
//! live API. The transcribe models only work through `/v1beta/interactions`.
//!
//! The two also differ in auth (`x-goog-api-key` header + an `Api-Revision`
//! header, not `?key=`), in transport (inline base64, not a file reference), and
//! in BYOK key slot (`geminiTranscribeAPIKey`, not `geminiAPIKey`).
//!
//! ## Wire shape (pre-recorded)
//!
//! ```text
//! POST https://generativelanguage.googleapis.com/v1beta/interactions
//! x-goog-api-key: <key>
//! Api-Revision: 2026-05-20
//! Content-Type: application/json
//!
//! {"model":"gemini-3.5-transcribe",
//!  "input":[{"type":"audio","mime_type":"audio/mp3","data":"<base64>"}],
//!  "generation_config":{"transcription_config":{
//!     "language_codes":["en-US"],
//!     "custom_vocabulary":["HyperWhisper"]}}}
//! ```
//!
//! Transcript at `steps[0].content[0].text`; optional word timings at
//! `steps[0].content[0].annotations[]` (`"type":"word_info"`).
//!
//! ## Inline base64 and the FFI contract
//!
//! The endpoint has no file-reference form, so the audio must be inline. Rust
//! still never touches the bytes: the builder emits
//! [`Body::JsonWithBase64File`], which names the path and the two literal JSON
//! fragments that surround the base64. The platform reads and encodes the file
//! while writing the request body.
//!
//! ## The mutual-exclusion rule (TRAP 2)
//!
//! `custom_vocabulary` is rejected with HTTP 400 when it is sent alongside
//! either `diarization_mode` ("incompatible with diarization") or
//! `timestamp_granularities` ("incompatible with timestamps"). HyperWhisper is a
//! dictation app, so **vocabulary wins**. The rule is enforced in exactly one
//! place, [`transcription_config`], which every builder in this module (REST and
//! live) goes through — never at a call site.
//!
//! ## Live (`gemini-3.5-transcribe-live`)
//!
//! The live model speaks `BidiGenerateContent` over a WebSocket, and its
//! transcription config lives at a **different path**:
//! `setup.input_audio_transcription`, *not*
//! `setup.generation_config.transcription_config`. Sending the pre-recorded
//! shape closes the socket with 1007. The frame builders here are pure Rust and
//! are deliberately **not** exposed over UniFFI (no platform streaming strategy
//! builds frames across FFI, and per-chunk audio marshalling would add a hot-path
//! copy); `shared-conformance/live-frame-vectors.json` pins their shape so the
//! native clients can be checked against the same answers.

use crate::contract::{
    Body, Header, HttpMethod, HttpRequest, HttpResponse, TranscribeParams, Transcript,
    TranscriptionError,
};
use crate::helpers::{keyword_boost_terms, resolve_mime};
use crate::providers::common::retry_after;

/// Gemini API root. `params.base_url` overrides it (tests/staging).
pub const API_ROOT: &str = "https://generativelanguage.googleapis.com";

/// The `Api-Revision` header value the `/v1beta/interactions` shape is pinned
/// to. Google versions this endpoint by date; omitting it selects whatever the
/// current default revision is, which is not a stable contract.
pub const API_REVISION: &str = "2026-05-20";

/// Default pre-recorded model when the caller leaves `params.model` empty.
pub const DEFAULT_MODEL: &str = "gemini-3.5-transcribe";

/// The live (WebSocket) model id. It is **not** routable through
/// [`build_transcribe_request`] — see [`is_live_model`].
pub const LIVE_MODEL: &str = "gemini-3.5-transcribe-live";

/// Sample rate, in Hz, of the PCM the live socket expects. Mono, signed 16-bit,
/// little-endian.
pub const LIVE_SAMPLE_RATE: u32 = 16_000;

/// MIME the live socket expects for its audio frames.
pub const LIVE_AUDIO_MIME: &str = "audio/pcm;rate=16000";

/// The WebSocket endpoint for `BidiGenerateContent`.
pub const LIVE_WS_ROOT: &str = "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

/// Sentinel spliced into the serialized JSON body and then split back out, so
/// the prefix/suffix halves of [`Body::JsonWithBase64File`] are produced by
/// `serde_json` itself rather than by hand-written string concatenation — the
/// escaping of every *other* field is therefore serde's problem, not ours.
///
/// It must be characters serde emits verbatim (no control characters — those
/// come back as `` and would never match), and it must not occur in any
/// other field. The builder asserts it appears exactly once rather than trusting
/// that: a vocabulary term is arbitrary user text.
const BASE64_SENTINEL: &str = "HW-BASE64-AUDIO-PLACEHOLDER-b7f3a1c2";

fn root(params: &TranscribeParams) -> String {
    params
        .base_url
        .as_deref()
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .unwrap_or(API_ROOT)
        .trim_end_matches('/')
        .to_string()
}

fn model(params: &TranscribeParams) -> String {
    let t = params.model.trim();
    if t.is_empty() {
        DEFAULT_MODEL.to_string()
    } else {
        t.to_string()
    }
}

fn mime(params: &TranscribeParams) -> String {
    params
        .audio_mime
        .clone()
        .unwrap_or_else(|| resolve_mime(&params.audio_path))
}

/// Whether `model` names the live (WebSocket) variant. The REST builder refuses
/// it: `/v1beta/interactions` does not serve the live model, and letting it
/// through would produce a routable-but-unserved model id.
pub fn is_live_model(model: &str) -> bool {
    model.trim().eq_ignore_ascii_case(LIVE_MODEL)
}

/// The `language_codes` array, or `None` for auto-detect.
///
/// An empty or `auto` language means auto-detect, which the API expresses by
/// **omitting** the field (an empty array is also accepted, but omitting keeps
/// the body minimal and matches the verified request).
fn language_codes(language: Option<&str>) -> Option<Vec<String>> {
    language
        .map(str::trim)
        .filter(|t| !t.is_empty() && !t.eq_ignore_ascii_case("auto"))
        .map(|t| vec![t.to_string()])
}

/// Optional extras a caller may *request* from [`transcription_config`]. Both
/// are dropped when vocabulary terms are present (TRAP 2).
///
/// `TranscribeParams` carries no diarization or timestamp fields and this module
/// deliberately does not add any — the dictation paths always pass
/// [`TranscriptionExtras::none`]. The parameters exist so the exclusion rule has
/// a single, directly testable home rather than being an unwritten convention.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct TranscriptionExtras {
    /// `generation_config.transcription_config.diarization_mode`, e.g.
    /// `"speaker"`.
    pub diarization_mode: Option<String>,
    /// `generation_config.transcription_config.timestamp_granularities`, e.g.
    /// `["word"]`.
    pub timestamp_granularities: Vec<String>,
}

impl TranscriptionExtras {
    /// No diarization, no timestamps — what every HyperWhisper call site uses.
    pub fn none() -> Self {
        Self::default()
    }

    fn is_empty(&self) -> bool {
        self.diarization_mode.is_none() && self.timestamp_granularities.is_empty()
    }
}

/// Build the `transcription_config` object shared by the REST body and the live
/// setup frame.
///
/// **This is the single enforcement point for TRAP 2.** Google rejects
/// `custom_vocabulary` with HTTP 400 if it arrives together with
/// `diarization_mode` ("incompatible with diarization") or
/// `timestamp_granularities` ("incompatible with timestamps"). Rather than
/// letting each call site remember that, this function resolves the conflict
/// itself: when vocabulary terms survive normalization, they are sent and the
/// extras are dropped. A dictation app gets far more from correct spellings of
/// the user's own jargon than from speaker labels or word offsets.
///
/// Returns a `serde_json::Value` object, possibly empty (`{}`) when nothing is
/// configured.
pub fn transcription_config(
    language: Option<&str>,
    vocabulary: &[String],
    extras: &TranscriptionExtras,
) -> serde_json::Value {
    let mut config = serde_json::Map::new();

    if let Some(codes) = language_codes(language) {
        config.insert("language_codes".to_string(), serde_json::json!(codes));
    }

    let terms = keyword_boost_terms(vocabulary, None);
    if !terms.is_empty() {
        // TRAP 2: vocabulary wins. `extras` is discarded, not merged.
        config.insert("custom_vocabulary".to_string(), serde_json::json!(terms));
        return serde_json::Value::Object(config);
    }

    if !extras.is_empty() {
        if let Some(mode) = extras.diarization_mode.as_deref().map(str::trim) {
            if !mode.is_empty() {
                config.insert("diarization_mode".to_string(), serde_json::json!(mode));
            }
        }
        if !extras.timestamp_granularities.is_empty() {
            config.insert(
                "timestamp_granularities".to_string(),
                serde_json::json!(extras.timestamp_granularities),
            );
        }
    }

    serde_json::Value::Object(config)
}

// ---------------------------------------------------------------------------
// Pre-recorded: POST /v1beta/interactions
// ---------------------------------------------------------------------------

fn headers(params: &TranscribeParams) -> Vec<Header> {
    vec![
        Header::new("x-goog-api-key", params.api_key.clone()),
        Header::new("Api-Revision", API_REVISION),
        Header::new("Content-Type", "application/json"),
    ]
}

/// Build the pre-recorded transcription request.
///
/// The audio is inline base64 — see the module docs — so the body is a
/// [`Body::JsonWithBase64File`] and the platform performs the encode.
pub fn build_transcribe_request(
    params: &TranscribeParams,
) -> Result<HttpRequest, TranscriptionError> {
    let model = model(params);
    if is_live_model(&model) {
        return Err(TranscriptionError::BadRequest {
            status: 400,
            message: format!(
                "{LIVE_MODEL} is a WebSocket-only model and cannot be used for \
                 pre-recorded transcription"
            ),
        });
    }

    let config = transcription_config(
        params.language.as_deref(),
        &params.vocabulary,
        &TranscriptionExtras::none(),
    );

    let mut body = serde_json::json!({
        "model": model,
        "input": [{
            "type": "audio",
            "mime_type": mime(params),
            "data": BASE64_SENTINEL,
        }],
    });
    if let Some(obj) = config.as_object() {
        if !obj.is_empty() {
            body["generation_config"] = serde_json::json!({ "transcription_config": config });
        }
    }

    let encoded = serde_json::to_string(&body).map_err(|e| TranscriptionError::Parse {
        message: format!("failed to encode interactions body: {e}"),
    })?;
    if encoded.matches(BASE64_SENTINEL).count() != 1 {
        // Either serde escaped it (a bug here) or a user-supplied vocabulary
        // term happens to contain it. Splitting on an ambiguous marker would
        // corrupt the body, so refuse instead.
        return Err(TranscriptionError::Parse {
            message: "interactions body has an ambiguous audio placeholder".to_string(),
        });
    }
    let (prefix, suffix) =
        encoded
            .split_once(BASE64_SENTINEL)
            .ok_or_else(|| TranscriptionError::Parse {
                message: "interactions body lost its audio placeholder".to_string(),
            })?;

    Ok(HttpRequest {
        method: HttpMethod::Post,
        url: format!("{}/v1beta/interactions", root(params)),
        headers: headers(params),
        body: Body::JsonWithBase64File {
            prefix: prefix.as_bytes().to_vec(),
            path: params.audio_path.clone(),
            suffix: suffix.as_bytes().to_vec(),
        },
    })
}

/// Parse the `/v1beta/interactions` response into a [`Transcript`].
///
/// Concatenates the `text` of every content entry across every step (in
/// practice there is exactly one of each). Blank → [`TranscriptionError::NoSpeech`].
pub fn parse_transcribe_response(resp: &HttpResponse) -> Result<Transcript, TranscriptionError> {
    if !(200..=299).contains(&resp.status) {
        return Err(classify(resp));
    }

    let json: serde_json::Value =
        serde_json::from_str(&resp.text()).map_err(|e| TranscriptionError::Parse {
            message: format!("failed to decode interactions response: {e}"),
        })?;

    let text = collect_text(&json);
    if text.is_empty() {
        return Err(TranscriptionError::NoSpeech);
    }

    Ok(Transcript {
        text,
        ..Default::default()
    })
}

fn collect_text(json: &serde_json::Value) -> String {
    let mut out = String::new();
    let steps = json.get("steps").and_then(|s| s.as_array());
    for step in steps.into_iter().flatten() {
        let content = step.get("content").and_then(|c| c.as_array());
        for entry in content.into_iter().flatten() {
            if let Some(t) = entry.get("text").and_then(|t| t.as_str()) {
                out.push_str(t);
            }
        }
    }
    out.trim().to_string()
}

/// One `"type":"word_info"` annotation from
/// `steps[].content[].annotations[]`.
///
/// Only populated when the request asked for `timestamp_granularities`, which
/// HyperWhisper's dictation paths never do (see [`transcription_config`]); this
/// exists so the response shape is pinned and tested rather than rediscovered.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct WordInfo {
    pub text: String,
    /// Character offsets into the step's transcript text.
    pub start_index: Option<u64>,
    pub end_index: Option<u64>,
    /// Seconds from the start of the audio, decoded from the wire's
    /// `"1.800s"` **string** form.
    pub start_seconds: Option<f64>,
    pub end_seconds: Option<f64>,
    /// Speaker label as sent, e.g. `"spk:0"`.
    pub speaker: Option<String>,
}

/// Decode Google's duration strings (`"1.800s"`, `"0s"`) into seconds.
/// The wire form is a **string with a trailing `s`**, not a number — a plain
/// `as_f64()` on it yields `None`.
fn duration_seconds(value: Option<&serde_json::Value>) -> Option<f64> {
    let raw = value?.as_str()?.trim();
    raw.strip_suffix('s').unwrap_or(raw).parse::<f64>().ok()
}

/// Extract every `word_info` annotation from an interactions response, in wire
/// order.
pub fn parse_word_infos(resp: &HttpResponse) -> Result<Vec<WordInfo>, TranscriptionError> {
    let json: serde_json::Value =
        serde_json::from_str(&resp.text()).map_err(|e| TranscriptionError::Parse {
            message: format!("failed to decode interactions response: {e}"),
        })?;

    let mut out = Vec::new();
    let steps = json.get("steps").and_then(|s| s.as_array());
    for step in steps.into_iter().flatten() {
        let content = step.get("content").and_then(|c| c.as_array());
        for entry in content.into_iter().flatten() {
            let annotations = entry.get("annotations").and_then(|a| a.as_array());
            for a in annotations.into_iter().flatten() {
                if a.get("type").and_then(|t| t.as_str()) != Some("word_info") {
                    continue;
                }
                out.push(WordInfo {
                    text: a
                        .get("text")
                        .and_then(|t| t.as_str())
                        .unwrap_or_default()
                        .to_string(),
                    start_index: a.get("start_index").and_then(|v| v.as_u64()),
                    end_index: a.get("end_index").and_then(|v| v.as_u64()),
                    start_seconds: duration_seconds(a.get("start_offset")),
                    end_seconds: duration_seconds(a.get("end_offset")),
                    speaker: a
                        .get("speaker")
                        .and_then(|v| v.as_str())
                        .map(str::to_string),
                });
            }
        }
    }
    Ok(out)
}

/// Token counts reported by `usage.input_tokens_by_modality[]`.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct UsageTokens {
    pub audio_tokens: u64,
    pub text_tokens: u64,
}

/// Read `usage.input_tokens_by_modality[]` — a list of
/// `{modality: "audio"|"text", tokens: N}` entries, not a flat map.
///
/// `usage.total_output_tokens` is always `0` on this endpoint, so there is
/// nothing to read for output; callers that need an output figure estimate it
/// from the transcript length.
pub fn parse_usage(resp: &HttpResponse) -> UsageTokens {
    let Ok(json) = serde_json::from_str::<serde_json::Value>(&resp.text()) else {
        return UsageTokens::default();
    };
    let mut usage = UsageTokens::default();
    let entries = json
        .get("usage")
        .and_then(|u| u.get("input_tokens_by_modality"))
        .and_then(|m| m.as_array());
    for entry in entries.into_iter().flatten() {
        let tokens = entry.get("tokens").and_then(|t| t.as_u64()).unwrap_or(0);
        match entry.get("modality").and_then(|m| m.as_str()) {
            Some("audio") => usage.audio_tokens += tokens,
            Some("text") => usage.text_tokens += tokens,
            _ => {}
        }
    }
    usage
}

/// Map a non-2xx interactions response onto a [`TranscriptionError`].
///
/// Google returns **400** for an invalid API key on this endpoint (the same
/// quirk `gemini.rs` and the health module document), so 400 with an
/// authentication-shaped message is folded into `Unauthorized` rather than a
/// generic bad request.
fn classify(resp: &HttpResponse) -> TranscriptionError {
    let body = resp.text();
    if resp.status == 400 && looks_like_auth_failure(&body) {
        return TranscriptionError::Unauthorized;
    }
    let err = crate::retry::classify_error(resp.status, &body);
    match err {
        TranscriptionError::RateLimited { .. } => TranscriptionError::RateLimited {
            retry_after_secs: retry_after(resp),
        },
        other => other,
    }
}

fn looks_like_auth_failure(body: &str) -> bool {
    let lower = body.to_ascii_lowercase();
    lower.contains("api key not valid")
        || lower.contains("api_key_invalid")
        || lower.contains("invalid authentication")
        || lower.contains("unauthenticated")
}

// ---------------------------------------------------------------------------
// Live: BidiGenerateContent frame builders (pure Rust, not exposed over UniFFI)
// ---------------------------------------------------------------------------

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

/// Build a live **audio** frame from raw PCM (16 kHz, mono, signed 16-bit LE).
pub fn build_live_audio_frame(pcm: &[u8]) -> String {
    build_live_audio_frame_base64(&base64_encode(pcm))
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
        if let Some(text) = transcription_text(content, "interimInputTranscription")
            .or_else(|| transcription_text(content, "interim_input_transcription"))
        {
            return Ok(LiveServerMessage::PartialTranscript { text });
        }
        if let Some(text) = transcription_text(content, "inputTranscription")
            .or_else(|| transcription_text(content, "input_transcription"))
        {
            return Ok(LiveServerMessage::FinalTranscript { text });
        }
        let complete = content
            .get("generationComplete")
            .or_else(|| content.get("generation_complete"))
            .or_else(|| content.get("turnComplete"))
            .or_else(|| content.get("turn_complete"))
            .and_then(|v| v.as_bool())
            .unwrap_or(false);
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

// ---------------------------------------------------------------------------
// base64
// ---------------------------------------------------------------------------

const B64: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

/// Standard (padded, non-URL-safe) base64, no line breaks.
///
/// Hand-rolled rather than pulled in as a dependency: the workspace pins its
/// dependency set so `cargo build --offline` works, and this is the only base64
/// Rust needs — the pre-recorded path hands the encode to the platform via
/// [`Body::JsonWithBase64File`].
pub fn base64_encode(bytes: &[u8]) -> String {
    let mut out = String::with_capacity(bytes.len().div_ceil(3) * 4);
    for chunk in bytes.chunks(3) {
        let b0 = chunk[0] as u32;
        let b1 = *chunk.get(1).unwrap_or(&0) as u32;
        let b2 = *chunk.get(2).unwrap_or(&0) as u32;
        let n = (b0 << 16) | (b1 << 8) | b2;
        out.push(B64[(n >> 18) as usize & 63] as char);
        out.push(B64[(n >> 12) as usize & 63] as char);
        out.push(if chunk.len() > 1 {
            B64[(n >> 6) as usize & 63] as char
        } else {
            '='
        });
        out.push(if chunk.len() > 2 {
            B64[n as usize & 63] as char
        } else {
            '='
        });
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn params() -> TranscribeParams {
        TranscribeParams {
            api_key: "test-key".to_string(),
            model: DEFAULT_MODEL.to_string(),
            audio_path: "/tmp/speech.mp3".to_string(),
            ..Default::default()
        }
    }

    fn body_json(req: &HttpRequest) -> serde_json::Value {
        match &req.body {
            Body::JsonWithBase64File { prefix, suffix, .. } => {
                let mut s = String::from_utf8(prefix.clone()).unwrap();
                s.push_str("QUJD"); // stand-in for the platform's base64
                s.push_str(&String::from_utf8(suffix.clone()).unwrap());
                serde_json::from_str(&s).expect("prefix + base64 + suffix must be valid JSON")
            }
            other => panic!("expected JsonWithBase64File, got {other:?}"),
        }
    }

    fn header<'a>(req: &'a HttpRequest, name: &str) -> Option<&'a str> {
        req.headers
            .iter()
            .find(|h| h.name.eq_ignore_ascii_case(name))
            .map(|h| h.value.as_str())
    }

    fn resp(status: u16, body: &str) -> HttpResponse {
        HttpResponse {
            status,
            headers: vec![],
            body: body.as_bytes().to_vec(),
        }
    }

    // ---- request building ----

    #[test]
    fn posts_to_interactions_not_generate_content() {
        // TRAP 1: `:generateContent` accepts this model, bills the audio and
        // returns an empty content part. Only /v1beta/interactions works.
        let req = build_transcribe_request(&params()).unwrap();
        assert_eq!(req.method, HttpMethod::Post);
        assert_eq!(
            req.url,
            "https://generativelanguage.googleapis.com/v1beta/interactions"
        );
        assert!(!req.url.contains("generateContent"));
    }

    #[test]
    fn auth_is_header_not_query_param() {
        let req = build_transcribe_request(&params()).unwrap();
        assert_eq!(header(&req, "x-goog-api-key"), Some("test-key"));
        assert_eq!(header(&req, "Api-Revision"), Some(API_REVISION));
        assert_eq!(header(&req, "Content-Type"), Some("application/json"));
        assert!(!req.url.contains("key="));
    }

    #[test]
    fn base_url_override_is_honored() {
        let p = TranscribeParams {
            base_url: Some("https://staging.test/".to_string()),
            ..params()
        };
        let req = build_transcribe_request(&p).unwrap();
        assert_eq!(req.url, "https://staging.test/v1beta/interactions");
    }

    #[test]
    fn body_references_the_audio_path_and_never_the_bytes() {
        let req = build_transcribe_request(&params()).unwrap();
        match &req.body {
            Body::JsonWithBase64File { path, .. } => assert_eq!(path, "/tmp/speech.mp3"),
            other => panic!("expected JsonWithBase64File, got {other:?}"),
        }
    }

    #[test]
    fn body_has_the_verified_input_shape() {
        let req = build_transcribe_request(&params()).unwrap();
        let json = body_json(&req);
        assert_eq!(json["model"], DEFAULT_MODEL);
        assert_eq!(json["input"][0]["type"], "audio");
        // `resolve_mime` says `audio/mpeg` for `.mp3`; verified accepted by the
        // live endpoint (as is `audio/wav`).
        assert_eq!(json["input"][0]["mime_type"], "audio/mpeg");
        assert_eq!(json["input"][0]["data"], "QUJD");
    }

    #[test]
    fn mime_is_resolved_from_the_path_when_not_supplied() {
        let wav = TranscribeParams {
            audio_path: "/tmp/speech.wav".to_string(),
            ..params()
        };
        let json = body_json(&build_transcribe_request(&wav).unwrap());
        assert_eq!(json["input"][0]["mime_type"], "audio/wav");
    }

    #[test]
    fn no_generation_config_when_nothing_is_configured() {
        let json = body_json(&build_transcribe_request(&params()).unwrap());
        assert!(json.get("generation_config").is_none());
    }

    #[test]
    fn language_becomes_language_codes() {
        let p = TranscribeParams {
            language: Some("en-US".to_string()),
            ..params()
        };
        let json = body_json(&build_transcribe_request(&p).unwrap());
        assert_eq!(
            json["generation_config"]["transcription_config"]["language_codes"],
            serde_json::json!(["en-US"])
        );
    }

    #[test]
    fn auto_language_is_omitted_for_auto_detect() {
        for lang in ["auto", "AUTO", "", "   "] {
            let p = TranscribeParams {
                language: Some(lang.to_string()),
                ..params()
            };
            let json = body_json(&build_transcribe_request(&p).unwrap());
            assert!(
                json.get("generation_config").is_none(),
                "language {lang:?} should mean auto-detect (field omitted)"
            );
        }
    }

    #[test]
    fn vocabulary_becomes_custom_vocabulary() {
        let p = TranscribeParams {
            vocabulary: vec!["HyperWhisper".to_string(), "Kalamazoo".to_string()],
            ..params()
        };
        let json = body_json(&build_transcribe_request(&p).unwrap());
        assert_eq!(
            json["generation_config"]["transcription_config"]["custom_vocabulary"],
            serde_json::json!(["HyperWhisper", "Kalamazoo"])
        );
    }

    #[test]
    fn live_model_is_rejected_by_the_rest_builder() {
        let p = TranscribeParams {
            model: LIVE_MODEL.to_string(),
            ..params()
        };
        let err = build_transcribe_request(&p).unwrap_err();
        assert!(matches!(
            err,
            TranscriptionError::BadRequest { status: 400, .. }
        ));
    }

    // ---- TRAP 2: mutual exclusion, enforced in one place ----

    #[test]
    fn vocabulary_suppresses_diarization_and_timestamps() {
        // GOLDEN (TRAP 2): Google 400s `custom_vocabulary` sent alongside either
        // `diarization_mode` or `timestamp_granularities`. Vocabulary wins.
        let extras = TranscriptionExtras {
            diarization_mode: Some("speaker".to_string()),
            timestamp_granularities: vec!["word".to_string()],
        };
        let config = transcription_config(Some("en-US"), &["HyperWhisper".to_string()], &extras);
        assert!(config.get("custom_vocabulary").is_some());
        assert!(config.get("diarization_mode").is_none());
        assert!(config.get("timestamp_granularities").is_none());
    }

    #[test]
    fn extras_survive_when_there_is_no_vocabulary() {
        let extras = TranscriptionExtras {
            diarization_mode: Some("speaker".to_string()),
            timestamp_granularities: vec!["word".to_string()],
        };
        let config = transcription_config(Some("en-US"), &[], &extras);
        assert!(config.get("custom_vocabulary").is_none());
        assert_eq!(config["diarization_mode"], "speaker");
        assert_eq!(
            config["timestamp_granularities"],
            serde_json::json!(["word"])
        );
    }

    #[test]
    fn vocabulary_that_normalizes_to_nothing_does_not_suppress_extras() {
        let extras = TranscriptionExtras {
            diarization_mode: Some("speaker".to_string()),
            ..Default::default()
        };
        let config = transcription_config(None, &["   ".to_string()], &extras);
        assert!(config.get("custom_vocabulary").is_none());
        assert_eq!(config["diarization_mode"], "speaker");
    }

    #[test]
    fn no_built_body_ever_pairs_vocabulary_with_diarization_or_timestamps() {
        // The exhaustive guard: every body this module can build, REST and live,
        // across every combination of language/vocabulary/extras.
        let vocabs: [Vec<String>; 3] = [
            vec![],
            vec!["HyperWhisper".to_string()],
            vec!["a".to_string(), "b".to_string()],
        ];
        let extras_matrix = [
            TranscriptionExtras::none(),
            TranscriptionExtras {
                diarization_mode: Some("speaker".to_string()),
                ..Default::default()
            },
            TranscriptionExtras {
                timestamp_granularities: vec!["word".to_string()],
                ..Default::default()
            },
            TranscriptionExtras {
                diarization_mode: Some("speaker".to_string()),
                timestamp_granularities: vec!["word".to_string()],
            },
        ];

        let check = |config: &serde_json::Value, what: &str| {
            let has_vocab = config.get("custom_vocabulary").is_some();
            let has_diar = config.get("diarization_mode").is_some();
            let has_ts = config.get("timestamp_granularities").is_some();
            assert!(
                !(has_vocab && (has_diar || has_ts)),
                "{what}: custom_vocabulary must never be sent with diarization or timestamps"
            );
        };

        for lang in [None, Some("en-US"), Some("auto")] {
            for vocab in &vocabs {
                for extras in &extras_matrix {
                    check(
                        &transcription_config(lang, vocab, extras),
                        "transcription_config",
                    );

                    let p = TranscribeParams {
                        language: lang.map(str::to_string),
                        vocabulary: vocab.clone(),
                        ..params()
                    };
                    let json = body_json(&build_transcribe_request(&p).unwrap());
                    let rest = json
                        .get("generation_config")
                        .and_then(|g| g.get("transcription_config"))
                        .cloned()
                        .unwrap_or_else(|| serde_json::json!({}));
                    check(&rest, "REST body");

                    let frame: serde_json::Value =
                        serde_json::from_str(&build_live_setup_frame(LIVE_MODEL, lang, vocab))
                            .unwrap();
                    check(&frame["setup"]["input_audio_transcription"], "live setup");
                }
            }
        }
    }

    // ---- response parsing ----

    const SAMPLE: &str = r#"{
      "steps": [{"content": [{
        "text": "Hello, this is a test of HyperWhisper transcription.",
        "annotations": [
          {"type":"word_info","text":"Hello","start_index":0,"end_index":5,
           "start_offset":"1.800s","end_offset":"2.100s","speaker":"spk:0"},
          {"type":"other","text":"ignored"}
        ]}]}],
      "usage": {"total_output_tokens": 0,
                "input_tokens_by_modality": [
                  {"modality":"audio","tokens":150},
                  {"modality":"text","tokens":12}]}
    }"#;

    #[test]
    fn reads_transcript_from_steps_content_text() {
        let t = parse_transcribe_response(&resp(200, SAMPLE)).unwrap();
        assert_eq!(
            t.text,
            "Hello, this is a test of HyperWhisper transcription."
        );
    }

    #[test]
    fn blank_transcript_is_no_speech() {
        let body = r#"{"steps":[{"content":[{"text":"   "}]}]}"#;
        assert_eq!(
            parse_transcribe_response(&resp(200, body)).unwrap_err(),
            TranscriptionError::NoSpeech
        );
    }

    #[test]
    fn empty_content_part_is_no_speech_not_success() {
        // This is exactly what `:generateContent` returns for this model — a
        // silent empty part. Whatever produces it, it must not look like a win.
        let body = r#"{"steps":[{"content":[{}]}]}"#;
        assert_eq!(
            parse_transcribe_response(&resp(200, body)).unwrap_err(),
            TranscriptionError::NoSpeech
        );
    }

    #[test]
    fn word_info_offsets_are_parsed_from_duration_strings() {
        // "1.800s" is a string; `as_f64()` on it is None.
        let words = parse_word_infos(&resp(200, SAMPLE)).unwrap();
        assert_eq!(words.len(), 1, "non-word_info annotations must be skipped");
        assert_eq!(words[0].text, "Hello");
        assert_eq!(words[0].start_seconds, Some(1.8));
        assert_eq!(words[0].end_seconds, Some(2.1));
        assert_eq!(words[0].start_index, Some(0));
        assert_eq!(words[0].end_index, Some(5));
        assert_eq!(words[0].speaker.as_deref(), Some("spk:0"));
    }

    #[test]
    fn usage_reads_the_by_modality_list() {
        let usage = parse_usage(&resp(200, SAMPLE));
        assert_eq!(usage.audio_tokens, 150);
        assert_eq!(usage.text_tokens, 12);
    }

    #[test]
    fn bad_key_400_is_unauthorized_not_bad_request() {
        let body = r#"{"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT"}}"#;
        assert_eq!(
            parse_transcribe_response(&resp(400, body)).unwrap_err(),
            TranscriptionError::Unauthorized
        );
    }

    #[test]
    fn other_400s_stay_bad_request() {
        let body = r#"{"error":{"message":"custom_vocabulary is incompatible with diarization"}}"#;
        match parse_transcribe_response(&resp(400, body)).unwrap_err() {
            TranscriptionError::BadRequest { status, message } => {
                assert_eq!(status, 400);
                assert!(message.contains("incompatible"));
            }
            other => panic!("expected BadRequest, got {other:?}"),
        }
    }

    #[test]
    fn server_errors_map_to_provider_unavailable() {
        assert_eq!(
            parse_transcribe_response(&resp(503, "upstream down")).unwrap_err(),
            TranscriptionError::ProviderUnavailable { status: 503 }
        );
    }

    // ---- live frames ----

    #[test]
    fn live_setup_uses_input_audio_transcription_not_generation_config() {
        // GOLDEN (TRAP 3): the pre-recorded path
        // (`setup.generation_config.transcription_config`) closes the socket
        // with code 1007.
        let frame: serde_json::Value = serde_json::from_str(&build_live_setup_frame(
            LIVE_MODEL,
            Some("en-US"),
            &["HyperWhisper".to_string()],
        ))
        .unwrap();
        assert_eq!(frame["setup"]["model"], "models/gemini-3.5-transcribe-live");
        assert_eq!(
            frame["setup"]["input_audio_transcription"]["language_codes"],
            serde_json::json!(["en-US"])
        );
        assert_eq!(
            frame["setup"]["input_audio_transcription"]["custom_vocabulary"],
            serde_json::json!(["HyperWhisper"])
        );
        assert!(frame["setup"].get("generation_config").is_none());
    }

    #[test]
    fn live_setup_model_is_prefixed_once() {
        for input in ["", LIVE_MODEL, "models/gemini-3.5-transcribe-live"] {
            let frame: serde_json::Value =
                serde_json::from_str(&build_live_setup_frame(input, None, &[])).unwrap();
            assert_eq!(frame["setup"]["model"], "models/gemini-3.5-transcribe-live");
        }
    }

    #[test]
    fn live_audio_frame_shape() {
        let frame: serde_json::Value =
            serde_json::from_str(&build_live_audio_frame(&[0x41, 0x42, 0x43])).unwrap();
        assert_eq!(frame["realtime_input"]["audio"]["data"], "QUJD");
        assert_eq!(
            frame["realtime_input"]["audio"]["mime_type"],
            "audio/pcm;rate=16000"
        );
    }

    #[test]
    fn live_end_frame_shape() {
        let frame: serde_json::Value =
            serde_json::from_str(&build_live_audio_stream_end_frame()).unwrap();
        assert_eq!(frame["realtime_input"]["audio_stream_end"], true);
    }

    #[test]
    fn live_ws_url_carries_the_key_as_a_query_param() {
        let url = live_ws_url("k123", None);
        assert!(url.starts_with("wss://generativelanguage.googleapis.com/ws/"));
        assert!(url.ends_with("BidiGenerateContent?key=k123"));
    }

    #[test]
    fn parses_live_server_messages() {
        let cases: [(&str, LiveServerMessage); 5] = [
            (r#"{"setupComplete":{}}"#, LiveServerMessage::SetupComplete),
            (
                r#"{"serverContent":{"interimInputTranscription":{"text":"hel"}}}"#,
                LiveServerMessage::PartialTranscript {
                    text: "hel".to_string(),
                },
            ),
            (
                r#"{"serverContent":{"inputTranscription":{"text":"hello"}}}"#,
                LiveServerMessage::FinalTranscript {
                    text: "hello".to_string(),
                },
            ),
            (
                r#"{"serverContent":{"generationComplete":true}}"#,
                LiveServerMessage::Complete,
            ),
            (
                r#"{"usageMetadata":{"totalTokenCount":3}}"#,
                LiveServerMessage::Unhandled,
            ),
        ];
        for (frame, expected) in cases {
            assert_eq!(
                parse_live_server_message(frame).unwrap(),
                expected,
                "{frame}"
            );
        }
    }

    #[test]
    fn live_error_frame_is_surfaced() {
        let msg = parse_live_server_message(r#"{"error":{"code":1007,"message":"invalid setup"}}"#)
            .unwrap();
        assert_eq!(
            msg,
            LiveServerMessage::Error {
                message: "invalid setup".to_string()
            }
        );
    }

    // ---- base64 ----

    #[test]
    fn base64_matches_the_standard_alphabet_and_padding() {
        assert_eq!(base64_encode(b""), "");
        assert_eq!(base64_encode(b"f"), "Zg==");
        assert_eq!(base64_encode(b"fo"), "Zm8=");
        assert_eq!(base64_encode(b"foo"), "Zm9v");
        assert_eq!(base64_encode(b"foob"), "Zm9vYg==");
        assert_eq!(base64_encode(b"fooba"), "Zm9vYmE=");
        assert_eq!(base64_encode(b"foobar"), "Zm9vYmFy");
        // Non-URL-safe alphabet: bytes that produce '+' and '/'.
        assert_eq!(base64_encode(&[0xFB, 0xFF]), "+/8=");
    }
}
