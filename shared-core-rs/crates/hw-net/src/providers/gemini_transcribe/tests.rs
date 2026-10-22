//! Tests for the Gemini 3.5 Transcribe pre-recorded request builder and
//! the TRAP 2 exclusion rule.

use super::*;
use crate::contract::{Body, HttpMethod, HttpRequest, TranscribeParams, TranscriptionError};

fn params() -> TranscribeParams {
    TranscribeParams {
        api_key: "test-key".to_string(),
        model: default_model().to_string(),
        audio_path: "/tmp/speech.mp3".to_string(),
        ..Default::default()
    }
}

fn body_json(req: &HttpRequest) -> serde_json::Value {
    match &req.body {
        Body::JsonWithBase64File { prefix, suffix, .. } => {
            let mut s = String::from_utf8(prefix.clone()).unwrap();
            s.push_str("QUJD"); // stand-in for the platform's base64
            s.push_str(&String::from_utf8(suffix.clone()).unwrap());
            serde_json::from_str(&s).expect("prefix + base64 + suffix must be valid JSON")
        }
        other => panic!("expected JsonWithBase64File, got {other:?}"),
    }
}

fn header<'a>(req: &'a HttpRequest, name: &str) -> Option<&'a str> {
    req.headers
        .iter()
        .find(|h| h.name.eq_ignore_ascii_case(name))
        .map(|h| h.value.as_str())
}

// ---- request building ----

#[test]
fn posts_to_interactions_not_generate_content() {
    // TRAP 1: `:generateContent` accepts this model, bills the audio and
    // returns an empty content part. Only /v1beta/interactions works.
    let req = build_transcribe_request(&params()).unwrap();
    assert_eq!(req.method, HttpMethod::Post);
    assert_eq!(
        req.url,
        "https://generativelanguage.googleapis.com/v1beta/interactions"
    );
    assert!(!req.url.contains("generateContent"));
}

#[test]
fn auth_is_header_not_query_param() {
    let req = build_transcribe_request(&params()).unwrap();
    assert_eq!(header(&req, "x-goog-api-key"), Some("test-key"));
    assert_eq!(header(&req, "Api-Revision"), Some(API_REVISION));
    assert_eq!(header(&req, "Content-Type"), Some("application/json"));
    assert!(!req.url.contains("key="));
}

#[test]
fn base_url_override_is_honored() {
    let p = TranscribeParams {
        base_url: Some("https://staging.test/".to_string()),
        ..params()
    };
    let req = build_transcribe_request(&p).unwrap();
    assert_eq!(req.url, "https://staging.test/v1beta/interactions");
}

#[test]
fn body_references_the_audio_path_and_never_the_bytes() {
    let req = build_transcribe_request(&params()).unwrap();
    match &req.body {
        Body::JsonWithBase64File { path, .. } => assert_eq!(path, "/tmp/speech.mp3"),
        other => panic!("expected JsonWithBase64File, got {other:?}"),
    }
}

#[test]
fn body_has_the_verified_input_shape() {
    let req = build_transcribe_request(&params()).unwrap();
    let json = body_json(&req);
    assert_eq!(json["model"], default_model());
    assert_eq!(json["input"][0]["type"], "audio");
    // `resolve_mime` says `audio/mpeg` for `.mp3`; verified accepted by the
    // live endpoint (as is `audio/wav`).
    assert_eq!(json["input"][0]["mime_type"], "audio/mpeg");
    assert_eq!(json["input"][0]["data"], "QUJD");
}

#[test]
fn mime_is_resolved_from_the_path_when_not_supplied() {
    let wav = TranscribeParams {
        audio_path: "/tmp/speech.wav".to_string(),
        ..params()
    };
    let json = body_json(&build_transcribe_request(&wav).unwrap());
    assert_eq!(json["input"][0]["mime_type"], "audio/wav");
}

#[test]
fn no_generation_config_when_nothing_is_configured() {
    let json = body_json(&build_transcribe_request(&params()).unwrap());
    assert!(json.get("generation_config").is_none());
}

