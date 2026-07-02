//! Shared building blocks for the OpenAI-compatible multipart providers
//! (OpenAI, Groq, Mistral, Grok) and the `{ "text": "..." }` response shape they
//! all return. Keeping this in one place lets each provider module stay a thin,
//! declarative description of its own quirks (endpoint, auth header, default
//! model, vocabulary support) while the multipart assembly and error mapping —
//! which are byte-identical across them — live here.
//!
//! These are plain helpers (no I/O, no clock, no RNG). Audio is referenced via a
//! [`Part::FileRef`] streamed by the platform.

use crate::contract::{
    Body, Header, HttpMethod, HttpRequest, HttpResponse, Part, TranscribeParams, Transcript,
    TranscriptionError,
};
use crate::helpers::{multipart_field, multipart_file, resolve_mime, vocabulary_csv, MULTIPART_BOUNDARY};

/// Which HTTP auth scheme a provider uses.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Auth {
    /// `Authorization: Bearer <key>` (OpenAI / Groq / Grok).
    Bearer,
    /// `x-api-key: <key>` (Mistral).
    XApiKey,
}

/// How a provider handles vocabulary terms.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum VocabularyMode {
    /// Inject comma-separated terms into the `prompt` field (OpenAI / Groq).
    Prompt,
    /// Provider does not accept vocabulary; terms are dropped (Mistral / Grok).
    None,
}

/// Declarative description of an OpenAI-style multipart transcription provider.
pub struct OpenAiStyleSpec {
    pub endpoint: &'static str,
    pub default_model: &'static str,
    pub auth: Auth,
    pub vocabulary: VocabularyMode,
    /// Whether to send a `model` field at all (Grok STT has no model param).
    pub send_model: bool,
    /// Whether to send `response_format=json` (OpenAI / Groq). Mistral / Grok
    /// return `{ "text" }` without this field, so they omit it.
    pub send_response_format: bool,
}

/// Build an OpenAI-compatible multipart request from a spec.
///
/// Field order matches the shipped clients: `file`, `model`, `language`,
/// `prompt`, `response_format` (each omitted per the spec / inputs).
pub fn build_openai_style(
    params: &TranscribeParams,
    spec: &OpenAiStyleSpec,
) -> Result<HttpRequest, TranscriptionError> {
    let mime = params
        .audio_mime
        .clone()
        .unwrap_or_else(|| resolve_mime(&params.audio_path));

    let mut parts: Vec<Part> = Vec::new();

    // file first (matches macOS/Windows assembly order).
    parts.push(multipart_file(
        "file",
        params.audio_path.clone(),
        mime.clone(),
        filename_of(&params.audio_path),
    ));

    // model
    if spec.send_model {
        let model = if params.model.trim().is_empty() {
            spec.default_model.to_string()
        } else {
            params.model.clone()
        };
        parts.push(multipart_field("model", model));
    }

    // language — omitted when absent / empty / "auto" (case-insensitive).
    //
    // PARITY: macOS `CloudWhisperProvider.swift` (lines 182-189) lowercases the
    // language and truncates it to its first 2 chars before sending
    // (`let lang = language?.lowercased(); ... let isoCode = lang.prefix(2)`).
    // OpenAI/Groq Whisper expect an ISO-639-1 (2-letter) code, so a full BCP-47
    // tag like "en-US" / "zh-TW" must collapse to "en" / "zh". We mirror that
    // here (lowercase + first-2-ASCII-char prefix) rather than sending the value
    // verbatim. This matches the ElevenLabs builder's primary-subtag
    // normalization in spirit while reproducing macOS's exact `prefix(2)` cut.
    if let Some(lang) = params.language.as_deref() {
        let trimmed = lang.trim();
        if !trimmed.is_empty() && !trimmed.eq_ignore_ascii_case("auto") {
            parts.push(multipart_field("language", iso_639_1(trimmed)));
        }
    }

    // prompt — vocabulary CSV (when supported) followed by the caller's custom
    // instructions (`params.prompt`). Either may be empty.
    if spec.vocabulary == VocabularyMode::Prompt {
        let prompt = build_prompt(params);
        if !prompt.is_empty() {
            parts.push(multipart_field("prompt", prompt));
        }
    }

    if spec.send_response_format {
        parts.push(multipart_field("response_format", "json"));
    }

    let headers = vec![auth_header(spec.auth, &params.api_key)];

    Ok(HttpRequest {
        method: HttpMethod::Post,
        url: spec.endpoint.to_string(),
        headers,
        body: Body::Multipart {
            boundary: MULTIPART_BOUNDARY.to_string(),
            parts,
        },
    })
}

/// Compose the `prompt` field value: bare vocabulary CSV, then the caller's
/// custom instructions (`params.prompt`), separated by a single space when both
/// are present.
fn build_prompt(params: &TranscribeParams) -> String {
    let csv = vocabulary_csv(&params.vocabulary);
    let custom = params
        .prompt
        .as_deref()
        .map(str::trim)
        .filter(|s| !s.is_empty());
    match (csv.is_empty(), custom) {
        (true, None) => String::new(),
        (true, Some(c)) => c.to_string(),
        (false, None) => csv,
        (false, Some(c)) => format!("{csv} {c}"),
    }
}

