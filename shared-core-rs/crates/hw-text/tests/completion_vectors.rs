//! Conformance-vector tests for the completion policy. The vectors in
//! `shared-conformance/completion-vectors.json` are the cross-platform source
//! of truth — the cloud backend runs the same cases through its TS
//! implementation (`hyperwhisper-cloud/src/lib/llm-completion.test.ts`).

use hw_text::completion::{
    evaluate_completion, normalize_termination, CompletionFailure, CompletionState, WireProtocol,
};
use serde::Deserialize;

const VECTORS: &str = include_str!("../../../../shared-conformance/completion-vectors.json");

#[derive(Deserialize)]
struct Document {
    cases: Vec<Case>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Case {
    name: String,
    wire_protocol: String,
    reason: Option<String>,
    content: String,
    original: String,
    expect: Expectation,
}

#[derive(Deserialize)]
struct Expectation {
    state: Option<String>,
    accepted: bool,
    text: Option<String>,
    failure: Option<String>,
}

fn wire_protocol(raw: &str) -> WireProtocol {
    match raw {
        "openai_chat" => WireProtocol::OpenAiChat,
        "anthropic_messages" => WireProtocol::AnthropicMessages,
        "unspecified" => WireProtocol::Unspecified,
        other => panic!("unknown wireProtocol in vectors: {other}"),
    }
}

fn state_name(state: CompletionState) -> &'static str {
    match state {
        CompletionState::Complete => "complete",
        CompletionState::OutputLimit => "output_limit",
        CompletionState::Incomplete => "incomplete",
        CompletionState::Unspecified => "unspecified",
        CompletionState::Malformed => "malformed",
    }
}

fn failure_name(failure: CompletionFailure) -> &'static str {
    match failure {
        CompletionFailure::None => "none",
        CompletionFailure::OutputLimit => "output_limit",
        CompletionFailure::IncompleteResponse => "incomplete_response",
        CompletionFailure::MalformedResponse => "malformed_response",
        CompletionFailure::PromptLeakage => "prompt_leakage",
        CompletionFailure::EmptyCleanedText => "empty_cleaned_text",
    }
}

#[test]
fn conformance_vectors() {
    let doc: Document = serde_json::from_str(VECTORS).expect("vectors JSON must parse");
    assert!(!doc.cases.is_empty());

    for case in &doc.cases {
        let state = normalize_termination(wire_protocol(&case.wire_protocol), case.reason.as_deref());
        if let Some(expected) = &case.expect.state {
            assert_eq!(state_name(state), expected, "state mismatch in case `{}`", case.name);
        }

        let eval = evaluate_completion(&case.original, &case.content, state);
        assert_eq!(
            eval.accepted, case.expect.accepted,
            "accepted mismatch in case `{}`",
            case.name
        );
        if let Some(expected) = &case.expect.text {
            assert_eq!(&eval.text, expected, "text mismatch in case `{}`", case.name);
        }
        if let Some(expected) = &case.expect.failure {
            assert_eq!(
                failure_name(eval.failure),
                expected,
                "failure mismatch in case `{}`",
                case.name
            );
        }
        // A rejected evaluation must hand back the untouched original transcript.
        if !eval.accepted {
            assert_eq!(eval.text, case.original, "rejected case `{}` must keep the original", case.name);
        }
    }
}
