//! UniFFI surface for the LLM completion policy (`hw_text::completion`).
//!
//! Mirrors the leaf enums/record as UniFFI types, exposes the two policy steps
//! (`normalize_termination` / `evaluate_completion`), plus a JSON convenience
//! (`evaluate_llm_response_json`) that pulls the content and finish/stop reason
//! straight out of a raw response body — following `ffi_backup`'s JSON-string
//! boundary-marshalling precedent. An unparseable body maps to the `Malformed`
//! state, so the caller still gets a normal (rejecting) evaluation back.

use hw_text::completion as leaf;

/// Wire protocol a response's termination metadata is expressed in. Mirrors
/// `hw_text::completion::WireProtocol`.
#[derive(uniffi::Enum)]
pub enum WireProtocol {
    OpenAiChat,
    AnthropicMessages,
    Unspecified,
}

impl From<WireProtocol> for leaf::WireProtocol {
    fn from(p: WireProtocol) -> Self {
        match p {
            WireProtocol::OpenAiChat => leaf::WireProtocol::OpenAiChat,
            WireProtocol::AnthropicMessages => leaf::WireProtocol::AnthropicMessages,
            WireProtocol::Unspecified => leaf::WireProtocol::Unspecified,
        }
    }
}

/// Normalized termination state. Mirrors `hw_text::completion::CompletionState`.
#[derive(uniffi::Enum)]
pub enum CompletionState {
    Complete,
    OutputLimit,
    Incomplete,
    Unspecified,
    Malformed,
}

impl From<CompletionState> for leaf::CompletionState {
    fn from(s: CompletionState) -> Self {
        match s {
            CompletionState::Complete => leaf::CompletionState::Complete,
            CompletionState::OutputLimit => leaf::CompletionState::OutputLimit,
            CompletionState::Incomplete => leaf::CompletionState::Incomplete,
            CompletionState::Unspecified => leaf::CompletionState::Unspecified,
            CompletionState::Malformed => leaf::CompletionState::Malformed,
        }
    }
}

impl From<leaf::CompletionState> for CompletionState {
    fn from(s: leaf::CompletionState) -> Self {
        match s {
            leaf::CompletionState::Complete => CompletionState::Complete,
            leaf::CompletionState::OutputLimit => CompletionState::OutputLimit,
            leaf::CompletionState::Incomplete => CompletionState::Incomplete,
            leaf::CompletionState::Unspecified => CompletionState::Unspecified,
            leaf::CompletionState::Malformed => CompletionState::Malformed,
        }
    }
}

/// Rejection reason for logs/telemetry. Mirrors
/// `hw_text::completion::CompletionFailure`.
#[derive(uniffi::Enum)]
pub enum CompletionFailure {
    None,
    OutputLimit,
    IncompleteResponse,
    MalformedResponse,
    PromptLeakage,
    EmptyCleanedText,
}

impl From<leaf::CompletionFailure> for CompletionFailure {
    fn from(f: leaf::CompletionFailure) -> Self {
        match f {
            leaf::CompletionFailure::None => CompletionFailure::None,
            leaf::CompletionFailure::OutputLimit => CompletionFailure::OutputLimit,
            leaf::CompletionFailure::IncompleteResponse => CompletionFailure::IncompleteResponse,
            leaf::CompletionFailure::MalformedResponse => CompletionFailure::MalformedResponse,
            leaf::CompletionFailure::PromptLeakage => CompletionFailure::PromptLeakage,
            leaf::CompletionFailure::EmptyCleanedText => CompletionFailure::EmptyCleanedText,
        }
    }
}

/// Policy verdict: `text` is the cleaned content when `accepted`, the untouched
/// original transcript otherwise. Mirrors
/// `hw_text::completion::CompletionEvaluation`.
#[derive(uniffi::Record)]
pub struct CompletionEvaluation {
    pub accepted: bool,
    pub text: String,
    pub failure: CompletionFailure,
}

impl From<leaf::CompletionEvaluation> for CompletionEvaluation {
    fn from(e: leaf::CompletionEvaluation) -> Self {
        CompletionEvaluation {
            accepted: e.accepted,
            text: e.text,
            failure: e.failure.into(),
        }
    }
}

/// Map a wire-level finish/stop reason to a [`CompletionState`]. Missing (or
/// empty) reason → `Unspecified` (proceed); `WireProtocol::Unspecified` always
/// → `Unspecified`.
#[uniffi::export]
pub fn normalize_termination(
    wire_protocol: WireProtocol,
    reason: Option<String>,
) -> CompletionState {
    leaf::normalize_termination(wire_protocol.into(), reason.as_deref()).into()
}

/// Decide whether `content` may replace `original` given the normalized state.
#[uniffi::export]
pub fn evaluate_completion(
    original: String,
    content: String,
    state: CompletionState,
) -> CompletionEvaluation {
    leaf::evaluate_completion(&original, &content, state.into()).into()
}

