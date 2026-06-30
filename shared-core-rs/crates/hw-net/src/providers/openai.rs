//! OpenAI audio transcription request/response (sans-I/O).
//!
//! `POST https://api.openai.com/v1/audio/transcriptions` — `multipart/form-data`
//! with `Authorization: Bearer <key>`. Vocabulary is injected into the `prompt`
//! field as comma-separated CSV; the parsed response is `{ "text": "..." }`.
//!
//! Parity references:
//! - macOS `CloudWhisperProvider.swift` (the `.openai` branch)
//! - Windows `OpenAIWhisperService.cs`
//!
//! ## Field order (matches the shipped multipart assembly)
//!
//! `file`, `model`, `language` (omitted when absent / empty / `"auto"`),
//! `prompt` (omitted when empty), `response_format`.
//!
//! ## Parity notes / unification choices
//!
//! - **`response_format`**: macOS sends `text` and parses the raw body; Windows
//!   sends `json` and parses `{ "text": "..." }`. A sans-I/O core must parse a
//!   *structured* response, so we send **`json`** (Windows behavior) and parse
//!   the `text` field. Documented divergence from macOS; only this one field
//!   value differs on the wire and the `text` string is identical.
//! - **`prompt` (vocabulary)**: macOS/Windows wrap the terms as
//!   `"Important terms to recognize: a, b, c. "` (comma-**space**) then append
//!   the mode's custom instructions. We send the bare `vocabulary_csv` (comma,
//!   no space) per the shared-core contract (`helpers::vocabulary_csv`), then
//!   append `params.prompt` (custom instructions) when present. Documented
//!   divergence — keeps vocabulary handling uniform and deterministic across
//!   providers; the preamble is a soft hint that does not change recognition.

use crate::contract::{HttpRequest, HttpResponse, TranscribeParams, Transcript, TranscriptionError};
use crate::providers::common::{self, Auth, OpenAiStyleSpec, VocabularyMode};

/// OpenAI transcription endpoint.
pub const ENDPOINT: &str = "https://api.openai.com/v1/audio/transcriptions";

/// Default model when the caller leaves `params.model` empty.
/// PARITY: macOS `CloudTranscriptionModels.defaultModel(.openai)` / Windows
/// `OpenAIWhisperService` both default to `whisper-1`.
pub const DEFAULT_MODEL: &str = "whisper-1";

fn spec() -> OpenAiStyleSpec {
    OpenAiStyleSpec {
        endpoint: ENDPOINT,
        default_model: DEFAULT_MODEL,
        auth: Auth::Bearer,
        vocabulary: VocabularyMode::Prompt,
        send_model: true,
        send_response_format: true,
    }
}

/// Build the OpenAI transcription request.
pub fn build_transcribe_request(
    params: &TranscribeParams,
) -> Result<HttpRequest, TranscriptionError> {
    common::build_openai_style(params, &spec())
}

