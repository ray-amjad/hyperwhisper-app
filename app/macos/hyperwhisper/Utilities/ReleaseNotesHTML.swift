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
        // survives as ordinary text.
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

        func currentLink() -> URL? {
            for entry in linkStack.reversed() where entry != nil { return entry }
            return nil
        }

        func flush() {
            guard !current.isEmpty else { return }
            result.append(Run(text: current, style: currentStyle(), link: currentLink()))
            current = ""
        }

        /// Close the current run at a `<b>`/`<i>`/`<a>` boundary. A space
        /// waiting to be written belongs to the text before the tag, so
        /// "bold <i>x</i>" keeps its space outside the italic run.
        func flushAtTagBoundary() {
            if pendingSpace && !current.isEmpty {
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
            var name = raw.trimmingCharacters(in: .whitespaces).lowercased()
            let isClosing = name.hasPrefix("/")
            if isClosing { name.removeFirst() }

            // Keep the element name only: "br/" -> "br", "a href=…" -> "a".
            name = String(name.prefix { $0 != "/" && $0 != " " && $0 != "\n" && $0 != "\t" })

            switch name {
            case "b", "strong":
                flushAtTagBoundary()
                boldDepth = isClosing ? max(0, boldDepth - 1) : boldDepth + 1
            case "i", "em":
                flushAtTagBoundary()
                italicDepth = isClosing ? max(0, italicDepth - 1) : italicDepth + 1
            case "a":
                flushAtTagBoundary()
                if isClosing {
                    if !linkStack.isEmpty { linkStack.removeLast() }
                } else {
                    linkStack.append(linkURL(inTag: raw))
                }
            case "br":
                appendLineBreak()
            default:
                break
            }
        }

        var index = html.startIndex
        while index < html.endIndex {
            let character = html[index]

            if character == "<" {
                guard let close = html[index...].firstIndex(of: ">") else {
                    // Unterminated tag: the rest is text, not markup.
                    append(String(html[index...]))
                    break
                }

                handleTag(String(html[html.index(after: index)..<close]))
                index = html.index(after: close)
                continue
            }

            if character == "&",
               let semicolon = html[index...].prefix(entityScanLimit).firstIndex(of: ";"),
               let decoded = decodeEntity(String(html[html.index(after: index)..<semicolon])) {
                append(decoded)
                index = html.index(after: semicolon)
                continue
            }

            append(String(character))
            index = html.index(after: index)
        }

        flush()
        return result
    }

    // MARK: - Links

    /// Schemes we are willing to hand to `openURL`. Anything else — most of
    /// all `javascript:` and `data:` — keeps its label and loses its link.
    private static let allowedSchemes: Set<String> = ["http", "https", "mailto"]

    /// Destination of an `<a …>` tag, or nil when it has no usable href.
    /// `raw` is the tag's contents, without the angle brackets.
    private static func linkURL(inTag raw: String) -> URL? {
        guard let href = attributeValue("href", inTag: raw) else { return nil }

        // Feeds escape query separators, so "?a=1&amp;b=2" has to be decoded
        // before it is a URL.
        let decoded = plainText(href).trimmingCharacters(in: .whitespacesAndNewlines)

        guard let url = URL(string: decoded),
              let scheme = url.scheme?.lowercased(),
              allowedSchemes.contains(scheme) else { return nil }

        return url
    }

    /// Value of an attribute inside a tag, quoted or bare.
    /// Case-insensitive on the name, and the value keeps its own case.
    private static func attributeValue(_ name: String, inTag raw: String) -> String? {
        let characters = Array(raw)
        let target = Array(name)
        var index = 0

        while index < characters.count {
            // Attribute names start at a whitespace boundary — this keeps
            // "data-href" from being read as "href".
            guard index == 0 || characters[index - 1].isWhitespace,
                  matchesName(target, in: characters, at: index) else {
                index += 1
                continue
            }

            var cursor = index + target.count
            while cursor < characters.count, characters[cursor].isWhitespace { cursor += 1 }

            guard cursor < characters.count, characters[cursor] == "=" else {
                index += 1
                continue
            }

            cursor += 1
            while cursor < characters.count, characters[cursor].isWhitespace { cursor += 1 }
            guard cursor < characters.count else { return nil }

            let quote = characters[cursor]
            if quote == "\"" || quote == "'" {
                cursor += 1
                var end = cursor
                while end < characters.count, characters[end] != quote { end += 1 }
                return String(characters[cursor..<end])
            }

            var end = cursor
            while end < characters.count, !characters[end].isWhitespace { end += 1 }
            return String(characters[cursor..<end])
        }

        return nil
    }

    /// Case-insensitive match of an attribute name at a position.
    private static func matchesName(_ target: [Character], in characters: [Character], at index: Int) -> Bool {
        guard index + target.count <= characters.count else { return false }

        for offset in target.indices
        where characters[index + offset].lowercased() != String(target[offset]) {
            return false
        }

        return true
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
