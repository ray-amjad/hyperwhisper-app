//! Picker-language folding.
//!
//! Reduces the catalog's mixed-format upstream language codes (BCP-47,
//! ISO-639-2/3, sentinels) to the two-letter code space a language picker uses.

use std::collections::BTreeSet;

use super::CloudSttCatalog;

impl CloudSttCatalog {
    // -- Picker-language folding ---------------------------------------------

    /// The provider's languages reduced to the two-letter code space a language
    /// picker uses, or `None` when the catalog leaves the set `"unverified"` (so
    /// the caller falls back to its full list) or the id is unknown.
    ///
    /// The catalog stores upstream-native codes in mixed formats — BCP-47
    /// (`en-AU`, `cmn-Hans-CN`), ISO-639-2/3 (`eng`, `nld`), region variants
    /// (`ar-AE`) and sentinels (`multi`). We take the primary subtag, map
    /// three-letter codes through [`iso6392_to_iso6391`], drop anything with no
    /// two-letter equivalent, fold the picker aliases, and dedupe — so the dozens
    /// of Deepgram `ar-XX`/`en-XX` rows collapse to `ar`/`en`. `"auto"` is always
    /// included. The result is sorted, so it is a stable golden value.
    ///
    /// Port of Windows `PickerLanguageCodesForId`. macOS's picker lists BCP-47
    /// region rows as separate entries, so a macOS caller must match a picker row
    /// by its PRIMARY SUBTAG against this set, not by exact code.
    pub fn picker_language_codes(&self, id: &str) -> Option<Vec<String>> {
        let raw = self.language_codes(id)?;
        let mut folded: BTreeSet<String> = BTreeSet::new();
        folded.insert("auto".to_string());
        for code in raw {
            if let Some(normalized) = normalize_to_iso6391(code) {
                folded.insert(normalized.to_string());
            }
        }
        Some(folded.into_iter().collect())
    }
}

/// Two-letter base codes that differ between an upstream's catalog declaration
/// and the code a language picker exposes for the same language. Applied after
/// [`normalize_to_iso6391`] reduces to a 639-1 base. Without this fold those
/// languages match no picker row and silently vanish from the dropdown for the
/// Azure / Google Chirp tiers.
///
/// The three-letter forms (`nor`/`heb`/`jav`) already map straight to
/// `no`/`he`/`jw` in [`iso6392_to_iso6391`]; this only catches the two-letter
/// and BCP-47 aliases.
fn picker_language_alias(two: &str) -> Option<&'static str> {
    match two {
        // Norwegian Bokmål → the picker's macrolanguage "no".
        "nb" => Some("no"),
        // Deprecated Hebrew code (Azure/Google) → "he".
        "iw" => Some("he"),
        // ISO-639-1 Javanese → the picker's legacy "jw".
        "jv" => Some("jw"),
        _ => None,
    }
}

/// ISO-639-2/3 → ISO-639-1, scoped to the three-letter codes that actually
/// appear in the catalog AND name a language the picker can show.
///
/// Most entries reduce to a two-letter code. Two do not and map to themselves,
/// because the picker lists them under the three-letter form: `yue`
/// (Cantonese) and `haw` (Hawaiian). The odd one out is `fil` → `tl`: Filipino
/// has no 639-1 code of its own, but the picker shows it as Tagalog, so it has
/// to fold. The backend unfolds it (`resolveElevenLabsLanguage` in
/// hyperwhisper-cloud sends ElevenLabs `fil` back, since Scribe does not list
/// `tl`).
///
/// Codes with neither a 639-1 form nor a picker row (`ceb`, `kea`, `nso`,
/// `nya`, `ful`, `luo`, `lug`, `xho`, `zul`, `ibo`, `kur`, `wol`, `ast`, …) are
/// intentionally omitted — they normalize to `None` and are dropped.
fn iso6392_to_iso6391(three: &str) -> Option<&'static str> {
    let mapped = match three {
        "afr" => "af", "amh" => "am", "ara" => "ar", "asm" => "as", "aze" => "az",
        "bel" => "be", "ben" => "bn", "bos" => "bs", "bul" => "bg", "cat" => "ca",
        "ces" => "cs", "cmn" => "zh", "cym" => "cy", "dan" => "da", "deu" => "de",
        "ell" => "el", "eng" => "en", "est" => "et", "fas" => "fa", "fil" => "tl",
        "fin" => "fi", "fra" => "fr", "glg" => "gl", "guj" => "gu", "hau" => "ha",
        "haw" => "haw", "heb" => "he", "hin" => "hi", "hrv" => "hr", "hun" => "hu",
        "hye" => "hy", "ind" => "id", "isl" => "is", "ita" => "it", "jav" => "jw",
        "jpn" => "ja", "kan" => "kn", "kat" => "ka", "kaz" => "kk", "khm" => "km",
        "kor" => "ko", "lao" => "lo", "lav" => "lv", "lin" => "ln", "lit" => "lt",
        "ltz" => "lb", "mal" => "ml", "mar" => "mr", "mkd" => "mk", "mlt" => "mt",
        "mon" => "mn", "mri" => "mi", "msa" => "ms", "mya" => "my", "nep" => "ne",
        "nld" => "nl", "nor" => "no", "oci" => "oc", "pan" => "pa", "pol" => "pl",
        "por" => "pt", "pus" => "ps", "ron" => "ro", "rus" => "ru", "slk" => "sk",
        "slv" => "sl", "sna" => "sn", "snd" => "sd", "som" => "so", "spa" => "es",
        "srp" => "sr", "swa" => "sw", "swe" => "sv", "tam" => "ta", "tel" => "te",
        "tgk" => "tg", "tha" => "th", "tur" => "tr", "ukr" => "uk", "urd" => "ur",
        "uzb" => "uz", "vie" => "vi", "yor" => "yo", "yue" => "yue",
        _ => return None,
    };
    Some(mapped)
}

/// Reduce a single upstream language code to its picker code, or `None` when
/// there is no clean equivalent (so it is dropped rather than poisoning the
/// picker). Splits on `-`/`_` to take the primary subtag, then: two-letter
/// subtags pass through; three-letter subtags go through
/// [`iso6392_to_iso6391`]; everything else (sentinels like `multi`, and
/// three-letter codes with no picker row like `ceb`) → `None`. The picker
/// aliases are folded last.
fn normalize_to_iso6391(code: &str) -> Option<String> {
    let trimmed = code.trim();
    if trimmed.is_empty() {
        return None;
    }
    let primary = trimmed
        .replace('_', "-")
        .split('-')
        .next()
        .unwrap_or_default()
        .to_lowercase();
    let two = match primary.len() {
        2 => primary,
        3 => iso6392_to_iso6391(&primary)?.to_string(),
        _ => return None,
    };
    Some(picker_language_alias(&two).unwrap_or(&two).to_string())
}
