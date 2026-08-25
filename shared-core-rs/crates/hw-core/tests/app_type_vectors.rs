//! Conformance-vector tests for the shared app-type classifier.
//!
//! `shared-conformance/app-type-vectors.json` is the cross-platform source of
//! truth for the four rules issue #279 moved into `hw-catalog`: the 8-element
//! priority order, the title word-boundary rule, the host-suffix rule, and the
//! email regex (with its `is_webmail` sibling). Swift and C# run the same file
//! through their own UniFFI bindings:
//!
//! - `app/macos/hyperwhisperTests/AppTypeConformanceVectorTests.swift`
//! - `app/shared-dotnet/HyperWhisper.AppTypeConformance.Tests/Program.cs`
//!
//! Before #279 each stack ran its own copy of those rules and they had already
//! drifted — the host confidence, the `appName` signal, the focused-element
//! field count and the webmail email fallback all differed between macOS and
//! Windows, and Linux had no classifier at all. There is now ONE
//! implementation, so these vectors pin its answer and every stack proves it
//! reads that same answer across the FFI boundary.
//!
//! Regenerate after an intended catalog or classifier change:
//!
//! ```sh
//! cd shared-core-rs
//! cargo test -p hw-core --test app_type_vectors -- --ignored regenerate
//! ```
//!
//! Then read the diff. An expectation that changes without a matching
//! `shared-app-classification/app-type-catalog.json` edit is a classifier
//! regression, not a refresh.

use std::path::PathBuf;

use serde::{Deserialize, Serialize};

// The crate's `[lib] name` is `hyperwhisper_core` (it drives the artifact
// name), so that — not `hw_core` — is how an integration test imports it.
use hyperwhisper_core::ffi_catalog::{app_classify, app_is_webmail, AppClassifyRequest};

const VECTORS_PATH: &str = "../../../shared-conformance/app-type-vectors.json";

fn vectors_path() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join(VECTORS_PATH)
}

// ---------------------------------------------------------------------------
// Vector shapes. Kept flat and explicit so the JSON reads as data a human can
// review in a pull request, not as a serde dump of the FFI records.
// ---------------------------------------------------------------------------

#[derive(Serialize, Deserialize, PartialEq, Debug)]
#[serde(rename_all = "camelCase")]
struct Document {
    description: String,
    classifications: Vec<CaseVector>,
    webmail_titles: Vec<WebmailVector>,
}

#[derive(Serialize, Deserialize, PartialEq, Debug)]
#[serde(rename_all = "camelCase")]
struct CaseVector {
    /// What this row proves. Read the names, not the inputs, to see what is
    /// covered.
    name: String,
    /// Which of the four rules the row belongs to. The coverage check counts
    /// these, so a rule cannot lose its last vector unnoticed.
    rule: String,
    request: RequestVector,
    expected: ExpectedVector,
}

#[derive(Serialize, Deserialize, PartialEq, Debug, Default, Clone)]
#[serde(rename_all = "camelCase")]
struct RequestVector {
    #[serde(default)]
    bundle_id: String,
    #[serde(default)]
    process_name: String,
    #[serde(default)]
    app_name: String,
    #[serde(default)]
    host: Option<String>,
    #[serde(default)]
    host_confidence: String,
    #[serde(default)]
    title: String,
    #[serde(default)]
    focused_pieces: Vec<String>,
}

#[derive(Serialize, Deserialize, PartialEq, Debug)]
#[serde(rename_all = "camelCase")]
struct ExpectedVector {
    /// The `ClassifiedAppType` variant name, e.g. `Email`.
    app_type: String,
    prompt_value: String,
    category: String,
    text_input_format: String,
    confidence: String,
    source: String,
    matched: Option<String>,
}

#[derive(Serialize, Deserialize, PartialEq, Debug)]
#[serde(rename_all = "camelCase")]
struct WebmailVector {
    title: String,
    expected: bool,
    /// `"keyword"`, `"address"`, or `"none"` — which half of `is_webmail`
    /// this row exercises.
    branch: String,
}

// ---------------------------------------------------------------------------
// Rule labels. One constant per rule so a typo cannot silently create a new,
// uncounted bucket.
// ---------------------------------------------------------------------------

const RULE_PRIORITY: &str = "priorityOrder";
const RULE_WORD_BOUNDARY: &str = "wordBoundary";
const RULE_HOST_SUFFIX: &str = "hostSuffix";
const RULE_EMAIL_REGEX: &str = "emailRegex";

