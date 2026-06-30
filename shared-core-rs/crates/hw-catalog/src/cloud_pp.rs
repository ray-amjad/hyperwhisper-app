//! WP-D3 — `cloud-pp-catalog.json` parsing + lookup.
//!
//! Port of `app/macos/.../AppClassification/CloudPPCatalog.swift` and
//! `app/windows/.../Services/AppClassification/CloudPpCatalog.cs`. Plain Rust,
//! sans-I/O: the catalog JSON is embedded at compile time
//! (`super::CLOUD_PP_CATALOG`), so this module only parses an in-memory string
//! and answers lookups.
//!
//! Drives the credit-billed (no-key) post-processing Engine + Model picker and
//! the `X-LLM-Provider` / `X-LLM-Model` headers sent to the backend
//! `/post-process` route. Prices are display/estimate only — the actual billing
//! constants live in `backend-v2-flyio/src/lib/cost-calculator.ts`.
//!
//! Parity notes:
//! - **`enabled` rollout gate.** `None` is treated as enabled (older catalogs);
//!   `Some(false)` hides the engine/model. Both platforms filter on
//!   `enabled != false`. Exposing a not-yet-deployed engine would make
//!   `X-LLM-Provider: <new>` silently fall back to Cerebras (wrong model + wrong
//!   billing), so the gate is load-bearing.
//! - **`X-LLM-Model` header** is `llmModelHeader` falling back to the model `id`
//!   ([`PpModel::model_header`]). `X-LLM-Provider` is the provider `llmProvider`.
//! - **Default model** is `isDefault: true`, else the first ENABLED model.
//! - Case-insensitive id lookup, matching both platforms.

use serde::Deserialize;

/// A selectable post-processing model within an engine. `id` drives the Model
/// dropdown; the `X-LLM-Model` header is [`PpModel::model_header`]
/// (`llmModelHeader` or `id`).
#[derive(Debug, Clone, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PpModel {
    pub id: String,
    pub display_name: String,
    #[serde(default)]
    pub llm_model_header: Option<String>,
    #[serde(default)]
    pub price_per_m_input: Option<f64>,
    #[serde(default)]
    pub price_per_m_output: Option<f64>,
    #[serde(default)]
    pub is_default: Option<bool>,
    #[serde(default)]
    pub is_recommended: Option<bool>,
    #[serde(default)]
    pub accuracy: Option<i64>,
    #[serde(default)]
    pub speed: Option<i64>,
    #[serde(default)]
    pub preview_status: Option<bool>,
    /// Rollout gate. `None` (and `Some(true)`) = visible; `Some(false)` = hidden.
    #[serde(default)]
    pub enabled: Option<bool>,
}

impl PpModel {
    /// The `X-LLM-Model` header value — explicit `llmModelHeader` or the `id`.
    /// Matches macOS `modelHeader` / Windows `ModelHeader`.
    pub fn model_header(&self) -> &str {
        self.llm_model_header.as_deref().unwrap_or(&self.id)
    }

    /// Whether this model is surfaced in the picker (`enabled != Some(false)`).
    fn is_visible(&self) -> bool {
        self.enabled != Some(false)
    }
}

/// A post-processing engine (provider). `id` is the storage prefix persisted in
/// `Mode.cloudPostProcessingModel` (`<id>:<modelId>`); `llm_provider` is the
/// `X-LLM-Provider` header value.
#[derive(Debug, Clone, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PpProvider {
    pub id: String,
    pub display_name: String,
    /// The `X-LLM-Provider` header value the backend routes on.
    pub llm_provider: String,
    /// `"openai"` (OpenAI-compatible) or `"anthropic"` (native). Informational.
    #[serde(default)]
    pub api_style: Option<String>,
    /// Rollout gate. `None` is treated as enabled; `Some(false)` hides the engine.
    #[serde(default)]
    pub enabled: Option<bool>,
    #[serde(default)]
    pub is_recommended: Option<bool>,
    #[serde(default)]
    pub models: Vec<PpModel>,
}

impl PpProvider {
    /// Whether this engine is surfaced in the picker (`enabled != Some(false)`).
    pub fn is_enabled(&self) -> bool {
        self.enabled != Some(false)
    }

    /// Visible (enabled) models, in catalog order.
    pub fn visible_models(&self) -> impl Iterator<Item = &PpModel> {
        self.models.iter().filter(|m| m.is_visible())
    }

    /// The default model — `isDefault: true`, else the first ENABLED model.
    /// Matches macOS/Windows `defaultModel(forProviderId:)` which filter to
    /// enabled models first.
    pub fn default_model(&self) -> Option<&PpModel> {
        self.models
            .iter()
            .filter(|m| m.is_visible())
            .find(|m| m.is_default == Some(true))
            .or_else(|| self.models.iter().find(|m| m.is_visible()))
    }
}

