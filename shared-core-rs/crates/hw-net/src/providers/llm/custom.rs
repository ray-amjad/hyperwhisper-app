//! Custom OpenAI-compatible endpoints: one validation rule, one copy-name rule.
//!
//! # The drift this replaces
//!
//! Custom-endpoint URL validation was four incompatible rules:
//!
//! | Where | Rule |
//! |---|---|
//! | macOS `CustomPostProcessingEndpoint.swift:175` | any `URL(string:)` — accepts a **schemeless** string |
//! | Windows `CustomPostProcessingEndpoint.cs:133` | absolute only |
//! | shared-dotnet UI `ModesViewModel.cs:344` | + http/https, no userinfo, no fragment |
//! | shared-dotnet runtime `CloudPostProcessingService.cs:338-343` | + 2048/256 length caps |
//!
//! Backups carry endpoints across the platform boundary, so an endpoint one head
//! accepted became a permanently-skipped post-processing step on another — and
//! the macOS case is worse than a skip: `URL(string:)` takes the schemeless
//! string, then every recording fails with `unsupportedURL` and silently returns
//! the raw transcript.
//!
//! [`normalize_custom_endpoint`] pins the union of those rules as the one
//! answer, and [`EndpointValidationMode::Lenient`] is what stops the tightening
//! from deleting endpoints that already exist: a saved endpoint that fails the
//! new rules comes back as [`EndpointStatus::NeedsRepair`] with a concrete
//! `suggestion`, so the UI can prompt instead of the runtime silently skipping.

use crate::contract::{Header, HttpMethod, HttpRequest};

use super::bodies::{self, OpenAiChatOptions};
use super::LlmError;

/// Max custom endpoint URL length. PARITY: `CloudPostProcessingService.cs:342`.
pub const MAX_CUSTOM_ENDPOINT_URL_CHARS: usize = 2048;

/// Max custom endpoint model-name length. PARITY: `CloudPostProcessingService.cs:336`.
pub const MAX_CUSTOM_ENDPOINT_MODEL_CHARS: usize = 256;

/// The prefix that marks a Mode's stored provider string as a custom endpoint.
pub const CUSTOM_PROVIDER_PREFIX: &str = "custom:";

/// How strictly to judge an endpoint.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EndpointValidationMode {
    /// A new or edited endpoint: every rule break is fatal, so nothing invalid
    /// is ever *saved*.
    Strict,
    /// An endpoint already on disk (or arriving in a backup). A rule break
    /// becomes a repair prompt, not a deletion, and the endpoint keeps working
    /// wherever it is still safe to call.
    Lenient,
}

/// What [`normalize_custom_endpoint`] decided.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EndpointStatus {
    /// Passes every rule. `url` and `model` are ready to use.
    Valid,
    /// Usable-ish but wrong: show the user `suggestion`. `url` is non-empty only
    /// when the endpoint is still safe to call as-is.
    NeedsRepair,
    /// Not an endpoint at all. `url` is always empty.
    Invalid,
}

/// The single rule that failed. Ordered URL-first, then model, so the message a
/// user sees names the first thing they must fix.
#[derive(Debug, Clone, Copy, PartialEq, Eq, thiserror::Error)]
pub enum EndpointIssue {
    #[error("an endpoint URL is required")]
    EmptyUrl,
    #[error("the endpoint URL must start with http:// or https://")]
    NotAbsolute,
    #[error("only http and https endpoints are supported")]
    UnsupportedScheme,
    #[error("the endpoint URL must not contain a username or password")]
    UserInfoNotAllowed,
    #[error("the endpoint URL must not contain a #fragment")]
    FragmentNotAllowed,
    #[error("the endpoint URL is too long")]
    UrlTooLong,
    #[error("a model name is required")]
    EmptyModel,
    #[error("the model name is too long")]
    ModelTooLong,
}

/// The verdict on one custom endpoint configuration.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EndpointVerdict {
    pub status: EndpointStatus,
    /// The URL to actually call. **Empty means do not call it** — that is the
    /// single check a runtime caller needs, in every mode.
    pub url: String,
    /// The trimmed (and, when over-long in lenient mode, truncated) model name.
    pub model: String,
    /// The rule that failed, if any.
    pub issue: Option<EndpointIssue>,
    /// A concrete repaired URL to offer the user, when one exists.
    pub suggestion: Option<String>,
}

