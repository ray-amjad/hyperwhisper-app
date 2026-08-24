//! UniFFI surface for the M1b prompt builder (`hw_text::prompt`).
//!
//! Mirrors `Preset`, `HwAppType`, `HwEnglishSpelling`, and `PromptContext` as
//! UniFFI records/enums (the leaf crate stays uniffi-free), with `From`
//! conversions into the leaf types, and thin `#[uniffi::export]` wrappers over
//! `build_system_prompt`, `build_system_info`, `sanitize_vocabulary_word`, plus
//! the `from_raw` / `prompt_value` parser helpers (UniFFI enums can't carry the
//! leaf's inherent methods, so they're exposed as free functions).

/// Preset (mode) selector. Mirrors `hw_text::Preset`.
#[derive(uniffi::Enum)]
pub enum Preset {
    Hyper,
    Message,
    Mail,
    Note,
    Meeting,
    Code,
    Custom,
}

impl From<Preset> for hw_text::Preset {
    fn from(p: Preset) -> Self {
        match p {
            Preset::Hyper => hw_text::Preset::Hyper,
            Preset::Message => hw_text::Preset::Message,
            Preset::Mail => hw_text::Preset::Mail,
            Preset::Note => hw_text::Preset::Note,
            Preset::Meeting => hw_text::Preset::Meeting,
            Preset::Code => hw_text::Preset::Code,
            Preset::Custom => hw_text::Preset::Custom,
        }
    }
}

impl From<hw_text::Preset> for Preset {
    fn from(p: hw_text::Preset) -> Self {
        match p {
            hw_text::Preset::Hyper => Preset::Hyper,
            hw_text::Preset::Message => Preset::Message,
            hw_text::Preset::Mail => Preset::Mail,
            hw_text::Preset::Note => Preset::Note,
            hw_text::Preset::Meeting => Preset::Meeting,
            hw_text::Preset::Code => Preset::Code,
            hw_text::Preset::Custom => Preset::Custom,
        }
    }
}

/// Detected application type for the contextual-formatting block. Mirrors
/// `hw_text::AppType`.
#[derive(uniffi::Enum)]
pub enum HwAppType {
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

impl From<HwAppType> for hw_text::AppType {
    fn from(a: HwAppType) -> Self {
        match a {
            HwAppType::Email => hw_text::AppType::Email,
            HwAppType::Ai => hw_text::AppType::Ai,
            HwAppType::WorkMessaging => hw_text::AppType::WorkMessaging,
            HwAppType::PersonalMessaging => hw_text::AppType::PersonalMessaging,
            HwAppType::Document => hw_text::AppType::Document,
            HwAppType::Code => hw_text::AppType::Code,
            HwAppType::Terminal => hw_text::AppType::Terminal,
            HwAppType::Sensitive => hw_text::AppType::Sensitive,
            HwAppType::Other => hw_text::AppType::Other,
        }
    }
}

impl From<hw_text::AppType> for HwAppType {
    fn from(a: hw_text::AppType) -> Self {
        match a {
            hw_text::AppType::Email => HwAppType::Email,
            hw_text::AppType::Ai => HwAppType::Ai,
            hw_text::AppType::WorkMessaging => HwAppType::WorkMessaging,
            hw_text::AppType::PersonalMessaging => HwAppType::PersonalMessaging,
            hw_text::AppType::Document => HwAppType::Document,
            hw_text::AppType::Code => HwAppType::Code,
            hw_text::AppType::Terminal => HwAppType::Terminal,
            hw_text::AppType::Sensitive => HwAppType::Sensitive,
            hw_text::AppType::Other => HwAppType::Other,
        }
    }
}

/// English-spelling variant for the `<SPELLING>` / `<DATE_FORMAT>` block.
/// Mirrors `hw_text::EnglishSpelling`.
#[derive(uniffi::Enum)]
pub enum HwEnglishSpelling {
    None,
    American,
    British,
    Australian,
    Canadian,
}

impl From<HwEnglishSpelling> for hw_text::EnglishSpelling {
    fn from(s: HwEnglishSpelling) -> Self {
        match s {
            HwEnglishSpelling::None => hw_text::EnglishSpelling::None,
            HwEnglishSpelling::American => hw_text::EnglishSpelling::American,
            HwEnglishSpelling::British => hw_text::EnglishSpelling::British,
            HwEnglishSpelling::Australian => hw_text::EnglishSpelling::Australian,
            HwEnglishSpelling::Canadian => hw_text::EnglishSpelling::Canadian,
        }
    }
}

impl From<hw_text::EnglishSpelling> for HwEnglishSpelling {
    fn from(s: hw_text::EnglishSpelling) -> Self {
        match s {
            hw_text::EnglishSpelling::None => HwEnglishSpelling::None,
            hw_text::EnglishSpelling::American => HwEnglishSpelling::American,
            hw_text::EnglishSpelling::British => HwEnglishSpelling::British,
            hw_text::EnglishSpelling::Australian => HwEnglishSpelling::Australian,
            hw_text::EnglishSpelling::Canadian => HwEnglishSpelling::Canadian,
        }
    }
}

/// All inputs needed to assemble the system prompt and system info. Mirrors
/// `hw_text::PromptContext` field-for-field.
#[derive(uniffi::Record)]
pub struct PromptContext {
    pub preset: Preset,
    pub custom_instructions: String,

