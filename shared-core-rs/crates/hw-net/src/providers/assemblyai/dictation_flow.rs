//! Dictation: completed PCM WAV recordings with built-in transcript cleanup.
//! Contract: https://www.assemblyai.com/docs/dictation (2026-09-17).
//! Shells must validate actual PCM16 WAV headers and duration before calling;
//! this sans-I/O builder cannot inspect a file. Never fall back to ordinary STT.
use super::auth_header;
use crate::contract::{
    Body, HttpMethod, HttpRequest, HttpResponse, Part, TranscribeParams, Transcript,
    TranscriptionError,
};
use crate::helpers::{keyword_boost_terms, multipart_file, resolve_mime};
use crate::providers::common::{classify_http_with_message, filename_of};

pub const DICTATION_BASE_URL: &str = "https://dictation.assemblyai.com";
pub const DICTATION_MAX_DURATION_SECS: f64 = 120.0;
pub const DICTATION_TIMEOUT_MS: u64 = 90_000;
pub const DICTATION_MAX_KEYTERMS: usize = 100;
pub const DICTATION_MAX_KEYTERM_CHARS: usize = 8000;
pub const DICTATION_MAX_STT_PROMPT_CHARS: usize = 6000;
pub const DICTATION_LANGUAGES: &[&str] = &[
    "en", "es", "de", "fr", "it", "pt", "tr", "nl", "sv", "no", "da", "fi", "hi", "vi", "he", "ur",
    "ko", "ca", "gl", "ru", "ro", "et", "fa", "yue", "af", "mr", "zu", "xh", "nn", "ar", "ja",
    "zh",
];

fn invalid(message: &str) -> TranscriptionError {
    TranscriptionError::BadRequest {
        status: 400,
        message: message.into(),
    }
}

/// WAV-only, ordered multipart with typed JSON configuration first. `prompt`
/// supplies transcription context, never rewrite instructions. Omission keeps
/// the vendor's default cleanup. Auto language is unsupported: omission would
/// silently select English, and the API documents no auto-detection option.
pub fn build_dictation_request(
    params: &TranscribeParams,
) -> Result<HttpRequest, TranscriptionError> {
    if params.model != "dictation" {
        return Err(invalid(
            "Select the AssemblyAI Dictation model without Medical Mode.",
        ));
    }
    let language = params
        .language
        .as_deref()
        .unwrap_or("")
        .trim()
        .to_lowercase();
    let code = language.split(['-', '_']).next().unwrap_or("");
    // App language selections may use BCP-47 region suffixes or nb for Bokmal.
    let code = if code == "nb" { "no" } else { code };
    if !DICTATION_LANGUAGES.contains(&code) {
        return Err(invalid("AssemblyAI Dictation requires an explicit supported language. Select a language instead of Auto."));
    }
    let mime = params
        .audio_mime
        .clone()
        .unwrap_or_else(|| resolve_mime(&params.audio_path));
    if !matches!(
        mime.split(';')
            .next()
            .unwrap_or("")
            .trim()
            .to_lowercase()
            .as_str(),
        "audio/wav" | "audio/wave" | "audio/x-wav" | "audio/vnd.wave"
    ) {
        return Err(invalid("AssemblyAI Dictation requires PCM16 WAV audio up to 120 seconds. Convert this recording to WAV."));
    }
    let mut config = serde_json::json!({"language_codes": [code]});
    let mut chars = 0;
    let terms: Vec<String> = keyword_boost_terms(&params.vocabulary, None)
        .into_iter()
        .take(DICTATION_MAX_KEYTERMS)
        .take_while(|term| {
            chars += term.chars().count();
            chars <= DICTATION_MAX_KEYTERM_CHARS
        })
        .collect();
    if !terms.is_empty() {
        config["keyterms_prompt"] = serde_json::json!(terms);
    }
    if let Some(prompt) = params
        .prompt
        .as_deref()
        .map(str::trim)
        .filter(|p| !p.is_empty())
    {
        if prompt.chars().count() > DICTATION_MAX_STT_PROMPT_CHARS {
            return Err(invalid(
                "AssemblyAI Dictation transcription context must be at most 6000 characters.",
            ));
        }
        config["stt_prompt"] = serde_json::json!(prompt);
    }
    let data = serde_json::to_vec(&config)
        .map_err(|_| invalid("Cannot encode Dictation configuration."))?;
    if data.len() > crate::contract::MAX_INLINE_MULTIPART_FILE_BYTES {
        return Err(invalid(
            "AssemblyAI Dictation configuration is too large. Shorten the vocabulary or context.",
        ));
    }
    Ok(HttpRequest {
        method: HttpMethod::Post,
        url: format!(
            "{}/v1/transcribe/live",
            params
                .base_url
                .as_deref()
                .unwrap_or(DICTATION_BASE_URL)
                .trim_end_matches('/')
        ),
        headers: vec![auth_header(&params.api_key)],
        body: Body::Multipart {
            boundary: crate::helpers::MULTIPART_BOUNDARY.into(),
            parts: vec![
                Part::InlineFile {
                    field: "config".into(),
                    filename: "config.json".into(),
                    mime: "application/json".into(),
                    data,
                },
                multipart_file(
                    "audio",
                    params.audio_path.clone(),
                    "audio/wav",
                    filename_of(&params.audio_path),
                ),
            ],
        },
    })
}

