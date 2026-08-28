//! The inline layer: `<b>`/`<strong>`, `<i>`/`<em>`, `<a href>`, `<br>` and
//! character entities, turned into styled runs.
//!
//! A one-for-one port of `ReleaseNotesHTML.runs(in:)` / `linkURL(fromHref:)`
//! (Swift) and `InlineHtml.Parse` / `LinkFrom` (C#).

use crate::entity;
use crate::tag;

/// A stretch of text that shares one style and, if it sits inside an `<a href>`,
/// one destination.
///
/// `link` is the feed's href **verbatim** (entity-decoded and trimmed), not a
/// parsed URL: UniFFI has no URL type, and letting each platform build
/// `URL(string:)` / `new Uri(...)` keeps the security decision — the scheme
/// allowlist, which lives here — in Rust while sidestepping the `URL`-vs-`Uri`
/// normalization drift the two heads already accommodate deliberately. A native
/// URL constructor that then fails yields no link, exactly as today.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Run {
    pub text: String,
    pub bold: bool,
    pub italic: bool,
    pub link: Option<String>,
}

/// Schemes we are willing to hand to the shell. Anything else — most of all
/// `javascript:` and `data:` — keeps its label and loses its link.
const ALLOWED_SCHEMES: [&str; 3] = ["http", "https", "mailto"];

/// Collapsible whitespace: exactly these four scalars.
///
/// U+00A0 is deliberately absent — a non-breaking space is not collapsible on
/// either native head, and `&nbsp;` must survive as itself. Note this is a
/// narrower set than `char::is_whitespace`, which the tag scanner uses; that
/// asymmetry is in both natives too.
const fn is_collapsible_whitespace(character: char) -> bool {
    matches!(character, ' ' | '\n' | '\r' | '\t')
}

/// The scheme of an href, ASCII-lowercased, or `None` when it has none.
///
/// RFC 3986: `ALPHA *( ALPHA / DIGIT / "+" / "-" / "." )` up to the first `:`.
/// This is exactly what `URL.scheme` and `Uri.Scheme` accept, so the allowlist
/// decides the same way here as it did on either head.
fn scheme_of(href: &str) -> Option<String> {
    let (head, _) = href.split_once(':')?;

    let mut characters = head.chars();
    let first = characters.next()?;
    if !first.is_ascii_alphabetic() {
        return None;
    }
    if !characters.all(|c| c.is_ascii_alphanumeric() || c == '+' || c == '-' || c == '.') {
        return None;
    }

    Some(head.to_ascii_lowercase())
}

/// Destination for an `<a>`'s href, or `None` when it is missing or is not a
/// scheme we are willing to open.
fn link_from(href: Option<&str>) -> Option<String> {
    let href = href?;

    // Entities only: feeds escape query separators, so "?a=1&amp;b=2" has to be
    // decoded before it is a URL. Nothing else about the href may change —
    // running it through the whole parser stripped markup and collapsed
    // whitespace inside it, quietly opening a different address than the feed
    // asked for.
    let decoded = entity::decode_entities(href);
    let decoded = decoded.trim();

    let scheme = scheme_of(decoded)?;
    if ALLOWED_SCHEMES.contains(&scheme.as_str()) {
        Some(decoded.to_string())
    } else {
        None
    }
}

/// Parser state. The two natives express this as nested closures over locals;
/// a struct is the same program without the borrow gymnastics.
struct Builder {
    runs: Vec<Run>,
    current: String,
    bold_depth: u32,
    italic_depth: u32,

    // HTML collapses whitespace, and feed entries are indented across several
    // lines, so a space is only emitted once real text follows it.
    pending_space: bool,
    produced_text: bool,

    // One entry per open `<a>`, `None` when its href was missing or unusable —
    // so the matching `</a>` still pops the right thing and the label survives
    // as ordinary text. The innermost entry wins: a nested `<a>` with a rejected
    // href must not inherit the outer destination.
    link_stack: Vec<Option<String>>,

    collapse_whitespace: bool,
}

impl Builder {
    fn new(collapse_whitespace: bool) -> Self {
        Builder {
            runs: Vec::new(),
            current: String::new(),
            bold_depth: 0,
            italic_depth: 0,
            pending_space: false,
            produced_text: false,
            link_stack: Vec::new(),
            collapse_whitespace,
        }
    }

    fn flush(&mut self) {
        if self.current.is_empty() {
            return;
        }

        self.runs.push(Run {
            text: std::mem::take(&mut self.current),
            bold: self.bold_depth > 0,
            italic: self.italic_depth > 0,
            link: self.link_stack.last().cloned().flatten(),
        });
    }

