//! AssemblyAI transcription (sans-I/O). A **three-step async workflow**:
//!
//! 1. **Upload** the raw audio bytes → `{ "upload_url": "..." }`.
//! 2. **Create** a transcript job from that URL → `{ "id": "...", "status": "queued" }`.
//! 3. **Poll** `GET /transcript/{id}` until `status == "completed"` (or `"error"`).
//!
//! The platform drives the loop (sleeping between polls — no clock/RNG in Rust);
//! Rust builds each step's [`HttpRequest`] and parses each step's [`HttpResponse`].
//! Audio bytes never cross FFI — the upload body is a [`Body::FileStream`] the
//! platform streams from disk.
//!
//! ## Parity references
//! - macOS `AssemblyAIProvider.swift` (`uploadFile` / `startTranscript` /
//!   `waitForTranscript`)
//! - Windows `AssemblyAIService.cs` (`UploadAudioAsync` / `CreateTranscriptAsync` /
//!   `PollTranscriptAsync`)
//!
//! ## Endpoints
//! - Upload:  `POST https://api.assemblyai.com/v2/upload`
//! - Create:  `POST https://api.assemblyai.com/v2/transcript`
//! - Poll:    `GET  https://api.assemblyai.com/v2/transcript/{id}`
//!
//! ## Auth
//! AssemblyAI uses a bare `Authorization: <key>` header — **no `Bearer` prefix**
//! (both reference impls set the key directly). See [`auth_header`].
//!
//! ## Parity notes / unification choices
//! - **`speech_model` vs `speech_models`**: macOS sends `speech_models` as a
//!   one-element **array**; Windows sends `speech_model` as a **string**. We
//!   follow macOS (the verified platform) and send `speech_models: [model]`.
//!   AssemblyAI accepts both; this is a documented divergence from Windows.
//! - **Model default / aliases**: empty model → `universal-2`. Legacy IDs
//!   `universal` → `universal-2`, `slam-1` → `universal-3-pro` (both platforms).
//!   A trailing `-medical` suffix is stripped and surfaces as
//!   `domain: "medical-v1"` (Medical Mode add-on).
//! - **Vocabulary** (`keyterms_prompt`): trimmed, drop empties, drop phrases
//!   with > 6 words, capped at 1000 for `universal-3-pro` else 200. (`word_boost`
//!   is deprecated; both platforms moved to `keyterms_prompt`.)
//! - **Poll status mapping**: `completed` → text (empty text → `NoSpeech`);
//!   `error` → `BadRequest`; `queued`/`processing`/unknown →
//!   [`PollOutcome::Pending`] so the platform keeps polling.

use crate::contract::{
    Body, Header, HttpMethod, HttpRequest, HttpResponse, TranscribeParams, Transcript,
    TranscriptionError,
};
use crate::helpers::{keyword_boost_terms, resolve_mime};
use crate::providers::common::classify_http;

/// AssemblyAI API base. `params.base_url` overrides it (tests/staging).
pub const BASE_URL: &str = "https://api.assemblyai.com/v2";

/// Default model when the caller leaves `params.model` empty.
/// PARITY: macOS `defaultModel(for: .assemblyAI)` / Windows default = `universal-2`.
pub const DEFAULT_MODEL: &str = "universal-2";

/// Max `keyterms_prompt` terms for `universal-3-pro` (else [`MAX_KEYTERMS_DEFAULT`]).
pub const MAX_KEYTERMS_PRO: usize = 1000;
/// Max `keyterms_prompt` terms for non-pro models.
pub const MAX_KEYTERMS_DEFAULT: usize = 200;
/// Max words per `keyterms_prompt` phrase (AssemblyAI spec).
pub const MAX_KEYTERM_WORDS: usize = 6;

fn base(params: &TranscribeParams) -> String {
    params
        .base_url
        .as_deref()
        .map(|s| s.trim_end_matches('/').to_string())
        .unwrap_or_else(|| BASE_URL.to_string())
}

