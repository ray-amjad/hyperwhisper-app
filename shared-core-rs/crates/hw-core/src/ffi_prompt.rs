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
