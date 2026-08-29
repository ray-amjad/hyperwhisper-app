//! App-type classification — the mirrored types and the two exported calls.

use super::catalogs::app_classifier;

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
