//! `cloud-stt-catalog.json` — the mirrored types and the exported lookups.

use super::catalogs::cloud_stt;

/// A cloud STT model. Owned mirror of `hw_catalog::SttModel`.
#[derive(uniffi::Record)]
pub struct SttModel {
    pub id: String,
    pub display_name: String,
    pub credits_per_minute: Option<f64>,
    pub is_default: Option<bool>,
    pub preview_status: Option<bool>,
    pub supports_custom_vocabulary: Option<bool>,
    /// Whether HyperWhisper Cloud serves a live WebSocket route for this model
    /// (catalog v8). NOT the entry-level `features.streaming` vendor hint.
    pub streaming: Option<bool>,
}

impl From<&hw_catalog::SttModel> for SttModel {
    fn from(m: &hw_catalog::SttModel) -> Self {
        SttModel {
            id: m.id.clone(),
            display_name: m.display_name.clone(),
            credits_per_minute: m.credits_per_minute,
            is_default: m.is_default,
            preview_status: m.preview_status,
            supports_custom_vocabulary: m.supports_custom_vocabulary,
            streaming: m.streaming,
        }
    }
}

/// Tri-state custom-vocabulary support. Mirrors `hw_catalog::VocabSupport`. The
/// catalog stores a bool or the literal `"unverified"`; only `Yes` means the
/// affordance is shown.
#[derive(uniffi::Enum)]
pub enum VocabSupport {
    Yes,
    No,
    Unverified,
}

impl From<hw_catalog::VocabSupport> for VocabSupport {
    fn from(v: hw_catalog::VocabSupport) -> Self {
        match v {
            hw_catalog::VocabSupport::Yes => VocabSupport::Yes,
            hw_catalog::VocabSupport::No => VocabSupport::No,
            hw_catalog::VocabSupport::Unverified => VocabSupport::Unverified,
        }
    }
}

/// Custom-vocabulary affordance for a provider. Owned mirror of
/// `hw_catalog::CustomVocabulary`.
#[derive(uniffi::Record)]
pub struct SttCustomVocabulary {
    pub supported: VocabSupport,
    pub field_name: Option<String>,
    pub caveats: Option<String>,
}

impl From<&hw_catalog::CustomVocabulary> for SttCustomVocabulary {
    fn from(c: &hw_catalog::CustomVocabulary) -> Self {
        SttCustomVocabulary {
            supported: c.supported.into(),
            field_name: c.field_name.clone(),
            caveats: c.caveats.clone(),
        }
    }
}

/// Cloud-tier display metadata. Owned mirror of `hw_catalog::CloudTier`.
#[derive(uniffi::Record)]
pub struct SttCloudTier {
    pub accuracy: String,
    pub credits_per_minute: f64,
}

/// Cloud-tier / BYOK eligibility. Owned mirror of `hw_catalog::Access`.
#[derive(uniffi::Record)]
pub struct SttAccess {
    pub cloud_tier_eligible: bool,
    pub byok_eligible: bool,
}

/// Per-provider capability flags. Owned mirror of `hw_catalog::Features`.
#[derive(uniffi::Record)]
pub struct SttFeatures {
    pub word_timestamps: bool,
    pub diarization: bool,
    pub streaming: bool,
}

/// The provider's `languages` metadata — WITHOUT the code list.
///
/// The codes are deliberately absent: `SttEntry` is read inside a SwiftUI
/// `ForEach` in a view `body`, and the full catalog carries ~736 codes across
/// the 11 providers, so shipping them per entry would copy that whole set on
/// every body re-evaluation. Fetch them per provider instead, with
/// `cloud_stt_language_codes` (raw) or `cloud_stt_picker_language_codes`
/// (folded).
#[derive(uniffi::Record)]
pub struct SttLanguages {
    /// Upstream's own declared count. `None` when the catalog says
    /// `"unverified"`; may disagree with the length of the code list.
    pub count: Option<i64>,
    pub auto_detect: Option<bool>,
    pub code_format: Option<String>,
    pub notes: Option<String>,
    /// Whether the catalog enumerates codes at all. `false` means
    /// `"unverified"`, i.e. both code accessors return `None`.
    pub has_codes: bool,
}

/// A cloud STT provider row. Owned mirror of `hw_catalog::SttEntry`.
#[derive(uniffi::Record)]
pub struct SttEntry {
    pub id: String,
    pub display_name: String,
    pub display_model: Option<String>,
    pub vendor: String,
    pub vendor_display_name: Option<String>,
    /// `vendor_display_name` falling back to `display_name` — the string a
    /// Provider dropdown shows.
    pub vendor_label: String,
    pub stt_provider: Option<String>,
    pub access: Option<SttAccess>,
    pub models: Vec<SttModel>,
    pub cloud_tier: Option<SttCloudTier>,
    pub features: SttFeatures,
    pub custom_vocabulary: Option<SttCustomVocabulary>,
    pub languages: SttLanguages,
    pub max_file_size_mb: Option<f64>,
    pub max_duration_minutes: Option<i64>,
    pub accepted_formats: Vec<String>,
    pub preview_status: Option<bool>,
    pub migrate_from: Vec<String>,
    pub legacy_cloud_provider_aliases: Vec<String>,
}

