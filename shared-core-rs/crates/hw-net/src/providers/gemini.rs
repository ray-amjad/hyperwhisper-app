//! Gemini transcription (sans-I/O) via the **Files API + `generateContent`**.
//!
//! Gemini has no dedicated STT endpoint — it transcribes audio through its
//! multimodal `generateContent` API. The shipped clients pick one of two
//! transports by estimated payload size: an *inline* base64 path (< 20 MB) and a
//! *Files API* path. **A sans-I/O core cannot base64 the audio** (that would
//! require reading the file across FFI, which the contract forbids), so this
//! module implements only the **Files API** path, which streams the audio bytes
//! from disk via [`Body::FileStream`]. The platform always drives Gemini through
//! these steps:
//!
//! 1. **Upload-start** — `POST /upload/v1beta/files?key=…` (resumable session).
//!    Parse the `X-Goog-Upload-URL` **response header** → the upload URL.
//! 2. **Upload-bytes** — `POST {uploadUrl}` streaming the raw audio. Parse the
//!    returned file resource → `{ name, uri, state }`.
//! 3. **Poll-file** — `GET /v1beta/{name}?key=…` until `state == "ACTIVE"`.
//! 4. **Generate** — `POST /v1beta/models/{model}:generateContent?key=…` with
//!    `file_data.file_uri`. Parse `candidates[].content.parts[].text`.
//! 5. (platform) best-effort `DELETE /v1beta/{name}?key=…` cleanup — see
//!    [`build_delete_request`].
//!
//! ## Auth
//! The API key is a **query parameter** (`?key=<key>`), not a header — matching
//! both reference impls (`buildGenerateContentURL` / `BuildGenerateContentUrl`).
//!
//! ## Parity references
//! - macOS `GeminiTranscriptionProvider.swift`
//! - Windows `GeminiTranscriptionService.cs`
//!
//! ## Parity notes / unification choices
//! - **Transport**: inline (< 20 MB) is omitted by design (no FFI audio read).
//!   We always use the Files API path. This is a documented divergence: on small
//!   files the platforms would inline, but the resulting transcript is identical
//!   and Gemini accepts Files-API uploads of any supported size.
//! - **Prompt**: built identically to both platforms —
//!   `"Transcribe this audio accurately. Output only the transcription text,
//!   nothing else."` then, when present, the language hint, the vocabulary hint,
//!   and the mode's custom prompt, joined by single spaces.
//! - **Response parse**: concatenates the `text` of every non-`thought` part
//!   (Windows behavior — strictly a superset of macOS's "first part" read, which
//!   matters for thinking models that emit a thought part first). Empty/blank →
//!   [`TranscriptionError::NoSpeech`]; trimmed.

use crate::contract::{
    Body, HttpMethod, HttpRequest, HttpResponse, TranscribeParams, Transcript, TranscriptionError,
};
use crate::helpers::{keyword_boost_terms, resolve_mime};

/// Gemini API root. `params.base_url` overrides it (tests/staging).
pub const API_ROOT: &str = "https://generativelanguage.googleapis.com";

/// Default model when the caller leaves `params.model` empty.
/// PARITY: macOS/Windows default = `gemini-2.5-flash`.
pub const DEFAULT_MODEL: &str = "gemini-2.5-flash";

/// Gemini's inline-payload limit (20 MB). Exposed for callers that still want to
/// reproduce the platform's transport decision; this module itself always uses
/// the Files API path. PARITY: `inlineRequestLimitBytes` / `InlineRequestLimitBytes`.
pub const INLINE_REQUEST_LIMIT_BYTES: u64 = 20 * 1024 * 1024;

fn root(params: &TranscribeParams) -> String {
    params
        .base_url
        .as_deref()
        .map(|s| s.trim_end_matches('/').to_string())
        .unwrap_or_else(|| API_ROOT.to_string())
}

fn model(params: &TranscribeParams) -> String {
    if params.model.trim().is_empty() {
        DEFAULT_MODEL.to_string()
    } else {
        params.model.clone()
    }
}

fn mime(params: &TranscribeParams) -> String {
    params
        .audio_mime
        .clone()
        .unwrap_or_else(|| resolve_mime(&params.audio_path))
}

