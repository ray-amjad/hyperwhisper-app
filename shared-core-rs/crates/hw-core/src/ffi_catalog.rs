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

    fn classify(
        bundle_id: &str,
        process_name: &str,
        host: Option<&str>,
        title: &str,
    ) -> AppClassification {
        app_classify(
            bundle_id.to_string(),
            process_name.to_string(),
            host.map(str::to_string),
            title.to_string(),
        )
    }

    // =======================================================================
    // app-type classification
    // =======================================================================

    /// The browser host outranks every other signal, so a Claude tab inside a
    /// mail client is classified as an AI app, not as email.
    #[test]
    fn app_classify_prefers_the_host_over_the_bundle_process_and_title() {
        let c = classify("com.apple.mail", "WindowsTerminal", Some("claude.ai"), "1Password");
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
            (hw_catalog::AppType::Email, "Email", "email", "Email Client", "email"),
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
            (hw_catalog::AppType::Code, "Code", "code", "Code Editor", "code"),
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
            (hw_catalog::AppType::Other, "Other", "other", "Application", "text"),
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
        assert!(!models_available_via_hw_cloud("gemini".to_string(), HwKind::Text, id));
    }

    /// Custom vocabulary and HyperWhisper Cloud availability are two separate
    /// catalog columns; `gemini-2.0-flash` sets the first and clears the second.
    #[test]
    fn models_custom_vocabulary_and_cloud_availability_are_different_flags() {
        let id = "gemini-2.0-flash".to_string();
        assert!(models_supports_custom_vocabulary(
            "gemini".to_string(),
            HwKind::Voice,
            id.clone()
        ));
        assert!(!models_available_via_hw_cloud("gemini".to_string(), HwKind::Voice, id));
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
            vec![
                "ar", "de", "en", "es", "fr", "hi", "it", "ja", "ko", "nl", "pt", "ru", "zh"
            ]
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
        assert_eq!(cloud_stt_credits_per_minute("openaiWhisper".to_string()), 0.0);
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
        assert_eq!(cloud_stt_credits_per_minute("deepgramNova3".to_string()), 5.5);
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
        assert_eq!(cloud_stt_credits_per_minute("noSuchProvider".to_string()), 0.0);
    }

    /// AssemblyAI lists `universal-3-pro` first but flags `universal-3-5-pro`
    /// as the default, so the flag has to win over catalog order.
    #[test]
    fn cloud_stt_default_model_id_prefers_the_flagged_default_over_the_first_listed() {
        let models = cloud_stt_models("assemblyAI".to_string());
        assert_eq!(models.first().map(|m| m.id.as_str()), Some("universal-3-pro"));
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
        assert_eq!(cloud_stt_default_model_id("noSuchProvider".to_string()), None);
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
        assert!(cloud_stt_supports_custom_vocabulary("deepgramNova3".to_string()));
        assert!(!cloud_stt_supports_custom_vocabulary("noSuchProvider".to_string()));
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
        assert!(groq.len() > 50, "expected the full upstream list, got {}", groq.len());
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
        assert_eq!(google.accuracy_tier.as_deref(), Some("googleChirp3"));

        let byok = cloud_stt_normalize_cloud_provider(Some("deepgram".to_string()));
        assert_eq!(byok.provider.as_deref(), Some("deepgram"));
        assert_eq!(byok.accuracy_tier, None);

        let absent = cloud_stt_normalize_cloud_provider(None);
        assert_eq!(absent.provider, None);
        assert_eq!(absent.accuracy_tier, None);
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
        assert_eq!(ids, vec!["gpt-5-mini".to_string(), "gpt-5-nano".to_string()]);
        assert!(cloud_pp_models("noSuchEngine".to_string()).is_empty());
    }
}
