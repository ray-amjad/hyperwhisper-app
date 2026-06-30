//! Transcription text-processing helpers. Ported from macOS
//! `TranscriptionTextProcessing.swift` + Windows `TranscriptionTextProcessing.cs`
//! / `SmartSpacing.cs` / `PromptBuilder.ExtractCleanedText`.

use std::sync::OnceLock;

use regex::Regex;

const START_VARIANTS: &[&str] = &[
    "<<CLEANED>>",
    "<<CLEANED>",
    "<CLEANED>>",
    "<CLEANED>",
    "<</CLEANED>>",
];
const END_VARIANTS: &[&str] =
    &["<<END>>", "<<END>", "<END>>", "<END>", "<</END>>"];

/// Earliest (lowest) byte index at which any of `needles` occurs, plus the
/// matched needle's length.
fn earliest(haystack: &str, needles: &[&str], from: usize) -> Option<(usize, usize)> {
    let mut best: Option<(usize, usize)> = None;
    for n in needles {
        if let Some(rel) = haystack[from..].find(n) {
            let idx = from + rel;
            if best.is_none_or(|(b, _)| idx < b) {
                best = Some((idx, n.len()));
            }
        }
    }
    best
}

fn strip_all(mut s: String, needles: &[&str]) -> String {
    for n in needles {
        s = s.replace(n, "");
    }
    s
}

/// Extract the text wrapped in `<<CLEANED>>…<<END>>` markers from a
/// post-processing response.
///
/// Unification DECISION: adopts the **strict** (Windows) behaviour — if no start
/// marker is present the model didn't honour the wrapping contract, so this
/// returns an empty string and the caller keeps the original transcription.
/// Returning the raw response (the old macOS behaviour) risks leaking the system
/// prompt / app-context / screen-OCR text into the user's transcription.
pub fn extract_cleaned_from_wrapped(text: &str) -> String {
    let Some((start_idx, start_len)) = earliest(text, START_VARIANTS, 0) else {
        return String::new();
    };
    let after_start = start_idx + start_len;
    let inner = match earliest(text, END_VARIANTS, after_start) {
        Some((end_idx, _)) => &text[after_start..end_idx],
        None => &text[after_start..],
    };
    let result = strip_all(inner.to_string(), START_VARIANTS);
    let result = strip_all(result, END_VARIANTS);
    result.trim().to_string()
}

/// Lenient wrapper handling for **plain transcription text** (not a
/// post-processing response): if `<<CLEANED>>` markers are present, return the
/// wrapped content; otherwise strip any stray end-tag variants and return the
/// text unchanged. This is the old macOS `extractCleanedFromWrapped` passthrough
/// behaviour, kept for the routed / cloud raw-transcription call sites where an
/// empty return (the strict path) would wipe out a valid transcript.
pub fn strip_wrapper_markers(text: &str) -> String {
    match earliest(text, START_VARIANTS, 0) {
        None => {
            // No start marker: the model didn't honour the wrapping contract, so
            // this raw text might be the injected system prompt / app-context /
            // screen-OCR (not a cleaned transcript). If it carries any known
            // prompt section tag, reject it (return empty → the caller falls back
            // to the original transcript, which is safe) rather than risk leaking
            // the prompt into the user's output.
            let stripped = strip_all(text.to_string(), END_VARIANTS).trim().to_string();
            if contains_prompt_markers(&stripped) {
                String::new()
            } else {
                stripped
            }
        }
        Some((start_idx, start_len)) => {
            let after_start = start_idx + start_len;
            let inner = match earliest(text, END_VARIANTS, after_start) {
                Some((end_idx, _)) => &text[after_start..end_idx],
                None => &text[after_start..],
            };
            let result = strip_all(inner.to_string(), START_VARIANTS);
            strip_all(result, END_VARIANTS).trim().to_string()
        }
    }
}

/// Prompt section tags (and their closers) assembled by [`crate::prompt`]. If any
/// appears in an unwrapped model response, the model echoed our system prompt /
/// application or screen context instead of producing a cleaned transcript — the
/// lenient path must reject such output rather than leak it to the user.
const PROMPT_MARKERS: &[&str] = &[
    "<USER_SYSTEM_PROMPT>",
    "</USER_SYSTEM_PROMPT>",
    "<SYSTEM_INFO>",
    "</SYSTEM_INFO>",
    "<APPLICATION_CONTEXT>",
    "</APPLICATION_CONTEXT>",
    "<SCREEN_CONTEXT>",
    "</SCREEN_CONTEXT>",
    "<CUSTOM_VOCABULARY>",
    "</CUSTOM_VOCABULARY>",
    "<LANGUAGE_REQUIREMENTS>",
    "</LANGUAGE_REQUIREMENTS>",
    "<MODE_FLAGS>",
    "</MODE_FLAGS>",
];

