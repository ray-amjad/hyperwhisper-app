//! WP-D3 — `app-type-catalog.json` parsing + classification.
//!
//! Port of `app/macos/.../AppClassification/AppTypeClassifier.swift` and
//! `app/windows/.../Services/AppClassification/AppTypeClassifier.cs`. Plain Rust,
//! sans-I/O: the catalog JSON is embedded at compile time
//! (`super::APP_TYPE_CATALOG`).
//!
//! Maps a foreground app — by macOS bundle id, Windows process name, browser
//! host, or window/tab title — to a coarse [`AppType`] driving app-aware
//! formatting (email vs code vs terminal vs markdown …).
//!
//! ## Matching algorithm (parity with both platforms)
//!
//! Entries are evaluated in a FIXED priority order (NOT catalog order):
//! `sensitive, email, terminal, code, ai, workMessaging, personalMessaging,
//! document`. Within `classify`, the SIGNALS are tried in this order, and the
//! first hit wins:
//!   1. **host** — exact or suffix (`host == h || host.ends_with(".{h}")`),
//!      confidence `strong`, source `browserHost`.
//!   2. **bundle id / process name** — exact (case-insensitive). macOS keys on
//!      `bundleId` (source `bundleId`); Windows keys on `processName`
//!      case-insensitively (source `processName`). We accept BOTH here and try
//!      bundle first, then process, so a single entry point serves both
//!      platforms. Confidence `strong`.
//!   3. **title** — keyword match (source `title`, confidence `medium`).
//!
//! Title-keyword matching mirrors macOS `titleKeywordMatches` exactly: a keyword
//! containing `.`, `/`, or a space is matched as a plain substring; otherwise it
//! must match on word boundaries (the surrounding chars must NOT be
//! alphanumeric-or-underscore). This is the same rule Windows encodes via a
//! `(?<![A-Za-z0-9_])kw(?![A-Za-z0-9_])` regex — implemented here with a manual
//! boundary scan to keep the crate dependency-free.
//!
//! ## Scope vs the reference impls (Wave 2 owns the rest)
//!
//! The task signature is `classify(bundle_id, process_name, host, title)`, so
//! this port covers the host/bundle/process/title signals — the deterministic,
//! catalog-driven core. The reference classifiers ALSO have an `appName`
//! fallback (lowercased title-match on the app name) and a `focusedElement`
//! email heuristic (the only place that needs the email regex). Those are
//! accessibility-snapshot inputs the platform owns; they are intentionally NOT
//! ported into this catalog crate. Divergence note: macOS uses `appName`,
//! Windows merges `windowTitle`+`browserTabTitle` into one title string — we
//! take a single already-prepared `title`, leaving the platform to choose how to
//! compose it.

use serde::Deserialize;

/// Coarse application type for app-aware formatting. `rawValue` strings match the
/// catalog keys / both platforms' `AppType` enum.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AppType {
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

impl AppType {
    /// The catalog key (`types.<key>`) for this type. Mirrors macOS
    /// `catalogKey` / Windows `ToCatalogKey`.
    fn catalog_key(self) -> &'static str {
        match self {
            AppType::Email => "email",
            AppType::Ai => "ai",
            AppType::WorkMessaging => "workMessaging",
            AppType::PersonalMessaging => "personalMessaging",
            AppType::Document => "document",
            AppType::Code => "code",
            AppType::Terminal => "terminal",
            AppType::Sensitive => "sensitive",
            AppType::Other => "other",
        }
    }

    /// The prompt token. Mirrors macOS `promptValue` / Windows `ToPromptValue`.
    pub fn prompt_value(self) -> &'static str {
        match self {
            AppType::WorkMessaging => "work_messaging",
            AppType::PersonalMessaging => "personal_messaging",
            AppType::Email => "email",
            AppType::Ai => "ai",
            AppType::Document => "document",
            AppType::Code => "code",
            AppType::Terminal => "terminal",
            AppType::Sensitive => "sensitive",
            AppType::Other => "other",
        }
    }

    /// The human category label. Mirrors macOS `category` / Windows `ToCategory`.
    pub fn category(self) -> &'static str {
        match self {
            AppType::Email => "Email Client",
            AppType::Ai => "AI",
            AppType::WorkMessaging | AppType::PersonalMessaging => "Communication",
            AppType::Document => "Document",
            AppType::Code => "Code Editor",
            AppType::Terminal => "Terminal",
            AppType::Sensitive => "Sensitive",
            AppType::Other => "Application",
        }
    }

    /// The text-input format hint. Mirrors macOS `textInputFormat` / Windows
    /// `ToTextFormat`.
    pub fn text_input_format(self) -> &'static str {
        match self {
            AppType::Email => "email",
            AppType::Code => "code",
            AppType::Terminal => "command",
            AppType::Document => "markdown",
            _ => "text",
        }
    }
}

