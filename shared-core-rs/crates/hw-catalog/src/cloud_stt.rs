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

use std::collections::BTreeSet;

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

/// Per-provider capability flags (catalog v7 `features`). All three default to
/// `false` on an older catalog that omits the block, which is the conservative
/// answer for each: no word timestamps, no diarization, no streaming.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Features {
    #[serde(default)]
    pub word_timestamps: bool,
    #[serde(default)]
    pub diarization: bool,
    #[serde(default)]
    pub streaming: bool,
}

/// The provider's `languages` block (catalog v7). Every field is polymorphic in
/// the same way: the real value, or the documented `"unverified"` literal, which
/// reduces to `None` so callers fall back to conservative defaults rather than
/// to an empty-but-typed value.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Languages {
    /// Upstream's own count of supported languages. May disagree with
    /// `codes.len()` (Deepgram declares 64 and enumerates 88 locale rows).
    pub count: Option<i64>,
    pub auto_detect: Option<bool>,
    /// Human-readable description of the code space `codes` is written in
    /// (e.g. `"ISO-639-1"`, `"BCP-47"`).
    pub code_format: Option<String>,
    pub notes: Option<String>,
    /// Raw upstream language codes, in whatever mixed format the upstream
    /// declares. `None` when the catalog leaves the set `"unverified"`.
    pub codes: Option<Vec<String>>,
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
    /// Whether HyperWhisper Cloud serves a **live WebSocket route** for this
    /// specific model.
    ///
    /// Deliberately narrower than the entry-level `features.streaming` hint,
    /// which merely records that the *vendor* offers streaming — it is `true`
    /// for six vendors we have no backend WS route for, so using it to build a
    /// picker would ship a 404 at dictation time. This flag means "we route it".
    ///
    /// `#[serde(default)]`, so today's catalog file (which has no `streaming`
    /// key on any model) still decodes unchanged and every model reads `false`.
    #[serde(default)]
    pub streaming: Option<bool>,
}

impl SttModel {
    /// Whether this specific model supports custom vocabulary, defaulting to
    /// false on a missing flag — matches Windows `ModelSupportsCustomVocabulary`.
    pub fn supports_custom_vocabulary(&self) -> bool {
        self.supports_custom_vocabulary.unwrap_or(false)
    }

    /// Whether HyperWhisper Cloud serves a live WebSocket route for this model.
    /// Missing flag ⇒ false.
    pub fn streaming(&self) -> bool {
        self.streaming.unwrap_or(false)
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
    /// Plain company name shown in the Provider dropdown ("Deepgram", "xAI").
    /// Catalog v7+ — carries no model family or version, unlike `display_name`
    /// ("Deepgram Nova 3"). `None` on an older catalog; callers fall back to
    /// `display_name` (see [`SttEntry::vendor_label`]).
    #[serde(default)]
    pub vendor_display_name: Option<String>,
    /// The `X-STT-Provider` header value the backend routes on (catalog v6+).
    #[serde(default)]
    pub stt_provider: Option<String>,
    #[serde(default)]
    pub access: Option<Access>,
    #[serde(default)]
    pub models: Vec<SttModel>,
    #[serde(default)]
    pub cloud_tier: Option<CloudTier>,
    /// Capability flags (catalog v7). Defaults to all-false when absent.
    #[serde(default)]
    pub features: Features,
    /// Upload size ceiling in MB, or `None` when `"unverified"`. Fractional —
    /// Google Chirp's inline limit is 9.5 MB.
    #[serde(default)]
    pub max_file_size_mb: Option<f64>,
    /// Per-request audio-duration ceiling in minutes, or `None` when absent or
    /// `"unverified"`.
    #[serde(default)]
    pub max_duration_minutes: Option<i64>,
    /// Container/codec extensions the upstream accepts, in catalog order.
    #[serde(default)]
    pub accepted_formats: Vec<String>,
    /// Parsed separately (the `supported` field is bool-or-string). Set during
    /// [`CloudSttCatalog::parse`]; `serde(skip)` keeps the derived `Deserialize`
    /// from choking on the polymorphic field.
    #[serde(skip)]
    pub custom_vocabulary: Option<CustomVocabulary>,
    /// The provider's `languages` block. Every field inside is polymorphic
    /// (real value or the `"unverified"` literal), so this too is reduced during
    /// [`CloudSttCatalog::parse`] rather than by the derived `Deserialize`.
    #[serde(skip)]
    pub languages: Languages,
    #[serde(default)]
    pub preview_status: Option<bool>,
    #[serde(default)]
    pub migrate_from: Option<Vec<String>>,
    #[serde(default)]
    pub legacy_cloud_provider_aliases: Option<Vec<String>>,
}

impl SttEntry {
    /// The plain company name for the Provider dropdown — `vendorDisplayName`,
    /// falling back to `displayName` on an older catalog. Matches macOS
    /// `first.vendorDisplayName ?? first.displayName` and Windows'
    /// `IsNullOrEmpty(VendorDisplayName) ? DisplayName : VendorDisplayName`
    /// (an empty string falls back too, which the Swift `??` did not do).
    pub fn vendor_label(&self) -> &str {
        match self.vendor_display_name.as_deref() {
            Some(v) if !v.is_empty() => v,
            _ => &self.display_name,
        }
    }