#[test]
fn language_becomes_language_codes() {
    let p = TranscribeParams {
        language: Some("en-US".to_string()),
        ..params()
    };
    let json = body_json(&build_transcribe_request(&p).unwrap());
    assert_eq!(
        json["generation_config"]["transcription_config"]["language_codes"],
        serde_json::json!(["en-US"])
    );
}

#[test]
fn auto_language_is_omitted_for_auto_detect() {
    for lang in ["auto", "AUTO", "", "   "] {
        let p = TranscribeParams {
            language: Some(lang.to_string()),
            ..params()
        };
        let json = body_json(&build_transcribe_request(&p).unwrap());
        assert!(
            json.get("generation_config").is_none(),
            "language {lang:?} should mean auto-detect (field omitted)"
        );
    }
}

#[test]
fn vocabulary_becomes_custom_vocabulary() {
    let p = TranscribeParams {
        vocabulary: vec!["HyperWhisper".to_string(), "Kalamazoo".to_string()],
        ..params()
    };
    let json = body_json(&build_transcribe_request(&p).unwrap());
    assert_eq!(
        json["generation_config"]["transcription_config"]["custom_vocabulary"],
        serde_json::json!(["HyperWhisper", "Kalamazoo"])
    );
}

/// The BYOK path must send no more terms than the Cloud adapter and the
/// three native live strategies do — `customVocabulary.maxPhrases` in
/// `cloud-stt-catalog.json` is 100, and that entry's caveat names this very
/// file as "capped at 100 terms". Uncapped, the sentence was false and a
/// user with a large vocabulary sent BYOK a different request than Cloud.
#[test]
fn vocabulary_is_capped_at_the_catalog_limit_on_both_paths() {
    assert_eq!(
        MAX_VOCABULARY_TERMS, 100,
        "must match the catalog maxPhrases"
    );
    let many: Vec<String> = (0..MAX_VOCABULARY_TERMS + 25)
        .map(|i| format!("Term{i}"))
        .collect();

    // Pre-recorded.
    let p = TranscribeParams {
        vocabulary: many.clone(),
        ..params()
    };
    let json = body_json(&build_transcribe_request(&p).unwrap());
    let sent = json["generation_config"]["transcription_config"]["custom_vocabulary"]
        .as_array()
        .expect("custom_vocabulary must be an array")
        .clone();
    assert_eq!(sent.len(), MAX_VOCABULARY_TERMS);
    // The cap keeps the FIRST terms, in order — the user's list order is
    // their priority order.
    assert_eq!(sent[0], serde_json::json!("Term0"));
    assert_eq!(
        sent[MAX_VOCABULARY_TERMS - 1],
        serde_json::json!(format!("Term{}", MAX_VOCABULARY_TERMS - 1))
    );

    // Live setup frame — same enforcement point, so the same answer.
    let frame: serde_json::Value =
        serde_json::from_str(&build_live_setup_frame(LIVE_MODEL, Some("en-US"), &many)).unwrap();
    assert_eq!(
        frame["setup"]["input_audio_transcription"]["custom_vocabulary"]
            .as_array()
            .expect("custom_vocabulary must be an array")
            .len(),
        MAX_VOCABULARY_TERMS
    );

    // A list at or under the cap is untouched.
    let exactly: Vec<String> = (0..MAX_VOCABULARY_TERMS).map(|i| format!("T{i}")).collect();
    let config = transcription_config(None, &exactly, &TranscriptionExtras::none());
    assert_eq!(
        config["custom_vocabulary"].as_array().unwrap().len(),
        MAX_VOCABULARY_TERMS
    );
}

#[test]
fn live_model_is_rejected_by_the_rest_builder() {
    let p = TranscribeParams {
        model: LIVE_MODEL.to_string(),
        ..params()
    };
    let err = build_transcribe_request(&p).unwrap_err();
    assert!(matches!(
        err,
        TranscriptionError::BadRequest { status: 400, .. }
    ));
}

