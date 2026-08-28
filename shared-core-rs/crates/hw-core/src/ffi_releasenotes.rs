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

/// What a block is for. Mirrors `hw_releasenotes::BlockKind`.
#[derive(uniffi::Enum)]
pub enum HwBlockKind {
    Heading,
    Bullet,
    Paragraph,
}

impl From<hw_releasenotes::BlockKind> for HwBlockKind {
    fn from(kind: hw_releasenotes::BlockKind) -> Self {
        match kind {
            hw_releasenotes::BlockKind::Heading => HwBlockKind::Heading,
            hw_releasenotes::BlockKind::Bullet => HwBlockKind::Bullet,
            hw_releasenotes::BlockKind::Paragraph => HwBlockKind::Paragraph,
        }
    }
}

/// One block-level element of a release note, already split into styled runs.
/// Mirrors `hw_releasenotes::Block`.
///
/// The runs sit inside the block rather than in a parallel sequence: issue #284
/// names `Vec<Vec<Run>>` as the shape to avoid, because nothing in it says which
/// inner sequence is the heading, and both heads had to re-derive that from
/// position.
#[derive(uniffi::Record)]
pub struct HwBlock {
    pub kind: HwBlockKind,
    pub runs: Vec<HwRun>,
}

impl From<hw_releasenotes::Block> for HwBlock {
    fn from(block: hw_releasenotes::Block) -> Self {
        HwBlock {
            kind: block.kind.into(),
            runs: block.runs.into_iter().map(HwRun::from).collect(),
        }
    }
}

/// A release note as the update cards render it. Mirrors
/// `hw_releasenotes::ReleaseNote`.
#[derive(uniffi::Record)]
pub struct HwReleaseNote {
    /// The heading above the bullet list, or `None` when the note has none.
    pub title: Option<HwBlock>,
    pub bullets: Vec<HwBlock>,
}

impl From<hw_releasenotes::ReleaseNote> for HwReleaseNote {
    fn from(note: hw_releasenotes::ReleaseNote) -> Self {
        HwReleaseNote {
            title: note.title.map(HwBlock::from),
            bullets: note.bullets.into_iter().map(HwBlock::from).collect(),
        }
    }
}

/// Every block of a release note, in document order — the update dialog's view.
///
/// A note with no block markup at all falls back to one block per line, so a
/// plain-text changelog still renders as a list. Each line keeps its own markup
/// and is parsed exactly once here; see `hw_releasenotes::split_blocks`.
#[uniffi::export]
pub fn release_notes_split_blocks(html: String) -> Vec<HwBlock> {
    hw_releasenotes::split_blocks(&html)
        .into_iter()
        .map(HwBlock::from)
        .collect()
}

/// A release note split into its heading and its bullets — the release-notes
/// cards' view, and the single source of truth for the title rule (decision (c)
/// of #284: the first `<h2>` case-insensitively, else the content before the
/// list).
#[uniffi::export]
pub fn release_notes_parse(html: String) -> HwReleaseNote {
    hw_releasenotes::parse_release_note(&html).into()
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

    /// The block mirror carries kind, nesting and runs through unchanged for
    /// both feed shapes.
    #[test]
    fn the_block_mirror_matches_the_leaf_crate() {
        let windows = "<h2>What's New in 1.11.0</h2><ul><li><b>a</b></li><li>b</li></ul>";

        let blocks = release_notes_split_blocks(windows.to_string());
        assert_eq!(blocks.len(), 3);
        assert!(matches!(blocks[0].kind, HwBlockKind::Heading));
        assert!(matches!(blocks[1].kind, HwBlockKind::Bullet));
        assert_eq!(blocks[0].runs[0].text, "What's New in 1.11.0");
        assert!(blocks[1].runs[0].bold);

        let note = release_notes_parse(windows.to_string());
        let title = note.title.expect("the <h2> is the title");
        assert!(matches!(title.kind, HwBlockKind::Heading));
        assert_eq!(title.runs[0].text, "What's New in 1.11.0");
        assert_eq!(note.bullets.len(), 2);
        assert_eq!(note.bullets[1].runs[0].text, "b");

        // The macOS feed shape: no heading at all, and the first bullet's <b>
        // is emphasis rather than a title.
        let macos = "<ul><li><b>Gemini 3.5 Transcribe is here.</b> Details.</li></ul>";
        let note = release_notes_parse(macos.to_string());
        assert!(note.title.is_none());
        assert_eq!(note.bullets.len(), 1);
        assert!(note.bullets[0].runs[0].bold);
    }

    /// The `<br>` fallback's double-parse guard survives the boundary: escaped
    /// markup arrives as text with no link, not as a live anchor.
    #[test]
    fn escaped_markup_crosses_the_boundary_as_text() {
        let blocks =
            release_notes_split_blocks(r#"Write &lt;a href="https://evil.example"&gt;x&lt;/a&gt;"#.to_string());
        assert_eq!(blocks.len(), 1);
        assert_eq!(
            blocks[0].runs[0].text,
            r#"Write <a href="https://evil.example">x</a>"#
        );
        assert!(blocks[0].runs.iter().all(|run| run.link.is_none()));
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
