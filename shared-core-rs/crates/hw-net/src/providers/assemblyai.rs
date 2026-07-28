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
    Body, Header, HttpMethod, HttpRequest, HttpResponse, Part, TranscribeParams, Transcript,
    TranscriptionError,
};
use crate::helpers::{keyword_boost_terms, multipart_field, multipart_file, resolve_mime};
use crate::providers::common::{classify_http, filename_of};

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

// ---------------------------------------------------------------------------
// Sync API — one blocking request, no upload/create/poll (short clips only)
// ---------------------------------------------------------------------------
//
// A separate product from the `v2` async API above: `POST` the audio once and
// get the finished transcript back in the same HTTP response (~134ms p50) —
// no `upload_url`, no job id, no polling. Capped at 120s of audio; the
// platform is responsible for gating on duration (Rust never sees it — it
// only builds/parses one request/response pair) and falling back to the async
// flow above on any sync failure, unknown/over-cap duration, or timeout.
//
// Contract verified against the AssemblyAI Python SDK source
// (`assemblyai/sync/v1/api.py`, `_base.py`, `types.py` — not a live call):
// - `POST https://sync.assemblyai.com/v1/transcribe` (the unprefixed
//   `/transcribe` predates a `/v1` prefix added later and is still served,
//   but new code should use the versioned path).
// - `multipart/form-data`: the audio file is field **`audio`** (NOT `files` —
//   an earlier assumption before checking the SDK source), plus an optional
//   `config` field carrying a JSON object (`language_codes`, `keyterms_prompt`,
//   `prompt`, `sample_rate`/`channels` for raw PCM, `timestamps`). We only
//   populate the fields this codebase's async create-request already sends
//   (language, keyterms, prompt) — no PCM/timestamps support needed today.
// - Headers: bare `Authorization: <key>` (matches the async steps), and
//   `X-AAI-Model` — the SDK always sends this, defaulting to the sync
//   product's only model, `universal-3-5-pro` ([`SyncSpeechModel`] has
//   exactly one member today; unlike async's `speech_models` priority list,
//   there is no alias table to reuse here).
// - Success (2xx): `{ "text", "words"?, "confidence", "audio_duration_ms",
//   "session_id", "request_time_ms"? }`. We only need `text`.
// - Failure (non-2xx): an RFC 9457 problem-details envelope
//   (`{"status","title","detail"}`), with older/alternate shapes
//   (`{"error_code","message"}` / `{"detail"}`) also tolerated by the SDK.
//   This is a different shape from the async `{"error": "..."}` body, so we
//   classify it separately in [`classify_sync_http`] rather than reusing
//   [`classify_http`].

/// Sync API base — a different host from [`BASE_URL`]. `params.base_url`
/// overrides it (tests/staging), same override field the async steps read;
/// don't build both a sync and an async request from one `params` value that
/// also sets `base_url`, since it applies to whichever builder is called.
pub const SYNC_BASE_URL: &str = "https://sync.assemblyai.com";

/// Canonical sync transcription path.
pub const SYNC_TRANSCRIBE_PATH: &str = "/v1/transcribe";

/// Sync API duration ceiling, in seconds. Platforms must gate on this (or a
/// conservative estimate) *before* calling [`build_sync_request`] — Rust has
/// no audio duration to check. Falls back to the async flow when unknown or
/// `>=` this value.
pub const SYNC_MAX_DURATION_SECS: f64 = 120.0;

/// The sync API's only supported model today (`X-AAI-Model`). Verified
/// against the Python SDK's `SyncSpeechModel` enum, which has exactly this
/// one member — if AssemblyAI adds more, give this an alias table like
/// [`resolve_model_alias`] instead of a single constant.
pub const SYNC_DEFAULT_MODEL: &str = "universal-3-5-pro";

/// Character budget (not term count) for the sync `config.keyterms_prompt`
/// array — the sync API docs cap it at 2048 total characters, unlike async's
/// per-model term-count caps ([`MAX_KEYTERMS_PRO`] / [`MAX_KEYTERMS_DEFAULT`]).
pub const SYNC_MAX_KEYTERMS_PROMPT_CHARS: usize = 2048;

fn sync_base(params: &TranscribeParams) -> String {
    params
        .base_url
        .as_deref()
        .map(|s| s.trim_end_matches('/').to_string())
        .unwrap_or_else(|| SYNC_BASE_URL.to_string())
}

/// Resolve the `X-AAI-Model` header value. Sync has exactly one model, so
/// this always returns [`SYNC_DEFAULT_MODEL`] regardless of `params.model`
/// (an async-only id like `universal-2`/`slam-1` would be meaningless to the
/// sync endpoint). Takes `params` for symmetry with the async resolvers and
/// so a future multi-model sync API only needs to change this function.
fn sync_model(_params: &TranscribeParams) -> &'static str {
    SYNC_DEFAULT_MODEL
}

