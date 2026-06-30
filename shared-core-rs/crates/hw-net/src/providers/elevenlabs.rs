//! ElevenLabs Scribe speech-to-text request/response (sans-I/O).
//!
//! `POST https://api.elevenlabs.io/v1/speech-to-text` — `multipart/form-data`
//! authenticated with the **`xi-api-key`** header (NOT `Authorization: Bearer`).
//!
//! ## Fields (order matches the shipped clients)
//!
//! `model_id` (default `scribe_v2`), `tag_audio_events=false`, `language_code`
//! (omitted when absent / empty / `"auto"`; normalized to the primary subtag,
//! e.g. `en-US` → `en`), repeated `keyterms` (Scribe v2 only — see below),
//! then `file`.
//!
//! ## Vocabulary (Scribe v2 only)
//!
//! Scribe **v2** accepts vocabulary as repeated `keyterms` form fields, capped at
//! [`ELEVENLABS_MAX_TERMS`] (100) terms, each dropped if longer than
//! [`ELEVENLABS_MAX_TERM_CHARS`] (50) chars. Scribe **v1** does not support
//! vocabulary, so terms are silently dropped. Mirrors `ElevenLabsProvider.swift`
//! and `ElevenLabsService.cs`.
//!
//! ## Response shape
//!
//! Prefer top-level `text`; else join `transcripts[].text` with `"\n"`; else join
//! `words[].text` with `" "`; an explicit non-empty `error` string is a bad
//! request; all-empty → [`TranscriptionError::NoSpeech`]. Mirrors `parseTranscript`.
//!
//! Parity references: macOS `ElevenLabsProvider.swift`, Windows `ElevenLabsService.cs`.

use crate::contract::{
    Body, Header, HttpMethod, HttpRequest, HttpResponse, Part, TranscribeParams, Transcript,
    TranscriptionError,
};
use crate::helpers::{
    keyword_boost_terms, multipart_field, multipart_file, resolve_mime, ELEVENLABS_MAX_TERMS,
    ELEVENLABS_MAX_TERM_CHARS, MULTIPART_BOUNDARY,
};
use crate::providers::common;

/// ElevenLabs Scribe endpoint.
pub const ENDPOINT: &str = "https://api.elevenlabs.io/v1/speech-to-text";

/// Auth header name (NOT `Authorization`).
pub const HEADER_API_KEY: &str = "xi-api-key";

/// Default model when the caller leaves `params.model` empty.
/// PARITY: macOS `CloudTranscriptionModels.defaultModel(.elevenLabs)` / Windows
/// `ElevenLabsService` both default to `scribe_v2`.
pub const DEFAULT_MODEL: &str = "scribe_v2";

/// The Scribe v2 model id (the only one that supports keyterm vocabulary).
pub const SCRIBE_V2: &str = "scribe_v2";

/// Build the ElevenLabs Scribe transcription request.
pub fn build_transcribe_request(
    params: &TranscribeParams,
) -> Result<HttpRequest, TranscriptionError> {
    let model_id = if params.model.trim().is_empty() {
        DEFAULT_MODEL.to_string()
    } else {
        params.model.clone()
    };

    let mime = params
        .audio_mime
        .clone()
        .unwrap_or_else(|| resolve_mime(&params.audio_path));

    let mut parts: Vec<Part> = Vec::new();
    parts.push(multipart_field("model_id", model_id.clone()));
    parts.push(multipart_field("tag_audio_events", "false"));

    // language_code — omitted when absent / empty / "auto"; normalized to the
    // primary subtag (e.g. en-US → en). Mirrors normalizeLanguageCode.
    if let Some(lang) = params.language.as_deref() {
        let trimmed = lang.trim();
        if !trimmed.is_empty() && !trimmed.eq_ignore_ascii_case("auto") {
            parts.push(multipart_field(
                "language_code",
                normalize_language(trimmed),
            ));
        }
    }

    // keyterms — Scribe v2 only, capped 100 terms, drop terms > 50 chars.
    if model_id == SCRIBE_V2 {
        for term in keyterms(&params.vocabulary) {
            parts.push(multipart_field("keyterms", term));
        }
    }

    // file last.
    parts.push(multipart_file(
        "file",
        params.audio_path.clone(),
        mime,
        common::filename_of(&params.audio_path),
    ));

    Ok(HttpRequest {
        method: HttpMethod::Post,
        url: ENDPOINT.to_string(),
        headers: vec![
            Header::new(HEADER_API_KEY, params.api_key.clone()),
            Header::new("Accept", "application/json"),
        ],
        body: Body::Multipart {
            boundary: MULTIPART_BOUNDARY.to_string(),
            parts,
        },
    })
}

