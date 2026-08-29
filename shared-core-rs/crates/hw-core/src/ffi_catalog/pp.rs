//! `cloud-pp-catalog.json` — the mirrored types and the exported lookups.

use super::catalogs::cloud_pp;

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