/// Last path component of `path`, for the upload `display_name`.
fn filename_of(path: &str) -> String {
    path.rsplit(['/', '\\'])
        .next()
        .filter(|s| !s.is_empty())
        .unwrap_or("audio")
        .to_string()
}

/// Append `?key=<key>` to a URL (Gemini auth is a query param). Keeps parity with
/// macOS `URLComponents` / Windows string interpolation, which both place the raw
/// key in the query; the platform performs no extra escaping on the key bytes.
fn with_key(url: String, api_key: &str) -> String {
    let sep = if url.contains('?') { '&' } else { '?' };
    format!("{url}{sep}key={api_key}")
}

/// Build the Gemini transcription prompt, identical to both platforms.
///
/// PARITY: macOS `buildPrompt` / Windows `BuildPrompt`, with shared vocabulary
/// sanitizer/dedup/length-cap hardening before terms enter the wire prompt.
pub fn build_prompt(params: &TranscribeParams) -> String {
    let mut parts: Vec<String> = vec![
        "Transcribe this audio accurately. Output only the transcription text, nothing else."
            .to_string(),
    ];

    if let Some(lang) = params.language.as_deref().map(str::trim) {
        if !lang.is_empty() && !lang.eq_ignore_ascii_case("auto") {
            parts.push(format!("The audio is in {lang}."));
        }
    }

    let terms = keyword_boost_terms(&params.vocabulary, None);
    if !terms.is_empty() {
        parts.push(format!(
            "The following specialized terms may appear: {}. Use these exact spellings when they occur.",
            terms.join(", ")
        ));
    }

    if let Some(custom) = params.prompt.as_deref().map(str::trim) {
        if !custom.is_empty() {
            parts.push(custom.to_string());
        }
    }

    parts.join(" ")
}

// ---------------------------------------------------------------------------
// Step 1 — upload-start (resumable session)
// ---------------------------------------------------------------------------

/// Build the **upload-start** request. The response's `X-Goog-Upload-URL` header
/// carries the URL for [`build_upload_bytes_request`] — parse it with
/// [`parse_upload_start_response`].
pub fn build_upload_start_request(
    params: &TranscribeParams,
) -> Result<HttpRequest, TranscriptionError> {
    let url = with_key(
        format!("{}/upload/v1beta/files", root(params)),
        &params.api_key,
    );

    let body = serde_json::json!({
        "file": { "display_name": filename_of(&params.audio_path) }
    });
    let data = serde_json::to_vec(&body).map_err(|e| TranscriptionError::Parse {
        message: format!("failed to encode upload-start body: {e}"),
    })?;

    Ok(HttpRequest {
        method: HttpMethod::Post,
        url,
        headers: vec![
            crate::contract::Header::new("X-Goog-Upload-Protocol", "resumable"),
            crate::contract::Header::new("X-Goog-Upload-Command", "start"),
            // The platform fills in the real byte length; the contract carries no
            // file size, so we omit X-Goog-Upload-Header-Content-Length here and
            // let the platform add it from its own stat() before sending.
            crate::contract::Header::new("X-Goog-Upload-Header-Content-Type", mime(params)),
            crate::contract::Header::new("Content-Type", "application/json"),
        ],
        body: Body::Bytes {
            content_type: "application/json".to_string(),
            data,
        },
    })
}

/// Parse the **upload-start** response → the resumable upload URL from the
/// `X-Goog-Upload-URL` response header.
pub fn parse_upload_start_response(resp: &HttpResponse) -> Result<String, TranscriptionError> {
    if !(200..=299).contains(&resp.status) {
        return Err(classify_gemini(resp));
    }
    resp.header("X-Goog-Upload-URL")
        .filter(|s| !s.is_empty())
        .map(|s| s.to_string())
        .ok_or_else(|| TranscriptionError::Parse {
            message: "upload-start response missing X-Goog-Upload-URL header".to_string(),
        })
}

// ---------------------------------------------------------------------------
// Step 2 — upload-bytes (stream audio, finalize)
// ---------------------------------------------------------------------------