const RULES: [&str; 4] = [
    RULE_PRIORITY,
    RULE_WORD_BOUNDARY,
    RULE_HOST_SUFFIX,
    RULE_EMAIL_REGEX,
];

// ---------------------------------------------------------------------------
// The inputs. Expectations are NOT written here — `build_document` fills them
// from the shared core, and the committed file is what review reads.
// ---------------------------------------------------------------------------

fn bundle(id: &str) -> RequestVector {
    RequestVector {
        bundle_id: id.to_string(),
        ..RequestVector::default()
    }
}

fn process(name: &str) -> RequestVector {
    RequestVector {
        process_name: name.to_string(),
        ..RequestVector::default()
    }
}

fn host(value: &str) -> RequestVector {
    RequestVector {
        host: Some(value.to_string()),
        ..RequestVector::default()
    }
}

fn title(value: &str) -> RequestVector {
    RequestVector {
        title: value.to_string(),
        ..RequestVector::default()
    }
}

fn focused(pieces: &[&str]) -> RequestVector {
    RequestVector {
        focused_pieces: pieces.iter().map(|p| p.to_string()).collect(),
        ..RequestVector::default()
    }
}

fn cases() -> Vec<(&'static str, &'static str, RequestVector)> {
    vec![
        // --- The 8-element priority order --------------------------------
        // One row per type, keyed on the signal that type is most often seen
        // through, so a reordered array moves at least one of them.
        (
            "sensitive: 1Password by bundle id",
            RULE_PRIORITY,
            bundle("com.1password.1password"),
        ),
        (
            "email: Outlook by process name",
            RULE_PRIORITY,
            process("OUTLOOK"),
        ),
        (
            "terminal: konsole by Linux process name",
            RULE_PRIORITY,
            process("konsole"),
        ),
        (
            "code: VS Code by bundle id",
            RULE_PRIORITY,
            bundle("com.microsoft.VSCode"),
        ),
        ("ai: claude.ai by host", RULE_PRIORITY, host("claude.ai")),
        (
            "workMessaging: Slack by title",
            RULE_PRIORITY,
            title("Acme Team - Slack"),
        ),
        (
            "personalMessaging: WhatsApp by process name",
            RULE_PRIORITY,
            process("WhatsApp"),
        ),
        ("document: Notion by host", RULE_PRIORITY, host("notion.so")),
        (
            "other: nothing matches any signal",
            RULE_PRIORITY,
            RequestVector {
                bundle_id: "com.example.nothing".into(),
                process_name: "nothing".into(),
                app_name: "Nothing At All".into(),
                host: Some("nothing.example".into()),
                title: "nothing at all".into(),
                ..RequestVector::default()
            },
        ),
        // Signal order within one request. Each row supplies signals that
        // point at DIFFERENT types, so only the documented winner is possible.
        (
            "host outranks bundle, process, title and app name",
            RULE_PRIORITY,
            RequestVector {
                bundle_id: "com.apple.mail".into(),
                process_name: "WindowsTerminal".into(),
                app_name: "Ghostty".into(),
                host: Some("claude.ai".into()),
                title: "1Password".into(),
                ..RequestVector::default()
            },
        ),
        (
            "bundle id outranks process name and title",
            RULE_PRIORITY,
            RequestVector {
                bundle_id: "com.apple.mail".into(),
                process_name: "WindowsTerminal".into(),
                title: "Acme Team - Slack".into(),
                ..RequestVector::default()
            },
        ),
        (
            "process name outranks title and app name",
            RULE_PRIORITY,
            RequestVector {
                process_name: "WindowsTerminal".into(),
                app_name: "1Password".into(),
                title: "Acme Team - Slack".into(),
                ..RequestVector::default()
            },
        ),
        (
            "title outranks the app name",
            RULE_PRIORITY,
            RequestVector {
                app_name: "Ghostty".into(),
                title: "Acme Team - Slack".into(),
                ..RequestVector::default()
            },
        ),
        (
            "app name outranks the focused element",
            RULE_PRIORITY,
            RequestVector {
                app_name: "Ghostty".into(),
                focused_pieces: vec!["Subject".into()],
                ..RequestVector::default()
            },
        ),
        (
            "a bundle id matches case-insensitively",
            RULE_PRIORITY,
            bundle("COM.APPLE.MAIL"),
        ),
        (
            "a process name matches case-insensitively and reports catalog casing",
            RULE_PRIORITY,
            process("outlook"),
        ),
        (
            "the app name uses the title keyword rule, at appName/medium",
            RULE_PRIORITY,
            RequestVector {
                app_name: "Ghostty".into(),
                ..RequestVector::default()
            },
        ),
        // The host confidence is the caller's, not a constant. macOS said
        // `strong`, Windows `medium`, the Local API `manual`, and that string
        // reaches the LLM prompt.
        (
            "host confidence is passed through from the caller",
            RULE_PRIORITY,
            RequestVector {
                host_confidence: "manual".into(),
                ..host("claude.ai")
            },
        ),
        (
            "an empty host confidence falls back to strong",
            RULE_PRIORITY,
            host("claude.ai"),
        ),
        // --- The title word-boundary rule --------------------------------
        (
            "word keyword matches on a word boundary",
            RULE_WORD_BOUNDARY,
            title("Acme Team - Slack"),
        ),
        (
            "word keyword does NOT match inside a larger word",
            RULE_WORD_BOUNDARY,
            title("decode the message"),
        ),
        (
            "word keyword does NOT match with a trailing word char",
            RULE_WORD_BOUNDARY,
            title("slackware release notes"),
        ),
        (
            "word keyword does NOT match across an underscore",
            RULE_WORD_BOUNDARY,
            title("my_slack_export"),
        ),
        (
            "word keyword matches when punctuation is the boundary",
            RULE_WORD_BOUNDARY,
            title("(slack)"),
        ),
        (
            "word keyword matches at the very start and end of the title",
            RULE_WORD_BOUNDARY,
            title("slack"),
        ),
        (
            "a keyword with a space is a plain substring",
            RULE_WORD_BOUNDARY,
            title("My Plan - Google Docs"),
        ),
        (
            "a keyword with a dot is a plain substring, so it matches inside a word",
            RULE_WORD_BOUNDARY,
            title("xrocket.chatx"),
        ),
        (
            "keyword matching is case-insensitive",
            RULE_WORD_BOUNDARY,
            title("ACME TEAM - SLACK"),
        ),
        (
            "a later occurrence still matches after a boundary miss",
            RULE_WORD_BOUNDARY,
            title("slackware and slack"),
        ),
        // --- The host-suffix rule ----------------------------------------
        ("an exact host matches", RULE_HOST_SUFFIX, host("notion.so")),
        (
            "a dot-delimited subdomain matches",
            RULE_HOST_SUFFIX,
            host("a.b.notion.so"),
        ),
        (
            "a host that merely ends with the same letters does NOT match",
            RULE_HOST_SUFFIX,
            host("evilnotion.so"),
        ),
        (
            "a catalog host used as a PREFIX does NOT match",
            RULE_HOST_SUFFIX,
            host("notion.so.evil.example"),
        ),
        (
            "a scheme is stripped before matching",
            RULE_HOST_SUFFIX,
            host("https://notion.so/page"),
        ),
        (
            "a leading www. is stripped before matching",
            RULE_HOST_SUFFIX,
            host("www.mail.google.com"),
        ),
        (
            "userinfo and a port are stripped before matching",
            RULE_HOST_SUFFIX,
            host("http://user:pw@mail.google.com:8443/path"),
        ),
        (
            "surrounding whitespace and casing are normalized",
            RULE_HOST_SUFFIX,
            host("  MAIL.GOOGLE.COM  "),
        ),
        // The scheme is stripped before matching even when the rest is not a
        // parseable URL, so the value never reaches the catalog with a
        // `https://` still on the front.
        (
            "a host with an embedded space matches nothing",
            RULE_HOST_SUFFIX,
            host("https://mail.google.com bogus"),
        ),
        (
            "an empty host is skipped rather than matched",
            RULE_HOST_SUFFIX,
            RequestVector {
                title: "Acme Team - Slack".into(),
                ..host("   ")
            },
        ),
        // --- The email regex ---------------------------------------------
        (
            "focused-element keyword beats the address scan",
            RULE_EMAIL_REGEX,
            focused(&["AXTextField", "Subject"]),
        ),
        (
            "a compose field is email at medium",
            RULE_EMAIL_REGEX,
            focused(&["Compose"]),
        ),
        (
            "a to: prefix is email at medium",
            RULE_EMAIL_REGEX,
            focused(&["AXTextField", "To: "]),
        ),
        (
            "a cc: prefix is email at medium",
            RULE_EMAIL_REGEX,
            focused(&["cc:"]),
        ),
        (
            "a bare address is email at weak",
            RULE_EMAIL_REGEX,
            focused(&["AXTextArea", "ray@example.com"]),
        ),
        (
            "an address with dots, plus and a multi-label domain matches",
            RULE_EMAIL_REGEX,
            focused(&["first.last+tag@sub.example.co.uk"]),
        ),
        (
            "an address in parentheses matches",
            RULE_EMAIL_REGEX,
            focused(&["(ray@example.com)"]),
        ),
        (
            "a one-letter TLD does NOT match",
            RULE_EMAIL_REGEX,
            focused(&["ray@example.c"]),
        ),
        (
            "a domain with no dot does NOT match",
            RULE_EMAIL_REGEX,
            focused(&["ray@example"]),
        ),
        (
            "an empty local part does NOT match",
            RULE_EMAIL_REGEX,
            focused(&["@example.com"]),
        ),
        (
            "a trailing word character after the TLD does NOT match",
            RULE_EMAIL_REGEX,
            focused(&["ray@example.com1"]),
        ),
        (
            "blank focused pieces are dropped",
            RULE_EMAIL_REGEX,
            focused(&["", "   "]),
        ),
        (
            "the focused element is tried after every catalog signal",
            RULE_EMAIL_REGEX,
            RequestVector {
                focused_pieces: vec!["ray@example.com".into()],
                ..title("zsh - Terminal")
            },
        ),
    ]
}