/// Parse the OpenAI transcription response (`{ "text": "..." }`).
pub fn parse_transcribe_response(resp: &HttpResponse) -> Result<Transcript, TranscriptionError> {
    common::parse_text_response(resp)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::contract::{Body, Header, HttpMethod, Part};
    use crate::helpers::MULTIPART_BOUNDARY;

    fn params() -> TranscribeParams {
        TranscribeParams {
            api_key: "sk-test".to_string(),
            model: "gpt-4o-transcribe".to_string(),
            audio_path: "/tmp/rec.m4a".to_string(),
            ..Default::default()
        }
    }

    fn field<'a>(parts: &'a [Part], name: &str) -> Option<&'a str> {
        parts.iter().find_map(|p| match p {
            Part::Field { name: n, value } if n == name => Some(value.as_str()),
            _ => None,
        })
    }

    #[test]
    fn builds_bearer_auth_and_endpoint() {
        let req = build_transcribe_request(&params()).unwrap();
        assert_eq!(req.url, ENDPOINT);
        assert_eq!(req.method, HttpMethod::Post);
        assert!(req
            .headers
            .contains(&Header::new("Authorization", "Bearer sk-test")));
    }

    #[test]
    fn multipart_field_order_and_values() {
        let req = build_transcribe_request(&params()).unwrap();
        match &req.body {
            Body::Multipart { boundary, parts } => {
                assert_eq!(boundary, MULTIPART_BOUNDARY);
                assert!(matches!(&parts[0], Part::FileRef { field, mime, filename, .. }
                    if field == "file" && mime == "audio/mp4" && filename == "rec.m4a"));
                assert_eq!(field(parts, "model"), Some("gpt-4o-transcribe"));
                assert_eq!(field(parts, "response_format"), Some("json"));
                assert_eq!(field(parts, "language"), None);
                assert_eq!(field(parts, "prompt"), None);
            }
            other => panic!("expected multipart, got {other:?}"),
        }
    }

    #[test]
    fn empty_model_defaults_to_whisper_1() {
        let mut p = params();
        p.model = "".to_string();
        let req = build_transcribe_request(&p).unwrap();
        if let Body::Multipart { parts, .. } = &req.body {
            assert_eq!(field(parts, "model"), Some("whisper-1"));
        } else {
            panic!("expected multipart");
        }
    }

    #[test]
    fn language_sent_only_when_not_auto() {
        let mut p = params();
        p.language = Some("auto".to_string());
        let req = build_transcribe_request(&p).unwrap();
        if let Body::Multipart { parts, .. } = &req.body {
            assert_eq!(field(parts, "language"), None);
        }

        p.language = Some("en".to_string());
        let req = build_transcribe_request(&p).unwrap();
        if let Body::Multipart { parts, .. } = &req.body {
            assert_eq!(field(parts, "language"), Some("en"));
        }
    }

    #[test]
    fn vocabulary_goes_into_prompt_as_bare_csv() {
        let mut p = params();
        p.vocabulary = vec!["Rust".to_string(), "UniFFI".to_string()];
        let req = build_transcribe_request(&p).unwrap();
        if let Body::Multipart { parts, .. } = &req.body {
            assert_eq!(field(parts, "prompt"), Some("Rust,UniFFI"));
        }
    }

    #[test]
    fn prompt_appends_custom_instructions_after_vocab() {
        let mut p = params();
        p.vocabulary = vec!["Rust".to_string()];
        p.prompt = Some("Format as bullet points.".to_string());
        let req = build_transcribe_request(&p).unwrap();
        if let Body::Multipart { parts, .. } = &req.body {
            assert_eq!(field(parts, "prompt"), Some("Rust Format as bullet points."));
        }
    }

    #[test]
    fn parses_text_response() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"text":"hello world"}"#.to_vec(),
        };
        let t = parse_transcribe_response(&resp).unwrap();
        assert_eq!(t.text, "hello world");
    }

    #[test]
    fn empty_text_maps_to_no_speech() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"text":""}"#.to_vec(),
        };
        assert_eq!(
            parse_transcribe_response(&resp).unwrap_err(),
            TranscriptionError::NoSpeech
        );
    }

    #[test]
    fn http_429_maps_to_rate_limited_with_retry_after() {
        let resp = HttpResponse {
            status: 429,
            headers: vec![Header::new("Retry-After", "12")],
            body: br#"{"error":{"message":"slow down"}}"#.to_vec(),
        };
        assert_eq!(
            parse_transcribe_response(&resp).unwrap_err(),
            TranscriptionError::RateLimited {
                retry_after_secs: Some(12)
            }
        );
    }

    #[test]
    fn insufficient_quota_429_maps_to_quota_exceeded() {
        let resp = HttpResponse {
            status: 429,
            headers: vec![],
            body: br#"{"error":{"code":"insufficient_quota","message":"You exceeded your quota"}}"#
                .to_vec(),
        };
        assert_eq!(
            parse_transcribe_response(&resp).unwrap_err(),
            TranscriptionError::QuotaExceeded
        );
    }

    #[test]
    fn http_401_maps_to_unauthorized() {
        let resp = HttpResponse {
            status: 401,
            headers: vec![],
            body: br#"{"error":{"message":"bad key"}}"#.to_vec(),
        };
        assert_eq!(
            parse_transcribe_response(&resp).unwrap_err(),
            TranscriptionError::Unauthorized
        );
    }
}
