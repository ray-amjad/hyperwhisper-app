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
//! `HwAppcastFeedEntry` / `HwAppcastRelease` (#353) make the same point twice
//! over: an unprefixed `AppcastItem` would collide with
//! `app/macos/hyperwhisper/Models/AppcastItem.swift` **and**
//! `app/windows/HyperWhisper/Models/AppcastItem.cs`, because the generated
//! Swift binding is compiled into the same module as the app's own types. The
//! output record is not called `HwAppcastItem` either — it is a *selected,
//! normalised* release, which is a different thing from the head's view model,
//! and the name should say so.
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

// ---------------------------------------------------------------------------
// The appcast item-selection step (#353).
//
// The XML reader stays native on each head — `XMLParser` on macOS, `XDocument`
// on Windows — and hands the raw fields across in document order. Everything
// that decides what a release IS lives in `hw_releasenotes::appcast`: which
// element supplies the version, which entries are dropped, how duplicates
// collapse and how the list is ordered. Those are the rules the two heads had
// drifted on, and each head's rule was wrong on the other head's feed.
// ---------------------------------------------------------------------------

/// One raw `<item>`, exactly as a native XML reader found it. Mirrors
/// `hw_releasenotes::FeedEntry`.
///
/// **No rules applied.** Every field is the feed's own text, untrimmed, and
/// `None` means the element was absent rather than empty. A reader that decides
/// anything for itself is drift waiting to happen.
#[derive(uniffi::Record)]
pub struct HwAppcastFeedEntry {
    /// `<title>`. Free-form prose: `2.46.0` on the macOS feed,
    /// `1.11.0 (ARM64)` on the Windows one.
    pub title: Option<String>,
    /// `sparkle:version` — the machine build identifier.
    pub sparkle_version: Option<String>,
    /// `sparkle:shortVersionString` — the human-readable version.
    pub sparkle_short_version_string: Option<String>,
    /// `<pubDate>`, as RFC 2822 text.
    pub pub_date: Option<String>,
    /// `<description>` — the inline release notes, HTML.
    pub description: Option<String>,
    /// Whether `sparkle:releaseNotesLink` was present. On macOS that is
    /// `item.Element(sparkle + "releaseNotesLink") != null`; the head sets the
    /// flag and Rust decides what it means.
    pub has_release_notes_link: bool,
}

impl From<HwAppcastFeedEntry> for hw_releasenotes::FeedEntry {
    fn from(entry: HwAppcastFeedEntry) -> Self {
        hw_releasenotes::FeedEntry {
            title: entry.title,
            sparkle_version: entry.sparkle_version,
            sparkle_short_version_string: entry.sparkle_short_version_string,
            pub_date: entry.pub_date,
            description: entry.description,
            has_release_notes_link: entry.has_release_notes_link,
        }
    }
}

/// One selected, normalised release, ready to render. Mirrors
/// `hw_releasenotes::Release`.
#[derive(uniffi::Record)]
pub struct HwAppcastRelease {
    /// The resolved version, trimmed and non-empty.
    pub version: String,
    /// The raw `sparkle:version`, trimmed, for macOS's
    /// `AppcastItem.buildNumber`. Windows has no such field. A passthrough, not
    /// a rule.
    pub build_number: Option<String>,
    /// Seconds since the Unix epoch, and **0 when `<pubDate>` was absent, blank
    /// or unparseable** — which sorts the entry last rather than first.
    ///
    /// Not an `Option`: that would turn `pubDate` into `Date?` / `DateTime?` on
    /// both heads and ripple into their formatters and equality, for an input
    /// that has never occurred in 129 committed feed items. `i64` seconds also
    /// keeps this side of the boundary clock-free, per `ffi_license.rs`.
    ///
    /// The Rust side bounds this value to
    /// `[hw_releasenotes::MIN_REPRESENTABLE_EPOCH_SECS,
    /// hw_releasenotes::MAX_REPRESENTABLE_EPOCH_SECS]` — i.e.
    /// `DateTimeOffset`'s own range, in whole seconds — which is what stops
    /// Windows' `DateTimeOffset.FromUnixTimeSeconds` throwing on a hostile
    /// feed. The bound is on the final UTC instant, after the feed's zone
    /// offset is applied, and not on the written year: a `-0100` offset on
    /// `31 Dec 9999` moves a legal-looking year past the maximum.
    pub pub_date_epoch_secs: i64,
    /// The inline release notes, trimmed and non-empty. Feed this straight to
    /// `release_notes_parse`.
    pub release_notes: String,
}

impl From<hw_releasenotes::Release> for HwAppcastRelease {
    fn from(release: hw_releasenotes::Release) -> Self {
        HwAppcastRelease {
            version: release.version,
            build_number: release.build_number,
            pub_date_epoch_secs: release.pub_date_epoch_secs,
            release_notes: release.release_notes,
        }
    }
}

