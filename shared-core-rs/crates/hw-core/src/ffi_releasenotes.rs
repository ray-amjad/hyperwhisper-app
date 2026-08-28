//! UniFFI surface for the release-notes HTML parser (`hw_releasenotes`, #284).
//!
//! Follows the `ffi_catalog` shape: the leaf crate's types are **mirrored** here
//! as owned `uniffi::Record`/`uniffi::Enum` types with `From` impls, rather than
//! re-exported, so `hw-releasenotes` stays a plain, dependency-free crate that
//! can be fuzzed and unit-tested without UniFFI in the way.
//!
//! # The `Hw` prefix is not cosmetic
//!
//! An unprefixed `Run` would generate `Run` in `hyperwhisper_core.cs`, which
//! collides with `System.Windows.Documents.Run` — the WPF type
//! `InlineHtmlText.cs` builds for every styled span. `HwRun` keeps both
//! reachable without a namespace alias in the head.
//!
//! # `link` is a `String`, not a URL
//!
//! UniFFI has no URL type. Returning the href verbatim keeps the security
//! decision — the `http`/`https`/`mailto` allowlist — inside Rust, while each
//! platform builds `URL(string:)` / `Uri.TryCreate(...)` itself. That also
//! sidesteps the `URL`-vs-`Uri` normalization drift the two heads already
//! accommodate deliberately (`ReleaseNotesHTMLTests.swift:139`,
//! `Program.cs:3964`). A native constructor that then fails yields no link,
//! which is exactly today's behaviour for the same input.

/// A stretch of release-notes text that shares one style and, if it sits inside
/// an `<a href>`, one destination. Mirrors `hw_releasenotes::Run`.
#[derive(uniffi::Record)]
pub struct HwRun {
    pub text: String,
    pub bold: bool,
    pub italic: bool,
    /// The feed's href verbatim, entity-decoded and trimmed, already checked
    /// against the scheme allowlist. `None` when the anchor had no usable href.
    pub link: Option<String>,
}

impl From<hw_releasenotes::Run> for HwRun {
    fn from(run: hw_releasenotes::Run) -> Self {
        HwRun {
            text: run.text,
            bold: run.bold,
            italic: run.italic,
            link: run.link,
        }
    }
}

/// Split a release-notes fragment into styled runs.
///
/// `collapse_whitespace` false keeps the fragment's own line breaks, for callers
/// that split the result into lines; it preserves the optional parameter on
/// `InlineHtml.Parse` / `InlineHtml.PlainText`. macOS always passes `true`.
#[uniffi::export]
pub fn release_notes_parse_inline(html: String, collapse_whitespace: bool) -> Vec<HwRun> {
    hw_releasenotes::parse_inline(&html, collapse_whitespace)
        .into_iter()
        .map(HwRun::from)
        .collect()
}

/// Tag-free, entity-decoded text for a release-notes fragment — for titles,
/// glyph selection, logging and tests.
#[uniffi::export]
pub fn release_notes_plain_text(html: String, collapse_whitespace: bool) -> String {
    hw_releasenotes::plain_text(&html, collapse_whitespace)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The mirror carries every field through unchanged, and the exported
    /// functions agree with the leaf crate they wrap.
    #[test]
    fn the_ffi_mirror_matches_the_leaf_crate() {
        let html = r#"<b>New</b> — see the <a href="https://example.com/x">page</a>."#;

        let runs = release_notes_parse_inline(html.to_string(), true);
        assert_eq!(runs.len(), 4);
        assert_eq!(runs[0].text, "New");
        assert!(runs[0].bold);
        assert!(runs[0].link.is_none());
        assert_eq!(runs[2].text, "page");
        assert_eq!(runs[2].link.as_deref(), Some("https://example.com/x"));

        assert_eq!(
            release_notes_plain_text(html.to_string(), true),
            "New — see the page."
        );
        assert_eq!(
            release_notes_plain_text(html.to_string(), true),
            hw_releasenotes::plain_text(html, true)
        );
    }

    /// The allowlist decision travels across the boundary: a hostile href
    /// arrives as `None`, not as a string the head might construct a URL from.
    #[test]
    fn a_rejected_scheme_crosses_the_boundary_as_none() {
        let runs =
            release_notes_parse_inline(r#"<a href="javascript:alert(1)">x</a>"#.to_string(), true);
        assert_eq!(runs.len(), 1);
        assert_eq!(runs[0].text, "x");
        assert!(runs[0].link.is_none());
    }

    /// The flag reaches the leaf crate rather than being ignored at the
    /// boundary.
    #[test]
    fn the_collapse_whitespace_flag_crosses_the_boundary() {
        assert_eq!(
            release_notes_plain_text("a\r\n\r\n  b".to_string(), true),
            "a b"
        );
        assert_eq!(
            release_notes_plain_text("a\r\n\r\n  b".to_string(), false),
            "a\r\n\r\n  b"
        );
    }
}
