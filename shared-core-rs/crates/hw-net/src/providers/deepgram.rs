//! Deepgram audio transcription request/response (sans-I/O).
//!
//! `POST https://api.deepgram.com/v1/listen?<query>` with the audio file sent as
//! the **raw request body** (NOT `multipart/form-data`) and the resolved audio
//! MIME as `Content-Type`. Auth is `Authorization: Token <key>` (Deepgram's
//! `Token` scheme, not `Bearer`). The response is structured JSON; the transcript
//! lives at `results.channels[0].alternatives[0].transcript`.
//!
//! Parity references:
//! - macOS `DeepgramProvider.swift` (the verified platform)
//! - Windows `DeepgramService.cs`
//!
//! ## Query parameters (built in this fixed order)
//!
//! `model`, `smart_format=true`, `mip_opt_out=true`, then **either**
//! `language=<lang>` (explicit / monolingual) **or** `detect_language=true`
//! (auto), then the model-dependent vocabulary params (see below). This mirrors
//! the macOS `URLQueryItem` append order exactly.
//!
//! ## Model-dependent vocabulary (the heart of this provider)
//!
//! Deepgram's vocabulary-boosting parameter depends on the model AND whether the
//! language is explicit:
//!
//! | Model family            | Language    | Vocab param                |
//! |-------------------------|-------------|----------------------------|
//! | `nova-3*` (monolingual) | explicit    | repeated `keyterm=TERM`    |
//! | `nova-3*` (auto-detect) | auto / none | none (keyterm is ignored)  |
//! | `nova-2*`/`nova-1*`/`enhanced*` | any | repeated `keywords=TERM:1.5` |
//! | `whisper*` / `base*`    | any         | none (unsupported)         |
//!
//! PARITY (keyterm gate): macOS gates `keyterm` on `model.hasPrefix("nova-3")`
//! AND an explicit (non-`auto`, non-empty) language. Windows instead uses an
//! explicit allow-set `{nova-3-general, nova-3-medical}`. We follow **macOS**
//! (the verified platform): any `nova-3` prefix qualifies. This is a documented,
//! functionally-equivalent divergence — the shipped nova-3 IDs are exactly
//! `nova-3-general` / `nova-3-medical`, so the two rules agree on real inputs.
//!
//! PARITY (auto-detect drops vocab): when the language is auto/empty, Deepgram
//! silently ignores `keyterm` (nova-3) and `keywords` is not meaningful, so both
//! platforms drop vocabulary entirely. We do the same — even for nova-2-family
//! models, macOS only appends `keywords` inside the `vocabParamName != nil`
//! branch which still fires under auto-detect; BUT the macOS branch appends
//! `keywords` regardless of language for nova-2. See the divergence note in
//! `vocab_param_name`.

use crate::contract::{
    Body, Header, HttpMethod, HttpRequest, HttpResponse, TranscribeParams, Transcript,
    TranscriptionError,
};
use crate::helpers::resolve_mime;
use crate::providers::hyperwhisper_cloud::encode_query;

/// Deepgram transcription endpoint.
pub const ENDPOINT: &str = "https://api.deepgram.com/v1/listen";

/// Default model when the caller leaves `params.model` empty (and the resolved
/// model is empty). PARITY: macOS `defaultModel(for: .deepgram)` / Windows
/// `DeepgramService` both default to `nova-3-general`.
pub const DEFAULT_MODEL: &str = "nova-3-general";

/// Deepgram model IDs removed in the 2026-05 catalog cleanup; these migrate to
/// `nova-3-general`. PARITY: macOS `CloudTranscriptionModels.removedDeepgramModelIds`
/// + `resolveDeepgramModelAlias`, and Windows `ResolveDeepgramModelAlias`.
const REMOVED_MODEL_IDS: &[&str] = &[
    "nova-2-meeting",
    "nova-2-phonecall",
    "nova-2-voicemail",
    "nova-2-finance",
    "nova-2-conversationalai",
    "nova-2-automotive",
    "nova-2-video",
    "nova",
    "nova-phonecall",
    "enhanced-general",
    "enhanced-meeting",
    "enhanced-phonecall",
    "enhanced-finance",
    "base-general",
    "base-meeting",
    "base-phonecall",
    "base-voicemail",
    "base-finance",
    "base-conversationalai",
    "base-video",
    "whisper-tiny",
    "whisper-base",
    "whisper-small",
    "whisper-medium",
    "whisper-large",
];

