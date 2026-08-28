//! The block layer: `<h2>`/`<h3>`, `<li>` and `<p>` turned into blocks of
//! styled runs, plus the release-note shape the update cards render.
//!
//! Where [`crate::parse_inline`] replaced two copies of one tokenizer, this
//! replaces *three* copies of one `<li>` extractor (issue #284):
//!
//! | site | how it found list items before |
//! |---|---|
//! | `AppcastItem.swift` (`listItemHTML`) | `range(of: "<li")`, then the first `>`, then `range(of: "</li")` |
//! | `AppcastItem.cs` (`BulletPoints`) | `Regex "<li[^>]*>(.*?)</li>"`, `IgnoreCase | Singleline` |
//! | `UpdateAvailableWindow.xaml.cs` | `Regex "<(h[23]|li|p)[^>]*>(.*?)</\1>"`, plus a `<br>` fallback |
//!
//! # The three disagreed, and this file picks a winner for each
//!
//! * **`</li >`** — a closing tag with whitespace before its `>`. macOS searched
//!   for the prefix `</li`, so it closed the item; the C# backreference `</\1>`
//!   needs the exact three characters, so it did not match at all and the bullet
//!   vanished. **macOS wins**: this scanner tokenizes with [`crate::tag`] rather
//!   than pattern-matching, so `</li >`, `</LI>` and `</li\n>` all close the
//!   item. Dropping a bullet a feed wrote is the worse failure.
//! * **`<li class="a>b">`** — a `>` inside a quoted attribute value. Both
//!   regexes' `[^>]*` ends the open tag at that `>`, so the item's text starts
//!   with `b">`. The tokenizer already knows a quoted value may carry a `>`
//!   (`tag::tag_end`), so the attribute stays an attribute.
//! * **`<H2 id="x">`** — Windows' title regex was case-*sensitive* and allowed
//!   no attributes. Decision (c) makes the title match case-insensitive, with
//!   attributes, on both heads. See [`parse_release_note`].
//!
//! # Emptiness
//!
//! A block whose content carries no text is dropped. All three sites agreed on
//! that already — `PlainText(content).Length == 0`, and `!characters.isEmpty` on
//! macOS — and it is what keeps `<li>  </li>` out of a bullet list. A block's
//! runs are therefore never empty, which is asserted in the fuzz target.

use crate::inline::{parse_inline, plain_text, Run};
use crate::tag;

/// What a block is for. `Heading` covers `<h2>` and `<h3>`; only an `<h2>` may
/// become a release title (see [`parse_release_note`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BlockKind {
    Heading,
    Bullet,
    Paragraph,
}

/// One block-level element, already split into styled runs.
///
/// The runs live here rather than in a parallel `Vec<Vec<Run>>` on purpose:
/// issue #284 calls a nested vector out by name, because nothing in it says
/// which inner vector is the heading and which are bullets, and the C# and Swift
/// heads had to re-derive that from position.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Block {
    pub kind: BlockKind,
    pub runs: Vec<Run>,
}

/// A release note as the two update cards render it: an optional heading and the
/// bullet list under it.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ReleaseNote {
    pub title: Option<Block>,
    pub bullets: Vec<Block>,
}

/// The block-level elements this layer understands, mirroring the C# walker's
/// `<(h[23]|li|p)>`. Everything else stays inline and is handled by
/// [`crate::parse_inline`] — a `<ul>` or a `<div>` is transparent here.
fn block_kind(name: &str) -> Option<BlockKind> {
    match name {
        "h2" | "h3" => Some(BlockKind::Heading),
        "li" => Some(BlockKind::Bullet),
        "p" => Some(BlockKind::Paragraph),
        _ => None,
    }
}

/// One scanned element: its lowercased name, its kind, and its inner HTML with
/// markup intact — the inner markup is inline emphasis and belongs to the block,
/// so it is parsed exactly once, by the caller.
struct Scanned {
    name: String,
    kind: BlockKind,
    content: String,
}