/// `Authorization: <key>` — AssemblyAI uses the bare key, **no `Bearer`**.
fn auth_header(api_key: &str) -> Header {
    Header::new("Authorization", api_key.to_string())
}

/// Resolve a legacy AssemblyAI model alias to its current ID.
/// PARITY: macOS `legacyAssemblyAIAliases` / Windows `LegacyAssemblyAIAliases`.
pub fn resolve_model_alias(id: &str) -> &str {
    match id {
        "universal" => "universal-2",
        "slam-1" => "universal-3-pro",
        other => other,
    }
}

/// Split a (possibly `-medical`) model ID into `(speech_model, medical)`.
/// PARITY: macOS `assemblyAIRequestParams(for:)` / Windows `GetAssemblyAIRequestParams`.
pub fn request_params(id: &str) -> (String, bool) {
    let resolved = resolve_model_alias(id);
    if let Some(stripped) = resolved.strip_suffix("-medical") {
        (stripped.to_string(), true)
    } else {
        (resolved.to_string(), false)
    }
}

/// The effective `speech_model` (after alias + `-medical` strip), defaulting
/// empty/blank model to [`DEFAULT_MODEL`].
fn speech_model_and_medical(params: &TranscribeParams) -> (String, bool) {
    let model = if params.model.trim().is_empty() {
        DEFAULT_MODEL
    } else {
        params.model.as_str()
    };
    request_params(model)
}

// ---------------------------------------------------------------------------
// Step 1 — upload
// ---------------------------------------------------------------------------

/// Build the **upload** request. The platform streams the audio bytes from
/// `params.audio_path` as the raw request body (`application/octet-stream`).
pub fn build_upload_request(params: &TranscribeParams) -> Result<HttpRequest, TranscriptionError> {
    Ok(HttpRequest {
        method: HttpMethod::Post,
        url: format!("{}/upload", base(params)),
        headers: vec![
            auth_header(&params.api_key),
            // PARITY: both impls set Content-Type: application/octet-stream on the
            // upload. resolve_mime is intentionally NOT used here.
            Header::new("Content-Type", "application/octet-stream"),
        ],
        body: Body::FileStream {
            path: params.audio_path.clone(),
            content_type: "application/octet-stream".to_string(),
        },
    })
}

/// Parse the **upload** response → the temporary `upload_url`.
pub fn parse_upload_response(resp: &HttpResponse) -> Result<String, TranscriptionError> {
    let raw = resp.text();
    if !(200..=299).contains(&resp.status) {
        return Err(classify_http(resp, &raw));
    }
    let json: serde_json::Value =
        serde_json::from_str(&raw).map_err(|e| TranscriptionError::Parse {
            message: format!("invalid upload JSON: {e}"),
        })?;
    json.get("upload_url")
        .and_then(|v| v.as_str())
        .filter(|s| !s.is_empty())
        .map(|s| s.to_string())
        .ok_or_else(|| TranscriptionError::Parse {
            message: "upload response missing upload_url".to_string(),
        })
}

// ---------------------------------------------------------------------------
// Step 2 — create transcript
// ---------------------------------------------------------------------------