    /// Close the current run at a `<b>`/`<i>`/`<a>` boundary. A space waiting to
    /// be written belongs *outside* the element: with the text before an opening
    /// tag, and with the text after a closing one. So `<b>See</b> <a>here</a>`
    /// does not underline and tint the space in front of the link, and
    /// `<a><b>the page</b> </a>now` does not leave a linked space behind either.
    /// Appending to an empty buffer makes that space its own run, carrying the
    /// style and destination in force where it is emitted.
    fn flush_at_tag_boundary(&mut self, is_closing: bool) {
        if self.pending_space && !is_closing {
            self.current.push(' ');
            self.pending_space = false;

            // That space is now written, so whitespace straight after it
            // collapses into it rather than arming a second one. Otherwise an
            // element that produces no text at all — `a <a href=…><img …></a> b`
            // — is spelt with a space on each side of nothing.
            self.produced_text = false;
        }

        self.flush();
    }

    fn append_char(&mut self, character: char) {
        if self.collapse_whitespace && is_collapsible_whitespace(character) {
            self.pending_space = self.produced_text;
            return;
        }

        // NOTE: the C# mirror also clears `producedText` for a `\n`/`\r` in the
        // NON-collapsing branch (InlineHtml.cs:138-141). That write is dead:
        // `producedText` is set back to `true` four lines later, and
        // `pendingSpace` can only ever be armed inside the collapsing branch, so
        // nothing reads it in between. It is not reproduced here.

        if self.pending_space {
            self.current.push(' ');
            self.pending_space = false;
        }

        self.current.push(character);
        self.produced_text = true;
    }

    fn append_str(&mut self, text: &str) {
        for character in text.chars() {
            self.append_char(character);
        }
    }

    fn append_line_break(&mut self) {
        self.current.push('\n');
        self.pending_space = false;
        self.produced_text = false;
    }

    fn handle_tag(&mut self, raw: &[char]) {
        let parsed = tag::parse_tag(raw);

        if parsed.name == "br" {
            self.append_line_break();
            return;
        }

        // A tag that closes itself opens and closes in one go, so it changes no
        // state at all. Acting on it would push a depth or a link entry nothing
        // ever pops, and the rest of the note would render bold, italic or
        // linked: `<a …/>`, `<b/>`, `<i/>`.
        //
        // `</a/>` is both closing and self-closing. It is the closing half that
        // counts: skipping it would leave the depth or link entry its `<a>`
        // pushed open, which is the very thing this guard is for.
        if parsed.is_self_closing && !parsed.is_closing {
            return;
        }

        match parsed.name.as_str() {
            "b" | "strong" => {
                self.flush_at_tag_boundary(parsed.is_closing);
                self.bold_depth = if parsed.is_closing {
                    self.bold_depth.saturating_sub(1)
                } else {
                    self.bold_depth.saturating_add(1)
                };
            }
            "i" | "em" => {
                self.flush_at_tag_boundary(parsed.is_closing);
                self.italic_depth = if parsed.is_closing {
                    self.italic_depth.saturating_sub(1)
                } else {
                    self.italic_depth.saturating_add(1)
                };
            }
            "a" => {
                self.flush_at_tag_boundary(parsed.is_closing);
                if parsed.is_closing {
                    self.link_stack.pop();
                } else {
                    self.link_stack.push(link_from(parsed.href.as_deref()));
                }
            }
            _ => {}
        }
    }
}

/// Split a fragment into styled runs.
///
/// `collapse_whitespace` false keeps existing line breaks, for callers that
/// split the result into lines. It exists because `InlineHtml.PlainText` /
/// `InlineHtml.Parse` take it as an optional C# parameter; macOS has no
/// equivalent and always passes `true`.
pub fn parse_inline(html: &str, collapse_whitespace: bool) -> Vec<Run> {
    let mut builder = Builder::new(collapse_whitespace);
    if html.is_empty() {
        return builder.runs;
    }

    let chars: Vec<char> = html.chars().collect();
    let mut index = 0usize;

    while let Some(&character) = chars.get(index) {
        if character == '<' {
            match tag::tag_end(&chars, index) {
                None => {
                    // Unterminated tag: the rest is text, not markup.
                    let rest: String = chars.get(index..).unwrap_or(&[]).iter().collect();
                    builder.append_str(&rest);
                    break;
                }
                Some(close) => {
                    let body = chars
                        .get(index.saturating_add(1)..close)
                        .unwrap_or(&[])
                        .to_vec();
                    builder.handle_tag(&body);
                    index = close.saturating_add(1);
                    continue;
                }
            }
        }

        if character == '&' {
            if let Some((decoded, end)) = entity::decode_entity_at(&chars, index) {
                builder.append_str(&decoded);
                index = end;
                continue;
            }
        }

        builder.append_char(character);
        index = index.saturating_add(1);
    }

    builder.flush();
    builder.runs
}

/// Tag-free, entity-decoded text — for titles, glyph selection, logging and
/// tests.
pub fn plain_text(html: &str, collapse_whitespace: bool) -> String {
    parse_inline(html, collapse_whitespace)
        .into_iter()
        .map(|run| run.text)
        .collect()
}