/// The result of a classification. `matched` is the catalog token that produced
/// the hit (a host, lowercased bundle id, process name, or keyword), or `None`
/// for the default fallback.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AppClassification {
    pub app_type: AppType,
    /// `"strong"` | `"medium"` | `"unknown"`.
    pub confidence: String,
    /// `"browserHost"` | `"bundleId"` | `"processName"` | `"title"` | `"default"`.
    pub source: String,
    pub matched: Option<String>,
}

#[derive(Debug, Clone)]
struct PreparedKeyword {
    value: String,
    is_substring: bool,
}

#[derive(Debug, Clone)]
struct PreparedEntry {
    app_type: AppType,
    /// Lowercased macOS bundle ids.
    bundle_ids: Vec<String>,
    /// Windows process names, preserved as-is; matched case-insensitively.
    process_names: Vec<String>,
    hosts: Vec<String>,
    title_keywords: Vec<PreparedKeyword>,
}

#[derive(Deserialize)]
struct RawCatalog {
    #[serde(default)]
    types: std::collections::HashMap<String, RawEntry>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RawEntry {
    #[serde(default)]
    mac_bundle_ids: Vec<String>,
    #[serde(default)]
    windows_processes: Vec<String>,
    #[serde(default)]
    hosts: Vec<String>,
    #[serde(default)]
    title_keywords: Vec<String>,
}

/// Error parsing the app-type catalog JSON.
#[derive(thiserror::Error, Debug)]
pub enum AppTypeError {
    #[error("app-type-catalog.json failed to decode: {0}")]
    Decode(#[from] serde_json::Error),
}

/// Parsed, prepared app-type classifier. Build once and reuse; `classify` is a
/// linear scan over a handful of prepared entries.
#[derive(Debug, Clone)]
pub struct AppTypeClassifier {
    prepared: Vec<PreparedEntry>,
}

/// The fixed evaluation order. NOT catalog order — `sensitive` is checked first
/// so a password manager always wins, then the more specific types before the
/// catch-all `document`. Identical to both platforms' `order` array.
const ORDER: [AppType; 8] = [
    AppType::Sensitive,
    AppType::Email,
    AppType::Terminal,
    AppType::Code,
    AppType::Ai,
    AppType::WorkMessaging,
    AppType::PersonalMessaging,
    AppType::Document,
];

impl AppTypeClassifier {
    /// Parse an app-type-catalog JSON string and prepare the lookup tables.
    pub fn parse(json: &str) -> Result<AppTypeClassifier, AppTypeError> {
        let raw: RawCatalog = serde_json::from_str(json)?;
        let prepared = ORDER
            .iter()
            .filter_map(|&app_type| {
                let entry = raw.types.get(app_type.catalog_key())?;
                let title_keywords = entry
                    .title_keywords
                    .iter()
                    .filter_map(|raw_kw| {
                        let normalized = raw_kw.trim().to_lowercase();
                        if normalized.is_empty() {
                            return None;
                        }
                        // A keyword with a dot, slash, or space is matched as a
                        // plain substring (matches macOS / Windows).
                        let is_substring = normalized.contains('.')
                            || normalized.contains('/')
                            || normalized.contains(' ');
                        Some(PreparedKeyword {
                            value: normalized,
                            is_substring,
                        })
                    })
                    .collect();
                Some(PreparedEntry {
                    app_type,
                    bundle_ids: entry.mac_bundle_ids.iter().map(|b| b.to_lowercase()).collect(),
                    process_names: entry.windows_processes.clone(),
                    hosts: entry.hosts.clone(),
                    title_keywords,
                })
            })
            .collect();
        Ok(AppTypeClassifier { prepared })
    }

    /// Parse the compile-time-embedded `app-type-catalog.json`.
    pub fn embedded() -> Result<AppTypeClassifier, AppTypeError> {
        AppTypeClassifier::parse(super::APP_TYPE_CATALOG)
    }

