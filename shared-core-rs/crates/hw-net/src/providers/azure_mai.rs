//! Microsoft MAI-Transcribe via HyperWhisper Cloud (routed, sans-I/O).
//!
//! Routes through the same Fly `/transcribe` endpoint as
//! [`crate::providers::hyperwhisper_cloud`] but pins `X-STT-Provider: azure-mai`
//! so the backend dispatches to Azure Speech. **No BYOK** — auth is always
//! `license_key` / `device_id`, identical to HyperWhisper Cloud.
//!
//! Parity references:
//! - macOS `AzureMAIProvider.swift` + `HyperWhisperRoutedTranscription.swift`
//! - Windows `AzureMAITranscriptionService.cs` + `HyperWhisperRoutedTranscriptionClient.cs`

use crate::contract::{HttpRequest, HttpResponse, TranscribeParams, Transcript, TranscriptionError};
use crate::providers::hyperwhisper_cloud;

/// `X-STT-Provider` header value the Fly backend uses to dispatch to Azure
/// Speech. Distinct from any catalog provider key — do not conflate.
pub const STT_PROVIDER_HEADER: &str = "azure-mai";

/// Build the Azure-MAI routed transcription request.
///
/// Forces `routed_provider = "azure-mai"` (overriding any caller value) so the
/// backend always dispatches to Azure for this provider. `routed_model` /
/// `routed_domain` are passed through when the caller set them.
pub fn build_transcribe_request(
    params: &TranscribeParams,
) -> Result<HttpRequest, TranscriptionError> {
    let mut p = params.clone();
    p.routed_provider = Some(STT_PROVIDER_HEADER.to_string());
    hyperwhisper_cloud::build_routed_request(&p)
}

/// Parse the Azure-MAI routed response (same contract as HyperWhisper Cloud).
pub fn parse_transcribe_response(resp: &HttpResponse) -> Result<Transcript, TranscriptionError> {
    hyperwhisper_cloud::parse_routed_response(resp)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::contract::{Body, Header, Part};
    use crate::helpers::MULTIPART_BOUNDARY;

    fn params() -> TranscribeParams {
        TranscribeParams {
            audio_path: "/tmp/rec.m4a".to_string(),
            base_url: Some("https://transcribe-prod-v2.hyperwhisper.com".to_string()),
            device_id: Some("dev1".to_string()),
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
    fn sets_azure_mai_provider_header() {
        let req = build_transcribe_request(&params()).unwrap();
        assert!(req
            .headers
            .contains(&Header::new("X-STT-Provider", "azure-mai")));
        assert_eq!(
            req.url,
            "https://transcribe-prod-v2.hyperwhisper.com/transcribe?device_id=dev1"
        );
        // m4a → audio/mp4
        assert!(req.headers.contains(&Header::new("Content-Type", "audio/mp4")));
        match &req.body {
            Body::Multipart { boundary, parts } => {
                assert_eq!(boundary, MULTIPART_BOUNDARY);
                assert_eq!(parts.len(), 1);
                assert!(matches!(&parts[0], Part::FileRef { field, .. }
                    if field == hyperwhisper_cloud::RAW_BODY_FIELD));
            }
            other => panic!("expected multipart, got {other:?}"),
        }
    }

    #[test]
    fn caller_routed_provider_is_overridden() {
        let mut p = params();
        p.routed_provider = Some("something-else".to_string());
        let req = build_transcribe_request(&p).unwrap();
        assert!(req
            .headers
            .contains(&Header::new("X-STT-Provider", "azure-mai")));
        assert!(!req
            .headers
            .iter()
            .any(|h| h.value == "something-else"));
    }

    #[test]
    fn passes_through_routed_model_and_domain() {
        let mut p = params();
        p.routed_model = Some("mai-1.5".to_string());
        p.routed_domain = Some("medical".to_string());
        let req = build_transcribe_request(&p).unwrap();
        assert!(req.headers.contains(&Header::new("X-STT-Model", "mai-1.5")));
        assert!(req.headers.contains(&Header::new("X-STT-Domain", "medical")));
    }

    #[test]
    fn parses_success_response() {
        let body = r#"{"text":"hallo welt","language":"de","cost":{"usd":0.0009,"credits":5.1}}"#;
        let t = parse_transcribe_response(&resp(200, body)).unwrap();
        assert_eq!(t.text, "hallo welt");
        assert_eq!(t.cost, Some(5.1));
    }

    #[test]
    fn no_speech_maps_to_nospeech() {
        let err = parse_transcribe_response(&resp(200, r#"{"no_speech_detected":true}"#)).unwrap_err();
        assert_eq!(err, TranscriptionError::NoSpeech);
    }

    #[test]
    fn http_413_maps_to_file_too_large() {
        let err = parse_transcribe_response(&resp(413, r#"{"error":"too big"}"#)).unwrap_err();
        assert_eq!(err, TranscriptionError::FileTooLarge);
    }
}
