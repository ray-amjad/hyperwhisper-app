//! Soniox async/file transcription (sans-I/O).
//!
//! Soniox is a **multi-step polling** provider (like AssemblyAI). The platform
//! drives the I/O; Rust builds each request and parses each response:
//!
//! 1. **Upload** the audio file → `POST {base}/files` (`multipart/form-data`,
//!    one `file` part) → parse `{ "id": <file_id> }`.
//! 2. **Create** the transcription → `POST {base}/transcriptions` (JSON body
//!    `{file_id, model, [language_hints], [context]}`) → parse `{ "id": <id> }`.
//! 3. **Poll** status → `GET {base}/transcriptions/{id}` → parse
//!    `{status, [error_message]}` until `completed`.
//! 4. **Fetch** transcript → `GET {base}/transcriptions/{id}/transcript` →
//!    parse `{ "text": "..." }` (the `final_transcript`/`text` field).
//! 5. **Cleanup** (best-effort) → `DELETE` the transcription and the file.
//!
//! Auth is `Authorization: Bearer <key>` on every request.
//!
//! Parity references:
//! - macOS `SonioxProvider.swift` (the verified platform)
//! - Windows `SonioxService.cs`
//!
//! ## The contract pair
//!
//! [`build_transcribe_request`] / [`parse_transcribe_response`] exist for
//! uniformity with the single-shot providers: the builder aliases the **upload**
//! request (step 1 — the only step needing just `TranscribeParams`), and the
//! parser aliases the **transcript-fetch** response (the step that produces a
//! [`Transcript`]). The orchestration layer (`hw-core`) sequences the discrete
//! step builders/parsers in between (create needs the `file_id` from step 1;
//! poll/fetch/delete need the transcription id from step 2).
//!
//! ## Parity notes
//!
//! - **context (vocabulary)**: both platforms join terms with `", "` (comma-space)
//!   into a single `context` string. Terms are routed through the shared
//!   sanitizer/dedup/length-cap helper before interpolation.
//! - **language**: sent as `language_hints: [lang]` only when explicit
//!   (non-empty, non-`auto`). Omitted otherwise.
//! - **default model**: `stt-async-v5` (macOS default; the verified platform).
//!   Windows defaults to `stt-async-v4` — documented divergence, we follow macOS.
//! - **transcript field**: the `/transcript` endpoint returns `{ "text": "..." }`.
//!   The task spec calls this `final_transcript`; the shipped Soniox file API uses
//!   `text` (verified in both reference impls). We read `text`, falling back to
//!   `final_transcript` defensively.

use crate::contract::{
    Body, Header, HttpMethod, HttpRequest, HttpResponse, Part, TranscribeParams, Transcript,
    TranscriptionError,
};
use crate::helpers::{keyword_boost_terms, resolve_mime, MULTIPART_BOUNDARY};
use crate::providers::common::filename_of;

/// Soniox API base URL.
pub const BASE_URL: &str = "https://api.soniox.com/v1";

/// Default model when `params.model` is empty. PARITY: macOS `stt-async-v5`
/// (verified platform); Windows uses `stt-async-v4` (documented divergence).
pub const DEFAULT_MODEL: &str = "stt-async-v5";

/// Resolve the effective base URL (override via `params.base_url`).
fn base(params: &TranscribeParams) -> String {
    params
        .base_url
        .as_deref()
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .unwrap_or(BASE_URL)
        .trim_end_matches('/')
        .to_string()
}

fn auth(params: &TranscribeParams) -> Header {
    Header::new("Authorization", format!("Bearer {}", params.api_key))
}

fn resolve_model(model: &str) -> String {
    let t = model.trim();
    if t.is_empty() {
        DEFAULT_MODEL.to_string()
    } else {
        t.to_string()
    }
}

/// Whether the language is explicit (non-empty, non-`auto`).
fn explicit_language(language: Option<&str>) -> Option<String> {
    language
        .map(str::trim)
        .filter(|t| !t.is_empty() && !t.eq_ignore_ascii_case("auto"))
        .map(str::to_string)
}

// ---------------------------------------------------------------------------
// Step 1 — upload file (multipart)
// ---------------------------------------------------------------------------

