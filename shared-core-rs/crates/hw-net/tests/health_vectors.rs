//! Conformance-vector tests for a provider health probe's verdict.
//!
//! `shared-conformance/health-vectors.json` pins, for every provider and every
//! probe status that matters, whether the key is healthy and WHY not — the
//! `reason` that decides between "Key invalid" and "Provider unreachable" in
//! every head's UI. The other heads replay the same file through their own
//! mapping of the core's verdict:
//!
//! - `app/windows/HyperWhisper.SmokeTests` → `CloudProviderHealthService.MapHealthVerdict`
//! - `app/shared-dotnet/HyperWhisper.SharedCore.Tests` → the C# binding (the
//!   Linux head's `ProviderMetadataHealthProbe` reads the same field)
//! - `app/macos/hyperwhisperTests/HealthConformanceVectorTests.swift` →
//!   `CloudProviderHealthManager.mapRustHealth`
//!
//! Issue #779: the core used to return only `healthy` + the raw status and left
//! "which vendors answer a bad key with HTTP 400" to each host. Windows' copy
//! left out Gemini 3.5 Transcribe, so the same bad key read "Provider
//! unreachable" on Windows and "Key invalid" on macOS. The rule now lives in
//! `hw_net::health::parse_health_response` alone.
//!
//! [`rule_matches_the_vectors`] asserts the file against a rule written out by
//! hand here, not against the core, so regenerating the file cannot quietly
//! agree with a regression.
//!
//! Regenerate after an intended policy change:
//!
//! ```sh
//! cd shared-core-rs
//! cargo test -p hw-net --test health_vectors -- --ignored regenerate
//! ```

use std::path::PathBuf;

use serde::{Deserialize, Serialize};

use hw_net::health::parse_health_response;
use hw_net::{HealthReason, HttpResponse, Provider};

const VECTORS_PATH: &str = "../../../shared-conformance/health-vectors.json";

/// Every probe status a row is written for. 422 is here for ElevenLabs' STT
/// probe, whose empty body a valid key gets rejected with 400 or 422.
const STATUSES: [u16; 7] = [200, 400, 401, 403, 422, 429, 500];

/// In the core's declaration order. The vector `provider` is the variant name,
/// which is also the C# binding's `HwProvider` member name.
const ALL_PROVIDERS: [Provider; 15] = [
    Provider::HyperWhisperCloud,
    Provider::Openai,
    Provider::Groq,
    Provider::Elevenlabs,
    Provider::Mistral,
    Provider::Grok,
    Provider::Deepgram,
    Provider::Soniox,
    Provider::Assemblyai,
    Provider::Gemini,
    Provider::AzureMai,
    Provider::GoogleChirp,
    Provider::GeminiTranscribe,
    Provider::GeminiTranscribeLive,
    Provider::Meta,
];

fn vectors_path() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join(VECTORS_PATH)
}

#[derive(Serialize, Deserialize, PartialEq, Debug)]
#[serde(rename_all = "camelCase")]
struct Document {
    description: String,
    cases: Vec<HealthVector>,
}

#[derive(Serialize, Deserialize, PartialEq, Debug, Clone)]
#[serde(rename_all = "camelCase")]
struct HealthVector {
    provider: String,
    status: u16,
    healthy: bool,
    /// `healthy`, `unauthorized` or `unreachable`.
    reason: String,
}

fn load() -> Document {
    let json = std::fs::read_to_string(vectors_path()).expect("health-vectors.json must exist");
    serde_json::from_str(&json).expect("health-vectors.json must parse")
}

fn name(p: Provider) -> String {
    format!("{p:?}")
}

fn reason_name(r: HealthReason) -> &'static str {
    match r {
        HealthReason::Healthy => "healthy",
        HealthReason::Unauthorized => "unauthorized",
        HealthReason::Unreachable => "unreachable",
    }
}

fn provider_named(n: &str) -> Provider {
    ALL_PROVIDERS
        .into_iter()
        .find(|p| name(*p) == n)
        .unwrap_or_else(|| panic!("health-vectors.json names an unknown provider `{n}`"))
}

fn core_verdict(provider: Provider, status: u16) -> HealthVector {
    let h = parse_health_response(
        provider,
        &HttpResponse {
            status,
            headers: vec![],
            body: vec![],
        },
    );
    HealthVector {
        provider: name(provider),
        status,
        healthy: h.healthy,
        reason: reason_name(h.reason).to_string(),
    }
}

/// The non-tautological half: the policy written out by hand.
fn expected_reason(provider: &str, status: u16) -> &'static str {
    let rejects_bad_key_with_400 = matches!(
        provider,
        "Gemini" | "GeminiTranscribe" | "GeminiTranscribeLive" | "Grok"
    );
    match status {
        200..=299 => "healthy",
        400 | 422 if provider == "Elevenlabs" => "healthy",
        401 | 403 => "unauthorized",
        400 if rejects_bad_key_with_400 => "unauthorized",
        _ => "unreachable",
    }
}

#[test]
fn health_vectors_match_the_core() {
    for want in load().cases {
        let got = core_verdict(provider_named(&want.provider), want.status);
        assert_eq!(got, want, "core verdict drifted from health-vectors.json");
    }
}

#[test]
fn health_vectors_match_the_written_rule() {
    for row in load().cases {
        let want = expected_reason(&row.provider, row.status);
        assert_eq!(row.reason, want, "{} {}", row.provider, row.status);
        assert_eq!(
            row.healthy,
            want == "healthy",
            "{} {}",
            row.provider,
            row.status
        );
    }
}

/// Every provider x every status, in order, so a head that replays the file
/// cannot pass by skipping the row it would fail.
#[test]
fn health_vectors_cover_every_provider_and_status() {
    let doc = load();
    assert!(!doc.description.is_empty());
    let got: Vec<(String, u16)> = doc
        .cases
        .iter()
        .map(|c| (c.provider.clone(), c.status))
        .collect();
    let want: Vec<(String, u16)> = ALL_PROVIDERS
        .into_iter()
        .flat_map(|p| STATUSES.into_iter().map(move |s| (name(p), s)))
        .collect();
    assert_eq!(
        got, want,
        "health-vectors.json must list every provider x status"
    );
}

/// The row issue #779 was filed for.
#[test]
fn health_vectors_pin_a_bad_gemini_transcribe_key_as_unauthorized() {
    let row = load()
        .cases
        .into_iter()
        .find(|c| c.provider == "GeminiTranscribe" && c.status == 400)
        .expect("GeminiTranscribe 400 row");
    assert_eq!(row.reason, "unauthorized");
    assert!(!row.healthy);
}

#[test]
#[ignore = "regenerates shared-conformance/health-vectors.json"]
fn regenerate() {
    let description = std::fs::read_to_string(vectors_path())
        .ok()
        .and_then(|raw| serde_json::from_str::<Document>(&raw).ok())
        .map(|d| d.description)
        .expect("keep the existing description; write one by hand for a new file");
    let cases = ALL_PROVIDERS
        .into_iter()
        .flat_map(|p| STATUSES.into_iter().map(move |s| core_verdict(p, s)))
        .collect();
    let doc = Document { description, cases };
    let mut json = serde_json::to_string_pretty(&doc).expect("serialize");
    json.push('\n');
    std::fs::write(vectors_path(), json).expect("write health-vectors.json");
}