/// Build the **upload-bytes** request to the `upload_url` from step 1. The
/// platform streams the audio from `params.audio_path`. The `Content-Length`
/// header is added by the platform (the contract carries no file size).
pub fn build_upload_bytes_request(
    params: &TranscribeParams,
    upload_url: &str,
) -> Result<HttpRequest, TranscriptionError> {
    Ok(HttpRequest {
        method: HttpMethod::Post,
        url: upload_url.to_string(),
        headers: vec![
            crate::contract::Header::new("X-Goog-Upload-Offset", "0"),
            crate::contract::Header::new("X-Goog-Upload-Command", "upload, finalize"),
            crate::contract::Header::new("Content-Type", mime(params)),
        ],
        body: Body::FileStream {
            path: params.audio_path.clone(),
            content_type: mime(params),
        },
    })
}

/// A Gemini file resource (`{ name, uri, mimeType, state }`), parsed from the
/// upload-bytes response and from each poll response.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct GeminiFile {
    pub name: Option<String>,
    pub uri: Option<String>,
    pub mime_type: Option<String>,
    pub state: Option<String>,
}

fn parse_file_resource(raw: &str) -> Result<GeminiFile, TranscriptionError> {
    let json: serde_json::Value =
        serde_json::from_str(raw).map_err(|e| TranscriptionError::Parse {
            message: format!("invalid file JSON: {e}"),
        })?;
    // The body may be the bare resource or wrapped as `{ "file": { … } }`.
    let obj = json.get("file").unwrap_or(&json);
    let get = |k: &str| obj.get(k).and_then(|v| v.as_str()).map(str::to_string);
    Ok(GeminiFile {
        name: get("name"),
        uri: get("uri"),
        mime_type: get("mimeType"),
        state: get("state"),
    })
}

/// Parse the **upload-bytes** response → the uploaded [`GeminiFile`]
/// (requires `name` + `uri`).
pub fn parse_upload_bytes_response(resp: &HttpResponse) -> Result<GeminiFile, TranscriptionError> {
    if !(200..=299).contains(&resp.status) {
        return Err(classify_gemini(resp));
    }
    let file = parse_file_resource(&resp.text())?;
    if file.name.as_deref().unwrap_or("").is_empty() || file.uri.as_deref().unwrap_or("").is_empty()
    {
        return Err(TranscriptionError::Parse {
            message: "upload returned incomplete file metadata (name/uri)".to_string(),
        });
    }
    Ok(file)
}

// ---------------------------------------------------------------------------
// Step 3 — poll file until ACTIVE
// ---------------------------------------------------------------------------

/// The result of parsing one file-poll response.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum FilePollOutcome {
    /// File still `PROCESSING` (or an unknown non-terminal state) — keep polling.
    Pending,
    /// File `ACTIVE` — ready for `generateContent`.
    Active(GeminiFile),
}

/// Build the **poll-file** request for resource `name`
/// (e.g. `"files/abc-123"`).
pub fn build_poll_request(
    params: &TranscribeParams,
    name: &str,
) -> Result<HttpRequest, TranscriptionError> {
    let url = with_key(format!("{}/v1beta/{}", root(params), name), &params.api_key);
    Ok(HttpRequest {
        method: HttpMethod::Get,
        url,
        headers: vec![],
        body: Body::Empty,
    })
}

/// Parse a **poll-file** response.
///
/// - `state == "ACTIVE"` → [`FilePollOutcome::Active`].
/// - `state == "FAILED"` → [`TranscriptionError::ProviderUnavailable`]
///   (status 503, matching both platforms' "failed to process the audio").
/// - otherwise (`PROCESSING`/unknown) → [`FilePollOutcome::Pending`].
pub fn parse_poll_response(resp: &HttpResponse) -> Result<FilePollOutcome, TranscriptionError> {
    if !(200..=299).contains(&resp.status) {
        return Err(classify_gemini(resp));
    }
    let file = parse_file_resource(&resp.text())?;
    match file.state.as_deref().map(str::to_uppercase).as_deref() {
        Some("ACTIVE") => Ok(FilePollOutcome::Active(file)),
        Some("FAILED") => Err(TranscriptionError::ProviderUnavailable { status: 503 }),
        _ => Ok(FilePollOutcome::Pending),
    }
}

// ---------------------------------------------------------------------------
// Step 4 — generateContent
// ---------------------------------------------------------------------------

