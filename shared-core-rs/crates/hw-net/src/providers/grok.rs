//! xAI Grok speech-to-text request/response (sans-I/O).
//!
//! `POST https://api.x.ai/v1/stt` — `multipart/form-data` with
//! `Authorization: Bearer <key>`. Response is `{ "text": "...", "language": ...,
//! "duration": ..., "words": [...] }`; we read `text`.
//!
//! ## Quirks (parity-critical)
//!
//! - **No `model` field** — Grok STT exposes a single implicit model, so no
//!   model param is sent (the model id passed by the platform is ignored).
//! - **Vocabulary is `keyterm`, repeated** — xAI takes one `keyterm` field per
//!   term (max 100 terms, each up to 50 characters). There is no free-text
//!   `prompt` field, so `params.prompt` is still dropped.
//! - **`language` + `format` are coupled.** xAI enables inverse-text-
//!   normalization (`format=true`) only when `language` is one of a fixed
//!   supported set. So we send BOTH `language` and `format=true` together — and
//!   only when the caller's selection maps to a supported code; for any other
//!   selection we send NEITHER field (the model still transcribes, just without
//!   ITN). This mirrors `GrokSTTProvider.supportedFormattingLanguage(for:)`
//!   (macOS) / `GrokSttService.TryGetSupportedFormattingLanguageCode` (Windows).
//!   Field order is `language`, then `format`, then `keyterm` (repeated), then
//!   `file` (audio last, per xAI docs and both shipped clients).
//!
//! Parity references: macOS `GrokSTTProvider.swift`, Windows `GrokSttService.cs`.

use crate::contract::{
    Body, HttpMethod, HttpRequest, HttpResponse, Part, TranscribeParams, Transcript,
    TranscriptionError,
};
use crate::helpers::{
    keyword_boost_terms, multipart_field, multipart_file, resolve_mime, MULTIPART_BOUNDARY,
};
use crate::providers::common::{self, Auth};

/// Grok STT endpoint.
pub const ENDPOINT: &str = "https://api.x.ai/v1/stt";

/// xAI `keyterm` limits: at most 100 terms, each at most 50 characters.
/// Documented for both the batch endpoint and the WebSocket endpoint.
pub const MAX_KEYTERMS: usize = 100;
pub const MAX_KEYTERM_CHARS: usize = 50;

/// xAI enables `format=true` ITN only for these language codes. Mirrors
/// `GrokSTTProvider.supportedFormattingLanguages` (macOS) /
/// `GrokSttService.SupportedFormattingLanguages` (Windows). Kept sorted for
/// readability; lookup is by membership.
pub const SUPPORTED_FORMATTING_LANGUAGES: &[&str] = &[
    "ar", "cs", "da", "de", "en", "es", "fa", "fil", "fr", "hi", "id", "it", "ja", "ko", "mk",
    "ms", "nl", "pl", "pt", "ro", "ru", "sv", "th", "tr", "vi",
];

/// macOS exposes "tl" (Tagalog) in the picker; xAI expects "fil". Alias on the
/// way out. Mirrors `GrokSTTProvider.languageAliases`.
fn alias(primary: &str) -> &str {
    match primary {
        "tl" => "fil",
        other => other,
    }
}

/// Returns the xAI-supported formatting code for the caller's selection, or
/// `None` when both `language` and `format=true` should be omitted (the "auto"
/// case, an unsupported code, or no selection). Mirrors
/// `supportedFormattingLanguage(for:)`.
pub fn supported_formatting_language(code: Option<&str>) -> Option<String> {
    let raw = code?.trim();
    if raw.is_empty() {
        return None;
    }
    let lower = raw.to_lowercase();
    if lower == "auto" {
        return None;
    }
    let primary = lower.split('-').next().unwrap_or(&lower);
    let normalized = alias(primary);
    if SUPPORTED_FORMATTING_LANGUAGES.contains(&normalized) {
        Some(normalized.to_string())
    } else {
        None
    }
}

