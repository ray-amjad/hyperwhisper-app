//! Pre-recorded transcription: `POST /v1beta/interactions`. See the parent
//! module's docs for the wire shape and the inline-base64 contract.

use super::{
    default_model, is_live_model, transcription_config, TranscriptionExtras, API_REVISION,
    API_ROOT, LIVE_MODEL,
};
use crate::contract::{
    Body, Header, HttpMethod, HttpRequest, HttpResponse, TranscribeParams, Transcript,
    TranscriptionError,
};
use crate::helpers::resolve_mime;
use crate::providers::common::retry_after;

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
        default_model().to_string()
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
