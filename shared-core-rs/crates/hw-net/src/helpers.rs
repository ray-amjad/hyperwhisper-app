//! Cross-provider request-building helpers shared by the 12 providers.
//!
//! - [`keyword_boost_terms`] — the common vocabulary egress normalization:
//!   sanitize each term, drop empties, case-insensitive de-dup, and optional
//!   cap. Per-provider param *names*, extra caps, intensifiers and formats live
//!   in each provider module (Wave 1).
//! - [`resolve_mime`] — extension → audio MIME, mirroring the platform resolvers.
//! - [`multipart_field`] / [`multipart_file`] — build [`Part`]s for a multipart body.
//!
//! Vocabulary terms may originate from imported backups, so all provider egress
//! routes must pass through the same sanitizer before interpolation.

use crate::contract::Part;

/// ElevenLabs Scribe v2 limits (applied in the elevenlabs provider module).
pub const ELEVENLABS_MAX_TERMS: usize = 100;
pub const ELEVENLABS_MAX_TERM_CHARS: usize = 50;

/// Maximum length of one sanitized vocabulary term. Re-exported from `hw-text`
/// so this crate has a single implementation of vocabulary-word sanitization.
pub use hw_text::prompt::MAX_VOCABULARY_TERM_CHARS;

/// HW Cloud / routed `initial_prompt` vocabulary cap (soft backend limit; terms
/// beyond are silently dropped). Applied via [`normalize_vocabulary_capped`].
pub const HW_CLOUD_MAX_VOCAB_TERMS: usize = 100;

/// Neutralize a vocabulary word for safe interpolation into a provider request
/// field (e.g. the Soniox `context` string). Port of macOS
/// `PromptBuilder.sanitizeVocabularyWord`, shared with `hw-text`'s prompt
/// assembly path so there is exactly one implementation: drop `<`/`>` so a
/// term cannot open/close a tag, collapse all whitespace runs into single
/// spaces so it cannot masquerade as a directive, and cap the result at
/// [`MAX_VOCABULARY_TERM_CHARS`].
pub use hw_text::sanitize_vocabulary_word;

/// Canonical vocabulary egress terms: sanitize, drop empties, de-duplicate
/// case-insensitively while preserving first-seen casing/order, and optionally
/// stop after `limit` terms.
///
/// `limit` is `None` for "no cap". `Some(0)` means what it says — zero terms,
/// the same answer LINQ `.Take(0)` and Swift `.prefix(0)` give. The cap is
/// therefore tested *before* each push, not after; testing it after would let
/// `Some(0)` return one term.
///
/// De-duplication runs on the SANITIZED term, i.e. *after* the 80-character
/// truncation in [`sanitize_vocabulary_word`], so two inputs longer than 80
/// characters that share their first 80 characters collapse into one. That is
/// deliberate, not an ordering accident: these are ASR keyword-boost hints, and
/// after truncation both inputs ARE the same hint — byte for byte. Emitting
/// both would send one provider the identical `keyterm` twice and burn two of
/// its limited term slots on it. De-duplicating on the pre-truncation term
/// would do exactly that. Pinned by
/// `dedupe_sees_the_truncated_term_so_an_eighty_char_prefix_collapses`.
pub fn keyword_boost_terms(words: &[String], limit: Option<usize>) -> Vec<String> {
    let mut seen: std::collections::HashSet<String> = std::collections::HashSet::new();
    let mut out: Vec<String> = Vec::new();
    for word in words {
        if limit.is_some_and(|cap| out.len() >= cap) {
            break;
        }
        let sanitized = sanitize_vocabulary_word(word);
        if sanitized.is_empty() {
            continue;
        }
        if seen.insert(sanitized.to_lowercase()) {
            out.push(sanitized);
        }
    }
    out
}

/// Normalize a vocabulary list with case-insensitive de-duplication and a cap,
/// for the HW Cloud / routed `initial_prompt` path.
pub fn normalize_vocabulary_capped(words: &[String], cap: usize) -> Vec<String> {
    keyword_boost_terms(words, Some(cap))
}

/// Default MIME when an extension is unknown. Matches macOS
/// `AudioMimeTypeResolver` (`audio/mp4`); the Windows dict defaults to
/// `audio/wav`, but recorded audio carries a real extension so this rarely hits.
pub const DEFAULT_AUDIO_MIME: &str = "audio/mp4";

