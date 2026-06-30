//! HyperWhisper Cloud transcription request/response (sans-I/O).
//!
//! Built-in cloud STT routed through the Fly transcribe service. **No API key** —
//! the caller is identified by `license_key` (paid) or `device_id` (trial),
//! passed as **query parameters** on `POST {base_url}/transcribe`.
//!
//! Parity references:
//! - macOS `HyperWhisperCloudProvider.swift`
//! - Windows `HyperWhisperCloudService.cs`
//!
//! ## Wire shape (matches the shipped clients)
//!
//! - `POST {base_url}/transcribe?<query>`
//!   - query: `license_key` **or** `device_id` (exactly one),
//!     `language` (omitted when absent / empty / `"auto"`),
//!     `initial_prompt` (vocabulary CSV; omitted when empty).
//!   - headers: `Content-Type: <audio mime>`, plus the optional routed STT
//!     headers (`X-STT-Provider` / `X-STT-Model` / `X-STT-Domain`) when the
//!     caller supplied `routed_provider` / `routed_model` / `routed_domain`.
//!     The base HyperWhisper Cloud provider leaves these `None`; the routed
//!     sibling providers (`azure_mai`, `google_chirp`) set them.
//!   - body: **raw audio bytes streamed by the platform** — never read here.
//!
//! ## Body representation (contract divergence, documented)
//!
//! The shipped clients stream the *raw* audio file as the entire POST body
//! (macOS `session.upload(for:fromFile:)`, Windows `StreamContent(fileStream)`)
//! — NOT a `multipart/form-data` envelope. The sans-I/O [`Body`] enum only
//! references a file via [`Part::FileRef`], which lives inside
//! [`Body::Multipart`]. To keep audio out of FFI while staying inside the fixed
//! contract, a raw-streamed body is represented as a `Body::Multipart` carrying
//! **exactly one** `Part::FileRef` whose `field` is the sentinel
//! [`RAW_BODY_FIELD`]. The platform recognises this shape and streams the file
//! as the raw body (using the `FileRef.mime` as the `Content-Type`), bypassing
//! multipart assembly. This is the agreed unification: macOS and Windows both
//! stream raw, so there is no real divergence to reconcile — only a contract
//! representation choice.
//!
//! ## Response shape
//!
//! `{ "text": "...", "language": "en", "duration": 1.2,
//!    "cost": { "usd": 0.0007, "credits": 4.3 },
//!    "no_speech_detected": false }`
//!
//! - `no_speech_detected: true` → [`TranscriptionError::NoSpeech`].
//! - `credits_remaining` is reported by the backend on the `cost` object as the
//!   *used* `credits` field in the shipped clients; the running *balance* is
//!   surfaced via response headers (`X-Device-Credits-Remaining`) which the
//!   platform reads. Here we expose `cost.credits` as `Transcript.cost` and the
//!   header-derived balance via [`parse_credits_remaining`] for the platform.

use crate::contract::{
    Body, Header, HttpMethod, HttpRequest, HttpResponse, Part, TranscribeParams, Transcript,
    TranscriptionError,
};
use crate::helpers::{
    normalize_vocabulary_capped, resolve_mime, HW_CLOUD_MAX_VOCAB_TERMS, MULTIPART_BOUNDARY,
};

/// Sentinel multipart field name marking a single-`FileRef` body that the
/// platform must stream as the **raw** request body (not a multipart envelope).
/// See the module docs for why this exists.
pub const RAW_BODY_FIELD: &str = "@raw";

/// Endpoint path appended to `base_url`.
pub const TRANSCRIBE_PATH: &str = "/transcribe";

/// Build the HyperWhisper Cloud transcription request.
///
/// `params.base_url` is required (the platform passes the resolved HW Cloud base,
/// e.g. `https://transcribe-prod-v2.hyperwhisper.com`). Exactly one of
/// `license_key` / `device_id` must be present.
pub fn build_transcribe_request(
    params: &TranscribeParams,
) -> Result<HttpRequest, TranscriptionError> {
    build_routed_request(params)
}

