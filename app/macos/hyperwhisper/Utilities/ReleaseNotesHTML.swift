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
        runs(in: html).map(\.text).joined()
    }

    /// Split a fragment into styled runs, collapsing HTML whitespace.
    static func runs(in html: String) -> [Run] {
        var result: [Run] = []
        var current = ""
        var boldDepth = 0
        var italicDepth = 0

        // One entry per open <a>, nil when its href was missing or unusable —
        // so the matching </a> still pops the right thing and the label
        // survives as ordinary text. The innermost entry wins: a nested <a>
        // with a rejected href must not inherit the outer destination.
        var linkStack: [URL?] = []

        // HTML collapses whitespace, and feed entries are indented across
        // several lines, so a space is only emitted once real text follows it.
        var pendingSpace = false
        var producedText = false

        func currentStyle() -> Style {
            var style: Style = []
            if boldDepth > 0 { style.insert(.bold) }
            if italicDepth > 0 { style.insert(.italic) }
            return style
        }

        func flush() {
            guard !current.isEmpty else { return }
            result.append(Run(text: current, style: currentStyle(), link: linkStack.last ?? nil))
            current = ""
        }

        /// Close the current run at a `<b>`/`<i>`/`<a>` boundary. A space
        /// waiting to be written belongs *outside* the element: with the text
        /// before an opening tag, and with the text after a closing one. So
        /// "<b>See</b> <a>here</a>" does not underline and tint the space in
        /// front of the link, and "<a><b>the page</b> </a>now" does not leave a
        /// linked space behind either. Appending to an empty buffer makes that
        /// space its own run, carrying the style and destination in force where
        /// it is emitted.
        func flushAtTagBoundary(isClosing: Bool) {
            if pendingSpace, !isClosing {
                current.append(" ")
                pendingSpace = false
            }

            flush()
        }

        func append(_ text: String) {
            for character in text {
                if character == " " || character == "\n" || character == "\r" || character == "\t" {
                    pendingSpace = producedText
                    continue
                }

                if pendingSpace {
                    current.append(" ")
                    pendingSpace = false
                }

                current.append(character)
                producedText = true
            }
        }

        func appendLineBreak() {
            current.append("\n")
            pendingSpace = false
            producedText = false
        }

        func handleTag(_ raw: String) {
            let tag = parseTag(raw)

            if tag.name == "br" {
                appendLineBreak()
                return
            }

            // A tag that closes itself opens and closes in one go, so it changes
            // no state at all. Acting on it would push a depth or a link entry
            // nothing ever pops, and the rest of the note would render bold,
            // italic or linked: "<a …/>", "<b/>", "<i/>".
            if tag.isSelfClosing { return }

            switch tag.name {
            case "b", "strong":
                flushAtTagBoundary(isClosing: tag.isClosing)
                boldDepth = tag.isClosing ? max(0, boldDepth - 1) : boldDepth + 1
            case "i", "em":
                flushAtTagBoundary(isClosing: tag.isClosing)
                italicDepth = tag.isClosing ? max(0, italicDepth - 1) : italicDepth + 1
            case "a":
                flushAtTagBoundary(isClosing: tag.isClosing)
                if tag.isClosing {
                    if !linkStack.isEmpty { linkStack.removeLast() }
                } else {
                    linkStack.append(linkURL(fromHref: tag.href))
                }
            default:
                break
            }
        }

        var index = html.startIndex
        while index < html.endIndex {
            let character = html[index]

            if character == "<" {
                guard let close = tagEnd(in: html, from: index) else {
                    // Unterminated tag: the rest is text, not markup.
                    append(String(html[index...]))
                    break
                }

                handleTag(String(html[html.index(after: index)..<close]))
                index = html.index(after: close)
                continue
            }

            if character == "&", let entity = decodeEntity(in: html, at: index) {
                append(entity.text)
                index = entity.end
                continue
            }

            append(String(character))
            index = html.index(after: index)
        }

        flush()
        return result
    }

    // MARK: - Tags

    /// Index of the ">" that ends the tag opened at `start`, ignoring any ">"
    /// inside a quoted attribute value — a URL may carry one in its query.
    /// Returns nil when the tag is never closed. A quote that is never closed
    /// falls back to the first ">", so one malformed attribute cannot swallow
    /// the rest of the fragment as markup.
    ///
    /// A quote only opens a value where `parseTag` would read one: straight
    /// after an "=". Anywhere else it is an ordinary character, so the
    /// apostrophe in a bare "href=it's" cannot pair up with a later one and run
    /// the scan past the ">" that really ends the tag.
    private static func tagEnd(in html: String, from start: String.Index) -> String.Index? {
        var index = html.index(after: start)
        var inValuePosition = false

        while index < html.endIndex {
            let character = html[index]

            if inValuePosition, character == "\"" || character == "'" {
                guard let quotedEnd = html[html.index(after: index)...].firstIndex(of: character) else { break }
                index = html.index(after: quotedEnd)
                inValuePosition = false
                continue
            }

            if character == ">" { return index }

            // Whitespace between "=" and the value is allowed, so it leaves the
            // position alone rather than ending it.
            if !character.isWhitespace { inValuePosition = character == "=" }
            index = html.index(after: index)
        }

        return html[start...].firstIndex(of: ">")
    }

    // MARK: - Links

    /// Schemes we are willing to hand to `openURL`. Anything else — most of
    /// all `javascript:` and `data:` — keeps its label and loses its link.
    private static let allowedSchemes: Set<String> = ["http", "https", "mailto"]

    /// Destination for an `<a>`'s href, or nil when it is missing or is not a
    /// scheme we are willing to open.
    private static func linkURL(fromHref href: String?) -> URL? {
        guard let href else { return nil }

        // Entities only: feeds escape query separators, so "?a=1&amp;b=2" has to
        // be decoded before it is a URL. Nothing else about the href may change —
        // running it through the whole parser stripped markup and collapsed
        // whitespace inside it, quietly opening a different address than the
        // feed asked for.
        let decoded = decodeEntities(href).trimmingCharacters(in: .whitespacesAndNewlines)

        guard let url = URL(string: decoded),
              let scheme = url.scheme?.lowercased(),
              allowedSchemes.contains(scheme) else { return nil }

        return url
    }

    // MARK: - Tokenizer

    /// One tag, tokenized: the element name (lowercased), whether it closes an
    /// element, whether it closes itself, and its href if it has one.
    private struct Tag {
        let name: String
        let isClosing: Bool
        let isSelfClosing: Bool
        let href: String?
    }

    /// Walk a tag's body once — a leading "/", the element name, then attribute
    /// by attribute — and report everything the caller needs to know about it.
    /// The first href wins; its value keeps its own case.
    ///
    /// The tag is walked rather than searched, so a value that happens to
    /// contain "href=" — a title, say — can never be mistaken for the attribute
    /// itself, and a bare value that ends in "/" — most URLs — is not mistaken
    /// for a self-closing tag. Only a "/" standing where a name may start, with
    /// nothing but whitespace after it, closes the tag. An unterminated quote
    /// gives up on the rest of the tag instead of inventing a value out of it.
    private static func parseTag(_ raw: String) -> Tag {
        let characters = Array(raw.trimmingCharacters(in: .whitespacesAndNewlines))
        var index = 0

        let isClosing = characters.first == "/"
        if isClosing { index += 1 }

        var name = ""
        var haveName = false
        var isSelfClosing = false
        var href: String?

        while index < characters.count {
            while index < characters.count, characters[index].isWhitespace { index += 1 }
            guard index < characters.count else { break }

            // A "/" where a name could start is the tag closing itself — "<a/>",
            // "<br />", "<a href=… />" — but only if nothing but whitespace
            // follows it, so the next token read clears this again. A "/" inside
            // a bare value, on the other hand, is simply part of the URL.
            if characters[index] == "/" {
                isSelfClosing = true
                index += 1
                continue
            }

            isSelfClosing = false

            let tokenStart = index
            while index < characters.count, !characters[index].isWhitespace,
                  characters[index] != "=", characters[index] != "/" {
                index += 1
            }
            let tokenEnd = index

            while index < characters.count, characters[index].isWhitespace { index += 1 }

            var value: String?
            if index < characters.count, characters[index] == "=" {
                index += 1
                while index < characters.count, characters[index].isWhitespace { index += 1 }
                guard index < characters.count else { break }   // "href=" with nothing after it

                let quote = characters[index]
                if quote == "\"" || quote == "'" {
                    guard let quotedEnd = characters[(index + 1)...].firstIndex(of: quote) else {
                        break   // unterminated: nothing here is trustworthy
                    }
                    value = String(characters[(index + 1)..<quotedEnd])
                    index = quotedEnd + 1
                } else {
                    let valueStart = index
                    while index < characters.count, !characters[index].isWhitespace { index += 1 }
                    value = String(characters[valueStart..<index])
                }
            }

            // The first token is the element name; every token after it is an
            // attribute, valued or not.
            if !haveName {
                name = String(characters[tokenStart..<tokenEnd]).lowercased()
                haveName = true
            } else if href == nil,
                      String(characters[tokenStart..<tokenEnd]).caseInsensitiveCompare("href") == .orderedSame {
                href = value
            }
        }

        return Tag(name: name, isClosing: isClosing, isSelfClosing: isSelfClosing, href: href)
    }

    // MARK: - Entities

    /// Longest entity we will look ahead for, including "&" and ";".
    private static let entityScanLimit = 12

    private static let namedEntities: [String: String] = [
        "amp": "&",
        "lt": "<",
        "gt": ">",
        "quot": "\"",
        "apos": "'",
        "nbsp": "\u{00A0}",
        "mdash": "—",
        "ndash": "–",
        "hellip": "…"
    ]

    /// Decode every character entity in a fragment, leaving all other text —
    /// markup included — exactly as it was.
    private static func decodeEntities(_ text: String) -> String {
        guard text.contains("&") else { return text }

        var result = ""
        var index = text.startIndex

        while index < text.endIndex {
            if text[index] == "&", let entity = decodeEntity(in: text, at: index) {
                result.append(entity.text)
                index = entity.end
                continue
            }

            result.append(text[index])
            index = text.index(after: index)
        }

        return result
    }

    /// Decode the entity starting at `start` (an "&"), and report where it ends.
    /// Returns nil when there is no complete, recognised entity there, in which
    /// case the "&" stays literal text.
    private static func decodeEntity(in text: String, at start: String.Index) -> (text: String, end: String.Index)? {
        guard let semicolon = text[start...].prefix(entityScanLimit).firstIndex(of: ";"),
              let decoded = decodeEntity(String(text[text.index(after: start)..<semicolon]))
        else { return nil }

        return (decoded, text.index(after: semicolon))
    }

    /// Decode the body of an entity ("amp", "#8212", "#x2014").
    /// Returns nil for anything unrecognised, so it stays literal text.
    private static func decodeEntity(_ body: String) -> String? {
        if let named = namedEntities[body.lowercased()] { return named }

        guard body.hasPrefix("#") else { return nil }
        let digits = body.dropFirst()

        let value: UInt32?
        if digits.hasPrefix("x") || digits.hasPrefix("X") {
            value = UInt32(digits.dropFirst(), radix: 16)
        } else {
            value = UInt32(digits, radix: 10)
        }

        guard let value, let scalar = Unicode.Scalar(value) else { return nil }
        return String(Character(scalar))
    }
}