/// Whether `s` contains any known prompt section tag — the signal that an
/// unwrapped response leaked the prompt rather than a cleaned transcript.
fn contains_prompt_markers(s: &str) -> bool {
    PROMPT_MARKERS.iter().any(|m| s.contains(m))
}

/// Streaming-display sanitizer (macOS only path): show content after the first
/// start marker, strip any stray markers. Does NOT trim (it's a live buffer).
pub fn sanitize_streaming_buffer(buffer: &str) -> String {
    let mut s = buffer.to_string();
    if let Some((idx, len)) = earliest(&s, START_VARIANTS, 0) {
        s = s[idx + len..].to_string();
    }
    let s = strip_all(s, START_VARIANTS);
    strip_all(s, END_VARIANTS)
}

/// Remove a single trailing period (but preserve an ellipsis "..").
pub fn remove_trailing_period(text: &str) -> String {
    let trimmed = text.trim();
    if trimmed.ends_with('.') && !trimmed.ends_with("..") {
        if let Some(idx) = text.rfind('.') {
            let mut result = String::with_capacity(text.len() - 1);
            result.push_str(&text[..idx]);
            result.push_str(&text[idx + 1..]);
            return result;
        }
    }
    text.to_string()
}

fn filler_re() -> &'static Regex {
    // No lookbehind/lookahead (unsupported by the `regex` crate): the surrounding
    // boundary is captured and partially restored in the replacement closure.
    //   g1 = preceding boundary (start "" or a whitespace run)
    //   g2 = filler word     g3 = optional comma
    //   g4 = following boundary (whitespace run or end "")
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(r"(?i)(^\s*|\s)(uh|um|er)(,?)(\s+|$)").unwrap())
}

fn leading_filler_re() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(r"(?i)^\s*\b(uh|um|er)\b").unwrap())
}

/// Remove the filler words "uh", "um", "er" (with an optional trailing comma)
/// when they stand alone, English only. Other languages / unknown ("auto") are
/// left untouched ("er"/"um" are real words in e.g. German).
pub fn remove_filler_words(text: &str, language: Option<&str>) -> String {
    let lang = language.unwrap_or("").to_lowercase();
    if !(lang == "en" || lang.starts_with("en-")) {
        return text.to_string();
    }

    // Apply to a fixpoint so adjacent fillers ("uh um") both go — a single pass
    // would leave the second once the shared whitespace is consumed.
    let re = filler_re();
    let mut current = text.to_string();
    loop {
        let next = re
            .replace_all(&current, |caps: &regex::Captures| {
                let preceding = &caps[1];
                let following = &caps[4];
                // Keep a single preceding space only when the filler is bounded by
                // whitespace on both sides; drop everything otherwise (start/end).
                if !preceding.is_empty() && !following.is_empty() {
                    " ".to_string()
                } else {
                    String::new()
                }
            })
            .into_owned();
        if next == current {
            break;
        }
        current = next;
    }

    // If a sentence-opening filler was stripped, the next word may now be
    // lowercase (STT had capitalized the filler as the opener) — restore it.
    if leading_filler_re().is_match(text) {
        if let Some(first) = current.chars().next() {
            if first.is_lowercase() {
                let upper: String = first.to_uppercase().collect();
                current = upper + &current[first.len_utf8()..];
            }
        }
    }

    current
}

fn new_line_command_re() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    // The trailing `\b` sits BEFORE the optional punctuation so "new line." still
    // matches (boundary between "line" and ".") but "newlines"/"new lines" do NOT
    // (no boundary between "line" and the following "s") — otherwise the command
    // would fire mid-word and leave an orphan "s".
    RE.get_or_init(|| Regex::new(r"(?i)\bnew\s*line\b[.,!?]?").unwrap())
}

/// Replace the spoken command "new line" / "newline" (+ optional trailing
/// punctuation) with a paragraph break.
pub fn process_voice_commands(text: &str) -> String {
    if text.is_empty() {
        return text.to_string();
    }
    new_line_command_re().replace_all(text, "\n\n").into_owned()
}

fn three_plus_newlines_re() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(r"\n{3,}").unwrap())
}

/// Final cleanup before saving a completed streaming session: normalize
/// newlines, trim each line's trailing whitespace, trim the ends, and collapse
/// 3+ blank lines to a paragraph break.
pub fn finalize_streaming_text(text: &str) -> String {
    if text.trim().is_empty() {
        return String::new();
    }
    let normalized = text.replace("\r\n", "\n").replace('\r', "\n");
    let joined = normalized
        .split('\n')
        .map(|line| line.trim_end())
        .collect::<Vec<_>>()
        .join("\n");
    let trimmed = joined.trim();
    three_plus_newlines_re()
        .replace_all(trimmed, "\n\n")
        .into_owned()
}