/// Shared request builder for HyperWhisper Cloud and its routed siblings
/// (`azure_mai`, `google_chirp`). The only difference between them is the
/// `X-STT-Provider` / `-Model` / `-Domain` headers, which come from the
/// `routed_*` fields on [`TranscribeParams`] — so this one builder serves all
/// three, exactly mirroring `HyperWhisperRoutedTranscription` reusing the same
/// `/transcribe` contract as `HyperWhisperCloudProvider`.
pub(crate) fn build_routed_request(
    params: &TranscribeParams,
) -> Result<HttpRequest, TranscriptionError> {
    let base = params.base_url.as_deref().unwrap_or("").trim_end_matches('/');
    if base.is_empty() {
        return Err(TranscriptionError::BadRequest {
            status: 0,
            message: "HyperWhisper Cloud requires base_url".to_string(),
        });
    }

    // Exactly one identifier. license_key wins when both are set (matches the
    // `isLicensed` branch in the shipped clients: licensed users send
    // license_key, trial users send device_id).
    let mut query: Vec<(String, String)> = Vec::new();
    match (params.license_key.as_deref(), params.device_id.as_deref()) {
        (Some(key), _) if !key.is_empty() => {
            query.push(("license_key".to_string(), key.to_string()));
        }
        (_, Some(dev)) if !dev.is_empty() => {
            query.push(("device_id".to_string(), dev.to_string()));
        }
        _ => {
            return Err(TranscriptionError::BadRequest {
                status: 0,
                message: "HyperWhisper Cloud requires license_key or device_id".to_string(),
            });
        }
    }

    // language: omitted when absent / empty / "auto" (case-insensitive).
    //
    // PARITY NOTE / unification choice: macOS `HyperWhisperCloudProvider`
    // truncates to a 2-char code (`String(lang.prefix(2))`), while the routed
    // path (`HyperWhisperRoutedTranscription`) sends the full BCP-47 tag
    // (Azure/Google need the region subtag) and Windows sends the value
    // verbatim. We send the language **verbatim** (lowercased), matching the
    // routed path + Windows. The caller is responsible for passing the code it
    // wants; the backend BCP-47 maps it. (The 2-char truncation was a macOS-only
    // quirk that breaks region-sensitive upstreams — not preserved.)
    if let Some(lang) = params.language.as_deref() {
        let lang = lang.trim().to_lowercase();
        if !lang.is_empty() && lang != "auto" {
            query.push(("language".to_string(), lang));
        }
    }

    // initial_prompt: bare-comma-separated vocabulary, omitted when empty.
    //
    // PARITY: the shipped clients join with "," (no space) — macOS
    // `entries.joined(separator: ",")`, Windows `string.Join(",", uniqueTerms)`.
    // `encode_query` then leaves the comma literal (matching macOS `URLQueryItem`,
    // which does NOT escape `,`), so we emit `initial_prompt=Rust,UniFFI` exactly
    // like the macOS wire bytes.
    //
    // Re-applies the shipped client behavior: case-insensitive de-dup (first
    // occurrence wins, order preserved) + cap at 100 terms (a soft backend limit),
    // then bare-comma join.
    let csv = normalize_vocabulary_capped(&params.vocabulary, HW_CLOUD_MAX_VOCAB_TERMS).join(",");
    if !csv.is_empty() {
        query.push(("initial_prompt".to_string(), csv));
    }

    let url = format!("{}{}?{}", base, TRANSCRIBE_PATH, encode_query(&query));

    // Content-Type is the audio MIME (raw streamed body).
    let mime = params
        .audio_mime
        .clone()
        .unwrap_or_else(|| resolve_mime(&params.audio_path));

    let mut headers = vec![Header::new("Content-Type", mime.clone())];

    // Routed STT headers — set only by the routed siblings.
    if let Some(p) = params.routed_provider.as_deref() {
        if !p.is_empty() {
            headers.push(Header::new("X-STT-Provider", p.to_string()));
        }
    }
    if let Some(m) = params.routed_model.as_deref() {
        if !m.is_empty() {
            headers.push(Header::new("X-STT-Model", m.to_string()));
        }
    }
    if let Some(d) = params.routed_domain.as_deref() {
        if !d.is_empty() {
            headers.push(Header::new("X-STT-Domain", d.to_string()));
        }
    }

    let body = Body::Multipart {
        boundary: MULTIPART_BOUNDARY.to_string(),
        parts: vec![Part::FileRef {
            field: RAW_BODY_FIELD.to_string(),
            path: params.audio_path.clone(),
            mime,
            filename: filename_of(&params.audio_path),
        }],
    };

    Ok(HttpRequest {
        method: HttpMethod::Post,
        url,
        headers,
        body,
    })
}

