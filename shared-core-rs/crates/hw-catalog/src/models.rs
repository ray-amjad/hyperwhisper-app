//! WP-D2 — `models-catalog.json` parsing + lookup.
//!
//! Port of `app/macos/.../SharedModelsCatalog.swift` and
//! `app/windows/.../Services/SharedModelsCatalog.cs`. Plain Rust, sans-I/O: the
//! catalog JSON is embedded at compile time (`super::MODELS_CATALOG`), so this
//! module only parses an in-memory string and answers lookups.
//!
//! Lookup precedence (mirrors both reference impls):
//!   1. Exact `(provider, kind, id)`
//!   2. Wildcard `(provider, kind, "*")`
//!   3. Miss → `None` (callers default the booleans to `false`)
//!
//! Parity notes:
//! - macOS keys `Kind` from the raw string, defaulting unknown values to
//!   `.voice` (`Kind(rawValue:) ?? .voice`); Windows does the same
//!   (`ParseKind` → `_ => CatalogKind.Voice`). We match that: any unrecognized
//!   `kind` string parses to `Kind::Voice`.
//! - `language_support` returns `supports_all == true` for an uncatalogued model
//!   or a cloud row carrying neither `supportedLanguages` nor
//!   `supportsAllLanguages`, so an uncatalogued model is never wrongly hidden —
//!   identical to both platforms.

use std::collections::{BTreeMap, BTreeSet};

use serde::Deserialize;

/// Voice vs text. Disambiguates IDs that exist as both a transcription model
/// and a post-processing LLM (the Gemini family is the canonical example).
/// Lookups must pass the kind to avoid inheriting the wrong row's flags.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum Kind {
    Voice,
    Text,
}

impl Kind {
    /// Parse a raw `kind` string. Any unrecognized value maps to `Voice`, which
    /// is the documented default on both macOS (`Kind(rawValue:) ?? .voice`)
    /// and Windows (`ParseKind` `_ => CatalogKind.Voice`).
    pub fn from_raw(raw: &str) -> Kind {
        match raw {
            "text" => Kind::Text,
            _ => Kind::Voice,
        }
    }

    pub fn as_str(self) -> &'static str {
        match self {
            Kind::Voice => "voice",
            Kind::Text => "text",
        }
    }
}

/// One catalogued model row. Mirrors macOS `Entry` / Windows `CatalogEntry`.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Entry {
    pub provider: String,
    pub id: String,
    /// Raw `kind` string from JSON (e.g. `"voice"` / `"text"`). The parsed
    /// [`Kind`] used for keying is available via [`Entry::kind`].
    pub kind: String,
    #[serde(default)]
    pub supports_custom_vocabulary: bool,
    #[serde(default)]
    pub available_via_hyper_whisper_cloud: bool,
    #[serde(default)]
    pub platforms: Vec<String>,
    #[serde(default)]
    pub display_name: Option<String>,
    #[serde(default)]
    pub notes: Option<String>,
    /// Base ISO language codes this CLOUD voice model supports (region/script
    /// stripped). Absent on local/wildcard rows and on `supportsAllLanguages`
    /// rows.
    #[serde(default)]
    pub supported_languages: Option<Vec<String>>,
    #[serde(default)]
    pub is_english_only: Option<bool>,
    /// When true the model passes every language filter (Whisper-family,
    /// Google Chirp, Gemini, Grok).
    #[serde(default)]
    pub supports_all_languages: Option<bool>,
}

impl Entry {
    /// Parsed kind used for catalog keying.
    pub fn kind(&self) -> Kind {
        Kind::from_raw(&self.kind)
    }
}

/// Resolved language-filter capability for a single (cloud) voice model.
/// Mirrors macOS `LanguageSupport` / Windows `LanguageSupport`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LanguageSupport {
    /// Base ISO codes (region stripped). Empty when `supports_all` is true.
    pub codes: BTreeSet<String>,
    pub supports_all: bool,
}

