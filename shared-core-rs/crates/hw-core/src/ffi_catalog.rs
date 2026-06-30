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

fn cloud_stt() -> &'static hw_catalog::CloudSttCatalog {
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
        hw_catalog::AppTypeClassifier::embedded().expect("embedded app-type-catalog.json must parse")
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

/// Classify the focused app from its identifiers. `host` is the browser host when
/// the app is a browser.
#[uniffi::export]
pub fn app_classify(
    bundle_id: String,
    process_name: String,
    host: Option<String>,
    title: String,
) -> AppClassification {
    app_classifier()
        .classify(&bundle_id, &process_name, host.as_deref(), &title)
        .into()
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
    models().language_support(&provider, kind.into(), &id).into()
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

/// Normalize a legacy cloud-provider storage value to a (provider, tier) pair.
#[uniffi::export]
pub fn cloud_stt_normalize_cloud_provider(value: Option<String>) -> NormalizedCloudProvider {
    cloud_stt()
        .normalize_cloud_provider(value.as_deref())
        .into()
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
    cloud_pp().models(&id).into_iter().map(PpModel::from).collect()
}
