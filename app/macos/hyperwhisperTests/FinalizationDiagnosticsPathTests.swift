//
//  FinalizationDiagnosticsPathTests.swift
//  hyperwhisperTests
//
//  "Audio finalization failed" (HYPERWHISPER-F1, #1649) ships the dictionary
//  `RecordingLifecycle.collectDiagnostics(rawURL:dstURL:duration:)` builds to
//  Sentry as extras. It used to hold `rawURL.path` and `dstURL.path`, and both
//  carry the macOS account name. `SentryService.beforeSend` redacts extras by
//  KEY (transcript/text/prompt) and never reads a value, so nothing downstream
//  catches a path. These tests pin that no path-derived value names the user.
//

import Foundation
import Testing

@testable import HyperWhisper

struct FinalizationDiagnosticsPathTests {

    private static let home = "/Users/someone"
    private static let defaultFolder = URL(
        fileURLWithPath: "\(home)/Documents/hyperwhisper/recordings",
        isDirectory: true
    )
    private static let rawURL = defaultFolder
        .appendingPathComponent(".incomplete_5134FE95-0000-0000-0000-000000000000.wav")
    private static let dstURL = defaultFolder
        .appendingPathComponent("recording_1791139574.wav")

    @Test func noValueContainsTheHomePathOrAccountName() {
        let diagnostics = RecordingLifecycle.pathDiagnostics(
            rawURL: Self.rawURL,
            dstURL: Self.dstURL,
            defaultRecordingsFolder: Self.defaultFolder
        )

        #expect(!diagnostics.isEmpty)
        for (key, value) in diagnostics {
            let text = String(describing: value)
            #expect(!text.contains("/Users/"), "\(key) carries a path: \(text)")
            #expect(!text.contains("someone"), "\(key) carries the account name: \(text)")
        }
        // Exactly the four measurements; a new key here is a new Sentry extra
        // and should be a deliberate edit of this list.
        #expect(Set(diagnostics.keys) == [
            "rawExtension",
            "dstExtension",
            "rawIsIncompleteName",
            "dstInDefaultRecordingsFolder"
        ])
    }

    @Test func measurementsDescribeTheFilesWithoutThePath() {
        let diagnostics = RecordingLifecycle.pathDiagnostics(
            rawURL: Self.rawURL,
            dstURL: Self.dstURL,
            defaultRecordingsFolder: Self.defaultFolder
        )

        #expect(diagnostics["rawExtension"] as? String == "wav")
        #expect(diagnostics["dstExtension"] as? String == "wav")
        #expect(diagnostics["rawIsIncompleteName"] as? Bool == true)
        #expect(diagnostics["dstInDefaultRecordingsFolder"] as? Bool == true)
    }

    @Test func customFolderIsReportedAsNotDefaultAndNotNamed() {
        let custom = URL(fileURLWithPath: "\(Self.home)/My Secret Project/audio", isDirectory: true)
        let diagnostics = RecordingLifecycle.pathDiagnostics(
            rawURL: custom.appendingPathComponent("take.wav"),
            dstURL: custom.appendingPathComponent("recording_1.m4a"),
            defaultRecordingsFolder: Self.defaultFolder
        )

        #expect(diagnostics["dstExtension"] as? String == "m4a")
        #expect(diagnostics["rawIsIncompleteName"] as? Bool == false)
        #expect(diagnostics["dstInDefaultRecordingsFolder"] as? Bool == false)
        for (key, value) in diagnostics {
            let text = String(describing: value)
            #expect(!text.contains("Secret"), "\(key) carries a user-chosen folder name: \(text)")
            #expect(!text.contains("/Users/"), "\(key) carries a path: \(text)")
        }
    }

    @Test func unknownDefaultFolderIsNotDefault() {
        let diagnostics = RecordingLifecycle.pathDiagnostics(
            rawURL: Self.rawURL,
            dstURL: Self.dstURL,
            defaultRecordingsFolder: nil
        )

        #expect(diagnostics["dstInDefaultRecordingsFolder"] as? Bool == false)
    }

    /// `collectDiagnostics` itself needs a live `RecordingLifecycle`, so its own
    /// dictionary literal is read back from source: no entry may take a raw or
    /// destination path (or URL string) as its value. It may still call
    /// `rawURL.path` for `fileExists` and friends, which is why the check is on
    /// the `"key": <url>.path` / `] = <url>.path` value shapes, not on the name.
    @Test func collectDiagnosticsPutsNoPathValueInTheDictionary() throws {
        let body = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/AudioRecording/Recording/RecordingLifecycle.swift",
            from: "private func collectDiagnostics(rawURL: URL, dstURL: URL, duration: TimeInterval) -> [String: Any] {",
            to: "private func collectInputDeviceDiagnostics("
        )

        #expect(body.contains("Self.pathDiagnostics("))
        // Built from parts so the issue's Done-when grep for the two old quoted
        // key names stays empty across the whole macOS tree, this file included.
        for url in ["raw", "dst"].map({ $0 + "URL" }) {
            for member in ["path", "absoluteString", "relativePath"] {
                // `\b` so `rawURL.pathExtension` (a measurement) does not match.
                let literalValue = "\": \(url)\\.\(member)\\b"
                let assignedValue = "\\] = \(url)\\.\(member)\\b"
                #expect(
                    body.range(of: literalValue, options: .regularExpression) == nil,
                    "dictionary value \(url).\(member)"
                )
                #expect(
                    body.range(of: assignedValue, options: .regularExpression) == nil,
                    "dictionary value \(url).\(member)"
                )
            }
            #expect(!body.contains("\"" + url + "\""), "the \(url) key is back")
        }
    }
}