/// Turn a feed's `<item>`s, **in document order**, into the releases the update
/// UI renders: filter, then dedupe, then a stable newest-first sort.
///
/// The caller must not re-filter, re-dedupe or re-sort the result — a leftover
/// `.Where` or `.OrderByDescending` in a head is exactly the drift this
/// replaces. The result carries no cap: `prefix(5)` on macOS and
/// `Take(maxCount)` on Windows stay native, because the two heads cap at
/// different points relative to their caches. Windows' `IsLatest` stays native
/// too — it means "index 0 of this list", not a property of a feed entry.
#[uniffi::export]
pub fn appcast_select_releases(entries: Vec<HwAppcastFeedEntry>) -> Vec<HwAppcastRelease> {
    let entries: Vec<hw_releasenotes::FeedEntry> = entries.into_iter().map(Into::into).collect();
    hw_releasenotes::select_releases(entries)
        .into_iter()
        .map(HwAppcastRelease::from)
        .collect()
}

/// Parse an RFC 2822 `<pubDate>` into seconds since the Unix epoch, or `None`
/// when it is not a date this accepts.
///
/// Exported separately so each head's own suite can pin the malformed-date case
/// without building a whole feed, and so a future caller does not re-implement
/// it. It is culture-free by construction: fixed English month abbreviations,
/// offset-aware, no locale and no clock — which is the fix for Windows'
/// `DateTime.TryParse` under `CurrentCulture`.
#[uniffi::export]
pub fn appcast_parse_pub_date(value: String) -> Option<i64> {
    hw_releasenotes::parse_pub_date(&value)
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

    fn feed_entry() -> HwAppcastFeedEntry {
        HwAppcastFeedEntry {
            title: None,
            sparkle_version: None,
            sparkle_short_version_string: None,
            pub_date: None,
            description: Some("<ul><li>a</li></ul>".to_string()),
            has_release_notes_link: false,
        }
    }

    /// Every field of both appcast records survives the boundary, in both
    /// directions — the inbound record the head builds and the outbound one it
    /// reads.
    #[test]
    fn the_appcast_mirror_carries_every_field_both_ways() {
        let releases = appcast_select_releases(vec![HwAppcastFeedEntry {
            title: Some("2.46.0".to_string()),
            sparkle_version: Some("116".to_string()),
            sparkle_short_version_string: Some("2.46.0".to_string()),
            pub_date: Some("Wed, 02 Sep 2026 12:06:28 +0000".to_string()),
            description: Some("  <ul><li>New</li></ul>  ".to_string()),
            has_release_notes_link: false,
        }]);

        assert_eq!(releases.len(), 1);
        assert_eq!(releases[0].version, "2.46.0");
        assert_eq!(releases[0].build_number.as_deref(), Some("116"));
        assert_eq!(releases[0].pub_date_epoch_secs, 1_788_350_788);
        assert_eq!(releases[0].release_notes, "<ul><li>New</li></ul>");

        // The `has_release_notes_link` flag is the one bool on the inbound
        // record; prove it is read rather than dropped at the boundary.
        let dropped = appcast_select_releases(vec![HwAppcastFeedEntry {
            has_release_notes_link: true,
            ..feed_entry_with_version("2.46.0")
        }]);
        assert!(dropped.is_empty());
    }

    fn feed_entry_with_version(version: &str) -> HwAppcastFeedEntry {
        HwAppcastFeedEntry {
            sparkle_short_version_string: Some(version.to_string()),
            ..feed_entry()
        }
    }

    /// Ordering and dedupe survive the boundary intact — the heads must not
    /// re-sort, so the order they receive is the order Rust chose.
    #[test]
    fn the_appcast_pipeline_crosses_the_boundary_intact() {
        let dated = |version: &str, pub_date: &str| HwAppcastFeedEntry {
            pub_date: Some(pub_date.to_string()),
            ..feed_entry_with_version(version)
        };

        let releases = appcast_select_releases(vec![
            dated("1.11.0", "Sun, 16 Aug 2026 04:17:53 +0000"),
            dated("1.11.0", "Sun, 16 Aug 2026 04:17:53 +0000"),
            dated("2.46.0", "Wed, 02 Sep 2026 12:06:28 +0000"),
            dated("broken", "not a date"),
        ]);

        let versions: Vec<&str> = releases.iter().map(|r| r.version.as_str()).collect();
        assert_eq!(versions, ["2.46.0", "1.11.0", "broken"]);
        assert_eq!(releases[2].pub_date_epoch_secs, 0);
    }

    /// The date parser is reachable on its own, and agrees with the leaf crate.
    #[test]
    fn the_date_parser_crosses_the_boundary() {
        let text = "Wed, 02 Sep 2026 12:06:28 +0000";
        assert_eq!(
            appcast_parse_pub_date(text.to_string()),
            hw_releasenotes::parse_pub_date(text)
        );
        assert_eq!(
            appcast_parse_pub_date(text.to_string()),
            Some(1_788_350_788)
        );
        assert_eq!(appcast_parse_pub_date("nonsense".to_string()), None);
    }

    /// The two layers compose: the notes a selected release carries are exactly
    /// what `release_notes_parse` expects.
    #[test]
    fn a_selected_releases_notes_feed_the_html_layer() {
        let releases = appcast_select_releases(vec![HwAppcastFeedEntry {
            description: Some(
                "\n <h2>What's New in 1.11.0</h2><ul><li><b>a</b></li></ul>\n".to_string(),
            ),
            ..feed_entry_with_version("1.11.0")
        }]);
        let note = release_notes_parse(releases[0].release_notes.clone());
        let title = note.title.expect("the <h2> is the title");
        assert_eq!(title.runs[0].text, "What's New in 1.11.0");
        assert_eq!(note.bullets.len(), 1);
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