/// Resolve a (possibly removed) Deepgram model alias to its canonical ID, then
/// fall back to [`DEFAULT_MODEL`] when empty. PARITY: macOS
/// `resolveDeepgramModelAlias` returns `nil`/passes through, and the call site
/// falls back to `defaultModel` when the mode model is empty; we fold both steps.
fn resolve_model(model: &str) -> String {
    let trimmed = model.trim();
    if trimmed.is_empty() {
        return DEFAULT_MODEL.to_string();
    }
    if REMOVED_MODEL_IDS.contains(&trimmed) {
        return DEFAULT_MODEL.to_string();
    }
    trimmed.to_string()
}

/// True when the language is explicit (monolingual): present, non-empty, not
/// `"auto"` (case-insensitive). Mirrors the `isMonolingual` / `!isAutoDetect`
/// check on both platforms.
fn is_monolingual(language: Option<&str>) -> bool {
    match language {
        Some(l) => {
            let t = l.trim();
            !t.is_empty() && !t.eq_ignore_ascii_case("auto")
        }
        None => false,
    }
}

/// Which vocabulary query param (if any) applies to `model` at the given
/// language mode. Returns `None` when the model/language combination does not
/// support vocabulary boosting.
///
/// PARITY (macOS `DeepgramProvider.swift` lines 100-119):
/// - `whisper*` / `base*` → `None` (unsupported).
/// - `nova-3*` → `Some("keyterm")` iff monolingual, else `None`.
/// - everything else (nova-2/nova-1/enhanced) → `Some("keywords")`.
///
/// PARITY divergence (auto-detect + nova-2): macOS computes `vocabParamName`
/// **without** consulting the language for the nova-2 branch, so under
/// auto-detect it would still append `keywords`. Windows, by contrast, guards
/// the whole vocab block on `!isAutoDetect`. The task spec ("nova-3 with
/// auto-detect → no vocab"; nova-2/1/enhanced → keywords) follows macOS, which
/// does NOT gate nova-2 keywords on language — so we keep `keywords` for nova-2
/// regardless of language, matching the verified platform.
fn vocab_param_name(model: &str, monolingual: bool) -> Option<&'static str> {
    if model.starts_with("whisper") || model.starts_with("base") {
        None
    } else if model.starts_with("nova-3") {
        if monolingual {
            Some("keyterm")
        } else {
            None
        }
    } else {
        Some("keywords")
    }
}

/// Build the Deepgram transcription request.
pub fn build_transcribe_request(
    params: &TranscribeParams,
) -> Result<HttpRequest, TranscriptionError> {
    let model = resolve_model(&params.model);
    let monolingual = is_monolingual(params.language.as_deref());

    // Query params, appended in the exact macOS order.
    let mut pairs: Vec<(String, String)> = Vec::new();
    pairs.push(("model".to_string(), model.clone()));
    pairs.push(("smart_format".to_string(), "true".to_string()));
    pairs.push(("mip_opt_out".to_string(), "true".to_string()));

    if monolingual {
        // Safe: `is_monolingual` already validated presence/non-empty/non-auto.
        let lang = params.language.as_deref().unwrap_or("").trim().to_string();
        pairs.push(("language".to_string(), lang));
    } else {
        pairs.push(("detect_language".to_string(), "true".to_string()));
    }

    // Model-dependent vocabulary. Terms are routed through the shared sanitizer
    // so provider egress applies identical filtering/dedup/length caps.
    if let Some(param) = vocab_param_name(&model, monolingual) {
        for t in crate::helpers::keyword_boost_terms(&params.vocabulary, None) {
            let value = if param == "keyterm" {
                // Nova-3 monolingual: bare term, no intensifier.
                t.to_string()
            } else {
                // keywords: `TERM:1.5` boost intensifier (parity with both platforms).
                format!("{t}:1.5")
            };
            pairs.push((param.to_string(), value));
        }
    }

    let base = params
        .base_url
        .as_deref()
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .unwrap_or(ENDPOINT);
    let url = format!("{base}?{}", encode_query(&pairs));

    let mime = params
        .audio_mime
        .clone()
        .unwrap_or_else(|| resolve_mime(&params.audio_path));

    let headers = vec![Header::new(
        "Authorization",
        format!("Token {}", params.api_key),
    )];

    Ok(HttpRequest {
        method: HttpMethod::Post,
        url,
        headers,
        // Raw binary body: the platform streams the audio file with `mime` as the
        // request Content-Type (matches Swift `httpBody = audioData` + Windows
        // `ByteArrayContent`). Audio never crosses FFI — only the path does.
        body: Body::FileStream {
            path: params.audio_path.clone(),
            content_type: mime,
        },
    })
}