// ===========================================================================
// Tests
//
// Every case below is a transliteration of a case in one or both of the two
// oracle suites that remain the source of truth for this behaviour:
//   app/macos/hyperwhisperTests/ReleaseNotesHTMLTests.swift  (the parser cases)
//   app/windows/HyperWhisper.SmokeTests/Program.cs           (the InlineHtml block)
// Neither file is modified by this crate; both must keep passing untouched.
//
// The one systematic difference: `link` is asserted as the verbatim href string
// rather than as a constructed URL/Uri, because URL construction now happens on
// the native side. So where the C# suite asserts `AbsoluteUri ==
// "https://example.com/"`, this asserts `Some("https://example.com")` — the
// trailing slash is `Uri`'s normalization, not the parser's output.
// ===========================================================================

#[cfg(test)]
mod tests {
    use super::*;

    fn runs(html: &str) -> Vec<Run> {
        parse_inline(html, true)
    }

    fn text(html: &str) -> String {
        plain_text(html, true)
    }

    fn plain(text: &str) -> Run {
        Run {
            text: text.to_string(),
            ..Run::default()
        }
    }

    fn bold(text: &str) -> Run {
        Run {
            text: text.to_string(),
            bold: true,
            ..Run::default()
        }
    }

    fn italic(text: &str) -> Run {
        Run {
            text: text.to_string(),
            italic: true,
            ..Run::default()
        }
    }

    fn linked(text: &str, link: &str) -> Run {
        Run {
            text: text.to_string(),
            link: Some(link.to_string()),
            ..Run::default()
        }
    }

    fn links(html: &str) -> Vec<Option<String>> {
        runs(html).into_iter().map(|r| r.link).collect()
    }

    // -- Inline formatting --------------------------------------------------

    /// macOS `boldTagBecomesEmphasisInsteadOfLiteralMarkup`;
    /// Windows "InlineHtml turns release-note <b> into a bold run".
    #[test]
    fn bold_tag_becomes_emphasis_instead_of_literal_markup() {
        assert_eq!(
            runs("<b>New models</b> — OpenAI gpt-transcribe and more."),
            vec![
                bold("New models"),
                plain(" — OpenAI gpt-transcribe and more."),
            ]
        );

        // The Windows spelling of the same case feeds `&mdash;` as an entity.
        assert_eq!(
            runs("<b>New models</b> &mdash; OpenAI gpt-transcribe and more."),
            vec![
                bold("New models"),
                plain(" — OpenAI gpt-transcribe and more."),
            ]
        );

        assert!(!text("<b>New models</b>").contains('<'));
        assert!(!text("<b>x</b>").contains('<'));
    }

    /// macOS `strongAndEmphasisAliasesAreHonoured`.
    #[test]
    fn strong_and_emphasis_aliases_are_honoured() {
        assert_eq!(
            runs("<strong>a</strong><em>b</em><i>c</i>"),
            vec![bold("a"), italic("b"), italic("c")]
        );
    }

    /// macOS `nestedEmphasisCombinesStyles`.
    #[test]
    fn nested_emphasis_combines_styles() {
        assert_eq!(
            runs("<b>bold <i>both</i></b>"),
            vec![
                bold("bold "),
                Run {
                    text: "both".to_string(),
                    bold: true,
                    italic: true,
                    link: None
                },
            ]
        );
    }

    /// macOS `attributedStringCarriesEmphasisIntent` — the parser-observable
    /// half; the `InlinePresentationIntent` mapping stays on the Swift side.
    #[test]
    fn emphasis_intent_is_carried_by_the_run_not_the_text() {
        let parsed = runs("<b>Punctuation control</b> — choose how much.");
        assert!(parsed.iter().any(|r| r.bold));
        assert_eq!(
            text("<b>Punctuation control</b> — choose how much."),
            "Punctuation control — choose how much."
        );
    }

    // -- Links --------------------------------------------------------------

