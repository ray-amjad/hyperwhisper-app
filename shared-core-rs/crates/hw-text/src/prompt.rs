//! Prompt builder (M1b) — assembles the static post-processing **system prompt**
//! and the per-request dynamic **system info** block from the shared prompt
//! templates in `shared-prompts/`.
//!
//! Ports macOS `PromptBuilder.swift` + `ApplicationContextGatherer.formatContextForPrompt`
//! and Windows `PromptBuilder.cs`. Everything platform-runtime (time, timezone,
//! locale, computer name, app context, vocabulary list, mode flags, spelling,
//! language) is PASSED IN via [`PromptContext`] — this module has NO clock, NO
//! I/O, NO RNG, so the output is fully deterministic and golden-testable.
//!
//! ## Template embedding
//! The shared templates are compiled into the binary with `include_str!`, so the
//! Rust core does not read the `shared-prompts/` bundle at runtime (the native
//! apps still ship it as a bundle resource, but the Rust core is self-contained).
//!
//! ## Cross-platform unification choices
//! macOS is the verified platform; where Windows diverges we adopt macOS:
//! - **XML escaping**: macOS escapes `&`/`<`/`>` in every interpolated context
//!   field (window/tab titles, OCR text, focused content) as a prompt-injection
//!   defense; Windows interpolates raw. We adopt the macOS escaping
//!   (`ApplicationContextGatherer.xmlEscaped`, lines 221-225). DIVERGENCE FIXED.
//! - **Vocabulary sanitization**: macOS strips `<`/`>` and collapses whitespace
//!   per word (`sanitizeVocabularyWord`, lines 300-306); Windows only filters
//!   empties. We adopt the macOS sanitization. DIVERGENCE FIXED.
//! - **Assembly order**: override-directive → user-system-prompt → anti-reply →
//!   number-formatting → preset (+contextual block) → mode-flags. This matches
//!   macOS `finalizePrompt` (lines 308-320) and Windows `FinalizePrompt`
//!   (lines 309-330), which agree on order.

// ---------------------------------------------------------------------------
// Embedded shared-prompts templates (compiled in via include_str!)
// ---------------------------------------------------------------------------
// Path is relative to THIS source file: crates/hw-text/src/prompt.rs ->
// ../../../../shared-prompts/ (repo root).
macro_rules! shared {
    ($sub:literal) => {
        include_str!(concat!("../../../../shared-prompts/", $sub))
    };
}

// Presets
const PRESET_HYPER: &str = shared!("presets/hyper.txt");
const PRESET_MESSAGE: &str = shared!("presets/message.txt");
const PRESET_MAIL: &str = shared!("presets/mail.txt");
const PRESET_NOTE: &str = shared!("presets/note.txt");
const PRESET_MEETING: &str = shared!("presets/meeting.txt");
const PRESET_CODE: &str = shared!("presets/code.txt");
const PRESET_CUSTOM: &str = shared!("presets/custom.txt");

// Contextual blocks
const CTX_EMAIL: &str = shared!("contextual/email.txt");
const CTX_WORK_MESSAGE: &str = shared!("contextual/work-message.txt");
const CTX_PERSONAL_MESSAGE: &str = shared!("contextual/personal-message.txt");
const CTX_DOCUMENT: &str = shared!("contextual/document.txt");
const CTX_CODE: &str = shared!("contextual/code.txt");
const CTX_TERMINAL: &str = shared!("contextual/terminal.txt");

// Fragments
const FRAG_OVERRIDE: &str = shared!("fragments/override-directive.txt");
const FRAG_ANTI_REPLY: &str = shared!("fragments/anti-reply-directive.txt");
const FRAG_NUMBER_FORMATTING: &str = shared!("fragments/number-formatting.txt");
const FRAG_EMAIL_RULES: &str = shared!("fragments/email-formatting-rules.txt");

// Flags
const FLAG_PUNCTUATION: &str = shared!("flags/punctuation.txt");
const FLAG_CAPITALIZATION: &str = shared!("flags/capitalization.txt");
const FLAG_PROFANITY: &str = shared!("flags/profanity-filter.txt");

// ---------------------------------------------------------------------------
// Public data model (POD — all values supplied by the platform)
// ---------------------------------------------------------------------------

