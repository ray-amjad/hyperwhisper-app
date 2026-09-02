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
//! - **Vendor grouping** ([`CloudSttCatalog::cloud_tier_vendor_groups`]) folds
//!   the cloud-tier rows by `vendor`. macOS keyed that dictionary
//!   case-SENSITIVELY and Windows case-INSENSITIVELY; this module pins the
//!   lenient (Windows) answer, so two rows spelled `google` and `Google` become
//!   one dropdown row rather than two. The sort is by display name,
//!   case-insensitive and STABLE, so equal names keep catalog order (Windows'
//!   `List.Sort` is an unstable introsort — that was unpinned behavior).
//! - **Picker-language folding** ([`CloudSttCatalog::picker_language_codes`])
//!   ports Windows' `PickerLanguageCodesForId`. It reduces the upstream-native
//!   mixed-format codes (BCP-47 `en-AU`, ISO-639-2/3 `eng`, sentinels like
//!   `multi`) to the two-letter picker code space, drops what has no picker
//!   row, and always includes `"auto"`. Platforms whose picker lists BCP-47
//!   region rows (macOS `STTCapabilities`) must match by primary subtag rather
//!   than by exact code — the fold answers in two-letter codes only.


mod entry;
mod lang;
mod normalize;
mod raw;
mod vendor;

// The test module below reaches this through its `use super::*`. Before the
// split the whole module shared one file-level import; `#[cfg(test)]` keeps it
// out of the non-test build, where nothing in this file uses it.
#[cfg(test)]
use std::collections::BTreeSet;

pub use entry::{
    Access, CloudTier, CustomVocabulary, Features, Languages, SttEntry, SttModel, VocabSupport,
};
pub use normalize::NormalizedCloudProvider;
pub use raw::CloudSttError;
pub use vendor::VendorGroup;

/// Parsed cloud STT catalog. Build once and reuse; lookups scan a small
/// provider list (≈11 entries) in catalog order so the order-sensitive picker
/// helpers are stable.
#[derive(Debug, Clone)]
pub struct CloudSttCatalog {
    version: i64,
    updated: String,
    providers: Vec<SttEntry>,
}

