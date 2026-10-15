//! Whisper's non-speech markers: the bracketed or parenthesised annotation
//! whisper.cpp writes in place of words when it hears none (a blank-audio tag,
//! `(silence)`, `[inaudible]`, `[ Music ]` and kin). Issue #1811 moved the rule
//! here from three private copies (Windows `TranscriptionService`, shared-dotnet
//! `WhisperNonSpeechMarkers`, macOS `LibWhisper.cleanTranscription`) that had
//! already drifted apart. The marker set is the union of what those heads
//! stripped. The cross-platform cases live in
//! `shared-conformance/whisper-marker-vectors.json`.

use std::sync::OnceLock;

use regex::Regex;

/// Only these fixed names match, so a bracket the speaker dictated
/// (`[TODO]`, `(optional)`) survives. Either bracket shape, any case, and
/// whitespace inside the brackets.
fn marker_re() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| {
        Regex::new(
            r"(?i)[\[(]\s*(?:blank[_ ]audio|silence|no speech|inaudible|music|applause)\s*[\])]",
        )
        .unwrap()
    })
}

fn whitespace_run_re() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(r"\s{2,}").unwrap())
}

/// Removes Whisper's non-speech markers from a transcript and trims it.
///
/// - A transcript made only of markers (or only whitespace) comes back empty,
///   which the heads report as "no speech".
/// - Where a marker was removed, every whitespace run of two or more
///   characters collapses to one space, so `"Hello. <marker> Bye."` reads
///   `"Hello. Bye."`.
/// - Text with no marker is only trimmed: its inner whitespace (line breaks
///   included) is left alone, as both .NET copies did.
pub fn strip_whisper_non_speech_markers(text: &str) -> String {
    let trimmed = text.trim();
    let marker = marker_re();
    if !marker.is_match(trimmed) {
        return trimmed.to_string();
    }
    let removed = marker.replace_all(trimmed, " ");
    whitespace_run_re()
        .replace_all(&removed, " ")
        .trim()
        .to_string()
}

#[cfg(test)]
mod tests {
    use super::strip_whisper_non_speech_markers as strip;

    #[test]
    fn marker_only_input_is_empty() {
        assert_eq!(strip(" [BLANK_AUDIO] [BLANK_AUDIO]"), "");
        assert_eq!(strip("(Silence)"), "");
        assert_eq!(strip("   "), "");
        assert_eq!(strip(""), "");
    }

    #[test]
    fn text_without_a_marker_is_only_trimmed() {
        assert_eq!(strip("  line one\n\nline two  "), "line one\n\nline two");
    }

    #[test]
    fn a_dictated_bracket_survives() {
        assert_eq!(strip("fix [TODO] later"), "fix [TODO] later");
    }
}