/// Build the **generateContent** request referencing the ACTIVE file's `uri`.
///
/// Body: `{ contents: [ { parts: [ {text: prompt}, {file_data:{mime_type,
/// file_uri}} ] } ] }`.
pub fn build_generate_request(
    params: &TranscribeParams,
    file: &GeminiFile,
) -> Result<HttpRequest, TranscriptionError> {
    let file_uri = file
        .uri
        .as_deref()
        .filter(|s| !s.is_empty())
        .ok_or_else(|| TranscriptionError::Parse {
            message: "generate request requires a file uri".to_string(),
        })?;
    // Fall back to the resolved audio MIME if the file resource omitted one.
    let mime_type = file
        .mime_type
        .clone()
        .filter(|s| !s.is_empty())
        .unwrap_or_else(|| mime(params));

    let url = with_key(
        format!(
            "{}/v1beta/models/{}:generateContent",
            root(params),
            model(params)
        ),
        &params.api_key,
    );

    let body = serde_json::json!({
        "contents": [{
            "parts": [
                { "text": build_prompt(params) },
                { "file_data": { "mime_type": mime_type, "file_uri": file_uri } }
            ]
        }]
    });
    let data = serde_json::to_vec(&body).map_err(|e| TranscriptionError::Parse {
        message: format!("failed to encode generate body: {e}"),
    })?;

    Ok(HttpRequest {
        method: HttpMethod::Post,
        url,
        headers: vec![crate::contract::Header::new(
            "Content-Type",
            "application/json",
        )],
        body: Body::Bytes {
            content_type: "application/json".to_string(),
            data,
        },
    })
}

/// Parse the **generateContent** response →
/// `candidates[0].content.parts[*].text` (concatenated, skipping `thought`
/// parts), trimmed. Empty/blank → [`TranscriptionError::NoSpeech`].
pub fn parse_generate_response(resp: &HttpResponse) -> Result<Transcript, TranscriptionError> {
    if !(200..=299).contains(&resp.status) {
        return Err(classify_gemini(resp));
    }
    let json: serde_json::Value =
        serde_json::from_str(&resp.text()).map_err(|e| TranscriptionError::Parse {
            message: format!("invalid generateContent JSON: {e}"),
        })?;

    let parts = json
        .get("candidates")
        .and_then(|c| c.as_array())
        .and_then(|c| c.first())
        .and_then(|c| c.get("content"))
        .and_then(|c| c.get("parts"))
        .and_then(|p| p.as_array());

    let mut text = String::new();
    if let Some(parts) = parts {
        for part in parts {
            // Skip "thought" parts (thinking models emit them first).
            if part.get("thought").and_then(|v| v.as_bool()) == Some(true) {
                continue;
            }
            if let Some(s) = part.get("text").and_then(|v| v.as_str()) {
                text.push_str(s);
            }
        }
    }

    let trimmed = text.trim();
    if trimmed.is_empty() {
        return Err(TranscriptionError::NoSpeech);
    }
    Ok(Transcript {
        text: trimmed.to_string(),
        ..Default::default()
    })
}

// ---------------------------------------------------------------------------
// Step 5 — delete (best-effort cleanup, driven by the platform)
// ---------------------------------------------------------------------------

/// Build the best-effort **delete** request for file resource `name`.
pub fn build_delete_request(
    params: &TranscribeParams,
    name: &str,
) -> Result<HttpRequest, TranscriptionError> {
    let url = with_key(format!("{}/v1beta/{}", root(params), name), &params.api_key);
    Ok(HttpRequest {
        method: HttpMethod::Delete,
        url,
        headers: vec![],
        body: Body::Empty,
    })
}

// ---------------------------------------------------------------------------
// Error classification
// ---------------------------------------------------------------------------

/// Map a non-2xx Gemini response to a [`TranscriptionError`].
///
/// PARITY: Gemini returns **400 for a bad/missing API key** (not 401). Both
/// reference impls treat 400/401/403 as `Unauthorized`. (macOS additionally
/// inspects the 400 body for non-auth "invalid request" cases, but in the STT
/// flow a 400 is overwhelmingly an auth/credential problem and both clients
/// surface it as such — we map 400/401/403 → `Unauthorized` for parity with
/// Windows and macOS's dominant path.) 429 → `RateLimited`; 5xx →
/// `ProviderUnavailable`; other 4xx → `BadRequest`.
fn classify_gemini(resp: &HttpResponse) -> TranscriptionError {
    let raw = resp.text();
    let msg = gemini_error_message(&raw);
    match resp.status {
        400 | 401 | 403 => TranscriptionError::Unauthorized,
        429 => TranscriptionError::RateLimited {
            retry_after_secs: resp
                .header("Retry-After")
                .and_then(|v| v.trim().parse::<u64>().ok()),
        },
        500..=599 => TranscriptionError::ProviderUnavailable {
            status: resp.status,
        },
        status => TranscriptionError::BadRequest {
            status,
            message: msg.unwrap_or_else(|| raw.chars().take(200).collect()),
        },
    }
}