impl CloudSttCatalog {
    /// Parse a cloud-stt-catalog JSON string.
    pub fn parse(json: &str) -> Result<CloudSttCatalog, CloudSttError> {
        let decoded = raw::decode(json)?;
        Ok(CloudSttCatalog {
            version: decoded.version,
            updated: decoded.updated,
            providers: decoded.providers,
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

    /// Cloud-tier providers that HyperWhisper Cloud can also serve **live**, in
    /// catalog order: `access.cloudTierEligible` *and* at least one model with
    /// `streaming: true`.
    ///
    /// This is the eligible set for the HyperWhisper-Cloud live vendor picker.
    /// It keys off the per-model [`SttModel::streaming`] flag, never the
    /// entry-level `features.streaming` vendor hint — see that field's docs for
    /// why the two are not interchangeable.
    pub fn streaming_cloud_tier_entries(&self) -> impl Iterator<Item = &SttEntry> {
        self.cloud_tier_entries()
            .filter(|e| e.models.iter().any(|m| m.streaming()))
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
        self.entry(id).and_then(|e| e.language_codes())
    }
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
        assert_eq!(c.version(), 9);
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

    // --- Golden: vocab supported via a repeated field (grokStt) -------------

    #[test]
    fn grok_stt_vocab_supported_through_keyterm() {
        let c = catalog();
        // grokStt.customVocabulary.supported == true — every request site
        // (backend, Rust core, both streaming strategies) forwards keyterms.
        assert!(c.supports_custom_vocabulary("grokStt"));
        assert_eq!(c.custom_vocabulary_field_name("grokStt"), Some("keyterm"));
        let e = c.entry("grokStt").unwrap();
        assert_eq!(
            e.custom_vocabulary.as_ref().map(|cv| cv.supported),
            Some(VocabSupport::Yes)
        );
    }

    // --- Golden: unverified tri-state ---------------------------------------

    #[test]
    fn azure_vocab_is_true_languages_present() {
        let c = catalog();
        // Was googleChirp3 until catalog v8 replaced it with geminiTranscribe,
        // whose language set is "unverified"; azureMaiTranscribe is the
        // remaining cloud-only entry with a real locale array.
        assert!(c.supports_custom_vocabulary("azureMaiTranscribe"));
        let codes = c.language_codes("azureMaiTranscribe").expect("codes present");
        assert!(codes.contains(&"en".to_string()));
    }

    #[test]
    fn gemini_transcribe_vocab_is_true_languages_unverified() {
        let c = catalog();
        // The structured `custom_vocabulary` field, unlike the `gemini` entry's
        // system-prompt encoding.
        assert!(c.supports_custom_vocabulary("geminiTranscribe"));
        assert_eq!(
            c.custom_vocabulary_field_name("geminiTranscribe"),
            Some("generation_config.transcription_config.custom_vocabulary")
        );
        // Google publishes "85+" but no per-code list for /v1beta/interactions.
        assert_eq!(c.language_codes("geminiTranscribe"), None);
        // It replaced googleChirp3 as the Google cloud tier, and that id must
        // survive only as a migration alias — never as an entry.
        assert!(c.entry("googleChirp3").is_none());
        for alias in ["googleChirp3", "chirp_3", "google-chirp", "chirp", "googlechirp"] {
            assert_eq!(
                c.entry_by_migrate_from(alias).map(|e| e.id.as_str()),
                Some("geminiTranscribe"),
                "{alias} must migrate onto the Google cloud tier, not fall back to Deepgram"
            );
        }
    }

    #[test]
    fn streaming_cloud_tier_entries_are_exactly_the_routed_vendors() {
        let c = catalog();
        let ids: Vec<&str> = c
            .streaming_cloud_tier_entries()
            .map(|e| e.id.as_str())
            .collect();
        assert_eq!(
            ids,
            vec!["deepgramNova3", "geminiTranscribe"],
            "models[].streaming gates the HW-Cloud live picker and the catalog has no \
             `enabled` gate — flipping it on a vendor with no /ws/streaming-* route \
             ships a 404 at dictation time"
        );
        // The entry-level `features.streaming` hint is NOT the same set: it is
        // true for vendors we serve no WS route for.
        assert!(c.entry("soniox").unwrap().features.streaming);
        assert!(!c.entry("soniox").unwrap().models.iter().any(|m| m.streaming()));
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
        assert_eq!(n.accuracy_tier.as_deref(), Some("geminiTranscribe"));
        // Meta was added only as a cloud tier on an unmerged branch. It has no
        // shipped standalone-provider storage to migrate, so the provider field
        // passes through while the tier's migrateFrom aliases remain available
        // to explicit tier and Local API resolution.
        let meta = c.normalize_cloud_provider(Some("meta"));
        assert_eq!(meta.provider.as_deref(), Some("meta"));
        assert_eq!(meta.accuracy_tier, None);
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

    /// A Windows→macOS restore must keep a BYOK user on their own Google key.
    ///
    /// Windows persists `geminiTranscribe`; macOS' `CloudProvider` raw value is
    /// `geminitranscribe` and is parsed case-sensitively with a silent
    /// `?? .hyperwhisper` fallback. Passing the camelCase spelling through
    /// verbatim therefore moved the user onto paid credits with no error. It is
    /// the first `byokEligible` camelCase id that is not also a legacy alias, so
    /// it is the first id the pass-through branch has to get right.
    #[test]
    fn normalize_cloud_provider_folds_case_on_byok_ids() {
        let c = catalog();
        for spelling in [
            "geminiTranscribe",
            "geminitranscribe",
            "GEMINITRANSCRIBE",
            "GeminiTranscribe",
            "gEmInItRaNsCrIbE",
        ] {
            let n = c.normalize_cloud_provider(Some(spelling));
            assert_eq!(
                n.provider.as_deref(),
                Some("geminitranscribe"),
                "`{spelling}` must normalize to the lowercase storage spelling \
                 both platforms parse, NOT fold onto hyperwhisper"
            );
            assert_eq!(
                n.accuracy_tier, None,
                "`{spelling}` is a BYOK provider, not a cloud tier"
            );
        }
    }

    /// The same class of bug for every other id either platform can persist:
    /// the pass-through must always be the lowercase spelling. The two camelCase
    /// ids Windows wrote before catalog v8 still fold onto the cloud tier,
    /// because they are `legacyCloudProviderAliases` — that branch runs first
    /// and is unaffected by the lowercasing.
    #[test]
    fn normalize_cloud_provider_pass_through_is_always_lowercase() {
        let c = catalog();
        for (input, expected) in [
            ("OpenAI", "openai"),
            ("Deepgram", "deepgram"),
            ("AssemblyAI", "assemblyai"),
            ("ElevenLabs", "elevenlabs"),
            ("HyperWhisper", "hyperwhisper"),
            ("Grok", "grok"),
            // Unknown ids are lowercased too, so a future camelCase provider id
            // cannot reintroduce the bug by not being in a table yet.
            ("someFutureProvider", "somefutureprovider"),
        ] {
            let n = c.normalize_cloud_provider(Some(input));
            assert_eq!(n.provider.as_deref(), Some(expected), "input `{input}`");
            assert_eq!(n.accuracy_tier, None, "input `{input}`");
        }
        // Legacy aliases still win over the pass-through, in either spelling.
        for alias in ["googleSpeech", "googlespeech", "microsoftAzureSpeech"] {
            let n = c.normalize_cloud_provider(Some(alias));
            assert_eq!(n.provider.as_deref(), Some("hyperwhisper"), "alias `{alias}`");
            assert!(n.accuracy_tier.is_some(), "alias `{alias}` must carry a tier");
        }
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

    // --- Widened v7 fields ---------------------------------------------------

    #[test]
    fn v7_fields_decode_for_a_fully_populated_row() {
        let c = catalog();
        let e = c.entry("groqWhisper").unwrap();
        assert_eq!(e.vendor_display_name.as_deref(), Some("Groq"));
        assert_eq!(e.vendor_label(), "Groq");
        assert!(e.features.word_timestamps);
        assert!(!e.features.diarization);
        assert!(!e.features.streaming);
        assert_eq!(e.max_file_size_mb, Some(25.0));
        // `maxDurationMinutes: "unverified"` reduces to None.
        assert_eq!(e.max_duration_minutes, None);
        assert!(e.accepted_formats.contains(&"wav".to_string()));
        assert_eq!(e.languages.count, Some(100));
        assert_eq!(e.languages.auto_detect, Some(true));
        assert!(e.languages.code_format.is_some());
        assert_eq!(e.language_codes().map(|c| c.len()), Some(100));
    }

    #[test]
    fn v7_polymorphic_scalars_reduce_to_none_when_unverified() {
        let c = catalog();
        // soniox declares maxFileSizeMb: "unverified".
        assert_eq!(c.entry("soniox").unwrap().max_file_size_mb, None);
        // gemini declares languages.count AND languages.codes "unverified".
        let gemini = c.entry("gemini").unwrap();
        assert_eq!(gemini.languages.count, None);
        assert_eq!(gemini.language_codes(), None);
        // deepgramNova3 declares a real duration cap.
        assert_eq!(c.entry("deepgramNova3").unwrap().max_duration_minutes, Some(10));
        assert_eq!(c.entry("geminiTranscribe").unwrap().max_file_size_mb, Some(14.0));
    }

    #[test]
    fn fractional_max_file_size_mb_does_not_truncate_to_an_integer() {
        // googleChirp3's 9.5 MB inline cap was the embedded catalog's only
        // fractional `maxFileSizeMb` until catalog v8 removed the entry. The
        // decoder branch still has to survive one, so pin it synthetically.
        let json = r#"{
            "version": 8, "updated": "x",
            "providers": [
                {"id":"a","displayName":"Acme","vendor":"acme","maxFileSizeMb":9.5,
                 "access":{"cloudTierEligible":true,"byokEligible":false}}
            ]
        }"#;
        let c = CloudSttCatalog::parse(json).unwrap();
        assert_eq!(c.entry("a").unwrap().max_file_size_mb, Some(9.5));
    }

    #[test]
    fn vendor_label_falls_back_to_display_name_without_vendor_display_name() {
        // An older catalog omits vendorDisplayName; an empty string must fall
        // back too (Windows checked IsNullOrEmpty, Swift's `??` did not).
        let json = r#"{
            "version": 6, "updated": "x",
            "providers": [
                {"id":"a","displayName":"Acme Nova 3","vendor":"acme",
                 "access":{"cloudTierEligible":true,"byokEligible":false}},
                {"id":"b","displayName":"Bravo v2","vendor":"bravo","vendorDisplayName":"",
                 "access":{"cloudTierEligible":true,"byokEligible":false}}
            ]
        }"#;
        let c = CloudSttCatalog::parse(json).unwrap();
        assert_eq!(c.entry("a").unwrap().vendor_label(), "Acme Nova 3");
        assert_eq!(c.entry("b").unwrap().vendor_label(), "Bravo v2");
    }

    // --- Vendor grouping -----------------------------------------------------

    #[test]
    fn vendor_groups_merge_a_company_and_sort_by_display_name() {
        let c = catalog();
        let groups = c.cloud_tier_vendor_groups();
        let names: Vec<&str> = groups.iter().map(|g| g.display_name.as_str()).collect();
        assert_eq!(
            names,
            vec![
                "AssemblyAI", "Deepgram", "ElevenLabs", "Google", "Groq", "Meta",
                "Microsoft", "Mistral", "OpenAI", "Soniox", "xAI",
            ],
            "the Provider dropdown reads alphabetically, case-insensitively \
             (xAI sorts last only under a case-insensitive compare)"
        );
        // Google owns two entries and contributes ONE row spanning both.
        let google = groups.iter().find(|g| g.vendor_key == "google").unwrap();
        let ids: Vec<&str> = google.entries.iter().map(|e| e.id.as_str()).collect();
        assert_eq!(ids, vec!["geminiTranscribe", "gemini"], "catalog order inside a group");
        assert_eq!(
            google.default_entry().map(|e| e.id.as_str()),
            Some("geminiTranscribe"),
            "geminiTranscribe must sit at the departed googleChirp3's array index, \
             or the Google row defaults to the BYOK LLM entry"
        );
        // Its model list spans both entries, each tagged with its owning tier.
        let owners: BTreeSet<&str> = google.models().iter().map(|(id, _)| *id).collect();
        assert_eq!(
            owners.into_iter().collect::<Vec<_>>(),
            vec!["gemini", "geminiTranscribe"]
        );
    }

    #[test]
    fn vendor_group_lookup_is_case_insensitive_on_both_axes() {
        let c = catalog();
        assert_eq!(
            c.vendor_group("GEMINI").map(|g| g.vendor_key),
            Some("google".to_string()),
            "a tier id resolves to the group its company owns"
        );
        assert_eq!(
            c.vendor_group_for_vendor_key("GOOGLE").map(|g| g.display_name),
            Some("Google".to_string())
        );
        assert!(c.vendor_group("noSuchTier").is_none());
        assert!(c.vendor_group_for_vendor_key("").is_none());
    }

    #[test]
    fn vendor_grouping_folds_case_variant_vendor_keys_into_one_row() {
        // macOS keyed this dictionary case-sensitively and would have produced
        // TWO "Acme" rows here; Windows produced one. Rust pins the lenient
        // answer, and the group keeps the FIRST spelling as its key.
        let json = r#"{
            "version": 7, "updated": "x",
            "providers": [
                {"id":"a","displayName":"Acme One","vendor":"acme","vendorDisplayName":"Acme",
                 "access":{"cloudTierEligible":true,"byokEligible":false}},
                {"id":"b","displayName":"Acme Two","vendor":"ACME","vendorDisplayName":"Acme",
                 "access":{"cloudTierEligible":true,"byokEligible":false}},
                {"id":"c","displayName":"Byok Only","vendor":"byok",
                 "access":{"cloudTierEligible":false,"byokEligible":true}}
            ]
        }"#;
        let c = CloudSttCatalog::parse(json).unwrap();
        let groups = c.cloud_tier_vendor_groups();
        assert_eq!(groups.len(), 1, "a BYOK-only row is never a Provider row");
        assert_eq!(groups[0].vendor_key, "acme");
        assert_eq!(
            groups[0].entries.iter().map(|e| e.id.as_str()).collect::<Vec<_>>(),
            vec!["a", "b"]
        );
        // A known but non-cloud-tier id has no group.
        assert!(c.vendor_group("c").is_none());
    }

    // --- Picker-language folding --------------------------------------------

    #[test]
    fn picker_language_codes_collapse_region_variants_and_add_auto() {
        let c = catalog();
        let deepgram = c.picker_language_codes("deepgramNova3").unwrap();
        assert!(deepgram.contains(&"auto".to_string()));
        // 88 raw rows (en-US, en-AU, es-419, …) collapse to their bases.
        assert!(deepgram.contains(&"en".to_string()));
        assert!(deepgram.contains(&"es".to_string()));
        assert!(!deepgram.iter().any(|c| c.contains('-')));
        assert!(deepgram.len() < 88, "region variants must collapse");
        // Sorted, so the vector is a stable golden value.
        let mut sorted = deepgram.clone();
        sorted.sort();
        assert_eq!(deepgram, sorted);
    }

    #[test]
    fn picker_language_codes_map_three_letter_codes_and_fold_aliases() {
        let c = catalog();
        // ElevenLabs declares ISO-639-3: fil→tl, cmn→zh, jav→jw, yue stays.
        let eleven = c.picker_language_codes("elevenLabsScribeV2").unwrap();
        for expected in ["tl", "zh", "jw", "yue", "en"] {
            assert!(
                eleven.contains(&expected.to_string()),
                "{expected} missing from the ElevenLabs picker set"
            );
        }
        // Three-letter codes with no picker row are dropped, not passed through.
        for dropped in ["ceb", "nya", "zul", "fil", "cmn", "jav"] {
            assert!(
                !eleven.contains(&dropped.to_string()),
                "{dropped} must not reach the picker"
            );
        }
        // Azure declares `nb`.
        let azure = c.picker_language_codes("azureMaiTranscribe").unwrap();
        assert!(azure.contains(&"no".to_string()) && !azure.contains(&"nb".to_string()));
        // googleChirp3 was the only entry declaring the deprecated `iw-IL` /
        // `jv-ID` tags; catalog v8 replaced it, so pin that fold synthetically
        // rather than losing the coverage.
        let json = r#"{
            "version": 8, "updated": "x",
            "providers": [
                {"id":"legacyTags","displayName":"Legacy","vendor":"legacy",
                 "access":{"cloudTierEligible":true,"byokEligible":false},
                 "languages":{"count":2,"autoDetect":false,"codes":["iw-IL","jv-ID"]}}
            ]
        }"#;
        let legacy = CloudSttCatalog::parse(json)
            .unwrap()
            .picker_language_codes("legacyTags")
            .unwrap();
        assert!(legacy.contains(&"he".to_string()) && !legacy.contains(&"iw".to_string()));
        assert!(legacy.contains(&"jw".to_string()) && !legacy.contains(&"jv".to_string()));
    }

    #[test]
    fn picker_language_codes_are_none_when_unverified_or_unknown() {
        let c = catalog();
        assert_eq!(c.picker_language_codes("gemini"), None);
        assert_eq!(c.picker_language_codes("noSuchProvider"), None);
    }

    #[test]
    fn picker_language_fold_drops_sentinels_and_blank_codes() {
        let json = r#"{
            "version": 7, "updated": "x",
            "providers": [
                {"id":"p","displayName":"P","vendor":"v",
                 "languages":{"codes":["multi","","  ","en_GB","EN-us","zzzz","eng","ceb"]}}
            ]
        }"#;
        let c = CloudSttCatalog::parse(json).unwrap();
        assert_eq!(
            c.picker_language_codes("p").unwrap(),
            vec!["auto".to_string(), "en".to_string()],
            "`multi`, blanks, a 4-letter sentinel and an unmappable 639-3 code all drop; \
             `en_GB`/`EN-us`/`eng` all fold onto `en`"
        );
    }

