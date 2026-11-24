//
//  ProductionSource+Localizations.swift
//  hyperwhisperTests
//
//  The per-locale `Localizable.strings` key walk, in one place.
//
//  Every localized-string test opens each `.lproj`, reads its
//  `Localizable.strings` and finds the one line for a key. That walk was copied
//  into nine test files and one copy drifted to a weaker check (#1994). Here it
//  lives once: the locale list, the lines of one locale's file, and the
//  "exactly one, terminated line for this key" check. Each test keeps only its
//  own assertions about the line's content.
//

import Foundation
import Testing

extension ProductionSource {

    /// The repo-relative folder holding the macOS app's `.lproj` directories.
    static let localizationsPath = "app/macos/hyperwhisper/Localizations"

    /// Every `.lproj` directory under `app/macos/hyperwhisper/Localizations`,
    /// sorted by name, so a walk is reproducible.
    static func localeDirectories() throws -> [URL] {
        try FileManager.default.contentsOfDirectory(
            at: url(localizationsPath),
            includingPropertiesForKeys: nil
        )
        .filter { $0.pathExtension == "lproj" }
        .sorted { $0.lastPathComponent < $1.lastPathComponent }
    }

    /// The lines of `locale`'s `Localizable.strings`.
    static func localizableLines(of locale: URL) throws -> [String] {
        try text(of: locale.appendingPathComponent("Localizable.strings"))
            .components(separatedBy: .newlines)
    }

    /// The one line that defines `key` in `lines`, or `nil` when there is none.
    ///
    /// Records a failure, at the caller's line, unless exactly one line starts
    /// `"key" = "` and that line ends `";`.
    static func onlyLine(
        forKey key: String,
        in lines: [String],
        locale: URL,
        sourceLocation: SourceLocation = #_sourceLocation
    ) -> String? {
        let matches = lines.filter { $0.hasPrefix("\"\(key)\" = \"") }
        #expect(
            matches.count == 1,
            "\(locale.lastPathComponent) needs exactly one \(key)",
            sourceLocation: sourceLocation
        )
        guard let line = matches.first else { return nil }
        #expect(
            line.trimmingCharacters(in: .whitespaces).hasSuffix("\";"),
            "\(locale.lastPathComponent) \(key)",
            sourceLocation: sourceLocation
        )
        return line
    }
}
