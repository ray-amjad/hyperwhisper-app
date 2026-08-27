//! The two language readings, and which providers take which.
//!
//! There is one rule the five providers share — "trim, and treat a blank string
//! or the app's `auto` sentinel as *send no language parameter*" — and one they
//! do not. Three of the five want an ISO-639-1 primary subtag and would reject
//! or ignore a region; two want the caller's tag exactly as the picker stored
//! it, because their own code list distinguishes the regions.
//!
//! | Provider | Reading | Why |
//! |---|---|---|
//! | ElevenLabs | [`normalize_language`] | `language_code` is documented ISO-639-1 |
//! | OpenAI Realtime | [`normalize_language`] | `transcription.language` is ISO-639-1 |
//! | xAI | [`normalize_language`], through [`crate::providers::grok::supported_formatting_language`] | its ITN support list is 25 primary subtags |
//! | Deepgram | [`language_tag`] | `zh-TW`, `zh-Hans`, `en-GB`, `pt-BR`… are **distinct** Deepgram codes |
//! | HyperWhisper Cloud | [`language_tag`] | the relay forwards the tag to Deepgram |
//!
//! Getting that split wrong is not cosmetic. `zh-TW` is the one region-tagged
//! entry in the Windows picker (`LanguageInfo.cs`) and it is stored verbatim, so
//! truncating it to `zh` asks Deepgram for Simplified Chinese when the user
//! chose Traditional. Both shipped .NET strategies that this module replaces
//! sent the tag verbatim on those two providers; only `shared-dotnet`
//! normalized, and it was the head with no users.

/// Normalize a caller's language selection to the **primary subtag** a provider
/// wants, or `None` when the parameter should be omitted entirely.
///
/// For the three providers whose language parameter is documented ISO-639-1.
/// Deepgram and HyperWhisper Cloud must use [`language_tag`] instead.
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
/// "en-US"     -> Some("en")  "zh-Hans"-> Some("zh")  "-en"  -> None
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
    let lower = language_selection(code)?.to_lowercase();
    let primary = lower.split('-').next().unwrap_or(&lower);
    if primary.is_empty() {
        return None;
    }
    Some(primary.to_string())
}

/// The caller's language selection **as the picker stored it**, trimmed, or
/// `None` when the parameter should be omitted entirely.
///
/// For Deepgram and for the HyperWhisper Cloud relay that forwards to it. Their
/// code list treats `zh` and `zh-TW` (and `en`/`en-GB`, `pt`/`pt-BR`, …) as
/// different languages, so the region and script subtags are content, not noise.
///
/// Case is preserved rather than lowercased: Deepgram's published codes are
/// mixed case (`zh-TW`, `zh-Hant`, `es-419`) and both shipped strategies sent
/// them unchanged. The HyperWhisper Cloud relay lowercases what it receives
/// before forwarding (`ws-streaming-deepgram.ts`), so nothing downstream depends
/// on this function doing it.
///
/// ```text
/// None    -> None            "  "      -> None          "auto"  -> None
/// "AUTO"  -> None            "en"      -> Some("en")    " en "  -> Some("en")
/// "en-US" -> Some("en-US")   "zh-TW"   -> Some("zh-TW")
/// ```
pub fn language_tag(code: Option<&str>) -> Option<String> {
    language_selection(code).map(str::to_string)
}

/// The half both readings share: trim, and map "no selection", a
/// whitespace-only string and the `"auto"` sentinel (in any case) to `None`.
fn language_selection(code: Option<&str>) -> Option<&str> {
    let raw = code?.trim();
    if raw.is_empty() || raw.eq_ignore_ascii_case("auto") {
        return None;
    }
    Some(raw)
}