/// Parse a HyperWhisper Cloud `/transcribe` response.
///
/// `no_speech_detected: true` → [`TranscriptionError::NoSpeech`].
/// Otherwise returns the `text` plus `cost.credits` / `cost.usd`.
/// Non-2xx status → [`TranscriptionError`] via the body's `error`/`message`.
pub fn parse_transcribe_response(resp: &HttpResponse) -> Result<Transcript, TranscriptionError> {
    parse_routed_response(resp)
}

/// Shared response parser for HyperWhisper Cloud and routed siblings.
pub(crate) fn parse_routed_response(
    resp: &HttpResponse,
) -> Result<Transcript, TranscriptionError> {
    let text = resp.text();
    let json: serde_json::Value = serde_json::from_str(&text).map_err(|e| {
        // Surface HTTP errors with their own variants even when the body is not
        // JSON (e.g. a proxy 502 with an HTML body).
        if resp.status != 200 {
            classify_status(resp.status, &text)
        } else {
            TranscriptionError::Parse {
                message: format!("invalid JSON: {e}"),
            }
        }
    })?;

    if resp.status != 200 {
        return Err(classify_status_json(resp.status, &json, &text));
    }

    // no_speech_detected → NoSpeech (matches both clients).
    if json
        .get("no_speech_detected")
        .and_then(|v| v.as_bool())
        .unwrap_or(false)
    {
        return Err(TranscriptionError::NoSpeech);
    }

    // Prefer `corrected` (server post-processed) → `text` → `original`,
    // mirroring the Windows client's preference order. macOS reads `text`.
    let text_value = json
        .get("corrected")
        .and_then(|v| v.as_str())
        .filter(|s| !s.is_empty())
        .or_else(|| json.get("text").and_then(|v| v.as_str()))
        .or_else(|| json.get("original").and_then(|v| v.as_str()))
        .map(|s| s.to_string());

    let Some(text_value) = text_value else {
        return Err(TranscriptionError::Parse {
            message: "response missing text/corrected/original".to_string(),
        });
    };

    // cost: { usd, credits } — `credits` is the amount charged for this call.
    let cost_obj = json.get("cost");
    let cost = cost_obj
        .and_then(|c| c.get("credits"))
        .and_then(|v| v.as_f64());
    let usd = cost_obj.and_then(|c| c.get("usd")).and_then(|v| v.as_f64());
    let _ = usd; // usd is logged by the platform, not part of Transcript today.

    // credits_remaining: balance left after the call. The backend reports this
    // primarily via the `X-Device-Credits-Remaining` response header (read by
    // the platform), but some responses also embed `credits_remaining` in the
    // JSON body (notably 402 errors). Read the body field opportunistically.
    let credits_remaining = json
        .get("credits_remaining")
        .and_then(|v| v.as_f64());

    let raw_provider = resp
        .header("X-STT-Provider")
        .map(|s| s.to_string())
        .or_else(|| {
            json.get("provider")
                .and_then(|v| v.as_str())
                .map(|s| s.to_string())
        });

    Ok(Transcript {
        text: text_value,
        credits_remaining,
        cost,
        raw_provider,
    })
}

/// Read the running credit balance from a response header
/// (`X-Device-Credits-Remaining`), for the platform to surface in the UI.
/// Returns `None` when the header is absent/unparseable.
pub fn parse_credits_remaining(resp: &HttpResponse) -> Option<f64> {
    resp.header("X-Device-Credits-Remaining")
        .and_then(|v| v.trim().parse::<f64>().ok())
}

/// Read credits *used* from the `X-Credits-Used` response header.
pub fn parse_credits_used(resp: &HttpResponse) -> Option<f64> {
    resp.header("X-Credits-Used")
        .and_then(|v| v.trim().parse::<f64>().ok())
}

// ---------------------------------------------------------------------------
// internals
// ---------------------------------------------------------------------------

