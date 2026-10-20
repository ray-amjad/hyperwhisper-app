//! Tests for the Gemini 3.5 Transcribe live frame builders, the live frame
//! decoder and base64.

use super::*;

// ---- live frames ----

#[test]
fn live_setup_uses_input_audio_transcription_not_generation_config() {
    // GOLDEN (TRAP 3): the pre-recorded path
    // (`setup.generation_config.transcription_config`) closes the socket
    // with code 1007.
    let frame: serde_json::Value = serde_json::from_str(&build_live_setup_frame(
        LIVE_MODEL,
        Some("en-US"),
        &["HyperWhisper".to_string()],
    ))
    .unwrap();
    assert_eq!(frame["setup"]["model"], "models/gemini-3.5-transcribe-live");
    assert_eq!(
        frame["setup"]["input_audio_transcription"]["language_codes"],
        serde_json::json!(["en-US"])
    );
    assert_eq!(
        frame["setup"]["input_audio_transcription"]["custom_vocabulary"],
        serde_json::json!(["HyperWhisper"])
    );
    assert!(frame["setup"].get("generation_config").is_none());
}

#[test]
fn live_setup_model_is_prefixed_once() {
    for input in ["", LIVE_MODEL, "models/gemini-3.5-transcribe-live"] {
        let frame: serde_json::Value =
            serde_json::from_str(&build_live_setup_frame(input, None, &[])).unwrap();
        assert_eq!(frame["setup"]["model"], "models/gemini-3.5-transcribe-live");
    }
}

#[test]
fn live_audio_frame_shape() {
    let frame: serde_json::Value =
        serde_json::from_str(&build_live_audio_frame_base64(&base64_encode(&[
            0x41, 0x42, 0x43,
        ])))
        .unwrap();
    assert_eq!(frame["realtime_input"]["audio"]["data"], "QUJD");
    assert_eq!(
        frame["realtime_input"]["audio"]["mime_type"],
        "audio/pcm;rate=16000"
    );
}

#[test]
fn live_end_frame_shape() {
    let frame: serde_json::Value =
        serde_json::from_str(&build_live_audio_stream_end_frame()).unwrap();
    assert_eq!(frame["realtime_input"]["audio_stream_end"], true);
}

#[test]
fn live_ws_url_carries_the_key_as_a_query_param() {
    let url = live_ws_url("k123", None);
    assert!(url.starts_with("wss://generativelanguage.googleapis.com/ws/"));
    assert!(url.ends_with("BidiGenerateContent?key=k123"));
}

#[test]
fn parses_live_server_messages() {
    let cases: [(&str, LiveServerMessage); 5] = [
        (r#"{"setupComplete":{}}"#, LiveServerMessage::SetupComplete),
        (
            r#"{"serverContent":{"interimInputTranscription":{"text":"hel"}}}"#,
            LiveServerMessage::PartialTranscript {
                text: "hel".to_string(),
            },
        ),
        (
            r#"{"serverContent":{"inputTranscription":{"text":"hello"}}}"#,
            LiveServerMessage::FinalTranscript {
                text: "hello".to_string(),
            },
        ),
        (
            r#"{"serverContent":{"generationComplete":true}}"#,
            LiveServerMessage::Complete,
        ),
        (
            r#"{"usageMetadata":{"totalTokenCount":3}}"#,
            LiveServerMessage::Unhandled,
        ),
    ];
    for (frame, expected) in cases {
        assert_eq!(
            parse_live_server_message(frame).unwrap(),
            expected,
            "{frame}"
        );
    }
}

/// A single `serverContent` frame may carry BOTH transcriptions. The final
/// is the committed text, so it must win: checking the interim first would
/// emit only the preview and never commit the segment, leaving the user's
/// document ending at the last hypothesis. Windows, macOS and the shared
/// .NET client all check final-first; this keeps Rust with them. Pinned
/// cross-platform by the `one-frame-carrying-both-decodes-as-the-final`
/// vector in `shared-conformance/live-frame-vectors.json`.
#[test]
fn a_frame_with_both_transcriptions_commits_the_final() {
    // Interim written FIRST, so a decoder that walks the object in wire
    // order rather than checking the final key explicitly gets it wrong.
    let frame = r#"{"serverContent":{
            "interimInputTranscription":{"text":"hello wor"},
            "inputTranscription":{"text":"hello world"}}}"#;
    assert_eq!(
        parse_live_server_message(frame).unwrap(),
        LiveServerMessage::FinalTranscript {
            text: "hello world".to_string()
        }
    );

    // Same for the snake_case spellings of the two keys.
    let snake = r#"{"server_content":{
            "interim_input_transcription":{"text":"hello wor"},
            "input_transcription":{"text":"hello world"}}}"#;
    assert_eq!(
        parse_live_server_message(snake).unwrap(),
        LiveServerMessage::FinalTranscript {
            text: "hello world".to_string()
        }
    );
}

