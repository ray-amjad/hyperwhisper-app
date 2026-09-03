//! UniFFI surface for the M4 catalogs (`hw_catalog`).
//!
//! The leaf catalog objects (`ModelsCatalog`, `CloudSttCatalog`, `CloudPpCatalog`,
//! `AppTypeClassifier`) expose borrow-returning methods (`&str`, `&[T]`,
//! `Option<&T>`, `impl Iterator`) that cannot cross UniFFI. So instead of mirroring
//! the catalog objects, we expose **free functions over the embedded catalogs**
//! returning OWNED values. Each catalog is parsed once from its compile-time
//! `include_str!` JSON into a `OnceLock` (the JSON is a build-time invariant, so
//! `.expect()` on parse is a programmer error, never a runtime failure).

use std::sync::OnceLock;

// ---------------------------------------------------------------------------
// Cached embedded catalogs
// ---------------------------------------------------------------------------

fn models() -> &'static hw_catalog::ModelsCatalog {
    static C: OnceLock<hw_catalog::ModelsCatalog> = OnceLock::new();
    C.get_or_init(|| {
        hw_catalog::ModelsCatalog::embedded().expect("embedded models-catalog.json must parse")
    })
}

/// `pub(crate)` so `ffi_backup::normalize_universal_mode_json` can compose the
/// `cloudProvider` fold with the `hw-backup` tier/pp migration.
pub(crate) fn cloud_stt() -> &'static hw_catalog::CloudSttCatalog {
    static C: OnceLock<hw_catalog::CloudSttCatalog> = OnceLock::new();
    C.get_or_init(|| {
        hw_catalog::CloudSttCatalog::embedded().expect("embedded cloud-stt-catalog.json must parse")
    })
}

fn cloud_pp() -> &'static hw_catalog::CloudPpCatalog {
    static C: OnceLock<hw_catalog::CloudPpCatalog> = OnceLock::new();
    C.get_or_init(|| {
        hw_catalog::CloudPpCatalog::embedded().expect("embedded cloud-pp-catalog.json must parse")
    })
}

fn app_classifier() -> &'static hw_catalog::AppTypeClassifier {
    static C: OnceLock<hw_catalog::AppTypeClassifier> = OnceLock::new();
    C.get_or_init(|| {
        hw_catalog::AppTypeClassifier::embedded()
            .expect("embedded app-type-catalog.json must parse")
    })
}

// ---------------------------------------------------------------------------
// Mirrored owned types
// ---------------------------------------------------------------------------

/// Model family. Mirrors `hw_catalog::Kind`.
#[derive(uniffi::Enum)]
pub enum HwKind {
    Voice,
    Text,
}

impl From<HwKind> for hw_catalog::Kind {
    fn from(k: HwKind) -> Self {
        match k {
            HwKind::Voice => hw_catalog::Kind::Voice,
            HwKind::Text => hw_catalog::Kind::Text,
        }
    }
}

/// Classified app type. Mirrors `hw_catalog::AppType` (renamed to avoid colliding
/// with `hw_text::AppType`, mirrored in `ffi_prompt`).
#[derive(uniffi::Enum)]
pub enum ClassifiedAppType {
    Email,
    Ai,
    WorkMessaging,
    PersonalMessaging,
    Document,
    Code,
    Terminal,
    Sensitive,
    Other,
}

impl From<hw_catalog::AppType> for ClassifiedAppType {
    fn from(a: hw_catalog::AppType) -> Self {
        match a {
            hw_catalog::AppType::Email => ClassifiedAppType::Email,
            hw_catalog::AppType::Ai => ClassifiedAppType::Ai,
            hw_catalog::AppType::WorkMessaging => ClassifiedAppType::WorkMessaging,
            hw_catalog::AppType::PersonalMessaging => ClassifiedAppType::PersonalMessaging,
            hw_catalog::AppType::Document => ClassifiedAppType::Document,
            hw_catalog::AppType::Code => ClassifiedAppType::Code,
            hw_catalog::AppType::Terminal => ClassifiedAppType::Terminal,
            hw_catalog::AppType::Sensitive => ClassifiedAppType::Sensitive,
            hw_catalog::AppType::Other => ClassifiedAppType::Other,
        }
    }
}

/// Result of classifying an app. Mirrors `hw_catalog::AppClassification`, plus the
/// app type's derived prompt/category/text-format strings (resolved here so the
/// platform gets everything in one owned struct).
#[derive(uniffi::Record)]
pub struct AppClassification {
    pub app_type: ClassifiedAppType,
    pub prompt_value: String,
    pub category: String,
    pub text_input_format: String,
    pub confidence: String,
    pub source: String,
    pub matched: Option<String>,
}

impl From<hw_catalog::AppClassification> for AppClassification {
    fn from(c: hw_catalog::AppClassification) -> Self {
        AppClassification {
            app_type: c.app_type.into(),
            prompt_value: c.app_type.prompt_value().to_string(),
            category: c.app_type.category().to_string(),
            text_input_format: c.app_type.text_input_format().to_string(),
            confidence: c.confidence,
            source: c.source,
            matched: c.matched,
        }
    }
}

/// Language support for a model. Mirrors `hw_catalog::LanguageSupport` with the
/// `BTreeSet` flattened to a sorted `Vec`.
#[derive(uniffi::Record)]
pub struct HwLanguageSupport {
    pub codes: Vec<String>,
    pub supports_all: bool,
}

impl From<hw_catalog::LanguageSupport> for HwLanguageSupport {
    fn from(l: hw_catalog::LanguageSupport) -> Self {
        HwLanguageSupport {
            codes: l.codes.into_iter().collect(), // BTreeSet iterates sorted
            supports_all: l.supports_all,
        }
    }
}

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

/// A cloud post-processing model. Owned mirror of `hw_catalog::PpModel`.
#[derive(uniffi::Record)]
pub struct PpModel {
    pub id: String,
    pub display_name: String,
    pub llm_model_header: Option<String>,
    pub price_per_m_input: Option<f64>,
    pub price_per_m_output: Option<f64>,
    pub is_default: Option<bool>,
    pub is_recommended: Option<bool>,
    pub accuracy: Option<i64>,
    pub speed: Option<i64>,
    pub preview_status: Option<bool>,
    pub enabled: Option<bool>,
}