/// Map an HTTP status (with parsed JSON body) to a [`TranscriptionError`],
/// mirroring `handleHTTPError` (macOS) / `HandleErrorResponseAsync` (Windows).
fn classify_status_json(
    status: u16,
    json: &serde_json::Value,
    raw: &str,
) -> TranscriptionError {
    let message = json
        .get("message")
        .and_then(|v| v.as_str())
        .or_else(|| json.get("error").and_then(|v| v.as_str()))
        .unwrap_or(raw)
        .to_string();
    match status {
        401 | 403 => TranscriptionError::Unauthorized,
        402 => TranscriptionError::QuotaExceeded,
        413 => TranscriptionError::FileTooLarge,
        429 => TranscriptionError::RateLimited {
            retry_after_secs: None,
        },
        500..=599 => TranscriptionError::ProviderUnavailable { status },
        _ => TranscriptionError::BadRequest { status, message },
    }
}

/// Same mapping when the body is not JSON.
fn classify_status(status: u16, raw: &str) -> TranscriptionError {
    match status {
        401 | 403 => TranscriptionError::Unauthorized,
        402 => TranscriptionError::QuotaExceeded,
        413 => TranscriptionError::FileTooLarge,
        429 => TranscriptionError::RateLimited {
            retry_after_secs: None,
        },
        500..=599 => TranscriptionError::ProviderUnavailable { status },
        _ => TranscriptionError::BadRequest {
            status,
            message: raw.chars().take(200).collect(),
        },
    }
}

/// Last path component of `path` (for the multipart `filename`).
fn filename_of(path: &str) -> String {
    path.rsplit(['/', '\\'])
        .next()
        .filter(|s| !s.is_empty())
        .unwrap_or("audio")
        .to_string()
}

/// Percent-encode a query string. Deterministic, no deps.
///
/// PARITY: byte-matches how macOS `URLComponents.queryItems` encodes query
/// **values** (the verified reference platform — see the module-level parity
/// refs). Empirically (confirmed by running Swift `URLComponents` /
/// `percentEncodedQuery`), Foundation leaves the RFC 3986 unreserved set plus
/// the sub-delims / extra chars in `CharacterSet.urlQueryAllowed` unescaped, and
/// additionally percent-encodes only `&` and `=` (the key/value separators) so
/// they cannot be confused with delimiters. Concretely it keeps these literal:
/// `! $ ' ( ) * + , - . / : ; ? @ _ ~` and alphanumerics, while space → `%20`,
/// `&` → `%26`, `=` → `%3D`, etc.
///
/// This matters for the vocabulary CSV: macOS sends `initial_prompt=Rust,UniFFI`
/// with a **literal** comma (URLQueryItem does NOT escape `,`). Matching that
/// here keeps true wire-byte parity with macOS. (Windows
/// `HttpUtility.ParseQueryString().ToString()` diverges — it lowercases to `%2c`
/// and turns space into `+`; we follow macOS, the verified platform, per the
/// parity rule.) Functionally all forms are equivalent — the Fly backend
/// URL-decodes `,`, `%2C`, and `%2c` identically — but this keeps the bytes
/// identical to the verified client.
pub(crate) fn encode_query(pairs: &[(String, String)]) -> String {
    pairs
        .iter()
        .map(|(k, v)| format!("{}={}", percent_encode(k), percent_encode(v)))
        .collect::<Vec<_>>()
        .join("&")
}

/// True iff `b` is left unescaped inside a macOS `URLComponents` query value:
/// `CharacterSet.urlQueryAllowed` minus the `&` and `=` separators.
fn is_query_value_unreserved(b: u8) -> bool {
    matches!(b,
        b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9'
        | b'!' | b'$' | b'\'' | b'(' | b')' | b'*' | b'+' | b','
        | b'-' | b'.' | b'/' | b':' | b';' | b'?' | b'@' | b'_' | b'~'
    )
}

fn percent_encode(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for b in s.bytes() {
        if is_query_value_unreserved(b) {
            out.push(b as char);
        } else {
            out.push('%');
            out.push(hex_upper(b >> 4));
            out.push(hex_upper(b & 0x0f));
        }
    }
    out
}