fn webmail_cases() -> Vec<(&'static str, &'static str)> {
    vec![
        ("Inbox (12) - Gmail", "keyword"),
        ("Mail - outlook.office.com", "keyword"),
        ("iCloud Mail", "keyword"),
        ("Proton Mail", "keyword"),
        // The Google Workspace shape: "<address> Mail", no keyword at all.
        // macOS had this fallback and Windows did not.
        ("ray@acme.co Mail", "address"),
        ("Acme Team - Slack", "none"),
        ("Untitled document", "none"),
        ("", "none"),
    ]
}

// ---------------------------------------------------------------------------
// Running the vectors
// ---------------------------------------------------------------------------

fn app_type_name(t: &hyperwhisper_core::ffi_catalog::ClassifiedAppType) -> &'static str {
    use hyperwhisper_core::ffi_catalog::ClassifiedAppType as T;
    match t {
        T::Email => "Email",
        T::Ai => "Ai",
        T::WorkMessaging => "WorkMessaging",
        T::PersonalMessaging => "PersonalMessaging",
        T::Document => "Document",
        T::Code => "Code",
        T::Terminal => "Terminal",
        T::Sensitive => "Sensitive",
        T::Other => "Other",
    }
}

fn classify(request: &RequestVector) -> ExpectedVector {
    let result = app_classify(AppClassifyRequest {
        bundle_id: request.bundle_id.clone(),
        process_name: request.process_name.clone(),
        app_name: request.app_name.clone(),
        host: request.host.clone(),
        host_confidence: request.host_confidence.clone(),
        title: request.title.clone(),
        focused_pieces: request.focused_pieces.clone(),
    });
    ExpectedVector {
        app_type: app_type_name(&result.app_type).to_string(),
        prompt_value: result.prompt_value,
        category: result.category,
        text_input_format: result.text_input_format,
        confidence: result.confidence,
        source: result.source,
        matched: result.matched,
    }
}

