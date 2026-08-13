//
//  ReleaseNotesHTMLTests.swift
//  hyperwhisperTests
//

import Foundation
import SwiftUI
import Testing
@testable import HyperWhisper

struct ReleaseNotesHTMLTests {

    // MARK: - Inline formatting

    @Test func boldTagBecomesEmphasisInsteadOfLiteralMarkup() {
        let runs = ReleaseNotesHTML.runs(in: "<b>New models</b> — OpenAI gpt-transcribe and more.")

        #expect(runs == [
            ReleaseNotesHTML.Run(text: "New models", style: .bold),
            ReleaseNotesHTML.Run(text: " — OpenAI gpt-transcribe and more.", style: [])
        ])
        #expect(!ReleaseNotesHTML.plainText("<b>New models</b>").contains("<"))
    }

    @Test func strongAndEmphasisAliasesAreHonoured() {
        #expect(ReleaseNotesHTML.runs(in: "<strong>a</strong><em>b</em><i>c</i>") == [
            ReleaseNotesHTML.Run(text: "a", style: .bold),
            ReleaseNotesHTML.Run(text: "b", style: .italic),
            ReleaseNotesHTML.Run(text: "c", style: .italic)
        ])
    }

    @Test func nestedEmphasisCombinesStyles() {
        #expect(ReleaseNotesHTML.runs(in: "<b>bold <i>both</i></b>") == [
            ReleaseNotesHTML.Run(text: "bold ", style: .bold),
            ReleaseNotesHTML.Run(text: "both", style: [.bold, .italic])
        ])
    }

    @Test func attributedStringCarriesEmphasisIntent() {
        let attributed = ReleaseNotesHTML.attributed("<b>Punctuation control</b> — choose how much.")
        let bold = attributed.runs.first { $0.inlinePresentationIntent?.contains(.stronglyEmphasized) == true }

        #expect(bold != nil)
        #expect(String(attributed.characters) == "Punctuation control — choose how much.")
    }

    // MARK: - Links

    @Test func anchorBecomesALinkedRun() {
        let runs = ReleaseNotesHTML.runs(in: #"See the <a href="https://example.com/latency">latency page</a> now."#)

        #expect(runs == [
            ReleaseNotesHTML.Run(text: "See the ", style: []),
            ReleaseNotesHTML.Run(text: "latency page", style: [], link: URL(string: "https://example.com/latency")),
            ReleaseNotesHTML.Run(text: " now.", style: [])
        ])
    }

    @Test func linkedRunIsTintedAndUnderlinedSoItLooksClickable() {
        let attributed = ReleaseNotesHTML.attributed(#"<a href="https://example.com">tap me</a>"#)
        let linked = attributed.runs.first { $0.link != nil }

        #expect(linked?.link == URL(string: "https://example.com"))
        #expect(linked?[AttributeScopes.SwiftUIAttributes.ForegroundColorAttribute.self] == .accentColor)
        #expect(linked?[AttributeScopes.SwiftUIAttributes.UnderlineStyleAttribute.self] == .single)
        #expect(String(attributed.characters) == "tap me")
    }

    @Test func emphasisInsideALinkKeepsBothTheStyleAndTheDestination() {
        #expect(ReleaseNotesHTML.runs(in: #"<a href="https://example.com"><b>bold link</b></a>"#) == [
            ReleaseNotesHTML.Run(text: "bold link", style: .bold, link: URL(string: "https://example.com"))
        ])
    }

    @Test func hrefAttributesAreReadWhateverTheirQuotingAndCase() {
        let expected = URL(string: "https://example.com/a")

        #expect(ReleaseNotesHTML.runs(in: #"<A HREF='https://example.com/a' class="x">x</A>"#).first?.link == expected)
        #expect(ReleaseNotesHTML.runs(in: #"<a class="x" href=https://example.com/a>x</a>"#).first?.link == expected)
        #expect(ReleaseNotesHTML.runs(in: #"<a href = "https://example.com/a">x</a>"#).first?.link == expected)
    }

    @Test func escapedQuerySeparatorsSurviveInTheDestination() {
        #expect(ReleaseNotesHTML.runs(in: #"<a href="https://example.com/p?a=1&amp;b=2">x</a>"#).first?.link
                == URL(string: "https://example.com/p?a=1&b=2"))
    }

    /// A feed we do not control must not be able to produce a clickable
    /// javascript: or data: URL — the label stays, the link does not.
    @Test func onlyWebAndMailSchemesBecomeLinks() {
        let hostile = [
            #"<a href="javascript:alert(1)">x</a>"#,
            #"<a href="data:text/html,<b>x</b>">x</a>"#,
            #"<a href="file:///etc/passwd">x</a>"#,
            #"<a href="/relative/path">x</a>"#,
            #"<a data-href="https://example.com">x</a>"#,
            "<a>x</a>"
        ]

        for html in hostile {
            #expect(ReleaseNotesHTML.runs(in: html).allSatisfy { $0.link == nil }, "linked: \(html)")
            // Exactly the label, nothing else: the data: case used to leak
            // '">x' into the visible text, because the tag scan cut at the '>'
            // inside the quoted href.
            #expect(ReleaseNotesHTML.plainText(html) == "x", "text: \(html)")
        }

        #expect(ReleaseNotesHTML.runs(in: #"<a href="mailto:hi@example.com">mail</a>"#).first?.link
                == URL(string: "mailto:hi@example.com"))
    }

    @Test func textAfterAnUnusableLinkIsNotLinkedEither() {
        #expect(ReleaseNotesHTML.runs(in: #"<a href="javascript:x">label</a> after"#) == [
            ReleaseNotesHTML.Run(text: "label", style: []),
            ReleaseNotesHTML.Run(text: " after", style: [])
        ])
    }

    /// "Preceded by whitespace" is also true inside a quoted value, so a title
    /// carrying "href=…" used to win over the real attribute.
    @Test func anHrefInsideAnotherAttributesValueDoesNotWin() {
        #expect(ReleaseNotesHTML.runs(in:
            #"<a title="see href=http://evil.example more" href="https://real.example">Label</a>"#)
            .first?.link == URL(string: "https://real.example"))

        #expect(ReleaseNotesHTML.runs(in: #"<a title="use href=1" href="https://real">x</a>"#)
            .first?.link == URL(string: "https://real"))

        #expect(ReleaseNotesHTML.runs(in:
            #"<a data-href="https://evil.example" href="https://real.example">x</a>"#)
            .first?.link == URL(string: "https://real.example"))
    }

    /// A '>' in a query string used to truncate the tag, linking half the URL
    /// and spilling the rest of the markup into the visible text. The
    /// destination itself is left to `URL` — the two platforms' URL parsers
    /// disagree about an unescaped '>', but neither may leak markup.
    @Test func aGreaterThanInsideAQuotedAttributeDoesNotEndTheTag() {
        let html = #"<li>Read <a href="https://ex.com/?q=a>b" title="t">here</a></li>"#

        #expect(ReleaseNotesHTML.plainText(html) == "Read here")
        #expect(ReleaseNotesHTML.runs(in: html).map(\.text) == ["Read ", "here"])
    }

    /// An href whose quote is never closed is not a destination — the scan
    /// used to hand back the rest of the tag as if it were the value.
    @Test func anUnterminatedQuoteProducesNoLinkAtAll() {
        let html = #"<a href="https://example.com>label</a> after"#

        #expect(ReleaseNotesHTML.runs(in: html).allSatisfy { $0.link == nil })
        #expect(ReleaseNotesHTML.plainText(html) == "label after")
    }

    /// `<a …/>` used to push an entry nothing ever popped, so every remaining
    /// word in the note rendered as part of the link.
    @Test func aSelfClosingOrUnclosedAnchorDoesNotLinkWhatFollowsIt() {
        #expect(ReleaseNotesHTML.runs(in: #"Before <a href="https://x.example"/> after and more"#)
            .allSatisfy { $0.link == nil })

        #expect(ReleaseNotesHTML.runs(in: #"<a href="https://x.example"/>after"#) == [
            ReleaseNotesHTML.Run(text: "after", style: [])
        ])

        // An <a> nobody closes is bounded by the end of the fragment.
        #expect(ReleaseNotesHTML.runs(in: #"<a href="https://x.example">unclosed"#) == [
            ReleaseNotesHTML.Run(text: "unclosed", style: [], link: URL(string: "https://x.example"))
        ])
    }

    /// Deciding "self-closing" from the raw tag's last character read every
    /// bare href ending in "/" — most URLs — as "<a …/>", and silently dropped
    /// the link.
    @Test func aSlashEndingABareHrefBelongsToTheURLNotTheTag() {
        #expect(ReleaseNotesHTML.runs(in: "<a href=https://example.com/>Home</a>") == [
            ReleaseNotesHTML.Run(text: "Home", style: [], link: URL(string: "https://example.com/"))
        ])

        #expect(ReleaseNotesHTML.runs(in: #"<a href="https://example.com/">Home</a>"#) == [
            ReleaseNotesHTML.Run(text: "Home", style: [], link: URL(string: "https://example.com/"))
        ])

        // A "/" of the tag's own still closes it, even after a bare href that
        // ends in one.
        let closed = "<a href=https://example.com/ />Home and the rest"
        #expect(ReleaseNotesHTML.runs(in: closed).allSatisfy { $0.link == nil })
        #expect(ReleaseNotesHTML.plainText(closed) == "Home and the rest")

        // A "/" that is not the last thing in the tag is not the tag's own.
        #expect(ReleaseNotesHTML.runs(in: "<a / href=https://example.com/>L</a>").first?.link
                == URL(string: "https://example.com/"))

        for lineBreak in ["a<br>b", "a<br/>b", "a<br />b"] {
            #expect(ReleaseNotesHTML.plainText(lineBreak) == "a\nb", "line break: \(lineBreak)")
        }
    }

    /// The inner href is rejected, so its label must lose the link rather than
    /// inherit the outer anchor's destination.
    @Test func aNestedAnchorTakesTheInnermostDestination() {
        #expect(ReleaseNotesHTML.runs(in:
            #"<a href="https://ok.example">read <a href="javascript:x">this</a></a>"#) == [
            ReleaseNotesHTML.Run(text: "read ", style: [], link: URL(string: "https://ok.example")),
            ReleaseNotesHTML.Run(text: "this", style: [])
        ])
    }

    /// A space at either edge of a link is underlined, tinted and inside the hit
    /// region — it belongs outside the anchor, on whichever side it was written.
    @Test func theSpacesAroundALinkStayOutsideIt() {
        let opening = ReleaseNotesHTML.runs(in: #"<b>See</b> <a href="https://example.com">here</a>"#)

        #expect(opening.map(\.text).joined() == "See here")
        #expect(opening.allSatisfy { $0.link == nil || !$0.text.hasPrefix(" ") })

        // The closing side: the space in front of </a> used to become a run of
        // its own, still tinted, underlined and inside the hit region.
        let closing = ReleaseNotesHTML.runs(in: #"See <a href="https://x.example"><b>the page</b> </a>now"#)

        #expect(closing.map(\.text).joined() == "See the page now")
        #expect(closing.allSatisfy { $0.link == nil || !$0.text.trimmingCharacters(in: .whitespaces).isEmpty })
    }

    /// Self-closing was consulted in the `<a>` branch only, so "<b/>" pushed a
    /// depth nothing ever popped and emboldened every remaining word.
    @Test func aSelfClosingEmphasisTagDoesNotStyleTheRestOfTheNote() {
        #expect(ReleaseNotesHTML.runs(in: "before <b/> after").allSatisfy { !$0.style.contains(.bold) })
        #expect(ReleaseNotesHTML.plainText("before <b/> after") == "before after")

        #expect(ReleaseNotesHTML.runs(in: "before <i/> after").allSatisfy { !$0.style.contains(.italic) })
        #expect(ReleaseNotesHTML.runs(in: "x<strong />y").allSatisfy { !$0.style.contains(.bold) })
        #expect(ReleaseNotesHTML.runs(in: "x<em />y").allSatisfy { !$0.style.contains(.italic) })

        // The paired forms still style what they wrap.
        #expect(ReleaseNotesHTML.runs(in: "<b>still bold</b>").first?.style.contains(.bold) == true)
    }

    /// Skipping quoted values while looking for the tag's ">" entered quote mode
    /// on any apostrophe, so the one in a bare "href=it's" paired up with the
    /// next one in the text and everything between them was swallowed as markup.
    @Test func anApostropheInABareValueIsAnOrdinaryCharacter() {
        #expect(ReleaseNotesHTML.plainText("<a href=it's>label</a> and <b>Ray's</b> note")
                == "label and Ray's note")
        #expect(ReleaseNotesHTML.plainText("<b>Ray's</b> and <i>don't</i>") == "Ray's and don't")

        // A quote in value position — with or without whitespace after the "=" —
        // still shields a ">" sitting inside the value.
        #expect(ReleaseNotesHTML.plainText(#"<a href="https://ex.com/?q=a>b" title="t">here</a>"#) == "here")
        #expect(ReleaseNotesHTML.plainText("<a href = 'https://ex.com/?q=a>b'>x</a>") == "x")
    }

    /// The href used to be decoded by running it through the whole parser, which
    /// also stripped markup and comments out of it — a valid, allow-listed, but
    /// different destination than the one in the feed.
    @Test func theHrefIsTheFeedsVerbatim() {
        let markup = #"<a href="https://ex.com/?q=<b>x</b>">Docs</a>"#
        #expect(ReleaseNotesHTML.runs(in: markup).first?.link != URL(string: "https://ex.com/?q=x"))
        #expect(ReleaseNotesHTML.plainText(markup) == "Docs")

        let commented = #"<a href="https://ex.com/<!-- c -->path">Docs</a>"#
        #expect(ReleaseNotesHTML.runs(in: commented).first?.link != URL(string: "https://ex.com/path"))

        // Entities in the href must still decode: feeds escape query separators,
        // and "?a=1&amp;b=2" is not a URL until they do.
        #expect(ReleaseNotesHTML.runs(in: #"<a href="https://ex.com/p?a=1&amp;b=2">x</a>"#).first?.link
                == URL(string: "https://ex.com/p?a=1&b=2"))
        #expect(ReleaseNotesHTML.runs(in: #"<a href="https://ex.com/p?a=1&#38;b=2">x</a>"#).first?.link
                == URL(string: "https://ex.com/p?a=1&b=2"))
    }

    // MARK: - Robustness

    @Test func unsupportedTagsAreDroppedButTheirTextIsKept() {
        #expect(ReleaseNotesHTML.plainText(#"<span class="x">kept</span><a href="u">link</a>"#) == "keptlink")
    }

    @Test func unterminatedTagIsTreatedAsText() {
        #expect(ReleaseNotesHTML.plainText("2 < 3 and counting") == "2 < 3 and counting")
    }

    @Test func entitiesAreDecodedAndUnknownOnesStayLiteral() {
        #expect(ReleaseNotesHTML.plainText("a &amp; b &mdash; c &#8212; d &#x2014; e") == "a & b — c — d — e")
        #expect(ReleaseNotesHTML.plainText("&bogus; stays") == "&bogus; stays")
        #expect(ReleaseNotesHTML.plainText("&lt;b&gt;not a tag&lt;/b&gt;") == "<b>not a tag</b>")
    }

    @Test func feedIndentationCollapsesToSingleSpaces() {
        #expect(ReleaseNotesHTML.plainText("\n    Short clips now\n    transcribe faster.\n  ")
                == "Short clips now transcribe faster.")
    }

    @Test func breakTagStartsANewLine() {
        #expect(ReleaseNotesHTML.plainText("first<br/>  second") == "first\nsecond")
    }

    // MARK: - AppcastItem integration

    /// The 2.42.0 shape: no heading, bold lead-ins inside the bullets.
    @Test func bulletsKeepEmphasisAndNoTitleIsInventedFromTheFirstBullet() {
        let item = AppcastItem(
            version: "2.42.0",
            buildNumber: "111",
            pubDate: Date(),
            releaseNotes: """
                <ul>
                    <li><b>Redesigned first-run setup</b> — a clearer 8-step walkthrough.</li><li>Short clips now transcribe much faster.</li>
                </ul>
                """
        )

        // A <b> inside the first bullet is emphasis, not a release title.
        #expect(item.releaseTitle == nil)
        #expect(item.bulletPoints.count == 2)
        #expect(String(item.bulletPoints[0].characters)
                == "Redesigned first-run setup — a clearer 8-step walkthrough.")
        #expect(item.bulletPoints[0].runs.contains { $0.inlinePresentationIntent?.contains(.stronglyEmphasized) == true })
        #expect(item.bulletPoints[1].runs.allSatisfy { $0.inlinePresentationIntent == nil })
    }

    /// The older shape: a heading before the list is still used as the title.
    @Test func headingBeforeTheListIsUsedAsTheTitle() {
        let item = AppcastItem(
            version: "2.5.3",
            buildNumber: "32",
            pubDate: Date(),
            releaseNotes: "<b>Enhanced Audio Recording</b>\n<ul>\n<li>Improved stability</li>\n</ul>"
        )

        #expect(item.releaseTitle.map { String($0.characters) } == "Enhanced Audio Recording")
        #expect(item.bulletPoints.map { String($0.characters) } == ["Improved stability"])
    }

    /// A link in the heading is as clickable as one in a bullet.
    @Test func headingKeepsItsLink() {
        let item = AppcastItem(
            version: "2.43.0",
            buildNumber: "112",
            pubDate: Date(),
            releaseNotes: #"<b>See the <a href="https://example.com/latency">latency page</a></b><ul><li>x</li></ul>"#
        )

        #expect(item.releaseTitle?.runs.contains { $0.link != nil } == true)
    }

    @Test func listItemsWithAttributesAndEmptyItemsAreHandled() {
        let item = AppcastItem(
            version: "1.0.0",
            buildNumber: "1",
            pubDate: Date(),
            releaseNotes: #"<ul><li class="x">kept</li><li>  </li><li>also kept</li></ul>"#
        )

        #expect(item.bulletPoints.map { String($0.characters) } == ["kept", "also kept"])
    }

    @Test func missingReleaseNotesProduceNoTitleAndNoBullets() {
        let item = AppcastItem(version: "1.0.0", buildNumber: "1", pubDate: Date(), releaseNotes: nil)

        #expect(item.releaseTitle == nil)
        #expect(item.bulletPoints.isEmpty)
        #expect(!item.hasReleaseNotes)
    }
}
