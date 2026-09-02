//! Meta Muse Voice Transcribe batch/file request and response (sans-I/O).
//!
//! `POST https://api.meta.ai/v1/asr/transcribe` uses a documented multipart
//! shape with a small JSON file part named `request` and a disk-streamed WAV
//! part named `audio`. The JSON crosses FFI as [`Part::InlineFile`]; audio never
//! does and remains a [`Part::FileRef`]. This module is the wire source of truth
//! for every direct desktop implementation.

use serde::{Deserialize, Serialize};

use crate::contract::{
    Body, Header, HttpMethod, HttpRequest, HttpResponse, TranscribeParams, Transcript,
    TranscriptionError,
};
use crate::helpers::{
    keyword_boost_terms, multipart_file, multipart_inline_file, resolve_mime, MULTIPART_BOUNDARY,
};
use crate::providers::common;

pub const ENDPOINT: &str = "https://api.meta.ai/v1/asr/transcribe";
pub const DEFAULT_MODEL: &str = "muse-voice-transcribe-1.0";
pub const MAX_KEYWORDS: usize = 100;

#[derive(Debug, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
struct MetaRequest<'a> {
    model: &'a str,
    audio_encoding: &'static str,
    mode: &'static str,
    #[serde(skip_serializing_if = "Option::is_none")]
    keywords: Option<Vec<String>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    language_bias: Option<Vec<&'static str>>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct MetaResponse {
    transcript: String,
    audio_duration_ms: f64,
}

/// Build Meta's batch/file request. Only WAV is accepted; callers normalize
/// imported formats before reaching this contract.
pub fn build_transcribe_request(
    params: &TranscribeParams,
) -> Result<HttpRequest, TranscriptionError> {
    let mime = params
        .audio_mime
        .clone()
        .unwrap_or_else(|| resolve_mime(&params.audio_path));
    if !mime.eq_ignore_ascii_case("audio/wav") {
        return Err(TranscriptionError::BadRequest {
            status: 400,
            message: "Meta Muse requires WAV audio".to_string(),
        });
    }

    let model = if params.model.trim().is_empty() {
        DEFAULT_MODEL
    } else {
        params.model.trim()
    };
    let keywords = keyword_boost_terms(&params.vocabulary, Some(MAX_KEYWORDS));
    let language_bias = params
        .language
        .as_deref()
        .and_then(resolve_language_bias)
        .map(|language| vec![language]);
    let request = MetaRequest {
        model,
        audio_encoding: "WAV",
        mode: "PUSH_TO_TALK",
        keywords: (!keywords.is_empty()).then_some(keywords),
        language_bias,
    };
    let request_bytes =
        serde_json::to_vec(&request).map_err(|error| TranscriptionError::Parse {
            message: format!("failed to serialize Meta request: {error}"),
        })?;

    let parts = vec![
        multipart_inline_file("request", "request.json", "application/json", request_bytes)?,
        multipart_file("audio", params.audio_path.clone(), "audio/wav", "audio.wav"),
    ];

    Ok(HttpRequest {
        method: HttpMethod::Post,
        url: params
            .base_url
            .as_deref()
            .map(str::trim)
            .filter(|value| !value.is_empty())
            .unwrap_or(ENDPOINT)
            .to_string(),
        headers: vec![
            Header::new("Authorization", format!("Bearer {}", params.api_key)),
            Header::new("Accept", "application/json"),
        ],
        body: Body::Multipart {
            boundary: MULTIPART_BOUNDARY.to_string(),
            parts,
        },
    })
}