/// Take terms in order, in order, up to a total-character budget (not a term
/// count) — stops before a term would push the running total over `budget`
/// rather than truncating a term mid-word.
fn cap_by_total_chars(terms: &[String], budget: usize) -> Vec<String> {
    let mut total = 0usize;
    let mut out = Vec::new();
    for term in terms {
        let len = term.chars().count();
        if total + len > budget {
            break;
        }
        total += len;
        out.push(term.clone());
    }
    out
}

/// Build the optional sync `config` JSON part from `params`. `None` when none
/// of language/vocabulary/prompt are set — the sync API's default (no
/// `config` part) is auto language detection with no keyterms, matching the
/// async create request's default branch.
fn sync_config_json(params: &TranscribeParams) -> Option<String> {
    let mut body = serde_json::Map::new();

    // language_codes: explicit code(s) only. The sync config has no
    // `language_detection` boolean like async's create body — omitting
    // `language_codes` entirely IS auto-detection, so "auto"/empty/absent
    // all mean "send nothing" here (unlike async, which sends an explicit
    // `language_detection: true`).
    if let Some(lang) = params.language.as_deref().map(str::trim) {
        if !lang.is_empty() && !lang.eq_ignore_ascii_case("auto") {
            let code: String = lang.to_lowercase().split(['-', '_']).next().unwrap_or("").into();
            if !code.is_empty() {
                body.insert("language_codes".into(), serde_json::json!([code]));
            }
        }
    }

    // keyterms_prompt: shared sanitize/dedup egress helper, capped by TOTAL
    // CHARACTERS (2048) rather than term count.
    let terms = keyword_boost_terms(&params.vocabulary, None);
    let capped = cap_by_total_chars(&terms, SYNC_MAX_KEYTERMS_PROMPT_CHARS);
    if !capped.is_empty() {
        body.insert(
            "keyterms_prompt".into(),
            serde_json::Value::Array(capped.into_iter().map(serde_json::Value::String).collect()),
        );
    }

    // prompt: the caller's custom instructions verbatim. Sync keeps
    // vocabulary and prompt as separate config fields (unlike the
    // OpenAI-style providers, which mash a vocabulary CSV into one `prompt`
    // string), so no CSV composition is needed here.
    if let Some(p) = params.prompt.as_deref().map(str::trim).filter(|s| !s.is_empty()) {
        body.insert("prompt".into(), serde_json::Value::String(p.to_string()));
    }

    if body.is_empty() {
        return None;
    }
    serde_json::to_string(&serde_json::Value::Object(body)).ok()
}

/// Build the **sync** transcription request: one multipart POST carrying the
/// audio file plus an optional `config` JSON part.
pub fn build_sync_request(params: &TranscribeParams) -> Result<HttpRequest, TranscriptionError> {
    let mime = params
        .audio_mime
        .clone()
        .unwrap_or_else(|| resolve_mime(&params.audio_path));

    let mut parts: Vec<Part> = vec![multipart_file(
        "audio",
        params.audio_path.clone(),
        mime,
        filename_of(&params.audio_path),
    )];
    if let Some(config_json) = sync_config_json(params) {
        parts.push(multipart_field("config", config_json));
    }

    Ok(HttpRequest {
        method: HttpMethod::Post,
        url: format!("{}{}", sync_base(params), SYNC_TRANSCRIBE_PATH),
        headers: vec![
            auth_header(&params.api_key),
            Header::new("X-AAI-Model", sync_model(params).to_string()),
        ],
        body: Body::Multipart {
            boundary: crate::helpers::MULTIPART_BOUNDARY.to_string(),
            parts,
        },
    })
}

/// Map a non-2xx sync response to a [`TranscriptionError`]. The sync API's
/// error envelope (`detail`/`message`/`title`) differs from the async
/// `{"error": "..."}` shape [`classify_http`] expects, so this extracts the
/// message independently before applying the same status-code mapping
/// (401/403 unauthorized, 402 quota, 413 too large, 429 rate limited, 5xx
/// unavailable, other 4xx bad request).
fn classify_sync_http(resp: &HttpResponse, raw: &str) -> TranscriptionError {
    let json: Option<serde_json::Value> = serde_json::from_str(raw).ok();
    let message = json.as_ref().and_then(|j| {
        j.get("detail")
            .or_else(|| j.get("message"))
            .and_then(|v| v.as_str())
            .map(str::to_string)
            .or_else(|| j.get("title").and_then(|v| v.as_str()).map(str::to_string))
    });

    match resp.status {
        401 | 403 => TranscriptionError::Unauthorized,
        402 => TranscriptionError::QuotaExceeded,
        413 => TranscriptionError::FileTooLarge,
        429 => TranscriptionError::RateLimited {
            retry_after_secs: resp.header("Retry-After").and_then(|v| v.trim().parse().ok()),
        },
        500..=599 => TranscriptionError::ProviderUnavailable { status: resp.status },
        _ => TranscriptionError::BadRequest {
            status: resp.status,
            message: message.unwrap_or_else(|| raw.chars().take(200).collect()),
        },
    }
}

