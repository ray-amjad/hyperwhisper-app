//
//  ReleaseNotesHTML.swift
//  hyperwhisper
//
//  RELEASE NOTES HTML
//  Converts the small slice of HTML the appcast feeds use for release notes
//  into text SwiftUI can render.
//
//  Supported: <b>/<strong>, <i>/<em>, <a href>, <br>, and character entities.
//  Everything else is dropped, keeping its text content — so a feed that grows
//  a <span> degrades to plain text instead of leaking markup into the UI,
//  which is what used to happen with <b> in the Recent Updates cards.
//
//  Only http, https and mailto links are carried through. A javascript: or
//  data: href keeps its label and loses the link, so a compromised feed cannot
//  turn a release note into something the user can click into running code.
//
//  The PARSER is not here. Issue #284 moved it into `hw-releasenotes` and this
//  is now a facade over `releaseNotesParseInline` / `releaseNotesPlainText`: the
//  tokenizer, the entity decoder and the scheme allowlist existed in both Swift
//  and C#, and every fix to one of them had to be made twice.
//  `ReleaseNotesHTMLTests` still pins the answer this file returns.
//

import Foundation
import SwiftUI

enum ReleaseNotesHTML {

    // MARK: - Types

    /// Inline emphasis carried by a stretch of text.
    struct Style: OptionSet {
        let rawValue: Int

        static let bold = Style(rawValue: 1 << 0)
        static let italic = Style(rawValue: 1 << 1)
    }

    /// A stretch of text that shares one style and, if it sits inside an
    /// `<a href>`, one destination.
    struct Run: Equatable {
        let text: String
        let style: Style
        var link: URL?
    }

    // MARK: - Public API

    /// Styled text for a release-notes fragment.
    ///
    /// Emphasis is expressed as presentation intent rather than a concrete
    /// font, so the caller's `.font(...)` still decides size and base weight.
    /// Links are the exception: colour and underline are set outright, because
    /// a link that is not visibly a link is one nobody clicks.
    static func attributed(_ html: String) -> AttributedString {
        var result = AttributedString()

        for run in runs(in: html) {
            var piece = AttributedString(run.text)
            var intent: InlinePresentationIntent = []

            if run.style.contains(.bold) { intent.insert(.stronglyEmphasized) }
            if run.style.contains(.italic) { intent.insert(.emphasized) }
            if !intent.isEmpty { piece.inlinePresentationIntent = intent }

            if let link = run.link {
                piece.link = link
                piece[AttributeScopes.SwiftUIAttributes.ForegroundColorAttribute.self] = .accentColor
                piece[AttributeScopes.SwiftUIAttributes.UnderlineStyleAttribute.self] = .single
            }

            result.append(piece)
        }

        return result
    }

    /// Tag-free, entity-decoded text — for titles, logging and tests.
    static func plainText(_ html: String) -> String {
        releaseNotesPlainText(html: html, collapseWhitespace: true)
    }

    /// Split a fragment into styled runs, collapsing HTML whitespace.
    ///
    /// `link` arrives as the feed's href verbatim — already entity-decoded,
    /// trimmed and checked against the scheme allowlist by the core. It is
    /// handed to `URL(string:)` untouched: decoding or trimming it a second
    /// time would open a different address than the feed asked for, and the
    /// allowlist decision is not re-made here. `URL(string:)` still runs,
    /// because a string the core allows but `URL` cannot parse is no link at
    /// all — which is what it was before this moved to Rust.
    static func runs(in html: String) -> [Run] {
        releaseNotesParseInline(html: html, collapseWhitespace: true).map { run in
            var style: Style = []
            if run.bold { style.insert(.bold) }
            if run.italic { style.insert(.italic) }

            return Run(text: run.text, style: style, link: run.link.flatMap(URL.init(string:)))
        }
    }
}
