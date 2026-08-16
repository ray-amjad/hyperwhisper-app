//! Sync API — one blocking request, no upload/create/poll (short clips only).
//!
//! A separate product from the async `v2` API in [`super::async_flow`]:
//! `POST` the audio once and get the finished transcript back in the same
//! HTTP response (~134ms p50) — no `upload_url`, no job id, no polling.
//! Capped at 120s of audio; the platform is responsible for gating on
//! duration (Rust never sees it — it only builds/parses one request/response
//! pair) and falling back to the async flow above on any sync failure,
//! unknown/over-cap duration, or timeout.
//!
//! Contract verified against the AssemblyAI Python SDK source
//! (`assemblyai/sync/v1/api.py`, `_base.py`, `types.py` — not a live call):
//! - `POST https://sync.assemblyai.com/v1/transcribe` (the unprefixed
//!   `/transcribe` predates a `/v1` prefix added later and is still served,
//!   but new code should use the versioned path).
//! - `multipart/form-data`: the audio file is field **`audio`** (NOT `files` —
//!   an earlier assumption before checking the SDK source), plus an optional
//!   `config` field carrying a JSON object (`language_codes`, `keyterms_prompt`,
//!   `prompt`, `sample_rate`/`channels` for raw PCM, `timestamps`). We only
//!   populate the fields this codebase's async create-request already sends
//!   (language, keyterms, prompt) — no PCM/timestamps support needed today.
//! - Headers: bare `Authorization: <key>` (matches the async steps), and
//!   `X-AAI-Model` — the SDK always sends this, defaulting to the sync
//!   product's only model, `universal-3-5-pro` ([`SYNC_DEFAULT_MODEL`] is the
//!   only member today; unlike async's `speech_models` priority list, there
//!   is no alias table to reuse here).
//! - Success (2xx): `{ "text", "words"?, "confidence", "audio_duration_ms",
//!   "session_id", "request_time_ms"? }`. We only need `text`.
//! - Failure (non-2xx): an RFC 9457 problem-details envelope
//!   (`{"status","title","detail"}`), with older/alternate shapes
//!   (`{"error_code","message"}` / `{"detail"}`) also tolerated by the SDK.
//!   This is a different shape from the async `{"error": "..."}` body, so
//!   [`classify_sync_http`] supplies its own message extractor to the shared
//!   status-code skeleton in `providers::common::classify_http_with_message`
//!   rather than reusing the async `{"error": "..."}`-shaped extractor.
//!
//! ## Language: no `language_codes` is NOT auto-detect
//! Unlike async's explicit `language_detection: true`, the sync API has no
//! `language_detection` boolean — but an OMITTED `language_codes` does **not**
//! mean auto-detect either. AssemblyAI's sync product defaults an omitted
//! language list to **English**. Sending nothing for a non-English clip would
//! silently return an English-biased (or empty) transcript instead of the
//! accurate multi-language result async would produce. Rather than guess at
//! undocumented sync auto-detect behavior, [`build_sync_request`] refuses to
//! build a request at all when the caller's language is absent/`"auto"` — see
//! [`build_sync_request`]'s doc comment. Every platform already treats a
//! build failure from this function as a fallback-to-async signal, not a hard
//! error, so this safely routes auto-language clips to async instead.

use crate::contract::{
    Body, Header, HttpMethod, HttpRequest, HttpResponse, Part, TranscribeParams, Transcript,
    TranscriptionError,
};
use crate::helpers::{keyword_boost_terms, multipart_field, multipart_file, resolve_mime};
use crate::providers::common::{classify_http_with_message, filename_of};

use super::{auth_header, filter_keyterm_words, request_params};

/// Sync API base — a different host from [`super::BASE_URL`]. `params.base_url`
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

