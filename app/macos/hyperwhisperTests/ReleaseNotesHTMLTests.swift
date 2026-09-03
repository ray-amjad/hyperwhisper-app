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

        // Each allSatisfy is paired with the text it should have produced.
        // allSatisfy is vacuously true on empty or truncated output, so on its
        // own it also passes for a parser that aborted on the self-closing tag
        // and lost the rest of the note.
        #expect(ReleaseNotesHTML.runs(in: "before <i/> after").allSatisfy { !$0.style.contains(.italic) })
        #expect(ReleaseNotesHTML.plainText("before <i/> after") == "before after")

        #expect(ReleaseNotesHTML.runs(in: "x<strong />y").allSatisfy { !$0.style.contains(.bold) })
        #expect(ReleaseNotesHTML.plainText("x<strong />y") == "xy")

        #expect(ReleaseNotesHTML.runs(in: "x<em />y").allSatisfy { !$0.style.contains(.italic) })
        #expect(ReleaseNotesHTML.plainText("x<em />y") == "xy")

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

    /// Skipping to the matching quote searched the whole fragment, so a value
    /// never closed inside its own tag paired up with the quote of a later one,
    /// and everything between them — the label, its "</a>", the next tag — was
    /// swallowed as one tag body.
    @Test func aValueLeftOpenDoesNotPairWithALaterTagsQuote() {
        let html = #"Read <a href="https://ex.com/latency>the page</a> for <b class="hl">details</b>."#
        let runs = ReleaseNotesHTML.runs(in: html)

        #expect(ReleaseNotesHTML.plainText(html) == "Read the page for details.")
        #expect(runs.allSatisfy { $0.link == nil })
        #expect(runs.contains { $0.text == "details" && $0.style.contains(.bold) })
        #expect(runs.allSatisfy { !$0.text.contains("<") && !$0.text.contains("href") })

        // A ">" inside a value that *is* closed still does not end the tag, and
        // an apostrophe in a bare value is still an ordinary character.
        #expect(ReleaseNotesHTML.plainText(#"<a href="https://ex.com/?q=a>b" title="t">here</a>"#) == "here")
        #expect(ReleaseNotesHTML.plainText("<a href=it's>label</a> and <b>Ray's</b> note")
                == "label and Ray's note")
    }

    /// parseTag sets both flags for "</a/>", and the self-closing guard ran
    /// first, so nothing was ever popped and the rest of the note stayed
    /// linked, bold or italic.
    @Test func aClosingTagThatAlsoClosesItselfStillPops() {
        let anchor = #"<a href="https://hyperwhisper.app/changelog">changelog</a/>. Also faster startup."#
        let runs = ReleaseNotesHTML.runs(in: anchor)

        #expect(ReleaseNotesHTML.plainText(anchor) == "changelog. Also faster startup.")
        #expect(runs.contains { $0.text == "changelog" && $0.link != nil })
        #expect(runs.allSatisfy { $0.link == nil || $0.text == "changelog" })

        #expect(ReleaseNotesHTML.runs(in: "<b>New:</b/> dictation is faster")
            .allSatisfy { !$0.style.contains(.bold) || $0.text == "New:" })
        #expect(ReleaseNotesHTML.runs(in: "<i>x</i/> y")
            .allSatisfy { !$0.style.contains(.italic) || $0.text == "x" })
        #expect(ReleaseNotesHTML.runs(in: #"<a href="https://x.example">x</a />after"#)
            .allSatisfy { $0.link == nil || $0.text == "x" })

        // The opening self-closing forms still change no state.
        #expect(ReleaseNotesHTML.runs(in: "before <b/> after").allSatisfy { !$0.style.contains(.bold) })
        #expect(ReleaseNotesHTML.runs(in: #"Before <a href="https://x.example"/> after"#)
            .allSatisfy { $0.link == nil })
        #expect(ReleaseNotesHTML.plainText("a<br/>b") == "a\nb")
    }

    /// The pending space was committed on the opening tag and then re-armed
    /// from producedText, so an element that produces no text at all was spelt
    /// with a space on each side of nothing.
    @Test func anElementThatProducesNoTextIsWrittenWithOneSpaceAroundIt() {
        for html in [
            #"Read <a href="https://x.example"><img src="badge.png"></a> the docs"#,
            #"Read <a href="https://x.example"></a> the docs"#,
            #"Read <a href="https://x.example">   </a> the docs"#
        ] {
            #expect(ReleaseNotesHTML.plainText(html) == "Read the docs", "spacing: \(html)")
        }

        // Both space cases above still hold.
        let opening = ReleaseNotesHTML.runs(in: #"<b>See</b> <a href="https://example.com">here</a>"#)
        #expect(opening.map(\.text).joined() == "See here")
        #expect(opening.allSatisfy { $0.link == nil || !$0.text.hasPrefix(" ") })

        let closing = ReleaseNotesHTML.runs(in: #"See <a href="https://x.example"><b>the page</b> </a>now"#)
        #expect(closing.map(\.text).joined() == "See the page now")
        #expect(closing.allSatisfy { $0.link == nil || !$0.text.trimmingCharacters(in: .whitespaces).isEmpty })
    }

    /// The name was recorded after the value parse, so giving up on a malformed
    /// value on the first token discarded the element with it.
    @Test func aMalformedValueOnTheFirstTokenKeepsTheElementName() {
        #expect(ReleaseNotesHTML.plainText("line one<br = >line two") == "line one\nline two")
        #expect(ReleaseNotesHTML.plainText(#"a<br = "unterminated>b"#) == "a\nb")
    }

    /// Whitespace is collapsed one Unicode SCALAR VALUE at a time: the unit
    /// `hw-releasenotes` pins for every index, scan limit and whitespace
    /// predicate on all three heads. A CRLF is two scalars — "\r" and "\n",
    /// each of them collapsible — so it collapses like any other run of
    /// whitespace, and the expectations below are the same on every head.
    ///
    /// This file used to read text by GRAPHEME, where "\r\n" is one Character
    /// equal to neither "\r" nor "\n", so the CRLF had to be named outright to
    /// collapse at all; the C# mirror walked UTF-16 units. Both are gone.
    /// A non-breaking space is not collapsible whitespace on any of them.
    @Test func aCarriageReturnLineFeedCollapsesLikeAnyOtherWhitespace() {
        #expect(ReleaseNotesHTML.plainText("line one\r\nline two") == "line one line two")
        #expect(ReleaseNotesHTML.plainText("a\r\n\r\n  b") == "a b")
        #expect(ReleaseNotesHTML.plainText("a&nbsp;b") == "a\u{00A0}b")
    }

    /// The href used to be decoded by running it through the whole parser, which
    /// also stripped markup and comments out of it — a valid, allow-listed, but
    /// different destination than the one in the feed. The destination is
    /// pinned outright: an inequality on an Optional is also true when the link
    /// was dropped altogether, so it could not fail.
    @Test func theHrefIsTheFeedsVerbatim() {
        let markup = #"<a href="https://ex.com/?q=<b>x</b>">Docs</a>"#
        #expect(ReleaseNotesHTML.runs(in: markup).first?.link?.absoluteString
                == "https://ex.com/?q=%3Cb%3Ex%3C/b%3E")
        #expect(ReleaseNotesHTML.plainText(markup) == "Docs")

        let commented = #"<a href="https://ex.com/<!-- c -->path">Docs</a>"#
        #expect(ReleaseNotesHTML.runs(in: commented).first?.link != nil)
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

    /// A DELIBERATE strictness change (#284, decision (a)). "&#+65;" and
    /// "&#x+41;" used to decode to "A" here and stayed literal on Windows,
    /// because `UInt32(_:radix:)` accepts a leading sign and
    /// `NumberStyles.None` does not. Nothing pinned either, no feed carries
    /// them, and this is remote input — so the shared decoder rejects a signed
    /// body on both heads and the "&" stays literal text. ("&#-65;" never
    /// decoded on either; it is pinned so it cannot start to.)
    @Test func aNumericEntityWithASignedBodyStaysLiteral() {
        #expect(ReleaseNotesHTML.plainText("&#+65;") == "&#+65;")
        #expect(ReleaseNotesHTML.plainText("&#-65;") == "&#-65;")
        #expect(ReleaseNotesHTML.plainText("&#x+41;") == "&#x+41;")

        // The well-formed spellings still decode.
        #expect(ReleaseNotesHTML.plainText("&#65;") == "A")
        #expect(ReleaseNotesHTML.plainText("&#x41;") == "A")
    }

    /// The other half of decision (a), pinned in the direction this head
    /// already took: "&#x 41;" never decoded here, and decoded to "A" on
    /// Windows, where `NumberStyles.HexNumber` allows leading white. The
    /// shared decoder keeps it literal, so this cannot regress into a decode.
    @Test func aNumericEntityWithWhitespaceInItsBodyStaysLiteral() {
        #expect(ReleaseNotesHTML.plainText("&#x 41;") == "&#x 41;")
        #expect(ReleaseNotesHTML.plainText("&# 65;") == "&# 65;")
        #expect(ReleaseNotesHTML.plainText("&#65 ;") == "&#65 ;")
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

    // MARK: - Decision (c): one title rule on both heads (#284)

    /// The Windows feed's shape, which this head could not read before: it took
    /// "everything before the list" and so made the whole `<h2>` element the
    /// title only by accident of its text. The rule is now explicit and shared.
    @Test func anH2BeforeTheListBecomesTheTitle() {
        let item = AppcastItem(
            version: "1.11.0",
            buildNumber: "1",
            pubDate: Date(),
            releaseNotes: "<h2>What's New in 1.11.0</h2>\n<ul>\n<li>Links are now clickable.</li>\n</ul>"
        )

        #expect(item.releaseTitle.map { String($0.characters) } == "What's New in 1.11.0")
        #expect(item.bulletPoints.map { String($0.characters) } == ["Links are now clickable."])
    }

    /// The half of decision (c) Windows did not have either: its `<h2>` regex
    /// was case-sensitive and allowed no attributes.
    @Test func theTitleHeadingMatchIsCaseInsensitiveAndAllowsAttributes() {
        let item = AppcastItem(
            version: "1.0.0",
            buildNumber: "1",
            pubDate: Date(),
            releaseNotes: #"<H2 id="whats-new">Title</H2><ul><li>x</li></ul>"#
        )

        #expect(item.releaseTitle.map { String($0.characters) } == "Title")
    }

    /// An `<h2>` anywhere wins over the pre-list content — the Windows rule —
    /// but an `<h3>` is a sub-heading and never becomes a title on its own.
    @Test func onlyAnH2IsMatchedByName() {
        let withH3 = AppcastItem(
            version: "1.0.0", buildNumber: "1", pubDate: Date(),
            releaseNotes: "<ul><li>x</li></ul><h3>Details</h3>"
        )
        #expect(withH3.releaseTitle == nil)

        let withH2 = AppcastItem(
            version: "1.0.0", buildNumber: "1", pubDate: Date(),
            releaseNotes: "<ul><li>x</li></ul><h2>Late heading</h2>"
        )
        #expect(withH2.releaseTitle.map { String($0.characters) } == "Late heading")
    }

    /// A closing tag with whitespace before its `>` still closes the item. This
    /// head already behaved this way — it searched for the prefix `</li` — and
    /// the shared core keeps that behaviour rather than the Windows regex's,
    /// which dropped the bullet entirely.
    @Test func aClosingListTagWithWhitespaceStillClosesTheItem() {
        let item = AppcastItem(
            version: "1.0.0", buildNumber: "1", pubDate: Date(),
            releaseNotes: "<ul><li>one</li ><li>two</li></ul>"
        )

        #expect(item.bulletPoints.map { String($0.characters) } == ["one", "two"])
    }

    // MARK: - Parse once, at construction (#284)

    /// STRUCTURAL: `releaseTitle` and `bulletPoints` must be STORED properties.
    ///
    /// They were computed, so every SwiftUI `body` pass of every
    /// `ReleaseNotesCard` re-ran the whole HTML parse — for every release in the
    /// list, on every redraw. `Mirror` reports stored properties only, so a
    /// regression back to `var releaseTitle: AttributedString? { ... }` fails
    /// here rather than silently costing a parse per frame.
    @Test func theTitleAndBulletsAreStoredNotRecomputed() {
        let item = AppcastItem(
            version: "2.5.3", buildNumber: "32", pubDate: Date(),
            releaseNotes: "<b>Title</b><ul><li>one</li></ul>"
        )

        let stored = Mirror(reflecting: item).children.compactMap(\.label)
        #expect(stored.contains("releaseTitle"))
        #expect(stored.contains("bulletPoints"))

        // And the stored values are the parsed ones, not empty defaults.
        #expect(item.releaseTitle.map { String($0.characters) } == "Title")
        #expect(item.bulletPoints.map { String($0.characters) } == ["one"])
    }

    /// Equatable still holds over the added stored properties: two items built
    /// from the same feed entry compare equal, and a different note does not.
    @Test func equalityStillFollowsTheFeedEntry() {
        let date = Date()
        let notes = "<b>Title</b><ul><li>one</li></ul>"

        let first = AppcastItem(version: "1.0.0", buildNumber: "1", pubDate: date, releaseNotes: notes)
        let second = AppcastItem(version: "1.0.0", buildNumber: "1", pubDate: date, releaseNotes: notes)
        let other = AppcastItem(version: "1.0.0", buildNumber: "1", pubDate: date,
                                releaseNotes: "<b>Other</b><ul><li>two</li></ul>")

        #expect(first == second)
        #expect(first != other)
    }
}

// MARK: - Appcast selection (#353)

/// The macOS side of #353: this head reads the XML and nothing else.
///
/// `AppcastParser.feedEntries` must hand every `<item>` over verbatim, and
/// `AppcastParser.selectReleases` must take the shared step's answer as given.
/// The RULES themselves — version precedence, the drop conditions, dedupe, the
/// ordering, the date grammar — are pinned by `hw-releasenotes`' own unit tests;
/// what is pinned here is the wiring, plus the handful of feed shapes where a
/// wiring mistake would look like a rule change.
struct AppcastSelectionTests {

    /// A feed with `items` spliced into the channel, after the channel's own
    /// `<title>hyperwhisper</title>` — the string that must never become a version.
    private func feed(_ items: String) -> Data {
        return Data("""
        <?xml version="1.0" encoding="utf-8"?>
        <rss xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle" version="2.0">
            <channel>
                <title>hyperwhisper</title>
        \(items)
            </channel>
        </rss>
        """.utf8)
    }

    /// The live macOS feed's shape, verbatim in every field.
    @Test func everyFieldOfARealItemReachesTheCoreUntouched() throws {
        let entries = try AppcastParser.feedEntries(from: feed("""
                <item>
                    <title>2.46.0</title>
                    <pubDate>Wed, 02 Sep 2026 12:06:28 +0000</pubDate>
                    <sparkle:version>116</sparkle:version>
                    <sparkle:shortVersionString>2.46.0</sparkle:shortVersionString>
                    <sparkle:minimumSystemVersion>14.6</sparkle:minimumSystemVersion>
                    <description><![CDATA[<ul><li>One</li></ul>]]></description>
                </item>
        """))

        #expect(entries.count == 1)
        #expect(entries[0].title == "2.46.0")
        #expect(entries[0].sparkleVersion == "116")
        #expect(entries[0].sparkleShortVersionString == "2.46.0")
        #expect(entries[0].pubDate == "Wed, 02 Sep 2026 12:06:28 +0000")
        #expect(entries[0].description == "<ul><li>One</li></ul>")
        #expect(entries[0].hasReleaseNotesLink == false)
    }

    /// FINDING #3, PINNED. `sparkle:version` and `sparkle:shortVersionString`
    /// are matched on their namespace URI and local name, never on the
    /// `sparkle:` prefix — the old code tested `qualifiedName == "sparkle:version"`
    /// under `shouldProcessNamespaces = true`, which is not specified to be
    /// populated in that mode. If Foundation ever changes what it reports, this
    /// fails here rather than silently emptying the build number in the field.
    @Test func sparkleFieldsAreMatchedByNamespaceNotByPrefix() throws {
        // Same two elements, bound to the Sparkle namespace through a
        // differently-named prefix. The prefix test could not have passed this.
        let entries = try AppcastParser.feedEntries(from: Data("""
        <?xml version="1.0" encoding="utf-8"?>
        <rss xmlns:sp="http://www.andymatuschak.org/xml-namespaces/sparkle" version="2.0">
            <channel>
                <item>
                    <title>2.46.0</title>
                    <sp:version>116</sp:version>
                    <sp:shortVersionString>2.46.0</sp:shortVersionString>
                </item>
            </channel>
        </rss>
        """.utf8))

        #expect(entries.count == 1)
        #expect(entries[0].sparkleVersion == "116")
        #expect(entries[0].sparkleShortVersionString == "2.46.0")
    }

    /// A `<version>` in no namespace at all is NOT `sparkle:version`.
    @Test func anUnnamespacedVersionElementIsIgnored() throws {
        let entries = try AppcastParser.feedEntries(from: feed("""
                <item>
                    <title>2.46.0</title>
                    <version>999</version>
                </item>
        """))

        #expect(entries[0].sparkleVersion == nil)
    }

    /// An absent element is `nil`, not `""`. "Absent" and "present but blank"
    /// are the core's distinction to make; flattening them here would move that
    /// decision into the head.
    @Test func absentElementsArriveAsNilAndBlankOnesAsEmpty() throws {
        let entries = try AppcastParser.feedEntries(from: feed("""
                <item>
                    <title>2.46.0</title>
                </item>
                <item>
                    <title></title>
                    <pubDate></pubDate>
                    <sparkle:version></sparkle:version>
                    <description></description>
                </item>
        """))

        #expect(entries.count == 2)
        #expect(entries[0].pubDate == nil)
        #expect(entries[0].sparkleVersion == nil)
        #expect(entries[0].sparkleShortVersionString == nil)
        #expect(entries[0].description == nil)

        #expect(entries[1].title == "")
        #expect(entries[1].pubDate == "")
        #expect(entries[1].sparkleVersion == "")
        #expect(entries[1].description == "")
    }

    /// The reader DROPS NOTHING. An item with no usable field at all still
    /// produces an entry — the core is the only thing allowed to decide that an
    /// item is not worth showing, or the two heads drop different items again.
    @Test func anItemWithNothingUsableStillBecomesAnEntry() throws {
        let entries = try AppcastParser.feedEntries(from: feed("""
                <item>
                </item>
        """))

        #expect(entries.count == 1)
        #expect(entries[0].title == nil)
        #expect(entries[0].pubDate == nil)
        #expect(entries[0].description == nil)
    }

    /// `sparkle:releaseNotesLink` is reported as a flag, not read as a value.
    @Test func aReleaseNotesLinkIsReportedAsAFlag() throws {
        let entries = try AppcastParser.feedEntries(from: feed("""
                <item>
                    <title>2.46.0</title>
                    <sparkle:releaseNotesLink>https://example.com/notes.html</sparkle:releaseNotesLink>
                    <description><![CDATA[<ul><li>One</li></ul>]]></description>
                </item>
                <item>
                    <title>2.45.0</title>
                    <description><![CDATA[<ul><li>Two</li></ul>]]></description>
                </item>
        """))

        #expect(entries[0].hasReleaseNotesLink == true)
        #expect(entries[1].hasReleaseNotesLink == false)

        // And the core drops the linked one: this card cannot fetch a URL.
        let releases = try AppcastParser.selectReleases(from: feed("""
                <item>
                    <title>2.46.0</title>
                    <sparkle:releaseNotesLink>https://example.com/notes.html</sparkle:releaseNotesLink>
                    <description><![CDATA[<ul><li>One</li></ul>]]></description>
                </item>
                <item>
                    <title>2.45.0</title>
                    <description><![CDATA[<ul><li>Two</li></ul>]]></description>
                </item>
        """))

        #expect(releases.map(\.version) == ["2.45.0"])
    }

    /// The channel's `<title>hyperwhisper</title>` must not leak into the first
    /// item. It sits before every `<item>` and `<title>` is now a version
    /// candidate, so a leak would put a release called "hyperwhisper" at the top
    /// of Recent Updates. The per-item reset is what prevents it.
    @Test func theChannelTitleNeverBecomesAnItemsTitle() throws {
        let entries = try AppcastParser.feedEntries(from: feed("""
                <item>
                    <pubDate>Wed, 02 Sep 2026 12:06:28 +0000</pubDate>
                    <sparkle:shortVersionString>2.46.0</sparkle:shortVersionString>
                    <description><![CDATA[<ul><li>One</li></ul>]]></description>
                </item>
        """))

        #expect(entries[0].title == nil)
    }

    /// END TO END: version, order and dedupe all come from the shared step.
    ///
    /// The fixture is deliberately in the wrong order and carries a duplicate:
    /// 1.0.0 first, then 2.0.0 which is newer, then a SECOND 1.0.0 that is newer
    /// still. The shared rules give [2.0.0, 1.0.0], and the surviving 1.0.0 is
    /// the first in document order — not the newest — which is the tie-break
    /// Windows has always used.
    @Test func versionOrderAndDedupeComeFromTheSharedStep() throws {
        let releases = try AppcastParser.selectReleases(from: feed("""
                <item>
                    <title>1.0.0</title>
                    <pubDate>Wed, 02 Sep 2026 12:06:28 +0000</pubDate>
                    <sparkle:version>100</sparkle:version>
                    <sparkle:shortVersionString>1.0.0</sparkle:shortVersionString>
                    <description><![CDATA[<ul><li>first in document order</li></ul>]]></description>
                </item>
                <item>
                    <title>2.0.0</title>
                    <pubDate>Fri, 04 Sep 2026 00:00:00 +0000</pubDate>
                    <sparkle:version>200</sparkle:version>
                    <sparkle:shortVersionString>2.0.0</sparkle:shortVersionString>
                    <description><![CDATA[<ul><li>newest</li></ul>]]></description>
                </item>
                <item>
                    <title>1.0.0</title>
                    <pubDate>Sat, 05 Sep 2026 00:00:00 +0000</pubDate>
                    <sparkle:version>101</sparkle:version>
                    <sparkle:shortVersionString>1.0.0</sparkle:shortVersionString>
                    <description><![CDATA[<ul><li>duplicate version, later date</li></ul>]]></description>
                </item>
        """))

        #expect(releases.map(\.version) == ["2.0.0", "1.0.0"])
        #expect(releases.map(\.buildNumber) == ["200", "100"])
        #expect(releases[1].bulletPoints.map { String($0.characters) } == ["first in document order"])
    }

    /// The `limit:` cap keeps the same items it would have kept when it was
    /// applied *after* the map to `AppcastItem`.
    ///
    /// `fetchReleases` used to build an `AppcastItem` for all 77 releases of the
    /// live feed — 77 `releaseNotesParse` FFI calls and 77 `AttributedString`s —
    /// and then keep 5. The cap now runs on the core's answer instead. That is
    /// only safe because the core has already filtered, deduplicated and ordered
    /// by then, and the map is one-to-one and order-preserving, so `prefix` on
    /// either side of it selects the same releases. The second `#expect` below
    /// is that equivalence, asserted rather than assumed; the fixture puts
    /// document order, date order and a duplicate version all in disagreement so
    /// a cap that ran at the wrong point could not agree by accident.
    @Test func theCapKeepsTheSameReleasesWhicheverSideOfTheMapItRunsOn() throws {
        let fixture = feed("""
                <item>
                    <title>1.0.0</title>
                    <pubDate>Wed, 02 Sep 2026 12:06:28 +0000</pubDate>
                    <sparkle:shortVersionString>1.0.0</sparkle:shortVersionString>
                    <description><![CDATA[<ul><li>oldest, but FIRST in the feed</li></ul>]]></description>
                </item>
                <item>
                    <title>3.0.0</title>
                    <pubDate>Sun, 06 Sep 2026 00:00:00 +0000</pubDate>
                    <sparkle:shortVersionString>3.0.0</sparkle:shortVersionString>
                    <description><![CDATA[<ul><li>newest</li></ul>]]></description>
                </item>
                <item>
                    <title>1.0.0</title>
                    <pubDate>Mon, 07 Sep 2026 00:00:00 +0000</pubDate>
                    <sparkle:shortVersionString>1.0.0</sparkle:shortVersionString>
                    <description><![CDATA[<ul><li>duplicate version, newest date — dropped by dedupe</li></ul>]]></description>
                </item>
                <item>
                    <title>2.0.0</title>
                    <pubDate>Fri, 04 Sep 2026 00:00:00 +0000</pubDate>
                    <sparkle:shortVersionString>2.0.0</sparkle:shortVersionString>
                    <description><![CDATA[<ul><li>middle</li></ul>]]></description>
                </item>
        """))

        let capped = try AppcastParser.selectReleases(from: fixture, limit: 2)
        let uncapped = try AppcastParser.selectReleases(from: fixture)

        #expect(uncapped.map(\.version) == ["3.0.0", "2.0.0", "1.0.0"])
        #expect(capped == Array(uncapped.prefix(2)))
        #expect(capped.map(\.version) == ["3.0.0", "2.0.0"])

        // A cap larger than the list is not an error, and `nil` means all of them.
        let overCap = try AppcastParser.selectReleases(from: fixture, limit: 99)
        #expect(overCap == uncapped)
    }

    /// `sparkle:shortVersionString` wins over `sparkle:version` and `<title>`.
    /// On the live macOS feed all three agree except `sparkle:version`, which is
    /// the build number — reading it as the version would show "116".
    @Test func theVersionIsTheShortVersionStringNotTheBuildNumber() throws {
        let releases = try AppcastParser.selectReleases(from: feed("""
                <item>
                    <title>HyperWhisper 2.46.0</title>
                    <pubDate>Wed, 02 Sep 2026 12:06:28 +0000</pubDate>
                    <sparkle:version>116</sparkle:version>
                    <sparkle:shortVersionString>2.46.0</sparkle:shortVersionString>
                    <description><![CDATA[<ul><li>One</li></ul>]]></description>
                </item>
        """))

        #expect(releases.map(\.version) == ["2.46.0"])
        #expect(releases[0].buildNumber == "116")
    }

    /// With no `sparkle:version` to pass through, `buildNumber` falls back to
    /// the resolved version — this head's only native-only field, and still not
    /// rendered anywhere.
    @Test func buildNumberFallsBackToTheVersion() throws {
        let releases = try AppcastParser.selectReleases(from: feed("""
                <item>
                    <title>2.46.0</title>
                    <pubDate>Wed, 02 Sep 2026 12:06:28 +0000</pubDate>
                    <description><![CDATA[<ul><li>One</li></ul>]]></description>
                </item>
        """))

        #expect(releases.map(\.version) == ["2.46.0"])
        #expect(releases[0].buildNumber == "2.46.0")
    }

    /// An entry with no inline notes is dropped by the core, so `fetchReleases`
    /// no longer filters on `hasReleaseNotes` itself. A whitespace-only
    /// `<description>` counts as none.
    @Test func entriesWithoutInlineNotesAreDroppedByTheCore() throws {
        let releases = try AppcastParser.selectReleases(from: feed("""
                <item>
                    <title>3.0.0</title>
                    <pubDate>Sun, 06 Sep 2026 00:00:00 +0000</pubDate>
                </item>
                <item>
                    <title>2.0.0</title>
                    <pubDate>Fri, 04 Sep 2026 00:00:00 +0000</pubDate>
                    <description>   </description>
                </item>
                <item>
                    <title>1.0.0</title>
                    <pubDate>Wed, 02 Sep 2026 12:06:28 +0000</pubDate>
                    <description><![CDATA[<ul><li>kept</li></ul>]]></description>
                </item>
        """))

        #expect(releases.map(\.version) == ["1.0.0"])
        #expect(releases.allSatisfy { $0.hasReleaseNotes })
    }

    /// A BEHAVIOUR CHANGE THIS HEAD GAINS: an item with a missing or unreadable
    /// `<pubDate>` used to be dropped outright. It now survives, dated to the
    /// epoch — which sorts it LAST, where the old `Date()` fallback would have
    /// sorted it first and moved it on every fetch.
    @Test func anUnreadableDateSurvivesAtTheEpochAndSortsLast() throws {
        let releases = try AppcastParser.selectReleases(from: feed("""
                <item>
                    <title>3.0.0</title>
                    <pubDate>not a date at all</pubDate>
                    <description><![CDATA[<ul><li>broken date</li></ul>]]></description>
                </item>
                <item>
                    <title>2.0.0</title>
                    <description><![CDATA[<ul><li>no date element</li></ul>]]></description>
                </item>
                <item>
                    <title>1.0.0</title>
                    <pubDate>Wed, 02 Sep 2026 12:06:28 +0000</pubDate>
                    <description><![CDATA[<ul><li>real date</li></ul>]]></description>
                </item>
        """))

        #expect(releases.map(\.version) == ["1.0.0", "3.0.0", "2.0.0"])
        #expect(releases[1].pubDate.timeIntervalSince1970 == 0)
        #expect(releases[2].pubDate.timeIntervalSince1970 == 0)

        // And the epoch renders. `formattedDate` is locale-dependent, so what is
        // asserted is that it produces something rather than what it says.
        #expect(!releases[1].formattedDate.isEmpty)
    }

    /// The date is read by the shared RFC 2822 parser, so the epoch it produces
    /// is the one the item carries.
    @Test func theDateIsTheSharedParsersAnswer() throws {
        let releases = try AppcastParser.selectReleases(from: feed("""
                <item>
                    <title>2.46.0</title>
                    <pubDate>Wed, 02 Sep 2026 12:06:28 +0000</pubDate>
                    <description><![CDATA[<ul><li>One</li></ul>]]></description>
                </item>
        """))

        #expect(releases[0].pubDate
                == Date(timeIntervalSince1970: Double(appcastParsePubDate(value: "Wed, 02 Sep 2026 12:06:28 +0000") ?? -1)))
    }

    /// The notes reaching `AppcastItem` are trimmed by the core, and the CDATA
    /// indentation the feed writes around them is gone.
    @Test func theNotesArriveTrimmed() throws {
        let releases = try AppcastParser.selectReleases(from: feed("""
                <item>
                    <title>2.46.0</title>
                    <pubDate>Wed, 02 Sep 2026 12:06:28 +0000</pubDate>
                    <description>
                        <![CDATA[
                        <ul><li>One</li></ul>
                        ]]>
                    </description>
                </item>
        """))

        #expect(releases[0].releaseNotes == "<ul><li>One</li></ul>")
        #expect(releases[0].bulletPoints.map { String($0.characters) } == ["One"])
    }

    /// Malformed XML is still a thrown `AppcastError.parseError`, not an empty list.
    @Test func malformedXMLStillThrows() {
        #expect(throws: AppcastError.self) {
            try AppcastParser.selectReleases(from: Data("<rss><channel><item>".utf8))
        }
    }
}
