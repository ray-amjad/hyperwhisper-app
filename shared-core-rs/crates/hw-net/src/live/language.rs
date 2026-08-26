//! The one language normalizer.
//!
//! "Trim, lowercase, drop everything after the first `-`, map `auto` to nothing"
//! was written seven times: macOS `OpenAIStreamingStrategy.normalizedLanguageCode`
//! and `ElevenLabsStreamingStrategy.normalizeLanguageCode`, Windows
//! `OpenAIStreamingStrategy.NormalizeLanguageCode` and
//! `ElevenLabsStreamingStrategy.NormalizeLanguageCode`, `shared-dotnet`
//! `LiveTranscriptionProtocolFactory.Language`, the xAI copies, and the generic
//! half of this crate's own `grok::supported_formatting_language`.

/// Normalize a caller's language selection to the primary subtag a provider
/// wants, or `None` when the parameter should be omitted entirely.
///
/// `None` covers three inputs that all mean the same thing to a provider: no
/// selection, a blank/whitespace-only string, and the app's `"auto"` sentinel.
/// Every shipped copy of this rule omitted the parameter in all three cases —
/// asking a provider to auto-detect is spelled by *not sending* a language, not
/// by sending the word "auto".
///
/// ```text
/// None        -> None        "  "     -> None        "auto" -> None
/// "AUTO"      -> None        "en"     -> Some("en")  " EN " -> Some("en")
/// "en-US"     -> Some("en")  "zh-Hans"-> Some("zh")  "-en"  -> Some("")…
/// ```
///
/// The last case is the one divergence worth naming. Two of the shipped copies
/// used `IndexOf('-') > 0`, which leaves a leading-hyphen tag whole; the others
/// split unconditionally and produce an empty primary subtag. Neither is
/// reachable from the app (the picker only ever supplies real BCP-47 tags), so
/// this takes the split — the macOS and `shared-dotnet` behaviour — and then
/// treats the empty result the same way it treats an empty input: `None`. That
/// makes the function total, and it can never emit `language=` with no value.
pub fn normalize_language(code: Option<&str>) -> Option<String> {
    let raw = code?.trim();
    if raw.is_empty() {
        return None;
    }
    let lower = raw.to_lowercase();
    if lower == "auto" {
        return None;
    }
    let primary = lower.split('-').next().unwrap_or(&lower);
    if primary.is_empty() {
        return None;
    }
    Some(primary.to_string())
}
