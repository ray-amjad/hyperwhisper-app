//! Mistral (Voxtral) audio transcription request/response (sans-I/O).
//!
//! `POST https://api.mistral.ai/v1/audio/transcriptions` — `multipart/form-data`.
//!
//! ## Auth (parity-critical)
//!
//! Mistral's *transcription* endpoint authenticates with the **`x-api-key`**
//! header, NOT `Authorization: Bearer`. Both shipped clients are explicit about
//! this (macOS `MistralProvider.swift`: "CRITICAL: Mistral uses x-api-key header,
//! NOT Bearer token"; Windows `MistralService.cs`: "Mistral uses x-api-key header
//! (NOT Bearer token)"). We follow the verified macOS behavior and send
//! `x-api-key`. (Mistral's *health-check* `/v1/models` endpoint uses Bearer, but
//! that path is not part of this sans-I/O builder.)
//!
//! ## Vocabulary
//!
//! Voxtral does not support custom vocabulary/prompts, so vocabulary terms are
//! dropped (no `prompt` field). Matches both shipped clients.
//!
//! ## Fields & response
//!
//! `file`, `model` (default `voxtral-mini-latest`), `language` (omitted when
//! absent / empty / `"auto"`). Response is `{ "text": "..." }`.
//!
//! Parity references: macOS `MistralProvider.swift`, Windows `MistralService.cs`.
//!
//! PARITY NOTE (field order): the shipped clients write `model`, then `language`,
//! then `file`; the shared `common` builder writes `file` first. multipart field
//! *order* is not significant to the Mistral endpoint (it parses by field name),
//! so this is a benign, intentional unification with the other providers.

use crate::contract::{HttpRequest, HttpResponse, TranscribeParams, Transcript, TranscriptionError};
use crate::providers::common::{self, Auth, OpenAiStyleSpec, VocabularyMode};

/// Mistral transcription endpoint.
pub const ENDPOINT: &str = "https://api.mistral.ai/v1/audio/transcriptions";

/// Default model when the caller leaves `params.model` empty.
/// PARITY: macOS + Windows both default to `voxtral-mini-latest`.
pub const DEFAULT_MODEL: &str = "voxtral-mini-latest";

fn spec() -> OpenAiStyleSpec {
    OpenAiStyleSpec {
        endpoint: ENDPOINT,
        default_model: DEFAULT_MODEL,
        // x-api-key, NOT Bearer — see module docs.
        auth: Auth::XApiKey,
        vocabulary: VocabularyMode::None,
        send_model: true,
        // Mistral returns { "text" } without a response_format field.
        send_response_format: false,
    }
}

/// Build the Mistral transcription request.
pub fn build_transcribe_request(
    params: &TranscribeParams,
) -> Result<HttpRequest, TranscriptionError> {
    common::build_openai_style(params, &spec())
}

/// Parse the Mistral transcription response (`{ "text": "..." }`).
pub fn parse_transcribe_response(resp: &HttpResponse) -> Result<Transcript, TranscriptionError> {
    common::parse_text_response(resp)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::contract::{Body, Header, Part};

    fn params() -> TranscribeParams {
        TranscribeParams {
            api_key: "mk-test".to_string(),
            model: "".to_string(),
            audio_path: "/tmp/rec.flac".to_string(),
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
    fn uses_x_api_key_not_bearer() {
        let req = build_transcribe_request(&params()).unwrap();
        assert!(req.headers.contains(&Header::new("x-api-key", "mk-test")));
        assert!(!req
            .headers
            .iter()
            .any(|h| h.name.eq_ignore_ascii_case("Authorization")));
    }

    #[test]
    fn default_model_and_no_prompt_or_response_format() {
        let mut p = params();
        p.vocabulary = vec!["ignored".to_string()];
        let req = build_transcribe_request(&p).unwrap();
        assert_eq!(req.url, ENDPOINT);
        match &req.body {
            Body::Multipart { parts, .. } => {
                assert!(matches!(&parts[0], Part::FileRef { field, mime, .. }
                    if field == "file" && mime == "audio/flac"));
                assert_eq!(field(parts, "model"), Some("voxtral-mini-latest"));
                // Voxtral: no vocabulary support, no response_format.
                assert_eq!(field(parts, "prompt"), None);
                assert_eq!(field(parts, "response_format"), None);
            }
            other => panic!("expected multipart, got {other:?}"),
        }
    }

    #[test]
    fn language_sent_verbatim_when_not_auto() {
        let mut p = params();
        p.language = Some("fr".to_string());
        let req = build_transcribe_request(&p).unwrap();
        if let Body::Multipart { parts, .. } = &req.body {
            assert_eq!(field(parts, "language"), Some("fr"));
        }
    }

    #[test]
    fn parses_text_response() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"text":"bonjour"}"#.to_vec(),
        };
        assert_eq!(parse_transcribe_response(&resp).unwrap().text, "bonjour");
    }

    #[test]
    fn unauthorized_and_bad_request_mapping() {
        let unauthorized = HttpResponse {
            status: 401,
            headers: vec![],
            body: br#"{"message":"Unauthorized"}"#.to_vec(),
        };
        assert_eq!(
            parse_transcribe_response(&unauthorized).unwrap_err(),
            TranscriptionError::Unauthorized
        );

        let bad = HttpResponse {
            status: 422,
            headers: vec![],
            body: br#"{"message":"invalid audio"}"#.to_vec(),
        };
        assert_eq!(
            parse_transcribe_response(&bad).unwrap_err(),
            TranscriptionError::BadRequest {
                status: 422,
                message: "invalid audio".to_string()
            }
        );
    }
}