/// Index of the `<` opening `</name>`, and the index just past its `>`.
///
/// `None` when the element is never closed, which is what the C# regex reported
/// too: no match, so the open tag is skipped and scanning continues after it
/// rather than swallowing the rest of the fragment.
fn find_close(chars: &[char], from: usize, name: &str) -> Option<(usize, usize)> {
    let mut index = from;

    while let Some(&character) = chars.get(index) {
        if character != '<' {
            index = index.saturating_add(1);
            continue;
        }

        // An unterminated `<` means there is no closing tag left to find.
        let end = tag::tag_end(chars, index)?;
        let body = chars
            .get(index.saturating_add(1)..end)
            .unwrap_or(&[])
            .to_vec();
        let parsed = tag::parse_tag(&body);

        if parsed.is_closing && parsed.name == name {
            return Some((index, end.saturating_add(1)));
        }

        index = end.saturating_add(1);
    }

    None
}

/// Every block-level element in document order, with its inner HTML.
///
/// Nesting is not tracked, and deliberately so: the first matching close tag
/// ends the element, which is exactly what the two non-greedy `(.*?)</\1>`
/// regexes did. A `<p>` inside an `<li>` is therefore part of the bullet's
/// inline content, not a second block, because scanning resumes after the
/// `</li>`.
fn scan_block_elements(chars: &[char]) -> Vec<Scanned> {
    let mut found: Vec<Scanned> = Vec::new();
    let mut index = 0usize;

    while let Some(&character) = chars.get(index) {
        if character != '<' {
            index = index.saturating_add(1);
            continue;
        }

        let Some(open_end) = tag::tag_end(chars, index) else {
            // Unterminated: the rest is text, so there are no more elements.
            break;
        };
        let body = chars
            .get(index.saturating_add(1)..open_end)
            .unwrap_or(&[])
            .to_vec();
        let parsed = tag::parse_tag(&body);
        let after_open = open_end.saturating_add(1);

        // A closing or self-closing tag opens nothing, and a tag that is not
        // block-level is inline content the block layer steps over.
        let Some(kind) = block_kind(parsed.name.as_str()) else {
            index = after_open;
            continue;
        };
        if parsed.is_closing || parsed.is_self_closing {
            index = after_open;
            continue;
        }

        match find_close(chars, after_open, &parsed.name) {
            Some((content_end, after_close)) => {
                found.push(Scanned {
                    name: parsed.name,
                    kind,
                    content: chars
                        .get(after_open..content_end)
                        .unwrap_or(&[])
                        .iter()
                        .collect(),
                });
                index = after_close;
            }
            None => index = after_open,
        }
    }

    found
}

/// A block from inner HTML, or `None` when it carries no text at all.
fn block_from(kind: BlockKind, content: &str) -> Option<Block> {
    let runs = parse_inline(content, true);
    if runs.is_empty() {
        None
    } else {
        Some(Block { kind, runs })
    }
}

/// First index at which `needle` occurs in `chars`, ASCII-case-insensitively.
///
/// Every needle here is ASCII (`<ul`, `<li`), so this is equivalent to the
/// `String.range(of:options: .caseInsensitive)` macOS used, without dragging in
/// a Unicode case-folding table.
fn find_ignoring_case(chars: &[char], needle: &str) -> Option<usize> {
    let needle: Vec<char> = needle.chars().collect();
    if needle.is_empty() || chars.len() < needle.len() {
        return None;
    }

    let last = chars.len().saturating_sub(needle.len());
    for start in 0..=last {
        let window = chars.get(start..start.saturating_add(needle.len()))?;
        if window
            .iter()
            .zip(needle.iter())
            .all(|(a, b)| a.eq_ignore_ascii_case(b))
        {
            return Some(start);
        }
    }

    None
}

