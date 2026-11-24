//! Issue #3049: ElevenLabs Scribe v3 (`scribe_v3`, 2026-11-09) takes the user's
//! vocabulary as repeated `keyterms` fields, on the same endpoint and request
//! shape as Scribe v2. Before the fix the keyterm gate matched only the v2 ids,
//! so a v3 request silently dropped every term.
//!
//! The v2 / v2 Medical / v1 cases are regressions: the gate must still send
//! keyterms for both v2 ids and still send none for the retired v1.
use hw_net::providers::elevenlabs;
use hw_net::{Body, Part, TranscribeParams};

fn request(model: &str, vocabulary: &[&str]) -> Vec<Part> {
    let p = TranscribeParams {
        api_key: "el-test".into(),
        model: model.into(),
        vocabulary: vocabulary.iter().map(|s| s.to_string()).collect(),
        audio_path: "/tmp/a.wav".into(),
        ..Default::default()
    };
    let req = elevenlabs::build_transcribe_request(&p).unwrap();
    assert_eq!(req.url, elevenlabs::ENDPOINT);
    let Body::Multipart { parts, .. } = req.body else {
        panic!("ElevenLabs request is not multipart")
    };
    parts
}

fn fields(parts: &[Part], field: &str) -> Vec<String> {
    parts
        .iter()
        .filter_map(|p| match p {
            Part::Field { name, value } if name == field => Some(value.clone()),
            _ => None,
        })
        .collect()
}

#[test]
fn scribe_v3_with_vocabulary_carries_keyterms() {
    let parts = request(elevenlabs::SCRIBE_V3, &["HyperWhisper", "Kubernetes"]);
    assert_eq!(fields(&parts, "model_id"), vec!["scribe_v3"]);
    assert_eq!(
        fields(&parts, "keyterms"),
        vec!["HyperWhisper", "Kubernetes"]
    );
    // The file stays the last part, after every keyterm.
    assert!(matches!(parts.last(), Some(Part::FileRef { field, .. }) if field == "file"));
}

#[test]
fn scribe_v2_still_carries_keyterms() {
    let parts = request(elevenlabs::SCRIBE_V2, &["HyperWhisper"]);
    assert_eq!(fields(&parts, "model_id"), vec!["scribe_v2"]);
    assert_eq!(fields(&parts, "keyterms"), vec!["HyperWhisper"]);
}

#[test]
fn scribe_v2_medical_still_carries_keyterms() {
    let parts = request(elevenlabs::SCRIBE_V2_MEDICAL, &["metoprolol"]);
    assert_eq!(fields(&parts, "model_id"), vec!["scribe_v2_medical"]);
    assert_eq!(fields(&parts, "keyterms"), vec!["metoprolol"]);
}

#[test]
fn scribe_v1_still_sends_no_keyterms() {
    let parts = request("scribe_v1", &["HyperWhisper"]);
    assert!(fields(&parts, "keyterms").is_empty());
}

#[test]
fn scribe_v3_live_is_not_a_batch_keyterm_model() {
    // `scribe_v3_live` is the realtime model and is not catalogued (#3049);
    // the batch gate must not match it by prefix.
    let parts = request("scribe_v3_live", &["HyperWhisper"]);
    assert!(fields(&parts, "keyterms").is_empty());
}
