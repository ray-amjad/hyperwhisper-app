//! `app-type-catalog.json` parsing + classification — the ONE implementation.
//!
//! Issue #279 deleted the Swift and C# copies of this algorithm
//! (`AppTypeClassifier.swift`, `AppTypeClassifier.cs`). Both heads now call
//! `app_classify` / `app_is_webmail` across the FFI, so the 8-element priority
//! array, the keyword-prep rule, the word-boundary rule, the host-suffix rule
//! and the email regex live here and nowhere else. Plain Rust, sans-I/O: the
//! catalog JSON is embedded at compile time (`super::APP_TYPE_CATALOG`).
//!
//! Maps a foreground app — by macOS bundle id, process name, browser host,
//! window/tab title, app name, or focused-element text — to a coarse [`AppType`]
//! driving app-aware formatting (email vs code vs terminal vs markdown …).
//!
//! ## Matching algorithm
//!
//! Entries are evaluated in a FIXED priority order (NOT catalog order):
//! `sensitive, email, terminal, code, ai, workMessaging, personalMessaging,
//! document`. Within [`AppTypeClassifier::classify`] the SIGNALS are tried in
//! this order, and the first hit wins:
//!   1. **host** — exact or suffix (`host == h || host.ends_with(".{h}")`),
//!      source `browserHost`. Confidence is the CALLER's `host_confidence`:
//!      macOS reported `strong`, Windows `medium` and the Local API `manual`,
//!      and that string reaches the LLM prompt, so the caller keeps owning it.
//!   2. **bundle id** — exact, case-insensitive. Source `bundleId`,
//!      confidence `strong`.
//!   3. **process name** — exact, case-insensitive, over the catalog's
//!      `windowsProcesses` + `linuxProcesses`. Source `processName`,
//!      confidence `strong`.
//!   4. **title** — keyword match. Source `title`, confidence `medium`. The
//!      caller composes the string, so macOS can fold in the window title
//!      (Windows joins tab + window title) without a change here.
//!   5. **app name** — the same keyword match over the app's display name.
//!      Source `appName`, confidence `medium`.
//!   6. **focused element** — the pieces the platform read off the focused
//!      accessibility node, joined and lowercased. `subject` / `compose` /
//!      `to:` / `cc:` gives Email at `medium`, source `focusedElement`; failing
//!      that an email ADDRESS in the text gives Email at `weak`, source
//!      `focusedElementText`.
//!   7. otherwise `Other` / `unknown` / `default`.
//!
//! A caller that cannot observe a signal passes an empty string, `None`, or an
//! empty vector for it, and that signal is skipped.
//!
//! Title-keyword matching: a keyword containing `.`, `/`, or a space is matched
//! as a plain substring; otherwise it must match on word boundaries (the
//! surrounding chars must NOT be alphanumeric-or-underscore). That is the rule
//! Windows used to encode as a `(?<![A-Za-z0-9_])kw(?![A-Za-z0-9_])` regex and
//! macOS as a `CharacterSet.alphanumerics.union("_")` scan — implemented here
//! with a manual boundary scan to keep the crate dependency-free.
//!
//! [`is_webmail`] is the browser-tab safety net both heads apply when nothing
//! else classified a browser window. It is deliberately NOT a `classify`
//! signal: only the caller knows whether the foreground app is a browser, and
//! applying it unconditionally would classify any window whose title happens to
//! contain an email address.

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
    /// Windows + Linux process names, preserved as-is; matched
    /// case-insensitively.
    process_names: Vec<String>,
    hosts: Vec<String>,
    title_keywords: Vec<PreparedKeyword>,
}