/// Split lines for the fallback branch: on a `<br>` and on a newline, with every
/// other tag left in the line verbatim.
///
/// The verbatim part is the whole point. Flattening the fragment to text here
/// and parsing the result again in the caller dropped every `<a href>` before it
/// could be rendered — and, worse, turned markup a feed had *escaped* so it
/// would show, `&lt;a href=…&gt;`, into a live link, because the first pass
/// decoded the entities and the second pass then read the result as a tag. Each
/// line below is raw HTML, parsed exactly once.
///
/// The `<br>` test is [`crate::tag`]'s, not the C# regex `<br\s*/?>`, so
/// `<br class="x">` and `</br>` end a line here as well. That matches the inline
/// layer, which turns every tag named `br` into a newline whatever else it
/// carries — and the inline layer is what produced the newlines this branch used
/// to split on.
fn fallback_lines(chars: &[char]) -> Vec<String> {
    let mut lines: Vec<String> = Vec::new();
    let mut current = String::new();
    let mut index = 0usize;

    while let Some(&character) = chars.get(index) {
        if character == '\n' {
            lines.push(std::mem::take(&mut current));
            index = index.saturating_add(1);
            continue;
        }

        if character == '<' {
            if let Some(end) = tag::tag_end(chars, index) {
                let body = chars
                    .get(index.saturating_add(1)..end)
                    .unwrap_or(&[])
                    .to_vec();

                if tag::parse_tag(&body).name == "br" {
                    lines.push(std::mem::take(&mut current));
                } else {
                    current.extend(chars.get(index..=end).unwrap_or(&[]).iter());
                }

                index = end.saturating_add(1);
                continue;
            }
        }

        current.push(character);
        index = index.saturating_add(1);
    }

    lines.push(current);
    lines
}

/// The fallback: a note with no block markup at all is a run of lines.
///
/// A line that opens with `-` or `*` is a bullet and loses the marker, which is
/// how a plain-text changelog pasted into a feed still renders as a list.
fn fallback_blocks(chars: &[char]) -> Vec<Block> {
    let mut blocks: Vec<Block> = Vec::new();

    for line in fallback_lines(chars) {
        let trimmed = line.trim();

        // Emptiness is judged on the line as written, before the marker is
        // stripped — so a line of nothing but markup is dropped, and a line of
        // nothing but markers becomes a bullet with no text, which `block_from`
        // then drops as well. (The C# original added an empty card for that
        // second case; an empty card is a rendering bug, not a behaviour worth
        // preserving.)
        if plain_text(trimmed, true).trim().is_empty() {
            continue;
        }

        let is_bullet = trimmed.starts_with('-') || trimmed.starts_with('*');
        let body = if is_bullet {
            trimmed.trim_start_matches(['-', '*', ' '])
        } else {
            trimmed
        };

        let kind = if is_bullet {
            BlockKind::Bullet
        } else {
            BlockKind::Paragraph
        };
        if let Some(block) = block_from(kind, body) {
            blocks.push(block);
        }
    }

    blocks
}

/// Every block in a release note, in document order.
///
/// This is the update dialog's view of a note: headings, bullets and paragraphs
/// interleaved as the feed wrote them. [`parse_release_note`] is the cards'
/// view of the same fragment.
///
/// When the fragment carries no block markup at all, its lines are the blocks —
/// see [`fallback_blocks`]. That decision is taken on whether any block *element*
/// was found, not on whether any survived the emptiness filter, mirroring the C#
/// walker's `matches.Count > 0`: a note that is one empty `<p>` renders nothing,
/// rather than falling through and rendering its own markup as text.
pub fn split_blocks(html: &str) -> Vec<Block> {
    let chars: Vec<char> = html.trim().chars().collect();

    let scanned = scan_block_elements(&chars);
    if scanned.is_empty() {
        return fallback_blocks(&chars);
    }

    scanned
        .into_iter()
        .filter_map(|element| block_from(element.kind, &element.content))
        .collect()
}