// ---- TRAP 2: mutual exclusion, enforced in one place ----

#[test]
fn vocabulary_suppresses_diarization_and_timestamps() {
    // GOLDEN (TRAP 2): Google 400s `custom_vocabulary` sent alongside either
    // `diarization_mode` or `timestamp_granularities`. Vocabulary wins.
    let extras = TranscriptionExtras {
        diarization_mode: Some("speaker".to_string()),
        timestamp_granularities: vec!["word".to_string()],
    };
    let config = transcription_config(Some("en-US"), &["HyperWhisper".to_string()], &extras);
    assert!(config.get("custom_vocabulary").is_some());
    assert!(config.get("diarization_mode").is_none());
    assert!(config.get("timestamp_granularities").is_none());
}

#[test]
fn extras_survive_when_there_is_no_vocabulary() {
    let extras = TranscriptionExtras {
        diarization_mode: Some("speaker".to_string()),
        timestamp_granularities: vec!["word".to_string()],
    };
    let config = transcription_config(Some("en-US"), &[], &extras);
    assert!(config.get("custom_vocabulary").is_none());
    assert_eq!(config["diarization_mode"], "speaker");
    assert_eq!(
        config["timestamp_granularities"],
        serde_json::json!(["word"])
    );
}

#[test]
fn vocabulary_that_normalizes_to_nothing_does_not_suppress_extras() {
    let extras = TranscriptionExtras {
        diarization_mode: Some("speaker".to_string()),
        ..Default::default()
    };
    let config = transcription_config(None, &["   ".to_string()], &extras);
    assert!(config.get("custom_vocabulary").is_none());
    assert_eq!(config["diarization_mode"], "speaker");
}

#[test]
fn no_built_body_ever_pairs_vocabulary_with_diarization_or_timestamps() {
    // The exhaustive guard: every body this module can build, REST and live,
    // across every combination of language/vocabulary/extras.
    let vocabs: [Vec<String>; 3] = [
        vec![],
        vec!["HyperWhisper".to_string()],
        vec!["a".to_string(), "b".to_string()],
    ];
    let extras_matrix = [
        TranscriptionExtras::none(),
        TranscriptionExtras {
            diarization_mode: Some("speaker".to_string()),
            ..Default::default()
        },
        TranscriptionExtras {
            timestamp_granularities: vec!["word".to_string()],
            ..Default::default()
        },
        TranscriptionExtras {
            diarization_mode: Some("speaker".to_string()),
            timestamp_granularities: vec!["word".to_string()],
        },
    ];

    let check = |config: &serde_json::Value, what: &str| {
        let has_vocab = config.get("custom_vocabulary").is_some();
        let has_diar = config.get("diarization_mode").is_some();
        let has_ts = config.get("timestamp_granularities").is_some();
        assert!(
            !(has_vocab && (has_diar || has_ts)),
            "{what}: custom_vocabulary must never be sent with diarization or timestamps"
        );
    };

    for lang in [None, Some("en-US"), Some("auto")] {
        for vocab in &vocabs {
            for extras in &extras_matrix {
                check(
                    &transcription_config(lang, vocab, extras),
                    "transcription_config",
                );

                let p = TranscribeParams {
                    language: lang.map(str::to_string),
                    vocabulary: vocab.clone(),
                    ..params()
                };
                let json = body_json(&build_transcribe_request(&p).unwrap());
                let rest = json
                    .get("generation_config")
                    .and_then(|g| g.get("transcription_config"))
                    .cloned()
                    .unwrap_or_else(|| serde_json::json!({}));
                check(&rest, "REST body");

                let frame: serde_json::Value =
                    serde_json::from_str(&build_live_setup_frame(LIVE_MODEL, lang, vocab)).unwrap();
                check(&frame["setup"]["input_audio_transcription"], "live setup");
            }
        }
    }
}