/// Error parsing the cloud-pp catalog JSON.
#[derive(thiserror::Error, Debug)]
pub enum CloudPpError {
    #[error("cloud-pp-catalog.json failed to decode: {0}")]
    Decode(#[from] serde_json::Error),
}

/// Parsed cloud post-processing catalog. Build once and reuse.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CloudPpCatalog {
    #[serde(default)]
    version: i64,
    #[serde(default)]
    updated: String,
    #[serde(default)]
    providers: Vec<PpProvider>,
}

impl CloudPpCatalog {
    /// Parse a cloud-pp-catalog JSON string.
    pub fn parse(json: &str) -> Result<CloudPpCatalog, CloudPpError> {
        Ok(serde_json::from_str(json)?)
    }

    /// Parse the compile-time-embedded `cloud-pp-catalog.json`.
    pub fn embedded() -> Result<CloudPpCatalog, CloudPpError> {
        CloudPpCatalog::parse(super::CLOUD_PP_CATALOG)
    }

    pub fn version(&self) -> i64 {
        self.version
    }

    pub fn updated(&self) -> &str {
        &self.updated
    }

    /// All engines (including disabled), in catalog order. Use
    /// [`CloudPpCatalog::picker_providers`] for the UI-visible subset.
    pub fn providers(&self) -> &[PpProvider] {
        &self.providers
    }

    /// Look up an engine by `id` (case-insensitive). Matches macOS
    /// `provider(byId:)` / Windows `GetById`.
    pub fn provider(&self, id: &str) -> Option<&PpProvider> {
        self.providers
            .iter()
            .find(|p| p.id.eq_ignore_ascii_case(id))
    }

    /// Engines surfaced in the Engine dropdown (`enabled != false`), in catalog
    /// order. The rollout gate — a `None` `enabled` is treated as enabled.
    pub fn picker_providers(&self) -> impl Iterator<Item = &PpProvider> {
        self.providers.iter().filter(|p| p.is_enabled())
    }

    /// Whether an engine is enabled (visible). Defaults to false on unknown id.
    pub fn is_enabled(&self, id: &str) -> bool {
        self.provider(id).map(|p| p.is_enabled()).unwrap_or(false)
    }

    /// Visible models for an engine (`enabled != false`), in catalog order.
    /// Empty when the engine is unknown.
    pub fn models(&self, id: &str) -> Vec<&PpModel> {
        match self.provider(id) {
            Some(p) => p.visible_models().collect(),
            None => Vec::new(),
        }
    }

    /// Default model for an engine — `isDefault: true`, else the first enabled.
    pub fn default_model(&self, id: &str) -> Option<&PpModel> {
        self.provider(id).and_then(|p| p.default_model())
    }

    /// Look up a single visible model within an engine by model id
    /// (case-insensitive).
    pub fn model(&self, id: &str, model_id: &str) -> Option<&PpModel> {
        self.provider(id).and_then(|p| {
            p.visible_models()
                .find(|m| m.id.eq_ignore_ascii_case(model_id))
        })
    }

    /// The `X-LLM-Provider` header value for an engine id, or `None`.
    pub fn llm_provider(&self, id: &str) -> Option<&str> {
        self.provider(id).map(|p| p.llm_provider.as_str())
    }