fn hex_upper(nibble: u8) -> char {
    match nibble {
        0..=9 => (b'0' + nibble) as char,
        _ => (b'A' + (nibble - 10)) as char,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn base_params() -> TranscribeParams {
        TranscribeParams {
            audio_path: "/tmp/rec.wav".to_string(),
            base_url: Some("https://transcribe-prod-v2.hyperwhisper.com".to_string()),
            ..Default::default()
        }
    }

    fn resp(status: u16, body: &str) -> HttpResponse {
        HttpResponse {
            status,
            headers: vec![],
            body: body.as_bytes().to_vec(),
        }
    }

    #[test]
    fn builds_request_with_license_key() {
        let mut p = base_params();
        p.license_key = Some("LIC-123".to_string());
        let req = build_transcribe_request(&p).unwrap();
        assert_eq!(req.method, HttpMethod::Post);
        assert_eq!(
            req.url,
            "https://transcribe-prod-v2.hyperwhisper.com/transcribe?license_key=LIC-123"
        );
        // Content-Type = audio mime resolved from .wav
        assert_eq!(req.headers[0], Header::new("Content-Type", "audio/wav"));
        // No routed headers for the base provider.
        assert_eq!(req.headers.len(), 1);
        // Body is the raw-stream sentinel single-FileRef multipart.
        match &req.body {
            Body::Multipart { boundary, parts } => {
                assert_eq!(boundary, MULTIPART_BOUNDARY);
                assert_eq!(parts.len(), 1);
                assert_eq!(
                    parts[0],
                    Part::FileRef {
                        field: RAW_BODY_FIELD.to_string(),
                        path: "/tmp/rec.wav".to_string(),
                        mime: "audio/wav".to_string(),
                        filename: "rec.wav".to_string(),
                    }
                );
            }
            other => panic!("expected multipart, got {other:?}"),
        }
    }

    #[test]
    fn builds_request_with_device_id_and_language_and_vocab() {
        let mut p = base_params();
        p.device_id = Some("dev abc".to_string()); // space → %20
        p.language = Some("EN-US".to_string());
        p.vocabulary = vec!["Rust".to_string(), "UniFFI".to_string()];
        let req = build_transcribe_request(&p).unwrap();
        assert_eq!(
            req.url,
            "https://transcribe-prod-v2.hyperwhisper.com/transcribe?device_id=dev%20abc&language=en-us&initial_prompt=Rust,UniFFI"
        );
    }

    #[test]
    fn language_auto_is_omitted() {
        let mut p = base_params();
        p.device_id = Some("d".to_string());
        p.language = Some("auto".to_string());
        let req = build_transcribe_request(&p).unwrap();
        assert_eq!(
            req.url,
            "https://transcribe-prod-v2.hyperwhisper.com/transcribe?device_id=d"
        );
    }

    #[test]
    fn license_key_wins_over_device_id() {
        let mut p = base_params();
        p.license_key = Some("L".to_string());
        p.device_id = Some("D".to_string());
        let req = build_transcribe_request(&p).unwrap();
        assert!(req.url.contains("license_key=L"));
        assert!(!req.url.contains("device_id"));
    }

    #[test]
    fn missing_identifier_errors() {
        let p = base_params();
        let err = build_transcribe_request(&p).unwrap_err();
        assert!(matches!(err, TranscriptionError::BadRequest { .. }));
    }

    #[test]
    fn missing_base_url_errors() {
        let mut p = base_params();
        p.base_url = None;
        p.device_id = Some("d".to_string());
        let err = build_transcribe_request(&p).unwrap_err();
        assert!(matches!(err, TranscriptionError::BadRequest { status: 0, .. }));
    }

    #[test]
    fn audio_mime_override_is_used() {
        let mut p = base_params();
        p.device_id = Some("d".to_string());
        p.audio_mime = Some("audio/x-custom".to_string());
        let req = build_transcribe_request(&p).unwrap();
        assert_eq!(req.headers[0], Header::new("Content-Type", "audio/x-custom"));
    }

    // ---- golden response parse ----

    #[test]
    fn parses_success_response() {
        let body = r#"{
            "text": "Hello world",
            "language": "en",
            "duration": 1.23,
            "cost": { "usd": 0.0007, "credits": 4.3 },
            "no_speech_detected": false
        }"#;
        let t = parse_transcribe_response(&resp(200, body)).unwrap();
        assert_eq!(t.text, "Hello world");
        assert_eq!(t.cost, Some(4.3));
        assert_eq!(t.credits_remaining, None);
    }

    #[test]
    fn prefers_corrected_then_text_then_original() {
        let body = r#"{"corrected":"C","text":"T","original":"O"}"#;
        assert_eq!(parse_transcribe_response(&resp(200, body)).unwrap().text, "C");
        let body = r#"{"corrected":"","text":"T","original":"O"}"#;
        assert_eq!(parse_transcribe_response(&resp(200, body)).unwrap().text, "T");
        let body = r#"{"original":"O"}"#;
        assert_eq!(parse_transcribe_response(&resp(200, body)).unwrap().text, "O");
    }

    #[test]
    fn parses_credits_remaining_from_body() {
        let body = r#"{"text":"hi","cost":{"credits":2.0},"credits_remaining":146.0}"#;
        let t = parse_transcribe_response(&resp(200, body)).unwrap();
        assert_eq!(t.credits_remaining, Some(146.0));
    }

    #[test]
    fn no_speech_maps_to_nospeech_error() {
        let body = r#"{"no_speech_detected": true}"#;
        let err = parse_transcribe_response(&resp(200, body)).unwrap_err();
        assert_eq!(err, TranscriptionError::NoSpeech);
    }

    #[test]
    fn http_402_maps_to_quota_exceeded() {
        let body = r#"{"error":"Insufficient credits","credits_remaining":0}"#;
        let err = parse_transcribe_response(&resp(402, body)).unwrap_err();
        assert_eq!(err, TranscriptionError::QuotaExceeded);
    }

    #[test]
    fn http_401_maps_to_unauthorized() {
        let err = parse_transcribe_response(&resp(401, r#"{"error":"bad key"}"#)).unwrap_err();
        assert_eq!(err, TranscriptionError::Unauthorized);
    }

    #[test]
    fn http_429_maps_to_rate_limited() {
        let err = parse_transcribe_response(&resp(429, r#"{"error":"slow down"}"#)).unwrap_err();
        assert_eq!(
            err,
            TranscriptionError::RateLimited {
                retry_after_secs: None
            }
        );
    }

    #[test]
    fn http_5xx_maps_to_provider_unavailable() {
        let err = parse_transcribe_response(&resp(503, "upstream down")).unwrap_err();
        assert_eq!(
            err,
            TranscriptionError::ProviderUnavailable { status: 503 }
        );
    }

    #[test]
    fn http_400_non_json_maps_to_bad_request() {
        let err = parse_transcribe_response(&resp(400, "nope")).unwrap_err();
        assert!(matches!(err, TranscriptionError::BadRequest { status: 400, .. }));
    }

    #[test]
    fn credits_remaining_from_header() {
        let r = HttpResponse {
            status: 200,
            headers: vec![Header::new("X-Device-Credits-Remaining", "142")],
            body: br#"{"text":"hi"}"#.to_vec(),
        };
        assert_eq!(parse_credits_remaining(&r), Some(142.0));
        assert_eq!(parse_credits_used(&r), None);
    }

    #[test]
    fn percent_encoding_unreserved_passthrough() {
        assert_eq!(percent_encode("Aa0-_.~"), "Aa0-_.~");
        assert_eq!(percent_encode("a b&c=d"), "a%20b%26c%3Dd");
    }

    /// Golden parity: byte-for-byte against what macOS `URLComponents` produces
    /// for a query VALUE (captured by running Swift `percentEncodedQuery`).
    /// macOS keeps `! $ ' ( ) * + , - . / : ; ? @ _ ~` and alphanumerics literal,
    /// and escapes only space/`&`/`=`/`#`/`%`/`"` (anything outside that set).
    #[test]
    fn percent_encode_matches_macos_url_query_value() {
        // Comma stays LITERAL (the headline fix) — macOS does not escape it.
        assert_eq!(percent_encode("Rust,UniFFI"), "Rust,UniFFI");
        // Full character sweep, identical to the Swift URLComponents output.
        let input = "Rust,UniFFI a&b=c+d?e/f:g@h!i$j(k)l*m;n'o~p-q_r.s#t%u\"v";
        let expected =
            "Rust,UniFFI%20a%26b%3Dc+d?e/f:g@h!i$j(k)l*m;n'o~p-q_r.s%23t%25u%22v";
        assert_eq!(percent_encode(input), expected);
    }

    #[test]
    fn vocab_comma_is_literal_in_url() {
        let mut p = base_params();
        p.device_id = Some("d".to_string());
        p.vocabulary = vec!["Rust".to_string(), "UniFFI".to_string(), "C#".to_string()];
        let req = build_transcribe_request(&p).unwrap();
        // Bare comma between terms (literal); `#` in "C#" still escapes to %23.
        assert!(req.url.ends_with("initial_prompt=Rust,UniFFI,C%23"), "{}", req.url);
    }
}