/// The `Authorization: Bearer` / `x-api-key` header for `auth`.
pub fn auth_header(auth: Auth, api_key: &str) -> Header {
    match auth {
        Auth::Bearer => Header::new("Authorization", format!("Bearer {api_key}")),
        Auth::XApiKey => Header::new("x-api-key", api_key.to_string()),
    }
}

/// Parse a `{ "text": "..." }` response shared by OpenAI / Groq / Mistral / Grok.
///
/// - Non-2xx → mapped via [`classify_http`].
/// - 2xx with empty / missing `text` → [`TranscriptionError::NoSpeech`]
///   (matches the shipped clients, which treat a blank transcript as silence).
pub fn parse_text_response(resp: &HttpResponse) -> Result<Transcript, TranscriptionError> {
    let raw = resp.text();

    if !(200..=299).contains(&resp.status) {
        return Err(classify_http(resp, &raw));
    }

    let json: serde_json::Value = serde_json::from_str(&raw).map_err(|e| TranscriptionError::Parse {
        message: format!("invalid JSON: {e}"),
    })?;

    let text = json
        .get("text")
        .and_then(|v| v.as_str())
        .unwrap_or("")
        .to_string();

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

/// Map a non-2xx HTTP response to a [`TranscriptionError`], mirroring the status
/// handling in the shipped providers.
///
/// PARITY:
/// - 401 / 403 → `Unauthorized`
/// - 402 → `QuotaExceeded`
/// - 413 → `FileTooLarge`
/// - 429 → `QuotaExceeded` when the OpenAI-style error body signals quota
///   exhaustion (`code`/`type` = `insufficient_quota`, or a message mentioning
///   "quota"/"billing"), else `RateLimited { retry_after_secs }` from the
///   `Retry-After` header (matches `CloudWhisperProvider.swift`'s 429 branch).
/// - 408 → `ProviderUnavailable` (request timeout is transient, same as 5xx;
///   keying it into the retryable variant keeps it retried instead of the
///   terminal `BadRequest` fallthrough)
/// - 5xx → `ProviderUnavailable`
/// - other 4xx → `BadRequest { message }` from `error.message` / `error` / body.
pub fn classify_http(resp: &HttpResponse, raw: &str) -> TranscriptionError {
    let status = resp.status;
    let json: Option<serde_json::Value> = serde_json::from_str(raw).ok();

    match status {
        401 | 403 => TranscriptionError::Unauthorized,
        402 => TranscriptionError::QuotaExceeded,
        408 => TranscriptionError::ProviderUnavailable { status },
        413 => TranscriptionError::FileTooLarge,
        429 => {
            if is_quota_error(json.as_ref()) {
                TranscriptionError::QuotaExceeded
            } else {
                TranscriptionError::RateLimited {
                    retry_after_secs: retry_after(resp),
                }
            }
        }
        500..=599 => TranscriptionError::ProviderUnavailable { status },
        _ => TranscriptionError::BadRequest {
            status,
            message: error_message(json.as_ref(), raw),
        },
    }
}

/// True when an OpenAI-style 429 body indicates permanent quota exhaustion
/// (vs. transient rate limiting). Mirrors the `isQuotaError` check in
/// `CloudWhisperProvider.swift`.
fn is_quota_error(json: Option<&serde_json::Value>) -> bool {
    let Some(error) = json.and_then(|j| j.get("error")) else {
        return false;
    };
    let code = error.get("code").and_then(|v| v.as_str()).unwrap_or("");
    let kind = error.get("type").and_then(|v| v.as_str()).unwrap_or("");
    let msg = error
        .get("message")
        .and_then(|v| v.as_str())
        .unwrap_or("")
        .to_lowercase();
    code == "insufficient_quota"
        || kind == "insufficient_quota"
        || msg.contains("quota")
        || msg.contains("billing")
}

/// Best-effort error message: `error.message`, then top-level `message`/`error`
/// (string), then the first 200 chars of the raw body.
fn error_message(json: Option<&serde_json::Value>, raw: &str) -> String {
    if let Some(j) = json {
        if let Some(m) = j
            .get("error")
            .and_then(|e| e.get("message"))
            .and_then(|v| v.as_str())
        {
            return m.to_string();
        }
        if let Some(m) = j.get("message").and_then(|v| v.as_str()) {
            return m.to_string();
        }
        if let Some(m) = j.get("error").and_then(|v| v.as_str()) {
            return m.to_string();
        }
    }
    raw.chars().take(200).collect()
}

/// Parse the `Retry-After` header (integer seconds) if present.
fn retry_after(resp: &HttpResponse) -> Option<u64> {
    resp.header("Retry-After")
        .and_then(|v| v.trim().parse::<u64>().ok())
}

/// Collapse a (possibly BCP-47) language tag to the ISO-639-1 form OpenAI/Groq
/// Whisper expect: lowercase, then the first 2 chars.
///
/// PARITY: mirrors macOS `CloudWhisperProvider.swift` `lang.lowercased()` +
/// `lang.prefix(2)` (e.g. `en-US` → `en`, `zh-TW` → `zh`, `EN` → `en`). The
/// `prefix(2)` is taken over Unicode scalars; for the language tags in use
/// (ASCII letters) this is equivalent to a 2-char ASCII cut.
fn iso_639_1(lang: &str) -> String {
    lang.to_lowercase().chars().take(2).collect()
}

/// Last path component of `path`, for the multipart `filename`. Falls back to
/// `"audio"` for an empty/degenerate path.
pub fn filename_of(path: &str) -> String {
    path.rsplit(['/', '\\'])
        .next()
        .filter(|s| !s.is_empty())
        .unwrap_or("audio")
        .to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn filename_handles_unix_windows_and_empty() {
        assert_eq!(filename_of("/tmp/a/b.wav"), "b.wav");
        assert_eq!(filename_of("C:\\x\\y.m4a"), "y.m4a");
        assert_eq!(filename_of(""), "audio");
        assert_eq!(filename_of("rec.mp3"), "rec.mp3");
    }

    #[test]
    fn iso_639_1_lowercases_and_truncates() {
        // PARITY with macOS CloudWhisperProvider: lowercase + prefix(2).
        assert_eq!(iso_639_1("en-US"), "en");
        assert_eq!(iso_639_1("zh-TW"), "zh");
        assert_eq!(iso_639_1("en-GB"), "en");
        assert_eq!(iso_639_1("EN"), "en");
        assert_eq!(iso_639_1("fr"), "fr");
        assert_eq!(iso_639_1("pt-BR"), "pt");
        assert_eq!(iso_639_1("e"), "e");
        assert_eq!(iso_639_1(""), "");
    }

    fn openai_test_spec() -> OpenAiStyleSpec {
        OpenAiStyleSpec {
            endpoint: "https://example.test/v1/audio/transcriptions",
            default_model: "whisper-1",
            auth: Auth::Bearer,
            vocabulary: VocabularyMode::Prompt,
            send_model: true,
            send_response_format: true,
        }
    }

    fn language_field(req: &HttpRequest) -> Option<String> {
        match &req.body {
            Body::Multipart { parts, .. } => parts.iter().find_map(|p| match p {
                Part::Field { name, value } if name == "language" => Some(value.clone()),
                _ => None,
            }),
            _ => None,
        }
    }

    #[test]
    fn build_openai_style_normalizes_language_to_iso_639_1() {
        let spec = openai_test_spec();

        // en-US -> en
        let mut p = TranscribeParams {
            language: Some("en-US".into()),
            ..Default::default()
        };
        let req = build_openai_style(&p, &spec).unwrap();
        assert_eq!(language_field(&req).as_deref(), Some("en"));

        // zh-TW -> zh
        p.language = Some("zh-TW".into());
        let req = build_openai_style(&p, &spec).unwrap();
        assert_eq!(language_field(&req).as_deref(), Some("zh"));

        // "auto" (any case) -> field omitted
        p.language = Some("AUTO".into());
        let req = build_openai_style(&p, &spec).unwrap();
        assert_eq!(language_field(&req), None);

        // empty / None -> field omitted
        p.language = Some("   ".into());
        let req = build_openai_style(&p, &spec).unwrap();
        assert_eq!(language_field(&req), None);
        p.language = None;
        let req = build_openai_style(&p, &spec).unwrap();
        assert_eq!(language_field(&req), None);
    }

    #[test]
    fn prompt_combines_vocab_and_custom() {
        let mut p = TranscribeParams::default();
        assert_eq!(build_prompt(&p), "");
        p.vocabulary = vec!["A".into(), "B".into()];
        assert_eq!(build_prompt(&p), "A,B");
        p.prompt = Some("  Be terse.  ".into());
        assert_eq!(build_prompt(&p), "A,B Be terse.");
        p.vocabulary.clear();
        assert_eq!(build_prompt(&p), "Be terse.");
    }

    #[test]
    fn classify_408_provider_unavailable() {
        // Request timeout is transient — retryable ProviderUnavailable, not the
        // terminal BadRequest fallthrough. Must agree with retry::classify_error
        // (the two classifiers agree on every status — see retry.rs module doc).
        let resp = HttpResponse {
            status: 408,
            headers: Vec::new(),
            body: Vec::new(),
        };
        assert_eq!(
            classify_http(&resp, ""),
            TranscriptionError::ProviderUnavailable { status: 408 }
        );
        assert_eq!(
            classify_http(&resp, ""),
            crate::retry::classify_error(408, "")
        );
    }
}