impl From<&hw_catalog::SttEntry> for SttEntry {
    fn from(e: &hw_catalog::SttEntry) -> Self {
        SttEntry {
            id: e.id.clone(),
            display_name: e.display_name.clone(),
            display_model: e.display_model.clone(),
            vendor: e.vendor.clone(),
            vendor_display_name: e.vendor_display_name.clone(),
            vendor_label: e.vendor_label().to_string(),
            stt_provider: e.stt_provider.clone(),
            access: e.access.map(|a| SttAccess {
                cloud_tier_eligible: a.cloud_tier_eligible,
                byok_eligible: a.byok_eligible,
            }),
            models: e.models.iter().map(SttModel::from).collect(),
            cloud_tier: e.cloud_tier.as_ref().map(|t| SttCloudTier {
                accuracy: t.accuracy.clone(),
                credits_per_minute: t.credits_per_minute,
            }),
            features: SttFeatures {
                word_timestamps: e.features.word_timestamps,
                diarization: e.features.diarization,
                streaming: e.features.streaming,
            },
            custom_vocabulary: e.custom_vocabulary.as_ref().map(SttCustomVocabulary::from),
            languages: SttLanguages {
                count: e.languages.count,
                auto_detect: e.languages.auto_detect,
                code_format: e.languages.code_format.clone(),
                notes: e.languages.notes.clone(),
                has_codes: e.languages.codes.is_some(),
            },
            max_file_size_mb: e.max_file_size_mb,
            max_duration_minutes: e.max_duration_minutes,
            accepted_formats: e.accepted_formats.clone(),
            preview_status: e.preview_status,
            // Flattened to a plain list: `None` and `[]` mean the same thing to
            // every caller (no aliases), and an empty list is cheaper to consume
            // than an optional across three languages.
            migrate_from: e.migrate_from.clone().unwrap_or_default(),
            legacy_cloud_provider_aliases: e
                .legacy_cloud_provider_aliases
                .clone()
                .unwrap_or_default(),
        }
    }
}

/// One row of the Provider dropdown. Owned mirror of `hw_catalog::VendorGroup`.
#[derive(uniffi::Record)]
pub struct SttVendorGroup {
    /// The catalog `vendor` key — the dropdown's selection tag.
    pub vendor_key: String,
    pub display_name: String,
    /// The group's entries, in catalog order; never empty.
    pub entries: Vec<SttEntry>,
}

impl From<&hw_catalog::VendorGroup> for SttVendorGroup {
    fn from(g: &hw_catalog::VendorGroup) -> Self {
        SttVendorGroup {
            vendor_key: g.vendor_key.clone(),
            display_name: g.display_name.clone(),
            entries: g.entries.iter().map(SttEntry::from).collect(),
        }
    }
}

/// A normalized (provider, accuracy-tier) pair from a legacy cloud-provider value.
/// Mirrors `hw_catalog::NormalizedCloudProvider`.
#[derive(uniffi::Record)]
pub struct NormalizedCloudProvider {
    pub provider: Option<String>,
    pub accuracy_tier: Option<String>,
}

impl From<hw_catalog::NormalizedCloudProvider> for NormalizedCloudProvider {
    fn from(n: hw_catalog::NormalizedCloudProvider) -> Self {
        NormalizedCloudProvider {
            provider: n.provider,
            accuracy_tier: n.accuracy_tier,
        }
    }
}

// ---------------------------------------------------------------------------
// cloud-stt catalog
// ---------------------------------------------------------------------------

/// Whether the STT provider supports custom vocabulary.
#[uniffi::export]
pub fn cloud_stt_supports_custom_vocabulary(id: String) -> bool {
    cloud_stt().supports_custom_vocabulary(&id)
}

/// The provider's custom-vocabulary request field name (if any).
#[uniffi::export]
pub fn cloud_stt_custom_vocabulary_field_name(id: String) -> Option<String> {
    cloud_stt()
        .custom_vocabulary_field_name(&id)
        .map(str::to_string)
}

/// Credits per minute for the provider's default model.
#[uniffi::export]
pub fn cloud_stt_credits_per_minute(id: String) -> f64 {
    cloud_stt().credits_per_minute(&id)
}

/// Credits per minute for a specific model.
#[uniffi::export]
pub fn cloud_stt_credits_per_minute_for_model(id: String, model_id: String) -> f64 {
    cloud_stt().credits_per_minute_for_model(&id, &model_id)
}

/// The underlying STT provider key (the `X-STT-Provider` value), if any.
#[uniffi::export]
pub fn cloud_stt_provider(id: String) -> Option<String> {
    cloud_stt().stt_provider(&id).map(str::to_string)
}