/// Preset (mode) selector. Mirrors macOS `PresetType` / Windows preset string.
/// Parsed from the platform's `mode.preset` string via [`Preset::from_raw`];
/// an unknown/missing value falls back to [`Preset::Hyper`] (matching the
/// macOS `systemPrompt` fallback).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Preset {
    /// Fallback preset (macOS `systemPrompt` defaults to hyper).
    #[default]
    Hyper,
    Message,
    Mail,
    Note,
    Meeting,
    Code,
    Custom,
}

impl Preset {
    /// Map a raw preset string (`mode.preset`) to a [`Preset`]. Unknown or empty
    /// → `Hyper`, matching macOS `PresetType(rawValue:)` fallback in `systemPrompt`.
    pub fn from_raw(raw: &str) -> Preset {
        match raw {
            "message" => Preset::Message,
            "mail" => Preset::Mail,
            "note" => Preset::Note,
            "meeting" => Preset::Meeting,
            "code" => Preset::Code,
            "custom" => Preset::Custom,
            "hyper" => Preset::Hyper,
            _ => Preset::Hyper,
        }
    }
}

/// Detected application type used to select the contextual-formatting block.
/// Mirrors macOS `AppType` / Windows `AppType`. `from_raw` accepts the camelCase
/// macOS raw values, the snake_case `promptValue` forms, and the PascalCase
/// Windows enum names so any platform's serialization round-trips.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum AppType {
    Email,
    Ai,
    WorkMessaging,
    PersonalMessaging,
    Document,
    Code,
    Terminal,
    Sensitive,
    #[default]
    Other,
}

impl AppType {
    /// Parse an app-type token. Accepts macOS rawValue (`workMessaging`),
    /// promptValue (`work_messaging`), and Windows PascalCase (`WorkMessaging`).
    /// Unknown → `Other`.
    pub fn from_raw(raw: &str) -> AppType {
        match raw {
            "email" | "Email" => AppType::Email,
            "ai" | "Ai" | "AI" => AppType::Ai,
            "workMessaging" | "work_messaging" | "WorkMessaging" => AppType::WorkMessaging,
            "personalMessaging" | "personal_messaging" | "PersonalMessaging" => {
                AppType::PersonalMessaging
            }
            "document" | "Document" => AppType::Document,
            "code" | "Code" => AppType::Code,
            "terminal" | "Terminal" => AppType::Terminal,
            "sensitive" | "Sensitive" => AppType::Sensitive,
            _ => AppType::Other,
        }
    }

    /// The value emitted in `<APP_TYPE>` — mirrors macOS `AppType.promptValue`.
    pub fn prompt_value(self) -> &'static str {
        match self {
            AppType::Email => "email",
            AppType::Ai => "ai",
            AppType::WorkMessaging => "work_messaging",
            AppType::PersonalMessaging => "personal_messaging",
            AppType::Document => "document",
            AppType::Code => "code",
            AppType::Terminal => "terminal",
            AppType::Sensitive => "sensitive",
            AppType::Other => "other",
        }
    }
}

/// English-spelling variant used for the `<SPELLING>` / `<DATE_FORMAT>` block.
/// Mirrors macOS `mode.englishSpelling` string. Parsed via [`EnglishSpelling::from_raw`].
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum EnglishSpelling {
    /// No spelling block emitted (macOS: empty/nil `englishSpelling`).
    #[default]
    None,
    American,
    British,
    Australian,
    Canadian,
}

impl EnglishSpelling {
    pub fn from_raw(raw: &str) -> EnglishSpelling {
        match raw {
            "british" => EnglishSpelling::British,
            "australian" => EnglishSpelling::Australian,
            "canadian" => EnglishSpelling::Canadian,
            "american" => EnglishSpelling::American,
            "" => EnglishSpelling::None,
            // macOS default branch is American for any non-empty unknown value.
            _ => EnglishSpelling::American,
        }
    }
}

/// All inputs needed to assemble both the static system prompt and the dynamic
/// system info. Plain data — the platform fills it in (including runtime values
/// like time/locale/host that Rust must NOT compute itself).
#[derive(Debug, Clone, Default)]
pub struct PromptContext {
    // --- preset / instructions ---
    pub preset: Preset,
    /// Custom instructions for the `custom` preset. Empty → the shared fallback
    /// "Process the text according to your best judgment." is substituted.
    pub custom_instructions: String,

