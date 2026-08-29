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
