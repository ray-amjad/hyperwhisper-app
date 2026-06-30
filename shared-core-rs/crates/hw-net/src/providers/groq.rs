//! Groq audio transcription request/response (sans-I/O).
//!
//! `POST https://api.groq.com/openai/v1/audio/transcriptions` — OpenAI-compatible
//! `multipart/form-data` with `Authorization: Bearer <key>`. Vocabulary goes into
//! the `prompt` field as comma-separated CSV; the response is `{ "text": "..." }`.
//!
//! Groq's transcription API is byte-for-byte OpenAI-compatible (Groq deliberately
//! mirrors OpenAI's `/audio/transcriptions` surface), so this module reuses the
//! exact same builder/parser as [`crate::providers::openai`] via
//! [`crate::providers::common`] — differing only in endpoint + default model.
//!
//! Parity references:
//! - macOS `CloudWhisperProvider.swift` (the `.groq` branch)
//! - Windows `GroqWhisperService.cs`
//!
//! See [`crate::providers::openai`] for the `response_format`/`prompt`
//! divergence notes (identical here).

use crate::contract::{HttpRequest, HttpResponse, TranscribeParams, Transcript, TranscriptionError};
use crate::providers::common::{self, Auth, OpenAiStyleSpec, VocabularyMode};

/// Groq transcription endpoint (OpenAI-compatible path).
pub const ENDPOINT: &str = "https://api.groq.com/openai/v1/audio/transcriptions";

/// Default model when the caller leaves `params.model` empty.
/// PARITY: macOS `CloudTranscriptionModels.defaultModel(.groq)` / Windows
/// `GroqWhisperService` both default to `whisper-large-v3-turbo`.
pub const DEFAULT_MODEL: &str = "whisper-large-v3-turbo";

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

/// Build the Groq transcription request.
pub fn build_transcribe_request(
    params: &TranscribeParams,
) -> Result<HttpRequest, TranscriptionError> {
    common::build_openai_style(params, &spec())
}

/// Parse the Groq transcription response (`{ "text": "..." }`).
pub fn parse_transcribe_response(resp: &HttpResponse) -> Result<Transcript, TranscriptionError> {
    common::parse_text_response(resp)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::contract::{Body, Header, Part};

    fn params() -> TranscribeParams {
        TranscribeParams {
            api_key: "gsk_test".to_string(),
            model: "".to_string(),
            audio_path: "/tmp/rec.wav".to_string(),
            vocabulary: vec!["Kubernetes".to_string()],
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
    fn endpoint_default_model_auth_and_vocab() {
        let req = build_transcribe_request(&params()).unwrap();
        assert_eq!(req.url, ENDPOINT);
        assert!(req
            .headers
            .contains(&Header::new("Authorization", "Bearer gsk_test")));
        match &req.body {
            Body::Multipart { parts, .. } => {
                assert!(matches!(&parts[0], Part::FileRef { field, mime, .. }
                    if field == "file" && mime == "audio/wav"));
                assert_eq!(field(parts, "model"), Some("whisper-large-v3-turbo"));
                assert_eq!(field(parts, "prompt"), Some("Kubernetes"));
                assert_eq!(field(parts, "response_format"), Some("json"));
            }
            other => panic!("expected multipart, got {other:?}"),
        }
    }

    #[test]
    fn parses_text_response() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"text":"groq output"}"#.to_vec(),
        };
        assert_eq!(parse_transcribe_response(&resp).unwrap().text, "groq output");
    }

    #[test]
    fn server_error_maps_to_provider_unavailable() {
        let resp = HttpResponse {
            status: 503,
            headers: vec![],
            body: b"upstream down".to_vec(),
        };
        assert_eq!(
            parse_transcribe_response(&resp).unwrap_err(),
            TranscriptionError::ProviderUnavailable { status: 503 }
        );
    }
}