impl EndpointVerdict {
    fn valid(url: String, model: String) -> Self {
        Self {
            status: EndpointStatus::Valid,
            url,
            model,
            issue: None,
            suggestion: None,
        }
    }

    /// True when the endpoint can be called right now (in either mode).
    pub fn is_usable(&self) -> bool {
        !self.url.is_empty()
    }
}

/// The one custom-endpoint rule, replacing all four shipped ones.
///
/// A URL must be absolute, `http`/`https`, free of userinfo and of a fragment,
/// and at most [`MAX_CUSTOM_ENDPOINT_URL_CHARS`]; a model name must be non-empty
/// and at most [`MAX_CUSTOM_ENDPOINT_MODEL_CHARS`]. Both inputs are trimmed
/// first.
///
/// In [`EndpointValidationMode::Strict`] any break is [`EndpointStatus::Invalid`]
/// and `url` comes back empty. In [`EndpointValidationMode::Lenient`] only a
/// missing URL/model or a scheme that cannot be spoken (`ftp:`, `file:`) is
/// fatal; everything else is [`EndpointStatus::NeedsRepair`], and `url` still
/// carries a callable URL when calling it is safe.
pub fn normalize_custom_endpoint(
    raw: &str,
    model: &str,
    mode: EndpointValidationMode,
) -> EndpointVerdict {
    let lenient = mode == EndpointValidationMode::Lenient;
    let url = raw.trim();
    let model_trimmed = model.trim();

    if url.is_empty() {
        return fatal(EndpointIssue::EmptyUrl, model_trimmed);
    }
    if url.chars().count() > MAX_CUSTOM_ENDPOINT_URL_CHARS {
        return decide(
            lenient,
            EndpointIssue::UrlTooLong,
            // Over-long is a size complaint, not a safety one — a lenient
            // caller may still send it.
            lenient.then(|| url.to_string()),
            None,
            model_trimmed,
        );
    }

    let Some((scheme, after_scheme)) = url.split_once("://") else {
        // No scheme at all. This is the macOS bug: `URL(string:)` accepts it,
        // then `URLSession` fails every recording with `unsupportedURL`.
        let suggestion = (!url.starts_with('/') && !url.contains(' ') && looks_like_a_host(url))
            .then(|| format!("https://{url}"));
        let usable = suggestion
            .as_ref()
            .filter(|_| lenient)
            .map(|s| strip_fragment(s));
        return decide(
            lenient,
            EndpointIssue::NotAbsolute,
            usable,
            suggestion,
            model_trimmed,
        );
    };

    if !scheme.eq_ignore_ascii_case("http") && !scheme.eq_ignore_ascii_case("https") {
        // Nothing here can be repaired into an HTTP call; `mailto:`/`file:` is
        // not a chat-completions endpoint under any reading.
        return fatal(EndpointIssue::UnsupportedScheme, model_trimmed);
    }

    let authority_end = after_scheme
        .find(['/', '?', '#'])
        .unwrap_or(after_scheme.len());
    let authority = &after_scheme[..authority_end];
    if authority.is_empty() {
        return fatal(EndpointIssue::NotAbsolute, model_trimmed);
    }

    if let Some((_, host_part)) = authority.rsplit_once('@') {
        // A credential in the URL leaks into logs and proxies, and the
        // shared-dotnet UI already banned it. Repair = drop the userinfo.
        let repaired = format!("{scheme}://{host_part}{}", &after_scheme[authority_end..]);
        let repaired = strip_fragment(&repaired);
        return decide(
            lenient,
            EndpointIssue::UserInfoNotAllowed,
            // Still callable as written, so a saved endpoint keeps working
            // while the user is being asked to fix it.
            lenient.then(|| strip_fragment(url)),
            Some(repaired),
            model_trimmed,
        );
    }

    if url.contains('#') {
        let repaired = strip_fragment(url);
        return decide(
            lenient,
            EndpointIssue::FragmentNotAllowed,
            lenient.then(|| repaired.clone()),
            Some(repaired),
            model_trimmed,
        );
    }

    if model_trimmed.is_empty() {
        return fatal(EndpointIssue::EmptyModel, model_trimmed);
    }
    if model_trimmed.chars().count() > MAX_CUSTOM_ENDPOINT_MODEL_CHARS {
        let truncated: String = model_trimmed
            .chars()
            .take(MAX_CUSTOM_ENDPOINT_MODEL_CHARS)
            .collect();
        return EndpointVerdict {
            status: if lenient {
                EndpointStatus::NeedsRepair
            } else {
                EndpointStatus::Invalid
            },
            url: if lenient {
                url.to_string()
            } else {
                String::new()
            },
            model: if lenient {
                truncated
            } else {
                model_trimmed.to_string()
            },
            issue: Some(EndpointIssue::ModelTooLong),
            suggestion: None,
        };
    }

    EndpointVerdict::valid(url.to_string(), model_trimmed.to_string())
}

