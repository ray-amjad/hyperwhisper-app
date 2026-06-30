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

/// Maximum length of one sanitized vocabulary term.
pub const MAX_VOCABULARY_TERM_CHARS: usize = 80;

/// HW Cloud / routed `initial_prompt` vocabulary cap (soft backend limit; terms
/// beyond are silently dropped). Applied via [`normalize_vocabulary_capped`].
pub const HW_CLOUD_MAX_VOCAB_TERMS: usize = 100;

/// Normalize a vocabulary list: trim each term and drop empties, preserving the
/// caller's order. **No lowercasing and no de-duplication** — matches the shipped
/// platform behavior (vocabulary terms are often proper nouns where case matters).
pub fn normalize_vocabulary(words: &[String]) -> Vec<String> {
    words
        .iter()
        .map(|w| w.trim().to_string())
        .filter(|w| !w.is_empty())
        .collect()
}

/// Neutralize a vocabulary word for safe interpolation into a provider request
/// field (e.g. the Soniox `context` string). Port of macOS
/// `PromptBuilder.sanitizeVocabularyWord` (and `hw-text`'s `sanitize_vocabulary_word`):
/// drop `<`/`>` so a term cannot open/close a tag, collapse all whitespace
/// runs into single spaces so it cannot masquerade as a directive, and cap the
/// result at [`MAX_VOCABULARY_TERM_CHARS`].
pub fn sanitize_vocabulary_word(word: &str) -> String {
    let without_brackets: String = word.chars().filter(|&c| c != '<' && c != '>').collect();
    without_brackets
        .split_whitespace()
        .collect::<Vec<_>>()
        .join(" ")
        .chars()
        .take(MAX_VOCABULARY_TERM_CHARS)
        .collect()
}

/// Canonical vocabulary egress terms: sanitize, drop empties, de-duplicate
/// case-insensitively while preserving first-seen casing/order, and optionally
/// stop after `limit` terms.
pub fn keyword_boost_terms(words: &[String], limit: Option<usize>) -> Vec<String> {
    let mut seen: std::collections::HashSet<String> = std::collections::HashSet::new();
    let mut out: Vec<String> = Vec::new();
    for word in words {
        let sanitized = sanitize_vocabulary_word(word);
        if sanitized.is_empty() {
            continue;
        }
        if seen.insert(sanitized.to_lowercase()) {
            out.push(sanitized);
            if limit.is_some_and(|cap| out.len() >= cap) {
                break;
            }
        }
    }
    out
}

/// Normalize a vocabulary list with case-insensitive de-duplication and a cap,
/// for the HW Cloud / routed `initial_prompt` path.
pub fn normalize_vocabulary_capped(words: &[String], cap: usize) -> Vec<String> {
    keyword_boost_terms(words, Some(cap))
}

/// Join normalized vocabulary terms as bare-comma-separated CSV (`"a,b,c"`),
/// matching how the shipped HW Cloud / routed clients build the vocabulary
/// string. The caller supplies any surrounding prompt text, and the query
/// encoder ([`crate::providers::hyperwhisper_cloud::encode_query`]) handles the
/// percent-encoding of the joined value.
///
/// PARITY (separator): macOS `HyperWhisperCloudProvider.swift`
/// (`entries.joined(separator: ",")`) and `HyperWhisperRoutedTranscription.swift`,
/// and Windows `HyperWhisperCloudService.cs` /
/// `HyperWhisperRoutedTranscriptionClient.cs` (`string.Join(",", uniqueTerms)`)
/// all join with a **bare** comma — no space. Using ", " here would inject a
/// `%20` into the encoded value, diverging from the wire bytes. Keep this a bare
/// comma.
///
/// PARITY (encoding): this function does NOT percent-encode. When placed in the
/// `initial_prompt` query param, `encode_query` leaves the joined comma
/// **literal** — byte-matching macOS `URLQueryItem`, which does not escape `,`
/// (verified by running Swift `URLComponents`), so the wire value is
/// `initial_prompt=Rust,UniFFI`. (Windows `HttpUtility` instead emits a
/// lowercase `%2c`; we follow macOS, the verified platform. The backend decodes
/// `,`/`%2C`/`%2c` identically, so this is byte-parity with macOS and a
/// functionally-equivalent divergence from Windows.)
pub fn vocabulary_csv(words: &[String]) -> String {
    keyword_boost_terms(words, None).join(",")
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

/// A fixed multipart boundary string. Boundaries must not appear in the payload;
/// since audio is streamed by the platform and our fields are short, a fixed
/// token is fine and keeps the core deterministic (no RNG in Rust).
pub const MULTIPART_BOUNDARY: &str = "----HyperWhisperFormBoundary7MA4YWxkTrZu0gW";

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn vocab_csv_uses_shared_sanitized_deduped_terms() {
        let words = vec![
            "Swift".to_string(),
            "  Rust  ".to_string(),
            "".to_string(),
            "swift".to_string(), // duplicate dropped
            "Rust<script>".to_string(),
            "multi\n word".to_string(),
        ];
        assert_eq!(vocabulary_csv(&words), "Swift,Rust,Rustscript,multi word");
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
}