    /// The `X-LLM-Model` header value for a (engine, model) pair, or `None`.
    /// Convenience over [`CloudPpCatalog::model`] + [`PpModel::model_header`].
    pub fn llm_model_header(&self, id: &str, model_id: &str) -> Option<&str> {
        self.model(id, model_id).map(|m| m.model_header())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn catalog() -> CloudPpCatalog {
        CloudPpCatalog::embedded().expect("embedded cloud-pp-catalog.json must parse")
    }

    #[test]
    fn embedded_catalog_parses() {
        let c = catalog();
        assert_eq!(c.version(), 1);
        assert!(c.providers().len() >= 5);
    }

    // --- Golden: enabled gate + headers (cerebras) --------------------------

    #[test]
    fn cerebras_enabled_with_headers() {
        let c = catalog();
        assert!(c.is_enabled("cerebras"));
        // X-LLM-Provider header.
        assert_eq!(c.llm_provider("cerebras"), Some("cerebras"));
        // Default model + X-LLM-Model header.
        let dm = c.default_model("cerebras").expect("cerebras default model");
        assert_eq!(dm.id, "gpt-oss-120b");
        assert_eq!(dm.model_header(), "gpt-oss-120b");
        assert_eq!(c.llm_model_header("cerebras", "gpt-oss-120b"), Some("gpt-oss-120b"));
        // Display pricing.
        assert_eq!(dm.price_per_m_input, Some(0.35));
        assert_eq!(dm.price_per_m_output, Some(0.75));
    }

    #[test]
    fn groq_model_header_carries_slash_prefix() {
        let c = catalog();
        // groq's model id IS the header (openai/gpt-oss-120b).
        assert_eq!(c.llm_provider("groq"), Some("groq"));
        assert_eq!(
            c.llm_model_header("groq", "openai/gpt-oss-120b"),
            Some("openai/gpt-oss-120b")
        );
    }

    #[test]
    fn anthropic_api_style_and_pricing() {
        let c = catalog();
        let p = c.provider("anthropic").unwrap();
        assert_eq!(p.api_style.as_deref(), Some("anthropic"));
        assert_eq!(p.llm_provider, "anthropic");
        let dm = c.default_model("anthropic").unwrap();
        assert_eq!(dm.id, "claude-haiku-4-5");
        assert_eq!(dm.price_per_m_input, Some(1.00));
        assert_eq!(dm.price_per_m_output, Some(5.00));
    }

    // --- Golden: case-insensitive lookup ------------------------------------

    #[test]
    fn case_insensitive_provider_lookup() {
        let c = catalog();
        assert!(c.provider("CEREBRAS").is_some());
        assert_eq!(c.llm_provider("Anthropic"), Some("anthropic"));
    }

    // --- Golden: picker filtering (all currently enabled) -------------------

    #[test]
    fn picker_includes_all_enabled_providers() {
        let c = catalog();
        let ids: Vec<&str> = c.picker_providers().map(|p| p.id.as_str()).collect();
        assert!(ids.contains(&"cerebras"));
        assert!(ids.contains(&"openai"));
        assert!(ids.contains(&"gemini"));
        // All shipped providers are enabled=true in v1.
        assert_eq!(ids.len(), c.providers().len());
    }

    #[test]
    fn multi_model_default_and_visible_models() {
        let c = catalog();
        // openai lists gpt-5-mini (default) + gpt-5-nano.
        let models = c.models("openai");
        assert_eq!(models.len(), 2);
        let dm = c.default_model("openai").unwrap();
        assert_eq!(dm.id, "gpt-5-mini");
        // Specific lookup.
        let nano = c.model("openai", "gpt-5-nano").unwrap();
        assert_eq!(nano.price_per_m_input, Some(0.05));
    }

    // --- Golden: disabled gate hides engine + model -------------------------

    #[test]
    fn disabled_engine_hidden_from_picker_and_lookups() {
        let json = r#"{
            "version": 1, "updated": "x",
            "providers": [
                {"id":"on","displayName":"On","llmProvider":"on","enabled":true,
                 "models":[{"id":"m1","displayName":"M1","isDefault":true}]},
                {"id":"off","displayName":"Off","llmProvider":"off","enabled":false,
                 "models":[{"id":"m2","displayName":"M2","isDefault":true}]}
            ]
        }"#;
        let c = CloudPpCatalog::parse(json).unwrap();
        // Disabled engine is hidden from the picker.
        let ids: Vec<&str> = c.picker_providers().map(|p| p.id.as_str()).collect();
        assert_eq!(ids, vec!["on"]);
        // is_enabled reflects the gate.
        assert!(c.is_enabled("on"));
        assert!(!c.is_enabled("off"));
        // But the provider is still resolvable by id (lookups don't filter the
        // engine itself — only the picker list does), matching both platforms.
        assert!(c.provider("off").is_some());
    }

    #[test]
    fn disabled_model_hidden_from_models_and_default() {
        let json = r#"{
            "version": 1, "updated": "x",
            "providers": [
                {"id":"e","displayName":"E","llmProvider":"e","enabled":true,
                 "models":[
                    {"id":"hidden","displayName":"H","isDefault":true,"enabled":false},
                    {"id":"shown","displayName":"S","enabled":true}
                 ]}
            ]
        }"#;
        let c = CloudPpCatalog::parse(json).unwrap();
        // Hidden model is filtered out.
        let models = c.models("e");
        assert_eq!(models.len(), 1);
        assert_eq!(models[0].id, "shown");
        // Default falls back to the first ENABLED model (the isDefault one is
        // hidden), matching macOS/Windows which filter before picking.
        assert_eq!(c.default_model("e").unwrap().id, "shown");
        assert!(c.model("e", "hidden").is_none());
    }

    #[test]
    fn missing_enabled_treated_as_enabled() {
        let json = r#"{
            "version": 1, "updated": "x",
            "providers": [
                {"id":"e","displayName":"E","llmProvider":"e",
                 "models":[{"id":"m","displayName":"M"}]}
            ]
        }"#;
        let c = CloudPpCatalog::parse(json).unwrap();
        assert!(c.is_enabled("e"));
        assert_eq!(c.picker_providers().count(), 1);
        assert_eq!(c.models("e").len(), 1);
    }

    // --- Misses --------------------------------------------------------------

    #[test]
    fn unknown_engine_safe_defaults() {
        let c = catalog();
        assert!(c.provider("nope").is_none());
        assert!(!c.is_enabled("nope"));
        assert_eq!(c.llm_provider("nope"), None);
        assert!(c.models("nope").is_empty());
        assert_eq!(c.default_model("nope"), None);
        assert_eq!(c.llm_model_header("nope", "x"), None);
    }

    #[test]
    fn malformed_json_is_error_not_panic() {
        assert!(CloudPpCatalog::parse("{ not json").is_err());
    }
}