/// Resolve an audio file path/extension to a MIME type. Mirrors the macOS
/// `AudioMimeTypeResolver` map (the superset of the Windows dict). Extension
/// match is case-insensitive. Unknown → [`DEFAULT_AUDIO_MIME`].
pub fn resolve_mime(path: &str) -> String {
    let ext = path
        .rsplit('.')
        .next()
        .filter(|e| !e.contains('/') && !e.contains('\\') && *e != path)
        .unwrap_or("")
        .to_lowercase();
    match ext.as_str() {
        "m4a" | "mp4" => "audio/mp4",
        "mp3" | "mpeg" | "mpga" => "audio/mpeg",
        "wav" => "audio/wav",
        "ogg" | "oga" => "audio/ogg",
        "opus" => "audio/opus",
        "flac" => "audio/flac",
        "webm" => "audio/webm",
        "aac" => "audio/aac",
        "caf" => "audio/x-caf",
        "aif" | "aiff" | "aifc" => "audio/aiff",
        "amr" => "audio/amr",
        _ => DEFAULT_AUDIO_MIME,
    }
    .to_string()
}

/// Build a multipart text field part.
pub fn multipart_field(name: impl Into<String>, value: impl Into<String>) -> Part {
    Part::Field {
        name: name.into(),
        value: value.into(),
    }
}

/// Build a multipart file part (audio streamed by the platform, never read here).
pub fn multipart_file(
    field: impl Into<String>,
    path: impl Into<String>,
    mime: impl Into<String>,
    filename: impl Into<String>,
) -> Part {
    Part::FileRef {
        field: field.into(),
        path: path.into(),
        mime: mime.into(),
        filename: filename.into(),
    }
}

/// Build a bounded typed in-memory multipart file. This is for small provider
/// metadata documents, never audio; audio must remain a [`Part::FileRef`].
pub fn multipart_inline_file(
    field: impl Into<String>,
    filename: impl Into<String>,
    mime: impl Into<String>,
    data: Vec<u8>,
) -> Result<Part, crate::contract::TranscriptionError> {
    if data.len() > crate::contract::MAX_INLINE_MULTIPART_FILE_BYTES {
        return Err(crate::contract::TranscriptionError::BadRequest {
            status: 400,
            message: "inline multipart file exceeds 64 KiB".to_string(),
        });
    }
    Ok(Part::InlineFile {
        field: field.into(),
        filename: filename.into(),
        mime: mime.into(),
        data,
    })
}