fn build_document() -> Document {
    Document {
        description: "Golden app-type classification vectors (issue #279). \
            Generated from hw-catalog by `cargo test -p hw-core --test app_type_vectors \
            -- --ignored regenerate`, and run unchanged by Rust, Swift and C#. Covers the \
            8-element priority order, the title word-boundary rule, the host-suffix rule \
            and the email regex."
            .to_string(),
        classifications: cases()
            .into_iter()
            .map(|(name, rule, request)| CaseVector {
                name: name.to_string(),
                rule: rule.to_string(),
                expected: classify(&request),
                request,
            })
            .collect(),
        webmail_titles: webmail_cases()
            .into_iter()
            .map(|(title, branch)| WebmailVector {
                title: title.to_string(),
                expected: app_is_webmail(title.to_string()),
                branch: branch.to_string(),
            })
            .collect(),
    }
}

fn load_document() -> Document {
    let raw = std::fs::read_to_string(vectors_path()).expect("app-type-vectors.json must exist");
    serde_json::from_str(&raw).expect("app-type-vectors.json must parse")
}

/// The committed vectors are exactly what the shared core answers today. This
/// is the whole point of the file: it fails on a behaviour change that was not
/// deliberately regenerated and reviewed.
#[test]
fn vectors_match_the_shared_core() {
    let expected = load_document();
    let actual = build_document();

    assert_eq!(
        expected.classifications.len(),
        actual.classifications.len(),
        "the committed vectors and the generator disagree on the case list — regenerate"
    );
    for (want, got) in expected
        .classifications
        .iter()
        .zip(actual.classifications.iter())
    {
        assert_eq!(want.name, got.name, "case order changed — regenerate");
        assert_eq!(
            want.request, got.request,
            "inputs changed for {:?}",
            want.name
        );
        assert_eq!(
            want.expected, got.expected,
            "answer changed for {:?}",
            want.name
        );
    }

    assert_eq!(expected.webmail_titles, actual.webmail_titles);
}

