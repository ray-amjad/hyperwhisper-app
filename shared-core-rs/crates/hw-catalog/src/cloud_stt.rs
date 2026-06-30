//! WP-D3 — `cloud-stt-catalog.json` parsing + lookup.
//!
//! Port of `app/macos/.../AppClassification/CloudSTTCatalog.swift` and
//! `app/windows/.../Services/AppClassification/CloudSttCatalog.cs`. Plain Rust,
//! sans-I/O: the catalog JSON is embedded at compile time
//! (`super::CLOUD_STT_CATALOG`), so this module only parses an in-memory string
//! and answers lookups.
//!
//! Drives the two-level HyperWhisper Cloud picker (provider tier → model), the
//! custom-vocabulary field-name/visibility affordance, the credits/min caption,
//! cloud-tier-vs-BYOK filtering, and legacy-value migration.
//!
//! Parity notes:
//! - **Case-insensitive id lookup.** Both platforms compare ids
//!   case-insensitively (`caseInsensitiveCompare` on macOS, `OrdinalIgnoreCase`
//!   on Windows). We lowercase both sides for the same behavior.
//! - **`customVocabulary.supported` is tri-state.** The JSON value is either a
//!   bool or the literal string `"unverified"`. macOS models it as an enum
//!   (`yes`/`no`/`unverified`); Windows stores it as a string and treats only
//!   the literal `"true"` as supported. We expose [`VocabSupport`] (tri-state)
//!   AND a `supports_custom_vocabulary(id)` helper matching Windows
//!   (`supported == Yes`), since `unverified` is the conservative "hidden"
//!   default on both.
//! - **Default model.** `isDefault: true`, else the first listed model, else
//!   nil. A model id may legitimately be `""` (Grok's single implicit model);
//!   the backend treats that as "provider default".
//! - **Per-model `creditsPerMinute`** falls back to the tier's
//!   `cloudTier.creditsPerMinute`, then `0.0` — matches Windows
//!   `CreditsPerMinuteForModel`.
//! - This module deliberately does NOT port Windows' ISO-639 picker-code
//!   normalization (`PickerLanguageCodesForId`): that mapping table is a
//!   Windows-only convenience that depends on `LanguageInfo.AllLanguages`, a
//!   platform UI list. We expose the raw upstream `languages.codes` instead;
//!   Wave 2 platforms keep owning the picker fold. (Divergence: macOS exposes
//!   no such helper either, so raw codes is the common denominator.)

use std::collections::BTreeMap;

use serde::Deserialize;

/// Tri-state custom-vocabulary support, mirroring macOS `CustomVocabulary.Support`.
/// The catalog stores either a bool or the literal string `"unverified"`. Any
/// unrecognized string falls back to [`VocabSupport::No`] (a single typo must
/// not brick the catalog), matching both platforms' lenient decoders.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum VocabSupport {
    Yes,
    No,
    Unverified,
}

impl VocabSupport {
    fn from_value(v: &serde_json::Value) -> VocabSupport {
        match v {
            serde_json::Value::Bool(true) => VocabSupport::Yes,
            serde_json::Value::Bool(false) => VocabSupport::No,
            serde_json::Value::String(s) if s == "unverified" => VocabSupport::Unverified,
            // Any other string (a catalog typo) → conservative No, matching the
            // macOS `default: self = .no` and Windows `BoolOrStringConverter`.
            _ => VocabSupport::No,
        }
    }
}

/// Custom-vocabulary affordance for a provider. `field_name` is the upstream API
/// parameter the vocabulary list is sent through (e.g. `keyterm`, `prompt`).
#[derive(Debug, Clone, PartialEq)]
pub struct CustomVocabulary {
    pub supported: VocabSupport,
    pub field_name: Option<String>,
    pub caveats: Option<String>,
}

/// Cloud-tier display metadata: the accuracy bucket (`"medium"` / `"high"` /
/// `"highest"`) and the display-only credits-per-minute.
#[derive(Debug, Clone, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CloudTier {
    pub accuracy: String,
    pub credits_per_minute: f64,
}