/// Parse the Deepgram transcription response.
///
/// Success shape: `{ "results": { "channels": [{ "alternatives": [{ "transcript": "..." }] }] } }`.
/// - Non-2xx → mapped via [`classify_deepgram_http`].
/// - 2xx with empty / missing transcript → [`TranscriptionError::NoSpeech`]
///   (matches the macOS "empty transcript = no speech" bugfix).
pub fn parse_transcribe_response(resp: &HttpResponse) -> Result<Transcript, TranscriptionError> {
    let raw = resp.text();

    if !(200..=299).contains(&resp.status) {
        return Err(classify_deepgram_http(resp, &raw));
    }

    // Plain-text 2xx fallback (mirrors the pre-unification macOS behavior): some
    // 2xx Deepgram responses are a bare transcript string rather than JSON. If the
    // trimmed body is non-empty and doesn't look like a JSON object/array, treat it
    // as the transcript instead of hard-failing with a parse error.
    let trimmed = raw.trim();
    if !trimmed.is_empty() && !trimmed.starts_with('{') && !trimmed.starts_with('[') {
        return Ok(Transcript {
            text: trimmed.to_string(),
            credits_remaining: None,
            cost: None,
            raw_provider: None,
        });
    }

    let json: serde_json::Value =
        serde_json::from_str(&raw).map_err(|e| TranscriptionError::Parse {
            message: format!("invalid JSON: {e}"),
        })?;

    let transcript = json
        .get("results")
        .and_then(|r| r.get("channels"))
        .and_then(|c| c.get(0))
        .and_then(|c0| c0.get("alternatives"))
        .and_then(|a| a.get(0))
        .and_then(|a0| a0.get("transcript"))
        .and_then(|t| t.as_str());

    match transcript {
        Some(text) if !text.is_empty() => Ok(Transcript {
            text: text.to_string(),
            credits_remaining: None,
            cost: None,
            raw_provider: None,
        }),
        // Present-but-empty OR shape-missing transcript both map to NoSpeech:
        // macOS treats a parsed-but-empty transcript as no-speech, and a Deepgram
        // 2xx always carries the results shape, so a missing transcript is
        // effectively silence rather than a parse failure.
        Some(_) | None => Err(TranscriptionError::NoSpeech),
    }
}

/// Map a non-2xx Deepgram response to a [`TranscriptionError`].
///
/// PARITY (macOS `DeepgramProvider.swift` switch + Windows `HandleErrorResponseAsync`):
/// - 401 / 403 → `Unauthorized`
/// - 413 → `FileTooLarge`
/// - 429 → `RateLimited { retry_after_secs }` (from `Retry-After`)
/// - 5xx → `ProviderUnavailable`
/// - other 4xx → `BadRequest` (message from `err_msg` / `error` / `message` / body)
fn classify_deepgram_http(resp: &HttpResponse, raw: &str) -> TranscriptionError {
    let status = resp.status;
    let json: Option<serde_json::Value> = serde_json::from_str(raw).ok();

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
            message: deepgram_error_message(json.as_ref(), raw),
        },
    }
}