    // --- language / spelling ---
    pub english_spelling: EnglishSpelling,
    /// Output language. Empty or "auto" → "same as transcript" requirements.
    /// Pass the platform's resolved DISPLAY name (e.g. "German"), not the code —
    /// language display-name resolution stays native.
    pub language: String,

    // --- user override ---
    /// The user's own system prompt (highest priority). Trimmed; empty → omitted.
    pub user_system_prompt: String,

    // --- application context (already gathered/classified by the platform) ---
    pub app_type: AppType,
    pub app_name: String,
    pub category: String,
    pub description: String,
    pub text_format: String,
    pub browser_host: String,
    pub browser_tab_title: String,
    /// Pre-joined focused-element label (macOS joins "Role - Title", stripping the
    /// "AX" prefix from the role). Empty → omitted.
    pub focused_element: String,
    /// Focused field content, already truncated to the platform's limit. Emitted
    /// only when there is no screen OCR text (avoids duplication). Empty → omitted.
    pub focused_content: String,
    /// OCR text from the screen. Empty → omitted.
    pub screen_ocr_text: String,
    pub app_type_confidence: String,
    pub app_type_source: String,
    /// Whether the platform actually gathered an app context. When false the whole
    /// `<APPLICATION_CONTEXT>` block is omitted (mirrors macOS `ApplicationContext.none`
    /// having empty fields, and Windows skipping the block when context is null).
    pub has_application_context: bool,

    // --- vocabulary (replacement-less words used as spelling hints) ---
    pub vocabulary_words: Vec<String>,

    // --- RUNTIME values passed in by the platform (NO clock/locale in Rust) ---
    /// Short localized time string, e.g. "3:42 PM". Platform-formatted.
    pub time: String,
    /// Timezone abbreviation, e.g. "PDT". Platform-resolved.
    pub timezone: String,
    /// Locale identifier, e.g. "en_US". Platform-resolved.
    pub locale: String,
    /// Computer/host name. Platform-resolved.
    pub computer_name: String,

    // --- mode flags ---
    pub punctuation: bool,
    pub capitalization: bool,
    pub profanity_filter: bool,
}

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

/// Build the STATIC system prompt for the given context.
///
/// Assembly order (matches macOS `finalizePrompt` / Windows `FinalizePrompt`):
/// `override-directive` → `<USER_SYSTEM_PROMPT>` (if set) → `anti-reply-directive`
/// → `number-formatting` → preset template (placeholders substituted, contextual
/// block injected) → `<MODE_FLAGS>`.
pub fn build_system_prompt(ctx: &PromptContext) -> String {
    let base = prompt_for_preset(ctx);

    let override_directive = FRAG_OVERRIDE;
    let anti_reply = FRAG_ANTI_REPLY;
    let number_formatting = FRAG_NUMBER_FORMATTING;

    let trimmed_user = ctx.user_system_prompt.trim();
    let user_block = if trimmed_user.is_empty() {
        String::new()
    } else {
        format!("\n\n<USER_SYSTEM_PROMPT>\n{trimmed_user}\n</USER_SYSTEM_PROMPT>")
    };

    let mut prompt = String::new();
    prompt.push_str(override_directive);
    prompt.push_str(&user_block);
    prompt.push('\n');
    prompt.push_str(anti_reply);
    prompt.push('\n');
    prompt.push_str(number_formatting);
    prompt.push('\n');
    prompt.push_str(&base);
    prompt.push_str(&mode_flags(ctx));

    prompt
}

