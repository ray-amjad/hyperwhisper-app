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

/// Optional capabilities for a voice model. Omitted fields and an omitted
/// block both decode to false so older catalog rows remain conservative.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct VoiceCapabilities {
    #[serde(default)]
    pub code_switching: bool,
    #[serde(default)]
    pub endpointing: bool,
    #[serde(default)]
    pub context_bias: bool,
    #[serde(default)]
    pub language_bias: bool,
    #[serde(default)]
    pub turn_timestamps: bool,
    #[serde(default)]
    pub diarization: bool,
    #[serde(default)]
    pub word_timestamps: bool,
}

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
    /// Structured voice-only capability metadata. Text and older rows omit it.
    #[serde(default)]
    pub voice_capabilities: Option<VoiceCapabilities>,
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
        // An empty id means "no model recorded", not "a model called empty".
        // Every Grok mode saved before xAI exposed a `model` parameter carries
        // one, and until 2026-09-19 it hit an empty-id row here. Resolve it the
        // way the request path does — to the cloud-STT catalog default — but
        // ONLY for a provider that really shipped a blank id. Every other
        // provider always named its model, so a blank there is corrupt data and
        // guessing at it would hide the fault.
        if id.is_empty() {
            if let Some(default_id) = legacy_blank_id_target(provider, kind) {
                if let Some(hit) = self.get(provider, kind, default_id) {
                    return Some(hit);
                }
            }
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

/// Providers that once had NO model parameter at all, so a user's stored model
/// id can legitimately be the empty string. Grok is the only one: `/v1/stt`
/// took no `model` until 2026-09-19, and xAI served exactly one model, which
/// was Grok Voice Transcribe 1. Adding the 1.0 row to the catalog means the
/// blank can no longer be resolved by "the provider has one row", so it is
/// resolved explicitly here instead.
const LEGACY_BLANK_MODEL_PROVIDERS: &[(&str, Kind)] = &[("grok", Kind::Voice)];

/// The id a legacy blank resolves to: the cloud-STT catalog's default model for
/// that provider. It is read rather than hard-coded so this cannot drift from
/// `grok::resolve_model`, which sends the same default on the wire. A blank
/// therefore lands on Grok Voice Transcribe 2, NOT on the 1 the mode really
/// ran — the question a read path answers is what the request will send next.
fn legacy_blank_id_target(provider: &str, kind: Kind) -> Option<&'static str> {
    if !LEGACY_BLANK_MODEL_PROVIDERS
        .iter()
        .any(|(p, k)| *p == provider && *k == kind)
    {
        return None;
    }
    static CLOUD_STT: std::sync::OnceLock<Option<crate::cloud_stt::CloudSttCatalog>> =
        std::sync::OnceLock::new();
    CLOUD_STT
        .get_or_init(|| crate::cloud_stt::CloudSttCatalog::embedded().ok())
        .as_ref()?
        .providers()
        .iter()
        .find(|entry| entry.stt_provider.as_deref() == Some(provider))
        .and_then(|entry| entry.default_model_id())
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
    fn empty_string_id_resolves_to_groks_default_voice_row() {
        let c = catalog();
        // Grok voice carried id == "" until xAI exposed a `model` parameter on
        // 2026-09-19. Modes saved before that still store the empty id, so it
        // must keep resolving — through the legacy-blank rule, not through an
        // exact hit and not through the wildcard "*". Grok has TWO voice rows
        // now, so "the provider's only row" is no longer an answer.
        let e = c
            .entry("grok", Kind::Voice, "")
            .expect("the empty id resolves to Grok's default voice row");
        assert_eq!(e.id, "grok-voice-transcribe-2.0");
        assert!(e.available_via_hyper_whisper_cloud);
        assert!(e.supports_custom_vocabulary);
    }

    #[test]
    fn the_legacy_blank_target_is_the_cloud_stt_default_not_a_copy_of_it() {
        // The blank must follow the catalog. If someone moves `isDefault` to
        // another Grok model, this test moves with it and no second copy of the
        // default is left behind to rot.
        let stt = crate::cloud_stt::CloudSttCatalog::embedded().expect("cloud-stt catalog parses");
        let expected = stt
            .entry("grokStt")
            .and_then(|e| e.default_model_id())
            .expect("grokStt names a default model");
        assert_eq!(
            catalog()
                .entry("grok", Kind::Voice, "")
                .map(|e| e.id.as_str()),
            Some(expected)
        );
    }

    #[test]
    fn an_empty_id_stays_unresolved_for_a_provider_that_never_shipped_one() {
        let c = catalog();
        // openai always named its model, so a blank there is corrupt data, not
        // a legacy value. The fallback must not guess at it.
        assert!(c.entry("openai", Kind::Voice, "").is_none());
    }

    #[test]
    fn grok_offers_both_transcribe_rows() {
        let c = catalog();
        for id in ["grok-voice-transcribe-1.0", "grok-voice-transcribe-2.0"] {
            let e = c
                .entry("grok", Kind::Voice, id)
                .unwrap_or_else(|| panic!("{id} is catalogued"));
            assert!(e.supports_custom_vocabulary, "{id} takes keyterms");
            assert!(e.available_via_hyper_whisper_cloud, "{id} is cloud-routed");
        }
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

    #[test]
    fn voice_capabilities_default_false_and_meta_retains_all_values() {
        let old = ModelsCatalog::parse(r#"{
            "schemaVersion":1,"models":[{"provider":"old","id":"m","kind":"voice",
            "supportsCustomVocabulary":false,"availableViaHyperWhisperCloud":false,
            "platforms":["macos"],"voiceCapabilities":{"diarization":true}}]
        }"#).unwrap();
        let old_caps = old.entry("old", Kind::Voice, "m").unwrap().voice_capabilities.unwrap();
        assert!(old_caps.diarization);
        assert!(!old_caps.code_switching);
        assert!(!old_caps.word_timestamps);

        let catalog = catalog();
        let meta = catalog.entry("meta", Kind::Voice, "muse-voice-transcribe-1.0")
            .expect("Meta Muse shared-model row");
        let caps = meta.voice_capabilities.expect("Meta Muse voice capabilities");
        assert!(caps.code_switching && caps.endpointing && caps.context_bias);
        assert!(caps.language_bias && caps.turn_timestamps && caps.diarization);
        assert!(!caps.word_timestamps);
    }

    #[test]
    fn meta_voice_capabilities_match_cloud_stt_source_of_truth() {
        let models = catalog();
        let model = models
            .entry("meta", Kind::Voice, "muse-voice-transcribe-1.0")
            .expect("Meta Muse shared-model row");
        let model_caps = model.voice_capabilities.expect("Meta Muse model capabilities");
        assert!(model.available_via_hyper_whisper_cloud);
        assert_eq!(model.platforms, ["macos", "windows", "linux"]);
        let stt = crate::cloud_stt::CloudSttCatalog::embedded().unwrap();
        let stt_caps = stt.entry("metaMuse").expect("Meta Muse STT row").features;

        assert_eq!(model_caps.code_switching, stt_caps.code_switching);
        assert_eq!(model_caps.endpointing, stt_caps.endpointing);
        assert_eq!(model_caps.context_bias, stt_caps.context_bias);
        assert_eq!(model_caps.language_bias, stt_caps.language_bias);
        assert_eq!(model_caps.turn_timestamps, stt_caps.turn_timestamps);
        assert_eq!(model_caps.diarization, stt_caps.diarization);
        assert_eq!(model_caps.word_timestamps, stt_caps.word_timestamps);
        assert_eq!(model.supports_custom_vocabulary,
            stt.entry("metaMuse").unwrap().supports_custom_vocabulary());
    }
}