/// A vector set is only proof while it still exercises both halves of every
/// rule. This fails if a future edit deletes the last negative case, the last
/// non-default source, or the last row of a rule.
#[test]
fn vectors_cover_every_rule_and_both_of_its_halves() {
    let doc = load_document();

    for rule in RULES {
        let count = doc
            .classifications
            .iter()
            .filter(|c| c.rule == rule)
            .count();
        assert!(count >= 4, "rule {rule} has only {count} vectors");
    }
    let unknown: Vec<&str> = doc
        .classifications
        .iter()
        .map(|c| c.rule.as_str())
        .filter(|r| !RULES.contains(r))
        .collect();
    assert!(unknown.is_empty(), "unknown rule labels: {unknown:?}");

    // Every app type is claimed by at least one vector, so a reordered
    // priority array cannot hide behind an untested type.
    for app_type in [
        "Email",
        "Ai",
        "WorkMessaging",
        "PersonalMessaging",
        "Document",
        "Code",
        "Terminal",
        "Sensitive",
        "Other",
    ] {
        assert!(
            doc.classifications
                .iter()
                .any(|c| c.expected.app_type == app_type),
            "no vector classifies as {app_type}"
        );
    }

    // Every signal produces at least one vector, so a dropped FFI field fails
    // here rather than shipping.
    for source in [
        "browserHost",
        "bundleId",
        "processName",
        "title",
        "appName",
        "focusedElement",
        "focusedElementText",
        "default",
    ] {
        assert!(
            doc.classifications
                .iter()
                .any(|c| c.expected.source == source),
            "no vector reaches the {source} signal"
        );
    }

    // Both halves of each rule.
    let negative = |rule: &str| {
        doc.classifications
            .iter()
            .filter(|c| c.rule == rule && c.expected.source == "default")
            .count()
    };
    assert!(
        negative(RULE_WORD_BOUNDARY) >= 3,
        "the word-boundary rule has too few misses"
    );
    assert!(
        negative(RULE_HOST_SUFFIX) >= 2,
        "the host-suffix rule has too few misses"
    );
    assert!(
        negative(RULE_EMAIL_REGEX) >= 4,
        "the email regex has too few misses"
    );

    // The host confidence must be exercised as BOTH a caller value and the
    // empty-string fallback, or the drift this change settles goes untested.
    assert!(
        doc.classifications
            .iter()
            .any(|c| c.expected.source == "browserHost" && c.expected.confidence == "manual"),
        "no vector proves the caller's host confidence is passed through"
    );
    assert!(
        doc.classifications
            .iter()
            .any(|c| c.expected.source == "browserHost"
                && c.request.host_confidence.is_empty()
                && c.expected.confidence == "strong"),
        "no vector proves the empty host confidence falls back to strong"
    );

    // is_webmail: both true branches and at least one false.
    for branch in ["keyword", "address", "none"] {
        assert!(
            doc.webmail_titles.iter().any(|w| w.branch == branch),
            "no webmail vector exercises the {branch} branch"
        );
    }
    assert!(
        doc.webmail_titles
            .iter()
            .any(|w| w.branch == "address" && w.expected),
        "the webmail address fallback is not proven"
    );
    assert!(
        doc.webmail_titles.iter().any(|w| !w.expected),
        "no webmail vector is negative"
    );
}

/// Writes the vectors from the current shared-core answer. Ignored by default;
/// run it deliberately after an intended catalog or classifier change, then
/// read the diff.
#[test]
#[ignore = "regenerates shared-conformance/app-type-vectors.json"]
fn regenerate() {
    let doc = build_document();
    let mut json = serde_json::to_string_pretty(&doc).expect("vectors must serialize");
    json.push('\n');
    std::fs::write(vectors_path(), json).expect("vectors must be writable");
    eprintln!("wrote {}", vectors_path().display());
}