/// Build the capped keyterms list: trim, drop empties, drop terms longer than
/// [`ELEVENLABS_MAX_TERM_CHARS`], take at most [`ELEVENLABS_MAX_TERMS`].
///
/// PARITY: the length filter counts Unicode scalar values (Swift `String.count`
/// counts grapheme clusters and C# `string.Length` counts UTF-16 code units;
/// `chars().count()` is the closest deterministic, dependency-free middle and
/// matches both for the ASCII/BMP terms that dominate real vocabularies).
fn keyterms(vocabulary: &[String]) -> Vec<String> {
    keyword_boost_terms(vocabulary, None)
        .into_iter()
        .filter(|w| w.chars().count() <= ELEVENLABS_MAX_TERM_CHARS)
        .take(ELEVENLABS_MAX_TERMS)
        .collect()
}

/// Normalize a BCP-47 tag to its primary subtag (`en-US` → `en`).
fn normalize_language(code: &str) -> String {
    code.split('-').next().unwrap_or(code).to_string()
}

/// Parse the ElevenLabs Scribe response.
pub fn parse_transcribe_response(resp: &HttpResponse) -> Result<Transcript, TranscriptionError> {
    let raw = resp.text();

    if !(200..=299).contains(&resp.status) {
        return Err(common::classify_http(resp, &raw));
    }

    let json: serde_json::Value =
        serde_json::from_str(&raw).map_err(|e| TranscriptionError::Parse {
            message: format!("invalid JSON: {e}"),
        })?;

    // Explicit error string → bad request (matches parseTranscript).
    if let Some(err) = json.get("error").and_then(|v| v.as_str()) {
        if !err.is_empty() {
            return Err(TranscriptionError::BadRequest {
                status: resp.status,
                message: err.to_string(),
            });
        }
    }

    // 1) top-level text
    if let Some(text) = json.get("text").and_then(|v| v.as_str()) {
        if !text.is_empty() {
            return Ok(transcript(text));
        }
    }

    // 2) transcripts[].text joined with "\n"
    if let Some(arr) = json.get("transcripts").and_then(|v| v.as_array()) {
        let combined = join_text_field(arr, "\n");
        if !combined.is_empty() {
            return Ok(transcript(&combined));
        }
    }

    // 3) words[].text joined with " "
    if let Some(arr) = json.get("words").and_then(|v| v.as_array()) {
        let combined = join_text_field(arr, " ");
        if !combined.is_empty() {
            return Ok(transcript(&combined));
        }
    }

    // All text fields empty = no speech detected (valid for silent audio).
    Err(TranscriptionError::NoSpeech)
}

fn join_text_field(arr: &[serde_json::Value], sep: &str) -> String {
    arr.iter()
        .filter_map(|v| v.get("text").and_then(|t| t.as_str()))
        .collect::<Vec<_>>()
        .join(sep)
}