/// Build the capped `keyterm` list: sanitize and de-duplicate through the
/// shared egress normalizer, drop terms longer than [`MAX_KEYTERM_CHARS`], then
/// take at most [`MAX_KEYTERMS`].
///
/// PARITY: the length filter counts Unicode scalar values, matching the
/// ElevenLabs keyterms builder in this crate — see `elevenlabs::keyterms` for
/// why `chars().count()` is the cross-platform middle ground.
pub fn keyterms(vocabulary: &[String]) -> Vec<String> {
    keyword_boost_terms(vocabulary, None)
        .into_iter()
        .filter(|w| w.chars().count() <= MAX_KEYTERM_CHARS)
        .take(MAX_KEYTERMS)
        .collect()
}

/// Build the Grok STT transcription request.
pub fn build_transcribe_request(
    params: &TranscribeParams,
) -> Result<HttpRequest, TranscriptionError> {
    let mime = params
        .audio_mime
        .clone()
        .unwrap_or_else(|| resolve_mime(&params.audio_path));

    let mut parts: Vec<Part> = Vec::new();

    // language + format together, only for supported codes — and before the file.
    if let Some(lang) = supported_formatting_language(params.language.as_deref()) {
        parts.push(multipart_field("language", lang));
        parts.push(multipart_field("format", "true"));
    }

    // keyterm — one field per term, capped at 100 terms / 50 chars, before the file.
    for term in keyterms(&params.vocabulary) {
        parts.push(multipart_field("keyterm", term));
    }

    // audio last (per xAI docs).
    parts.push(multipart_file(
        "file",
        params.audio_path.clone(),
        mime,
        common::filename_of(&params.audio_path),
    ));

    Ok(HttpRequest {
        method: HttpMethod::Post,
        url: ENDPOINT.to_string(),
        headers: vec![common::auth_header(Auth::Bearer, &params.api_key)],
        body: Body::Multipart {
            boundary: MULTIPART_BOUNDARY.to_string(),
            parts,
        },
    })
}

