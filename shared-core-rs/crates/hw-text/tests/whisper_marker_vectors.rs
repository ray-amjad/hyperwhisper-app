//! Conformance-vector tests for Whisper's non-speech markers (issue #1811).
//! `shared-conformance/whisper-marker-vectors.json` is the cross-platform source
//! of truth: the .NET heads (HyperWhisper.LocalInference.Tests, Windows
//! HyperWhisper.SmokeTests) and the macOS test target run the same file through
//! the binding.

use hw_text::strip_whisper_non_speech_markers;
use serde::Deserialize;

const VECTORS: &str = include_str!("../../../../shared-conformance/whisper-marker-vectors.json");

#[derive(Deserialize)]
struct Document {
    cases: Vec<Case>,
}

#[derive(Deserialize)]
struct Case {
    name: String,
    input: String,
    expected: String,
}

#[test]
fn every_whisper_marker_vector_matches() {
    let doc: Document =
        serde_json::from_str(VECTORS).expect("whisper-marker-vectors.json must parse");
    assert!(
        doc.cases.len() >= 20,
        "whisper-marker-vectors.json looks truncated"
    );
    let failures: Vec<String> = doc
        .cases
        .iter()
        .filter_map(|case| {
            let actual = strip_whisper_non_speech_markers(&case.input);
            (actual != case.expected).then(|| {
                format!(
                    "{}: input {:?} gave {:?}, expected {:?}",
                    case.name, case.input, actual, case.expected
                )
            })
        })
        .collect();
    assert!(failures.is_empty(), "{}", failures.join("\n"));
}

#[test]
fn every_case_is_idempotent() {
    let doc: Document =
        serde_json::from_str(VECTORS).expect("whisper-marker-vectors.json must parse");
    for case in &doc.cases {
        let once = strip_whisper_non_speech_markers(&case.input);
        assert_eq!(
            strip_whisper_non_speech_markers(&once),
            once,
            "{}: a second pass changed the text",
            case.name
        );
    }
}