/// Sync HTTP call timeout budget, in milliseconds. AssemblyAI's sync p50
/// latency is ~134ms, so even this leaves enormous headroom for a
/// genuinely-slow-but-successful response. Deliberately tighter than an
/// earlier 40s: a stalled/slow sync call fully blocks the async fallback for
/// up to this long (sequential sync-then-async, not a concurrent race — that
/// redesign is out of scope here), so a smaller-but-still-safe budget caps
/// the worst-case latency regression vs. pre-sync straight-to-async behavior.
/// Exported via FFI (`assemblyai_sync_timeout_ms`) so Swift/C# consume ONE
/// shared constant instead of each hardcoding their own copy-pasted literal
/// (the cloud TS backend has no FFI access to Rust and keeps its own local
/// copy of this same value).
pub const SYNC_TIMEOUT_MS: u64 = 15_000;

/// The sync API's only supported model today (`X-AAI-Model`). Verified
/// against the Python SDK's `SyncSpeechModel` enum, which has exactly this
/// one member — if AssemblyAI adds more, give this an alias table like
/// [`super::resolve_model_alias`] instead of a single constant.
pub const SYNC_DEFAULT_MODEL: &str = "universal-3-5-pro";

/// Character budget (not term count) for the sync `config.keyterms_prompt`
/// array — the sync API docs cap it at 2048 total characters, unlike async's
/// per-model term-count caps ([`super::MAX_KEYTERMS_PRO`] / [`super::MAX_KEYTERMS_DEFAULT`]).
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