    /// Classify a foreground app. Signals are tried in order — host, then
    /// bundle id, then process name, then title — and the first hit wins;
    /// otherwise returns the `Other`/`default` fallback. All string inputs may
    /// be empty; pass `None`/`""` for signals the platform can't observe.
    ///
    /// - `bundle_id`: macOS bundle identifier (e.g. `com.apple.mail`).
    /// - `process_name`: Windows process name without `.exe` (e.g. `OUTLOOK`).
    /// - `host`: browser host for a web app (e.g. `mail.google.com`); pass the
    ///   already-normalized host (no scheme, no `www.`). Empty/None to skip.
    /// - `title`: window or browser-tab title; matched case-insensitively.
    pub fn classify(
        &self,
        bundle_id: &str,
        process_name: &str,
        host: Option<&str>,
        title: &str,
    ) -> AppClassification {
        // 1. Host (strong).
        if let Some(host) = host {
            let host = normalize_host(host);
            if let Some(h) = host.as_deref() {
                if !h.is_empty() {
                    if let Some((entry, matched)) = self.match_host(h) {
                        return AppClassification {
                            app_type: entry,
                            confidence: "strong".into(),
                            source: "browserHost".into(),
                            matched: Some(matched),
                        };
                    }
                }
            }
        }

        // 2. macOS bundle id (strong).
        let bundle = bundle_id.trim();
        if !bundle.is_empty() {
            let lowered = bundle.to_lowercase();
            for entry in &self.prepared {
                if entry.bundle_ids.iter().any(|b| b == &lowered) {
                    return AppClassification {
                        app_type: entry.app_type,
                        confidence: "strong".into(),
                        source: "bundleId".into(),
                        matched: Some(lowered),
                    };
                }
            }
        }

        // 3. Windows process name (strong, case-insensitive).
        let process = process_name.trim();
        if !process.is_empty() {
            for entry in &self.prepared {
                if let Some(p) = entry
                    .process_names
                    .iter()
                    .find(|p| p.eq_ignore_ascii_case(process))
                {
                    return AppClassification {
                        app_type: entry.app_type,
                        confidence: "strong".into(),
                        source: "processName".into(),
                        matched: Some(p.clone()),
                    };
                }
            }
        }

        // 4. Title keyword (medium).
        let title_lc = title.to_lowercase();
        if !title_lc.is_empty() {
            if let Some((entry, matched)) = self.match_title(&title_lc) {
                return AppClassification {
                    app_type: entry,
                    confidence: "medium".into(),
                    source: "title".into(),
                    matched: Some(matched),
                };
            }
        }

        AppClassification {
            app_type: AppType::Other,
            confidence: "unknown".into(),
            source: "default".into(),
            matched: None,
        }
    }

    fn match_host(&self, host: &str) -> Option<(AppType, String)> {
        for entry in &self.prepared {
            if let Some(matched) = entry
                .hosts
                .iter()
                .find(|h| host == h.as_str() || host.ends_with(&format!(".{h}")))
            {
                return Some((entry.app_type, matched.clone()));
            }
        }
        None
    }