/// Build the **create-transcript** request from the uploaded `audio_url`.
///
/// Body JSON: `{ audio_url, speech_models: [model], [domain], (language_code |
/// language_detection), [keyterms_prompt] }`.
pub fn build_create_request(
    params: &TranscribeParams,
    audio_url: &str,
) -> Result<HttpRequest, TranscriptionError> {
    let (speech_model, medical) = speech_model_and_medical(params);

    let mut body = serde_json::Map::new();
    body.insert(
        "audio_url".into(),
        serde_json::Value::String(audio_url.to_string()),
    );
    // PARITY (divergence): macOS sends `speech_models` as a 1-element array;
    // Windows sends `speech_model` string. We follow macOS (verified platform).
    body.insert(
        "speech_models".into(),
        serde_json::Value::Array(vec![serde_json::Value::String(speech_model.clone())]),
    );
    if medical {
        body.insert(
            "domain".into(),
            serde_json::Value::String("medical-v1".into()),
        );
    }

    // Language: explicit code, else auto-detection.
    match params.language.as_deref().map(str::trim) {
        Some(lang) if !lang.is_empty() && !lang.eq_ignore_ascii_case("auto") => {
            body.insert(
                "language_code".into(),
                serde_json::Value::String(lang.to_string()),
            );
        }
        _ => {
            body.insert("language_detection".into(), serde_json::Value::Bool(true));
        }
    }

    // keyterms_prompt: shared sanitize/dedup, drop > 6-word phrases, cap by model.
    let max_terms = if speech_model == "universal-3-pro" {
        MAX_KEYTERMS_PRO
    } else {
        MAX_KEYTERMS_DEFAULT
    };
    let keyterms: Vec<serde_json::Value> = keyword_boost_terms(&params.vocabulary, None)
        .into_iter()
        .filter(|w| w.split_whitespace().count() <= MAX_KEYTERM_WORDS)
        .take(max_terms)
        .map(serde_json::Value::String)
        .collect();
    if !keyterms.is_empty() {
        body.insert("keyterms_prompt".into(), serde_json::Value::Array(keyterms));
    }

    let data = serde_json::to_vec(&serde_json::Value::Object(body)).map_err(|e| {
        TranscriptionError::Parse {
            message: format!("failed to encode create body: {e}"),
        }
    })?;

    Ok(HttpRequest {
        method: HttpMethod::Post,
        url: format!("{}/transcript", base(params)),
        headers: vec![auth_header(&params.api_key)],
        body: Body::Bytes {
            content_type: "application/json".to_string(),
            data,
        },
    })
}

/// Parse the **create-transcript** response → the transcript `id`.
pub fn parse_create_response(resp: &HttpResponse) -> Result<String, TranscriptionError> {
    let raw = resp.text();
    if !(200..=299).contains(&resp.status) {
        return Err(classify_http(resp, &raw));
    }
    let json: serde_json::Value =
        serde_json::from_str(&raw).map_err(|e| TranscriptionError::Parse {
            message: format!("invalid create JSON: {e}"),
        })?;
    json.get("id")
        .and_then(|v| v.as_str())
        .filter(|s| !s.is_empty())
        .map(|s| s.to_string())
        .ok_or_else(|| TranscriptionError::Parse {
            message: "create response missing id".to_string(),
        })
}

// ---------------------------------------------------------------------------
// Step 3 — poll
// ---------------------------------------------------------------------------

/// The result of parsing one poll response. `Pending` tells the platform to wait
/// and poll again; `Done` carries the finished [`Transcript`].
#[derive(Debug, Clone, PartialEq)]
pub enum PollOutcome {
    /// Job still `queued` / `processing` (or an unknown non-terminal status).
    Pending,
    /// Job `completed` — transcript ready.
    Done(Transcript),
}

/// Build the **poll** request for transcript `id`.
pub fn build_poll_request(
    params: &TranscribeParams,
    id: &str,
) -> Result<HttpRequest, TranscriptionError> {
    Ok(HttpRequest {
        method: HttpMethod::Get,
        url: format!("{}/transcript/{}", base(params), id),
        headers: vec![auth_header(&params.api_key)],
        body: Body::Empty,
    })
}