impl From<&hw_catalog::PpModel> for PpModel {
    fn from(m: &hw_catalog::PpModel) -> Self {
        PpModel {
            id: m.id.clone(),
            display_name: m.display_name.clone(),
            llm_model_header: m.llm_model_header.clone(),
            price_per_m_input: m.price_per_m_input,
            price_per_m_output: m.price_per_m_output,
            is_default: m.is_default,
            is_recommended: m.is_recommended,
            accuracy: m.accuracy,
            speed: m.speed,
            preview_status: m.preview_status,
            enabled: m.enabled,
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
    pub code_switching: bool,
    pub endpointing: bool,
    pub context_bias: bool,
    pub language_bias: bool,
    pub turn_timestamps: bool,
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
                code_switching: e.features.code_switching,
                endpointing: e.features.endpointing,
                context_bias: e.features.context_bias,
                language_bias: e.features.language_bias,
                turn_timestamps: e.features.turn_timestamps,
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

/// A post-processing engine and its models. Owned mirror of
/// `hw_catalog::PpProvider`, with `models` already filtered to the visible ones.
#[derive(uniffi::Record)]
pub struct PpProvider {
    pub id: String,
    pub display_name: String,
    /// The `X-LLM-Provider` header value the backend routes on.
    pub llm_provider: String,
    pub api_style: Option<String>,
    /// The rollout gate, already resolved: `enabled != Some(false)`.
    pub enabled: bool,
    pub is_recommended: Option<bool>,
    /// Visible (enabled) models only, in catalog order.
    pub models: Vec<PpModel>,
}

impl From<&hw_catalog::PpProvider> for PpProvider {
    fn from(p: &hw_catalog::PpProvider) -> Self {
        PpProvider {
            id: p.id.clone(),
            display_name: p.display_name.clone(),
            llm_provider: p.llm_provider.clone(),
            api_style: p.api_style.clone(),
            enabled: p.is_enabled(),
            is_recommended: p.is_recommended,
            models: p.visible_models().map(PpModel::from).collect(),
        }
    }
}

/// One row of `models-catalog.json`. Owned mirror of `hw_catalog::Entry`.
#[derive(uniffi::Record)]
pub struct ModelsEntry {
    pub provider: String,
    pub id: String,
    /// The raw `kind` string (`"voice"` / `"text"`). Anything unrecognized is
    /// keyed as voice, matching both platforms' loaders.
    pub kind: String,
    pub supports_custom_vocabulary: bool,
    pub available_via_hyper_whisper_cloud: bool,
    pub platforms: Vec<String>,
    pub display_name: Option<String>,
    pub notes: Option<String>,
    /// Base ISO codes, empty on rows that carry none. Prefer
    /// `models_language_support`, which resolves the wildcard fallback and the
    /// "uncatalogued ⇒ every language" rule.
    pub supported_languages: Vec<String>,
    pub is_english_only: Option<bool>,
    pub supports_all_languages: Option<bool>,
    pub voice_capabilities: Option<ModelsVoiceCapabilities>,
}

/// Structured voice-model capabilities from `models-catalog.json`.
#[derive(uniffi::Record)]
pub struct ModelsVoiceCapabilities {
    pub code_switching: bool,
    pub endpointing: bool,
    pub context_bias: bool,
    pub language_bias: bool,
    pub turn_timestamps: bool,
    pub diarization: bool,
    pub word_timestamps: bool,
}

impl From<&hw_catalog::Entry> for ModelsEntry {
    fn from(e: &hw_catalog::Entry) -> Self {
        ModelsEntry {
            provider: e.provider.clone(),
            id: e.id.clone(),
            kind: e.kind.clone(),
            supports_custom_vocabulary: e.supports_custom_vocabulary,
            available_via_hyper_whisper_cloud: e.available_via_hyper_whisper_cloud,
            platforms: e.platforms.clone(),
            display_name: e.display_name.clone(),
            notes: e.notes.clone(),
            supported_languages: e.supported_languages.clone().unwrap_or_default(),
            is_english_only: e.is_english_only,
            supports_all_languages: e.supports_all_languages,
            voice_capabilities: e.voice_capabilities.map(|c| ModelsVoiceCapabilities {
                code_switching: c.code_switching,
                endpointing: c.endpointing,
                context_bias: c.context_bias,
                language_bias: c.language_bias,
                turn_timestamps: c.turn_timestamps,
                diarization: c.diarization,
                word_timestamps: c.word_timestamps,
            }),
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
// app-type classification
// ---------------------------------------------------------------------------

/// Everything a platform can observe about the foreground app. Mirrors
/// `hw_catalog::ClassifyRequest`.
///
/// A record rather than a parameter list on purpose: issue #279 routes macOS,
/// Windows and Linux through this one call, and each head can see a different
/// subset of the signals. A new signal then costs a field, not a break in every
/// binding. Pass an empty string / `None` / an empty list for a signal the
/// platform cannot observe.
#[derive(uniffi::Record)]
pub struct AppClassifyRequest {
    /// macOS bundle identifier, e.g. `com.apple.mail`.
    pub bundle_id: String,
    /// Process name without an extension, e.g. `OUTLOOK` or `konsole`.
    pub process_name: String,
    /// The app's display name, e.g. `Visual Studio Code`.
    pub app_name: String,
    /// Browser host for a web app. A full URL is accepted and normalized.
    pub host: Option<String>,
    /// The confidence to report for a host hit; empty means `strong`. It
    /// reaches the LLM prompt, so the caller owns it.
    pub host_confidence: String,
    /// Window and/or browser-tab title, composed by the caller.
    pub title: String,
    /// Text read off the focused accessibility element.
    pub focused_pieces: Vec<String>,
}

impl From<AppClassifyRequest> for hw_catalog::ClassifyRequest {
    fn from(r: AppClassifyRequest) -> Self {
        hw_catalog::ClassifyRequest {
            bundle_id: r.bundle_id,
            process_name: r.process_name,
            app_name: r.app_name,
            host: r.host,
            host_confidence: r.host_confidence,
            title: r.title,
            focused_pieces: r.focused_pieces,
        }
    }
}

/// Classify the focused app from everything the platform observed about it.
#[uniffi::export]
pub fn app_classify(request: AppClassifyRequest) -> AppClassification {
    app_classifier().classify(&request.into()).into()
}

/// Whether a browser-tab title looks like webmail.
///
/// The safety net both heads apply when the host was unreadable and nothing
/// else classified the window. Call it ONLY once you know the foreground app is
/// a browser — a title is not evidence of webmail on its own.
#[uniffi::export]
pub fn app_is_webmail(title: String) -> bool {
    hw_catalog::is_webmail(&title)
}

// ---------------------------------------------------------------------------
// models catalog
// ---------------------------------------------------------------------------

/// Whether a model supports custom vocabulary.
#[uniffi::export]
pub fn models_supports_custom_vocabulary(provider: String, kind: HwKind, id: String) -> bool {
    models().supports_custom_vocabulary(&provider, kind.into(), &id)
}

/// Whether a model is available via HyperWhisper Cloud.
#[uniffi::export]
pub fn models_available_via_hw_cloud(provider: String, kind: HwKind, id: String) -> bool {
    models().available_via_hyper_whisper_cloud(&provider, kind.into(), &id)
}

/// The language support of a model (codes sorted; `supports_all` for any-language).
#[uniffi::export]
pub fn models_language_support(provider: String, kind: HwKind, id: String) -> HwLanguageSupport {
    models()
        .language_support(&provider, kind.into(), &id)
        .into()
}

/// A single catalogued model row, resolving the `(provider, kind, "*")` wildcard
/// when the exact id is not catalogued. `None` on a miss.
#[uniffi::export]
pub fn models_entry(provider: String, kind: HwKind, id: String) -> Option<ModelsEntry> {
    models()
        .entry(&provider, kind.into(), &id)
        .map(ModelsEntry::from)
}

/// Every catalogued model row, ordered by `(provider, kind, id)`. Replaces the
/// per-platform catalog decoders that scanned the file for parity checks and for
/// the unified capability list.
#[uniffi::export]
pub fn models_all_entries() -> Vec<ModelsEntry> {
    models().all_entries().map(ModelsEntry::from).collect()
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

// ---------------------------------------------------------------------------
// cloud-pp catalog
// ---------------------------------------------------------------------------

/// Whether the post-processing provider is enabled.
#[uniffi::export]
pub fn cloud_pp_is_enabled(id: String) -> bool {
    cloud_pp().is_enabled(&id)
}

/// The provider's LLM-provider key, if any.
#[uniffi::export]
pub fn cloud_pp_llm_provider(id: String) -> Option<String> {
    cloud_pp().llm_provider(&id).map(str::to_string)
}

/// The LLM model header for a specific model, if any.
#[uniffi::export]
pub fn cloud_pp_llm_model_header(id: String, model_id: String) -> Option<String> {
    cloud_pp()
        .llm_model_header(&id, &model_id)
        .map(str::to_string)
}

/// The provider's default post-processing model, if any.
#[uniffi::export]
pub fn cloud_pp_default_model(id: String) -> Option<PpModel> {
    cloud_pp().default_model(&id).map(PpModel::from)
}

/// A specific post-processing model, if present.
#[uniffi::export]
pub fn cloud_pp_model(id: String, model_id: String) -> Option<PpModel> {
    cloud_pp().model(&id, &model_id).map(PpModel::from)
}

/// All (visible) models for the post-processing provider.
#[uniffi::export]
pub fn cloud_pp_models(id: String) -> Vec<PpModel> {
    cloud_pp()
        .models(&id)
        .into_iter()
        .map(PpModel::from)
        .collect()
}

/// Every post-processing engine, in catalog order — INCLUDING the ones the
/// rollout gate hides. Read `enabled` before surfacing a row; use
/// `cloud_pp_picker_providers` for the dropdown.
#[uniffi::export]
pub fn cloud_pp_providers() -> Vec<PpProvider> {
    cloud_pp()
        .providers()
        .iter()
        .map(PpProvider::from)
        .collect()
}

/// The Engine dropdown's rows: post-processing engines with `enabled != false`,
/// in catalog order, each carrying only its visible models.
#[uniffi::export]
pub fn cloud_pp_picker_providers() -> Vec<PpProvider> {
    cloud_pp()
        .picker_providers()
        .map(PpProvider::from)
        .collect()
}

// ---------------------------------------------------------------------------
// first-run mode seed
// ---------------------------------------------------------------------------

/// The one mode a brand-new install creates. Owned mirror of
/// `hw_catalog::ModeSeed`.
///
/// Deliberately a flat record of non-optional scalars. A head must write EVERY
/// field: macOS Core Data attribute defaults are hostile (`language="en"`,
/// `model="base"`, `cloudProvider="openai"`, `postProcessingProvider="openai"`,
/// `cloudPostProcessingModel="claudeHaiku"`), so a field left unwritten inherits
/// a wrong value rather than an empty one. There is no `Option` here to make
/// "skip it" look reasonable.
///
/// Two field mappings are NOT interchangeable:
///
/// * [`Self::provider_type`] → macOS Core Data `mode.model`, C# `Mode.ProviderType`.
///   macOS has no `providerType` column; the C# entity has both `Model` (left
///   null) and `ProviderType`.
/// * [`Self::post_processing_mode`] is `i32`; macOS narrows it to `Int16`.
#[derive(uniffi::Record, Debug, Clone, PartialEq, Eq)]
pub struct ModeSeed {
    /// `"00000000-0000-0000-0000-000000000001"`. Already identical on all three
    /// heads and anchored by onboarding restore-point lookups. Do not change it.
    pub id: String,
    /// `"Hyper"`.
    pub name: String,
    /// `"hyper"`.
    pub preset: String,
    /// `"auto"`.
    pub language: String,
    /// `"cloud"`. macOS `mode.model`; C# `Mode.ProviderType`. See above.
    pub provider_type: String,
    /// `"hyperwhisper"` — the cloud *transcription* provider, not the
    /// post-processing one.
    pub cloud_provider: String,
    /// `"elevenLabsScribeV2"`.
    pub cloud_accuracy_tier: String,
    /// Resolved from `cloud-stt-catalog.json`.
    pub cloud_transcription_model: String,
    /// `1`. macOS narrows to `Int16`.
    pub post_processing_mode: i32,
    /// `"hyperwhispercloud"` — the canonical token. macOS must be able to READ
    /// this before it starts writing it.
    pub post_processing_provider: String,
    /// `"<engineId>:<modelId>"`, resolved from `cloud-pp-catalog.json`. Store it
    /// verbatim: macOS' parser falls back to **Grok** on a value it cannot split.
    pub cloud_post_processing_model: String,
    /// Never `""` — that token means "no spelling instruction at all".
    pub english_spelling: String,
    pub punctuation: bool,
    pub capitalization: bool,
    pub profanity_filter: bool,
    /// `""`, written explicitly.
    pub custom_instructions: String,
    pub is_default: bool,
    pub is_system_provided: bool,
    pub sort_order: i32,
}

impl From<hw_catalog::ModeSeed> for ModeSeed {
    fn from(s: hw_catalog::ModeSeed) -> Self {
        ModeSeed {
            id: s.id,
            name: s.name,
            preset: s.preset,
            language: s.language,
            provider_type: s.provider_type,
            cloud_provider: s.cloud_provider,
            cloud_accuracy_tier: s.cloud_accuracy_tier,
            cloud_transcription_model: s.cloud_transcription_model,
            post_processing_mode: s.post_processing_mode,
            post_processing_provider: s.post_processing_provider,
            cloud_post_processing_model: s.cloud_post_processing_model,
            english_spelling: s.english_spelling,
            punctuation: s.punctuation,
            capitalization: s.capitalization,
            profanity_filter: s.profanity_filter,
            custom_instructions: s.custom_instructions,
            is_default: s.is_default,
            is_system_provided: s.is_system_provided,
            sort_order: s.sort_order,
        }
    }
}

/// The mode to seed when the store holds NO modes, for a host region.
///
/// Returns ONE record, not a `Vec` — "exactly one mode on a fresh install" is
/// then not a count a head can disagree about. Seeding must still run only on an
/// empty store; that guard stays in each head and is what keeps existing users
/// untouched.
///
/// `region` is an ISO 3166-1 alpha-2 code, `Option` so Rust owns the nil case —
/// the same contract and the same parameter shape as
/// [`crate::ffi_prompt::english_spelling_for_region`]. `None`, empty and unknown
/// all seed American spelling.
///
/// Never panics and never fails: the two catalog-resolved fields fall back to
/// literals rather than unwrap, because the release profile sets
/// `panic = "abort"` and this is the first-launch path. `hw-catalog`'s
/// `catalog_resolution_matches_the_fallback_literals` test pins the catalog to
/// those literals so the fallback cannot silently become the shipped answer.
#[uniffi::export]
pub fn mode_seed_default(region: Option<String>) -> ModeSeed {
    hw_catalog::mode_seed_default(region.as_deref()).into()
}

#[cfg(test)]
mod tests {
    use super::*;

    // The mirrored FFI types are `uniffi::Enum` / `uniffi::Record`, so they carry
    // no `Debug`/`PartialEq` (adding derives to satisfy a test would change the
    // FFI surface). Name each variant through a test-local helper instead, so an
    // assertion never travels back through the conversion it is checking.
    fn app_type_name(t: &ClassifiedAppType) -> &'static str {
        match t {
            ClassifiedAppType::Email => "Email",
            ClassifiedAppType::Ai => "Ai",
            ClassifiedAppType::WorkMessaging => "WorkMessaging",
            ClassifiedAppType::PersonalMessaging => "PersonalMessaging",
            ClassifiedAppType::Document => "Document",
            ClassifiedAppType::Code => "Code",
            ClassifiedAppType::Terminal => "Terminal",
            ClassifiedAppType::Sensitive => "Sensitive",
            ClassifiedAppType::Other => "Other",
        }
    }

    fn request() -> AppClassifyRequest {
        AppClassifyRequest {
            bundle_id: String::new(),
            process_name: String::new(),
            app_name: String::new(),
            host: None,
            host_confidence: String::new(),
            title: String::new(),
            focused_pieces: Vec::new(),
        }
    }

    fn classify(
        bundle_id: &str,
        process_name: &str,
        host: Option<&str>,
        title: &str,
    ) -> AppClassification {
        app_classify(AppClassifyRequest {
            bundle_id: bundle_id.to_string(),
            process_name: process_name.to_string(),
            host: host.map(str::to_string),
            title: title.to_string(),
            ..request()
        })
    }

    // =======================================================================
    // app-type classification
    // =======================================================================

    /// The browser host outranks every other signal, so a Claude tab inside a
    /// mail client is classified as an AI app, not as email.
    #[test]
    fn app_classify_prefers_the_host_over_the_bundle_process_and_title() {
        let c = classify(
            "com.apple.mail",
            "WindowsTerminal",
            Some("claude.ai"),
            "1Password",
        );
        assert_eq!(app_type_name(&c.app_type), "Ai");
        assert_eq!(c.confidence, "strong");
        assert_eq!(c.source, "browserHost");
        assert_eq!(c.matched.as_deref(), Some("claude.ai"));
    }

    /// `bundle_id` and `process_name` are two different catalog columns. The
    /// second call swaps the two values: a macOS bundle id is not a Windows
    /// process name, so nothing matches and the result must fall back to
    /// `Other`. A bridge that passed the arguments in the wrong order would
    /// classify both calls as email.
    #[test]
    fn app_classify_keeps_the_bundle_id_and_process_name_arguments_apart() {
        let mac = classify("com.apple.mail", "wt", None, "");
        assert_eq!(app_type_name(&mac.app_type), "Email");
        assert_eq!(mac.source, "bundleId");
        assert_eq!(mac.matched.as_deref(), Some("com.apple.mail"));

        let swapped = classify("wt", "com.apple.mail", None, "");
        assert_eq!(app_type_name(&swapped.app_type), "Other");
        assert_eq!(swapped.source, "default");
        assert_eq!(swapped.matched, None);
    }

    /// A Windows process name matches case-insensitively and reports the
    /// catalog's own casing as `matched`, not the caller's.
    #[test]
    fn app_classify_process_name_hit_reports_the_catalog_casing() {
        let c = classify("", "outlook", None, "");
        assert_eq!(app_type_name(&c.app_type), "Email");
        assert_eq!(c.confidence, "strong");
        assert_eq!(c.source, "processName");
        assert_eq!(c.matched.as_deref(), Some("OUTLOOK"));
    }

    /// A title keyword is the weakest signal and is reported as `medium`.
    #[test]
    fn app_classify_title_keyword_hit_is_medium_confidence() {
        let c = classify("", "", None, "Ghostty \u{2014} zsh");
        assert_eq!(app_type_name(&c.app_type), "Terminal");
        assert_eq!(c.confidence, "medium");
        assert_eq!(c.source, "title");
        assert_eq!(c.matched.as_deref(), Some("ghostty"));
    }

    /// The three signals issue #279 added to the record cross the boundary in
    /// their own fields. Each is checked on its own, because a bridge that
    /// dropped one would still classify every other case correctly.
    #[test]
    fn app_classify_carries_the_app_name_focused_pieces_and_host_confidence() {
        let by_name = app_classify(AppClassifyRequest {
            app_name: "Ghostty".to_string(),
            ..request()
        });
        assert_eq!(app_type_name(&by_name.app_type), "Terminal");
        assert_eq!(by_name.source, "appName");
        assert_eq!(by_name.confidence, "medium");

        let by_focus = app_classify(AppClassifyRequest {
            focused_pieces: vec!["AXTextField".to_string(), "Subject".to_string()],
            ..request()
        });
        assert_eq!(app_type_name(&by_focus.app_type), "Email");
        assert_eq!(by_focus.source, "focusedElement");

        let by_address = app_classify(AppClassifyRequest {
            focused_pieces: vec!["ray@example.com".to_string()],
            ..request()
        });
        assert_eq!(app_type_name(&by_address.app_type), "Email");
        assert_eq!(by_address.source, "focusedElementText");
        assert_eq!(by_address.confidence, "weak");

        let caller_confidence = app_classify(AppClassifyRequest {
            host: Some("claude.ai".to_string()),
            host_confidence: "manual".to_string(),
            ..request()
        });
        assert_eq!(caller_confidence.confidence, "manual");
        assert_eq!(caller_confidence.source, "browserHost");
    }

    /// `app_is_webmail` is the browser-tab safety net, and it is deliberately
    /// NOT a `classify` signal — the same title must not classify on its own.
    #[test]
    fn app_is_webmail_is_separate_from_classification() {
        assert!(app_is_webmail("Inbox (12) - Gmail".to_string()));
        assert!(app_is_webmail("ray@acme.co Mail".to_string()));
        assert!(!app_is_webmail("Acme Team - Slack".to_string()));

        let c = classify("", "", None, "ray@acme.co Mail");
        assert_eq!(app_type_name(&c.app_type), "Other");
        assert_eq!(c.source, "default");
    }

    /// Nothing in the catalog matches, so the bridge returns the default row.
    #[test]
    fn app_classify_falls_back_to_other_when_no_signal_matches() {
        let c = classify(
            "com.example.nothing",
            "nothing",
            Some("nothing.example"),
            "nothing at all",
        );
        assert_eq!(app_type_name(&c.app_type), "Other");
        assert_eq!(c.confidence, "unknown");
        assert_eq!(c.source, "default");
        assert_eq!(c.matched, None);
    }

    /// Every leaf `AppType` arm maps to its own FFI variant, and the record
    /// carries the three derived strings in the right fields. The expectations
    /// are written out by hand — they never go back through the conversion —
    /// so a swapped pair of arms or a swapped pair of fields fails here.
    #[test]
    fn every_app_type_arm_maps_to_its_own_variant_and_derived_strings() {
        // (leaf type, variant name, prompt_value, category, text_input_format)
        let cases: &[(hw_catalog::AppType, &str, &str, &str, &str)] = &[
            (
                hw_catalog::AppType::Email,
                "Email",
                "email",
                "Email Client",
                "email",
            ),
            (hw_catalog::AppType::Ai, "Ai", "ai", "AI", "text"),
            (
                hw_catalog::AppType::WorkMessaging,
                "WorkMessaging",
                "work_messaging",
                "Communication",
                "text",
            ),
            (
                hw_catalog::AppType::PersonalMessaging,
                "PersonalMessaging",
                "personal_messaging",
                "Communication",
                "text",
            ),
            (
                hw_catalog::AppType::Document,
                "Document",
                "document",
                "Document",
                "markdown",
            ),
            (
                hw_catalog::AppType::Code,
                "Code",
                "code",
                "Code Editor",
                "code",
            ),
            (
                hw_catalog::AppType::Terminal,
                "Terminal",
                "terminal",
                "Terminal",
                "command",
            ),
            (
                hw_catalog::AppType::Sensitive,
                "Sensitive",
                "sensitive",
                "Sensitive",
                "text",
            ),
            (
                hw_catalog::AppType::Other,
                "Other",
                "other",
                "Application",
                "text",
            ),
        ];
        for (leaf, name, prompt_value, category, text_input_format) in cases {
            let ffi: AppClassification = hw_catalog::AppClassification {
                app_type: *leaf,
                confidence: "strong".to_string(),
                source: "bundleId".to_string(),
                matched: Some("com.example.app".to_string()),
            }
            .into();
            assert_eq!(app_type_name(&ffi.app_type), *name);
            assert_eq!(ffi.prompt_value, *prompt_value, "prompt_value for {name}");
            assert_eq!(ffi.category, *category, "category for {name}");
            assert_eq!(
                ffi.text_input_format, *text_input_format,
                "text_input_format for {name}"
            );
            // The pass-through fields keep their own slots.
            assert_eq!(ffi.confidence, "strong");
            assert_eq!(ffi.source, "bundleId");
            assert_eq!(ffi.matched.as_deref(), Some("com.example.app"));
        }
    }

    // =======================================================================
    // models catalog
    // =======================================================================

    /// `HwKind` is load-bearing: the same provider and id resolve to a
    /// catalogued voice row and to no text row at all. A bridge that dropped
    /// the kind (or mapped both arms to `Voice`) would answer `true` twice.
    #[test]
    fn models_lookups_are_kind_sensitive() {
        let id = "gemini-2.5-flash".to_string();
        assert!(models_supports_custom_vocabulary(
            "gemini".to_string(),
            HwKind::Voice,
            id.clone()
        ));
        assert!(!models_supports_custom_vocabulary(
            "gemini".to_string(),
            HwKind::Text,
            id.clone()
        ));
        assert!(models_available_via_hw_cloud(
            "gemini".to_string(),
            HwKind::Voice,
            id.clone()
        ));
        assert!(!models_available_via_hw_cloud(
            "gemini".to_string(),
            HwKind::Text,
            id
        ));
    }

    /// Custom vocabulary and HyperWhisper Cloud availability are two separate
    /// catalog columns; `gemini-3.1-flash-lite` sets the first and clears the second.
    #[test]
    fn models_custom_vocabulary_and_cloud_availability_are_different_flags() {
        let id = "gemini-3.1-flash-lite".to_string();
        assert!(models_supports_custom_vocabulary(
            "gemini".to_string(),
            HwKind::Voice,
            id.clone()
        ));
        assert!(!models_available_via_hw_cloud(
            "gemini".to_string(),
            HwKind::Voice,
            id
        ));
    }

    /// An unknown model is never wrongly hidden: language support falls back to
    /// "all languages" with an empty code list.
    #[test]
    fn models_language_support_defaults_to_all_languages_for_an_unknown_model() {
        let s = models_language_support(
            "gemini".to_string(),
            HwKind::Voice,
            "no-such-model".to_string(),
        );
        assert!(s.supports_all);
        assert!(s.codes.is_empty());
    }

    /// The leaf holds the codes in a `BTreeSet`; the FFI record flattens it to
    /// a `Vec`. The catalog lists Voxtral's languages in a different order
    /// (`en`, `zh`, `hi`, ...), so this pins the sorted-order contract the
    /// platforms read.
    #[test]
    fn models_language_support_flattens_the_code_set_in_sorted_order() {
        let s = models_language_support(
            "mistral".to_string(),
            HwKind::Voice,
            "voxtral-mini-latest".to_string(),
        );
        assert!(!s.supports_all);
        assert_eq!(
            s.codes,
            vec!["ar", "de", "en", "es", "fr", "hi", "it", "ja", "ko", "nl", "pt", "ru", "zh"]
        );
    }

    // =======================================================================
    // cloud-STT catalog
    // =======================================================================

    /// The tier price and the per-model price are separate lookups.
    /// `openaiWhisper` has no cloud tier at all, so the tier price is `0.0`
    /// while its models still carry their own prices.
    #[test]
    fn cloud_stt_tier_price_and_model_price_are_separate_lookups() {
        assert_eq!(
            cloud_stt_credits_per_minute("openaiWhisper".to_string()),
            0.0
        );
        assert_eq!(
            cloud_stt_credits_per_minute_for_model(
                "openaiWhisper".to_string(),
                "gpt-4o-transcribe".to_string()
            ),
            6.0
        );
        assert_eq!(
            cloud_stt_credits_per_minute_for_model(
                "openaiWhisper".to_string(),
                "gpt-4o-mini-transcribe".to_string()
            ),
            3.0
        );
        assert_eq!(
            cloud_stt_credits_per_minute("deepgramNova3".to_string()),
            5.5
        );
    }

    /// An unknown model falls back to the provider's tier price; an unknown
    /// provider is `0.0`.
    #[test]
    fn cloud_stt_model_price_falls_back_to_the_tier_price() {
        assert_eq!(
            cloud_stt_credits_per_minute_for_model(
                "deepgramNova3".to_string(),
                "no-such-model".to_string()
            ),
            5.5
        );
        assert_eq!(
            cloud_stt_credits_per_minute_for_model(
                "noSuchProvider".to_string(),
                "nova-3-general".to_string()
            ),
            0.0
        );
        assert_eq!(
            cloud_stt_credits_per_minute("noSuchProvider".to_string()),
            0.0
        );
    }

    /// AssemblyAI's retired Pro model must not survive in the shared cloud catalog,
    /// and the current 3.5 Pro model remains the routed default.
    #[test]
    fn cloud_stt_assemblyai_catalog_uses_the_current_default() {
        let models = cloud_stt_models("assemblyAI".to_string());
        assert_eq!(
            models.first().map(|m| m.id.as_str()),
            Some("universal-3-5-pro")
        );
        assert!(!models.iter().any(|m| m.id == "universal-3-pro"));
        assert_eq!(
            cloud_stt_default_model_id("assemblyAI".to_string()).as_deref(),
            Some("universal-3-5-pro")
        );
    }

    /// Grok's single model has an empty id — "the provider default" to the
    /// backend. `Some("")` must not collapse into `None`, which is what an
    /// unknown provider returns.
    #[test]
    fn cloud_stt_default_model_id_keeps_an_empty_model_id() {
        assert_eq!(
            cloud_stt_default_model_id("grokStt".to_string()).as_deref(),
            Some("")
        );
        assert_eq!(
            cloud_stt_default_model_id("noSuchProvider".to_string()),
            None
        );
    }

    /// The routing header value and the custom-vocabulary field name come from
    /// two different catalog fields, and both are `None` for an unknown id.
    #[test]
    fn cloud_stt_routing_header_and_vocabulary_field_are_separate_lookups() {
        assert_eq!(
            cloud_stt_provider("azureMaiTranscribe".to_string()).as_deref(),
            Some("azure-mai")
        );
        assert_eq!(
            cloud_stt_custom_vocabulary_field_name("azureMaiTranscribe".to_string()).as_deref(),
            Some("definition.phraseList.phrases")
        );
        assert_eq!(
            cloud_stt_provider("deepgramNova3".to_string()).as_deref(),
            Some("deepgram")
        );
        assert_eq!(
            cloud_stt_custom_vocabulary_field_name("deepgramNova3".to_string()).as_deref(),
            Some("keyterm")
        );
        assert_eq!(cloud_stt_provider("noSuchProvider".to_string()), None);
        assert_eq!(
            cloud_stt_custom_vocabulary_field_name("noSuchProvider".to_string()),
            None
        );
        assert!(cloud_stt_supports_custom_vocabulary(
            "deepgramNova3".to_string()
        ));
        assert!(!cloud_stt_supports_custom_vocabulary(
            "noSuchProvider".to_string()
        ));
    }

    /// Gemini leaves its language set `"unverified"` in the catalog, which the
    /// leaf reduces to `None`. An enumerated provider returns the real list.
    #[test]
    fn cloud_stt_language_codes_are_none_when_the_catalog_is_unverified() {
        assert_eq!(cloud_stt_language_codes("gemini".to_string()), None);
        assert_eq!(cloud_stt_language_codes("noSuchProvider".to_string()), None);
        let groq = cloud_stt_language_codes("groqWhisper".to_string())
            .expect("groqWhisper enumerates its languages");
        assert!(groq.contains(&"en".to_string()));
        assert!(
            groq.len() > 50,
            "expected the full upstream list, got {}",
            groq.len()
        );
    }

    /// Pins every field of the owned `SttModel` mirror against two rows whose
    /// flags differ, so a swapped pair of fields cannot pass.
    #[test]
    fn cloud_stt_models_mirror_every_field_in_catalog_order() {
        let models = cloud_stt_models("openaiWhisper".to_string());
        let ids: Vec<&str> = models.iter().map(|m| m.id.as_str()).collect();
        assert_eq!(
            ids,
            vec![
                "gpt-4o-transcribe",
                "gpt-4o-mini-transcribe",
                "whisper-1",
                "gpt-transcribe",
                "gpt-live-transcribe",
            ]
        );

        let default_row = &models[0];
        assert_eq!(default_row.display_name, "GPT-4o Transcribe");
        assert_eq!(default_row.credits_per_minute, Some(6.0));
        assert_eq!(default_row.is_default, Some(true));
        assert_eq!(default_row.preview_status, Some(false));
        assert_eq!(default_row.supports_custom_vocabulary, Some(true));

        let other_row = &models[1];
        assert_eq!(other_row.display_name, "GPT-4o Mini Transcribe");
        assert_eq!(other_row.credits_per_minute, Some(3.0));
        assert_eq!(other_row.is_default, Some(false));
        assert_eq!(other_row.preview_status, Some(false));
        assert_eq!(other_row.supports_custom_vocabulary, Some(true));

        assert!(cloud_stt_models("noSuchProvider".to_string()).is_empty());
    }

    /// A legacy standalone-provider value folds onto the HyperWhisper Cloud
    /// tier; a BYOK provider name passes through untouched.
    #[test]
    fn cloud_stt_normalize_folds_a_legacy_alias_but_leaves_byok_names_alone() {
        let folded = cloud_stt_normalize_cloud_provider(Some("microsoftazurespeech".to_string()));
        assert_eq!(folded.provider.as_deref(), Some("hyperwhisper"));
        assert_eq!(folded.accuracy_tier.as_deref(), Some("azureMaiTranscribe"));

        let google = cloud_stt_normalize_cloud_provider(Some("googleSpeech".to_string()));
        assert_eq!(google.provider.as_deref(), Some("hyperwhisper"));
        assert_eq!(google.accuracy_tier.as_deref(), Some("geminiTranscribe"));

        let byok = cloud_stt_normalize_cloud_provider(Some("deepgram".to_string()));
        assert_eq!(byok.provider.as_deref(), Some("deepgram"));
        assert_eq!(byok.accuracy_tier, None);

        let absent = cloud_stt_normalize_cloud_provider(None);
        assert_eq!(absent.provider, None);
        assert_eq!(absent.accuracy_tier, None);
    }

    /// The FFI boundary the platforms actually cross. Windows persists
    /// `geminiTranscribe`; macOS' `CloudProvider` raw value is the lowercase
    /// `geminitranscribe` and its `CloudProvider(rawValue:)` miss silently falls
    /// back to `.hyperwhisper`, so a camelCase pass-through moved a BYOK user
    /// onto paid credits on restore. The core must hand both platforms the one
    /// spelling both parsers accept.
    #[test]
    fn cloud_stt_normalize_lowercases_gemini_transcribe_for_the_macos_parser() {
        for spelling in ["geminiTranscribe", "geminitranscribe", "GEMINITRANSCRIBE"] {
            let n = cloud_stt_normalize_cloud_provider(Some(spelling.to_string()));
            assert_eq!(n.provider.as_deref(), Some("geminitranscribe"), "{spelling}");
            assert_eq!(n.accuracy_tier, None, "{spelling} is BYOK, not a cloud tier");
        }
    }

    // =======================================================================
    // cloud-PP catalog
    // =======================================================================

    /// The rollout gate defaults to false for an engine the catalog does not
    /// list, so an unknown `X-LLM-Provider` is never surfaced.
    #[test]
    fn cloud_pp_is_enabled_is_false_for_an_unknown_engine() {
        assert!(cloud_pp_is_enabled("openai".to_string()));
        assert!(cloud_pp_is_enabled("anthropic".to_string()));
        assert!(!cloud_pp_is_enabled("noSuchEngine".to_string()));
    }

    /// `cloud_pp_llm_model_header` takes (engine id, model id) in that order.
    /// Swapping them finds nothing.
    #[test]
    fn cloud_pp_llm_model_header_takes_the_engine_id_first() {
        assert_eq!(
            cloud_pp_llm_model_header("openai".to_string(), "gpt-5-nano".to_string()).as_deref(),
            Some("gpt-5-nano")
        );
        assert_eq!(
            cloud_pp_llm_model_header("gpt-5-nano".to_string(), "openai".to_string()),
            None
        );
        assert_eq!(
            cloud_pp_llm_model_header("openai".to_string(), "no-such-model".to_string()),
            None
        );
        assert_eq!(
            cloud_pp_llm_provider("anthropic".to_string()).as_deref(),
            Some("anthropic")
        );
        assert_eq!(cloud_pp_llm_provider("noSuchEngine".to_string()), None);
    }

    /// Pins every field of the owned `PpModel` mirror. Input and output prices
    /// differ, and so do accuracy and speed, so a swapped pair fails.
    #[test]
    fn cloud_pp_default_model_mirrors_every_field() {
        let m = cloud_pp_default_model("openai".to_string()).expect("openai has a default model");
        assert_eq!(m.id, "gpt-5-mini");
        assert_eq!(m.display_name, "GPT-5 mini");
        assert_eq!(m.llm_model_header.as_deref(), Some("gpt-5-mini"));
        assert_eq!(m.price_per_m_input, Some(0.25));
        assert_eq!(m.price_per_m_output, Some(2.0));
        assert_eq!(m.is_default, Some(true));
        assert_eq!(m.is_recommended, Some(true));
        assert_eq!(m.accuracy, Some(4));
        assert_eq!(m.speed, Some(1));
        assert_eq!(m.preview_status, Some(false));
        assert_eq!(m.enabled, Some(true));

        assert!(cloud_pp_default_model("noSuchEngine".to_string()).is_none());
    }

    /// A non-default model is reachable by id, and carries its own flags.
    #[test]
    fn cloud_pp_model_looks_up_a_non_default_model_by_id() {
        let m = cloud_pp_model("openai".to_string(), "gpt-5-nano".to_string())
            .expect("gpt-5-nano is catalogued");
        assert_eq!(m.id, "gpt-5-nano");
        assert_eq!(m.display_name, "GPT-5 nano");
        assert_eq!(m.price_per_m_input, Some(0.05));
        assert_eq!(m.price_per_m_output, Some(0.4));
        assert_eq!(m.is_default, Some(false));
        assert_eq!(m.is_recommended, Some(false));
        assert_eq!(m.accuracy, Some(2));
        assert_eq!(m.speed, Some(2));

        assert!(cloud_pp_model("openai".to_string(), "no-such-model".to_string()).is_none());
        assert!(cloud_pp_model("noSuchEngine".to_string(), "gpt-5-nano".to_string()).is_none());
    }

    /// The engine's models come back in catalog order, default first here.
    #[test]
    fn cloud_pp_models_list_the_engines_visible_models_in_catalog_order() {
        let ids: Vec<String> = cloud_pp_models("openai".to_string())
            .into_iter()
            .map(|m| m.id)
            .collect();
        assert_eq!(
            ids,
            vec!["gpt-5-mini".to_string(), "gpt-5-nano".to_string()]
        );
        assert!(cloud_pp_models("noSuchEngine".to_string()).is_empty());
    }
}

// ===========================================================================
// The language catalog (#285).
//
// Two native lists became one: `LanguageData.swift` carried 126 rows plus the
// canonicalizer and the alias map, `LanguageInfo.cs` carried 102 of those rows
// with a plain `Code == code` scan and no canonicalization at all. Both are
// deleted; this is the whole surface the pickers now build from.
//
// The `Locale.localizedString` fallback deliberately stays native. A code the
// catalog does not know comes back with `display_name: None`, and the host asks
// its own system database for a name.
// ===========================================================================

/// One catalog row. `Hw`-prefixed for the usual reason: an unprefixed
/// `Language` would land one namespace import away from the heads' own
/// language types, and `LanguageInfo` is literally the name of the Windows
/// class this replaces.
#[derive(uniffi::Record)]
pub struct HwLanguage {
    /// The canonical BCP-47 tag.
    pub code: String,
    /// The English display name, or `None` when the catalog does not know the
    /// code and the host has to localize it.
    pub display_name: Option<String>,
}

impl From<hw_catalog::Language> for HwLanguage {
    fn from(language: hw_catalog::Language) -> Self {
        HwLanguage {
            code: language.code,
            display_name: language.display_name,
        }
    }
}

impl From<&HwLanguage> for hw_catalog::Language {
    fn from(language: &HwLanguage) -> Self {
        hw_catalog::Language {
            code: language.code.clone(),
            display_name: language.display_name.clone(),
        }
    }
}

/// Canonicalize a BCP-47 tag: `en_gb` becomes `en-GB`, `ZH-HANT` becomes
/// `zh-Hant`, an empty tag becomes `auto`.
#[uniffi::export]
pub fn language_canonicalize(code: String) -> String {
    hw_catalog::canonicalize_language_code(&code)
}

/// The canonical tag to persist. A missing or empty code becomes `en`.
#[uniffi::export]
pub fn language_canonical_code(code: Option<String>) -> String {
    hw_catalog::canonical_language_code(code.as_deref())
}

/// The 2-letter ISO 639 code, for the frameworks that refuse anything longer.
/// `auto` survives; a missing code becomes `en`.
#[uniffi::export]
pub fn language_normalize(code: Option<String>) -> String {
    hw_catalog::normalize_language_code(code.as_deref())
}

/// Whether a code means English. A missing code counts as English.
#[uniffi::export]
pub fn language_is_english(code: Option<String>) -> bool {
    hw_catalog::is_english(code.as_deref())
}

/// Look one code up. `None` means the catalog does not know it — canonicalize
/// it with [`language_canonicalize`] and localize it natively.
#[uniffi::export]
pub fn language_info(code: String) -> Option<HwLanguage> {
    hw_catalog::info(&code).map(HwLanguage::from)
}

/// The whole catalog in picker order: `auto`, then the popular rows in their
/// declared order, then everything else alphabetically by display name.
#[uniffi::export]
pub fn language_all() -> Vec<HwLanguage> {
    hw_catalog::all_languages()
        .into_iter()
        .map(HwLanguage::from)
        .collect()
}

/// The codes the pickers float to the top, in the order they appear there.
#[uniffi::export]
pub fn language_popular_codes() -> Vec<String> {
    hw_catalog::POPULAR_CODES
        .iter()
        .map(|code| (*code).to_string())
        .collect()
}

/// Canonical rows for a provider's advertised code list, deduplicated, in the
/// order given. An unknown code keeps its canonical form and comes back with no
/// display name.
#[uniffi::export]
pub fn language_resolve(codes: Vec<String>) -> Vec<HwLanguage> {
    hw_catalog::resolve_languages(&codes)
        .into_iter()
        .map(HwLanguage::from)
        .collect()
}

/// Move `auto` to the front of a list if it is present and not already there.
#[uniffi::export]
pub fn language_prioritize_automatic(languages: Vec<HwLanguage>) -> Vec<HwLanguage> {
    let languages: Vec<hw_catalog::Language> = languages
        .iter()
        .map(hw_catalog::Language::from)
        .collect();
    hw_catalog::prioritize_automatic(languages)
        .into_iter()
        .map(HwLanguage::from)
        .collect()
}

#[cfg(test)]
mod language_tests {
    use super::*;

    #[test]
    fn the_catalog_crosses_the_boundary_whole() {
        let all = language_all();
        assert_eq!(all.len(), 126);
        assert_eq!(all.first().map(|first| first.code.as_str()), Some("auto"));
        assert!(all.iter().all(|row| row.display_name.is_some()));
    }

    #[test]
    fn a_stored_windows_style_code_now_resolves() {
        // `LanguageInfo.GetDisplayName("en_GB")` returned the string "en_GB".
        assert_eq!(language_canonicalize("en_GB".to_string()), "en-GB");
        assert_eq!(
            language_info("en_GB".to_string()).and_then(|row| row.display_name),
            Some("English (United Kingdom)".to_string())
        );
    }

    #[test]
    fn an_unknown_code_is_handed_back_for_the_host_to_localize() {
        let resolved = language_resolve(vec!["xx".to_string()]);
        assert_eq!(resolved.len(), 1);
        assert!(language_info("xx".to_string()).is_none());
        assert_eq!(
            resolved.first().and_then(|row| row.display_name.clone()),
            None
        );
    }

    #[test]
    fn the_scalar_helpers_agree_with_the_catalog() {
        assert_eq!(language_normalize(Some("zh_Hant".to_string())), "zh");
        assert_eq!(language_canonical_code(None), "en");
        assert!(language_is_english(None));
        assert!(!language_is_english(Some("auto".to_string())));
        assert_eq!(language_popular_codes().len(), 19);
    }

    #[test]
    fn prioritize_automatic_round_trips_the_records() {
        let ordered = language_prioritize_automatic(language_resolve(vec![
            "fr".to_string(),
            "auto".to_string(),
        ]));
        assert_eq!(
            ordered
                .iter()
                .map(|row| row.code.as_str())
                .collect::<Vec<&str>>(),
            vec!["auto", "fr"]
        );
    }
}
