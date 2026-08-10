//
//  ReleaseNotesHTMLTests.swift
//  hyperwhisperTests
//

import Foundation
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

        #expect(item.releaseTitle == "Enhanced Audio Recording")
        #expect(item.bulletPoints.map { String($0.characters) } == ["Improved stability"])
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