/// Build the **upload** request: `POST {base}/files` with a single `file`
/// multipart part the platform streams from disk.
pub fn build_upload_request(params: &TranscribeParams) -> Result<HttpRequest, TranscriptionError> {
    let mime = params
        .audio_mime
        .clone()
        .unwrap_or_else(|| resolve_mime(&params.audio_path));

    let parts = vec![Part::FileRef {
        field: "file".to_string(),
        path: params.audio_path.clone(),
        mime,
        filename: filename_of(&params.audio_path),
    }];

    Ok(HttpRequest {
        method: HttpMethod::Post,
        url: format!("{}/files", base(params)),
        headers: vec![auth(params)],
        body: Body::Multipart {
            boundary: MULTIPART_BOUNDARY.to_string(),
            parts,
        },
    })
}

/// Parse the **upload** response → the file id (`{ "id": "..." }`).
pub fn parse_upload_response(resp: &HttpResponse) -> Result<String, TranscriptionError> {
    parse_id(resp, "upload")
}

// ---------------------------------------------------------------------------
// Step 2 — create transcription (JSON)
// ---------------------------------------------------------------------------

/// Build the **create-transcription** request: `POST {base}/transcriptions` with
/// JSON body `{file_id, model, [language_hints], [context]}`.
pub fn build_create_request(
    params: &TranscribeParams,
    file_id: &str,
) -> Result<HttpRequest, TranscriptionError> {
    let mut obj = serde_json::Map::new();
    obj.insert(
        "file_id".to_string(),
        serde_json::Value::String(file_id.to_string()),
    );
    obj.insert(
        "model".to_string(),
        serde_json::Value::String(resolve_model(&params.model)),
    );

    if let Some(lang) = explicit_language(params.language.as_deref()) {
        obj.insert(
            "language_hints".to_string(),
            serde_json::Value::Array(vec![serde_json::Value::String(lang)]),
        );
    }

    // context: comma-space-joined vocabulary after shared sanitizer/dedup.
    let terms = keyword_boost_terms(&params.vocabulary, None);
    if !terms.is_empty() {
        obj.insert(
            "context".to_string(),
            serde_json::Value::String(terms.join(", ")),
        );
    }

    let data = serde_json::to_vec(&serde_json::Value::Object(obj)).map_err(|e| {
        TranscriptionError::Parse {
            message: format!("failed to encode create body: {e}"),
        }
    })?;

    Ok(HttpRequest {
        method: HttpMethod::Post,
        url: format!("{}/transcriptions", base(params)),
        headers: vec![auth(params)],
        body: Body::Bytes {
            content_type: "application/json".to_string(),
            data,
        },
    })
}

/// Parse the **create-transcription** response → the transcription id.
pub fn parse_create_response(resp: &HttpResponse) -> Result<String, TranscriptionError> {
    parse_id(resp, "create")
}

// ---------------------------------------------------------------------------
// Step 3 — poll status (GET)
// ---------------------------------------------------------------------------

/// Build the **status-poll** request: `GET {base}/transcriptions/{id}`.
pub fn build_status_request(
    params: &TranscribeParams,
    transcription_id: &str,
) -> Result<HttpRequest, TranscriptionError> {
    Ok(HttpRequest {
        method: HttpMethod::Get,
        url: format!("{}/transcriptions/{}", base(params), transcription_id),
        headers: vec![auth(params)],
        body: Body::Empty,
    })
}

/// The terminal/intermediate states a Soniox transcription job reports.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PollStatus {
    /// Still queued/processing — keep polling.
    Pending,
    /// Job finished successfully — fetch the transcript next.
    Completed,
}