/// Parse Meta's response. A successful response must carry both a transcript
/// string and a finite non-negative duration. Empty text is the shared no-speech
/// error; non-empty text also requires a positive duration, matching the cloud
/// adapter's malformed-duration rule.
pub fn parse_transcribe_response(resp: &HttpResponse) -> Result<Transcript, TranscriptionError> {
    if !(200..=299).contains(&resp.status) {
        return Err(common::classify_http(resp, &resp.text()));
    }
    let parsed: MetaResponse =
        serde_json::from_slice(&resp.body).map_err(|_| TranscriptionError::Parse {
            message: "malformed Meta 200 response body".to_string(),
        })?;
    if !parsed.audio_duration_ms.is_finite() || parsed.audio_duration_ms < 0.0 {
        return Err(TranscriptionError::Parse {
            message: "malformed Meta audioDurationMs".to_string(),
        });
    }
    let text = parsed.transcript.trim().to_string();
    if text.is_empty() {
        return Err(TranscriptionError::NoSpeech);
    }
    if parsed.audio_duration_ms == 0.0 {
        return Err(TranscriptionError::Parse {
            message: "malformed Meta audio duration".to_string(),
        });
    }
    Ok(Transcript {
        text,
        ..Default::default()
    })
}

/// Map the desktop picker code space to Meta's documented language names.
/// Unknown, empty, and `auto` values intentionally return `None` for upstream
/// auto-detection. Aliases match the HyperWhisper Cloud adapter exactly.
pub fn resolve_language_bias(language: &str) -> Option<&'static str> {
    let base = language
        .trim()
        .split(['-', '_'])
        .next()
        .unwrap_or("")
        .to_ascii_lowercase();
    match base.as_str() {
        "ar" => Some("Arabic"),
        "bn" => Some("Bengali"),
        "nl" => Some("Dutch"),
        "en" => Some("English"),
        "fr" => Some("French"),
        "de" => Some("German"),
        "he" | "iw" => Some("Hebrew"),
        "hi" => Some("Hindi"),
        "id" | "in" => Some("Indonesian"),
        "it" => Some("Italian"),
        "ja" => Some("Japanese"),
        "kn" => Some("Kannada"),
        "ko" => Some("Korean"),
        "ms" => Some("Malay"),
        "zh" | "cmn" => Some("Mandarin Chinese"),
        "mr" => Some("Marathi"),
        "pl" => Some("Polish"),
        "pt" => Some("Portuguese"),
        "es" => Some("Spanish"),
        "fil" | "tl" => Some("Tagalog"),
        "ta" => Some("Tamil"),
        "te" => Some("Telugu"),
        "th" => Some("Thai"),
        "tr" => Some("Turkish"),
        "vi" => Some("Vietnamese"),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::contract::{Part, TranscriptionError};

    fn params() -> TranscribeParams {
        TranscribeParams {
            api_key: "test-key-not-a-secret".to_string(),
            model: DEFAULT_MODEL.to_string(),
            audio_path: "/tmp/input.wav".to_string(),
            ..Default::default()
        }
    }

    fn header<'a>(request: &'a HttpRequest, name: &str) -> Option<&'a str> {
        request
            .headers
            .iter()
            .find(|header| header.name.eq_ignore_ascii_case(name))
            .map(|header| header.value.as_str())
    }

    fn request_json(request: &HttpRequest) -> serde_json::Value {
        let Body::Multipart { parts, .. } = &request.body else {
            panic!("expected multipart");
        };
        let Part::InlineFile { data, .. } = &parts[0] else {
            panic!("expected typed request file");
        };
        serde_json::from_slice(data).unwrap()
    }

    #[test]
    fn exact_endpoint_auth_and_multipart_shape() {
        let request = build_transcribe_request(&params()).unwrap();
        assert_eq!(request.method, HttpMethod::Post);
        assert_eq!(request.url, ENDPOINT);
        assert_eq!(
            header(&request, "Authorization"),
            Some("Bearer test-key-not-a-secret")
        );
        assert_eq!(header(&request, "Accept"), Some("application/json"));
        let Body::Multipart { boundary, parts } = request.body else {
            panic!("expected multipart");
        };
        assert_eq!(boundary, MULTIPART_BOUNDARY);
        assert!(
            matches!(&parts[0], Part::InlineFile { field, filename, mime, data }
            if field == "request" && filename == "request.json"
                && mime == "application/json" && data.len() <= crate::contract::MAX_INLINE_MULTIPART_FILE_BYTES)
        );
        assert!(
            matches!(&parts[1], Part::FileRef { field, path, mime, filename }
            if field == "audio" && path == "/tmp/input.wav"
                && mime == "audio/wav" && filename == "audio.wav")
        );
    }

    #[test]
    fn payload_defaults_and_omits_empty_options() {
        let payload = request_json(&build_transcribe_request(&params()).unwrap());
        assert_eq!(payload["model"], DEFAULT_MODEL);
        assert_eq!(payload["audioEncoding"], "WAV");
        assert_eq!(payload["mode"], "PUSH_TO_TALK");
        assert!(payload.get("keywords").is_none());
        assert!(payload.get("languageBias").is_none());
    }

    #[test]
    fn vocabulary_is_sanitized_deduplicated_capped_and_term_bounded() {
        let mut input = params();
        let long = "x".repeat(100);
        input.vocabulary = vec!["  Meta  ".into(), "meta".into(), "<>".into(), long]
            .into_iter()
            .chain((0..110).map(|index| format!("term{index}")))
            .collect();
        let payload = request_json(&build_transcribe_request(&input).unwrap());
        let terms = payload["keywords"].as_array().unwrap();
        assert_eq!(terms.len(), MAX_KEYWORDS);
        assert_eq!(terms[0], "Meta");
        assert_eq!(terms[1].as_str().unwrap().chars().count(), 80);
        assert!(terms
            .iter()
            .all(|term| term.as_str().unwrap().chars().count() <= 80));
    }

    #[test]
    fn language_aliases_match_cloud_and_unknown_or_auto_is_omitted() {
        for (code, expected) in [
            ("iw", "Hebrew"),
            ("in-ID", "Indonesian"),
            ("cmn-Hans-CN", "Mandarin Chinese"),
            ("tl", "Tagalog"),
        ] {
            let mut input = params();
            input.language = Some(code.to_string());
            let payload = request_json(&build_transcribe_request(&input).unwrap());
            assert_eq!(payload["languageBias"], serde_json::json!([expected]));
        }
        for code in ["auto", "", "xx"] {
            let mut input = params();
            input.language = Some(code.to_string());
            let payload = request_json(&build_transcribe_request(&input).unwrap());
            assert!(payload.get("languageBias").is_none());
        }
    }

    #[test]
    fn rejects_non_wav_input() {
        let mut input = params();
        input.audio_path = "/tmp/input.mp3".to_string();
        assert!(matches!(
            build_transcribe_request(&input),
            Err(TranscriptionError::BadRequest { status: 400, .. })
        ));
    }

    fn response(status: u16, body: &str) -> HttpResponse {
        HttpResponse {
            status,
            headers: vec![],
            body: body.as_bytes().to_vec(),
        }
    }

    #[test]
    fn parses_and_trims_valid_response() {
        let transcript = parse_transcribe_response(&response(
            200,
            r#"{"transcript":"  hello world  ","audioDurationMs":1234}"#,
        ))
        .unwrap();
        assert_eq!(transcript.text, "hello world");
    }

    #[test]
    fn empty_transcript_is_no_speech() {
        assert_eq!(
            parse_transcribe_response(
                &response(200, r#"{"transcript":"  ","audioDurationMs":0}"#,)
            ),
            Err(TranscriptionError::NoSpeech)
        );
    }

    #[test]
    fn malformed_success_fields_are_rejected() {
        for body in [
            r#"{"transcript":7,"audioDurationMs":1}"#,
            r#"{"transcript":"ok"}"#,
            r#"{"transcript":"ok","audioDurationMs":-1}"#,
            r#"{"transcript":"ok","audioDurationMs":0}"#,
            r#"{"transcript":"ok","audioDurationMs":"NaN"}"#,
            "not-json",
        ] {
            assert!(matches!(
                parse_transcribe_response(&response(200, body)),
                Err(TranscriptionError::Parse { .. })
            ));
        }
    }

    #[test]
    fn auth_and_status_mapping_match_cloud_rules() {
        assert_eq!(
            parse_transcribe_response(&response(401, "bad")),
            Err(TranscriptionError::Unauthorized)
        );
        assert_eq!(
            parse_transcribe_response(&response(403, "bad")),
            Err(TranscriptionError::Unauthorized)
        );
        assert_eq!(
            parse_transcribe_response(&response(503, "down")),
            Err(TranscriptionError::ProviderUnavailable { status: 503 })
        );
    }
}
