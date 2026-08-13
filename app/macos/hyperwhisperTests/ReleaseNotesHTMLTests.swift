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
            #expect(ReleaseNotesHTML.plainText(html).contains("x"))
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
