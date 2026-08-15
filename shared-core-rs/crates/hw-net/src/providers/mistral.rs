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
//! Voxtral has no free-text `prompt` field. It takes a STRUCTURED
//! `context_bias` list instead — one field per term, at most
//! [`MAX_CONTEXT_BIAS_TERMS`], with whitespace and commas folded to `_` because
//! Voxtral's `comma_separated` validator 400s an item that contains either.
//! `params.prompt` (custom instructions) is still dropped, since there is no
//! field to carry it.
//!
//! PARITY: `hyperwhisper-cloud/src/providers/mistral.ts` sends the same field
//! with the same 100-term cap and the same `\s+ → _` substitution on the routed
//! path. Mistral's API schema types
//! `context_bias` as an array, so it is one form field per term — a single
//! comma-joined value would bias one literal phrase containing commas.
//!
//! ## Fields & response
//!
//! `file`, `model` (default `voxtral-mini-latest`), `language` (omitted when
//! absent / empty / `"auto"`), `context_bias` (repeated, omitted when the
//! vocabulary is empty). Response is `{ "text": "..." }`.
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

/// Mistral caps `context_bias` at 100 phrases.
pub const MAX_CONTEXT_BIAS_TERMS: usize = 100;

fn spec() -> OpenAiStyleSpec {
    OpenAiStyleSpec {
        endpoint: ENDPOINT,
        default_model: DEFAULT_MODEL,
        // x-api-key, NOT Bearer — see module docs.
        auth: Auth::XApiKey,
        vocabulary: VocabularyMode::Terms {
            name: "context_bias",
            max_terms: MAX_CONTEXT_BIAS_TERMS,
            // Voxtral rejects a context_bias item containing whitespace or a
            // comma with HTTP 400 — see VocabularyMode::Terms.
            underscore_separators: true,
        },
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
        let req = build_transcribe_request(&params()).unwrap();
        assert_eq!(req.url, ENDPOINT);
        match &req.body {
            Body::Multipart { parts, .. } => {
                assert!(matches!(&parts[0], Part::FileRef { field, mime, .. }
                    if field == "file" && mime == "audio/flac"));
                assert_eq!(field(parts, "model"), Some("voxtral-mini-latest"));
                // Voxtral: no free-text prompt field, no response_format.
                assert_eq!(field(parts, "prompt"), None);
                assert_eq!(field(parts, "response_format"), None);
                assert!(fields(parts, "context_bias").is_empty());
            }
            other => panic!("expected multipart, got {other:?}"),
        }
    }

    #[test]
    fn vocabulary_becomes_repeated_context_bias_fields() {
        let mut p = params();
        p.vocabulary = vec!["HyperWhisper".to_string(), "UniFFI".to_string()];
        let req = build_transcribe_request(&p).unwrap();
        if let Body::Multipart { parts, .. } = &req.body {
            assert_eq!(
                fields(parts, "context_bias"),
                vec!["HyperWhisper", "UniFFI"]
            );
            // The custom-instructions prompt still has no field to go in.
            assert_eq!(field(parts, "prompt"), None);
        } else {
            panic!("expected multipart");
        }
    }

    #[test]
    fn context_bias_deduplicates_and_caps_at_100() {
        let mut p = params();
        p.vocabulary = std::iter::once("Kept".to_string())
            .chain(std::iter::once("kept".to_string()))
            .chain((0..MAX_CONTEXT_BIAS_TERMS).map(|i| format!("term{i}")))
            .collect();
        let req = build_transcribe_request(&p).unwrap();
        if let Body::Multipart { parts, .. } = &req.body {
            let sent = fields(parts, "context_bias");
            assert_eq!(sent.len(), MAX_CONTEXT_BIAS_TERMS);
            assert_eq!(sent[0], "Kept");
            assert_eq!(sent[1], "term0");
        } else {
            panic!("expected multipart");
        }
    }

    #[test]
    fn prompt_only_still_sends_nothing() {
        let mut p = params();
        p.prompt = Some("Format as bullet points.".to_string());
        let req = build_transcribe_request(&p).unwrap();
        if let Body::Multipart { parts, .. } = &req.body {
            assert_eq!(field(parts, "prompt"), None);
            assert!(fields(parts, "context_bias").is_empty());
        } else {
            panic!("expected multipart");
        }
    }

    #[test]
    fn multi_word_and_comma_terms_are_underscored_not_rejected() {
        // Voxtral's comma_separated validator 400s an item containing
        // whitespace or a comma, so the builder must fold both to "_".
        let mut p = params();
        p.vocabulary = vec![
            "Claude Code".to_string(),
            "Smith,  Jr.".to_string(),
            "HyperWhisper".to_string(),
        ];
        let req = build_transcribe_request(&p).unwrap();
        if let Body::Multipart { parts, .. } = &req.body {
            let sent = fields(parts, "context_bias");
            assert_eq!(sent, vec!["Claude_Code", "Smith_Jr.", "HyperWhisper"]);
            assert!(!sent.iter().any(|t| t.contains(' ') || t.contains(',')));
        } else {
            panic!("expected multipart");
        }
    }

    #[test]
    fn language_sent_verbatim_when_not_auto() {
        let mut p = params();
        p.language = Some("fr".to_string());
        let req = build_transcribe_request(&p).unwrap();
        if let Body::Multipart { parts, .. } = &req.body {
            assert_eq!(field(parts, "language"), Some("fr"));
        } else {
            panic!("expected multipart");
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
