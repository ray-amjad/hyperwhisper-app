//
//  ProductionSource.swift
//  hyperwhisperTests
//
//  Reading the app's own Swift source back off disk, in one place.
//
//  A handful of Local API properties cannot be asserted by calling anything:
//  which of two shared caps a `private static` function passes to another, that
//  no file outside `LocalAPIBodyLimit.swift` reads a request body, that a typed
//  `catch` arm precedes the catch-all it would otherwise be shadowed by. Those
//  are wiring, and the functions that hold them either take FlyingFox types this
//  target links nothing for, or take values only a live pipeline can mint.
//
//  Scraping Swift text is much weaker than calling a function: it proves a
//  symbol is *mentioned*, never that it is used correctly. So it is the last
//  resort, not the first — anything that can be lifted into a callable pure
//  function should be, and tested by calling it. `LocalAPIBodyLimit.drain`,
//  `LocalAPIBodyLimit.reservation(declared:received:)` and
//  `TranscribeEndpoint.uploadCapRefusal(forAudioBytes:)` all exist because of
//  that rule.
//
//  What lives here is the machinery, not the assertions: the `#filePath` walk to
//  the repo root, reading a file, stripping comments, and slicing between two
//  anchors. `LocalAPIBodyLimitTests` and `LocalAPIFilePathCapTests` each had
//  their own copy of all four, which meant two independent guesses at how deep
//  this directory sits in the repo. One copy, one guess.
//

import Foundation

enum ProductionSource {

    enum Failure: Error, CustomStringConvertible {
        case unreadable(String)
        case anchorNotFound(anchor: String, file: String)

        var description: String {
            switch self {
            case .unreadable(let path):
                return "Could not read production source at \(path)"
            case .anchorNotFound(let anchor, let file):
                return """
                Could not locate '\(anchor)' in \(file). It was probably renamed or moved — update \
                the anchor in the test that reads it rather than deleting the check.
                """
            }
        }
    }

    /// The repo root, derived from this file's own compile-time path.
    ///
    /// The one assumption in this file, and the reason it is a file: every test
    /// that reads production source gets the same answer, and a directory move
    /// is a one-line fix rather than a hunt.
    static var repoRoot: URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()  // hyperwhisperTests
            .deletingLastPathComponent()  // macos
            .deletingLastPathComponent()  // app
            .deletingLastPathComponent()  // <repo root>
    }

    /// A repo-relative path, resolved against `repoRoot`.
    static func url(_ repoRelativePath: String) -> URL {
        repoRoot.appendingPathComponent(repoRelativePath)
    }

    /// The text of a file on disk.
    static func text(of url: URL) throws -> String {
        guard let data = try? Data(contentsOf: url) else {
            throw Failure.unreadable(url.path)
        }
        return String(decoding: data, as: UTF8.self)
    }

    /// Every `.swift` file under `directory`, sorted, so a walk is reproducible.
    static func swiftFiles(under directory: URL) throws -> [URL] {
        guard let walker = FileManager.default.enumerator(
            at: directory,
            includingPropertiesForKeys: nil
        ) else {
            throw Failure.unreadable(directory.path)
        }
        return walker
            .compactMap { $0 as? URL }
            .filter { $0.pathExtension == "swift" }
            .sorted { $0.path < $1.path }
    }

    /// A production file with every comment line removed.
    ///
    /// Comments go first so a doc comment that *describes* the wiring cannot
    /// stand in for the wiring — the whole point is to read the code. It also
    /// keeps prose about a banned idiom from reading as a use of it.
    static func code(of repoRelativePath: String) throws -> String {
        try text(of: url(repoRelativePath))
            .components(separatedBy: .newlines)
            .filter { !$0.trimmingCharacters(in: .whitespaces).hasPrefix("//") }
            .joined(separator: "\n")
    }

    /// The comment-free source of `repoRelativePath` between two anchors.
    ///
    /// - Parameters:
    ///   - opening: text that starts the region. The slice begins after it.
    ///   - closing: text that ends the region. The slice stops before it.
    static func slice(
        of repoRelativePath: String,
        from opening: String,
        to closing: String
    ) throws -> String {
        let file = URL(fileURLWithPath: repoRelativePath).lastPathComponent
        let source = try code(of: repoRelativePath)
        guard let start = source.range(of: opening) else {
            throw Failure.anchorNotFound(anchor: opening, file: file)
        }
        let rest = source[start.upperBound...]
        guard let end = rest.range(of: closing) else {
            throw Failure.anchorNotFound(anchor: closing, file: file)
        }
        return String(rest[..<end.lowerBound])
    }

    /// One `switch` arm out of an already-extracted `body`, from its `case`
    /// label to the next `case` label or the end.
    static func switchArm(named label: String, in body: String, of file: String) throws -> String {
        guard let start = body.range(of: label) else {
            throw Failure.anchorNotFound(anchor: label, file: file)
        }
        let rest = body[start.upperBound...]
        guard let end = rest.range(of: "case .") else {
            return String(rest)
        }
        return String(rest[..<end.lowerBound])
    }
}
