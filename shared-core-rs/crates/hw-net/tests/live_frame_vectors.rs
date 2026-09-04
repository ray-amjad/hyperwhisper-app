//! Conformance-vector tests for the Gemini 3.5 Transcribe Live WebSocket
//! frames. The vectors in `shared-conformance/live-frame-vectors.json` are the
//! cross-platform source of truth: the Rust builders here are not exposed over
//! UniFFI, so the native streaming strategies on macOS, Windows and Linux build
//! their own frames and are held to these same answers.
//!
//! What this pins above all else is the config *path*:
//! `setup.input_audio_transcription`. The pre-recorded location
//! (`setup.generation_config.transcription_config`) closes the socket with 1007.

use hw_net::providers::gemini_transcribe as gt;
use serde::Deserialize;

const VECTORS: &str = include_str!("../../../../shared-conformance/live-frame-vectors.json");

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Document {
    cases: Vec<Case>,
    server_messages: Vec<ServerCase>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Case {
    name: String,
    kind: String,
    #[serde(default)]
    model: Option<String>,
    #[serde(default)]
    language: Option<String>,
    #[serde(default)]
    vocabulary: Vec<String>,
    #[serde(default)]
    pcm_base64: Option<String>,
    expect: serde_json::Value,
}

#[derive(Deserialize)]
struct ServerCase {
    name: String,
    frame: serde_json::Value,
    expect: ServerExpect,
}

#[derive(Deserialize)]
struct ServerExpect {
    kind: String,
    #[serde(default)]
    text: Option<String>,
}

fn document() -> Document {
    serde_json::from_str(VECTORS).expect("live-frame-vectors.json must parse")
}

fn built(case: &Case) -> serde_json::Value {
    let frame = match case.kind.as_str() {
        "setup" => gt::build_live_setup_frame(
            case.model.as_deref().unwrap_or(""),
            case.language.as_deref(),
            &case.vocabulary,
        ),
        "audio" => gt::build_live_audio_frame_base64(
            case.pcm_base64
                .as_deref()
                .expect("audio case needs pcmBase64"),
        ),
        "audioStreamEnd" => gt::build_live_audio_stream_end_frame(),
        other => panic!("unknown case kind {other:?} in {}", case.name),
    };
    serde_json::from_str(&frame).expect("built frame must be valid JSON")
}

#[test]
fn client_frames_match_the_vectors() {
    for case in document().cases {
        assert_eq!(built(&case), case.expect, "case {}", case.name);
    }
}

#[test]
fn no_setup_vector_uses_the_pre_recorded_config_path() {
    // TRAP 3, asserted on the vectors themselves so a future edit to the JSON
    // cannot quietly reintroduce the shape that closes the socket with 1007.
    for case in document().cases {
        if case.kind != "setup" {
            continue;
        }
        let setup = &case.expect["setup"];
        assert!(
            setup.get("input_audio_transcription").is_some(),
            "case {}: live setup config must live at setup.input_audio_transcription",
            case.name
        );
        assert!(
            setup.get("generation_config").is_none(),
            "case {}: setup.generation_config is the PRE-RECORDED shape and closes the socket with 1007",
            case.name
        );
    }
}

#[test]
fn no_setup_vector_pairs_vocabulary_with_diarization_or_timestamps() {
    for case in document().cases {
        if case.kind != "setup" {
            continue;
        }
        let config = &case.expect["setup"]["input_audio_transcription"];
        let has_vocab = config.get("custom_vocabulary").is_some();
        let has_extras = config.get("diarization_mode").is_some()
            || config.get("timestamp_granularities").is_some();
        assert!(
            !(has_vocab && has_extras),
            "case {}: custom_vocabulary is rejected alongside diarization or timestamps",
            case.name
        );
    }
}

#[test]
fn server_frames_decode_to_the_expected_events() {
    for case in document().server_messages {
        let decoded = gt::parse_live_server_message(&case.frame.to_string())
            .unwrap_or_else(|e| panic!("case {}: {e}", case.name));
        let (kind, text) = match &decoded {
            gt::LiveServerMessage::SetupComplete => ("setupComplete", None),
            gt::LiveServerMessage::PartialTranscript { text } => {
                ("partialTranscript", Some(text.clone()))
            }
            gt::LiveServerMessage::FinalTranscript { text } => {
                ("finalTranscript", Some(text.clone()))
            }
            gt::LiveServerMessage::FinalTranscriptAndComplete { text } => {
                ("finalTranscriptAndComplete", Some(text.clone()))
            }
            gt::LiveServerMessage::Complete => ("complete", None),
            gt::LiveServerMessage::Error { message } => ("error", Some(message.clone())),
            gt::LiveServerMessage::Unhandled => ("unhandled", None),
        };
        assert_eq!(kind, case.expect.kind, "case {}", case.name);
        if let Some(expected) = case.expect.text {
            assert_eq!(
                text.as_deref(),
                Some(expected.as_str()),
                "case {}",
                case.name
            );
        }
    }
}