/// A release note split into the heading the cards show above the bullet list,
/// and the bullets themselves.
///
/// # Decision (c): one rule for the title on both heads
///
/// The two feeds are shaped differently and neither head could read the other's:
///
/// * `appcast.xml` (macOS) has no heading at all — the note opens straight into
///   `<ul>`. macOS took "everything before the list" as the title, which is
///   empty here, so no title is shown.
/// * `appcast-windows.xml` opens with `<h2>What's New in 1.11.0</h2>`. Windows
///   took the first `<h2>`.
///
/// The rule is now both, in order: **the first `<h2>` if there is one,
/// case-insensitively and whatever attributes it carries; otherwise the content
/// before the first `<ul>`, or before the first `<li>` if there is no `<ul>`.**
/// Each feed keeps rendering exactly as it does today, and each head gains the
/// other's shape for free. An `<h3>` is never a title — it is a sub-heading in
/// the body, and `split_blocks` still reports it.
///
/// An `<h2>` that carries no text falls through to the second rule rather than
/// suppressing the title, so an empty heading cannot hide a real one.
///
/// Only `<li>` elements become bullets. The [`split_blocks`] fallback is not
/// applied: a card with no list renders its title and nothing else, exactly as
/// both heads do today, instead of repeating the whole note underneath itself.
pub fn parse_release_note(html: &str) -> ReleaseNote {
    let chars: Vec<char> = html.chars().collect();
    let scanned = scan_block_elements(&chars);

    let title = scanned
        .iter()
        .find(|element| element.name == "h2")
        .and_then(|element| block_from(BlockKind::Heading, &element.content))
        .or_else(|| {
            let cut = find_ignoring_case(&chars, "<ul")
                .or_else(|| find_ignoring_case(&chars, "<li"))
                .unwrap_or(chars.len());
            let heading: String = chars.get(..cut).unwrap_or(&[]).iter().collect();
            block_from(BlockKind::Heading, &heading)
        });

    let bullets = scanned
        .into_iter()
        .filter(|element| element.kind == BlockKind::Bullet)
        .filter_map(|element| block_from(element.kind, &element.content))
        .collect();

    ReleaseNote { title, bullets }
}

// ===========================================================================
// Tests
//
// The `AppcastItem` cases of both oracle suites, transliterated — the eight
// integration `@Test`s of `ReleaseNotesHTMLTests.swift` and the
// `AppcastItem.BulletPoints` / `ParseHtmlToTextBlocks` behaviour pinned by the
// Windows smoke suite — plus a case for every disagreement resolved above.
// ===========================================================================

#[cfg(test)]
mod tests {
    use super::*;

    fn texts(blocks: &[Block]) -> Vec<String> {
        blocks
            .iter()
            .map(|block| block.runs.iter().map(|run| run.text.as_str()).collect())
            .collect()
    }

    fn kinds(blocks: &[Block]) -> Vec<BlockKind> {
        blocks.iter().map(|block| block.kind).collect()
    }

    fn title_text(note: &ReleaseNote) -> Option<String> {
        note.title
            .as_ref()
            .map(|block| block.runs.iter().map(|run| run.text.as_str()).collect())
    }

    // -- split_blocks: the <li> extractor the three sites shared ------------

    #[test]
    fn list_items_are_returned_in_document_order() {
        let blocks = split_blocks("<ul><li>one</li><li>two</li><li>three</li></ul>");
        assert_eq!(texts(&blocks), ["one", "two", "three"]);
        assert_eq!(
            kinds(&blocks),
            [BlockKind::Bullet, BlockKind::Bullet, BlockKind::Bullet]
        );
    }