/// Parse a **status-poll** response.
///
/// - 2xx `status=completed` → [`PollStatus::Completed`].
/// - 2xx `status` in `queued`/`processing`/other → [`PollStatus::Pending`].
/// - 2xx `status=error` → mapped error (quota when the message signals
///   balance/funds/autopay/quota/limit, else `BadRequest`). PARITY:
///   `SonioxProvider.swift` `waitForCompletion` error branch.
/// - Non-2xx → [`classify_soniox_http`].
pub fn parse_status_response(resp: &HttpResponse) -> Result<PollStatus, TranscriptionError> {
    let raw = resp.text();
    if !(200..=299).contains(&resp.status) {
        return Err(classify_soniox_http(resp, &raw));
    }

    let json: serde_json::Value =
        serde_json::from_str(&raw).map_err(|e| TranscriptionError::Parse {
            message: format!("invalid JSON: {e}"),
        })?;

    let status = json
        .get("status")
        .and_then(|v| v.as_str())
        .unwrap_or("")
        .to_lowercase();

    match status.as_str() {
        "completed" => Ok(PollStatus::Completed),
        "error" => {
            let message = json
                .get("error_message")
                .and_then(|v| v.as_str())
                .map(str::trim)
                .filter(|s| !s.is_empty())
                .map(str::to_string);
            let lower = message.as_deref().unwrap_or("").to_lowercase();
            if ["balance", "funds", "autopay", "quota", "limit"]
                .iter()
                .any(|k| lower.contains(k))
            {
                Err(TranscriptionError::QuotaExceeded)
            } else {
                Err(TranscriptionError::BadRequest {
                    status: resp.status,
                    message: message.unwrap_or_else(|| "Soniox transcription failed".to_string()),
                })
            }
        }
        // queued / processing / unknown → keep polling.
        _ => Ok(PollStatus::Pending),
    }
}

// ---------------------------------------------------------------------------
// Step 4 — fetch transcript (GET)
// ---------------------------------------------------------------------------

/// Build the **transcript-fetch** request:
/// `GET {base}/transcriptions/{id}/transcript`.
pub fn build_transcript_request(
    params: &TranscribeParams,
    transcription_id: &str,
) -> Result<HttpRequest, TranscriptionError> {
    Ok(HttpRequest {
        method: HttpMethod::Get,
        url: format!(
            "{}/transcriptions/{}/transcript",
            base(params),
            transcription_id
        ),
        headers: vec![auth(params)],
        body: Body::Empty,
    })
}

/// Parse the **transcript-fetch** response → [`Transcript`].
///
/// Success shape: `{ "text": "..." }` (the `final_transcript`). 2xx with empty
/// text → [`TranscriptionError::NoSpeech`] (parity with both platforms treating
/// a blank transcript as silence). Non-2xx → [`classify_soniox_http`].
pub fn parse_transcript_response(resp: &HttpResponse) -> Result<Transcript, TranscriptionError> {
    let raw = resp.text();
    if !(200..=299).contains(&resp.status) {
        return Err(classify_soniox_http(resp, &raw));
    }

    // Try JSON `{ "text" }` / `{ "final_transcript" }`; fall back to plain text
    // (matches the Swift/C# `decode? else plain text` path).
    let text = match serde_json::from_str::<serde_json::Value>(&raw) {
        Ok(json) => json
            .get("text")
            .or_else(|| json.get("final_transcript"))
            .and_then(|v| v.as_str())
            .unwrap_or("")
            .to_string(),
        Err(_) => raw.trim().to_string(),
    };

    if text.is_empty() {
        return Err(TranscriptionError::NoSpeech);
    }

    Ok(Transcript {
        text,
        credits_remaining: None,
        cost: None,
        raw_provider: None,
    })
}

// ---------------------------------------------------------------------------
// Step 5 — cleanup (DELETE, best-effort)
// ---------------------------------------------------------------------------

/// Build the **delete-transcription** cleanup request.
pub fn build_delete_transcription_request(
    params: &TranscribeParams,
    transcription_id: &str,
) -> HttpRequest {
    HttpRequest {
        method: HttpMethod::Delete,
        url: format!("{}/transcriptions/{}", base(params), transcription_id),
        headers: vec![auth(params)],
        body: Body::Empty,
    }
}

/// Build the **delete-file** cleanup request.
pub fn build_delete_file_request(params: &TranscribeParams, file_id: &str) -> HttpRequest {
    HttpRequest {
        method: HttpMethod::Delete,
        url: format!("{}/files/{}", base(params), file_id),
        headers: vec![auth(params)],
        body: Body::Empty,
    }
}