    /// macOS `anchorBecomesALinkedRun`; Windows "turns <a href> into a linked
    /// run".
    #[test]
    fn anchor_becomes_a_linked_run() {
        assert_eq!(
            runs(r#"See the <a href="https://example.com/latency">latency page</a> now."#),
            vec![
                plain("See the "),
                linked("latency page", "https://example.com/latency"),
                plain(" now."),
            ]
        );
    }

    /// macOS `linkedRunIsTintedAndUnderlinedSoItLooksClickable` — the
    /// parser-observable half; tint and underline stay on the Swift side.
    #[test]
    fn a_link_carries_its_destination_and_only_its_label() {
        let parsed = runs(r#"<a href="https://example.com">tap me</a>"#);
        assert_eq!(
            parsed
                .iter()
                .find(|r| r.link.is_some())
                .map(|r| r.link.clone()),
            Some(Some("https://example.com".to_string()))
        );
        assert_eq!(
            text(r#"<a href="https://example.com">tap me</a>"#),
            "tap me"
        );
    }

    /// macOS `emphasisInsideALinkKeepsBothTheStyleAndTheDestination`.
    #[test]
    fn emphasis_inside_a_link_keeps_both_the_style_and_the_destination() {
        assert_eq!(
            runs(r#"<a href="https://example.com"><b>bold link</b></a>"#),
            vec![Run {
                text: "bold link".to_string(),
                bold: true,
                italic: false,
                link: Some("https://example.com".to_string()),
            }]
        );
    }

    /// macOS `hrefAttributesAreReadWhateverTheirQuotingAndCase`.
    #[test]
    fn href_attributes_are_read_whatever_their_quoting_and_case() {
        let expected = Some("https://example.com/a".to_string());

        for html in [
            r#"<A HREF='https://example.com/a' class="x">x</A>"#,
            r#"<a class="x" href=https://example.com/a>x</a>"#,
            r#"<a href = "https://example.com/a">x</a>"#,
        ] {
            assert_eq!(
                runs(html).first().and_then(|r| r.link.clone()),
                expected,
                "href not read from '{html}'"
            );
        }
    }

    /// macOS `escapedQuerySeparatorsSurviveInTheDestination`.
    #[test]
    fn escaped_query_separators_survive_in_the_destination() {
        assert_eq!(
            runs(r#"<a href="https://example.com/p?a=1&amp;b=2">x</a>"#)
                .first()
                .and_then(|r| r.link.clone()),
            Some("https://example.com/p?a=1&b=2".to_string())
        );
    }

    /// macOS `onlyWebAndMailSchemesBecomeLinks`; Windows "links only web and
    /// mail schemes". A feed we do not control must not be able to produce a
    /// clickable javascript: or data: URL — the label stays, the link does not.
    #[test]
    fn only_web_and_mail_schemes_become_links() {
        for html in [
            r#"<a href="javascript:alert(1)">x</a>"#,
            r#"<a href="data:text/html,<b>x</b>">x</a>"#,
            r#"<a href="file:///etc/passwd">x</a>"#,
            r#"<a href="/relative/path">x</a>"#,
            r#"<a data-href="https://example.com">x</a>"#,
            "<a>x</a>",
        ] {
            assert!(links(html).iter().all(Option::is_none), "linked: '{html}'");
            // Exactly the label, nothing else: the data: case used to leak
            // '">x' into the visible text, because the tag scan cut at the '>'
            // inside the quoted href.
            assert_eq!(text(html), "x", "text: '{html}'");
        }

        assert_eq!(
            runs(r#"<a href="mailto:hi@example.com">mail</a>"#)
                .first()
                .and_then(|r| r.link.clone()),
            Some("mailto:hi@example.com".to_string())
        );
    }

    /// macOS `textAfterAnUnusableLinkIsNotLinkedEither`.
    #[test]
    fn text_after_an_unusable_link_is_not_linked_either() {
        assert_eq!(
            runs(r#"<a href="javascript:x">label</a> after"#),
            vec![plain("label"), plain(" after")]
        );
    }

    /// macOS `anHrefInsideAnotherAttributesValueDoesNotWin`; Windows "reads href
    /// only in attribute-name position". "Preceded by whitespace" is also true
    /// inside a quoted value, so a title carrying "href=…" used to win over the
    /// real attribute.
    #[test]
    fn an_href_inside_another_attributes_value_does_not_win() {
        assert_eq!(
            runs(r#"<a title="see href=http://evil.example more" href="https://real.example">Label</a>"#)
                .first()
                .and_then(|r| r.link.clone()),
            Some("https://real.example".to_string())
        );
        assert_eq!(
            runs(r#"<a title="use href=1" href="https://real">x</a>"#)
                .first()
                .and_then(|r| r.link.clone()),
            Some("https://real".to_string())
        );
        assert_eq!(
            runs(r#"<a data-href="https://evil.example" href="https://real.example">x</a>"#)
                .first()
                .and_then(|r| r.link.clone()),
            Some("https://real.example".to_string())
        );
    }

    /// macOS `aGreaterThanInsideAQuotedAttributeDoesNotEndTheTag`; Windows
    /// "keeps a quoted '>' inside the tag". A '>' in a query string used to
    /// truncate the tag, linking half the URL and spilling the rest of the
    /// markup into the visible text.
    #[test]
    fn a_greater_than_inside_a_quoted_attribute_does_not_end_the_tag() {
        let html = r#"<li>Read <a href="https://ex.com/?q=a>b" title="t">here</a></li>"#;

        assert_eq!(text(html), "Read here");
        assert_eq!(
            runs(html)
                .iter()
                .map(|r| r.text.clone())
                .collect::<Vec<_>>(),
            vec!["Read ".to_string(), "here".to_string()]
        );
    }

    /// macOS `anUnterminatedQuoteProducesNoLinkAtAll`. An href whose quote is
    /// never closed is not a destination — the scan used to hand back the rest
    /// of the tag as if it were the value.
    #[test]
    fn an_unterminated_quote_produces_no_link_at_all() {
        let html = r#"<a href="https://example.com>label</a> after"#;

        assert!(links(html).iter().all(Option::is_none));
        assert_eq!(text(html), "label after");
    }

    /// macOS `aSelfClosingOrUnclosedAnchorDoesNotLinkWhatFollowsIt`. `<a …/>`
    /// used to push an entry nothing ever popped, so every remaining word in the
    /// note rendered as part of the link.
    #[test]
    fn a_self_closing_or_unclosed_anchor_does_not_link_what_follows_it() {
        assert!(
            links(r#"Before <a href="https://x.example"/> after and more"#)
                .iter()
                .all(Option::is_none)
        );

        assert_eq!(
            runs(r#"<a href="https://x.example"/>after"#),
            vec![plain("after")]
        );

        // An <a> nobody closes is bounded by the end of the fragment.
        assert_eq!(
            runs(r#"<a href="https://x.example">unclosed"#),
            vec![linked("unclosed", "https://x.example")]
        );
    }

    /// macOS `aSlashEndingABareHrefBelongsToTheURLNotTheTag`. Deciding
    /// "self-closing" from the raw tag's last character read every bare href
    /// ending in "/" — most URLs — as `<a …/>`, and silently dropped the link.
    #[test]
    fn a_slash_ending_a_bare_href_belongs_to_the_url_not_the_tag() {
        assert_eq!(
            runs("<a href=https://example.com/>Home</a>"),
            vec![linked("Home", "https://example.com/")]
        );
        assert_eq!(
            runs(r#"<a href="https://example.com/">Home</a>"#),
            vec![linked("Home", "https://example.com/")]
        );

        // A "/" of the tag's own still closes it, even after a bare href that
        // ends in one.
        let closed = "<a href=https://example.com/ />Home and the rest";
        assert!(links(closed).iter().all(Option::is_none));
        assert_eq!(text(closed), "Home and the rest");

        // A "/" that is not the last thing in the tag is not the tag's own.
        assert_eq!(
            runs("<a / href=https://example.com/>L</a>")
                .first()
                .and_then(|r| r.link.clone()),
            Some("https://example.com/".to_string())
        );

        for line_break in ["a<br>b", "a<br/>b", "a<br />b"] {
            assert_eq!(text(line_break), "a\nb", "line break: '{line_break}'");
        }
    }

    /// macOS `aNestedAnchorTakesTheInnermostDestination`. The inner href is
    /// rejected, so its label must lose the link rather than inherit the outer
    /// anchor's destination.
    #[test]
    fn a_nested_anchor_takes_the_innermost_destination() {
        assert_eq!(
            runs(r#"<a href="https://ok.example">read <a href="javascript:x">this</a></a>"#),
            vec![linked("read ", "https://ok.example"), plain("this")]
        );
    }

    /// macOS `theSpacesAroundALinkStayOutsideIt`. A space at either edge of a
    /// link is underlined, tinted and inside the hit region — it belongs outside
    /// the anchor, on whichever side it was written.
    #[test]
    fn the_spaces_around_a_link_stay_outside_it() {
        let opening = runs(r#"<b>See</b> <a href="https://example.com">here</a>"#);
        assert_eq!(
            opening.iter().map(|r| r.text.clone()).collect::<String>(),
            "See here"
        );
        assert!(opening
            .iter()
            .all(|r| r.link.is_none() || !r.text.starts_with(' ')));

        // The closing side: the space in front of </a> used to become a run of
        // its own, still tinted, underlined and inside the hit region.
        let closing = runs(r#"See <a href="https://x.example"><b>the page</b> </a>now"#);
        assert_eq!(
            closing.iter().map(|r| r.text.clone()).collect::<String>(),
            "See the page now"
        );
        assert!(closing
            .iter()
            .all(|r| r.link.is_none() || !r.text.trim().is_empty()));
    }

    /// macOS `aSelfClosingEmphasisTagDoesNotStyleTheRestOfTheNote`.
    /// Self-closing was consulted in the `<a>` branch only, so `<b/>` pushed a
    /// depth nothing ever popped and emboldened every remaining word.
    #[test]
    fn a_self_closing_emphasis_tag_does_not_style_the_rest_of_the_note() {
        assert!(runs("before <b/> after").iter().all(|r| !r.bold));
        assert_eq!(text("before <b/> after"), "before after");

        // Each `all(…)` is paired with an equality check on the text, because
        // `all` is vacuously true on empty or truncated output: a mutant that
        // made a self-closing emphasis tag abort the parse — so `x<strong />y`
        // rendered as "x" and the rest of the note was lost — passed the whole
        // suite while only the unpaired assertions guarded these three cases.
        assert!(runs("before <i/> after").iter().all(|r| !r.italic));
        assert_eq!(text("before <i/> after"), "before after");

        assert!(runs("x<strong />y").iter().all(|r| !r.bold));
        assert_eq!(text("x<strong />y"), "xy");

        assert!(runs("x<em />y").iter().all(|r| !r.italic));
        assert_eq!(text("x<em />y"), "xy");

        // The paired forms still style what they wrap.
        assert_eq!(
            runs("<b>still bold</b>").first().map(|r| r.bold),
            Some(true)
        );
    }

    /// macOS `anApostropheInABareValueIsAnOrdinaryCharacter`. Skipping quoted
    /// values while looking for the tag's ">" entered quote mode on any
    /// apostrophe, so the one in a bare "href=it's" paired up with the next one
    /// in the text and everything between them was swallowed as markup.
    #[test]
    fn an_apostrophe_in_a_bare_value_is_an_ordinary_character() {
        assert_eq!(
            text("<a href=it's>label</a> and <b>Ray's</b> note"),
            "label and Ray's note"
        );
        assert_eq!(text("<b>Ray's</b> and <i>don't</i>"), "Ray's and don't");

        // A quote in value position — with or without whitespace after the "=" —
        // still shields a ">" sitting inside the value.
        assert_eq!(
            text(r#"<a href="https://ex.com/?q=a>b" title="t">here</a>"#),
            "here"
        );
        assert_eq!(text("<a href = 'https://ex.com/?q=a>b'>x</a>"), "x");
    }

    /// macOS `aValueLeftOpenDoesNotPairWithALaterTagsQuote`. Skipping to the
    /// matching quote searched the whole fragment, so a value never closed
    /// inside its own tag paired up with the quote of a later one, and
    /// everything between them was swallowed as one tag body.
    #[test]
    fn a_value_left_open_does_not_pair_with_a_later_tags_quote() {
        let html =
            r#"Read <a href="https://ex.com/latency>the page</a> for <b class="hl">details</b>."#;
        let parsed = runs(html);

        assert_eq!(text(html), "Read the page for details.");
        assert!(parsed.iter().all(|r| r.link.is_none()));
        assert!(parsed.iter().any(|r| r.text == "details" && r.bold));
        assert!(parsed
            .iter()
            .all(|r| !r.text.contains('<') && !r.text.contains("href")));
    }

    /// macOS `aClosingTagThatAlsoClosesItselfStillPops`. parse_tag sets both
    /// flags for "</a/>", and the self-closing guard ran first, so nothing was
    /// ever popped and the rest of the note stayed linked, bold or italic.
    #[test]
    fn a_closing_tag_that_also_closes_itself_still_pops() {
        let anchor =
            r#"<a href="https://hyperwhisper.app/changelog">changelog</a/>. Also faster startup."#;
        let parsed = runs(anchor);

        assert_eq!(text(anchor), "changelog. Also faster startup.");
        assert!(parsed
            .iter()
            .any(|r| r.text == "changelog" && r.link.is_some()));
        assert!(parsed
            .iter()
            .all(|r| r.link.is_none() || r.text == "changelog"));

        assert!(runs("<b>New:</b/> dictation is faster")
            .iter()
            .all(|r| !r.bold || r.text == "New:"));
        assert!(runs("<i>x</i/> y")
            .iter()
            .all(|r| !r.italic || r.text == "x"));
        assert!(runs(r#"<a href="https://x.example">x</a />after"#)
            .iter()
            .all(|r| r.link.is_none() || r.text == "x"));

        // The opening self-closing forms still change no state.
        assert!(runs("before <b/> after").iter().all(|r| !r.bold));
        assert!(links(r#"Before <a href="https://x.example"/> after"#)
            .iter()
            .all(Option::is_none));
        assert_eq!(text("a<br/>b"), "a\nb");
    }

    /// macOS `anElementThatProducesNoTextIsWrittenWithOneSpaceAroundIt`. The
    /// pending space was committed on the opening tag and then re-armed from
    /// produced_text, so an element that produces no text at all was spelt with
    /// a space on each side of nothing.
    #[test]
    fn an_element_that_produces_no_text_is_written_with_one_space_around_it() {
        for html in [
            r#"Read <a href="https://x.example"><img src="badge.png"></a> the docs"#,
            r#"Read <a href="https://x.example"></a> the docs"#,
            r#"Read <a href="https://x.example">   </a> the docs"#,
        ] {
            assert_eq!(text(html), "Read the docs", "spacing: '{html}'");
        }
    }

    /// macOS `aMalformedValueOnTheFirstTokenKeepsTheElementName`. The name was
    /// recorded after the value parse, so giving up on a malformed value on the
    /// first token discarded the element with it.
    #[test]
    fn a_malformed_value_on_the_first_token_keeps_the_element_name() {
        assert_eq!(text("line one<br = >line two"), "line one\nline two");
        assert_eq!(text(r#"a<br = "unterminated>b"#), "a\nb");
    }

    /// macOS `aCarriageReturnLineFeedCollapsesLikeAnyOtherWhitespace`; Windows
    /// "collapses a CRLF and leaves a non-breaking space alone".
    ///
    /// Decision (b) in action. macOS reads text by GRAPHEME today, where "\r\n"
    /// is ONE Character equal to neither "\r" nor "\n" (hence the explicit
    /// `character == "\r\n"` arm in ReleaseNotesHTML.swift). Windows reads UTF-16
    /// units. This crate reads Unicode scalar values, where a CRLF is two
    /// scalars, each of them collapsible — so the assertions below are unchanged
    /// on all three, and only the two heads' doc comments become false.
    #[test]
    fn a_carriage_return_line_feed_collapses_like_any_other_whitespace() {
        assert_eq!(text("line one\r\nline two"), "line one line two");
        assert_eq!(text("a\r\n\r\n  b"), "a b");
        assert_eq!(text("a&nbsp;b"), "a\u{00A0}b");
    }

    /// macOS `theHrefIsTheFeedsVerbatim`; Windows "keeps the feed's href
    /// verbatim, decoding entities only". The href used to be decoded by running
    /// it through the whole parser, which also stripped markup and comments out
    /// of it — a valid, allow-listed, but different destination.
    #[test]
    fn the_href_is_the_feeds_verbatim() {
        let markup = r#"<a href="https://ex.com/?q=<b>x</b>">Docs</a>"#;
        assert_eq!(
            runs(markup).first().and_then(|r| r.link.clone()),
            Some("https://ex.com/?q=<b>x</b>".to_string())
        );
        assert_eq!(text(markup), "Docs");

        let commented = r#"<a href="https://ex.com/<!-- c -->path">Docs</a>"#;
        let link = runs(commented).first().and_then(|r| r.link.clone());
        assert!(link.is_some());
        assert_ne!(link.as_deref(), Some("https://ex.com/path"));

        // Entities in the href must still decode: feeds escape query separators,
        // and "?a=1&amp;b=2" is not a URL until they do.
        for html in [
            r#"<a href="https://ex.com/p?a=1&amp;b=2">x</a>"#,
            r#"<a href="https://ex.com/p?a=1&#38;b=2">x</a>"#,
        ] {
            assert_eq!(
                runs(html).first().and_then(|r| r.link.clone()),
                Some("https://ex.com/p?a=1&b=2".to_string()),
                "href entities stopped decoding in '{html}'"
            );
        }
    }

    // -- Robustness ---------------------------------------------------------

    /// macOS `unsupportedTagsAreDroppedButTheirTextIsKept`; Windows "keeps text
    /// from tags it does not support".
    #[test]
    fn unsupported_tags_are_dropped_but_their_text_is_kept() {
        assert_eq!(
            text(r#"<span class="x">kept</span><a href="u">link</a>"#),
            "keptlink"
        );
        assert_eq!(text(r#"<span class="x">kept</span>"#), "kept");
    }

    /// macOS `unterminatedTagIsTreatedAsText`.
    #[test]
    fn unterminated_tag_is_treated_as_text() {
        assert_eq!(text("2 < 3 and counting"), "2 < 3 and counting");
        assert_eq!(text("2 < 3"), "2 < 3");
    }

    /// macOS `entitiesAreDecodedAndUnknownOnesStayLiteral`.
    #[test]
    fn entities_are_decoded_and_unknown_ones_stay_literal() {
        assert_eq!(
            text("a &amp; b &mdash; c &#8212; d &#x2014; e"),
            "a & b — c — d — e"
        );
        assert_eq!(text("&bogus; stays"), "&bogus; stays");
        assert_eq!(text("&lt;b&gt;not a tag&lt;/b&gt;"), "<b>not a tag</b>");
        assert_eq!(text("&lt;b&gt;escaped&lt;/b&gt;"), "<b>escaped</b>");
    }

    /// macOS `feedIndentationCollapsesToSingleSpaces`.
    #[test]
    fn feed_indentation_collapses_to_single_spaces() {
        assert_eq!(
            text("\n    Short clips now\n    transcribe faster.\n  "),
            "Short clips now transcribe faster."
        );
    }

    /// macOS `breakTagStartsANewLine`.
    #[test]
    fn break_tag_starts_a_new_line() {
        assert_eq!(text("first<br/>  second"), "first\nsecond");
    }

    /// Windows "InlineHtmlText renders an anchor containing emphasis as one
    /// link" — the parser half. The WPF `Apply` half stays on the C# side.
    #[test]
    fn an_anchor_containing_emphasis_yields_three_runs_with_one_destination() {
        let parsed = runs(r#"before <a href="https://x.com">see <b>this</b> page</a> after"#);
        let linked_runs: Vec<&Run> = parsed.iter().filter(|r| r.link.is_some()).collect();

        assert_eq!(linked_runs.len(), 3);
        assert!(linked_runs
            .iter()
            .all(|r| r.link.as_deref() == Some("https://x.com")));
        assert_eq!(
            text(r#"before <a href="https://x.com">see <b>this</b> page</a> after"#),
            "before see this page after"
        );
    }

    // -- Decision (b) and the collapse_whitespace flag -----------------------

    /// The scalar unit is the crate's single source of truth. A grapheme cluster
    /// that is NOT one scalar — an emoji with a skin-tone modifier, a combining
    /// accent — is emitted whole either way, because every scalar of it is
    /// non-collapsible and is appended in order.
    #[test]
    fn multi_scalar_grapheme_clusters_survive_intact() {
        assert_eq!(text("a 👍🏽 b"), "a 👍🏽 b");
        assert_eq!(text("e\u{0301}cole"), "e\u{0301}cole");
        assert_eq!(text("<b>👨‍👩‍👧‍👦</b>"), "👨‍👩‍👧‍👦");
    }

    /// `collapse_whitespace = false` is the C# `PlainText(html, false)` branch:
    /// existing line breaks survive for callers that split the result into
    /// lines. macOS has no equivalent and always passes `true`.
    #[test]
    fn not_collapsing_whitespace_keeps_the_feeds_own_line_breaks() {
        assert_eq!(
            plain_text("line one\r\nline two", false),
            "line one\r\nline two"
        );
        assert_eq!(plain_text("a\r\n\r\n  b", false), "a\r\n\r\n  b");
        assert_eq!(
            plain_text("\n    Short clips now\n    transcribe faster.\n  ", false),
            "\n    Short clips now\n    transcribe faster.\n  "
        );

        // Tags, entities and links behave identically either way.
        assert_eq!(
            parse_inline(r#"<b>a</b> <a href="https://x.example">b</a>"#, false),
            vec![bold("a"), plain(" "), linked("b", "https://x.example"),]
        );
        assert_eq!(plain_text("a&nbsp;b", false), "a\u{00A0}b");
        assert_eq!(plain_text("a<br>b", false), "a\nb");
    }

    /// Decision (a) end-to-end through the parser, not just the decoder: neither
    /// drift case decodes any more, on any head.
    #[test]
    fn the_two_entity_drift_cases_stay_literal_text() {
        // Decoded to "A" on macOS today (UInt32 accepts a leading '+').
        assert_eq!(text("&#+65;"), "&#+65;");
        // Decoded to "A" on Windows today (NumberStyles.HexNumber allows white).
        assert_eq!(text("&#x 41;"), "&#x 41;");

        // The well-formed spellings still decode, on both.
        assert_eq!(text("&#65;"), "A");
        assert_eq!(text("&#x41;"), "A");
    }

    // -- Degenerate input ---------------------------------------------------

    /// Nothing below may panic: the workspace builds with `panic = "abort"` and
    /// this input is remote. These are the shapes the fuzz corpus is seeded with.
    #[test]
    fn degenerate_input_is_survivable() {
        for html in [
            "",
            "<",
            ">",
            "<>",
            "</>",
            "&",
            "&;",
            "&#;",
            "&#",
            "<a href=",
            "<a href=\"",
            "<a href='",
            "</a>",
            "</a></a></a>",
            "<b><b><b>",
            "</b></b></b>",
            "<br",
            "<br=",
            "<=>",
            "< >",
            "<a href=\"\">x</a>",
            "<a href=\":\">x</a>",
            "<a href=\"http:\">x</a>",
            "\u{0}",
            "&#0;",
        ] {
            let _ = parse_inline(html, true);
            let _ = parse_inline(html, false);
        }

        // An unbalanced `</b>` must not underflow the depth counter. Each
        // `all(…)` is paired with an equality check on the text: `all` is
        // vacuously true on empty or truncated output, so on its own it would
        // also pass for a parser that dropped the `x`.
        assert!(runs("</b></b>x").iter().all(|r| !r.bold));
        assert_eq!(text("</b></b>x"), "x");

        assert!(runs("</i></i>x").iter().all(|r| !r.italic));
        assert_eq!(text("</i></i>x"), "x");

        // Nor may an unbalanced `</a>` underflow the link stack.
        assert!(links("</a></a>x").iter().all(Option::is_none));
        assert_eq!(text("</a></a>x"), "x");
    }

    /// A scheme-only or empty href is not a destination.
    #[test]
    fn a_degenerate_href_is_not_a_destination() {
        assert_eq!(link_from(None), None);
        assert_eq!(link_from(Some("")), None);
        assert_eq!(link_from(Some(":")), None);
        assert_eq!(link_from(Some("://x")), None);
        assert_eq!(link_from(Some("1http://x")), None);
        assert_eq!(link_from(Some("ht tp://x")), None);
        assert_eq!(link_from(Some("/relative/path")), None);
        assert_eq!(link_from(Some("HTTPS://X")).as_deref(), Some("HTTPS://X"));
        assert_eq!(
            link_from(Some("  https://x  ")).as_deref(),
            Some("https://x")
        );
        assert_eq!(link_from(Some("mailto:")).as_deref(), Some("mailto:"));
    }

    /// Live-feed shapes, lifted from nextjs/public/appcast.xml and
    /// appcast-windows.xml.
    #[test]
    fn the_live_feed_shapes_parse() {
        assert_eq!(
            text("<li><b>Redesigned first-run setup</b> — a clearer 8-step walkthrough.</li>"),
            "Redesigned first-run setup — a clearer 8-step walkthrough."
        );
        assert_eq!(
            text("<h2>What&#39;s New in 1.11.0</h2>"),
            "What's New in 1.11.0"
        );
        assert_eq!(
            text("<h2>What&apos;s New in 1.11.0</h2>"),
            "What's New in 1.11.0"
        );
    }
}