    pub english_spelling: HwEnglishSpelling,
    pub language: String,

    pub user_system_prompt: String,

    pub app_type: HwAppType,
    pub app_name: String,
    pub category: String,
    pub description: String,
    pub text_format: String,
    pub browser_host: String,
    pub browser_tab_title: String,
    pub focused_element: String,
    pub focused_content: String,
    pub screen_ocr_text: String,
    pub app_type_confidence: String,
    pub app_type_source: String,
    pub has_application_context: bool,

    pub vocabulary_words: Vec<String>,

    pub time: String,
    pub timezone: String,
    pub locale: String,
    pub computer_name: String,

    pub punctuation: bool,
    pub capitalization: bool,
    pub profanity_filter: bool,
}

impl From<PromptContext> for hw_text::PromptContext {
    fn from(c: PromptContext) -> Self {
        hw_text::PromptContext {
            preset: c.preset.into(),
            custom_instructions: c.custom_instructions,
            english_spelling: c.english_spelling.into(),
            language: c.language,
            user_system_prompt: c.user_system_prompt,
            app_type: c.app_type.into(),
            app_name: c.app_name,
            category: c.category,
            description: c.description,
            text_format: c.text_format,
            browser_host: c.browser_host,
            browser_tab_title: c.browser_tab_title,
            focused_element: c.focused_element,
            focused_content: c.focused_content,
            screen_ocr_text: c.screen_ocr_text,
            app_type_confidence: c.app_type_confidence,
            app_type_source: c.app_type_source,
            has_application_context: c.has_application_context,
            vocabulary_words: c.vocabulary_words,
            time: c.time,
            timezone: c.timezone,
            locale: c.locale,
            computer_name: c.computer_name,
            punctuation: c.punctuation,
            capitalization: c.capitalization,
            profanity_filter: c.profanity_filter,
        }
    }
}

// --- exported functions ---

/// Build the STATIC system prompt for the given context.
#[uniffi::export]
pub fn build_system_prompt(ctx: PromptContext) -> String {
    hw_text::build_system_prompt(&ctx.into())
}

/// Build the DYNAMIC system-info block (prepended to the user message).
#[uniffi::export]
pub fn build_system_info(ctx: PromptContext) -> String {
    hw_text::build_system_info(&ctx.into())
}

/// Neutralize a vocabulary word for safe interpolation into the prompt.
#[uniffi::export]
pub fn sanitize_vocabulary_word(word: String) -> String {
    hw_text::sanitize_vocabulary_word(&word)
}

/// Parse a raw `mode.preset` string. Unknown/empty → `Hyper`.
#[uniffi::export]
pub fn preset_from_raw(raw: String) -> Preset {
    hw_text::Preset::from_raw(&raw).into()
}

/// Parse an app-type token (macOS rawValue, promptValue, or Windows
/// PascalCase). Unknown → `Other`.
#[uniffi::export]
pub fn app_type_from_raw(raw: String) -> HwAppType {
    hw_text::AppType::from_raw(&raw).into()
}

/// The value emitted in `<APP_TYPE>` for an app type.
#[uniffi::export]
pub fn app_type_prompt_value(app_type: HwAppType) -> String {
    let leaf: hw_text::AppType = app_type.into();
    leaf.prompt_value().to_string()
}

/// Parse a raw `mode.englishSpelling` string.
#[uniffi::export]
pub fn english_spelling_from_raw(raw: String) -> HwEnglishSpelling {
    hw_text::EnglishSpelling::from_raw(&raw).into()
}

/// The spelling variant to **seed** into a new mode, from an ISO 3166-1
/// alpha-2 region code. Unknown / empty / `None` → `American`, never `None`.
///
/// `Option<String>` so Rust owns the nil case: every host reads a nullable
/// region (`Locale.current.region?.identifier`,
/// `RegionInfo.CurrentRegion` behind a try/catch) and all three assert that a
/// missing region seeds American.
///
/// This is a *seeding* function and is not the inverse of
/// [`english_spelling_from_raw`]: `HwEnglishSpelling::None` means "no spelling
/// instruction at all", which is never the right thing to seed.
#[uniffi::export]
pub fn english_spelling_for_region(region: Option<String>) -> HwEnglishSpelling {
    hw_text::EnglishSpelling::for_region(region.as_deref()).into()
}

/// The raw `mode.englishSpelling` token for a variant — the inverse of
/// [`english_spelling_from_raw`]. `None` → `""` (no spelling block), not
/// `"american"`.
#[uniffi::export]
pub fn english_spelling_raw_value(spelling: HwEnglishSpelling) -> String {
    let leaf: hw_text::EnglishSpelling = spelling.into();
    leaf.raw_value().to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    // The `From` impls in this file ARE the code under test, so no test may use
    // them to state its own expectation. Two independent yardsticks are used:
    //   * leaf -> FFI direction: a hand-written tag function over the FFI enum.
    //   * FFI -> leaf direction: the leaf's own observable output (the prompt
    //     text, or `prompt_value`), pinned to literals.
    // A pair of arms swapped in BOTH directions still fails, because neither
    // yardstick goes back through a conversion.

    /// All-empty context. Field-for-field the same as
    /// `hw_text::PromptContext::default()`, so the two are directly comparable.
    fn ctx() -> PromptContext {
        PromptContext {
            preset: Preset::Hyper,
            custom_instructions: String::new(),
            english_spelling: HwEnglishSpelling::None,
            language: String::new(),
            user_system_prompt: String::new(),
            app_type: HwAppType::Other,
            app_name: String::new(),
            category: String::new(),
            description: String::new(),
            text_format: String::new(),
            browser_host: String::new(),
            browser_tab_title: String::new(),
            focused_element: String::new(),
            focused_content: String::new(),
            screen_ocr_text: String::new(),
            app_type_confidence: String::new(),
            app_type_source: String::new(),
            has_application_context: false,
            vocabulary_words: Vec::new(),
            time: String::new(),
            timezone: String::new(),
            locale: String::new(),
            computer_name: String::new(),
            punctuation: false,
            capitalization: false,
            profanity_filter: false,
        }
    }

    fn prompt_for(preset: Preset) -> String {
        build_system_prompt(PromptContext { preset, ..ctx() })
    }

    fn leaf_prompt_for(preset: hw_text::Preset) -> String {
        hw_text::build_system_prompt(&hw_text::PromptContext {
            preset,
            ..Default::default()
        })
    }

    fn preset_tag(p: &Preset) -> &'static str {
        match p {
            Preset::Hyper => "hyper",
            Preset::Message => "message",
            Preset::Mail => "mail",
            Preset::Note => "note",
            Preset::Meeting => "meeting",
            Preset::Code => "code",
            Preset::Custom => "custom",
        }
    }