/// Parse the Grok STT response (`{ "text": "..." }`).
pub fn parse_transcribe_response(resp: &HttpResponse) -> Result<Transcript, TranscriptionError> {
    common::parse_text_response(resp)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::contract::{Body, Header, Part};

    fn params() -> TranscribeParams {
        TranscribeParams {
            api_key: "xai-test".to_string(),
            model: "ignored-model".to_string(),
            audio_path: "/tmp/rec.mp3".to_string(),
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
    fn no_model_field_and_bearer_auth() {
        let req = build_transcribe_request(&params()).unwrap();
        assert_eq!(req.url, ENDPOINT);
        assert!(req
            .headers
            .contains(&Header::new("Authorization", "Bearer xai-test")));
        if let Body::Multipart { parts, .. } = &req.body {
            assert_eq!(field(parts, "model"), None);
            // file present.
            assert!(parts.iter().any(|p| matches!(p, Part::FileRef { field, mime, .. }
                if field == "file" && mime == "audio/mpeg")));
        } else {
            panic!("expected multipart");
        }
    }

    #[test]
    fn supported_language_sends_language_and_format_before_file() {
        let mut p = params();
        p.language = Some("en-US".to_string());
        let req = build_transcribe_request(&p).unwrap();
        if let Body::Multipart { parts, .. } = &req.body {
            // language and format come before the file part.
            assert!(matches!(&parts[0], Part::Field { name, value } if name == "language" && value == "en"));
            assert!(matches!(&parts[1], Part::Field { name, value } if name == "format" && value == "true"));
            assert!(matches!(&parts[2], Part::FileRef { .. }));
        } else {
            panic!("expected multipart");
        }
    }

    #[test]
    fn tagalog_aliases_to_fil() {
        assert_eq!(supported_formatting_language(Some("tl")), Some("fil".to_string()));
    }

    #[test]
    fn unsupported_and_auto_languages_omit_both_fields() {
        for lang in ["auto", "xx", "zh", ""] {
            let mut p = params();
            p.language = Some(lang.to_string());
            let req = build_transcribe_request(&p).unwrap();
            if let Body::Multipart { parts, .. } = &req.body {
                assert_eq!(field(parts, "language"), None, "lang={lang}");
                assert_eq!(field(parts, "format"), None, "lang={lang}");
            }
        }
    }

    #[test]
    fn vocabulary_becomes_repeated_keyterm_fields_before_the_file() {
        let mut p = params();
        p.language = Some("en".to_string());
        p.vocabulary = vec!["HyperWhisper".to_string(), "UniFFI".to_string()];
        let req = build_transcribe_request(&p).unwrap();
        if let Body::Multipart { parts, .. } = &req.body {
            assert_eq!(fields(parts, "keyterm"), vec!["HyperWhisper", "UniFFI"]);
            // language, format, then the keyterms, then the file.
            assert!(matches!(&parts[0], Part::Field { name, .. } if name == "language"));
            assert!(matches!(&parts[1], Part::Field { name, .. } if name == "format"));
            assert!(matches!(&parts[2], Part::Field { name, value } if name == "keyterm" && value == "HyperWhisper"));
            assert!(matches!(&parts[4], Part::FileRef { .. }));
        } else {
            panic!("expected multipart");
        }
    }

    #[test]
    fn keyterms_are_sent_without_a_language_selection() {
        let mut p = params();
        p.vocabulary = vec!["Kubernetes".to_string()];
        let req = build_transcribe_request(&p).unwrap();
        if let Body::Multipart { parts, .. } = &req.body {
            assert_eq!(field(parts, "language"), None);
            assert_eq!(fields(parts, "keyterm"), vec!["Kubernetes"]);
        } else {
            panic!("expected multipart");
        }
    }

    #[test]
    fn empty_vocabulary_sends_no_keyterm_field() {
        let req = build_transcribe_request(&params()).unwrap();
        if let Body::Multipart { parts, .. } = &req.body {
            assert!(fields(parts, "keyterm").is_empty());
        } else {
            panic!("expected multipart");
        }
    }

    #[test]
    fn keyterms_drop_over_long_terms_and_cap_at_100() {
        let long = "x".repeat(MAX_KEYTERM_CHARS + 1);
        let mut vocab = vec![long, "kept".to_string()];
        vocab.extend((0..MAX_KEYTERMS).map(|i| format!("term{i}")));
        let mut p = params();
        p.vocabulary = vocab;
        let req = build_transcribe_request(&p).unwrap();
        if let Body::Multipart { parts, .. } = &req.body {
            let sent = fields(parts, "keyterm");
            assert_eq!(sent.len(), MAX_KEYTERMS);
            assert_eq!(sent[0], "kept");
            assert!(!sent.iter().any(|t| t.chars().count() > MAX_KEYTERM_CHARS));
        } else {
            panic!("expected multipart");
        }
    }

    #[test]
    fn keyterms_deduplicate_case_insensitively() {
        let mut p = params();
        p.vocabulary = vec![
            "HyperWhisper".to_string(),
            "hyperwhisper".to_string(),
            "  ".to_string(),
        ];
        let req = build_transcribe_request(&p).unwrap();
        if let Body::Multipart { parts, .. } = &req.body {
            assert_eq!(fields(parts, "keyterm"), vec!["HyperWhisper"]);
        } else {
            panic!("expected multipart");
        }
    }

    #[test]
    fn parses_text_response() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"text":"grok says hi","language":"en","duration":1.2}"#.to_vec(),
        };
        assert_eq!(parse_transcribe_response(&resp).unwrap().text, "grok says hi");
    }

    #[test]
    fn empty_text_is_no_speech() {
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
}