/// Build the DYNAMIC system-info block (time, timezone, locale, computer,
/// spelling, language requirements, application context, vocabulary).
///
/// This is prepended to the *user* message (not the system prompt) so the static
/// system prompt stays byte-identical across requests and benefits from provider
/// prompt caching. Mirrors macOS `systemInfo` (lines 212-294).
pub fn build_system_info(ctx: &PromptContext) -> String {
    let spelling = spelling_instructions(ctx.english_spelling);
    let language = language_requirements(&ctx.language);
    let app_context = format_context_for_prompt(ctx);

    // Matches the macOS multiline interpolation exactly, including the blank
    // line between </SYSTEM_INFO> and <LANGUAGE_REQUIREMENTS> and before the
    // application context.
    let mut info = format!(
        "<SYSTEM_INFO>\n<TIME>{time}</TIME>\n<TIMEZONE>{tz}</TIMEZONE>\n<LOCALE>{locale}</LOCALE>\n<COMPUTER>{computer}</COMPUTER>\n{spelling}\n</SYSTEM_INFO>\n\n{language}\n\n{app_context}",
        time = ctx.time,
        tz = ctx.timezone,
        locale = ctx.locale,
        computer = ctx.computer_name,
        spelling = spelling,
        language = language,
        app_context = app_context,
    );

    // Custom vocabulary — sanitize each word (macOS sanitizeVocabularyWord) so
    // untrusted vocabulary cannot break out of the <CUSTOM_VOCABULARY> block.
    let words: Vec<String> = ctx
        .vocabulary_words
        .iter()
        .map(|w| sanitize_vocabulary_word(w))
        .filter(|w| !w.is_empty())
        .collect();
    if !words.is_empty() {
        info.push_str(&format!(
            "\n<CUSTOM_VOCABULARY>\n{}\n</CUSTOM_VOCABULARY>",
            words.join(", ")
        ));
    }

    info
}

/// Maximum length of one sanitized vocabulary term.
pub const MAX_VOCABULARY_TERM_CHARS: usize = 80;

/// Neutralize a vocabulary word for safe interpolation into the prompt.
/// Ported from macOS `PromptBuilder.sanitizeVocabularyWord`: drop `<`/`>` (so
/// it can't open/close XML tags), collapse all whitespace runs into single
/// spaces (so it can't masquerade as a directive), and cap the term length.
pub fn sanitize_vocabulary_word(word: &str) -> String {
    let without_brackets: String = word.chars().filter(|&c| c != '<' && c != '>').collect();
    without_brackets
        .split_whitespace()
        .collect::<Vec<_>>()
        .join(" ")
        .chars()
        .take(MAX_VOCABULARY_TERM_CHARS)
        .collect()
}

// ---------------------------------------------------------------------------
// Internal helpers
// ---------------------------------------------------------------------------

/// Escape XML metacharacters. Ported from macOS `ApplicationContextGatherer.xmlEscaped`
/// (lines 221-225). `&` MUST be replaced first. Adopted for Windows (which did
/// not escape) — see module-level DIVERGENCE note.
fn xml_escaped(s: &str) -> String {
    s.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
}

/// Resolve the preset template with its placeholders substituted and contextual
/// block injected. Mirrors macOS `promptForPreset`.
fn prompt_for_preset(ctx: &PromptContext) -> String {
    let block = contextual_formatting_block(ctx);
    match ctx.preset {
        Preset::Hyper => apply_contextual_formatting(PRESET_HYPER, &block),
        Preset::Message => apply_contextual_formatting(PRESET_MESSAGE, &block),
        Preset::Note => apply_contextual_formatting(PRESET_NOTE, &block),
        Preset::Meeting => apply_contextual_formatting(PRESET_MEETING, &block),
        Preset::Code => apply_contextual_formatting(PRESET_CODE, &block),
        Preset::Mail => {
            // Mail does not use a contextual block; it injects the email rules.
            PRESET_MAIL.replace("{{EMAIL_FORMATTING_RULES}}", FRAG_EMAIL_RULES)
        }
        Preset::Custom => {
            let custom = if ctx.custom_instructions.is_empty() {
                "Process the text according to your best judgment."
            } else {
                ctx.custom_instructions.as_str()
            };
            PRESET_CUSTOM
                .replace("{{CUSTOM_INSTRUCTIONS}}", custom)
                .replace("{{CONTEXTUAL_FORMATTING_BLOCK}}", &block)
        }
    }
}

/// macOS `applyContextualFormatting` — substitutes both placeholder spellings.
fn apply_contextual_formatting(template: &str, block: &str) -> String {
    template
        .replace("{{CONTEXTUAL_FORMATTING_BLOCK}}", block)
        .replace("{{EMAIL_BLOCK}}", block)
}