    fn app_type_tag(a: &HwAppType) -> &'static str {
        match a {
            HwAppType::Email => "Email",
            HwAppType::Ai => "Ai",
            HwAppType::WorkMessaging => "WorkMessaging",
            HwAppType::PersonalMessaging => "PersonalMessaging",
            HwAppType::Document => "Document",
            HwAppType::Code => "Code",
            HwAppType::Terminal => "Terminal",
            HwAppType::Sensitive => "Sensitive",
            HwAppType::Other => "Other",
        }
    }

    fn spelling_tag(s: &HwEnglishSpelling) -> &'static str {
        match s {
            HwEnglishSpelling::None => "None",
            HwEnglishSpelling::American => "American",
            HwEnglishSpelling::British => "British",
            HwEnglishSpelling::Australian => "Australian",
            HwEnglishSpelling::Canadian => "Canadian",
        }
    }

    // --- PromptContext record -> leaf record --------------------------------

    #[test]
    fn empty_ffi_context_equals_the_leaf_default_context() {
        let leaf = hw_text::PromptContext::default();
        assert_eq!(
            build_system_prompt(ctx()),
            hw_text::build_system_prompt(&leaf)
        );
        assert_eq!(build_system_info(ctx()), hw_text::build_system_info(&leaf));
    }

    #[test]
    fn every_runtime_and_context_string_lands_in_its_own_tag() {
        // Each field gets a different sentinel, so any two fields wired to the
        // wrong leaf field fail this test.
        let info = build_system_info(PromptContext {
            app_type: HwAppType::Document,
            has_application_context: true,
            app_name: "app-name-here".to_string(),
            category: "category-here".to_string(),
            description: "description-here".to_string(),
            text_format: "text-format-here".to_string(),
            browser_host: "browser-host-here".to_string(),
            browser_tab_title: "tab-title-here".to_string(),
            focused_element: "focused-element-here".to_string(),
            focused_content: "focused-content-here".to_string(),
            app_type_confidence: "confidence-here".to_string(),
            app_type_source: "source-here".to_string(),
            time: "time-here".to_string(),
            timezone: "timezone-here".to_string(),
            locale: "locale-here".to_string(),
            computer_name: "computer-here".to_string(),
            language: "Language-Here".to_string(),
            ..ctx()
        });

        assert!(info.contains("<TIME>time-here</TIME>"), "{info}");
        assert!(
            info.contains("<TIMEZONE>timezone-here</TIMEZONE>"),
            "{info}"
        );
        assert!(info.contains("<LOCALE>locale-here</LOCALE>"), "{info}");
        assert!(
            info.contains("<COMPUTER>computer-here</COMPUTER>"),
            "{info}"
        );
        assert!(info.contains("<APP>app-name-here</APP>"), "{info}");
        assert!(info.contains("<TAB>tab-title-here</TAB>"), "{info}");
        assert!(
            info.contains("<BROWSER_HOST>browser-host-here</BROWSER_HOST>"),
            "{info}"
        );
        assert!(
            info.contains("<APP_TYPE_CONFIDENCE>confidence-here</APP_TYPE_CONFIDENCE>"),
            "{info}"
        );
        assert!(
            info.contains("<APP_TYPE_SOURCE>source-here</APP_TYPE_SOURCE>"),
            "{info}"
        );
        assert!(
            info.contains("<CATEGORY>category-here</CATEGORY>"),
            "{info}"
        );
        assert!(
            info.contains("<DESCRIPTION>description-here</DESCRIPTION>"),
            "{info}"
        );
        assert!(
            info.contains("<TEXT_FORMAT>text-format-here</TEXT_FORMAT>"),
            "{info}"
        );
        assert!(
            info.contains("<FOCUSED_ELEMENT>focused-element-here</FOCUSED_ELEMENT>"),
            "{info}"
        );
        assert!(
            info.contains("<FOCUSED_CONTENT>focused-content-here</FOCUSED_CONTENT>"),
            "{info}"
        );
        assert!(
            info.contains("- Output ALL text in Language-Here, including headings"),
            "{info}"
        );
    }

    #[test]
    fn screen_ocr_text_and_the_context_flag_are_carried_over() {
        let with_ocr = build_system_info(PromptContext {
            has_application_context: true,
            app_name: "app-name-here".to_string(),
            screen_ocr_text: "ocr-text-here".to_string(),
            ..ctx()
        });
        assert!(
            with_ocr.contains("<SCREEN_CONTEXT>\nocr-text-here\n</SCREEN_CONTEXT>"),
            "{with_ocr}"
        );

        // has_application_context = false must drop the whole block, so a field
        // wired to the wrong bool shows up here.
        let without = build_system_info(PromptContext {
            has_application_context: false,
            app_name: "app-name-here".to_string(),
            screen_ocr_text: "ocr-text-here".to_string(),
            ..ctx()
        });
        assert!(!without.contains("<APPLICATION_CONTEXT>"), "{without}");
        assert!(!without.contains("app-name-here"), "{without}");
    }

    #[test]
    fn each_mode_flag_carries_only_its_own_flag_text() {
        const PUNCTUATION: &str = "- Add appropriate punctuation (periods, commas, etc.).";
        const CAPITALIZATION: &str = "- Use appropriate capitalization throughout.";
        const PROFANITY: &str = "- Remove profanity.";

        let punctuation = build_system_prompt(PromptContext {
            punctuation: true,
            ..ctx()
        });
        assert!(punctuation.contains(PUNCTUATION), "{punctuation}");
        assert!(!punctuation.contains(CAPITALIZATION));
        assert!(!punctuation.contains(PROFANITY));

        let capitalization = build_system_prompt(PromptContext {
            capitalization: true,
            ..ctx()
        });
        assert!(capitalization.contains(CAPITALIZATION), "{capitalization}");
        assert!(!capitalization.contains(PUNCTUATION));
        assert!(!capitalization.contains(PROFANITY));

        let profanity = build_system_prompt(PromptContext {
            profanity_filter: true,
            ..ctx()
        });
        assert!(profanity.contains(PROFANITY), "{profanity}");
        assert!(!profanity.contains(PUNCTUATION));
        assert!(!profanity.contains(CAPITALIZATION));
    }

    #[test]
    fn user_system_prompt_and_custom_instructions_reach_their_own_slots() {
        let prompt = build_system_prompt(PromptContext {
            preset: Preset::Custom,
            user_system_prompt: "  user-prompt-here  ".to_string(),
            custom_instructions: "custom-instructions-here".to_string(),
            ..ctx()
        });
        assert!(
            prompt.contains("<USER_SYSTEM_PROMPT>\nuser-prompt-here\n</USER_SYSTEM_PROMPT>"),
            "{prompt}"
        );
        assert!(prompt.contains("custom-instructions-here"), "{prompt}");

        // Empty custom instructions fall back to the shared default, which
        // proves the field is read rather than ignored.
        let fallback = prompt_for(Preset::Custom);
        assert!(
            fallback.contains("Process the text according to your best judgment."),
            "{fallback}"
        );
        // The override directive names the tag, so the CLOSING tag is what
        // shows whether an empty user prompt was omitted.
        assert!(!fallback.contains("</USER_SYSTEM_PROMPT>"), "{fallback}");
    }

    #[test]
    fn vocabulary_words_are_forwarded_and_sanitized() {
        let info = build_system_info(PromptContext {
            vocabulary_words: vec![
                "Kubernetes".to_string(),
                "<script>alert".to_string(),
                "   ".to_string(),
                "multi   word    term".to_string(),
            ],
            ..ctx()
        });
        assert!(
            info.contains("<CUSTOM_VOCABULARY>\nKubernetes, scriptalert, multi word term\n</CUSTOM_VOCABULARY>"),
            "{info}"
        );
    }

    // --- exported free functions --------------------------------------------

    #[test]
    fn build_system_prompt_and_build_system_info_are_wired_to_different_builders() {
        let prompt = build_system_prompt(PromptContext {
            time: "time-here".to_string(),
            ..ctx()
        });
        let info = build_system_info(PromptContext {
            time: "time-here".to_string(),
            ..ctx()
        });

        // The static prompt must not carry the per-request runtime values, or
        // provider prompt caching breaks.
        assert!(prompt.starts_with("<USER_PROMPT_OVERRIDES>"), "{prompt}");
        assert!(!prompt.contains("time-here"), "{prompt}");
        assert!(info.starts_with("<SYSTEM_INFO>"), "{info}");
        assert!(!info.contains("<USER_PROMPT_OVERRIDES>"), "{info}");
    }

    #[test]
    fn sanitize_vocabulary_word_strips_brackets_collapses_space_and_caps_length() {
        assert_eq!(
            sanitize_vocabulary_word("<b>bold</b>".to_string()),
            "bbold/b"
        );
        assert_eq!(
            sanitize_vocabulary_word("  spaced \n out \t term ".to_string()),
            "spaced out term"
        );
        assert_eq!(sanitize_vocabulary_word("   ".to_string()), "");

        let long = "a".repeat(hw_text::prompt::MAX_VOCABULARY_TERM_CHARS + 20);
        assert_eq!(
            sanitize_vocabulary_word(long).chars().count(),
            hw_text::prompt::MAX_VOCABULARY_TERM_CHARS
        );
    }

    // --- Preset -------------------------------------------------------------

    #[test]
    fn each_preset_arm_selects_the_matching_leaf_preset() {
        assert_eq!(
            prompt_for(Preset::Hyper),
            leaf_prompt_for(hw_text::Preset::Hyper)
        );
        assert_eq!(
            prompt_for(Preset::Message),
            leaf_prompt_for(hw_text::Preset::Message)
        );
        assert_eq!(
            prompt_for(Preset::Mail),
            leaf_prompt_for(hw_text::Preset::Mail)
        );
        assert_eq!(
            prompt_for(Preset::Note),
            leaf_prompt_for(hw_text::Preset::Note)
        );
        assert_eq!(
            prompt_for(Preset::Meeting),
            leaf_prompt_for(hw_text::Preset::Meeting)
        );
        assert_eq!(
            prompt_for(Preset::Code),
            leaf_prompt_for(hw_text::Preset::Code)
        );
        assert_eq!(
            prompt_for(Preset::Custom),
            leaf_prompt_for(hw_text::Preset::Custom)
        );
    }

    #[test]
    fn the_seven_presets_produce_seven_different_prompts() {
        // Without this, the equality checks above would still pass if every arm
        // collapsed onto one leaf preset.
        let prompts = [
            prompt_for(Preset::Hyper),
            prompt_for(Preset::Message),
            prompt_for(Preset::Mail),
            prompt_for(Preset::Note),
            prompt_for(Preset::Meeting),
            prompt_for(Preset::Code),
            prompt_for(Preset::Custom),
        ];
        for i in 0..prompts.len() {
            for j in (i + 1)..prompts.len() {
                assert_ne!(prompts[i], prompts[j], "presets {i} and {j} are identical");
            }
        }
    }

    #[test]
    fn preset_from_raw_maps_every_token_and_falls_back_to_hyper() {
        for (raw, want) in [
            ("hyper", "hyper"),
            ("message", "message"),
            ("mail", "mail"),
            ("note", "note"),
            ("meeting", "meeting"),
            ("code", "code"),
            ("custom", "custom"),
            ("", "hyper"),
            ("Mail", "hyper"),
            ("not-a-preset", "hyper"),
        ] {
            assert_eq!(
                preset_tag(&preset_from_raw(raw.to_string())),
                want,
                "preset_from_raw({raw:?})"
            );
        }
    }

    // --- HwAppType ----------------------------------------------------------

    #[test]
    fn app_type_prompt_value_pins_every_arm() {
        assert_eq!(app_type_prompt_value(HwAppType::Email), "email");
        assert_eq!(app_type_prompt_value(HwAppType::Ai), "ai");
        assert_eq!(
            app_type_prompt_value(HwAppType::WorkMessaging),
            "work_messaging"
        );
        assert_eq!(
            app_type_prompt_value(HwAppType::PersonalMessaging),
            "personal_messaging"
        );
        assert_eq!(app_type_prompt_value(HwAppType::Document), "document");
        assert_eq!(app_type_prompt_value(HwAppType::Code), "code");
        assert_eq!(app_type_prompt_value(HwAppType::Terminal), "terminal");
        assert_eq!(app_type_prompt_value(HwAppType::Sensitive), "sensitive");
        assert_eq!(app_type_prompt_value(HwAppType::Other), "other");
    }

    #[test]
    fn app_type_arms_select_the_matching_contextual_block() {
        // Second, independent observable for the HwAppType -> leaf direction:
        // the contextual block the leaf injects into the hyper preset.
        for (app_type, want) in [
            (HwAppType::Email, "<EMAIL_CONTEXT_DETECTED>"),
            (HwAppType::WorkMessaging, "<WORK_MESSAGE_CONTEXT_DETECTED>"),
            (
                HwAppType::PersonalMessaging,
                "<PERSONAL_MESSAGE_CONTEXT_DETECTED>",
            ),
            (HwAppType::Document, "<DOCUMENT_CONTEXT_DETECTED>"),
            (HwAppType::Code, "<CODE_CONTEXT_DETECTED>"),
            (HwAppType::Terminal, "<TERMINAL_CONTEXT_DETECTED>"),
        ] {
            let prompt = build_system_prompt(PromptContext { app_type, ..ctx() });
            assert!(prompt.contains(want), "expected {want} in:\n{prompt}");
        }

        // Sensitive and Other get no block at all.
        for app_type in [HwAppType::Sensitive, HwAppType::Other] {
            let prompt = build_system_prompt(PromptContext { app_type, ..ctx() });
            assert!(!prompt.contains("_CONTEXT_DETECTED>"), "{prompt}");
        }
    }

    #[test]
    fn sensitive_app_type_suppresses_focused_content_and_screen_text() {
        let info = build_system_info(PromptContext {
            app_type: HwAppType::Sensitive,
            has_application_context: true,
            app_name: "app-name-here".to_string(),
            focused_content: "focused-content-here".to_string(),
            screen_ocr_text: "ocr-text-here".to_string(),
            ..ctx()
        });
        assert!(info.contains("<APP_TYPE>sensitive</APP_TYPE>"), "{info}");
        assert!(!info.contains("focused-content-here"), "{info}");
        assert!(!info.contains("ocr-text-here"), "{info}");
    }

    #[test]
    fn app_type_from_raw_accepts_every_platform_serialization() {
        for (raw, want) in [
            ("email", "Email"),
            ("Email", "Email"),
            ("ai", "Ai"),
            ("AI", "Ai"),
            ("workMessaging", "WorkMessaging"),
            ("work_messaging", "WorkMessaging"),
            ("WorkMessaging", "WorkMessaging"),
            ("personalMessaging", "PersonalMessaging"),
            ("personal_messaging", "PersonalMessaging"),
            ("PersonalMessaging", "PersonalMessaging"),
            ("document", "Document"),
            ("code", "Code"),
            ("terminal", "Terminal"),
            ("sensitive", "Sensitive"),
            ("", "Other"),
            ("not-an-app-type", "Other"),
        ] {
            assert_eq!(
                app_type_tag(&app_type_from_raw(raw.to_string())),
                want,
                "app_type_from_raw({raw:?})"
            );
        }
    }

    // --- HwEnglishSpelling --------------------------------------------------

    #[test]
    fn each_spelling_arm_selects_the_matching_spelling_block() {
        for (spelling, want) in [
            (
                HwEnglishSpelling::British,
                "<SPELLING>British English (e.g., colour, realise",
            ),
            (
                HwEnglishSpelling::Australian,
                "<SPELLING>Australian English (e.g., colour, realise",
            ),
            (
                HwEnglishSpelling::Canadian,
                "<SPELLING>Canadian English (e.g., colour, realize",
            ),
            (
                HwEnglishSpelling::American,
                "<SPELLING>American English (e.g., color, realize",
            ),
        ] {
            let info = build_system_info(PromptContext {
                english_spelling: spelling,
                ..ctx()
            });
            assert!(info.contains(want), "expected {want} in:\n{info}");
        }

        let none = build_system_info(PromptContext {
            english_spelling: HwEnglishSpelling::None,
            ..ctx()
        });
        assert!(!none.contains("<SPELLING>"), "{none}");
        assert!(!none.contains("<DATE_FORMAT>"), "{none}");
    }

    #[test]
    fn british_and_australian_differ_by_their_date_format_line() {
        // The two share their spelling examples, so the date line is what keeps
        // a swap of those two arms detectable.
        let british = build_system_info(PromptContext {
            english_spelling: HwEnglishSpelling::British,
            ..ctx()
        });
        let australian = build_system_info(PromptContext {
            english_spelling: HwEnglishSpelling::Australian,
            ..ctx()
        });
        assert!(british.contains("Use British date format"), "{british}");
        assert!(
            australian.contains("Use Australian date format"),
            "{australian}"
        );
    }

    #[test]
    fn english_spelling_from_raw_maps_every_token() {
        for (raw, want) in [
            ("british", "British"),
            ("australian", "Australian"),
            ("canadian", "Canadian"),
            ("american", "American"),
            ("", "None"),
            // macOS treats any non-empty unknown value as American.
            ("en-GB", "American"),
        ] {
            assert_eq!(
                spelling_tag(&english_spelling_from_raw(raw.to_string())),
                want,
                "english_spelling_from_raw({raw:?})"
            );
        }
    }

    #[test]
    fn english_spelling_for_region_maps_each_table() {
        for (region, want) in [
            ("GB", "British"),
            ("IE", "British"),
            ("ZA", "British"),
            ("IN", "British"),
            ("NZ", "British"),
            ("AU", "Australian"),
            ("CC", "Australian"),
            ("CA", "Canadian"),
            ("US", "American"),
            ("JP", "American"),
            ("ZZ", "American"),
            // Case and surrounding whitespace are normalized away.
            ("gb", "British"),
            (" au ", "Australian"),
            ("\nca\n", "Canadian"),
        ] {
            assert_eq!(
                spelling_tag(&english_spelling_for_region(Some(region.to_string()))),
                want,
                "english_spelling_for_region({region:?})"
            );
        }
    }

    /// Seeding a mode must always yield a concrete variant. `None` means "emit
    /// no spelling instruction at all" and is reachable only from an explicit
    /// empty `mode.englishSpelling` — never from a region lookup.
    #[test]
    fn english_spelling_for_region_seeds_american_never_none() {
        for region in [None, Some(String::new()), Some("   ".to_string())] {
            assert_eq!(
                spelling_tag(&english_spelling_for_region(region.clone())),
                "American",
                "english_spelling_for_region({region:?}) must seed American"
            );
        }
    }

    #[test]
    fn english_spelling_raw_value_maps_every_variant() {
        for (spelling, want) in [
            (HwEnglishSpelling::None, ""),
            (HwEnglishSpelling::American, "american"),
            (HwEnglishSpelling::British, "british"),
            (HwEnglishSpelling::Australian, "australian"),
            (HwEnglishSpelling::Canadian, "canadian"),
        ] {
            let tag = spelling_tag(&spelling);
            assert_eq!(
                english_spelling_raw_value(spelling),
                want,
                "english_spelling_raw_value({tag})"
            );
        }
    }

    /// The two-call bridge the hosts use in Phase 2:
    /// `raw_value(for_region(code))` must produce a token `from_raw` accepts.
    #[test]
    fn for_region_then_raw_value_round_trips_through_from_raw() {
        for region in ["GB", "AU", "CA", "US", "", "nonsense"] {
            let seeded = english_spelling_for_region(Some(region.to_string()));
            let seeded_tag = spelling_tag(&seeded);
            let raw = english_spelling_raw_value(seeded);
            assert!(
                !raw.is_empty(),
                "a seeded region must never produce the empty token: {region:?}"
            );
            assert_eq!(
                spelling_tag(&english_spelling_from_raw(raw.clone())),
                seeded_tag,
                "round trip for {region:?} via {raw:?}"
            );
        }
    }
}