/// Best-effort `error.message` / `error` (string) extraction from a Gemini error body.
fn gemini_error_message(raw: &str) -> Option<String> {
    let json: serde_json::Value = serde_json::from_str(raw).ok()?;
    let err = json.get("error")?;
    if let Some(m) = err.get("message").and_then(|v| v.as_str()) {
        return Some(m.to_string());
    }
    err.as_str().map(str::to_string)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::contract::Header;

    fn params() -> TranscribeParams {
        TranscribeParams {
            api_key: "gem-key".to_string(),
            model: "gemini-2.5-flash".to_string(),
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

    // ---- prompt ------------------------------------------------------------

    #[test]
    fn prompt_base_only() {
        assert_eq!(
            build_prompt(&params()),
            "Transcribe this audio accurately. Output only the transcription text, nothing else."
        );
    }

    #[test]
    fn prompt_with_language_vocab_and_custom() {
        let mut p = params();
        p.language = Some("en".to_string());
        p.vocabulary = vec![
            "Rust".to_string(),
            "  UniFFI<script> ".to_string(),
            "rust".to_string(),
            "".to_string(),
        ];
        p.prompt = Some("  Keep it formal.  ".to_string());
        assert_eq!(
            build_prompt(&p),
            "Transcribe this audio accurately. Output only the transcription text, nothing else. \
             The audio is in en. \
             The following specialized terms may appear: Rust, UniFFIscript. Use these exact spellings when they occur. \
             Keep it formal."
        );
    }

    #[test]
    fn prompt_auto_language_omitted() {
        let mut p = params();
        p.language = Some("AUTO".to_string());
        assert!(!build_prompt(&p).contains("The audio is in"));
    }

    // ---- step 1: upload-start ---------------------------------------------

    #[test]
    fn upload_start_request_shape() {
        let req = build_upload_start_request(&params()).unwrap();
        assert_eq!(req.method, HttpMethod::Post);
        assert_eq!(
            req.url,
            "https://generativelanguage.googleapis.com/upload/v1beta/files?key=gem-key"
        );
        assert!(req
            .headers
            .contains(&Header::new("X-Goog-Upload-Protocol", "resumable")));
        assert!(req
            .headers
            .contains(&Header::new("X-Goog-Upload-Command", "start")));
        assert!(req.headers.contains(&Header::new(
            "X-Goog-Upload-Header-Content-Type",
            "audio/mp4"
        )));
        let j = body_json(&req);
        assert_eq!(j["file"]["display_name"], "rec.m4a");
    }

    #[test]
    fn parse_upload_start_reads_header() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![Header::new(
                "X-Goog-Upload-URL",
                "https://upload.googleapis.com/resumable/xyz",
            )],
            body: b"".to_vec(),
        };
        assert_eq!(
            parse_upload_start_response(&resp).unwrap(),
            "https://upload.googleapis.com/resumable/xyz"
        );
    }

    #[test]
    fn parse_upload_start_header_case_insensitive() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![Header::new("x-goog-upload-url", "https://up/abc")],
            body: b"".to_vec(),
        };
        assert_eq!(
            parse_upload_start_response(&resp).unwrap(),
            "https://up/abc"
        );
    }

    #[test]
    fn parse_upload_start_missing_header_is_parse_error() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: b"".to_vec(),
        };
        assert!(matches!(
            parse_upload_start_response(&resp).unwrap_err(),
            TranscriptionError::Parse { .. }
        ));
    }

    #[test]
    fn parse_upload_start_400_is_unauthorized() {
        // Gemini quirk: bad key returns 400.
        let resp = HttpResponse {
            status: 400,
            headers: vec![],
            body: br#"{"error":{"message":"API key not valid"}}"#.to_vec(),
        };
        assert_eq!(
            parse_upload_start_response(&resp).unwrap_err(),
            TranscriptionError::Unauthorized
        );
    }

    // ---- step 2: upload-bytes ---------------------------------------------

    #[test]
    fn upload_bytes_request_streams_file() {
        let req = build_upload_bytes_request(&params(), "https://upload/xyz").unwrap();
        assert_eq!(req.url, "https://upload/xyz");
        assert!(req
            .headers
            .contains(&Header::new("X-Goog-Upload-Command", "upload, finalize")));
        assert!(req
            .headers
            .contains(&Header::new("X-Goog-Upload-Offset", "0")));
        assert!(req
            .headers
            .contains(&Header::new("Content-Type", "audio/mp4")));
        match &req.body {
            Body::FileStream { path, content_type } => {
                assert_eq!(path, "/tmp/rec.m4a");
                assert_eq!(content_type, "audio/mp4");
            }
            other => panic!("expected FileStream, got {other:?}"),
        }
    }

    #[test]
    fn parse_upload_bytes_extracts_file() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"file":{"name":"files/abc","uri":"https://gen/files/abc","state":"PROCESSING","mimeType":"audio/mp4"}}"#.to_vec(),
        };
        let f = parse_upload_bytes_response(&resp).unwrap();
        assert_eq!(f.name.as_deref(), Some("files/abc"));
        assert_eq!(f.uri.as_deref(), Some("https://gen/files/abc"));
        assert_eq!(f.state.as_deref(), Some("PROCESSING"));
    }

    #[test]
    fn parse_upload_bytes_bare_resource_no_envelope() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"name":"files/x","uri":"https://u/x","state":"ACTIVE"}"#.to_vec(),
        };
        let f = parse_upload_bytes_response(&resp).unwrap();
        assert_eq!(f.name.as_deref(), Some("files/x"));
    }

    #[test]
    fn parse_upload_bytes_missing_uri_is_parse_error() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"file":{"name":"files/abc"}}"#.to_vec(),
        };
        assert!(matches!(
            parse_upload_bytes_response(&resp).unwrap_err(),
            TranscriptionError::Parse { .. }
        ));
    }

    // ---- step 3: poll ------------------------------------------------------

    #[test]
    fn poll_request_targets_name() {
        let req = build_poll_request(&params(), "files/abc").unwrap();
        assert_eq!(req.method, HttpMethod::Get);
        assert_eq!(
            req.url,
            "https://generativelanguage.googleapis.com/v1beta/files/abc?key=gem-key"
        );
        assert!(matches!(req.body, Body::Empty));
    }

    #[test]
    fn poll_processing_is_pending() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"name":"files/abc","state":"PROCESSING"}"#.to_vec(),
        };
        assert_eq!(
            parse_poll_response(&resp).unwrap(),
            FilePollOutcome::Pending
        );
    }

    #[test]
    fn poll_active_returns_file() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"name":"files/abc","uri":"https://u/abc","state":"ACTIVE","mimeType":"audio/mp4"}"#.to_vec(),
        };
        match parse_poll_response(&resp).unwrap() {
            FilePollOutcome::Active(f) => {
                assert_eq!(f.uri.as_deref(), Some("https://u/abc"));
            }
            other => panic!("expected Active, got {other:?}"),
        }
    }

    #[test]
    fn poll_failed_is_provider_unavailable() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"name":"files/abc","state":"FAILED"}"#.to_vec(),
        };
        assert_eq!(
            parse_poll_response(&resp).unwrap_err(),
            TranscriptionError::ProviderUnavailable { status: 503 }
        );
    }

    // ---- step 4: generate --------------------------------------------------

    #[test]
    fn generate_request_shape() {
        let file = GeminiFile {
            name: Some("files/abc".to_string()),
            uri: Some("https://u/abc".to_string()),
            mime_type: Some("audio/mp4".to_string()),
            state: Some("ACTIVE".to_string()),
        };
        let req = build_generate_request(&params(), &file).unwrap();
        assert_eq!(
            req.url,
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key=gem-key"
        );
        let j = body_json(&req);
        let parts = &j["contents"][0]["parts"];
        assert!(parts[0]["text"]
            .as_str()
            .unwrap()
            .starts_with("Transcribe this audio"));
        assert_eq!(parts[1]["file_data"]["file_uri"], "https://u/abc");
        assert_eq!(parts[1]["file_data"]["mime_type"], "audio/mp4");
    }

    #[test]
    fn generate_request_falls_back_to_resolved_mime() {
        let file = GeminiFile {
            name: Some("files/abc".to_string()),
            uri: Some("https://u/abc".to_string()),
            mime_type: None,
            state: Some("ACTIVE".to_string()),
        };
        let mut p = params();
        p.audio_path = "/tmp/rec.wav".to_string();
        let j = body_json(&build_generate_request(&p, &file).unwrap());
        assert_eq!(
            j["contents"][0]["parts"][1]["file_data"]["mime_type"],
            "audio/wav"
        );
    }

    #[test]
    fn generate_request_empty_model_defaults() {
        let file = GeminiFile {
            uri: Some("https://u/abc".to_string()),
            ..Default::default()
        };
        let mut p = params();
        p.model = "".to_string();
        let req = build_generate_request(&p, &file).unwrap();
        assert!(req.url.contains("/models/gemini-2.5-flash:generateContent"));
    }

    #[test]
    fn parse_generate_extracts_text() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"candidates":[{"content":{"parts":[{"text":"hello world"}]}}]}"#.to_vec(),
        };
        assert_eq!(parse_generate_response(&resp).unwrap().text, "hello world");
    }

    #[test]
    fn parse_generate_concatenates_and_skips_thought() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"candidates":[{"content":{"parts":[{"thought":true,"text":"thinking..."},{"text":"hello "},{"text":"world"}]}}]}"#.to_vec(),
        };
        assert_eq!(parse_generate_response(&resp).unwrap().text, "hello world");
    }

    #[test]
    fn parse_generate_trims() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"candidates":[{"content":{"parts":[{"text":"  spaced  "}]}}]}"#.to_vec(),
        };
        assert_eq!(parse_generate_response(&resp).unwrap().text, "spaced");
    }

    #[test]
    fn parse_generate_empty_is_no_speech() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"candidates":[{"content":{"parts":[{"text":""}]}}]}"#.to_vec(),
        };
        assert_eq!(
            parse_generate_response(&resp).unwrap_err(),
            TranscriptionError::NoSpeech
        );
    }

    #[test]
    fn parse_generate_no_candidates_is_no_speech() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"candidates":[]}"#.to_vec(),
        };
        assert_eq!(
            parse_generate_response(&resp).unwrap_err(),
            TranscriptionError::NoSpeech
        );
    }

    #[test]
    fn parse_generate_429_rate_limited() {
        let resp = HttpResponse {
            status: 429,
            headers: vec![Header::new("Retry-After", "30")],
            body: br#"{"error":{"message":"quota"}}"#.to_vec(),
        };
        assert_eq!(
            parse_generate_response(&resp).unwrap_err(),
            TranscriptionError::RateLimited {
                retry_after_secs: Some(30)
            }
        );
    }

    #[test]
    fn parse_generate_500_provider_unavailable() {
        let resp = HttpResponse {
            status: 503,
            headers: vec![],
            body: b"down".to_vec(),
        };
        assert_eq!(
            parse_generate_response(&resp).unwrap_err(),
            TranscriptionError::ProviderUnavailable { status: 503 }
        );
    }

    // ---- step 5: delete ----------------------------------------------------

    #[test]
    fn delete_request_shape() {
        let req = build_delete_request(&params(), "files/abc").unwrap();
        assert_eq!(req.method, HttpMethod::Delete);
        assert_eq!(
            req.url,
            "https://generativelanguage.googleapis.com/v1beta/files/abc?key=gem-key"
        );
    }

    // ---- base_url override -------------------------------------------------

    #[test]
    fn base_url_override_applies() {
        let mut p = params();
        p.base_url = Some("https://gem.staging.test/".to_string());
        assert_eq!(
            build_upload_start_request(&p).unwrap().url,
            "https://gem.staging.test/upload/v1beta/files?key=gem-key"
        );
        assert_eq!(
            build_poll_request(&p, "files/x").unwrap().url,
            "https://gem.staging.test/v1beta/files/x?key=gem-key"
        );
    }
}