/// macOS `contextualFormattingBlock(for:appContext:)` — chooses the block based
/// on (preset, appType).
fn contextual_formatting_block(ctx: &PromptContext) -> String {
    let app_type = ctx.app_type;
    match ctx.preset {
        Preset::Hyper | Preset::Custom => block_for(app_type),
        Preset::Message => {
            if app_type == AppType::WorkMessaging {
                load_contextual("work-message")
            } else {
                load_contextual("personal-message")
            }
        }
        Preset::Note | Preset::Meeting => {
            if app_type == AppType::Code {
                load_contextual("code")
            } else if app_type == AppType::Terminal {
                load_contextual("terminal")
            } else {
                load_contextual("document")
            }
        }
        Preset::Code => {
            if app_type == AppType::Terminal {
                load_contextual("terminal")
            } else {
                load_contextual("code")
            }
        }
        Preset::Mail => String::new(),
    }
}

/// macOS `block(for appType:)`.
fn block_for(app_type: AppType) -> String {
    match app_type {
        AppType::Email => load_contextual("email"),
        AppType::WorkMessaging => load_contextual("work-message"),
        AppType::PersonalMessaging => load_contextual("personal-message"),
        AppType::Document => load_contextual("document"),
        AppType::Code | AppType::Ai => load_contextual("code"),
        AppType::Terminal => load_contextual("terminal"),
        AppType::Sensitive | AppType::Other => String::new(),
    }
}

/// macOS `loadContextualBlock` — returns the embedded block, substituting the
/// email formatting rules into the email block.
fn load_contextual(name: &str) -> String {
    let raw = match name {
        "email" => CTX_EMAIL,
        "work-message" => CTX_WORK_MESSAGE,
        "personal-message" => CTX_PERSONAL_MESSAGE,
        "document" => CTX_DOCUMENT,
        "code" => CTX_CODE,
        "terminal" => CTX_TERMINAL,
        _ => "",
    };
    if name == "email" {
        raw.replace("{{EMAIL_FORMATTING_RULES}}", FRAG_EMAIL_RULES)
    } else {
        raw.to_string()
    }
}

/// macOS `addModeFlags` / Windows `AddModeFlags`.
fn mode_flags(ctx: &PromptContext) -> String {
    let mut block = String::from("\n\n<MODE_FLAGS>");
    if ctx.punctuation {
        block.push('\n');
        block.push_str(FLAG_PUNCTUATION);
    }
    if ctx.capitalization {
        block.push('\n');
        block.push_str(FLAG_CAPITALIZATION);
    }
    if ctx.profanity_filter {
        block.push('\n');
        block.push_str(FLAG_PROFANITY);
    }
    block.push_str("\n</MODE_FLAGS>");
    block
}

/// macOS `spellingInstructions` closure (lines 216-240). Returns "" for `None`.
/// The leading "\n" matches the macOS string (it interpolates `\n<SPELLING>...`).
fn spelling_instructions(spelling: EnglishSpelling) -> String {
    match spelling {
        EnglishSpelling::None => String::new(),
        EnglishSpelling::British => "\n<SPELLING>British English (e.g., colour, realise, organisation, centre, travelled)</SPELLING>\n<DATE_FORMAT>Use British date format: DD/MM/YYYY (e.g., 25/12/2025) or \"25 December 2025\" / \"25th December 2025\"</DATE_FORMAT>".to_string(),
        EnglishSpelling::Australian => "\n<SPELLING>Australian English (e.g., colour, realise, organisation, centre, travelled)</SPELLING>\n<DATE_FORMAT>Use Australian date format: DD/MM/YYYY (e.g., 25/12/2025) or \"25 December 2025\" / \"25th December 2025\"</DATE_FORMAT>".to_string(),
        EnglishSpelling::Canadian => "\n<SPELLING>Canadian English (e.g., colour, realize, organization, centre, travelled)</SPELLING>\n<DATE_FORMAT>Use Canadian date format: DD/MM/YYYY or YYYY-MM-DD (e.g., 25/12/2025 or 2025-12-25) or \"25 December 2025\"</DATE_FORMAT>".to_string(),
        EnglishSpelling::American => "\n<SPELLING>American English (e.g., color, realize, organization, center, traveled)</SPELLING>\n<DATE_FORMAT>Use American date format: MM/DD/YYYY (e.g., 12/25/2025) or \"December 25, 2025\"</DATE_FORMAT>".to_string(),
    }
}