impl LanguageSupport {
    /// Whether this model should pass the library filter for `base_code`
    /// (already region-stripped, e.g. `"es"`). A prefix check tolerates any
    /// stray region-qualified entry that slipped past normalization — matches
    /// both platforms' `supports(_:)` / `Supports(...)`.
    pub fn supports(&self, base_code: &str) -> bool {
        if self.supports_all {
            return true;
        }
        if self.codes.contains(base_code) {
            return true;
        }
        let prefix = format!("{base_code}-");
        self.codes.iter().any(|c| c.starts_with(&prefix))
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct CatalogFile {
    #[serde(default)]
    schema_version: i64,
    models: Vec<Entry>,
}

/// Error parsing the models catalog JSON. The catalog is embedded at compile
/// time, so in production this is effectively infallible; the error type exists
/// so callers (and tests) can parse arbitrary JSON strings safely.
#[derive(thiserror::Error, Debug)]
pub enum CatalogError {
    #[error("models-catalog.json failed to decode: {0}")]
    Decode(#[from] serde_json::Error),
}

type Key = (String, Kind, String);

/// Parsed, indexed models catalog. Build once with [`ModelsCatalog::parse`] (or
/// [`ModelsCatalog::embedded`]) and reuse; lookups are O(log n).
#[derive(Debug, Clone)]
pub struct ModelsCatalog {
    schema_version: i64,
    by_key: BTreeMap<Key, Entry>,
}

impl ModelsCatalog {
    /// Parse a models-catalog JSON string and index it by `(provider, kind, id)`.
    ///
    /// Later rows win on a duplicate key, matching the dictionary-assignment
    /// behavior of both reference loaders (`map[key] = entry`).
    pub fn parse(json: &str) -> Result<ModelsCatalog, CatalogError> {
        let file: CatalogFile = serde_json::from_str(json)?;
        let mut by_key: BTreeMap<Key, Entry> = BTreeMap::new();
        for entry in file.models {
            // Skip rows missing the required provider, matching Windows'
            // `IsNullOrEmpty(raw.Provider)` guard. (macOS' Decodable would fail
            // the whole decode on a missing field, but the shipped catalog never
            // omits it; the lenient Windows behavior is the safer unification.)
            if entry.provider.is_empty() {
                continue;
            }
            let key = (entry.provider.clone(), entry.kind(), entry.id.clone());
            by_key.insert(key, entry);
        }
        Ok(ModelsCatalog {
            schema_version: file.schema_version,
            by_key,
        })
    }

    /// Parse the compile-time-embedded `shared-models/models-catalog.json`.
    pub fn embedded() -> Result<ModelsCatalog, CatalogError> {
        ModelsCatalog::parse(super::MODELS_CATALOG)
    }

    /// `schemaVersion` from the catalog file.
    pub fn schema_version(&self) -> i64 {
        self.schema_version
    }

    /// Look up an entry by `(provider, kind, id)`, falling back to the
    /// provider/kind wildcard entry (`id == "*"`) when the exact id isn't
    /// catalogued. Returns `None` on a miss.
    pub fn entry(&self, provider: &str, kind: Kind, id: &str) -> Option<&Entry> {
        if let Some(exact) = self.get(provider, kind, id) {
            return Some(exact);
        }
        self.get(provider, kind, "*")
    }

    fn get(&self, provider: &str, kind: Kind, id: &str) -> Option<&Entry> {
        // Borrow-keyed lookup without allocating a String for the tuple key.
        self.by_key
            .iter()
            .find(|((p, k, i), _)| p == provider && *k == kind && i == id)
            .map(|(_, v)| v)
    }

    /// All catalogued entries (iteration order is by key). Primarily for tests
    /// and parity guards that need to scan the catalog.
    pub fn all_entries(&self) -> impl Iterator<Item = &Entry> {
        self.by_key.values()
    }

    /// Whether the resolved entry supports custom vocabulary. Defaults to
    /// `false` on a miss — matches both platforms.
    pub fn supports_custom_vocabulary(&self, provider: &str, kind: Kind, id: &str) -> bool {
        self.entry(provider, kind, id)
            .map(|e| e.supports_custom_vocabulary)
            .unwrap_or(false)
    }

    /// Whether the resolved entry is routable through HyperWhisper Cloud.
    /// Defaults to `false` on a miss — matches both platforms.
    pub fn available_via_hyper_whisper_cloud(&self, provider: &str, kind: Kind, id: &str) -> bool {
        self.entry(provider, kind, id)
            .map(|e| e.available_via_hyper_whisper_cloud)
            .unwrap_or(false)
    }

    /// Language-filter capability for a CLOUD voice model. Local providers carry
    /// no language data (their rows are wildcards), so callers resolve those
    /// in-code. A miss, or a cloud row with neither `supportedLanguages` nor
    /// `supportsAllLanguages`, yields `supports_all == true` so an uncatalogued
    /// model is never wrongly hidden — identical to macOS/Windows.
    pub fn language_support(&self, provider: &str, kind: Kind, id: &str) -> LanguageSupport {
        let Some(entry) = self.entry(provider, kind, id) else {
            return LanguageSupport {
                codes: BTreeSet::new(),
                supports_all: true,
            };
        };
        if entry.supports_all_languages == Some(true) {
            return LanguageSupport {
                codes: BTreeSet::new(),
                supports_all: true,
            };
        }
        if let Some(codes) = &entry.supported_languages {
            if !codes.is_empty() {
                return LanguageSupport {
                    codes: codes.iter().cloned().collect(),
                    supports_all: false,
                };
            }
        }
        LanguageSupport {
            codes: BTreeSet::new(),
            supports_all: true,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn catalog() -> ModelsCatalog {
        ModelsCatalog::embedded().expect("embedded models-catalog.json must parse")
    }

    #[test]
    fn embedded_catalog_parses_with_schema_version() {
        let c = catalog();
        assert_eq!(c.schema_version(), 1);
        assert!(c.all_entries().count() > 20);
    }

    // --- Golden: exact-id hit ------------------------------------------------

    #[test]
    fn exact_id_hit_returns_that_row() {
        let c = catalog();
        let e = c
            .entry("deepgram", Kind::Voice, "nova-3-general")
            .expect("exact row exists");
        assert_eq!(e.id, "nova-3-general");
        assert_eq!(e.provider, "deepgram");
        assert_eq!(e.kind(), Kind::Voice);
        assert!(e.supports_custom_vocabulary);
        assert!(e.available_via_hyper_whisper_cloud);
    }

    #[test]
    fn kind_disambiguates_same_id_across_voice_and_text() {
        let c = catalog();
        // gemini-2.5-flash is a voice row; there is no text row with that id.
        let voice = c.entry("gemini", Kind::Voice, "gemini-2.5-flash");
        assert!(voice.is_some());
        assert_eq!(voice.unwrap().kind(), Kind::Voice);
        let text = c.entry("gemini", Kind::Text, "gemini-2.5-flash");
        assert!(text.is_none());
    }

    #[test]
    fn provider_keying_distinguishes_same_id() {
        let c = catalog();
        // gpt-oss-120b exists under cerebras; groq uses openai/gpt-oss-120b.
        let cerebras = c.entry("cerebras", Kind::Text, "gpt-oss-120b");
        assert!(cerebras.is_some());
        assert_eq!(cerebras.unwrap().provider, "cerebras");
        // groq has no bare gpt-oss-120b and no text wildcard → miss.
        assert!(c.entry("groq", Kind::Text, "gpt-oss-120b").is_none());
        assert!(c.entry("groq", Kind::Text, "openai/gpt-oss-120b").is_some());
    }

    // --- Golden: wildcard fallback -------------------------------------------

    #[test]
    fn wildcard_fallback_for_local_provider() {
        let c = catalog();
        // appleSpeech only has an id == "*" row; any concrete id resolves to it.
        let e = c
            .entry("appleSpeech", Kind::Voice, "some-unlisted-model")
            .expect("falls back to wildcard");
        assert_eq!(e.id, "*");
        assert_eq!(e.provider, "appleSpeech");
        assert!(e.supports_custom_vocabulary);
    }

    #[test]
    fn wildcard_helpers_resolve_flags() {
        let c = catalog();
        assert!(c.supports_custom_vocabulary("localWhisper", Kind::Voice, "ggml-large-v3"));
        assert!(!c.supports_custom_vocabulary("parakeet", Kind::Voice, "v3"));
        assert!(!c.available_via_hyper_whisper_cloud("parakeet", Kind::Voice, "v3"));
    }

    #[test]
    fn empty_string_id_matches_grok_implicit_model() {
        let c = catalog();
        // Grok voice uses id == "" (xAI's single implicit model). It is NOT the
        // wildcard "*", so an empty-id lookup must hit it exactly.
        let e = c.entry("grok", Kind::Voice, "").expect("empty-id row exists");
        assert_eq!(e.id, "");
        assert!(e.available_via_hyper_whisper_cloud);
    }

    // --- Golden: language support yes/no -------------------------------------

    #[test]
    fn language_support_yes_for_listed_code() {
        let c = catalog();
        let ls = c.language_support("deepgram", Kind::Voice, "nova-3-general");
        assert!(!ls.supports_all);
        assert!(ls.supports("es"));
        assert!(ls.supports("en"));
    }

    #[test]
    fn language_support_no_for_unlisted_code() {
        let c = catalog();
        let ls = c.language_support("deepgram", Kind::Voice, "nova-3-general");
        // "th" (Thai) is not in nova-3-general's supportedLanguages.
        assert!(!ls.supports("th"));
    }

    #[test]
    fn language_support_all_when_supports_all_languages_flag() {
        let c = catalog();
        let ls = c.language_support("openai", Kind::Voice, "whisper-1");
        assert!(ls.supports_all);
        assert!(ls.codes.is_empty());
        // supports_all short-circuits to true for any code.
        assert!(ls.supports("xx"));
        assert!(ls.supports("zh"));
    }

    #[test]
    fn english_only_row_supports_only_english() {
        let c = catalog();
        let ls = c.language_support("deepgram", Kind::Voice, "nova-3-medical");
        assert!(!ls.supports_all);
        assert!(ls.supports("en"));
        assert!(!ls.supports("es"));
    }

    #[test]
    fn language_support_prefix_tolerates_region_qualified_entry() {
        // A stray region-qualified code ("pt-BR") must still satisfy a base
        // "pt" query via the prefix check.
        let json = r#"{
            "schemaVersion": 1,
            "models": [
                {"provider":"x","id":"m","kind":"voice",
                 "supportsCustomVocabulary":false,"availableViaHyperWhisperCloud":false,
                 "platforms":["macos"],"supportedLanguages":["pt-BR","fr"]}
            ]
        }"#;
        let c = ModelsCatalog::parse(json).unwrap();
        let ls = c.language_support("x", Kind::Voice, "m");
        assert!(ls.supports("pt"));
        assert!(ls.supports("fr"));
        assert!(!ls.supports("de"));
    }

    #[test]
    fn language_support_true_for_uncatalogued_model() {
        let c = catalog();
        let ls = c.language_support("nonexistent", Kind::Voice, "whatever");
        assert!(ls.supports_all);
        assert!(ls.supports("anything"));
    }

    // --- Golden: miss --------------------------------------------------------

    #[test]
    fn miss_returns_none_and_false_defaults() {
        let c = catalog();
        // openai has concrete voice rows but NO voice wildcard, so an unknown
        // openai voice id is a hard miss.
        assert!(c.entry("openai", Kind::Voice, "not-a-real-model").is_none());
        assert!(!c.supports_custom_vocabulary("openai", Kind::Voice, "not-a-real-model"));
        assert!(!c.available_via_hyper_whisper_cloud("openai", Kind::Voice, "not-a-real-model"));
    }

    #[test]
    fn miss_unknown_provider_returns_none() {
        let c = catalog();
        assert!(c.entry("totallyUnknown", Kind::Voice, "x").is_none());
        assert!(c.entry("totallyUnknown", Kind::Text, "x").is_none());
    }

    // --- Parsing edge cases --------------------------------------------------

    #[test]
    fn unknown_kind_string_parses_to_voice() {
        assert_eq!(Kind::from_raw("voice"), Kind::Voice);
        assert_eq!(Kind::from_raw("text"), Kind::Text);
        assert_eq!(Kind::from_raw("garbage"), Kind::Voice);
        assert_eq!(Kind::from_raw(""), Kind::Voice);
    }

    #[test]
    fn malformed_json_is_an_error_not_a_panic() {
        let err = ModelsCatalog::parse("{ not valid json");
        assert!(err.is_err());
    }

    #[test]
    fn row_missing_provider_is_skipped() {
        let json = r#"{
            "schemaVersion": 1,
            "models": [
                {"provider":"","id":"m","kind":"voice",
                 "supportsCustomVocabulary":true,"availableViaHyperWhisperCloud":false,
                 "platforms":["macos"]},
                {"provider":"keep","id":"m","kind":"voice",
                 "supportsCustomVocabulary":true,"availableViaHyperWhisperCloud":false,
                 "platforms":["macos"]}
            ]
        }"#;
        let c = ModelsCatalog::parse(json).unwrap();
        assert_eq!(c.all_entries().count(), 1);
        assert!(c.entry("keep", Kind::Voice, "m").is_some());
        assert!(c.entry("", Kind::Voice, "m").is_none());
    }
}
