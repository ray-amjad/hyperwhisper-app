//! Tests for the Gemini 3.5 Transcribe pre-recorded response parser.

use super::*;
use crate::contract::{HttpResponse, TranscriptionError};

fn resp(status: u16, body: &str) -> HttpResponse {
    HttpResponse {
        status,
        headers: vec![],
        body: body.as_bytes().to_vec(),
    }
}

// ---- response parsing ----

const SAMPLE: &str = r#"{
      "steps": [{"content": [{
        "text": "Hello, this is a test of HyperWhisper transcription.",
        "annotations": [
          {"type":"word_info","text":"Hello","start_index":0,"end_index":5,
           "start_offset":"1.800s","end_offset":"2.100s","speaker":"spk:0"},
          {"type":"other","text":"ignored"}
        ]}]}],
      "usage": {"total_output_tokens": 0,
                "input_tokens_by_modality": [
                  {"modality":"audio","tokens":150},
                  {"modality":"text","tokens":12}]}
    }"#;

#[test]
fn reads_transcript_from_steps_content_text() {
    let t = parse_transcribe_response(&resp(200, SAMPLE)).unwrap();
    assert_eq!(
        t.text,
        "Hello, this is a test of HyperWhisper transcription."
    );
}

#[test]
fn blank_transcript_is_no_speech() {
    let body = r#"{"steps":[{"content":[{"text":"   "}]}]}"#;
    assert_eq!(
        parse_transcribe_response(&resp(200, body)).unwrap_err(),
        TranscriptionError::NoSpeech
    );
}

#[test]
fn empty_content_part_is_no_speech_not_success() {
    // This is exactly what `:generateContent` returns for this model — a
    // silent empty part. Whatever produces it, it must not look like a win.
    let body = r#"{"steps":[{"content":[{}]}]}"#;
    assert_eq!(
        parse_transcribe_response(&resp(200, body)).unwrap_err(),
        TranscriptionError::NoSpeech
    );
}

#[test]
fn word_info_offsets_are_parsed_from_duration_strings() {
    // "1.800s" is a string; `as_f64()` on it is None.
    let words = parse_word_infos(&resp(200, SAMPLE)).unwrap();
    assert_eq!(words.len(), 1, "non-word_info annotations must be skipped");
    assert_eq!(words[0].text, "Hello");
    assert_eq!(words[0].start_seconds, Some(1.8));
    assert_eq!(words[0].end_seconds, Some(2.1));
    assert_eq!(words[0].start_index, Some(0));
    assert_eq!(words[0].end_index, Some(5));
    assert_eq!(words[0].speaker.as_deref(), Some("spk:0"));
}

#[test]
fn usage_reads_the_by_modality_list() {
    let usage = parse_usage(&resp(200, SAMPLE));
    assert_eq!(usage.audio_tokens, 150);
    assert_eq!(usage.text_tokens, 12);
}

#[test]
fn bad_key_400_is_unauthorized_not_bad_request() {
    let body = r#"{"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT"}}"#;
    assert_eq!(
        parse_transcribe_response(&resp(400, body)).unwrap_err(),
        TranscriptionError::Unauthorized
    );
}

#[test]
fn other_400s_stay_bad_request() {
    let body = r#"{"error":{"message":"custom_vocabulary is incompatible with diarization"}}"#;
    match parse_transcribe_response(&resp(400, body)).unwrap_err() {
        TranscriptionError::BadRequest { status, message } => {
            assert_eq!(status, 400);
            assert!(message.contains("incompatible"));
        }
        other => panic!("expected BadRequest, got {other:?}"),
    }
}

#[test]
fn server_errors_map_to_provider_unavailable() {
    assert_eq!(
        parse_transcribe_response(&resp(503, "upstream down")).unwrap_err(),
        TranscriptionError::ProviderUnavailable { status: 503 }
    );
}