/// macOS `languageRequirements` closure (lines 243-264). `language` is the
/// resolved display name; empty (or the platform's "automatic" sentinel, which
/// the platform should map to empty before calling) → same-language block.
fn language_requirements(language: &str) -> String {
    if language.is_empty() {
        "<LANGUAGE_REQUIREMENTS>\n- Output in the SAME language as the transcript\n- If multiple languages are used, keep each section in its original language\n- Preserve names, technical terms, and proper nouns as spoken\n</LANGUAGE_REQUIREMENTS>".to_string()
    } else {
        format!(
            "<LANGUAGE_REQUIREMENTS>\n- Output ALL text in {lang}, including headings, labels, and content\n- Do NOT mix languages (e.g., English headings with {lang} content)\n- Preserve names, technical terms, and proper nouns as spoken\n</LANGUAGE_REQUIREMENTS>",
            lang = language
        )
    }
}

/// macOS `ApplicationContextGatherer.formatContextForPrompt` (lines 230-286).
/// Every interpolated field is XML-escaped (the unification choice over Windows).
fn format_context_for_prompt(ctx: &PromptContext) -> String {
    if !ctx.has_application_context {
        // No frontmost app (e.g. local-API post-process). macOS uses
        // ApplicationContext.none which has empty appName etc.; we omit the whole
        // block so it doesn't inject an empty <APP></APP> and bust prompt caching.
        return String::new();
    }

    let mut prompt = String::from("<APPLICATION_CONTEXT>\n");
    prompt.push_str(&format!("<APP>{}</APP>\n", xml_escaped(&ctx.app_name)));

    if !ctx.browser_tab_title.is_empty() {
        prompt.push_str(&format!(
            "<TAB>{}</TAB>\n",
            xml_escaped(&ctx.browser_tab_title)
        ));
    }
    if !ctx.browser_host.is_empty() {
        prompt.push_str(&format!(
            "<BROWSER_HOST>{}</BROWSER_HOST>\n",
            xml_escaped(&ctx.browser_host)
        ));
    }

    prompt.push_str(&format!(
        "<APP_TYPE>{}</APP_TYPE>\n",
        xml_escaped(ctx.app_type.prompt_value())
    ));
    prompt.push_str(&format!(
        "<APP_TYPE_CONFIDENCE>{}</APP_TYPE_CONFIDENCE>\n",
        xml_escaped(&ctx.app_type_confidence)
    ));
    prompt.push_str(&format!(
        "<APP_TYPE_SOURCE>{}</APP_TYPE_SOURCE>\n",
        xml_escaped(&ctx.app_type_source)
    ));
    prompt.push_str(&format!(
        "<CATEGORY>{}</CATEGORY>\n",
        xml_escaped(&ctx.category)
    ));
    prompt.push_str(&format!(
        "<DESCRIPTION>{}</DESCRIPTION>\n",
        xml_escaped(&ctx.description)
    ));
    prompt.push_str(&format!(
        "<TEXT_FORMAT>{}</TEXT_FORMAT>\n",
        xml_escaped(&ctx.text_format)
    ));

    // Focused element label (platform pre-joins "Role - Title").
    if !ctx.focused_element.is_empty() {
        prompt.push_str(&format!(
            "<FOCUSED_ELEMENT>{}</FOCUSED_ELEMENT>\n",
            xml_escaped(&ctx.focused_element)
        ));
    }

    // Focused content only when not sensitive AND no OCR present (avoids dup).
    if ctx.app_type != AppType::Sensitive
        && ctx.screen_ocr_text.is_empty()
        && !ctx.focused_content.is_empty()
    {
        prompt.push_str(&format!(
            "<FOCUSED_CONTENT>{}</FOCUSED_CONTENT>\n",
            xml_escaped(&ctx.focused_content)
        ));
    }

    prompt.push_str("</APPLICATION_CONTEXT>");

    // Screen OCR — outside APPLICATION_CONTEXT tags, skipped for sensitive apps.
    if ctx.app_type != AppType::Sensitive && !ctx.screen_ocr_text.is_empty() {
        prompt.push_str(&format!(
            "\n<SCREEN_CONTEXT>\n{}\n</SCREEN_CONTEXT>",
            xml_escaped(&ctx.screen_ocr_text)
        ));
    }

    prompt
}

#[cfg(test)]
#[path = "tests.rs"]
mod tests;