/// Parse a **poll** response.
///
/// - `completed` → [`PollOutcome::Done`] (empty text → [`TranscriptionError::NoSpeech`]).
/// - `error` → [`TranscriptionError::BadRequest`] carrying the `error` message.
/// - `queued` / `processing` / unknown → [`PollOutcome::Pending`].
///
/// PARITY: a transient HTTP status on a poll (429/5xx) means the job is still
/// running server-side; the macOS/Windows impls keep polling. We surface those as
/// [`PollOutcome::Pending`] so the platform retries (401/403 still fail).
pub fn parse_poll_response(resp: &HttpResponse) -> Result<PollOutcome, TranscriptionError> {
    let raw = resp.text();

    if !(200..=299).contains(&resp.status) {
        // Unauthorized is terminal; transient statuses keep the loop alive.
        match resp.status {
            401 | 403 => return Err(TranscriptionError::Unauthorized),
            429 | 500..=599 => return Ok(PollOutcome::Pending),
            _ => return Err(classify_http(resp, &raw)),
        }
    }

    let json: serde_json::Value =
        serde_json::from_str(&raw).map_err(|e| TranscriptionError::Parse {
            message: format!("invalid poll JSON: {e}"),
        })?;

    let status = json
        .get("status")
        .and_then(|v| v.as_str())
        .unwrap_or("")
        .to_lowercase();

    match status.as_str() {
        "completed" => {
            let text = json.get("text").and_then(|v| v.as_str()).unwrap_or("");
            if text.is_empty() {
                return Err(TranscriptionError::NoSpeech);
            }
            Ok(PollOutcome::Done(Transcript {
                text: text.to_string(),
                ..Default::default()
            }))
        }
        "error" => {
            let message = json
                .get("error")
                .and_then(|v| v.as_str())
                .unwrap_or("transcription failed")
                .to_string();
            Err(TranscriptionError::BadRequest {
                status: resp.status,
                message,
            })
        }
        // queued / processing / unknown — keep polling.
        _ => Ok(PollOutcome::Pending),
    }
}

// ---------------------------------------------------------------------------
// Convenience: MIME (kept for symmetry / call-site discoverability)
// ---------------------------------------------------------------------------