/// Validate an endpoint that is already saved (or arriving in a backup).
/// Shorthand for [`EndpointValidationMode::Lenient`].
pub fn validate_existing(raw: &str, model: &str) -> EndpointVerdict {
    normalize_custom_endpoint(raw, model, EndpointValidationMode::Lenient)
}

fn fatal(issue: EndpointIssue, model: &str) -> EndpointVerdict {
    EndpointVerdict {
        status: EndpointStatus::Invalid,
        url: String::new(),
        model: model.to_string(),
        issue: Some(issue),
        suggestion: None,
    }
}

fn decide(
    lenient: bool,
    issue: EndpointIssue,
    usable_url: Option<String>,
    suggestion: Option<String>,
    model: &str,
) -> EndpointVerdict {
    EndpointVerdict {
        status: if lenient {
            EndpointStatus::NeedsRepair
        } else {
            EndpointStatus::Invalid
        },
        url: usable_url.unwrap_or_default(),
        model: model.to_string(),
        issue: Some(issue),
        suggestion,
    }
}

/// Whether a schemeless string is worth prefixing with `https://`.
///
/// A colon is the hard case. `localhost:11434/v1/chat/completions` is the most
/// common self-hosted endpoint there is (Ollama, LM Studio) and its colon is a
/// PORT, so the repair is right. `mailto:someone@example.com` also has no
/// `://`, and its colon separates a scheme, so the repair would be nonsense.
/// Tell them apart by what follows the first colon: a port is all digits.
fn looks_like_a_host(url: &str) -> bool {
    let Some((_, after_colon)) = url.split_once(':') else {
        return true;
    };
    let port = after_colon
        .split(['/', '?', '#'])
        .next()
        .unwrap_or(after_colon);
    !port.is_empty() && port.chars().all(|c| c.is_ascii_digit())
}

fn strip_fragment(url: &str) -> String {
    match url.split_once('#') {
        Some((head, _)) => head.to_string(),
        None => url.to_string(),
    }
}

/// The host of an absolute `http`/`https` URL, lowercased, with any port and
/// userinfo removed. Used for the `api.groq.com` sniff.
pub(super) fn host_of(url: &str) -> Option<String> {
    let (_, after_scheme) = url.split_once("://")?;
    let authority_end = after_scheme
        .find(['/', '?', '#'])
        .unwrap_or(after_scheme.len());
    let authority = &after_scheme[..authority_end];
    let host_port = authority.rsplit_once('@').map_or(authority, |(_, h)| h);
    let host = match host_port.rsplit_once(':') {
        // Keep an IPv6 literal intact; only split a real `host:port`.
        Some((h, port)) if !h.ends_with(']') && port.chars().all(|c| c.is_ascii_digit()) => h,
        _ => host_port,
    };
    (!host.is_empty()).then(|| host.to_ascii_lowercase())
}

