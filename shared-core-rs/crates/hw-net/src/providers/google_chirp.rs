//! Google Cloud Speech-to-Text V2 (Chirp 3) via HyperWhisper Cloud (routed, sans-I/O).
//!
//! Same routing strategy as [`crate::providers::azure_mai`] — pins
//! `X-STT-Provider: google-chirp` so the Fly backend dispatches to Google Speech
//! V2. **No BYOK** — auth is `license_key` / `device_id` like HyperWhisper Cloud.
//!
//! Parity references:
//! - macOS `GoogleChirpProvider.swift` + `HyperWhisperRoutedTranscription.swift`
//! - Windows `GoogleChirpTranscriptionService.cs` + `HyperWhisperRoutedTranscriptionClient.cs`
//!
//! Note: the backend forwards `initial_prompt` to Chirp 3 as a Speech V2 inline
//! phrase set (`config.adaptation`), so vocabulary IS honoured for this tier.
//! Whether a client sends any is a catalog-driven, platform-side decision (it
//! depends on the per-model catalog the platform owns), so it is not enforced
//! in this sans-I/O builder — the caller passes an empty `vocabulary` when the
//! catalog says unsupported.

use crate::contract::{HttpRequest, HttpResponse, TranscribeParams, Transcript, TranscriptionError};
use crate::providers::hyperwhisper_cloud;

/// `X-STT-Provider` header value the Fly backend uses to dispatch to Google
/// Speech V2. Distinct from any catalog provider key — do not conflate.
pub const STT_PROVIDER_HEADER: &str = "google-chirp";

/// Build the Google-Chirp routed transcription request.
///
/// Forces `routed_provider = "google-chirp"` (overriding any caller value).
/// `routed_model` / `routed_domain` are passed through when set.
pub fn build_transcribe_request(
    params: &TranscribeParams,
) -> Result<HttpRequest, TranscriptionError> {
    let mut p = params.clone();
    p.routed_provider = Some(STT_PROVIDER_HEADER.to_string());
    hyperwhisper_cloud::build_routed_request(&p)
}

/// Parse the Google-Chirp routed response (same contract as HyperWhisper Cloud).
pub fn parse_transcribe_response(resp: &HttpResponse) -> Result<Transcript, TranscriptionError> {
    hyperwhisper_cloud::parse_routed_response(resp)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::contract::Header;

    fn params() -> TranscribeParams {
        TranscribeParams {
            audio_path: "/tmp/rec.flac".to_string(),
            base_url: Some("https://transcribe-prod-v2.hyperwhisper.com".to_string()),
            license_key: Some("LIC-9".to_string()),
            // Sharing on: no opt-out header, matching a default install.
            share_anonymous_speed_data: true,
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
    fn sets_google_chirp_provider_header() {
        let req = build_transcribe_request(&params()).unwrap();
        assert!(req
            .headers
            .contains(&Header::new("X-STT-Provider", "google-chirp")));
        assert_eq!(
            req.url,
            "https://transcribe-prod-v2.hyperwhisper.com/transcribe?license_key=LIC-9"
        );
        // flac → audio/flac
        assert!(req.headers.contains(&Header::new("Content-Type", "audio/flac")));
    }

    #[test]
    fn caller_routed_provider_is_overridden() {
        let mut p = params();
        p.routed_provider = Some("azure-mai".to_string());
        let req = build_transcribe_request(&p).unwrap();
        assert!(req
            .headers
            .contains(&Header::new("X-STT-Provider", "google-chirp")));
        assert!(!req.headers.contains(&Header::new("X-STT-Provider", "azure-mai")));
    }

    /// Google Chirp is one of the two providers the latency opt-out exists to
    /// protect, so assert it through THIS module's entry point rather than
    /// relying on `hyperwhisper_cloud::routed_requests_carry_the_opt_out_too`,
    /// which calls the shared builder directly. If this module ever grew its
    /// own builder instead of delegating, that test would stay green and the
    /// opt-out would silently vanish here.
    #[test]
    fn the_latency_opt_out_survives_this_modules_entry_point() {
        let mut opted_out = params();
        opted_out.share_anonymous_speed_data = false;
        let req = build_transcribe_request(&opted_out).unwrap();
        assert!(
            req.headers.contains(&Header::new(
                hyperwhisper_cloud::LATENCY_OPT_OUT_HEADER,
                "1"
            )),
            "an opted-out user must send X-Latency-Opt-Out: 1, got {:?}",
            req.headers
        );

        // Sharing is expressed by the header's ABSENCE — there is no "yes" header.
        let sharing = build_transcribe_request(&params()).unwrap();
        assert!(
            !sharing
                .headers
                .iter()
                .any(|h| h.name == hyperwhisper_cloud::LATENCY_OPT_OUT_HEADER),
            "a sharing user must send no opt-out header, got {:?}",
            sharing.headers
        );
    }

    #[test]
    fn parses_success_response() {
        let body = r#"{"text":"bonjour","language":"fr","cost":{"usd":0.0008,"credits":4.0}}"#;
        let t = parse_transcribe_response(&resp(200, body)).unwrap();
        assert_eq!(t.text, "bonjour");
        assert_eq!(t.cost, Some(4.0));
    }

    #[test]
    fn no_speech_maps_to_nospeech() {
        let err = parse_transcribe_response(&resp(200, r#"{"no_speech_detected":true}"#)).unwrap_err();
        assert_eq!(err, TranscriptionError::NoSpeech);
    }

    #[test]
    fn http_5xx_maps_to_provider_unavailable() {
        let err = parse_transcribe_response(&resp(502, "bad gateway")).unwrap_err();
        assert_eq!(err, TranscriptionError::ProviderUnavailable { status: 502 });
    }
}