    /// Raw upstream language codes, or `None` when the catalog leaves the set
    /// `"unverified"`.
    pub fn language_codes(&self) -> Option<&[String]> {
        self.languages.codes.as_deref()
    }

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
    vendor_display_name: Option<String>,
    #[serde(default)]
    stt_provider: Option<String>,
    #[serde(default)]
    access: Option<Access>,
    #[serde(default)]
    models: Vec<SttModel>,
    #[serde(default)]
    cloud_tier: Option<CloudTier>,
    #[serde(default)]
    features: Features,
    /// `number` or the `"unverified"` literal.
    #[serde(default)]
    max_file_size_mb: serde_json::Value,
    /// `number` or the `"unverified"` literal.
    #[serde(default)]
    max_duration_minutes: serde_json::Value,
    #[serde(default)]
    accepted_formats: Vec<String>,
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
    count: serde_json::Value,
    #[serde(default)]
    auto_detect: serde_json::Value,
    #[serde(default)]
    code_format: Option<String>,
    #[serde(default)]
    notes: Option<String>,
    #[serde(default)]
    codes: serde_json::Value,
}

/// Reduce a `number`-or-`"unverified"` field to `Some(n)`. Any non-number
/// (`"unverified"`, a typo, or absent) is `None` — the same fall-through both
/// platforms' converters use, so one bad row never fails the whole decode.
fn number_or_none(v: &serde_json::Value) -> Option<f64> {
    v.as_f64()
}

/// Reduce a `bool`-or-`"unverified"` field. Non-bool → `None`.
fn bool_or_none(v: &serde_json::Value) -> Option<bool> {
    v.as_bool()
}

/// Reduce a `string[]`-or-`"unverified"` field. Non-array → `None`; non-string
/// elements inside an array are dropped.
fn string_array_or_none(v: serde_json::Value) -> Option<Vec<String>> {
    match v {
        serde_json::Value::Array(arr) => Some(
            arr.into_iter()
                .filter_map(|v| match v {
                    serde_json::Value::String(s) => Some(s),
                    _ => None,
                })
                .collect(),
        ),
        _ => None,
    }
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
                // Every field of `languages` is "the real value or the literal
                // "unverified"" — anything of the wrong JSON type reduces to
                // None, matching Swift's ArrayOrString/IntOrString/BoolOrString
                // and the Windows *OrStringConverter family.
                let languages = r
                    .languages
                    .map(|l| Languages {
                        count: number_or_none(&l.count).map(|n| n as i64),
                        auto_detect: bool_or_none(&l.auto_detect),
                        code_format: l.code_format,
                        notes: l.notes,
                        codes: string_array_or_none(l.codes),
                    })
                    .unwrap_or_default();
                SttEntry {
                    id: r.id,
                    display_name: r.display_name,
                    display_model: r.display_model,
                    vendor: r.vendor,
                    vendor_display_name: r.vendor_display_name,
                    stt_provider: r.stt_provider,
                    access: r.access,
                    models: r.models,
                    cloud_tier: r.cloud_tier,
                    features: r.features,
                    max_file_size_mb: number_or_none(&r.max_file_size_mb),
                    max_duration_minutes: number_or_none(&r.max_duration_minutes)
                        .map(|n| n as i64),
                    accepted_formats: r.accepted_formats,
                    custom_vocabulary,
                    languages,
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

    // -- Vendor grouping (Provider dropdown, catalog v7+) --------------------

    /// The Provider dropdown's rows: cloud-tier entries folded by `vendor` and
    /// sorted by company name, so the list reads alphabetically and each company
    /// appears exactly once. Google owns two entries (Chirp + Gemini) and so
    /// contributes one row whose model list spans both.
    ///
    /// The fold is case-INSENSITIVE on `vendor` (the Windows answer; macOS keyed
    /// its dictionary case-sensitively, which would have split a `google` row
    /// from a `Google` one). The group's `vendor_key` keeps the FIRST spelling
    /// seen in catalog order. The sort is stable, so two companies with the same
    /// display name keep catalog order rather than an arbitrary one.
    pub fn cloud_tier_vendor_groups(&self) -> Vec<VendorGroup> {
        let mut groups: Vec<VendorGroup> = Vec::new();
        for entry in self.cloud_tier_entries() {
            match groups
                .iter_mut()
                .find(|g| g.vendor_key.eq_ignore_ascii_case(&entry.vendor))
            {
                Some(group) => group.entries.push(entry.clone()),
                None => groups.push(VendorGroup {
                    vendor_key: entry.vendor.clone(),
                    display_name: entry.vendor_label().to_string(),
                    entries: vec![entry.clone()],
                }),
            }
        }
        groups.sort_by(|a, b| {
            a.display_name
                .to_lowercase()
                .cmp(&b.display_name.to_lowercase())
        });
        groups
    }

    /// The vendor group with the given `vendor` key (case-insensitive), or
    /// `None`. Matches Windows `VendorGroupForVendorKey`.
    pub fn vendor_group_for_vendor_key(&self, vendor_key: &str) -> Option<VendorGroup> {
        if vendor_key.is_empty() {
            return None;
        }
        self.cloud_tier_vendor_groups()
            .into_iter()
            .find(|g| g.vendor_key.eq_ignore_ascii_case(vendor_key))
    }

    /// The vendor group a cloud-tier entry id belongs to, or `None` for an
    /// unknown id — or for a known id that is not cloud-tier eligible, since
    /// only cloud-tier rows are grouped. Matches macOS `vendorGroup(forEntryId:)`
    /// / Windows `VendorGroupForId`.
    pub fn vendor_group(&self, id: &str) -> Option<VendorGroup> {
        let vendor = &self.entry(id)?.vendor;
        self.vendor_group_for_vendor_key(vendor)
    }

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

/// One row of the Provider dropdown: a company and every cloud-tier entry it
/// owns, in catalog order. Produced only by
/// [`CloudSttCatalog::cloud_tier_vendor_groups`], so `entries` is never empty.
#[derive(Debug, Clone, PartialEq)]
pub struct VendorGroup {
    /// The catalog `vendor` key — the dropdown's selection tag. Carries the
    /// first spelling seen in catalog order.
    pub vendor_key: String,
    /// Plain company name shown in the dropdown.
    pub display_name: String,
    pub entries: Vec<SttEntry>,
}

impl VendorGroup {
    /// The entry a fresh selection lands on — the first in catalog order.
    pub fn default_entry(&self) -> Option<&SttEntry> {
        self.entries.first()
    }

    /// Every model in the group, each paired with the id of the entry that owns
    /// it. The owning entry is what becomes the `X-STT-Provider` header, so a
    /// merged row (Google) can still route each model correctly. Ordered by
    /// entry, then by each entry's own model order.
    pub fn models(&self) -> Vec<(&str, &SttModel)> {
        self.entries
            .iter()
            .flat_map(|e| e.models.iter().map(move |m| (e.id.as_str(), m)))
            .collect()
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

#[cfg(test)]
mod tests {
    use super::*;

    fn catalog() -> CloudSttCatalog {
        CloudSttCatalog::embedded().expect("embedded cloud-stt-catalog.json must parse")
    }

    #[test]
    fn embedded_catalog_parses() {
        let c = catalog();
        assert_eq!(c.version(), 8);
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
                "AssemblyAI", "Deepgram", "ElevenLabs", "Google", "Groq", "Microsoft",
                "Mistral", "OpenAI", "Soniox", "xAI",
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
}