/// The default model id for the provider, if any.
#[uniffi::export]
pub fn cloud_stt_default_model_id(id: String) -> Option<String> {
    cloud_stt().default_model_id(&id).map(str::to_string)
}

/// The provider's supported language codes, if enumerated.
#[uniffi::export]
pub fn cloud_stt_language_codes(id: String) -> Option<Vec<String>> {
    cloud_stt().language_codes(&id).map(|c| c.to_vec())
}

/// All models for the provider.
#[uniffi::export]
pub fn cloud_stt_models(id: String) -> Vec<SttModel> {
    cloud_stt().models(&id).iter().map(SttModel::from).collect()
}

/// The provider's languages folded to the two-letter picker code space, sorted
/// and always including `"auto"`; `None` when the catalog leaves the set
/// `"unverified"` or the id is unknown, so the caller keeps its full list.
///
/// A picker that lists BCP-47 region rows (macOS `STTCapabilities`) must match a
/// row by its PRIMARY SUBTAG against this set, not by exact code.
#[uniffi::export]
pub fn cloud_stt_picker_language_codes(id: String) -> Option<Vec<String>> {
    cloud_stt().picker_language_codes(&id)
}

/// Every cloud STT provider row, in catalog order.
#[uniffi::export]
pub fn cloud_stt_entries() -> Vec<SttEntry> {
    cloud_stt().providers().iter().map(SttEntry::from).collect()
}

/// A single cloud STT provider row by id (case-insensitive), or `None`.
#[uniffi::export]
pub fn cloud_stt_entry(id: String) -> Option<SttEntry> {
    cloud_stt().entry(&id).map(SttEntry::from)
}

/// The provider row whose `migrateFrom` list contains `alias` (case-insensitive,
/// trimmed). Drives legacy accuracy-tier resolution — NOT `cloudProvider`
/// rewriting, which is `cloud_stt_normalize_cloud_provider`.
#[uniffi::export]
pub fn cloud_stt_entry_by_migrate_from(alias: String) -> Option<SttEntry> {
    cloud_stt()
        .entry_by_migrate_from(&alias)
        .map(SttEntry::from)
}

/// The Provider dropdown's rows: cloud-tier entries folded by company and
/// sorted by display name.
#[uniffi::export]
pub fn cloud_stt_cloud_tier_vendor_groups() -> Vec<SttVendorGroup> {
    cloud_stt()
        .cloud_tier_vendor_groups()
        .iter()
        .map(SttVendorGroup::from)
        .collect()
}

/// Cloud-tier provider ids HyperWhisper Cloud can also serve live, in catalog
/// order — `cloudTierEligible` AND at least one model with `streaming: true`.
/// The eligible set for the HW-Cloud live vendor picker.
#[uniffi::export]
pub fn cloud_stt_streaming_cloud_tier_entry_ids() -> Vec<String> {
    cloud_stt()
        .streaming_cloud_tier_entries()
        .map(|e| e.id.clone())
        .collect()
}

/// Same set as `cloud_stt_streaming_cloud_tier_entry_ids`, as full entries.
#[uniffi::export]
pub fn cloud_stt_streaming_cloud_tier_entries() -> Vec<SttEntry> {
    cloud_stt()
        .streaming_cloud_tier_entries()
        .map(SttEntry::from)
        .collect()
}

/// The vendor group a cloud-tier provider id belongs to, or `None` when the id
/// is unknown or is not cloud-tier eligible.
#[uniffi::export]
pub fn cloud_stt_vendor_group(id: String) -> Option<SttVendorGroup> {
    cloud_stt()
        .vendor_group(&id)
        .as_ref()
        .map(SttVendorGroup::from)
}

/// The vendor group with the given `vendor` key (case-insensitive), or `None`.
#[uniffi::export]
pub fn cloud_stt_vendor_group_for_vendor_key(vendor_key: String) -> Option<SttVendorGroup> {
    cloud_stt()
        .vendor_group_for_vendor_key(&vendor_key)
        .as_ref()
        .map(SttVendorGroup::from)
}

/// Normalize a legacy cloud-provider storage value to a (provider, tier) pair.
#[uniffi::export]
pub fn cloud_stt_normalize_cloud_provider(value: Option<String>) -> NormalizedCloudProvider {
    cloud_stt()
        .normalize_cloud_provider(value.as_deref())
        .into()
}

/// Resolve a legacy cloud-STT model id onto its current catalog id.
///
/// `provider` is the persisted `cloudProvider` identifier (`"deepgram"`,
/// `"assemblyai"`, …). `None`, an empty string, or an identifier the provider
/// enum does not know chains every alias table — the behaviour Windows'
/// `CloudTranscriptionModels.ResolveModelAlias` gives its
/// `null or CloudTranscriptionProvider.None` arm.
#[uniffi::export]
pub fn cloud_stt_resolve_model_alias(model_id: String, provider: Option<String>) -> String {
    hw_catalog::resolve_model_alias(&model_id, provider.as_deref())
}