/// The UUID inside a Mode's `"custom:<uuid>"` provider string.
///
/// Returns the canonical **lowercase** form. macOS writes `UUID.uuidString`
/// (uppercase) and the .NET heads write `Guid.ToString()` (lowercase), so a
/// backup moved between platforms carries both casings for the same endpoint —
/// normalizing here is what makes the two comparable. `None` for anything that
/// is not the dashed 8-4-4-4-12 form.
pub fn parse_custom_provider_string(provider_string: &str) -> Option<String> {
    let raw = provider_string.strip_prefix(CUSTOM_PROVIDER_PREFIX)?;
    let groups: Vec<&str> = raw.split('-').collect();
    if groups.len() != 5 {
        return None;
    }
    let lengths = [8, 4, 4, 4, 12];
    for (group, expected) in groups.iter().zip(lengths) {
        if group.len() != expected || !group.chars().all(|c| c.is_ascii_hexdigit()) {
            return None;
        }
    }
    Some(raw.to_ascii_lowercase())
}

/// Whether a Mode's stored provider string names a custom endpoint.
pub fn is_custom_provider_string(provider_string: &str) -> bool {
    provider_string.starts_with(CUSTOM_PROVIDER_PREFIX)
}

/// The "Hello world" probe the Add/Edit endpoint sheet sends.
///
/// Same body shape as a real post-processing call so a pass means the real call
/// will work; no provider-specific fields, because the server behind a
/// user-supplied URL is unknown. Parse the 200 with
/// [`super::LlmWireProtocol::OpenAiChat`].
pub fn build_custom_endpoint_test_request(
    raw_url: &str,
    model: &str,
    api_key: Option<&str>,
) -> Result<HttpRequest, LlmError> {
    let verdict = normalize_custom_endpoint(raw_url, model, EndpointValidationMode::Strict);
    if !verdict.is_usable() {
        return Err(LlmError::InvalidEndpoint {
            message: verdict
                .issue
                .map(|i| i.to_string())
                .unwrap_or_else(|| "unusable custom endpoint".to_string()),
        });
    }

    let mut headers = Vec::new();
    if let Some(key) = api_key.map(str::trim).filter(|k| !k.is_empty()) {
        headers.push(Header::new("Authorization", format!("Bearer {key}")));
    }

    Ok(HttpRequest {
        method: HttpMethod::Post,
        url: verdict.url,
        headers,
        body: bodies::openai_chat(
            &verdict.model,
            "You are a helpful assistant.",
            "Say hello in one word.",
            OpenAiChatOptions::default(),
        ),
    })
}

/// The next name when the user duplicates an endpoint.
///
/// `"Name"` → `"Name (copy)"` → `"Name (copy 2)"` → `"Name (copy 3)"`.
/// Case-sensitive and end-anchored, so `"Name (COPY)"` and
/// `"Name (copy) Extra"` both just gain a fresh `" (copy)"`.
///
/// PARITY: macOS `CustomPostProcessingManager.generateCopyName` (regex
/// `\s\(copy(?:\s(\d+))?\)$`) and Windows `CustomEndpointManager.GenerateCopyName`
/// (the same regex). This is a hand-rolled scan rather than a regex so `hw-net`
/// does not take a `regex` dependency for one suffix.
pub fn next_copy_name(original_name: &str) -> String {
    if let Some((base, number)) = split_copy_suffix(original_name) {
        return format!("{base} (copy {})", number + 1);
    }
    format!("{original_name} (copy)")
}

/// Split `"<base> (copy)"` / `"<base> (copy N)"` into the base and the current
/// copy number (`1` for the un-numbered form). The leading separator must be
/// whitespace, matching `\s` in both shipped regexes.
fn split_copy_suffix(name: &str) -> Option<(&str, u32)> {
    let head = name.strip_suffix(')')?;
    let open = head.rfind('(')?;
    let inner = &head[open + 1..];
    let base = &head[..open];
    // `\s` before the `(`.
    let base = base.strip_suffix(|c: char| c.is_whitespace())?;

    if inner == "copy" {
        return Some((base, 1));
    }
    let digits = inner.strip_prefix("copy ")?;
    if digits.is_empty() || !digits.chars().all(|c| c.is_ascii_digit()) {
        return None;
    }
    // Saturate rather than wrap: an absurd counter keeps counting instead of
    // panicking in a release build or resetting to 1.
    Some((base, digits.parse::<u32>().unwrap_or(u32::MAX - 1)))
}