/// Prefer usable cleaned text; a failed/empty rewrite keeps the raw transcript.
/// Do not embed the response body in parse failures (it can contain dictation).
pub fn parse_dictation_response(resp: &HttpResponse) -> Result<Transcript, TranscriptionError> {
    let raw = resp.text();
    if !(200..=299).contains(&resp.status) {
        return Err(classify_http_with_message(resp, &raw, |json, _| {
            json.and_then(|j| {
                ["detail", "error", "message", "title"]
                    .iter()
                    .find_map(|key| {
                        j.get(key)
                            .and_then(|v| v.as_str())
                            .filter(|s| !s.trim().is_empty())
                    })
            })
            .map(|s| s.chars().take(500).collect())
            .unwrap_or_else(|| "AssemblyAI Dictation request failed.".into())
        }));
    }
    let json: serde_json::Value =
        serde_json::from_str(&raw).map_err(|_| TranscriptionError::Parse {
            message: "Invalid Dictation JSON response.".into(),
        })?;
    let cleaned = json
        .get("llm_response")
        .and_then(|v| v.as_str())
        .filter(|s| !s.trim().is_empty());
    let text = cleaned
        .or_else(|| json.get("text").and_then(|v| v.as_str()))
        .ok_or_else(|| TranscriptionError::Parse {
            message: "Dictation response has no transcript string.".into(),
        })?;
    if text.trim().is_empty() {
        return Err(TranscriptionError::NoSpeech);
    }
    Ok(Transcript {
        text: text.into(),
        ..Default::default()
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    fn params() -> TranscribeParams {
        TranscribeParams {
            model: "dictation".into(),
            language: Some("ja-JP".into()),
            audio_path: "/tmp/clip.wav".into(),
            api_key: "test-key".into(),
            ..Default::default()
        }
    }
    fn config(p: &TranscribeParams) -> serde_json::Value {
        let Body::Multipart { parts, .. } = build_dictation_request(p).unwrap().body else {
            panic!()
        };
        let Part::InlineFile { data, .. } = &parts[0] else {
            panic!()
        };
        serde_json::from_slice(data).unwrap()
    }
    #[test]
    fn ordered_typed_multipart_and_auth() {
        let mut p = params();
        p.audio_mime = Some("audio/vnd.wave".into());
        let req = build_dictation_request(&p).unwrap();
        assert_eq!(
            req.url,
            "https://dictation.assemblyai.com/v1/transcribe/live"
        );
        assert_eq!(req.headers, vec![auth_header("test-key")]);
        let Body::Multipart { parts, .. } = req.body else {
            panic!()
        };
        assert!(
            matches!(&parts[0], Part::InlineFile { field, mime, .. } if field == "config" && mime == "application/json")
        );
        assert!(
            matches!(&parts[1], Part::FileRef { field, mime, .. } if field == "audio" && mime == "audio/wav")
        );
        p.base_url = Some("https://test.invalid/".into());
        assert_eq!(
            build_dictation_request(&p).unwrap().url,
            "https://test.invalid/v1/transcribe/live"
        );
    }
    #[test]
    fn language_and_model_validation() {
        let mut p = params();
        assert_eq!(config(&p)["language_codes"], serde_json::json!(["ja"]));
        for code in DICTATION_LANGUAGES {
            p.language = Some((*code).into());
            assert!(build_dictation_request(&p).is_ok());
        }
        for lang in [None, Some("auto"), Some(""), Some("xx")] {
            p.language = lang.map(String::from);
            assert!(build_dictation_request(&p).is_err());
        }
        p.language = Some("nb-NO".into());
        assert_eq!(config(&p)["language_codes"], serde_json::json!(["no"]));
        p.model = "dictation-medical".into();
        assert!(build_dictation_request(&p).is_err());
        p.model = "dictation".into();
        p.audio_mime = Some("audio/mpeg".into());
        assert!(build_dictation_request(&p).is_err());
    }
    #[test]
    fn vocabulary_and_context_limits() {
        let mut p = params();
        p.vocabulary = (0..101).map(|i| format!("term{i}")).collect();
        assert_eq!(config(&p)["keyterms_prompt"].as_array().unwrap().len(), 100);
        p.vocabulary = (0..101)
            .map(|i| format!("{i:03}{}", "あ".repeat(77)))
            .collect();
        assert_eq!(config(&p)["keyterms_prompt"].as_array().unwrap().len(), 100);
        p.prompt = Some("a".repeat(6000));
        assert!(config(&p).get("stt_prompt").is_some());
        assert!(config(&p).get("llm_instruction").is_none());
        p.prompt = Some("a".repeat(6001));
        assert!(build_dictation_request(&p).is_err());
    }
    fn response(status: u16, value: serde_json::Value) -> HttpResponse {
        HttpResponse {
            status,
            headers: vec![],
            body: serde_json::to_vec(&value).unwrap(),
        }
    }
    #[test]
    fn cleaned_raw_and_no_speech() {
        for cleaned in [
            serde_json::Value::Null,
            serde_json::json!(""),
            serde_json::json!("  "),
            serde_json::json!(123),
        ] {
            assert_eq!(
                parse_dictation_response(&response(
                    200,
                    serde_json::json!({"text":"raw", "llm_response":cleaned, "llm_error":"timeout"})
                ))
                .unwrap()
                .text,
                "raw"
            );
        }
        assert_eq!(
            parse_dictation_response(&response(
                200,
                serde_json::json!({"text":"raw", "llm_response":"clean"})
            ))
            .unwrap()
            .text,
            "clean"
        );
        assert_eq!(
            parse_dictation_response(&response(
                200,
                serde_json::json!({"text":" ","llm_response":null})
            )),
            Err(TranscriptionError::NoSpeech)
        );
        assert!(matches!(
            parse_dictation_response(&response(200, serde_json::json!({}))),
            Err(TranscriptionError::Parse { .. })
        ));
    }
    #[test]
    fn provider_errors() {
        let limited = HttpResponse {
            status: 429,
            headers: vec![crate::contract::Header::new("Retry-After", "7")],
            body: vec![],
        };
        assert_eq!(
            parse_dictation_response(&limited),
            Err(TranscriptionError::RateLimited {
                retry_after_secs: Some(7)
            })
        );
        let invalid_json = HttpResponse {
            status: 200,
            headers: vec![],
            body: b"private transcript".to_vec(),
        };
        assert_eq!(
            parse_dictation_response(&invalid_json),
            Err(TranscriptionError::Parse {
                message: "Invalid Dictation JSON response.".into()
            })
        );
        assert_eq!(
            parse_dictation_response(&response(401, serde_json::json!({}))),
            Err(TranscriptionError::Unauthorized)
        );
        for body in [
            serde_json::json!({"detail":"bad config"}),
            serde_json::json!({"error":"bad config"}),
        ] {
            assert_eq!(
                parse_dictation_response(&response(400, body)),
                Err(TranscriptionError::BadRequest {
                    status: 400,
                    message: "bad config".into()
                })
            );
        }
        assert_eq!(
            parse_dictation_response(&response(503, serde_json::json!({}))),
            Err(TranscriptionError::ProviderUnavailable { status: 503 })
        );
    }
}