/// The frame Google answers `audio_stream_end` with: the last committed
/// segment and the turn's completion on ONE `serverContent`. Both halves
/// are reported, because the completion is what a client's stop wait is
/// blocked on and dropping it costs that wait its whole budget.
#[test]
fn a_frame_with_a_final_and_a_completion_reports_both() {
    for frame in [
        r#"{"serverContent":{"inputTranscription":{"text":"hello world"},"generationComplete":true}}"#,
        r#"{"serverContent":{"generationComplete":true,"inputTranscription":{"text":"hello world"}}}"#,
        r#"{"serverContent":{"inputTranscription":{"text":"hello world"},"turnComplete":true}}"#,
        r#"{"server_content":{"input_transcription":{"text":"hello world"},"generation_complete":true}}"#,
    ] {
        assert_eq!(
            parse_live_server_message(frame).unwrap(),
            LiveServerMessage::FinalTranscriptAndComplete {
                text: "hello world".to_string()
            },
            "{frame}"
        );
    }

    // A completion flag that is present and false is not a boundary.
    assert_eq!(
        parse_live_server_message(
            r#"{"serverContent":{"inputTranscription":{"text":"hello world"},"generationComplete":false}}"#
        )
        .unwrap(),
        LiveServerMessage::FinalTranscript {
            text: "hello world".to_string()
        }
    );

    // An interim has no committed text to pair a completion with.
    assert_eq!(
        parse_live_server_message(
            r#"{"serverContent":{"interimInputTranscription":{"text":"hello wor"},"generationComplete":true}}"#
        )
        .unwrap(),
        LiveServerMessage::PartialTranscript {
            text: "hello wor".to_string()
        }
    );
}

/// The four completion spellings are an OR, not first-present-wins.
///
/// The `.or_else` chain that used to read this flag stopped at the first
/// key that was PRESENT, so `"generationComplete": false` next to a true
/// `turnComplete` decoded as "no boundary" and the stop handshake waited
/// out its whole budget. The cloud relay's decoder has always been a
/// genuine `||`, so this was also a disagreement between two decoders of
/// the same frame. Pinned cross-platform by the
/// `a-false-generation-complete-does-not-mask-a-true-turn-complete` vector.
#[test]
fn a_false_completion_flag_does_not_mask_a_true_one() {
    for frame in [
        r#"{"serverContent":{"generationComplete":false,"turnComplete":true}}"#,
        r#"{"serverContent":{"generationComplete":false,"turn_complete":true}}"#,
        r#"{"server_content":{"generation_complete":false,"turnComplete":true}}"#,
    ] {
        assert_eq!(
            parse_live_server_message(frame).unwrap(),
            LiveServerMessage::Complete,
            "{frame}"
        );
    }

    // And the same rule on the combined frame.
    assert_eq!(
        parse_live_server_message(
            r#"{"serverContent":{"inputTranscription":{"text":"hello world"},"generationComplete":false,"turnComplete":true}}"#
        )
        .unwrap(),
        LiveServerMessage::FinalTranscriptAndComplete {
            text: "hello world".to_string()
        }
    );

    // Every flag present and false is still not a boundary.
    assert_eq!(
        parse_live_server_message(
            r#"{"serverContent":{"generationComplete":false,"turnComplete":false}}"#
        )
        .unwrap(),
        LiveServerMessage::Unhandled
    );
}

#[test]
fn live_error_frame_is_surfaced() {
    let msg =
        parse_live_server_message(r#"{"error":{"code":1007,"message":"invalid setup"}}"#).unwrap();
    assert_eq!(
        msg,
        LiveServerMessage::Error {
            message: "invalid setup".to_string()
        }
    );
}

// ---- base64 ----

#[test]
fn base64_matches_the_standard_alphabet_and_padding() {
    assert_eq!(base64_encode(b""), "");
    assert_eq!(base64_encode(b"f"), "Zg==");
    assert_eq!(base64_encode(b"fo"), "Zm8=");
    assert_eq!(base64_encode(b"foo"), "Zm9v");
    assert_eq!(base64_encode(b"foob"), "Zm9vYg==");
    assert_eq!(base64_encode(b"fooba"), "Zm9vYmE=");
    assert_eq!(base64_encode(b"foobar"), "Zm9vYmFy");
    // Non-URL-safe alphabet: bytes that produce '+' and '/'.
    assert_eq!(base64_encode(&[0xFB, 0xFF]), "+/8=");
}