/// Parse a **sync** response.
///
/// - Non-2xx → [`classify_sync_http`].
/// - 2xx with a missing/non-string `text` field → [`TranscriptionError::Parse`]
///   (defensive: an unexpected response shape is a signal to fall back to
///   async, not a crash).
/// - 2xx with empty `text` → [`TranscriptionError::NoSpeech`] (mirrors the
///   poll response's empty-transcript handling: legitimate silence, not a
///   reason to fall back and re-run the same clip through async).
pub fn parse_sync_response(resp: &HttpResponse) -> Result<Transcript, TranscriptionError> {
    let raw = resp.text();

    if !(200..=299).contains(&resp.status) {
        return Err(classify_sync_http(resp, &raw));
    }

    let json: serde_json::Value =
        serde_json::from_str(&raw).map_err(|e| TranscriptionError::Parse {
            message: format!("invalid sync JSON: {e}"),
        })?;

    let text = json
        .get("text")
        .and_then(|v| v.as_str())
        .ok_or_else(|| TranscriptionError::Parse {
            message: "sync response missing text".to_string(),
        })?;

    if text.is_empty() {
        return Err(TranscriptionError::NoSpeech);
    }

    Ok(Transcript {
        text: text.to_string(),
        ..Default::default()
    })
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

    // ---- sync API ------------------------------------------------------------

    fn sync_config(req: &HttpRequest) -> Option<serde_json::Value> {
        match &req.body {
            Body::Multipart { parts, .. } => parts.iter().find_map(|p| match p {
                Part::Field { name, value } if name == "config" => {
                    Some(serde_json::from_str(value).unwrap())
                }
                _ => None,
            }),
            other => panic!("expected Multipart body, got {other:?}"),
        }
    }

    #[test]
    fn sync_request_basic_shape() {
        let req = build_sync_request(&params()).unwrap();
        assert_eq!(req.method, HttpMethod::Post);
        assert_eq!(req.url, "https://sync.assemblyai.com/v1/transcribe");
        // bare key, no "Bearer "
        assert!(req
            .headers
            .contains(&Header::new("Authorization", "aai-key")));
        assert!(req
            .headers
            .contains(&Header::new("X-AAI-Model", "universal-3-5-pro")));
        match &req.body {
            Body::Multipart { parts, .. } => match &parts[0] {
                Part::FileRef { field, path, .. } => {
                    assert_eq!(field, "audio");
                    assert_eq!(path, "/tmp/rec.m4a");
                }
                other => panic!("expected FileRef first, got {other:?}"),
            },
            other => panic!("expected Multipart body, got {other:?}"),
        }
        // no language/vocabulary/prompt -> no config part at all
        assert!(matches!(&req.body, Body::Multipart { parts, .. } if parts.len() == 1));
    }

    #[test]
    fn sync_request_ignores_async_model_ids() {
        // The sync endpoint has exactly one model; an async-only id must not
        // leak into X-AAI-Model (it would be meaningless upstream).
        for model in ["universal-2", "slam-1", "universal-2-medical", ""] {
            let mut p = params();
            p.model = model.to_string();
            let req = build_sync_request(&p).unwrap();
            assert!(
                req.headers
                    .contains(&Header::new("X-AAI-Model", "universal-3-5-pro")),
                "model {model:?} should resolve to the sync default"
            );
        }
    }

    #[test]
    fn sync_request_language_becomes_language_codes() {
        let mut p = params();
        p.language = Some("en-US".to_string());
        let cfg = sync_config(&build_sync_request(&p).unwrap()).unwrap();
        assert_eq!(cfg["language_codes"], serde_json::json!(["en"]));
    }

    #[test]
    fn sync_request_auto_language_omits_config_language() {
        let mut p = params();
        p.language = Some("AUTO".to_string());
        // no vocabulary/prompt either -> no config part built at all
        assert!(matches!(
            &build_sync_request(&p).unwrap().body,
            Body::Multipart { parts, .. } if parts.len() == 1
        ));
    }

    #[test]
    fn sync_request_keyterms_capped_by_total_chars_not_count() {
        let mut p = params();
        // Many short terms whose combined length exceeds the 2048-char budget.
        p.vocabulary = (0..500).map(|i| format!("term-{i}")).collect();
        let cfg = sync_config(&build_sync_request(&p).unwrap()).unwrap();
        let terms = cfg["keyterms_prompt"].as_array().unwrap();
        let total_chars: usize = terms.iter().map(|t| t.as_str().unwrap().len()).sum();
        assert!(total_chars <= SYNC_MAX_KEYTERMS_PROMPT_CHARS);
        assert!(!terms.is_empty());
    }

    #[test]
    fn sync_request_prompt_and_keyterms_are_separate_fields() {
        let mut p = params();
        p.vocabulary = vec!["Rust".to_string()];
        p.prompt = Some("Be terse.".to_string());
        let cfg = sync_config(&build_sync_request(&p).unwrap()).unwrap();
        assert_eq!(cfg["keyterms_prompt"], serde_json::json!(["Rust"]));
        assert_eq!(cfg["prompt"], "Be terse.");
    }

    #[test]
    fn sync_request_base_url_override() {
        let mut p = params();
        p.base_url = Some("https://sync.staging.assemblyai.test".to_string());
        assert_eq!(
            build_sync_request(&p).unwrap().url,
            "https://sync.staging.assemblyai.test/v1/transcribe"
        );
    }

    #[test]
    fn parse_sync_response_extracts_text() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"text":"hello sync world","confidence":0.98,"audio_duration_ms":1200,"session_id":"s1"}"#
                .to_vec(),
        };
        assert_eq!(parse_sync_response(&resp).unwrap().text, "hello sync world");
    }

    #[test]
    fn parse_sync_response_empty_text_is_no_speech() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"text":"","confidence":1.0,"audio_duration_ms":500,"session_id":"s1"}"#
                .to_vec(),
        };
        assert_eq!(
            parse_sync_response(&resp).unwrap_err(),
            TranscriptionError::NoSpeech
        );
    }

    #[test]
    fn parse_sync_response_missing_text_is_parse_error_not_panic() {
        // Defensive: an unexpected/missing response shape must fall back to
        // async, never panic.
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"confidence":1.0}"#.to_vec(),
        };
        assert!(matches!(
            parse_sync_response(&resp).unwrap_err(),
            TranscriptionError::Parse { .. }
        ));
    }

    #[test]
    fn parse_sync_response_non_json_body_is_parse_error_not_panic() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: b"not json at all".to_vec(),
        };
        assert!(matches!(
            parse_sync_response(&resp).unwrap_err(),
            TranscriptionError::Parse { .. }
        ));
    }

    #[test]
    fn parse_sync_response_problem_details_error_shape() {
        // RFC 9457 problem-details envelope, per the AssemblyAI Python SDK's
        // `_error_from_response` — distinct from the async `{"error": "..."}`
        // shape.
        let resp = HttpResponse {
            status: 422,
            headers: vec![],
            body: br#"{"status":422,"title":"Audio Too Large","detail":"clip exceeds 120s"}"#
                .to_vec(),
        };
        match parse_sync_response(&resp).unwrap_err() {
            TranscriptionError::BadRequest { status, message } => {
                assert_eq!(status, 422);
                assert_eq!(message, "clip exceeds 120s");
            }
            other => panic!("expected BadRequest, got {other:?}"),
        }
    }

    #[test]
    fn parse_sync_response_401_unauthorized() {
        let resp = HttpResponse {
            status: 401,
            headers: vec![],
            body: br#"{"status":401,"title":"Unauthorized","detail":"bad key"}"#.to_vec(),
        };
        assert_eq!(
            parse_sync_response(&resp).unwrap_err(),
            TranscriptionError::Unauthorized
        );
    }

    #[test]
    fn parse_sync_response_429_rate_limited_with_retry_after() {
        let resp = HttpResponse {
            status: 429,
            headers: vec![Header::new("Retry-After", "7")],
            body: br#"{"status":429,"title":"Too Many Requests"}"#.to_vec(),
        };
        assert_eq!(
            parse_sync_response(&resp).unwrap_err(),
            TranscriptionError::RateLimited {
                retry_after_secs: Some(7)
            }
        );
    }

    #[test]
    fn parse_sync_response_5xx_provider_unavailable() {
        let resp = HttpResponse {
            status: 503,
            headers: vec![],
            body: b"upstream busy".to_vec(),
        };
        assert_eq!(
            parse_sync_response(&resp).unwrap_err(),
            TranscriptionError::ProviderUnavailable { status: 503 }
        );
    }
}
