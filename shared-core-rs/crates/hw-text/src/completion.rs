//! LLM post-processing completion policy — the single cross-platform decision
//! for "may this model output replace the user's transcript?".
//!
//! Two steps, shared by macOS, Windows and (via the conformance vectors in
//! `shared-conformance/completion-vectors.json`) the cloud backend:
//!
//! 1. [`normalize_termination`] maps a provider's wire-level finish/stop reason
//!    to a [`CompletionState`]. Missing metadata maps to
//!    [`CompletionState::Unspecified`] and PROCEEDS — custom OpenAI-compatible
//!    servers routinely omit `finish_reason`, and rejecting them would break
//!    every custom-endpoint user.
//! 2. [`evaluate_completion`] gates on that state (`OutputLimit` / `Incomplete`
//!    / `Malformed` never replace the transcript), then applies the same
//!    lenient marker handling as [`crate::strip_wrapper_markers`] with a
//!    distinguishable failure taxonomy for logs/telemetry.

use crate::text_processing::{
    contains_prompt_markers, earliest, strip_all, END_VARIANTS, START_VARIANTS,
};

/// The wire protocol a response's termination metadata is expressed in.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum WireProtocol {
    /// OpenAI-compatible Chat Completions (`choices[0].finish_reason`).
    OpenAiChat,
    /// Anthropic Messages (`stop_reason`).
    AnthropicMessages,
    /// No trustworthy termination metadata (e.g. in-process LLamaSharp).
    Unspecified,
}

/// Normalized termination state of one model response.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum CompletionState {
    /// The model stopped naturally.
    Complete,
    /// The model hit its output-token ceiling; the text is truncated.
    OutputLimit,
    /// The model stopped for another abnormal reason (content filter, refusal…).
    Incomplete,
    /// No termination metadata — proceed to text handling (see module docs).
    Unspecified,
    /// The response body could not be parsed at all.
    Malformed,
}

/// Why an evaluation rejected the model output (for logs/telemetry).
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum CompletionFailure {
    None,
    OutputLimit,
    IncompleteResponse,
    MalformedResponse,
    PromptLeakage,
    EmptyCleanedText,
}

/// The policy verdict: `text` is the cleaned content when `accepted`, the
/// untouched original transcript otherwise — callers can always insert `text`.
#[derive(Clone, PartialEq, Eq, Debug)]
pub struct CompletionEvaluation {
    pub accepted: bool,
    pub text: String,
    pub failure: CompletionFailure,
}

/// Map a wire-level finish/stop reason to a [`CompletionState`].
///
/// An empty or whitespace-only reason counts as missing; comparison is
/// case-insensitive (some OpenAI-compatible servers capitalize).
pub fn normalize_termination(wire_protocol: WireProtocol, reason: Option<&str>) -> CompletionState {
    let normalized = reason
        .map(|r| r.trim().to_lowercase())
        .filter(|r| !r.is_empty());
    match wire_protocol {
        WireProtocol::Unspecified => CompletionState::Unspecified,
        WireProtocol::OpenAiChat => match normalized.as_deref() {
            None => CompletionState::Unspecified,
            Some("stop") => CompletionState::Complete,
            Some("length") => CompletionState::OutputLimit,
            Some(_) => CompletionState::Incomplete,
        },
        WireProtocol::AnthropicMessages => match normalized.as_deref() {
            None => CompletionState::Unspecified,
            Some("end_turn") | Some("stop_sequence") => CompletionState::Complete,
            Some("max_tokens") => CompletionState::OutputLimit,
            Some(_) => CompletionState::Incomplete,
        },
    }
}

/// Decide whether `content` may replace `original_transcript`.
///
/// Rejecting states short-circuit; `Complete`/`Unspecified` fall through to the
/// same lenient marker handling as [`crate::strip_wrapper_markers`] (extract the
/// wrapped content when a start-marker variant is present, otherwise strip stray
/// markers), then reject leaked prompt scaffolding on either path, with the
/// leakage and empty outcomes reported as distinct failures instead of a bare
/// empty string.
pub fn evaluate_completion(
    original_transcript: &str,
    content: &str,
    state: CompletionState,
) -> CompletionEvaluation {
    let reject = |failure: CompletionFailure| CompletionEvaluation {
        accepted: false,
        text: original_transcript.to_string(),
        failure,
    };

    match state {
        CompletionState::OutputLimit => reject(CompletionFailure::OutputLimit),
        CompletionState::Incomplete => reject(CompletionFailure::IncompleteResponse),
        CompletionState::Malformed => reject(CompletionFailure::MalformedResponse),
        CompletionState::Complete | CompletionState::Unspecified => {
            // Mirrors `strip_wrapper_markers` (see module docs) — kept in one
            // place via the shared marker tables and helpers.
            let cleaned = match earliest(content, START_VARIANTS, 0) {
                None => strip_all(content.to_string(), END_VARIANTS).trim().to_string(),
                Some((start_idx, start_len)) => {
                    let after_start = start_idx + start_len;
                    let inner = match earliest(content, END_VARIANTS, after_start) {
                        Some((end_idx, _)) => &content[after_start..end_idx],
                        None => &content[after_start..],
                    };
                    let result = strip_all(inner.to_string(), START_VARIANTS);
                    strip_all(result, END_VARIANTS).trim().to_string()
                }
            };
            if contains_prompt_markers(&cleaned) {
                reject(CompletionFailure::PromptLeakage)
            } else if cleaned.is_empty() {
                reject(CompletionFailure::EmptyCleanedText)
            } else {
                CompletionEvaluation {
                    accepted: true,
                    text: cleaned,
                    failure: CompletionFailure::None,
                }
            }
        }
    }
}
