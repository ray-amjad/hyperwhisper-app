//
//  ReleaseNotesHTML.swift
//  hyperwhisper
//
//  RELEASE NOTES HTML
//  Converts the small slice of HTML the appcast feeds use for release notes
//  into text SwiftUI can render.
//
//  Supported: <b>/<strong>, <i>/<em>, <br>, and character entities.
//  Everything else is dropped, keeping its text content — so a feed that grows
//  a <span> or an <a> degrades to plain text instead of leaking markup into
//  the UI, which is what used to happen with <b> in the Recent Updates cards.

import Foundation

enum ReleaseNotesHTML {

    // MARK: - Types

    /// Inline emphasis carried by a stretch of text.
    struct Style: OptionSet {
        let rawValue: Int

        static let bold = Style(rawValue: 1 << 0)
        static let italic = Style(rawValue: 1 << 1)
    }

    /// A stretch of text that shares one style.
    struct Run: Equatable {
        let text: String
        let style: Style
    }

    // MARK: - Public API

    /// Styled text for a release-notes fragment.
    ///
    /// Emphasis is expressed as presentation intent rather than a concrete
    /// font, so the caller's `.font(...)` still decides size and base weight.
    static func attributed(_ html: String) -> AttributedString {
        var result = AttributedString()

        for run in runs(in: html) {
            var piece = AttributedString(run.text)
            var intent: InlinePresentationIntent = []

            if run.style.contains(.bold) { intent.insert(.stronglyEmphasized) }
            if run.style.contains(.italic) { intent.insert(.emphasized) }
            if !intent.isEmpty { piece.inlinePresentationIntent = intent }

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
            result.append(Run(text: current, style: currentStyle()))
            current = ""
        }

        /// Close the current run at a `<b>`/`<i>` boundary. A space waiting to
        /// be written belongs to the text before the tag, so "bold <i>x</i>"
        /// keeps its space outside the italic run.
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