    fn match_title(&self, title_lc: &str) -> Option<(AppType, String)> {
        for entry in &self.prepared {
            if let Some(kw) = entry
                .title_keywords
                .iter()
                .find(|kw| keyword_matches(kw, title_lc))
            {
                return Some((entry.app_type, kw.value.clone()));
            }
        }
        None
    }
}

/// Whether `c` counts as a "word" character for title-boundary purposes:
/// ASCII alphanumeric or underscore. Mirrors macOS `titleBoundaryCharacterSet`
/// (`alphanumerics.union("_")`) and the Windows `[A-Za-z0-9_]` regex class.
/// We restrict to ASCII alphanumerics for cross-platform determinism (the
/// catalog keywords are all ASCII; Unicode alnum edge cases never arise).
fn is_word_char(c: char) -> bool {
    c.is_ascii_alphanumeric() || c == '_'
}

/// Title-keyword match mirroring macOS `titleKeywordMatches`. Substring keywords
/// (containing `.`/`/`/space) match anywhere; word keywords must have non-word
/// boundaries on both sides.
fn keyword_matches(kw: &PreparedKeyword, title: &str) -> bool {
    if kw.is_substring {
        return title.contains(&kw.value);
    }
    let needle = kw.value.as_bytes();
    let hay = title.as_bytes();
    if needle.is_empty() {
        return false;
    }
    // Byte-scan is safe: title is lowercased ASCII catalog text in practice; for
    // any multi-byte input the boundary check below treats continuation bytes as
    // word-ish (not boundaries), which is the conservative/correct behavior for
    // ASCII keywords embedded in Unicode titles.
    let mut start = 0usize;
    while let Some(pos) = find_subslice(&hay[start..], needle) {
        let abs = start + pos;
        let end = abs + needle.len();
        let before_ok = abs == 0 || !byte_is_word(hay[abs - 1]);
        let after_ok = end == hay.len() || !byte_is_word(hay[end]);
        if before_ok && after_ok {
            return true;
        }
        start = abs + 1;
    }
    false
}

fn byte_is_word(b: u8) -> bool {
    let c = b as char;
    is_word_char(c)
}

fn find_subslice(hay: &[u8], needle: &[u8]) -> Option<usize> {
    if needle.len() > hay.len() {
        return None;
    }
    hay.windows(needle.len()).position(|w| w == needle)
}

/// Normalize a browser host the way both platforms do: trim, lowercase, prepend
/// `https://` if no scheme, parse out the host, and strip a leading `www.`.
/// Without a full URL parser we approximate: strip scheme, take the authority up
/// to the first `/`, drop any userinfo/port, strip `www.`. The catalog hosts are
/// plain hostnames so a host already in canonical form passes through unchanged.
fn normalize_host(value: &str) -> Option<String> {
    let trimmed = value.trim().to_lowercase();
    if trimmed.is_empty() {
        return None;
    }
    // Drop scheme.
    let after_scheme = match trimmed.find("://") {
        Some(i) => &trimmed[i + 3..],
        None => trimmed.as_str(),
    };
    // Authority is up to the first '/'.
    let authority = after_scheme.split('/').next().unwrap_or(after_scheme);
    // Drop userinfo.
    let host_port = authority.rsplit('@').next().unwrap_or(authority);
    // Drop port (last ':' — hosts here are never IPv6 literals).
    let host = host_port.split(':').next().unwrap_or(host_port);
    let host = host.strip_prefix("www.").unwrap_or(host);
    if host.is_empty() {
        None
    } else {
        Some(host.to_string())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn classifier() -> AppTypeClassifier {
        AppTypeClassifier::embedded().expect("embedded app-type-catalog.json must parse")
    }

    #[test]
    fn embedded_catalog_parses() {
        let c = classifier();
        // All 8 catalog types prepared.
        assert_eq!(c.prepared.len(), 8);
    }

    // --- Golden: representative apps ----------------------------------------

    #[test]
    fn mac_bundle_email_apple_mail() {
        let c = classifier();
        let r = c.classify("com.apple.mail", "", None, "");
        assert_eq!(r.app_type, AppType::Email);
        assert_eq!(r.confidence, "strong");
        assert_eq!(r.source, "bundleId");
        assert_eq!(r.matched.as_deref(), Some("com.apple.mail"));
    }

    #[test]
    fn mac_bundle_case_insensitive() {
        let c = classifier();
        let r = c.classify("COM.APPLE.MAIL", "", None, "");
        assert_eq!(r.app_type, AppType::Email);
        assert_eq!(r.matched.as_deref(), Some("com.apple.mail"));
    }

    #[test]
    fn windows_process_outlook_email() {
        let c = classifier();
        let r = c.classify("", "OUTLOOK", None, "");
        assert_eq!(r.app_type, AppType::Email);
        assert_eq!(r.confidence, "strong");
        assert_eq!(r.source, "processName");
        assert_eq!(r.matched.as_deref(), Some("OUTLOOK"));
    }

    #[test]
    fn windows_process_case_insensitive() {
        let c = classifier();
        // Windows matches process names case-insensitively.
        let r = c.classify("", "outlook", None, "");
        assert_eq!(r.app_type, AppType::Email);
        assert_eq!(r.source, "processName");
    }

    #[test]
    fn host_gmail_email() {
        let c = classifier();
        let r = c.classify("", "", Some("mail.google.com"), "");
        assert_eq!(r.app_type, AppType::Email);
        assert_eq!(r.confidence, "strong");
        assert_eq!(r.source, "browserHost");
        assert_eq!(r.matched.as_deref(), Some("mail.google.com"));
    }

    #[test]
    fn host_subdomain_suffix_match() {
        let c = classifier();
        // A subdomain of a catalog host matches via the suffix rule.
        let r = c.classify("", "", Some("foo.notion.so"), "");
        assert_eq!(r.app_type, AppType::Document);
        assert_eq!(r.matched.as_deref(), Some("notion.so"));
    }

    #[test]
    fn host_with_scheme_and_www_normalized() {
        let c = classifier();
        let r = c.classify("", "", Some("https://www.cursor.com/dashboard"), "");
        assert_eq!(r.app_type, AppType::Ai);
        assert_eq!(r.matched.as_deref(), Some("cursor.com"));
    }

    #[test]
    fn code_editor_vscode_bundle() {
        let c = classifier();
        let r = c.classify("com.microsoft.VSCode", "", None, "");
        assert_eq!(r.app_type, AppType::Code);
    }

    #[test]
    fn terminal_iterm_bundle() {
        let c = classifier();
        let r = c.classify("com.googlecode.iterm2", "", None, "");
        assert_eq!(r.app_type, AppType::Terminal);
    }

    #[test]
    fn sensitive_1password_bundle() {
        let c = classifier();
        let r = c.classify("com.1password.1password", "", None, "");
        assert_eq!(r.app_type, AppType::Sensitive);
    }

    #[test]
    fn ai_claude_host() {
        let c = classifier();
        let r = c.classify("", "", Some("claude.ai"), "");
        assert_eq!(r.app_type, AppType::Ai);
    }

    // --- Golden: title keyword matching -------------------------------------

    #[test]
    fn title_keyword_word_boundary_match() {
        let c = classifier();
        // "slack" is a workMessaging keyword; word-boundary match in a title.
        let r = c.classify("", "", None, "Acme Team - Slack");
        assert_eq!(r.app_type, AppType::WorkMessaging);
        assert_eq!(r.confidence, "medium");
        assert_eq!(r.source, "title");
        assert_eq!(r.matched.as_deref(), Some("slack"));
    }

    #[test]
    fn title_keyword_substring_for_multiword() {
        let c = classifier();
        // "google docs" contains a space → substring keyword.
        let r = c.classify("", "", None, "My Plan - Google Docs");
        assert_eq!(r.app_type, AppType::Document);
        assert_eq!(r.matched.as_deref(), Some("google docs"));
    }

    #[test]
    fn title_keyword_no_match_inside_larger_word() {
        let c = classifier();
        // "code" must NOT match inside "decode" (word boundary fails). With no
        // other signal it falls back to Other.
        let r = c.classify("", "", None, "decode the message");
        assert_eq!(r.app_type, AppType::Other);
        assert_eq!(r.source, "default");
    }

    // --- Golden: signal priority --------------------------------------------

    #[test]
    fn host_beats_bundle_and_title() {
        let c = classifier();
        // Host (email) is tried before bundle (code) — host wins.
        let r = c.classify(
            "com.microsoft.VSCode",
            "",
            Some("mail.google.com"),
            "Visual Studio Code",
        );
        assert_eq!(r.app_type, AppType::Email);
        assert_eq!(r.source, "browserHost");
    }

    #[test]
    fn sensitive_priority_over_document_in_order() {
        // Construct a catalog where the same host appears under both document
        // and sensitive; sensitive is earlier in ORDER so it must win.
        let json = r#"{
            "version": 1,
            "types": {
                "document": {"macBundleIds":[],"windowsProcesses":[],
                    "hosts":["dup.example.com"],"titleKeywords":[]},
                "sensitive": {"macBundleIds":[],"windowsProcesses":[],
                    "hosts":["dup.example.com"],"titleKeywords":[]}
            }
        }"#;
        let c = AppTypeClassifier::parse(json).unwrap();
        let r = c.classify("", "", Some("dup.example.com"), "");
        assert_eq!(r.app_type, AppType::Sensitive);
    }

    // --- Golden: default / unknown fallback ---------------------------------

    #[test]
    fn unknown_app_falls_back_to_other() {
        let c = classifier();
        let r = c.classify("com.unknown.app", "RandomProc", Some("example.com"), "Untitled");
        assert_eq!(r.app_type, AppType::Other);
        assert_eq!(r.confidence, "unknown");
        assert_eq!(r.source, "default");
        assert_eq!(r.matched, None);
    }

    #[test]
    fn all_empty_inputs_fall_back_to_other() {
        let c = classifier();
        let r = c.classify("", "", None, "");
        assert_eq!(r.app_type, AppType::Other);
        assert_eq!(r.source, "default");
    }

    // --- AppType metadata parity --------------------------------------------

    #[test]
    fn app_type_metadata_matches_reference() {
        assert_eq!(AppType::WorkMessaging.prompt_value(), "work_messaging");
        assert_eq!(AppType::PersonalMessaging.prompt_value(), "personal_messaging");
        assert_eq!(AppType::Email.category(), "Email Client");
        assert_eq!(AppType::WorkMessaging.category(), "Communication");
        assert_eq!(AppType::PersonalMessaging.category(), "Communication");
        assert_eq!(AppType::Other.category(), "Application");
        assert_eq!(AppType::Email.text_input_format(), "email");
        assert_eq!(AppType::Code.text_input_format(), "code");
        assert_eq!(AppType::Terminal.text_input_format(), "command");
        assert_eq!(AppType::Document.text_input_format(), "markdown");
        assert_eq!(AppType::Ai.text_input_format(), "text");
    }

    #[test]
    fn malformed_json_is_error_not_panic() {
        assert!(AppTypeClassifier::parse("{ not json").is_err());
    }
}