// ---------------------------------------------------------------------------
// Contract-named pair (uniformity with single-shot providers)
// ---------------------------------------------------------------------------

/// Contract entry point. Soniox's first request is the file upload — the only
/// step that needs just [`TranscribeParams`] — so this aliases
/// [`build_upload_request`]. Subsequent steps use the discrete builders.
pub fn build_transcribe_request(
    params: &TranscribeParams,
) -> Result<HttpRequest, TranscriptionError> {
    build_upload_request(params)
}

/// Contract entry point. The final transcript comes from the transcript-fetch
/// step, so this aliases [`parse_transcript_response`].
pub fn parse_transcribe_response(resp: &HttpResponse) -> Result<Transcript, TranscriptionError> {
    parse_transcript_response(resp)
}

// ---------------------------------------------------------------------------
// shared helpers
// ---------------------------------------------------------------------------

/// Parse a `{ "id": "..." }` body, classifying non-2xx and missing-id failures.
fn parse_id(resp: &HttpResponse, op: &str) -> Result<String, TranscriptionError> {
    let raw = resp.text();
    if !(200..=299).contains(&resp.status) {
        return Err(classify_soniox_http(resp, &raw));
    }
    let json: serde_json::Value =
        serde_json::from_str(&raw).map_err(|e| TranscriptionError::Parse {
            message: format!("invalid JSON: {e}"),
        })?;
    json.get("id")
        .and_then(|v| v.as_str())
        .filter(|s| !s.is_empty())
        .map(str::to_string)
        .ok_or_else(|| TranscriptionError::Parse {
            message: format!("Soniox {op} response missing id"),
        })
}