fn transcript(text: &str) -> Transcript {
    Transcript {
        text: text.to_string(),
        credits_remaining: None,
        cost: None,
        raw_provider: None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::contract::{Body, Part};

    fn params() -> TranscribeParams {
        TranscribeParams {
            api_key: "xi-test".to_string(),
            model: "".to_string(),
            audio_path: "/tmp/rec.wav".to_string(),
            ..Default::default()
        }
    }

    fn fields<'a>(parts: &'a [Part], name: &str) -> Vec<&'a str> {
        parts
            .iter()
            .filter_map(|p| match p {
                Part::Field { name: n, value } if n == name => Some(value.as_str()),
                _ => None,
            })
            .collect()
    }

    #[test]
    fn uses_xi_api_key_and_default_model() {
        let req = build_transcribe_request(&params()).unwrap();
        assert_eq!(req.url, ENDPOINT);
        assert!(req.headers.contains(&Header::new("xi-api-key", "xi-test")));
        assert!(!req
            .headers
            .iter()
            .any(|h| h.name.eq_ignore_ascii_case("Authorization")));
        if let Body::Multipart { parts, .. } = &req.body {
            assert_eq!(fields(parts, "model_id"), vec!["scribe_v2"]);
            assert_eq!(fields(parts, "tag_audio_events"), vec!["false"]);
            // file is last.
            assert!(matches!(parts.last(), Some(Part::FileRef { field, .. }) if field == "file"));
        } else {
            panic!("expected multipart");
        }
    }

    #[test]
    fn language_code_normalized_and_skipped_for_auto() {
        let mut p = params();
        p.language = Some("en-US".to_string());
        let req = build_transcribe_request(&p).unwrap();
        if let Body::Multipart { parts, .. } = &req.body {
            assert_eq!(fields(parts, "language_code"), vec!["en"]);
        }

        p.language = Some("auto".to_string());
        let req = build_transcribe_request(&p).unwrap();
        if let Body::Multipart { parts, .. } = &req.body {
            assert!(fields(parts, "language_code").is_empty());
        }
    }

    #[test]
    fn scribe_v2_emits_keyterms_capped_and_filtered() {
        let mut p = params();
        let long = "x".repeat(ELEVENLABS_MAX_TERM_CHARS + 1);
        let ok = "x".repeat(ELEVENLABS_MAX_TERM_CHARS);
        p.vocabulary = vec![
            "Rust".to_string(),
            "rust".to_string(), // duplicate dropped by shared egress helper
            "API<script>".to_string(),
            "  ".to_string(), // empty after trim → dropped
            long,             // > 50 chars → dropped
            ok.clone(),       // exactly 50 → kept
        ];
        let req = build_transcribe_request(&p).unwrap();
        if let Body::Multipart { parts, .. } = &req.body {
            assert_eq!(
                fields(parts, "keyterms"),
                vec!["Rust", "APIscript", ok.as_str()]
            );
        }
    }

    #[test]
    fn keyterms_capped_at_100() {
        let mut p = params();
        p.vocabulary = (0..150).map(|i| format!("t{i}")).collect();
        let req = build_transcribe_request(&p).unwrap();
        if let Body::Multipart { parts, .. } = &req.body {
            assert_eq!(fields(parts, "keyterms").len(), ELEVENLABS_MAX_TERMS);
        }
    }

    #[test]
    fn scribe_v1_drops_all_vocabulary() {
        let mut p = params();
        p.model = "scribe_v1".to_string();
        p.vocabulary = vec!["Rust".to_string()];
        let req = build_transcribe_request(&p).unwrap();
        if let Body::Multipart { parts, .. } = &req.body {
            assert!(fields(parts, "keyterms").is_empty());
            assert_eq!(fields(parts, "model_id"), vec!["scribe_v1"]);
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
    fn parses_top_level_text() {
        let t = parse_transcribe_response(&resp(
            200,
            r#"{"text":"hello scribe","language_code":"en"}"#,
        ))
        .unwrap();
        assert_eq!(t.text, "hello scribe");
    }

    #[test]
    fn falls_back_to_transcripts_then_words() {
        let t = parse_transcribe_response(&resp(
            200,
            r#"{"text":"","transcripts":[{"text":"line one"},{"text":"line two"}]}"#,
        ))
        .unwrap();
        assert_eq!(t.text, "line one\nline two");

        let t = parse_transcribe_response(&resp(
            200,
            r#"{"words":[{"text":"a"},{"text":"b"},{"text":"c"}]}"#,
        ))
        .unwrap();
        assert_eq!(t.text, "a b c");
    }

    #[test]
    fn all_empty_is_no_speech() {
        assert_eq!(
            parse_transcribe_response(&resp(200, r#"{"text":""}"#)).unwrap_err(),
            TranscriptionError::NoSpeech
        );
    }

    #[test]
    fn explicit_error_string_is_bad_request() {
        let err =
            parse_transcribe_response(&resp(200, r#"{"error":"invalid model"}"#)).unwrap_err();
        assert_eq!(
            err,
            TranscriptionError::BadRequest {
                status: 200,
                message: "invalid model".to_string()
            }
        );
    }

    #[test]
    fn http_413_is_file_too_large() {
        assert_eq!(
            parse_transcribe_response(&resp(413, "too big")).unwrap_err(),
            TranscriptionError::FileTooLarge
        );
    }
}
