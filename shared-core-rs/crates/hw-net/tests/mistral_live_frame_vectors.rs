//! Replays `shared-conformance/mistral-live-frame-vectors.json` through the
//! public [`hw_net::live::LiveSession`] for the BYOK Mistral Voxtral realtime
//! socket: the connect descriptor, the `session.update` start frame, the audio
//! envelope, the stop frames, and the parse of each server frame in order.

use hw_net::live::{AudioFraming, LiveConfig, LiveEvent, LiveProvider, LiveSession, StopStep};
use serde::Deserialize;

const VECTORS: &str =
    include_str!("../../../../shared-conformance/mistral-live-frame-vectors.json");

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Document {
    connect: Vec<ConnectCase>,
    audio_frame: Envelope,
    stop_frames: Vec<String>,
    session: Vec<SessionCase>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct ConnectCase {
    name: String,
    model: Option<String>,
    language: Option<String>,
    vocabulary: Vec<String>,
    expect_url: String,
    expect_authorization: String,
    expect_start_frame: serde_json::Value,
}

#[derive(Deserialize)]
struct Envelope {
    prefix: String,
    suffix: String,
}

#[derive(Deserialize)]
struct SessionCase {
    name: String,
    frames: Vec<Frame>,
}

#[derive(Deserialize)]
struct Frame {
    #[serde(rename = "in")]
    input: String,
    expect: Expected,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Expected {
    event: String,
    #[serde(default)]
    text: Option<String>,
    #[serde(default)]
    session_id: Option<String>,
    #[serde(default)]
    duration_seconds: Option<f64>,
}

fn document() -> Document {
    serde_json::from_str(VECTORS).expect("mistral-live-frame-vectors.json parses")
}

fn session(
    model: Option<String>,
    language: Option<String>,
    vocabulary: Vec<String>,
) -> LiveSession {
    let mut config = LiveConfig::new(LiveProvider::Mistral);
    config.api_key = Some("test-key".to_string());
    config.model = model;
    config.language = language;
    config.vocabulary = vocabulary;
    LiveSession::new(config)
}

#[test]
fn connect_cases_match() {
    for case in document().connect {
        let connect = session(case.model, case.language, case.vocabulary)
            .connect()
            .expect("connect");
        assert_eq!(connect.url, case.expect_url, "{}", case.name);
        let auth = connect
            .headers
            .iter()
            .find(|h| h.name == "Authorization")
            .map(|h| h.value.clone());
        assert_eq!(
            auth.as_deref(),
            Some(case.expect_authorization.as_str()),
            "{}",
            case.name
        );
        assert_eq!(connect.start_frames.len(), 1, "{}", case.name);
        let frame: serde_json::Value =
            serde_json::from_str(&connect.start_frames[0].data).expect("start frame json");
        assert_eq!(frame, case.expect_start_frame, "{}", case.name);
    }
}

#[test]
fn audio_envelope_and_stop_frames_match() {
    let doc = document();
    let connect = session(None, None, Vec::new()).connect().expect("connect");
    assert_eq!(
        connect.framing,
        AudioFraming::Base64Json {
            prefix: doc.audio_frame.prefix,
            suffix: doc.audio_frame.suffix,
        }
    );
    let sent: Vec<String> = session(None, None, Vec::new())
        .stop_sequence(0)
        .into_iter()
        .filter_map(|step| match step {
            StopStep::SendText { text } => Some(text),
            _ => None,
        })
        .collect();
    assert_eq!(sent, doc.stop_frames);
}

#[test]
fn session_cases_match() {
    for case in document().session {
        let mut live = session(None, None, Vec::new());
        live.connect().expect("connect");
        for (index, frame) in case.frames.iter().enumerate() {
            let event = live.parse(&frame.input);
            let label = format!("{} frame {index}", case.name);
            let e = &frame.expect;
            match (e.event.as_str(), event) {
                ("ignore", LiveEvent::Ignore) => {}
                ("sessionStarted", LiveEvent::SessionStarted { session_id }) => {
                    assert_eq!(session_id, e.session_id, "{label}")
                }
                ("partial", LiveEvent::PartialTranscript { text }) => {
                    assert_eq!(Some(text), e.text.clone(), "{label}")
                }
                ("final", LiveEvent::FinalTranscript { text }) => {
                    assert_eq!(Some(text), e.text.clone(), "{label}")
                }
                (
                    "finalAndComplete",
                    LiveEvent::FinalTranscriptAndSessionComplete {
                        text,
                        duration_seconds,
                        ..
                    },
                ) => {
                    assert_eq!(Some(text), e.text.clone(), "{label}");
                    assert_eq!(Some(duration_seconds), e.duration_seconds, "{label}");
                }
                ("error", LiveEvent::Error { message, .. }) => {
                    assert_eq!(Some(message), e.text.clone(), "{label}")
                }
                (expected, other) => panic!("{label}: expected {expected}, got {other:?}"),
            }
        }
    }
}