/// Access flags: whether the provider appears under the HyperWhisper Cloud
/// accuracy dropdown (`cloud_tier_eligible`) and/or the BYOK list
/// (`byok_eligible`). Both can be true.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Access {
    pub cloud_tier_eligible: bool,
    pub byok_eligible: bool,
}

/// A single routable model within a provider. `id` is the `X-STT-Model` header
/// value (may be `""` for single-model providers like Grok).
#[derive(Debug, Clone, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SttModel {
    pub id: String,
    pub display_name: String,
    #[serde(default)]
    pub credits_per_minute: Option<f64>,
    #[serde(default)]
    pub is_default: Option<bool>,
    #[serde(default)]
    pub preview_status: Option<bool>,
    #[serde(default)]
    pub supports_custom_vocabulary: Option<bool>,
}

impl SttModel {
    /// Whether this specific model supports custom vocabulary, defaulting to
    /// false on a missing flag — matches Windows `ModelSupportsCustomVocabulary`.
    pub fn supports_custom_vocabulary(&self) -> bool {
        self.supports_custom_vocabulary.unwrap_or(false)
    }
}

/// One cloud STT provider row. Mirrors macOS `CloudSTTCatalog.Entry` /
/// Windows `CloudSttCatalogEntry`.
#[derive(Debug, Clone, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SttEntry {
    pub id: String,
    pub display_name: String,
    #[serde(default)]
    pub display_model: Option<String>,
    pub vendor: String,
    /// The `X-STT-Provider` header value the backend routes on (catalog v6+).
    #[serde(default)]
    pub stt_provider: Option<String>,
    #[serde(default)]
    pub access: Option<Access>,
    #[serde(default)]
    pub models: Vec<SttModel>,
    #[serde(default)]
    pub cloud_tier: Option<CloudTier>,
    /// Parsed separately (the `supported` field is bool-or-string). Set during
    /// [`CloudSttCatalog::parse`]; `serde(skip)` keeps the derived `Deserialize`
    /// from choking on the polymorphic field.
    #[serde(skip)]
    pub custom_vocabulary: Option<CustomVocabulary>,
    /// Raw upstream language codes (in mixed formats — ISO-639-1, BCP-47,
    /// ISO-639-3). `None` when the catalog leaves the set `"unverified"`.
    /// Populated during parse from the polymorphic `languages.codes` field.
    #[serde(skip)]
    pub language_codes: Option<Vec<String>>,
    #[serde(default)]
    pub preview_status: Option<bool>,
    #[serde(default)]
    pub migrate_from: Option<Vec<String>>,
    #[serde(default)]
    pub legacy_cloud_provider_aliases: Option<Vec<String>>,
}

impl SttEntry {
    /// Whether the provider supports custom vocabulary through OUR backend.
    /// Matches Windows `SupportsCustomVocabulary` (`supported == "true"`): only
    /// an explicit `Yes` counts — `Unverified` and `No` are both false.
    pub fn supports_custom_vocabulary(&self) -> bool {
        matches!(
            self.custom_vocabulary.as_ref().map(|cv| cv.supported),
            Some(VocabSupport::Yes)
        )
    }

    /// Display-only credits/min from the cloud-tier block, or `0.0` when absent.
    /// Matches Windows `CreditsPerMinute`.
    pub fn credits_per_minute(&self) -> f64 {
        self.cloud_tier.as_ref().map(|t| t.credits_per_minute).unwrap_or(0.0)
    }

    /// The default model — `isDefault: true`, else the first listed, else `None`.
    pub fn default_model(&self) -> Option<&SttModel> {
        self.models
            .iter()
            .find(|m| m.is_default == Some(true))
            .or_else(|| self.models.first())
    }

    /// The default model id (`X-STT-Model` value), or `None` when the provider
    /// lists no models. Note: the id may legitimately be `""` (Grok).
    pub fn default_model_id(&self) -> Option<&str> {
        self.default_model().map(|m| m.id.as_str())
    }