/// One-call convenience over a raw response body: extract the message content
/// and finish/stop reason for `wire_protocol`, normalize, and evaluate.
/// A body that doesn't parse to the expected shape evaluates as `Malformed`
/// (rejected, `original` returned).
#[uniffi::export]
pub fn evaluate_llm_response_json(
    wire_protocol: WireProtocol,
    response_json: String,
    original: String,
) -> CompletionEvaluation {
    let wire_protocol: leaf::WireProtocol = wire_protocol.into();
    match parse_response(wire_protocol, &response_json) {
        Some((content, reason)) => {
            let state = leaf::normalize_termination(wire_protocol, reason.as_deref());
            leaf::evaluate_completion(&original, &content, state).into()
        }
        None => {
            leaf::evaluate_completion(&original, "", leaf::CompletionState::Malformed).into()
        }
    }
}

/// Pull `(content, finish/stop reason)` out of a raw response body.
///
/// `OpenAiChat` (and `Unspecified`, which shares the OpenAI-compatible shape):
/// `choices[0].message.content` + `choices[0].finish_reason`.
/// `AnthropicMessages`: first `content[]` block carrying `text` + `stop_reason`.
fn parse_response(
    wire_protocol: leaf::WireProtocol,
    body: &str,
) -> Option<(String, Option<String>)> {
    let v: serde_json::Value = serde_json::from_str(body).ok()?;
    match wire_protocol {
        leaf::WireProtocol::AnthropicMessages => {
            let text = v
                .get("content")?
                .as_array()?
                .iter()
                .find_map(|block| block.get("text").and_then(|t| t.as_str()))?;
            let reason = v
                .get("stop_reason")
                .and_then(|r| r.as_str())
                .map(str::to_string);
            Some((text.to_string(), reason))
        }
        leaf::WireProtocol::OpenAiChat | leaf::WireProtocol::Unspecified => {
            let first = v.get("choices")?.as_array()?.first()?;
            let text = first.get("message")?.get("content")?.as_str()?;
            let reason = first
                .get("finish_reason")
                .and_then(|r| r.as_str())
                .map(str::to_string);
            Some((text.to_string(), reason))
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn eval(wire_protocol: WireProtocol, body: &str) -> CompletionEvaluation {
        evaluate_llm_response_json(wire_protocol, body.to_string(), "original".to_string())
    }

    #[test]
    fn openai_shape_accepted() {
        let body = r#"{"choices":[{"message":{"content":"<<CLEANED>>Hi<<END>>"},"finish_reason":"stop"}]}"#;
        let e = eval(WireProtocol::OpenAiChat, body);
        assert!(e.accepted);
        assert_eq!(e.text, "Hi");
        assert!(matches!(e.failure, CompletionFailure::None));
    }

    #[test]
    fn openai_length_rejected_keeps_original() {
        let body = r#"{"choices":[{"message":{"content":"<<CLEANED>>partial"},"finish_reason":"length"}]}"#;
        let e = eval(WireProtocol::OpenAiChat, body);
        assert!(!e.accepted);
        assert_eq!(e.text, "original");
        assert!(matches!(e.failure, CompletionFailure::OutputLimit));
    }

    #[test]
    fn openai_missing_finish_reason_proceeds() {
        let body = r#"{"choices":[{"message":{"content":"<<CLEANED>>Hi<<END>>"}}]}"#;
        let e = eval(WireProtocol::OpenAiChat, body);
        assert!(e.accepted);
        assert_eq!(e.text, "Hi");
    }

    #[test]
    fn anthropic_shape_accepted() {
        let body = r#"{"content":[{"type":"text","text":"<<CLEANED>>Hi<<END>>"}],"stop_reason":"end_turn"}"#;
        let e = eval(WireProtocol::AnthropicMessages, body);
        assert!(e.accepted);
        assert_eq!(e.text, "Hi");
    }

    #[test]
    fn anthropic_max_tokens_rejected() {
        let body = r#"{"content":[{"type":"text","text":"cut off"}],"stop_reason":"max_tokens"}"#;
        let e = eval(WireProtocol::AnthropicMessages, body);
        assert!(!e.accepted);
        assert_eq!(e.text, "original");
        assert!(matches!(e.failure, CompletionFailure::OutputLimit));
    }

    #[test]
    fn unparseable_body_is_malformed() {
        for body in ["not json", "{}", r#"{"choices":[]}"#, r#"{"choices":[{"message":{}}]}"#] {
            let e = eval(WireProtocol::OpenAiChat, body);
            assert!(!e.accepted, "body should be malformed: {body}");
            assert_eq!(e.text, "original");
            assert!(matches!(e.failure, CompletionFailure::MalformedResponse));
        }
    }

    #[test]
    fn anthropic_missing_text_block_is_malformed() {
        let body = r#"{"content":[],"stop_reason":"end_turn"}"#;
        let e = eval(WireProtocol::AnthropicMessages, body);
        assert!(!e.accepted);
        assert!(matches!(e.failure, CompletionFailure::MalformedResponse));
    }
}