/// Everything a platform can observe about the foreground app. Every field is
/// optional in effect: pass an empty string, `None`, or an empty vector for a
/// signal the platform cannot see, and it is skipped.
///
/// This is one struct rather than seven arguments because the FFI mirrors it as
/// a UniFFI record, and a record survives a new signal without breaking every
/// binding.
#[derive(Debug, Clone, Default)]
pub struct ClassifyRequest {
    /// macOS bundle identifier, e.g. `com.apple.mail`.
    pub bundle_id: String,
    /// Process name without an extension, e.g. `OUTLOOK` or `gnome-terminal-server`.
    pub process_name: String,
    /// The app's display name, e.g. `Visual Studio Code`.
    pub app_name: String,
    /// Browser host for a web app, e.g. `mail.google.com`. Normalized here, so
    /// a full URL is accepted.
    pub host: Option<String>,
    /// The confidence to report for a host hit. Reaches the LLM prompt, so the
    /// caller owns it. Empty falls back to `strong`.
    pub host_confidence: String,
    /// Window and/or browser-tab title. The caller composes it.
    pub title: String,
    /// Text read off the focused accessibility element — role, title,
    /// description, placeholder, value, or whatever subset the platform has.
    pub focused_pieces: Vec<String>,
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
    /// Linux binaries whose name differs from the Windows one (`konsole`,
    /// `evolution`, …). Purely additive: process matching is case-insensitive,
    /// so the many names the two platforms share are already covered by
    /// `windowsProcesses`.
    #[serde(default)]
    linux_processes: Vec<String>,
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
                let mut process_names = entry.windows_processes.clone();
                process_names.extend(entry.linux_processes.iter().cloned());
                Some(PreparedEntry {
                    app_type,
                    bundle_ids: entry
                        .mac_bundle_ids
                        .iter()
                        .map(|b| b.to_lowercase())
                        .collect(),
                    process_names,
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

    /// Classify a foreground app. The signals in [`ClassifyRequest`] are tried
    /// in order — host, bundle id, process name, title, app name, focused
    /// element — and the first hit wins; otherwise this returns the
    /// `Other`/`unknown`/`default` fallback.
    pub fn classify(&self, request: &ClassifyRequest) -> AppClassification {
        // 1. Host (caller-supplied confidence).
        if let Some(host) = request.host.as_deref() {
            if let Some(h) = normalize_host(host) {
                if let Some((entry, matched)) = self.match_host(&h) {
                    let confidence = request.host_confidence.trim();
                    return AppClassification {
                        app_type: entry,
                        confidence: if confidence.is_empty() {
                            "strong".into()
                        } else {
                            confidence.to_string()
                        },
                        source: "browserHost".into(),
                        matched: Some(matched),
                    };
                }
            }
        }

        // 2. macOS bundle id (strong).
        let bundle = request.bundle_id.trim();
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

        // 3. Process name (strong, case-insensitive).
        let process = request.process_name.trim();
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
        let title_lc = request.title.to_lowercase();
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

        // 5. App display name, matched with the same keyword rule (medium).
        let app_name_lc = request.app_name.to_lowercase();
        if !app_name_lc.is_empty() {
            if let Some((entry, matched)) = self.match_title(&app_name_lc) {
                return AppClassification {
                    app_type: entry,
                    confidence: "medium".into(),
                    source: "appName".into(),
                    matched: Some(matched),
                };
            }
        }

        // 6. Focused accessibility element — the only email-specific heuristic.
        if let Some(classification) = classify_focused_pieces(&request.focused_pieces) {
            return classification;
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

/// The focused-element email heuristic. `pieces` are whatever the platform read
/// off the focused accessibility node — macOS supplies role, title,
/// description, placeholder and value; Windows supplies the element type and
/// its content. Blank pieces are dropped, the rest are joined with a space and
/// lowercased, exactly as both heads did before this moved here.
fn classify_focused_pieces(pieces: &[String]) -> Option<AppClassification> {
    let joined = pieces
        .iter()
        .map(|piece| piece.trim())
        .filter(|piece| !piece.is_empty())
        .collect::<Vec<_>>()
        .join(" ")
        .to_lowercase();
    if joined.is_empty() {
        return None;
    }

    if joined.contains("subject")
        || joined.contains("compose")
        || joined.contains("to:")
        || joined.contains("cc:")
    {
        return Some(AppClassification {
            app_type: AppType::Email,
            confidence: "medium".into(),
            source: "focusedElement".into(),
            matched: None,
        });
    }

    if contains_email_address(&joined) {
        return Some(AppClassification {
            app_type: AppType::Email,
            confidence: "weak".into(),
            source: "focusedElementText".into(),
            matched: None,
        });
    }

    None
}

/// Browser-tab titles that mean "this is webmail" even when the host is not
/// readable. Identical to the list both heads carried.
const WEBMAIL_KEYWORDS: [&str; 15] = [
    "gmail",
    "inbox",
    "mail.google",
    "outlook.live",
    "outlook.office",
    "mail.yahoo",
    "yahoo mail",
    "protonmail",
    "proton mail",
    "hey.com",
    "fastmail",
    "icloud.com/mail",
    "icloud mail",
    "zoho mail",
    "aol mail",
];

/// Whether a browser-tab title looks like webmail. The caller applies this ONLY
/// when it already knows the foreground app is a browser and nothing else
/// classified the window — see the module docs.
///
/// A Google Workspace tab reads `<name> Mail` rather than `Gmail`, so an email
/// ADDRESS anywhere in the title also counts. macOS had that fallback and
/// Windows did not; the shared answer is macOS's, because the drift lost a real
/// signal rather than adding a false one.
pub fn is_webmail(title: &str) -> bool {
    let lowered = title.to_lowercase();
    if WEBMAIL_KEYWORDS
        .iter()
        .any(|keyword| lowered.contains(keyword))
    {
        return true;
    }
    contains_email_address(title)
}

/// Whether `text` contains an email address, matching the regex both heads
/// compiled three times over:
/// `\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b`.
///
/// Hand-rolled to keep `hw-catalog` dependency-free, and scanned rather than
/// backtracked: for each `@`, the local part is the maximal run of local-class
/// bytes to its left (it matches if ANY start inside that run sits on a word
/// boundary), and the domain must be at least one domain-class byte, then a
/// dot, then two or more letters ending on a word boundary.
///
/// One deliberate narrowing: `\b` here is ASCII (`[A-Za-z0-9_]`), matching the
/// word-boundary rule the title keywords already use. .NET and ICU treat `\b`
/// as Unicode-aware, so a non-ASCII letter directly against an address — say
/// `メール太郎@example.com` — matched there and does not here. The catalog and
/// these heuristics are ASCII throughout, and one rule for both boundaries is
/// worth more than that corner.
fn contains_email_address(text: &str) -> bool {
    let bytes = text.as_bytes();
    for (at, &byte) in bytes.iter().enumerate() {
        if byte != b'@' {
            continue;
        }

        // Local part: the maximal run of `[A-Za-z0-9._%+-]` ending at `at`.
        let mut local_start = at;
        while local_start > 0 && is_email_local_byte(bytes[local_start - 1]) {
            local_start -= 1;
        }
        if local_start == at {
            continue;
        }
        // `\b` must hold at SOME start inside that run. The regex engine is free
        // to begin the match anywhere, and the local class holds both word bytes
        // (`a`, `0`, `_`) and non-word ones (`.`, `%`, `+`, `-`).
        if !(local_start..at).any(|start| is_word_boundary(bytes, start)) {
            continue;
        }

        // Domain: `[A-Za-z0-9.-]+` `\.` `[A-Za-z]{2,}` `\b`.
        let mut domain_end = at + 1;
        while domain_end < bytes.len() && is_email_domain_byte(bytes[domain_end]) {
            domain_end += 1;
        }
        // At least one byte before the dot that starts the TLD.
        for dot in (at + 2)..domain_end {
            if bytes[dot] != b'.' {
                continue;
            }
            let mut tld_end = dot + 1;
            while tld_end < domain_end && bytes[tld_end].is_ascii_alphabetic() {
                tld_end += 1;
            }
            if tld_end - (dot + 1) < 2 {
                continue;
            }
            if tld_end == bytes.len() || !byte_is_word(bytes[tld_end]) {
                return true;
            }
        }
    }
    false
}

fn is_email_local_byte(b: u8) -> bool {
    b.is_ascii_alphanumeric() || matches!(b, b'.' | b'_' | b'%' | b'+' | b'-')
}

fn is_email_domain_byte(b: u8) -> bool {
    b.is_ascii_alphanumeric() || matches!(b, b'.' | b'-')
}

/// Whether `index` sits on a `\b` — exactly one side is a word byte.
fn is_word_boundary(bytes: &[u8], index: usize) -> bool {
    let after = index < bytes.len() && byte_is_word(bytes[index]);
    let before = index > 0 && byte_is_word(bytes[index - 1]);
    before != after
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

    /// A request with every signal empty. Tests set only the field under test,
    /// which is also how each platform calls it.
    fn req() -> ClassifyRequest {
        ClassifyRequest::default()
    }

    fn with_bundle(id: &str) -> ClassifyRequest {
        ClassifyRequest {
            bundle_id: id.to_string(),
            ..req()
        }
    }

    fn with_process(name: &str) -> ClassifyRequest {
        ClassifyRequest {
            process_name: name.to_string(),
            ..req()
        }
    }

    fn with_host(host: &str) -> ClassifyRequest {
        ClassifyRequest {
            host: Some(host.to_string()),
            ..req()
        }
    }

    fn with_title(title: &str) -> ClassifyRequest {
        ClassifyRequest {
            title: title.to_string(),
            ..req()
        }
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
        let r = classifier().classify(&with_bundle("com.apple.mail"));
        assert_eq!(r.app_type, AppType::Email);
        assert_eq!(r.confidence, "strong");
        assert_eq!(r.source, "bundleId");
        assert_eq!(r.matched.as_deref(), Some("com.apple.mail"));
    }

    #[test]
    fn mac_bundle_case_insensitive() {
        let r = classifier().classify(&with_bundle("COM.APPLE.MAIL"));
        assert_eq!(r.app_type, AppType::Email);
        assert_eq!(r.matched.as_deref(), Some("com.apple.mail"));
    }

    #[test]
    fn windows_process_outlook_email() {
        let r = classifier().classify(&with_process("OUTLOOK"));
        assert_eq!(r.app_type, AppType::Email);
        assert_eq!(r.confidence, "strong");
        assert_eq!(r.source, "processName");
        assert_eq!(r.matched.as_deref(), Some("OUTLOOK"));
    }

    #[test]
    fn windows_process_case_insensitive() {
        let r = classifier().classify(&with_process("outlook"));
        assert_eq!(r.app_type, AppType::Email);
        assert_eq!(r.source, "processName");
    }

    #[test]
    fn linux_process_names_classify_too() {
        // The Linux privacy fix: a `linuxProcesses` binary name that Windows
        // never ships must classify, or Linux keeps the `other` default and the
        // Sensitive screen-OCR gate never fires.
        let c = classifier();
        let r = c.classify(&with_process("konsole"));
        assert_eq!(r.app_type, AppType::Terminal);
        assert_eq!(r.source, "processName");
        // A name the two platforms share is matched case-insensitively, so it
        // needs no `linuxProcesses` entry at all.
        assert_eq!(c.classify(&with_process("code")).app_type, AppType::Code);
    }

    #[test]
    fn host_gmail_email() {
        let r = classifier().classify(&with_host("mail.google.com"));
        assert_eq!(r.app_type, AppType::Email);
        assert_eq!(r.confidence, "strong");
        assert_eq!(r.source, "browserHost");
        assert_eq!(r.matched.as_deref(), Some("mail.google.com"));
    }

    #[test]
    fn host_confidence_comes_from_the_caller() {
        // macOS hardcoded "strong", Windows passed "medium" and the Local API
        // "manual" — and that string is written into the LLM prompt. The caller
        // keeps owning it; an empty value falls back to the macOS answer.
        let c = classifier();
        let r = c.classify(&ClassifyRequest {
            host_confidence: "manual".into(),
            ..with_host("mail.google.com")
        });
        assert_eq!(r.confidence, "manual");
        assert_eq!(
            c.classify(&with_host("mail.google.com")).confidence,
            "strong"
        );
    }

    #[test]
    fn host_subdomain_suffix_match() {
        let r = classifier().classify(&with_host("foo.notion.so"));
        assert_eq!(r.app_type, AppType::Document);
        assert_eq!(r.matched.as_deref(), Some("notion.so"));
    }

    #[test]
    fn host_with_scheme_and_www_normalized() {
        let r = classifier().classify(&with_host("https://www.cursor.com/dashboard"));
        assert_eq!(r.app_type, AppType::Ai);
        assert_eq!(r.matched.as_deref(), Some("cursor.com"));
    }

    #[test]
    fn code_editor_vscode_bundle() {
        assert_eq!(
            classifier()
                .classify(&with_bundle("com.microsoft.VSCode"))
                .app_type,
            AppType::Code
        );
    }

    #[test]
    fn terminal_iterm_bundle() {
        assert_eq!(
            classifier()
                .classify(&with_bundle("com.googlecode.iterm2"))
                .app_type,
            AppType::Terminal
        );
    }

    #[test]
    fn sensitive_1password_bundle() {
        assert_eq!(
            classifier()
                .classify(&with_bundle("com.1password.1password"))
                .app_type,
            AppType::Sensitive
        );
    }

    #[test]
    fn ai_claude_host() {
        assert_eq!(
            classifier().classify(&with_host("claude.ai")).app_type,
            AppType::Ai
        );
    }

    // --- Golden: title keyword matching -------------------------------------

    #[test]
    fn title_keyword_word_boundary_match() {
        let r = classifier().classify(&with_title("Acme Team - Slack"));
        assert_eq!(r.app_type, AppType::WorkMessaging);
        assert_eq!(r.confidence, "medium");
        assert_eq!(r.source, "title");
        assert_eq!(r.matched.as_deref(), Some("slack"));
    }

    #[test]
    fn title_keyword_substring_for_multiword() {
        // "google docs" contains a space → substring keyword.
        let r = classifier().classify(&with_title("My Plan - Google Docs"));
        assert_eq!(r.app_type, AppType::Document);
        assert_eq!(r.matched.as_deref(), Some("google docs"));
    }

    #[test]
    fn title_keyword_no_match_inside_larger_word() {
        // "code" must NOT match inside "decode" (word boundary fails).
        let r = classifier().classify(&with_title("decode the message"));
        assert_eq!(r.app_type, AppType::Other);
        assert_eq!(r.source, "default");
    }

    // --- Golden: the app-name fallback --------------------------------------

    #[test]
    fn app_name_is_matched_with_the_same_keyword_rule() {
        let r = classifier().classify(&ClassifyRequest {
            app_name: "Ghostty".into(),
            ..req()
        });
        assert_eq!(r.app_type, AppType::Terminal);
        assert_eq!(r.confidence, "medium");
        assert_eq!(r.source, "appName");
        assert_eq!(r.matched.as_deref(), Some("ghostty"));
    }

    #[test]
    fn title_is_tried_before_the_app_name() {
        let r = classifier().classify(&ClassifyRequest {
            app_name: "Ghostty".into(),
            ..with_title("Acme Team - Slack")
        });
        assert_eq!(r.app_type, AppType::WorkMessaging);
        assert_eq!(r.source, "title");
    }

    // --- Golden: the focused-element heuristic ------------------------------

    #[test]
    fn focused_element_keyword_is_email_at_medium() {
        let r = classifier().classify(&ClassifyRequest {
            focused_pieces: vec!["AXTextField".into(), "Subject".into()],
            ..req()
        });
        assert_eq!(r.app_type, AppType::Email);
        assert_eq!(r.confidence, "medium");
        assert_eq!(r.source, "focusedElement");
        assert_eq!(r.matched, None);
    }

    #[test]
    fn focused_element_address_is_email_at_weak() {
        let r = classifier().classify(&ClassifyRequest {
            focused_pieces: vec!["AXTextArea".into(), "ray@example.com".into()],
            ..req()
        });
        assert_eq!(r.app_type, AppType::Email);
        assert_eq!(r.confidence, "weak");
        assert_eq!(r.source, "focusedElementText");
    }

    #[test]
    fn focused_element_blank_pieces_are_dropped() {
        let r = classifier().classify(&ClassifyRequest {
            focused_pieces: vec![String::new(), "   ".into()],
            ..req()
        });
        assert_eq!(r.app_type, AppType::Other);
        assert_eq!(r.source, "default");
    }

    #[test]
    fn focused_element_is_tried_after_every_catalog_signal() {
        // A window that is BOTH a terminal by title and holds an address in the
        // focused field is a terminal, not email.
        let r = classifier().classify(&ClassifyRequest {
            focused_pieces: vec!["ray@example.com".into()],
            ..with_title("zsh — Terminal")
        });
        assert_eq!(r.app_type, AppType::Terminal);
        assert_eq!(r.source, "title");
    }

    // --- Golden: signal priority --------------------------------------------

    #[test]
    fn host_beats_bundle_and_title() {
        let r = classifier().classify(&ClassifyRequest {
            bundle_id: "com.microsoft.VSCode".into(),
            title: "Visual Studio Code".into(),
            ..with_host("mail.google.com")
        });
        assert_eq!(r.app_type, AppType::Email);
        assert_eq!(r.source, "browserHost");
    }

    #[test]
    fn bundle_beats_process_and_title() {
        let r = classifier().classify(&ClassifyRequest {
            bundle_id: "com.apple.mail".into(),
            process_name: "warp".into(),
            title: "Acme Team - Slack".into(),
            ..req()
        });
        assert_eq!(r.app_type, AppType::Email);
        assert_eq!(r.source, "bundleId");
    }

    #[test]
    fn process_beats_title() {
        let r = classifier().classify(&ClassifyRequest {
            process_name: "warp".into(),
            ..with_title("Acme Team - Slack")
        });
        assert_eq!(r.app_type, AppType::Terminal);
        assert_eq!(r.source, "processName");
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
        assert_eq!(
            c.classify(&with_host("dup.example.com")).app_type,
            AppType::Sensitive
        );
    }

    #[test]
    fn the_priority_order_is_the_documented_eight() {
        // The whole array, pinned: a catalog where every type claims the same
        // host must resolve in ORDER as each earlier type is removed.
        let types: Vec<&str> = ORDER.iter().map(|t| t.catalog_key()).collect();
        assert_eq!(
            types,
            vec![
                "sensitive",
                "email",
                "terminal",
                "code",
                "ai",
                "workMessaging",
                "personalMessaging",
                "document"
            ]
        );
        for skip in 0..ORDER.len() {
            let entries: Vec<String> = ORDER[skip..]
                .iter()
                .map(|t| format!(r#""{}": {{"hosts":["dup.example.com"]}}"#, t.catalog_key()))
                .collect();
            let json = format!(r#"{{"types":{{{}}}}}"#, entries.join(","));
            let c = AppTypeClassifier::parse(&json).unwrap();
            assert_eq!(
                c.classify(&with_host("dup.example.com")).app_type,
                ORDER[skip],
                "with {:?} first in the catalog",
                ORDER[skip]
            );
        }
    }

    // --- Golden: default / unknown fallback ---------------------------------

    #[test]
    fn unknown_app_falls_back_to_other() {
        let r = classifier().classify(&ClassifyRequest {
            bundle_id: "com.unknown.app".into(),
            process_name: "RandomProc".into(),
            title: "Untitled".into(),
            ..with_host("example.com")
        });
        assert_eq!(r.app_type, AppType::Other);
        assert_eq!(r.confidence, "unknown");
        assert_eq!(r.source, "default");
        assert_eq!(r.matched, None);
    }

    #[test]
    fn all_empty_inputs_fall_back_to_other() {
        let r = classifier().classify(&req());
        assert_eq!(r.app_type, AppType::Other);
        assert_eq!(r.source, "default");
    }

    // --- Golden: the host-suffix rule ---------------------------------------

    #[test]
    fn host_suffix_needs_a_dot_boundary() {
        let c = classifier();
        // "notion.so" itself, and any dot-delimited subdomain of it.
        assert_eq!(
            c.classify(&with_host("notion.so")).app_type,
            AppType::Document
        );
        assert_eq!(
            c.classify(&with_host("a.b.notion.so")).app_type,
            AppType::Document
        );
        // NOT a host that merely ENDS with the same letters.
        assert_eq!(
            c.classify(&with_host("evilnotion.so")).app_type,
            AppType::Other
        );
        // NOT a prefix.
        assert_eq!(
            c.classify(&with_host("notion.so.evil.com")).app_type,
            AppType::Other
        );
    }

    #[test]
    fn host_normalization_strips_scheme_userinfo_port_and_www() {
        let c = classifier();
        for raw in [
            "mail.google.com",
            "MAIL.GOOGLE.COM",
            "  mail.google.com  ",
            "www.mail.google.com",
            "https://mail.google.com/u/0/#inbox",
            "http://user:pw@mail.google.com:8443/path",
        ] {
            let r = c.classify(&with_host(raw));
            assert_eq!(r.app_type, AppType::Email, "host {raw:?}");
            assert_eq!(
                r.matched.as_deref(),
                Some("mail.google.com"),
                "host {raw:?}"
            );
        }
    }

    // --- Golden: the email regex --------------------------------------------

    #[test]
    fn email_address_matching_mirrors_the_regex() {
        for text in [
            "ray@example.com",
            "Re: ray@example.com",
            "first.last+tag@sub.example.co.uk",
            "a_b%c@example.io",
            "(ray@example.com)",
            "mail to ray@example.com.",
        ] {
            assert!(contains_email_address(text), "should match {text:?}");
        }
        for text in [
            "",
            "ray@example",
            "ray@example.c",
            "@example.com",
            "ray@.com",
            "ray@example.com1",
            "ray@example.com_",
            "plain text with no address",
        ] {
            assert!(!contains_email_address(text), "should not match {text:?}");
        }
    }

    // --- Golden: is_webmail --------------------------------------------------

    #[test]
    fn is_webmail_matches_keywords_and_addresses() {
        assert!(is_webmail("Inbox (12) - Gmail"));
        assert!(is_webmail("Mail - Outlook.office.com"));
        assert!(is_webmail("iCloud Mail"));
        // The Google Workspace shape: "<address> Mail", no keyword.
        assert!(is_webmail("ray@acme.co Mail"));
        assert!(!is_webmail("Acme Team - Slack"));
        assert!(!is_webmail(""));
    }

    // --- AppType metadata parity --------------------------------------------

    #[test]
    fn app_type_metadata_matches_reference() {
        assert_eq!(AppType::WorkMessaging.prompt_value(), "work_messaging");
        assert_eq!(
            AppType::PersonalMessaging.prompt_value(),
            "personal_messaging"
        );
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