    /// Look up a model by id (case-insensitive), matching the catalog convention.
    pub fn model(&self, model_id: &str) -> Option<&SttModel> {
        self.models
            .iter()
            .find(|m| m.id.eq_ignore_ascii_case(model_id))
    }

    /// Credits/min for a specific model, falling back to the tier cost, then
    /// `0.0` — matches Windows `CreditsPerMinuteForModel`.
    pub fn credits_per_minute_for_model(&self, model_id: &str) -> f64 {
        self.model(model_id)
            .and_then(|m| m.credits_per_minute)
            .unwrap_or_else(|| self.credits_per_minute())
    }
}

/// Error parsing the cloud-stt catalog JSON.
#[derive(thiserror::Error, Debug)]
pub enum CloudSttError {
    #[error("cloud-stt-catalog.json failed to decode: {0}")]
    Decode(#[from] serde_json::Error),
}

/// Parsed cloud STT catalog. Build once and reuse; lookups scan a small
/// provider list (≈11 entries) in catalog order so the order-sensitive picker
/// helpers are stable.
#[derive(Debug, Clone)]
pub struct CloudSttCatalog {
    version: i64,
    updated: String,
    providers: Vec<SttEntry>,
}

/// Raw deserialization shape. The polymorphic `customVocabulary.supported` and
/// `languages.codes` fields are captured as `serde_json::Value` and reduced in
/// [`CloudSttCatalog::parse`].
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RawFile {
    #[serde(default)]
    version: i64,
    #[serde(default)]
    updated: String,
    #[serde(default)]
    providers: Vec<RawEntry>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RawEntry {
    id: String,
    #[serde(default)]
    display_name: String,
    #[serde(default)]
    display_model: Option<String>,
    #[serde(default)]
    vendor: String,
    #[serde(default)]
    stt_provider: Option<String>,
    #[serde(default)]
    access: Option<Access>,
    #[serde(default)]
    models: Vec<SttModel>,
    #[serde(default)]
    cloud_tier: Option<CloudTier>,
    #[serde(default)]
    custom_vocabulary: Option<RawCustomVocabulary>,
    #[serde(default)]
    languages: Option<RawLanguages>,
    #[serde(default)]
    preview_status: Option<bool>,
    #[serde(default)]
    migrate_from: Option<Vec<String>>,
    #[serde(default)]
    legacy_cloud_provider_aliases: Option<Vec<String>>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RawCustomVocabulary {
    #[serde(default)]
    supported: serde_json::Value,
    #[serde(default)]
    field_name: Option<String>,
    #[serde(default)]
    caveats: Option<String>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RawLanguages {
    #[serde(default)]
    codes: serde_json::Value,
}

impl CloudSttCatalog {
    /// Parse a cloud-stt-catalog JSON string.
    pub fn parse(json: &str) -> Result<CloudSttCatalog, CloudSttError> {
        let raw: RawFile = serde_json::from_str(json)?;
        let providers = raw
            .providers
            .into_iter()
            .map(|r| {
                let custom_vocabulary = r.custom_vocabulary.map(|cv| CustomVocabulary {
                    supported: VocabSupport::from_value(&cv.supported),
                    field_name: cv.field_name,
                    caveats: cv.caveats,
                });
                // `languages.codes` is either a string array or the literal
                // "unverified" — anything non-array reduces to None (matching
                // Swift's ArrayOrString and the Windows StringArrayOrStringConverter).
                let language_codes = r.languages.and_then(|l| match l.codes {
                    serde_json::Value::Array(arr) => Some(
                        arr.into_iter()
                            .filter_map(|v| v.as_str().map(|s| s.to_string()))
                            .collect(),
                    ),
                    _ => None,
                });
                SttEntry {
                    id: r.id,
                    display_name: r.display_name,
                    display_model: r.display_model,
                    vendor: r.vendor,
                    stt_provider: r.stt_provider,
                    access: r.access,
                    models: r.models,
                    cloud_tier: r.cloud_tier,
                    custom_vocabulary,
                    language_codes,
                    preview_status: r.preview_status,
                    migrate_from: r.migrate_from,
                    legacy_cloud_provider_aliases: r.legacy_cloud_provider_aliases,
                }
            })
            .collect();
        Ok(CloudSttCatalog {
            version: raw.version,
            updated: raw.updated,
            providers,
        })
    }

    /// Parse the compile-time-embedded `cloud-stt-catalog.json`.
    pub fn embedded() -> Result<CloudSttCatalog, CloudSttError> {
        CloudSttCatalog::parse(super::CLOUD_STT_CATALOG)
    }

    pub fn version(&self) -> i64 {
        self.version
    }

    pub fn updated(&self) -> &str {
        &self.updated
    }

    /// All provider entries, in catalog order.
    pub fn providers(&self) -> &[SttEntry] {
        &self.providers
    }

    /// Look up a provider by `id` (case-insensitive). Matches macOS
    /// `entry(byId:)` / Windows `GetById`.
    pub fn entry(&self, id: &str) -> Option<&SttEntry> {
        self.providers
            .iter()
            .find(|e| e.id.eq_ignore_ascii_case(id))
    }

    /// Look up a provider whose `migrateFrom` list contains `alias`
    /// (case-insensitive, trimmed). Drives legacy `cloudAccuracyTier` resolution.
    pub fn entry_by_migrate_from(&self, alias: &str) -> Option<&SttEntry> {
        let needle = alias.trim();
        if needle.is_empty() {
            return None;
        }
        self.providers.iter().find(|e| {
            e.migrate_from
                .as_ref()
                .is_some_and(|aliases| aliases.iter().any(|a| a.eq_ignore_ascii_case(needle)))
        })
    }

    /// Look up a provider whose `legacyCloudProviderAliases` list contains
    /// `alias` (case-insensitive, trimmed). Drives `normalize_cloud_provider`
    /// ONLY — kept separate from `migrate_from` so BYOK provider names never get
    /// misinterpreted as cloud-tier migrations.
    pub fn entry_by_legacy_cloud_provider_alias(&self, alias: &str) -> Option<&SttEntry> {
        let needle = alias.trim();
        if needle.is_empty() {
            return None;
        }
        self.providers.iter().find(|e| {
            e.legacy_cloud_provider_aliases
                .as_ref()
                .is_some_and(|aliases| aliases.iter().any(|a| a.eq_ignore_ascii_case(needle)))
        })
    }

    /// Providers surfaced under the HyperWhisper Cloud accuracy dropdown
    /// (`access.cloudTierEligible == true`), in catalog order.
    pub fn cloud_tier_entries(&self) -> impl Iterator<Item = &SttEntry> {
        self.providers
            .iter()
            .filter(|e| e.access.map(|a| a.cloud_tier_eligible).unwrap_or(false))
    }

    /// The `X-STT-Provider` header value for a provider id, or `None`.
    pub fn stt_provider(&self, id: &str) -> Option<&str> {
        self.entry(id).and_then(|e| e.stt_provider.as_deref())
    }

    /// Whether the provider supports custom vocabulary through our backend.
    /// Matches Windows `SupportsCustomVocabulary` (`supported == "true"`).
    /// Defaults to false on an unknown id.
    pub fn supports_custom_vocabulary(&self, id: &str) -> bool {
        self.entry(id).map(|e| e.supports_custom_vocabulary()).unwrap_or(false)
    }

    /// The custom-vocabulary field name for a provider (the upstream API
    /// parameter the vocab list is sent through), or `None`.
    pub fn custom_vocabulary_field_name(&self, id: &str) -> Option<&str> {
        self.entry(id)
            .and_then(|e| e.custom_vocabulary.as_ref())
            .and_then(|cv| cv.field_name.as_deref())
    }

    /// Display-only credits/min for the provider's cloud tier, or `0.0`.
    pub fn credits_per_minute(&self, id: &str) -> f64 {
        self.entry(id).map(|e| e.credits_per_minute()).unwrap_or(0.0)
    }

    /// Credits/min for a specific model within a provider, falling back to the
    /// tier cost, then `0.0`.
    pub fn credits_per_minute_for_model(&self, id: &str, model_id: &str) -> f64 {
        self.entry(id)
            .map(|e| e.credits_per_minute_for_model(model_id))
            .unwrap_or(0.0)
    }

    /// Models for a provider, in catalog order; empty when unknown.
    pub fn models(&self, id: &str) -> &[SttModel] {
        self.entry(id).map(|e| e.models.as_slice()).unwrap_or(&[])
    }

    /// The default model id for a provider, or `None`.
    pub fn default_model_id(&self, id: &str) -> Option<&str> {
        self.entry(id).and_then(|e| e.default_model_id())
    }

    /// Look up a single model by (provider id, model id), case-insensitive.
    pub fn model(&self, id: &str, model_id: &str) -> Option<&SttModel> {
        self.entry(id).and_then(|e| e.model(model_id))
    }

    /// Raw upstream language codes for a provider, or `None` when unspecified
    /// (`"unverified"`) or unknown.
    pub fn language_codes(&self, id: &str) -> Option<&[String]> {
        self.entry(id).and_then(|e| e.language_codes.as_deref())
    }

    /// Normalize a persisted `cloudProvider` storage value. If `value` is a
    /// legacy standalone-provider alias for a provider now surfaced as a cloud
    /// tier (e.g. `microsoftazurespeech` → `azureMaiTranscribe`), returns
    /// `(Some("hyperwhisper"), Some(<tier id>))`. Otherwise returns the input
    /// unchanged with `accuracy_tier == None`. Critically, BYOK provider names
    /// (`"deepgram"`, `"groq"`) pass through untouched even though they appear in
    /// `migrateFrom`. Mirrors macOS/Windows `normalizeCloudProvider`.
    pub fn normalize_cloud_provider(&self, value: Option<&str>) -> NormalizedCloudProvider {
        let Some(value) = value.filter(|v| !v.is_empty()) else {
            return NormalizedCloudProvider {
                provider: value.map(|s| s.to_string()),
                accuracy_tier: None,
            };
        };
        if let Some(entry) = self.entry_by_legacy_cloud_provider_alias(value) {
            return NormalizedCloudProvider {
                provider: Some("hyperwhisper".to_string()),
                accuracy_tier: Some(entry.id.clone()),
            };
        }
        NormalizedCloudProvider {
            provider: Some(value.to_string()),
            accuracy_tier: None,
        }
    }
}

/// Result of [`CloudSttCatalog::normalize_cloud_provider`]. `accuracy_tier` is
/// `Some` only when `provider` was folded onto `"hyperwhisper"`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct NormalizedCloudProvider {
    pub provider: Option<String>,
    pub accuracy_tier: Option<String>,
}

/// Index of provider ids → catalog position. Exposed for callers that need a
/// stable ordering map; not used internally (lookups scan the small list).
#[allow(dead_code)]
pub(crate) fn id_index(catalog: &CloudSttCatalog) -> BTreeMap<String, usize> {
    catalog
        .providers
        .iter()
        .enumerate()
        .map(|(i, e)| (e.id.clone(), i))
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn catalog() -> CloudSttCatalog {
        CloudSttCatalog::embedded().expect("embedded cloud-stt-catalog.json must parse")
    }

    #[test]
    fn embedded_catalog_parses() {
        let c = catalog();
        assert_eq!(c.version(), 6);
        assert!(c.providers().len() >= 10);
    }

    // --- Golden: known provider caps + vocab field --------------------------

    #[test]
    fn deepgram_nova3_caps_and_vocab_field() {
        let c = catalog();
        let e = c.entry("deepgramNova3").expect("deepgramNova3 exists");
        assert_eq!(e.vendor, "deepgram");
        assert_eq!(e.stt_provider.as_deref(), Some("deepgram"));
        assert_eq!(c.stt_provider("deepgramNova3"), Some("deepgram"));
        // Cloud tier + BYOK both eligible.
        let access = e.access.expect("access present");
        assert!(access.cloud_tier_eligible);
        assert!(access.byok_eligible);
        // Custom vocab supported via the `keyterm` field.
        assert!(c.supports_custom_vocabulary("deepgramNova3"));
        assert_eq!(c.custom_vocabulary_field_name("deepgramNova3"), Some("keyterm"));
        // Display credits/min from the cloud tier.
        assert_eq!(c.credits_per_minute("deepgramNova3"), 5.5);
        // Default model.
        assert_eq!(c.default_model_id("deepgramNova3"), Some("nova-3-general"));
        let m = c.model("deepgramNova3", "nova-3-general").unwrap();
        assert!(m.supports_custom_vocabulary());
        assert_eq!(c.credits_per_minute_for_model("deepgramNova3", "nova-3-general"), 5.5);
    }

    #[test]
    fn case_insensitive_id_lookup() {
        let c = catalog();
        assert!(c.entry("DEEPGRAMNOVA3").is_some());
        assert!(c.entry("deepgramnova3").is_some());
        assert_eq!(c.stt_provider("GroqWhisper"), Some("groq"));
    }

    #[test]
    fn vocab_field_name_for_groq_is_prompt() {
        let c = catalog();
        assert_eq!(c.custom_vocabulary_field_name("groqWhisper"), Some("prompt"));
        assert!(c.supports_custom_vocabulary("groqWhisper"));
    }

    // --- Golden: vocab NOT supported (grokStt — backend doesn't forward) ----

    #[test]
    fn grok_stt_vocab_unsupported_despite_field_name() {
        let c = catalog();
        // grokStt.customVocabulary.supported == false (backend doesn't forward).
        assert!(!c.supports_custom_vocabulary("grokStt"));
        // The field name is still present in the catalog ("keyterm").
        assert_eq!(c.custom_vocabulary_field_name("grokStt"), Some("keyterm"));
        let e = c.entry("grokStt").unwrap();
        assert_eq!(
            e.custom_vocabulary.as_ref().map(|cv| cv.supported),
            Some(VocabSupport::No)
        );
    }

    // --- Golden: unverified tri-state (googleChirp3) ------------------------

    #[test]
    fn google_chirp_vocab_is_false_languages_present() {
        let c = catalog();
        // googleChirp3 customVocabulary.supported == false.
        assert!(!c.supports_custom_vocabulary("googleChirp3"));
        // languages.codes IS a real array (count 111).
        let codes = c.language_codes("googleChirp3").expect("codes present");
        assert!(codes.contains(&"en-US".to_string()));
    }

    #[test]
    fn gemini_languages_unverified_yields_none() {
        let c = catalog();
        // gemini's languages.codes == "unverified" → None.
        assert_eq!(c.language_codes("gemini"), None);
        // But customVocabulary IS supported (systemInstruction).
        assert!(c.supports_custom_vocabulary("gemini"));
        assert_eq!(c.custom_vocabulary_field_name("gemini"), Some("systemInstruction"));
    }

    // --- Golden: default model fallback + empty-id model --------------------

    #[test]
    fn grok_stt_default_model_id_is_empty_string() {
        let c = catalog();
        // grokStt has a single model with id "" — default resolves to it.
        assert_eq!(c.default_model_id("grokStt"), Some(""));
    }

    #[test]
    fn soniox_default_is_flagged_not_first() {
        let c = catalog();
        // soniox lists v4 first but v5 is isDefault — default must be v5.
        assert_eq!(c.default_model_id("soniox"), Some("stt-async-v5"));
    }

    // --- Golden: cloud-tier filtering ---------------------------------------

    #[test]
    fn cloud_tier_entries_includes_cloud_only_providers() {
        let c = catalog();
        let ids: Vec<&str> = c.cloud_tier_entries().map(|e| e.id.as_str()).collect();
        assert!(ids.contains(&"deepgramNova3"));
        // azureMaiTranscribe is cloud-only (byok false) but still cloud-tier.
        assert!(ids.contains(&"azureMaiTranscribe"));
        let azure = c.entry("azureMaiTranscribe").unwrap();
        assert!(azure.access.unwrap().cloud_tier_eligible);
        assert!(!azure.access.unwrap().byok_eligible);
    }

    // --- Golden: migration aliases ------------------------------------------

    #[test]
    fn migrate_from_resolves_legacy_tier_strings() {
        let c = catalog();
        // "high" is a legacy tier bucket that migrates to deepgramNova3.
        assert_eq!(c.entry_by_migrate_from("high").map(|e| e.id.as_str()), Some("deepgramNova3"));
        // "medium" → groqWhisper.
        assert_eq!(c.entry_by_migrate_from("medium").map(|e| e.id.as_str()), Some("groqWhisper"));
        // Case-insensitive + trimmed.
        assert_eq!(c.entry_by_migrate_from("  HIGH  ").map(|e| e.id.as_str()), Some("deepgramNova3"));
    }

    #[test]
    fn legacy_cloud_provider_alias_separate_from_migrate_from() {
        let c = catalog();
        // "microsoftazurespeech" is a legacyCloudProviderAlias for azureMaiTranscribe.
        assert_eq!(
            c.entry_by_legacy_cloud_provider_alias("microsoftazurespeech")
                .map(|e| e.id.as_str()),
            Some("azureMaiTranscribe")
        );
        // But a BYOK provider name like "deepgram" is NOT a legacy cloud alias.
        assert!(c.entry_by_legacy_cloud_provider_alias("deepgram").is_none());
    }

    #[test]
    fn normalize_cloud_provider_folds_legacy_only() {
        let c = catalog();
        // Legacy standalone provider folds onto hyperwhisper + tier.
        let n = c.normalize_cloud_provider(Some("googlespeech"));
        assert_eq!(n.provider.as_deref(), Some("hyperwhisper"));
        assert_eq!(n.accuracy_tier.as_deref(), Some("googleChirp3"));
        // BYOK provider name passes through untouched (CRITICAL — must not
        // silently disable a user's BYOK setup).
        let byok = c.normalize_cloud_provider(Some("deepgram"));
        assert_eq!(byok.provider.as_deref(), Some("deepgram"));
        assert_eq!(byok.accuracy_tier, None);
        // Empty / None pass through.
        assert_eq!(
            c.normalize_cloud_provider(Some("")),
            NormalizedCloudProvider { provider: Some("".into()), accuracy_tier: None }
        );
        assert_eq!(
            c.normalize_cloud_provider(None),
            NormalizedCloudProvider { provider: None, accuracy_tier: None }
        );
    }

    // --- Misses --------------------------------------------------------------

    #[test]
    fn unknown_id_safe_defaults() {
        let c = catalog();
        assert!(c.entry("nope").is_none());
        assert!(!c.supports_custom_vocabulary("nope"));
        assert_eq!(c.custom_vocabulary_field_name("nope"), None);
        assert_eq!(c.credits_per_minute("nope"), 0.0);
        assert_eq!(c.default_model_id("nope"), None);
        assert_eq!(c.stt_provider("nope"), None);
        assert!(c.models("nope").is_empty());
        assert_eq!(c.language_codes("nope"), None);
    }

    // --- Tri-state decoding edge cases --------------------------------------

    #[test]
    fn vocab_support_typo_defaults_to_no() {
        let json = r#"{
            "version": 1, "updated": "x",
            "providers": [
                {"id":"p","displayName":"P","vendor":"v",
                 "customVocabulary":{"supported":"garbage","fieldName":"f"}}
            ]
        }"#;
        let c = CloudSttCatalog::parse(json).unwrap();
        assert!(!c.supports_custom_vocabulary("p"));
        let e = c.entry("p").unwrap();
        assert_eq!(
            e.custom_vocabulary.as_ref().map(|cv| cv.supported),
            Some(VocabSupport::No)
        );
    }

    #[test]
    fn malformed_json_is_error_not_panic() {
        assert!(CloudSttCatalog::parse("{ not json").is_err());
    }
}