    #[test]
    fn deepgram_nova3_picker_set_is_the_pinned_answer() {
        // The audit found macOS and Windows disagreed here: Windows offered
        // gu/th/zh for deepgramNova3 and macOS did not. The catalog is the
        // source of truth and Rust is now the only implementation of the fold,
        // so this pins the answer both platforms must show.
        let c = catalog();
        let codes = c.picker_language_codes("deepgramNova3").unwrap();
        for present in ["gu", "th", "zh"] {
            assert!(
                codes.contains(&present.to_string()),
                "{present} is in the catalog and must reach the picker"
            );
        }
    }

    #[test]
    fn new_feature_fields_default_false_and_meta_sets_verified_values() {
        let old = CloudSttCatalog::parse(r#"{
            "version":8,"providers":[{"id":"old","displayName":"Old","vendor":"old",
            "features":{"wordTimestamps":true}}]
        }"#).unwrap();
        let old_features = old.entry("old").unwrap().features;
        assert!(old_features.word_timestamps);
        assert!(!old_features.code_switching);
        assert!(!old_features.endpointing);
        assert!(!old_features.context_bias);
        assert!(!old_features.language_bias);
        assert!(!old_features.turn_timestamps);

        let catalog = catalog();
        let meta = catalog.entry("metaMuse").expect("Meta Muse catalog row");
        assert_eq!(meta.stt_provider.as_deref(), Some("meta"));
        assert_eq!(meta.default_model_id(), Some("muse-voice-transcribe-1.0"));
        let access = meta.access.expect("Meta Muse access gate");
        assert!(access.cloud_tier_eligible);
        assert!(!access.byok_eligible);
        assert!(meta.features.code_switching);
        assert!(meta.features.endpointing);
        assert!(meta.features.context_bias);
        assert!(meta.features.language_bias);
        assert!(meta.features.turn_timestamps);
        assert!(meta.features.diarization);
        assert!(!meta.features.word_timestamps);
        assert!(!meta.default_model().unwrap().streaming());
    }
}