/// Map a non-2xx Soniox response to a [`TranscriptionError`].
///
/// PARITY (`SonioxProvider.swift` `throwForFailure` + `SonioxService.cs`
/// `HandleErrorResponseAsync`):
/// - 401 / 403 → `Unauthorized`
/// - 413 → `FileTooLarge`
/// - 429 → `RateLimited { retry_after_secs }`
/// - 5xx → `ProviderUnavailable`
/// - 400 / 404 / 409 / other 4xx → `BadRequest` (message from `message`/body)
fn classify_soniox_http(resp: &HttpResponse, raw: &str) -> TranscriptionError {
    let status = resp.status;
    let json: Option<serde_json::Value> = serde_json::from_str(raw).ok();
    let message = json
        .as_ref()
        .and_then(|j| j.get("message"))
        .and_then(|v| v.as_str())
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .map(str::to_string);

    match status {
        401 | 403 => TranscriptionError::Unauthorized,
        413 => TranscriptionError::FileTooLarge,
        429 => TranscriptionError::RateLimited {
            retry_after_secs: resp
                .header("Retry-After")
                .and_then(|v| v.trim().parse::<u64>().ok()),
        },
        500..=599 => TranscriptionError::ProviderUnavailable { status },
        _ => TranscriptionError::BadRequest {
            status,
            message: message.unwrap_or_else(|| raw.chars().take(200).collect()),
        },
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn params() -> TranscribeParams {
        TranscribeParams {
            api_key: "sx-test".to_string(),
            model: "stt-async-v5".to_string(),
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

    // ---- upload ----

    #[test]
    fn upload_is_multipart_file_to_files_endpoint() {
        let req = build_upload_request(&params()).unwrap();
        assert_eq!(req.method, HttpMethod::Post);
        assert_eq!(req.url, "https://api.soniox.com/v1/files");
        assert!(req
            .headers
            .contains(&Header::new("Authorization", "Bearer sx-test")));
        match &req.body {
            Body::Multipart { boundary, parts } => {
                assert_eq!(boundary, MULTIPART_BOUNDARY);
                assert!(
                    matches!(&parts[0], Part::FileRef { field, mime, filename, .. }
                    if field == "file" && mime == "audio/mp4" && filename == "rec.m4a")
                );
            }
            other => panic!("expected multipart, got {other:?}"),
        }
    }

    #[test]
    fn parse_upload_extracts_id() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"id":"file_abc"}"#.to_vec(),
        };
        assert_eq!(parse_upload_response(&resp).unwrap(), "file_abc");
    }

    #[test]
    fn parse_upload_missing_id_is_parse_error() {
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

    // ---- create ----

    #[test]
    fn create_body_minimal() {
        let req = build_create_request(&params(), "file_abc").unwrap();
        assert_eq!(req.method, HttpMethod::Post);
        assert_eq!(req.url, "https://api.soniox.com/v1/transcriptions");
        let j = body_json(&req);
        assert_eq!(j["file_id"], "file_abc");
        assert_eq!(j["model"], "stt-async-v5");
        assert!(j.get("language_hints").is_none());
        assert!(j.get("context").is_none());
    }

    #[test]
    fn create_empty_model_defaults() {
        let mut p = params();
        p.model = "".to_string();
        let req = build_create_request(&p, "f").unwrap();
        assert_eq!(body_json(&req)["model"], "stt-async-v5");
    }

    #[test]
    fn create_explicit_language_adds_hints() {
        let mut p = params();
        p.language = Some("en".to_string());
        let req = build_create_request(&p, "f").unwrap();
        let j = body_json(&req);
        assert_eq!(j["language_hints"], serde_json::json!(["en"]));
    }

    #[test]
    fn create_auto_language_omits_hints() {
        let mut p = params();
        p.language = Some("AUTO".to_string());
        let req = build_create_request(&p, "f").unwrap();
        assert!(body_json(&req).get("language_hints").is_none());
    }

    #[test]
    fn create_context_joins_vocab_comma_space_and_dedups() {
        let mut p = params();
        p.vocabulary = vec![
            "  Rust  ".to_string(),
            "".to_string(),
            "UniFFI".to_string(),
            "rust".to_string(), // duplicate dropped
        ];
        let req = build_create_request(&p, "f").unwrap();
        assert_eq!(body_json(&req)["context"], "Rust, UniFFI");
    }

    #[test]
    fn create_context_sanitizes_vocab_terms() {
        // GOLDEN (F3): `<`/`>` dropped, internal whitespace collapsed, terms that
        // sanitize to empty dropped. Order/case otherwise preserved.
        let mut p = params();
        p.vocabulary = vec![
            "Rust<script>".to_string(),
            "  Multi  Space  ".to_string(),
            "rustscript".to_string(),
            "<>".to_string(),
        ];
        let req = build_create_request(&p, "f").unwrap();
        assert_eq!(body_json(&req)["context"], "Rustscript, Multi Space");
    }

    #[test]
    fn create_empty_vocab_omits_context() {
        let mut p = params();
        p.vocabulary = vec!["   ".to_string()];
        let req = build_create_request(&p, "f").unwrap();
        assert!(body_json(&req).get("context").is_none());
    }

    #[test]
    fn parse_create_extracts_id() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"id":"tx_123"}"#.to_vec(),
        };
        assert_eq!(parse_create_response(&resp).unwrap(), "tx_123");
    }

    // ---- status poll ----

    #[test]
    fn status_request_targets_transcription_id() {
        let req = build_status_request(&params(), "tx_123").unwrap();
        assert_eq!(req.method, HttpMethod::Get);
        assert_eq!(req.url, "https://api.soniox.com/v1/transcriptions/tx_123");
        assert_eq!(req.body, Body::Empty);
    }

    #[test]
    fn status_completed() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"id":"tx","status":"completed"}"#.to_vec(),
        };
        assert_eq!(parse_status_response(&resp).unwrap(), PollStatus::Completed);
    }

    #[test]
    fn status_processing_is_pending() {
        for s in ["queued", "processing", "weird-future-state"] {
            let resp = HttpResponse {
                status: 200,
                headers: vec![],
                body: format!(r#"{{"status":"{s}"}}"#).into_bytes(),
            };
            assert_eq!(parse_status_response(&resp).unwrap(), PollStatus::Pending);
        }
    }

    #[test]
    fn status_error_with_quota_message_maps_to_quota() {
        for msg in [
            "insufficient balance",
            "out of funds",
            "autopay failed",
            "quota exceeded",
            "rate limit reached",
        ] {
            let resp = HttpResponse {
                status: 200,
                headers: vec![],
                body: format!(r#"{{"status":"error","error_message":"{msg}"}}"#).into_bytes(),
            };
            assert_eq!(
                parse_status_response(&resp).unwrap_err(),
                TranscriptionError::QuotaExceeded,
                "msg={msg}"
            );
        }
    }

    #[test]
    fn status_error_generic_maps_to_bad_request() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"status":"error","error_message":"decode failed"}"#.to_vec(),
        };
        assert_eq!(
            parse_status_response(&resp).unwrap_err(),
            TranscriptionError::BadRequest {
                status: 200,
                message: "decode failed".to_string()
            }
        );
    }

    #[test]
    fn status_non_2xx_classified() {
        let resp = HttpResponse {
            status: 401,
            headers: vec![],
            body: b"{}".to_vec(),
        };
        assert_eq!(
            parse_status_response(&resp).unwrap_err(),
            TranscriptionError::Unauthorized
        );
    }

    // ---- transcript ----

    #[test]
    fn transcript_request_targets_transcript_path() {
        let req = build_transcript_request(&params(), "tx_123").unwrap();
        assert_eq!(req.method, HttpMethod::Get);
        assert_eq!(
            req.url,
            "https://api.soniox.com/v1/transcriptions/tx_123/transcript"
        );
    }

    #[test]
    fn parse_transcript_text() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"id":"tx","text":"hello soniox"}"#.to_vec(),
        };
        assert_eq!(
            parse_transcript_response(&resp).unwrap().text,
            "hello soniox"
        );
    }

    #[test]
    fn parse_transcript_final_transcript_fallback() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"final_transcript":"hi there"}"#.to_vec(),
        };
        assert_eq!(parse_transcript_response(&resp).unwrap().text, "hi there");
    }

    #[test]
    fn parse_transcript_plain_text_fallback() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: b"  bare text  ".to_vec(),
        };
        assert_eq!(parse_transcript_response(&resp).unwrap().text, "bare text");
    }

    #[test]
    fn parse_transcript_empty_is_no_speech() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"text":""}"#.to_vec(),
        };
        assert_eq!(
            parse_transcript_response(&resp).unwrap_err(),
            TranscriptionError::NoSpeech
        );
    }

    #[test]
    fn parse_transcript_429_rate_limited() {
        let resp = HttpResponse {
            status: 429,
            headers: vec![Header::new("Retry-After", "5")],
            body: b"{}".to_vec(),
        };
        assert_eq!(
            parse_transcript_response(&resp).unwrap_err(),
            TranscriptionError::RateLimited {
                retry_after_secs: Some(5)
            }
        );
    }

    // ---- cleanup ----

    #[test]
    fn delete_requests_target_correct_paths() {
        let dt = build_delete_transcription_request(&params(), "tx_1");
        assert_eq!(dt.method, HttpMethod::Delete);
        assert_eq!(dt.url, "https://api.soniox.com/v1/transcriptions/tx_1");
        let df = build_delete_file_request(&params(), "file_1");
        assert_eq!(df.method, HttpMethod::Delete);
        assert_eq!(df.url, "https://api.soniox.com/v1/files/file_1");
    }

    // ---- base url override ----

    #[test]
    fn base_url_override_applies_to_all_steps() {
        let mut p = params();
        p.base_url = Some("https://staging.soniox.test/v1/".to_string());
        assert_eq!(
            build_upload_request(&p).unwrap().url,
            "https://staging.soniox.test/v1/files"
        );
        assert_eq!(
            build_create_request(&p, "f").unwrap().url,
            "https://staging.soniox.test/v1/transcriptions"
        );
    }

    // ---- contract pair aliases ----

    #[test]
    fn contract_build_aliases_upload() {
        let a = build_transcribe_request(&params()).unwrap();
        let b = build_upload_request(&params()).unwrap();
        assert_eq!(a.url, b.url);
        assert_eq!(a.method, b.method);
    }

    #[test]
    fn contract_parse_aliases_transcript() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"text":"x"}"#.to_vec(),
        };
        assert_eq!(parse_transcribe_response(&resp).unwrap().text, "x");
    }
}