/// Resolve the audio MIME for `params` (used only when a caller wants it; the
/// upload itself always sends `application/octet-stream` per parity).
pub fn audio_mime(params: &TranscribeParams) -> String {
    params
        .audio_mime
        .clone()
        .unwrap_or_else(|| resolve_mime(&params.audio_path))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn params() -> TranscribeParams {
        TranscribeParams {
            api_key: "aai-key".to_string(),
            model: "universal-2".to_string(),
            audio_path: "/tmp/rec.m4a".to_string(),
            ..Default::default()
        }
    }

    fn body_json(req: &HttpRequest) -> serde_json::Value {
        match &req.body {
            Body::Bytes { data, .. } => serde_json::from_slice(data).unwrap(),
            other => panic!("expected Bytes body, got {other:?}"),
        }
    }

    // ---- aliases / medical -------------------------------------------------

    #[test]
    fn resolves_legacy_aliases() {
        assert_eq!(resolve_model_alias("universal"), "universal-2");
        assert_eq!(resolve_model_alias("slam-1"), "universal-3-pro");
        assert_eq!(resolve_model_alias("universal-3-pro"), "universal-3-pro");
    }

    #[test]
    fn request_params_strips_medical_suffix() {
        assert_eq!(request_params("universal-2"), ("universal-2".into(), false));
        assert_eq!(
            request_params("universal-2-medical"),
            ("universal-2".into(), true)
        );
        // alias then medical strip
        assert_eq!(request_params("slam-1"), ("universal-3-pro".into(), false));
    }

    // ---- step 1: upload ----------------------------------------------------

    #[test]
    fn upload_request_streams_file_with_bare_auth() {
        let req = build_upload_request(&params()).unwrap();
        assert_eq!(req.method, HttpMethod::Post);
        assert_eq!(req.url, "https://api.assemblyai.com/v2/upload");
        // bare key, no "Bearer "
        assert!(req
            .headers
            .contains(&Header::new("Authorization", "aai-key")));
        assert!(req
            .headers
            .contains(&Header::new("Content-Type", "application/octet-stream")));
        match &req.body {
            Body::FileStream { path, content_type } => {
                assert_eq!(path, "/tmp/rec.m4a");
                assert_eq!(content_type, "application/octet-stream");
            }
            other => panic!("expected FileStream, got {other:?}"),
        }
    }

    #[test]
    fn parse_upload_response_extracts_url() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"upload_url":"https://cdn.assemblyai.com/upload/abc123"}"#.to_vec(),
        };
        assert_eq!(
            parse_upload_response(&resp).unwrap(),
            "https://cdn.assemblyai.com/upload/abc123"
        );
    }

    #[test]
    fn parse_upload_response_missing_url_is_parse_error() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{}"#.to_vec(),
        };
        assert!(matches!(
            parse_upload_response(&resp).unwrap_err(),
            TranscriptionError::Parse { .. }
        ));
    }

    #[test]
    fn parse_upload_response_401_unauthorized() {
        let resp = HttpResponse {
            status: 401,
            headers: vec![],
            body: br#"{"error":"bad key"}"#.to_vec(),
        };
        assert_eq!(
            parse_upload_response(&resp).unwrap_err(),
            TranscriptionError::Unauthorized
        );
    }

    // ---- step 2: create ----------------------------------------------------

    #[test]
    fn create_request_basic_shape() {
        let req = build_create_request(&params(), "https://cdn/upload/x").unwrap();
        assert_eq!(req.method, HttpMethod::Post);
        assert_eq!(req.url, "https://api.assemblyai.com/v2/transcript");
        assert!(req
            .headers
            .contains(&Header::new("Authorization", "aai-key")));
        let j = body_json(&req);
        assert_eq!(j["audio_url"], "https://cdn/upload/x");
        // speech_models as array (macOS parity)
        assert_eq!(j["speech_models"], serde_json::json!(["universal-2"]));
        // no language -> language_detection true
        assert_eq!(j["language_detection"], serde_json::json!(true));
        assert!(j.get("language_code").is_none());
        assert!(j.get("domain").is_none());
        assert!(j.get("keyterms_prompt").is_none());
    }

    #[test]
    fn create_request_explicit_language() {
        let mut p = params();
        p.language = Some("es".to_string());
        let j = body_json(&build_create_request(&p, "u").unwrap());
        assert_eq!(j["language_code"], "es");
        assert!(j.get("language_detection").is_none());
    }

    #[test]
    fn create_request_auto_language_uses_detection() {
        let mut p = params();
        p.language = Some("AUTO".to_string());
        let j = body_json(&build_create_request(&p, "u").unwrap());
        assert_eq!(j["language_detection"], serde_json::json!(true));
        assert!(j.get("language_code").is_none());
    }

    #[test]
    fn create_request_medical_model_adds_domain() {
        let mut p = params();
        p.model = "universal-2-medical".to_string();
        let j = body_json(&build_create_request(&p, "u").unwrap());
        assert_eq!(j["speech_models"], serde_json::json!(["universal-2"]));
        assert_eq!(j["domain"], "medical-v1");
    }

    #[test]
    fn create_request_empty_model_defaults() {
        let mut p = params();
        p.model = "".to_string();
        let j = body_json(&build_create_request(&p, "u").unwrap());
        assert_eq!(j["speech_models"], serde_json::json!(["universal-2"]));
    }

    #[test]
    fn create_request_keyterms_filters_and_caps() {
        let mut p = params();
        p.vocabulary = vec![
            "Rust".to_string(),
            "  UniFFI  ".to_string(),
            "rust".to_string(), // duplicate dropped by shared egress helper
            "API<script>".to_string(),
            "".to_string(),
            "this phrase has way too many words to keep".to_string(), // 9 words -> dropped
        ];
        let j = body_json(&build_create_request(&p, "u").unwrap());
        assert_eq!(
            j["keyterms_prompt"],
            serde_json::json!(["Rust", "UniFFI", "APIscript"])
        );
    }

    #[test]
    fn create_request_pro_model_higher_cap() {
        let mut p = params();
        p.model = "slam-1".to_string(); // -> universal-3-pro
        p.vocabulary = (0..1001).map(|i| format!("t{i}")).collect();
        let j = body_json(&build_create_request(&p, "u").unwrap());
        assert_eq!(
            j["keyterms_prompt"].as_array().unwrap().len(),
            MAX_KEYTERMS_PRO
        );
    }

    #[test]
    fn create_request_default_model_cap() {
        let mut p = params();
        p.vocabulary = (0..201).map(|i| format!("t{i}")).collect();
        let j = body_json(&build_create_request(&p, "u").unwrap());
        assert_eq!(
            j["keyterms_prompt"].as_array().unwrap().len(),
            MAX_KEYTERMS_DEFAULT
        );
    }

    #[test]
    fn parse_create_response_extracts_id() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"id":"transcript_abc","status":"queued"}"#.to_vec(),
        };
        assert_eq!(parse_create_response(&resp).unwrap(), "transcript_abc");
    }

    // ---- step 3: poll ------------------------------------------------------

    #[test]
    fn poll_request_targets_id() {
        let req = build_poll_request(&params(), "tid").unwrap();
        assert_eq!(req.method, HttpMethod::Get);
        assert_eq!(req.url, "https://api.assemblyai.com/v2/transcript/tid");
        assert!(matches!(req.body, Body::Empty));
    }

    #[test]
    fn poll_processing_is_pending() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"id":"x","status":"processing","text":null}"#.to_vec(),
        };
        assert_eq!(parse_poll_response(&resp).unwrap(), PollOutcome::Pending);
    }

    #[test]
    fn poll_queued_is_pending() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"status":"queued"}"#.to_vec(),
        };
        assert_eq!(parse_poll_response(&resp).unwrap(), PollOutcome::Pending);
    }

    #[test]
    fn poll_completed_returns_transcript() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"id":"x","status":"completed","text":"hello world"}"#.to_vec(),
        };
        match parse_poll_response(&resp).unwrap() {
            PollOutcome::Done(t) => assert_eq!(t.text, "hello world"),
            other => panic!("expected Done, got {other:?}"),
        }
    }

    #[test]
    fn poll_completed_empty_text_is_no_speech() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"status":"completed","text":""}"#.to_vec(),
        };
        assert_eq!(
            parse_poll_response(&resp).unwrap_err(),
            TranscriptionError::NoSpeech
        );
    }

    #[test]
    fn poll_error_status_is_bad_request_with_message() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"status":"error","error":"audio too short"}"#.to_vec(),
        };
        match parse_poll_response(&resp).unwrap_err() {
            TranscriptionError::BadRequest { message, .. } => {
                assert_eq!(message, "audio too short")
            }
            other => panic!("expected BadRequest, got {other:?}"),
        }
    }

    #[test]
    fn poll_transient_http_keeps_polling() {
        for status in [429u16, 500, 503] {
            let resp = HttpResponse {
                status,
                headers: vec![],
                body: b"upstream busy".to_vec(),
            };
            assert_eq!(
                parse_poll_response(&resp).unwrap(),
                PollOutcome::Pending,
                "status {status} should be Pending"
            );
        }
    }

    #[test]
    fn poll_401_is_terminal_unauthorized() {
        let resp = HttpResponse {
            status: 401,
            headers: vec![],
            body: b"".to_vec(),
        };
        assert_eq!(
            parse_poll_response(&resp).unwrap_err(),
            TranscriptionError::Unauthorized
        );
    }

    // ---- base_url override -------------------------------------------------

    #[test]
    fn base_url_override_applies_to_all_steps() {
        let mut p = params();
        p.base_url = Some("https://staging.assemblyai.test/v2/".to_string());
        assert_eq!(
            build_upload_request(&p).unwrap().url,
            "https://staging.assemblyai.test/v2/upload"
        );
        assert_eq!(
            build_create_request(&p, "u").unwrap().url,
            "https://staging.assemblyai.test/v2/transcript"
        );
        assert_eq!(
            build_poll_request(&p, "tid").unwrap().url,
            "https://staging.assemblyai.test/v2/transcript/tid"
        );
    }
}