/// Best-effort Deepgram error message: `err_msg`, then `error`, then `message`,
/// then the first 200 chars of the raw body. Deepgram uses `err_msg` for most
/// 4xx bodies (per Windows `HandleErrorResponseAsync`).
fn deepgram_error_message(json: Option<&serde_json::Value>, raw: &str) -> String {
    if let Some(j) = json {
        for key in ["err_msg", "error", "message"] {
            if let Some(m) = j.get(key).and_then(|v| v.as_str()) {
                return m.to_string();
            }
        }
    }
    raw.chars().take(200).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn params() -> TranscribeParams {
        TranscribeParams {
            api_key: "dg-test".to_string(),
            model: "nova-3-general".to_string(),
            audio_path: "/tmp/rec.wav".to_string(),
            ..Default::default()
        }
    }

    /// Extract the query string portion of the built URL.
    fn query(req: &HttpRequest) -> String {
        req.url.split_once('?').map(|(_, q)| q.to_string()).unwrap()
    }

    /// All values for a repeated query key, in order.
    fn values_for<'a>(q: &'a str, key: &str) -> Vec<&'a str> {
        q.split('&')
            .filter_map(|kv| kv.split_once('='))
            .filter(|(k, _)| *k == key)
            .map(|(_, v)| v)
            .collect()
    }

    #[test]
    fn builds_token_auth_and_endpoint() {
        let req = build_transcribe_request(&params()).unwrap();
        assert_eq!(req.method, HttpMethod::Post);
        assert!(req.url.starts_with(ENDPOINT));
        assert!(req
            .headers
            .contains(&Header::new("Authorization", "Token dg-test")));
    }

    #[test]
    fn body_is_filestream_with_resolved_mime() {
        let req = build_transcribe_request(&params()).unwrap();
        match &req.body {
            Body::FileStream { path, content_type } => {
                assert_eq!(path, "/tmp/rec.wav");
                assert_eq!(content_type, "audio/wav");
            }
            other => panic!("expected FileStream, got {other:?}"),
        }
    }

    #[test]
    fn audio_mime_override_wins() {
        let mut p = params();
        p.audio_mime = Some("audio/x-custom".to_string());
        let req = build_transcribe_request(&p).unwrap();
        match &req.body {
            Body::FileStream { content_type, .. } => assert_eq!(content_type, "audio/x-custom"),
            other => panic!("expected FileStream, got {other:?}"),
        }
    }

    #[test]
    fn base_query_params_and_order() {
        let req = build_transcribe_request(&params()).unwrap();
        let q = query(&req);
        // Auto-detect (no language) → detect_language, fixed leading order.
        assert!(q.starts_with(
            "model=nova-3-general&smart_format=true&mip_opt_out=true&detect_language=true"
        ));
    }

    #[test]
    fn explicit_language_uses_language_param_not_detect() {
        let mut p = params();
        p.language = Some("en".to_string());
        let req = build_transcribe_request(&p).unwrap();
        let q = query(&req);
        assert!(q.contains("&language=en"));
        assert!(!q.contains("detect_language"));
    }

    #[test]
    fn auto_language_uses_detect_language() {
        let mut p = params();
        p.language = Some("AUTO".to_string());
        let req = build_transcribe_request(&p).unwrap();
        let q = query(&req);
        assert!(q.contains("&detect_language=true"));
        assert!(!q.contains("&language="));
    }

    // ---- model-dependent vocabulary selection (golden) ----

    #[test]
    fn nova3_monolingual_uses_keyterm_bare() {
        let mut p = params();
        p.model = "nova-3-general".to_string();
        p.language = Some("en".to_string());
        p.vocabulary = vec!["Rust".to_string(), "UniFFI".to_string()];
        let q = query(&build_transcribe_request(&p).unwrap());
        assert_eq!(values_for(&q, "keyterm"), vec!["Rust", "UniFFI"]);
        assert!(values_for(&q, "keywords").is_empty());
    }

    #[test]
    fn nova3_autodetect_drops_vocab() {
        let mut p = params();
        p.model = "nova-3-medical".to_string();
        p.language = None; // auto-detect
        p.vocabulary = vec!["Rust".to_string()];
        let q = query(&build_transcribe_request(&p).unwrap());
        assert!(values_for(&q, "keyterm").is_empty());
        assert!(values_for(&q, "keywords").is_empty());
    }

    #[test]
    fn nova2_uses_keywords_with_boost() {
        let mut p = params();
        p.model = "nova-2-general".to_string();
        p.language = Some("en".to_string());
        p.vocabulary = vec!["Rust".to_string(), "UniFFI".to_string()];
        let q = query(&build_transcribe_request(&p).unwrap());
        // encode_query leaves ':' literal (urlQueryAllowed); '.' literal too.
        assert_eq!(values_for(&q, "keywords"), vec!["Rust:1.5", "UniFFI:1.5"]);
        assert!(values_for(&q, "keyterm").is_empty());
    }

    #[test]
    fn nova2_keywords_apply_even_under_autodetect_macos_parity() {
        // PARITY divergence note: macOS appends keywords for nova-2 regardless of
        // language; we follow macOS.
        let mut p = params();
        p.model = "nova-2-general".to_string();
        p.language = None;
        p.vocabulary = vec!["Rust".to_string()];
        let q = query(&build_transcribe_request(&p).unwrap());
        assert_eq!(values_for(&q, "keywords"), vec!["Rust:1.5"]);
    }

    #[test]
    fn whisper_and_base_models_drop_vocab() {
        // Use NON-removed whisper/base IDs so the alias migration doesn't fold
        // them into nova-3-general first; this exercises the whisper/base prefix
        // branch of `vocab_param_name` directly. (The shipped removed whisper/base
        // aliases migrate to nova-3-general — covered by the alias test below.)
        for model in ["whisper-large-v3", "base-nova"] {
            let mut p = params();
            p.model = model.to_string();
            p.language = Some("en".to_string());
            p.vocabulary = vec!["Rust".to_string()];
            let q = query(&build_transcribe_request(&p).unwrap());
            assert!(values_for(&q, "keyterm").is_empty(), "{model}");
            assert!(values_for(&q, "keywords").is_empty(), "{model}");
        }
    }

    #[test]
    fn removed_model_alias_migrates_to_nova3_general() {
        let mut p = params();
        p.model = "nova-2-meeting".to_string(); // removed → nova-3-general
        p.language = Some("en".to_string());
        p.vocabulary = vec!["Rust".to_string()];
        let q = query(&build_transcribe_request(&p).unwrap());
        assert!(q.starts_with("model=nova-3-general&"));
        // Resolved to nova-3 → keyterm path.
        assert_eq!(values_for(&q, "keyterm"), vec!["Rust"]);
    }

    #[test]
    fn empty_model_defaults_to_nova3_general() {
        let mut p = params();
        p.model = "".to_string();
        let req = build_transcribe_request(&p).unwrap();
        assert!(req.url.contains("model=nova-3-general"));
    }

    #[test]
    fn vocab_terms_sanitized_deduped_and_order_preserved() {
        let mut p = params();
        p.model = "nova-3-general".to_string();
        p.language = Some("en".to_string());
        p.vocabulary = vec![
            "  Rust  ".to_string(),
            "".to_string(),
            "UniFFI<script>".to_string(),
            "rust".to_string(), // duplicate dropped
        ];
        let q = query(&build_transcribe_request(&p).unwrap());
        assert_eq!(values_for(&q, "keyterm"), vec!["Rust", "UniFFIscript"]);
    }

    #[test]
    fn base_url_override_used() {
        let mut p = params();
        p.base_url = Some("https://staging.deepgram.test/v1/listen".to_string());
        let req = build_transcribe_request(&p).unwrap();
        assert!(req
            .url
            .starts_with("https://staging.deepgram.test/v1/listen?"));
    }

    // ---- response parsing (golden) ----

    #[test]
    fn parses_nested_transcript() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"results":{"channels":[{"alternatives":[{"transcript":"hello world"}]}]}}"#
                .to_vec(),
        };
        let t = parse_transcribe_response(&resp).unwrap();
        assert_eq!(t.text, "hello world");
    }

    #[test]
    fn empty_transcript_maps_to_no_speech() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"results":{"channels":[{"alternatives":[{"transcript":""}]}]}}"#.to_vec(),
        };
        assert_eq!(
            parse_transcribe_response(&resp).unwrap_err(),
            TranscriptionError::NoSpeech
        );
    }

    #[test]
    fn missing_channels_maps_to_no_speech() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"results":{"channels":[]}}"#.to_vec(),
        };
        assert_eq!(
            parse_transcribe_response(&resp).unwrap_err(),
            TranscriptionError::NoSpeech
        );
    }

    #[test]
    fn malformed_json_object_2xx_maps_to_parse_error() {
        // A body that LOOKS like JSON (starts with `{`) but is malformed still
        // maps to Parse — the plain-text fallback only applies to non-JSON bodies.
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"results": "#.to_vec(),
        };
        assert!(matches!(
            parse_transcribe_response(&resp).unwrap_err(),
            TranscriptionError::Parse { .. }
        ));
    }

    #[test]
    fn plain_text_2xx_body_is_used_as_transcript() {
        // GOLDEN (C4): a non-JSON 2xx body is a bare transcript string (mirrors the
        // pre-unification macOS fallback), not a parse failure.
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: b"  hello from deepgram  ".to_vec(),
        };
        let t = parse_transcribe_response(&resp).unwrap();
        assert_eq!(t.text, "hello from deepgram");
    }

    #[test]
    fn empty_plain_text_2xx_body_is_no_speech() {
        // A blank/whitespace 2xx body is not a transcript — it falls through to the
        // JSON path, which fails to parse "" → Parse error (no transcript content).
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: b"   ".to_vec(),
        };
        assert!(matches!(
            parse_transcribe_response(&resp).unwrap_err(),
            TranscriptionError::Parse { .. }
        ));
    }

    #[test]
    fn http_401_unauthorized() {
        let resp = HttpResponse {
            status: 401,
            headers: vec![],
            body: br#"{"err_msg":"bad key"}"#.to_vec(),
        };
        assert_eq!(
            parse_transcribe_response(&resp).unwrap_err(),
            TranscriptionError::Unauthorized
        );
    }

    #[test]
    fn http_403_unauthorized() {
        let resp = HttpResponse {
            status: 403,
            headers: vec![],
            body: b"{}".to_vec(),
        };
        assert_eq!(
            parse_transcribe_response(&resp).unwrap_err(),
            TranscriptionError::Unauthorized
        );
    }

    #[test]
    fn http_429_rate_limited_with_retry_after() {
        let resp = HttpResponse {
            status: 429,
            headers: vec![Header::new("Retry-After", "7")],
            body: b"{}".to_vec(),
        };
        assert_eq!(
            parse_transcribe_response(&resp).unwrap_err(),
            TranscriptionError::RateLimited {
                retry_after_secs: Some(7)
            }
        );
    }

    #[test]
    fn http_413_file_too_large() {
        let resp = HttpResponse {
            status: 413,
            headers: vec![],
            body: b"{}".to_vec(),
        };
        assert_eq!(
            parse_transcribe_response(&resp).unwrap_err(),
            TranscriptionError::FileTooLarge
        );
    }

    #[test]
    fn http_503_provider_unavailable() {
        let resp = HttpResponse {
            status: 503,
            headers: vec![],
            body: b"{}".to_vec(),
        };
        assert_eq!(
            parse_transcribe_response(&resp).unwrap_err(),
            TranscriptionError::ProviderUnavailable { status: 503 }
        );
    }

    #[test]
    fn http_400_bad_request_extracts_err_msg() {
        let resp = HttpResponse {
            status: 400,
            headers: vec![],
            body: br#"{"err_msg":"bad model"}"#.to_vec(),
        };
        assert_eq!(
            parse_transcribe_response(&resp).unwrap_err(),
            TranscriptionError::BadRequest {
                status: 400,
                message: "bad model".to_string()
            }
        );
    }
}