/// `true` when `params.language` is an explicit, non-"auto" language code —
/// the only case [`build_sync_request`] will build a request for. See the
/// module doc's "Language" section for why absent/"auto" is NOT eligible.
fn has_explicit_language(params: &TranscribeParams) -> bool {
    params
        .language
        .as_deref()
        .map(str::trim)
        .is_some_and(|s| !s.is_empty() && !s.eq_ignore_ascii_case("auto"))
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
/// of language/vocabulary/prompt are set.
///
/// Only called by [`build_sync_request`] after it has already confirmed
/// `params.language` is explicit (see [`has_explicit_language`]), so the
/// `language_codes` branch below is always taken in practice — the guard
/// stays defensive (rather than assuming the caller checked) in case this
/// function is ever called directly.
fn sync_config_json(params: &TranscribeParams) -> Option<String> {
    let mut body = serde_json::Map::new();

    if let Some(lang) = params.language.as_deref().map(str::trim) {
        if !lang.is_empty() && !lang.eq_ignore_ascii_case("auto") {
            let code: String = lang.to_lowercase().split(['-', '_']).next().unwrap_or("").into();
            if !code.is_empty() {
                body.insert("language_codes".into(), serde_json::json!([code]));
            }
        }
    }

    // keyterms_prompt: shared sanitize/dedup egress helper, THEN the same
    // per-term word-count filter async applies (drop phrases over
    // MAX_KEYTERM_WORDS words — a vocab term async would silently drop must
    // not slip through unfiltered here just because sync caps by characters
    // instead of term count), THEN capped by TOTAL CHARACTERS (2048).
    let terms: Vec<String> = filter_keyterm_words(keyword_boost_terms(&params.vocabulary, None));
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

/// `true` when `mime` denotes a WAV container — the only container
/// AssemblyAI's sync endpoint is documented to accept (WAV or raw S16LE PCM;
/// no platform caller here produces raw PCM, so WAV is the only container
/// this can cheaply confirm sync accepts). A substring match, not an exact
/// `"audio/wav"` comparison, mirrors the cloud TS mirror's
/// `isWavContentType` so `audio/wave` / `audio/x-wav` variants are still
/// recognized as WAV.
fn is_wav_mime(mime: &str) -> bool {
    mime.to_lowercase().contains("wav")
}

/// The Content-Type put on the `audio` multipart part, ALWAYS — the resolved
/// MIME is what [`is_wav_mime`] gates on, never what goes on the wire.
///
/// Sync matches this value against a fixed set and rejects anything else with
/// `{"status":415,"title":"Unsupported Media Type","detail":"unsupported audio
/// Content-Type: '<value>'"}` BEFORE decoding a byte, so an unrecognized
/// spelling of WAV fails 100% of the time regardless of the audio itself.
///
/// Measured against the live API (2026-08-16, one 7s 16 kHz mono WAV, same
/// bytes each time): `audio/wav`, `audio/wave` and `audio/x-wav` -> 200;
/// `audio/vnd.wave` and `application/octet-stream` -> 415. The multipart
/// filename has no effect either way.
///
/// `audio/vnd.wave` is not hypothetical: it is what macOS's
/// `UTType.preferredMIMEType` returns for a .wav file, so it is the value
/// `AudioMimeTypeResolver` puts in `params.audio_mime` on every macOS
/// recording. Until this constant existed, EVERY macOS sync attempt 415'd and
/// fell through to the async upload/create/poll flow. Mirrors the cloud TS
/// mirror's `SYNC_AUDIO_CONTENT_TYPE`.
const SYNC_AUDIO_MIME: &str = "audio/wav";

/// Build the **sync** transcription request: one multipart POST carrying the
/// audio file plus an optional `config` JSON part.
///
/// Returns [`TranscriptionError::Parse`] WITHOUT building anything when:
/// - `params.language` is absent/`"auto"` — see the module doc's "Language"
///   section.
/// - `params.model` is a medical variant (`-medical` suffix) — Medical Mode
///   is an async-only `domain: "medical-v1"` add-on with no sync equivalent.
///   This is a defense-in-depth check: every platform call site is already
///   supposed to gate medical models out of the sync path itself, but this
///   builder enforces it independently so a caller that forgets its own gate
///   still can't silently build a medical-losing sync request.
/// - the resolved audio MIME isn't WAV — the sync endpoint only accepts WAV
///   or raw S16LE PCM, unlike async's upload endpoint which accepts any
///   container. Forwarding a compressed container (MP3, M4A) would get
///   rejected by the sync endpoint before falling back to async, wasting a
///   round-trip on what's likely the most common input format.
///
/// Every platform call site already treats any error from this function as a
/// signal to fall back to the async flow, so this is the single place each of
/// these gates needs to live for all three platforms to pick it up.
pub fn build_sync_request(params: &TranscribeParams) -> Result<HttpRequest, TranscriptionError> {
    if !has_explicit_language(params) {
        return Err(TranscriptionError::Parse {
            message: "sync API requires an explicit language (an absent/\"auto\" language \
                      defaults to English server-side, not auto-detection) — falling back to \
                      async"
                .to_string(),
        });
    }

    let (_, medical) = request_params(&params.model);
    if medical {
        return Err(TranscriptionError::Parse {
            message: "sync API has no medical/domain support (Medical Mode is an async-only \
                      add-on) — falling back to async"
                .to_string(),
        });
    }

    let mime = params
        .audio_mime
        .clone()
        .unwrap_or_else(|| resolve_mime(&params.audio_path));

    if !is_wav_mime(&mime) {
        return Err(TranscriptionError::Parse {
            message: format!(
                "sync API only accepts WAV audio (resolved MIME {mime:?}) — falling back to async"
            ),
        });
    }

    let mut parts: Vec<Part> = vec![multipart_file(
        "audio",
        params.audio_path.clone(),
        // Canonical `audio/wav`, NOT the resolved `mime` — see SYNC_AUDIO_MIME.
        SYNC_AUDIO_MIME.to_string(),
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

/// Extract the sync API's RFC 9457 problem-details message (`detail` /
/// `message` / `title`, falling back to a raw-body preview) — the
/// message-extraction half of [`classify_sync_http`]'s status-code mapping
/// that differs from the async `{"error": "..."}` shape.
fn sync_error_message(json: Option<&serde_json::Value>, raw: &str) -> String {
    json.and_then(|j| {
        j.get("detail")
            .or_else(|| j.get("message"))
            .and_then(|v| v.as_str())
            .map(str::to_string)
            .or_else(|| j.get("title").and_then(|v| v.as_str()).map(str::to_string))
    })
    .unwrap_or_else(|| raw.chars().take(200).collect())
}

/// Map a non-2xx sync response to a [`TranscriptionError`]. Delegates the
/// status-code skeleton (401/403/402/408/413/429/5xx/other) to the shared
/// `providers::common::classify_http_with_message`, supplying only
/// [`sync_error_message`] as the body-message extractor — the one part that
/// actually differs from async's `classify_http`.
fn classify_sync_http(resp: &HttpResponse, raw: &str) -> TranscriptionError {
    classify_http_with_message(resp, raw, sync_error_message)
}

/// Parse a **sync** response.
///
/// - Non-2xx → [`classify_sync_http`].
/// - 2xx with a missing/non-string `text` field → [`TranscriptionError::Parse`]
///   (defensive: an unexpected response shape is a signal to fall back to
///   async, not a crash).
/// - 2xx with empty/whitespace-only `text` → [`TranscriptionError::NoSpeech`]
///   (mirrors the poll response's empty-transcript handling: legitimate
///   silence, not a reason to fall back and re-run the same clip through
///   async — trimmed before the check so a whitespace-only response is
///   classified the same way, matching the independent TS implementation).
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

    if text.trim().is_empty() {
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
    use crate::providers::assemblyai::test_support::params;

    /// Sync requests need an explicit language AND a WAV container (see the
    /// module doc), so most shape/vocab/prompt tests below use this fixture
    /// instead of the bare `params()` default (which has neither — its
    /// `audio_path` is `.m4a`).
    fn sync_params() -> TranscribeParams {
        let mut p = params();
        p.language = Some("en".to_string());
        p.audio_mime = Some("audio/wav".to_string());
        p
    }

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
        let req = build_sync_request(&sync_params()).unwrap();
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
    }

    /// Regression: sync matches the audio part's Content-Type against a fixed
    /// set and 415s everything else ("unsupported audio Content-Type:
    /// 'audio/vnd.wave'"). macOS resolves a .wav file to `audio/vnd.wave`, so
    /// forwarding the resolved MIME meant every macOS sync attempt 415'd and
    /// silently fell through to async. See `SYNC_AUDIO_MIME`.
    #[test]
    fn sync_request_sends_canonical_wav_mime_not_the_resolved_one() {
        for resolved in ["audio/vnd.wave", "audio/wave", "audio/x-wav", "audio/wav"] {
            let mut p = sync_params();
            p.audio_mime = Some(resolved.to_string());
            let req = build_sync_request(&p).unwrap();
            match &req.body {
                Body::Multipart { parts, .. } => match &parts[0] {
                    Part::FileRef { mime, .. } => {
                        assert_eq!(mime, SYNC_AUDIO_MIME, "resolved MIME {resolved:?}");
                    }
                    other => panic!("expected FileRef first, got {other:?}"),
                },
                other => panic!("expected Multipart body, got {other:?}"),
            }
        }
    }

    #[test]
    fn sync_request_no_vocabulary_or_prompt_still_sends_language_config() {
        // Explicit language but no vocab/prompt -> a config part exists with
        // ONLY language_codes.
        let req = build_sync_request(&sync_params()).unwrap();
        let cfg = sync_config(&req).unwrap();
        assert_eq!(cfg["language_codes"], serde_json::json!(["en"]));
        assert!(cfg.get("keyterms_prompt").is_none());
        assert!(cfg.get("prompt").is_none());
    }

    #[test]
    fn sync_request_ignores_async_model_ids() {
        // The sync endpoint has exactly one model; an async-only (non-medical)
        // id must not leak into X-AAI-Model (it would be meaningless
        // upstream). Medical ids are covered separately below — they must be
        // REJECTED, not resolved to the sync default.
        for model in ["universal-2", "slam-1", ""] {
            let mut p = sync_params();
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
    fn sync_request_rejects_medical_models() {
        // Defense-in-depth: medical exclusion is also enforced independently
        // by each platform's own gate before ever calling this builder, but
        // the builder must refuse on its own too — so a caller that forgets
        // its platform-side gate still can't silently build a sync request
        // that drops Medical Mode. Sync has no `domain`/medical equivalent.
        for model in [
            "universal-2-medical",
            "universal-3-pro-medical",
            "slam-1-medical",
        ] {
            let mut p = sync_params();
            p.model = model.to_string();
            assert!(
                build_sync_request(&p).is_err(),
                "medical model {model:?} must be rejected by build_sync_request directly"
            );
        }
    }

    #[test]
    fn sync_request_rejects_non_wav_containers() {
        // The sync endpoint only accepts WAV or raw S16LE PCM, unlike async's
        // upload endpoint which accepts any container — forwarding a
        // compressed container would waste a round-trip on a guaranteed
        // rejection instead of skipping straight to async.
        for mime in [
            "audio/mp4",
            "audio/mpeg",
            "audio/webm",
            "audio/ogg",
            "audio/flac",
        ] {
            let mut p = sync_params();
            p.audio_mime = Some(mime.to_string());
            assert!(
                build_sync_request(&p).is_err(),
                "non-WAV mime {mime:?} must be rejected by build_sync_request"
            );
        }
    }

    #[test]
    fn sync_request_accepts_wav_container_resolved_from_path_when_mime_unset() {
        // When `audio_mime` is unset, the WAV gate must check the MIME
        // resolved from the file extension, not silently accept everything.
        let mut p = sync_params();
        p.audio_mime = None;
        p.audio_path = "/tmp/rec.wav".to_string();
        assert!(build_sync_request(&p).is_ok());

        let mut p2 = sync_params();
        p2.audio_mime = None;
        p2.audio_path = "/tmp/rec.m4a".to_string();
        assert!(build_sync_request(&p2).is_err());
    }

    #[test]
    fn sync_request_language_becomes_language_codes() {
        let mut p = sync_params();
        p.language = Some("en-US".to_string());
        let cfg = sync_config(&build_sync_request(&p).unwrap()).unwrap();
        assert_eq!(cfg["language_codes"], serde_json::json!(["en"]));
    }

    #[test]
    fn sync_request_auto_or_absent_language_falls_back_to_async() {
        // Unlike async's explicit `language_detection: true`, the sync API
        // defaults an OMITTED language list to English rather than
        // auto-detecting — so absent/"auto" must refuse to build a sync
        // request at all (every call site treats an Err here as "fall back
        // to async"), not silently omit language_codes.
        for lang in [None, Some("auto".to_string()), Some("AUTO".to_string()), Some("  ".to_string())] {
            let mut p = params();
            p.language = lang.clone();
            assert!(
                build_sync_request(&p).is_err(),
                "language {lang:?} should make the sync request build fail (fallback signal)"
            );
        }
    }

    #[test]
    fn sync_request_keyterms_capped_by_total_chars_not_count() {
        let mut p = sync_params();
        // Many short terms whose combined length exceeds the 2048-char budget.
        p.vocabulary = (0..500).map(|i| format!("term-{i}")).collect();
        let cfg = sync_config(&build_sync_request(&p).unwrap()).unwrap();
        let terms = cfg["keyterms_prompt"].as_array().unwrap();
        let total_chars: usize = terms.iter().map(|t| t.as_str().unwrap().len()).sum();
        assert!(total_chars <= SYNC_MAX_KEYTERMS_PROMPT_CHARS);
        assert!(!terms.is_empty());
    }

    #[test]
    fn sync_request_keyterms_drops_over_long_phrases() {
        // Mirrors async's MAX_KEYTERM_WORDS filter: a sync keyterms_prompt
        // must drop the same over-6-word phrases async silently drops, not
        // just cap by total characters.
        let mut p = sync_params();
        p.vocabulary = vec![
            "Rust".to_string(),
            "this phrase has way too many words to keep".to_string(), // 9 words -> dropped
        ];
        let cfg = sync_config(&build_sync_request(&p).unwrap()).unwrap();
        assert_eq!(cfg["keyterms_prompt"], serde_json::json!(["Rust"]));
    }

    #[test]
    fn sync_request_prompt_and_keyterms_are_separate_fields() {
        let mut p = sync_params();
        p.vocabulary = vec!["Rust".to_string()];
        p.prompt = Some("Be terse.".to_string());
        let cfg = sync_config(&build_sync_request(&p).unwrap()).unwrap();
        assert_eq!(cfg["keyterms_prompt"], serde_json::json!(["Rust"]));
        assert_eq!(cfg["prompt"], "Be terse.");
    }

    #[test]
    fn sync_request_base_url_override() {
        let mut p = sync_params();
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
    fn parse_sync_response_whitespace_only_text_is_no_speech() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"text":"   \n\t  ","confidence":1.0,"audio_duration_ms":500,"session_id":"s1"}"#
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