/// A fixed multipart boundary string. Boundaries must not appear in the payload;
/// since audio is streamed by the platform and our fields are short, a fixed
/// token is fine and keeps the core deterministic (no RNG in Rust).
pub const MULTIPART_BOUNDARY: &str = "----HyperWhisperFormBoundary7MA4YWxkTrZu0gW";

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn uncapped_vocab_uses_shared_sanitized_deduped_terms() {
        let words = vec![
            "Swift".to_string(),
            "  Rust  ".to_string(),
            "".to_string(),
            "swift".to_string(), // duplicate dropped
            "Rust<script>".to_string(),
            "multi\n word".to_string(),
        ];
        assert_eq!(
            keyword_boost_terms(&words, None),
            vec!["Swift", "Rust", "Rustscript", "multi word"]
        );
    }

    /// The cap counts terms that SURVIVED dedupe, not input words. `Some(1)`
    /// over a list whose first two entries collapse still yields one term.
    #[test]
    fn limit_counts_surviving_terms_not_input_words() {
        let words = vec![
            "  API  ".to_string(),
            "api".to_string(), // dedupes into the first
            "Rust".to_string(),
        ];
        assert_eq!(keyword_boost_terms(&words, Some(1)), vec!["API"]);
        assert_eq!(keyword_boost_terms(&words, Some(2)), vec!["API", "Rust"]);
        // A cap larger than the surviving set is not padded.
        assert_eq!(keyword_boost_terms(&words, Some(99)), vec!["API", "Rust"]);
    }

    /// `Some(0)` means zero terms — the answer `.Take(0)` / `.prefix(0)` give
    /// on the hosts. It must NOT mean "uncapped", and it must not leak the one
    /// term an after-the-push cap check would have let through.
    #[test]
    fn a_zero_limit_yields_no_terms() {
        let words = vec!["Rust".to_string(), "Swift".to_string()];
        assert_eq!(keyword_boost_terms(&words, Some(0)), Vec::<String>::new());
        // Contrast: None is how "no cap" is expressed.
        assert_eq!(keyword_boost_terms(&words, None), vec!["Rust", "Swift"]);
    }

    #[test]
    fn capped_vocab_dedups_case_insensitively_and_caps() {
        // GOLDEN (C1): trim + drop empties + case-insensitive dedup (first wins,
        // order preserved) + cap. "API"/"api" collapse to the first occurrence.
        let words = vec![
            "  API  ".to_string(),
            "".to_string(),
            "api".to_string(),
            "Rust".to_string(),
            "RUST".to_string(),
        ];
        assert_eq!(
            normalize_vocabulary_capped(&words, 100),
            vec!["API".to_string(), "Rust".to_string()]
        );

        // >100 terms → capped at 100, in order.
        let many: Vec<String> = (0..150).map(|i| format!("term{i}")).collect();
        let capped = normalize_vocabulary_capped(&many, 100);
        assert_eq!(capped.len(), 100);
        assert_eq!(capped.first().map(String::as_str), Some("term0"));
        assert_eq!(capped.last().map(String::as_str), Some("term99"));
    }

    /// Truncation happens BEFORE the de-dup key is taken, so two inputs that
    /// differ only past character 80 collapse into a single term. Sending both
    /// would put the identical `keyterm` on the wire twice and spend two of the
    /// provider's term slots on one hint — see the doc comment on
    /// `keyword_boost_terms`. This test exists so that a later "obvious" fix
    /// that moves de-dup ahead of truncation fails here and has to argue.
    #[test]
    fn dedupe_sees_the_truncated_term_so_an_eighty_char_prefix_collapses() {
        let prefix = "x".repeat(MAX_VOCABULARY_TERM_CHARS);
        let words = vec![format!("{prefix}alpha"), format!("{prefix}bravo")];

        let terms = keyword_boost_terms(&words, None);

        assert_eq!(terms, vec![prefix.clone()]);
        assert_eq!(terms[0].chars().count(), MAX_VOCABULARY_TERM_CHARS);

        // The boundary: differing INSIDE the first 80 characters keeps both.
        let short = "y".repeat(MAX_VOCABULARY_TERM_CHARS - 1);
        let both = vec![format!("{short}a"), format!("{short}b")];
        assert_eq!(
            keyword_boost_terms(&both, None),
            vec![format!("{short}a"), format!("{short}b")]
        );
    }

    #[test]
    fn sanitize_vocab_word_strips_brackets_and_collapses_whitespace() {
        // GOLDEN (F3): drop `<`/`>`, collapse internal whitespace runs, cap.
        assert_eq!(sanitize_vocabulary_word("Rust<script>"), "Rustscript");
        assert_eq!(sanitize_vocabulary_word("  Multi  Space  "), "Multi Space");
        // A word that is only brackets/whitespace collapses to empty (caller drops it).
        assert_eq!(sanitize_vocabulary_word("<>"), "");
        assert_eq!(
            sanitize_vocabulary_word(&"x".repeat(100)).chars().count(),
            80
        );
    }

    #[test]
    fn mime_resolves_common_extensions_case_insensitively() {
        assert_eq!(resolve_mime("/tmp/rec.wav"), "audio/wav");
        assert_eq!(resolve_mime("/tmp/rec.MP3"), "audio/mpeg");
        assert_eq!(resolve_mime("/tmp/rec.m4a"), "audio/mp4");
        assert_eq!(resolve_mime("/tmp/rec.caf"), "audio/x-caf");
        assert_eq!(resolve_mime("/tmp/noext"), DEFAULT_AUDIO_MIME);
    }

    #[test]
    fn inline_multipart_file_is_bounded() {
        assert!(multipart_inline_file(
            "request",
            "request.json",
            "application/json",
            vec![0; crate::contract::MAX_INLINE_MULTIPART_FILE_BYTES],
        )
        .is_ok());
        assert!(multipart_inline_file(
            "request",
            "request.json",
            "application/json",
            vec![0; crate::contract::MAX_INLINE_MULTIPART_FILE_BYTES + 1],
        )
        .is_err());
    }
}