    /// `ReleaseNotesHTMLTests.listItemsWithAttributesAndEmptyItemsAreHandled`
    /// and the Windows `AppcastItem.BulletPoints` smoke case.
    #[test]
    fn attributes_are_allowed_and_textless_items_are_dropped() {
        let blocks = split_blocks(r#"<ul><li class="x">kept</li><li>  </li><li>also kept</li></ul>"#);
        assert_eq!(texts(&blocks), ["kept", "also kept"]);
    }

    /// The `</li >` disagreement, resolved macOS's way. The C# backreference
    /// `</\1>` did not match this at all, so the bullet was lost.
    #[test]
    fn a_closing_tag_with_whitespace_still_closes_the_item() {
        assert_eq!(texts(&split_blocks("<ul><li>one</li ><li>two</li></ul>")), ["one", "two"]);
        assert_eq!(texts(&split_blocks("<ul><li>one</LI><li>two</li></ul>")), ["one", "two"]);
        assert_eq!(texts(&split_blocks("<ul><li>one</li\n><li>two</li></ul>")), ["one", "two"]);
    }

    /// The `[^>]*` disagreement: a `>` inside a quoted attribute value used to
    /// end the open tag early and leak `b">` into the bullet's text.
    #[test]
    fn a_quoted_angle_bracket_in_an_attribute_stays_an_attribute() {
        assert_eq!(
            texts(&split_blocks(r#"<ul><li class="a>b">kept</li></ul>"#)),
            ["kept"]
        );
    }

    #[test]
    fn inline_emphasis_inside_a_bullet_survives_as_runs() {
        let blocks = split_blocks("<ul><li><b>Bold lead</b> — detail.</li></ul>");
        assert_eq!(blocks.len(), 1);
        assert_eq!(blocks[0].runs.len(), 2);
        assert!(blocks[0].runs[0].bold);
        assert_eq!(blocks[0].runs[0].text, "Bold lead");
        assert!(!blocks[0].runs[1].bold);
    }

    #[test]
    fn a_link_inside_a_bullet_keeps_its_destination() {
        let blocks =
            split_blocks(r#"<ul><li>see the <a href="https://example.com/x">page</a></li></ul>"#);
        assert_eq!(blocks.len(), 1);
        assert_eq!(
            blocks[0].runs.iter().filter_map(|run| run.link.as_deref()).collect::<Vec<_>>(),
            ["https://example.com/x"]
        );
    }

    #[test]
    fn an_unclosed_item_is_skipped_without_taking_the_rest_with_it() {
        assert_eq!(texts(&split_blocks("<ul><li>one</li><li>two</ul>")), ["one"]);
    }

    /// The C# walker's element set, in order, with kinds.
    #[test]
    fn headings_bullets_and_paragraphs_are_all_blocks() {
        let blocks =
            split_blocks("<h2>Title</h2><p>Intro</p><ul><li>a</li></ul><h3>More</h3><p>Outro</p>");
        assert_eq!(texts(&blocks), ["Title", "Intro", "a", "More", "Outro"]);
        assert_eq!(
            kinds(&blocks),
            [
                BlockKind::Heading,
                BlockKind::Paragraph,
                BlockKind::Bullet,
                BlockKind::Heading,
                BlockKind::Paragraph,
            ]
        );
    }

    /// A block element inside another is inline content, not a second block —
    /// scanning resumes after the outer close tag, as the non-greedy regex did.
    #[test]
    fn a_nested_block_element_does_not_become_a_second_block() {
        let blocks = split_blocks("<li><p>x</p></li>");
        assert_eq!(texts(&blocks), ["x"]);
        assert_eq!(kinds(&blocks), [BlockKind::Bullet]);
    }

    /// A note that is one textless block renders nothing at all: the fallback is
    /// chosen on whether a block element was FOUND, not on whether one survived.
    #[test]
    fn an_empty_block_element_does_not_trigger_the_fallback() {
        assert!(split_blocks("<p>   </p>").is_empty());
    }

    // -- split_blocks: the <br> fallback -----------------------------------

    #[test]
    fn a_note_with_no_block_markup_becomes_one_block_per_line() {
        let blocks = split_blocks("first line<br>second line\nthird line");
        assert_eq!(texts(&blocks), ["first line", "second line", "third line"]);
        assert_eq!(
            kinds(&blocks),
            [BlockKind::Paragraph, BlockKind::Paragraph, BlockKind::Paragraph]
        );
    }

    #[test]
    fn a_dash_or_star_opens_a_bullet_in_the_fallback() {
        let blocks = split_blocks("Heading line\n- first\n* second\n   \n-  spaced");
        assert_eq!(texts(&blocks), ["Heading line", "first", "second", "spaced"]);
        assert_eq!(
            kinds(&blocks),
            [
                BlockKind::Paragraph,
                BlockKind::Bullet,
                BlockKind::Bullet,
                BlockKind::Bullet,
            ]
        );
    }

    /// THE double-parse guard. Escaped markup in the feed is text the user is
    /// meant to READ. Flattening the note and parsing the result again decoded
    /// the entities on the first pass and read the decoded result as a tag on
    /// the second, turning a printed example into a live link.
    #[test]
    fn escaped_markup_in_the_fallback_is_parsed_exactly_once() {
        let blocks = split_blocks(r#"Write &lt;a href="https://evil.example"&gt;x&lt;/a&gt; to link."#);
        assert_eq!(
            texts(&blocks),
            [r#"Write <a href="https://evil.example">x</a> to link."#]
        );
        assert!(blocks.iter().all(|block| block.runs.iter().all(|run| run.link.is_none())));
    }

    /// The same guard for a real anchor: the fallback line keeps its markup, so
    /// the link is still there to render rather than flattened away.
    #[test]
    fn a_real_anchor_in_the_fallback_keeps_its_link() {
        let blocks = split_blocks(r#"see <a href="https://example.com/x">the page</a>"#);
        assert_eq!(
            blocks
                .iter()
                .flat_map(|block| block.runs.iter())
                .filter_map(|run| run.link.as_deref())
                .collect::<Vec<_>>(),
            ["https://example.com/x"]
        );
    }

    #[test]
    fn a_line_of_nothing_but_markers_is_dropped() {
        assert!(split_blocks("---").is_empty());
        assert!(split_blocks("  <br>  ").is_empty());
    }

    #[test]
    fn an_empty_fragment_has_no_blocks() {
        assert!(split_blocks("").is_empty());
        assert!(split_blocks("   \n  ").is_empty());
    }

    // -- parse_release_note: decision (c) ----------------------------------

    /// `appcast-windows.xml`'s shape. Windows took the first `<h2>` and still
    /// does; macOS now reads this feed the same way.
    #[test]
    fn an_h2_becomes_the_title() {
        let note = parse_release_note(
            "<h2>What's New in 1.11.0</h2>\n<ul>\n<li>Your vocabulary reaches more providers.</li>\n</ul>",
        );
        assert_eq!(title_text(&note).as_deref(), Some("What's New in 1.11.0"));
        assert_eq!(texts(&note.bullets), ["Your vocabulary reaches more providers."]);
    }

    /// The half of decision (c) Windows did not have: its regex was
    /// case-sensitive and allowed no attributes.
    #[test]
    fn the_h2_match_is_case_insensitive_and_allows_attributes() {
        assert_eq!(
            title_text(&parse_release_note(r#"<H2 id="whats-new">Title</H2><ul><li>x</li></ul>"#))
                .as_deref(),
            Some("Title")
        );
    }

    /// `ReleaseNotesHTMLTests.headingBeforeTheListIsUsedAsTheTitle`: the shape
    /// with no `<h2>`. Windows gains this branch.
    #[test]
    fn without_an_h2_the_content_before_the_list_is_the_title() {
        let note = parse_release_note(
            "<b>Enhanced Audio Recording</b>\n<ul>\n<li>Improved stability</li>\n</ul>",
        );
        assert_eq!(title_text(&note).as_deref(), Some("Enhanced Audio Recording"));
        assert_eq!(texts(&note.bullets), ["Improved stability"]);
        assert!(note.title.as_ref().is_some_and(|block| block.runs.iter().all(|run| run.bold)));
    }

    /// `ReleaseNotesHTMLTests.bulletsKeepEmphasisAndNoTitleIsInventedFromTheFirstBullet`
    /// — `appcast.xml`'s shape. A `<b>` inside the first bullet is emphasis, not
    /// a title.
    #[test]
    fn a_note_that_opens_with_the_list_has_no_title() {
        let note = parse_release_note(
            "<ul>\n    <li><b>Redesigned first-run setup</b> — a clearer 8-step walkthrough.</li><li>Short clips now transcribe much faster.</li>\n</ul>",
        );
        assert!(note.title.is_none());
        assert_eq!(
            texts(&note.bullets),
            [
                "Redesigned first-run setup — a clearer 8-step walkthrough.",
                "Short clips now transcribe much faster."
            ]
        );
        assert!(note.bullets[0].runs[0].bold);
        assert!(note.bullets[1].runs.iter().all(|run| !run.bold));
    }

    /// `ReleaseNotesHTMLTests.headingKeepsItsLink`.
    #[test]
    fn a_link_in_the_title_survives() {
        let note = parse_release_note(
            r#"<b>See the <a href="https://example.com/latency">latency page</a></b><ul><li>x</li></ul>"#,
        );
        assert!(note
            .title
            .as_ref()
            .is_some_and(|block| block.runs.iter().any(|run| run.link.is_some())));
    }

    /// `ReleaseNotesHTMLTests.missingReleaseNotesProduceNoTitleAndNoBullets` —
    /// the empty fragment both heads pass for a feed entry with no notes.
    #[test]
    fn an_empty_note_has_no_title_and_no_bullets() {
        let note = parse_release_note("");
        assert!(note.title.is_none());
        assert!(note.bullets.is_empty());
    }

    /// macOS looked for `<ul` FIRST and only fell back to `<li` when there was
    /// no `<ul` anywhere — so a stray `<li>` before the list does not cut the
    /// title short. Pinned so it cannot drift.
    #[test]
    fn the_list_cut_prefers_ul_over_li() {
        assert_eq!(
            title_text(&parse_release_note("Intro <li>stray</li> more<ul><li>x</li></ul>")).as_deref(),
            Some("Intro stray more")
        );
        assert_eq!(
            title_text(&parse_release_note("Intro<li>x</li>")).as_deref(),
            Some("Intro")
        );
    }

    #[test]
    fn the_list_cut_is_case_insensitive() {
        assert_eq!(
            title_text(&parse_release_note("Intro<UL><LI>x</LI></UL>")).as_deref(),
            Some("Intro")
        );
    }

    /// A note with no list at all is all title — macOS's `?? html.endIndex`.
    #[test]
    fn a_note_with_no_list_is_all_title() {
        let note = parse_release_note("Just a sentence.");
        assert_eq!(title_text(&note).as_deref(), Some("Just a sentence."));
        assert!(note.bullets.is_empty());
    }

    /// An `<h3>` is a sub-heading: it never triggers the first rule. Only an
    /// `<h2>` is looked for by name, anywhere in the note — which is what
    /// Windows' `<h2>(.*?)</h2>` regex did.
    ///
    /// An `<h3>` that happens to sit before the list is still *inside* the
    /// second rule's slice, and its text reads as the title there — exactly as
    /// macOS renders it today. That is the second rule working, not the first.
    #[test]
    fn only_an_h2_is_matched_by_name() {
        assert!(parse_release_note("<ul><li>x</li></ul><h3>Details</h3>")
            .title
            .is_none());
        assert_eq!(
            title_text(&parse_release_note("<ul><li>x</li></ul><h2>Late</h2>")).as_deref(),
            Some("Late")
        );
        assert_eq!(kinds(&split_blocks("<h3>Details</h3>")), [BlockKind::Heading]);
    }

    /// A textless `<h2>` falls through to the second rule instead of hiding a
    /// real heading.
    #[test]
    fn an_empty_h2_does_not_suppress_the_title() {
        assert_eq!(
            title_text(&parse_release_note("<h2>  </h2>Real title<ul><li>x</li></ul>")).as_deref(),
            Some("Real title")
        );
    }

    /// `parse_release_note` never applies the fallback: a note with no list is a
    /// title and nothing else, so the card cannot render the note twice.
    #[test]
    fn parse_release_note_does_not_fall_back_to_lines() {
        let note = parse_release_note("- first\n- second");
        assert!(note.bullets.is_empty());
        assert_eq!(title_text(&note).as_deref(), Some("- first - second"));
    }

    // -- the live feeds ----------------------------------------------------

    /// The real 2.45.0 entry of `nextjs/public/appcast.xml`, indentation and
    /// all: no heading, five bullets, bold lead-ins on the first three.
    #[test]
    fn the_macos_feeds_shape_parses_as_it_renders_today() {
        let note = parse_release_note(
            "\n                <ul>\n                    <li><b>Google Gemini 3.5 Transcribe is here.</b> Google's dedicated speech model.</li><li><b>It replaces Chirp 3.</b> Chirp 3 Modes move across.</li><li>Recording starts without the pause some Macs saw.</li>\n                </ul>\n                ",
        );
        assert!(note.title.is_none());
        assert_eq!(note.bullets.len(), 3);
        assert!(note.bullets[0].runs[0].bold);
        assert!(note.bullets[2].runs.iter().all(|run| !run.bold));
    }

    /// The real 1.11.0 entry of `nextjs/public/appcast-windows.xml`.
    #[test]
    fn the_windows_feeds_shape_parses_as_it_renders_today() {
        let html = "<h2>What's New in 1.11.0</h2>\n<ul>\n<li>Your custom vocabulary now reaches more providers.</li><li>Three new post-processing models.</li>\n</ul>\n";

        let note = parse_release_note(html);
        assert_eq!(title_text(&note).as_deref(), Some("What's New in 1.11.0"));
        assert_eq!(note.bullets.len(), 2);

        // The update dialog's view of the same fragment: the heading is a block
        // in document order rather than a separate field.
        let blocks = split_blocks(html);
        assert_eq!(
            kinds(&blocks),
            [BlockKind::Heading, BlockKind::Bullet, BlockKind::Bullet]
        );
    }

    // -- invariants --------------------------------------------------------

    #[test]
    fn no_block_is_ever_empty() {
        for html in [
            "",
            "   ",
            "<li></li>",
            "<h2></h2>",
            "<p><br></p>",
            "<ul><li> </li><li>x</li></ul>",
            "- \n* \n---",
        ] {
            for block in split_blocks(html) {
                assert!(!block.runs.is_empty(), "empty block from {html:?}");
                assert!(block.runs.iter().all(|run| !run.text.is_empty()));
            }
        }
    }

    /// Pathological nesting must terminate and must not panic — the scanner
    /// always advances past the tag it just looked at.
    #[test]
    fn pathological_input_terminates() {
        // Never-closed `<`: no tag at all, so the whole thing is text, and the
        // fallback renders it verbatim rather than as markup.
        let unterminated = "<li".repeat(200);
        assert_eq!(texts(&split_blocks(&unterminated)), [unterminated.as_str()]);

        // 200 opens, 200 closes: the first close ends the first element, and
        // everything inside it is inline content.
        let deep = format!("{}x{}", "<li>".repeat(200), "</li>".repeat(200));
        assert_eq!(texts(&split_blocks(&deep)), ["x"]);

        // Opens that never close: each is skipped, one step at a time.
        assert!(parse_release_note(&"<h2>".repeat(500)).title.is_none());
    }
}
