//
//  TranscribeAudioSourceTests.swift
//  hyperwhisperTests
//
//  The `file` allow-list of `POST /transcribe`, called directly (issue #733).
//
//  `TranscribeAudioSource.resolvedURLWithinAllowedRoots` is what stops a Local
//  API token holder reading a file outside the recordings folder (issue #713).
//  It resolves symlinks before the containment check, so a link that sits
//  inside the recordings folder but points outside it must be refused. These
//  tests build that layout on disk and ask the real function.
//
//  The allowed root is read from the `recordingsFolder` default, so each test
//  points it at a fresh temp folder and puts the old value back. That default
//  is shared, so these tests join the `.serialized` #1473 suite that already
//  writes it, as `RecordingsFolderFallbackFailureTests` does.
//

import Foundation
import Testing

@testable import HyperWhisper

/// A recordings folder and a sibling folder outside it, both in a fresh temp
/// folder, with `recordingsFolder` pointed at the first. Removed, and the
/// default put back, when `body` returns.
@MainActor
private func withRecordingsRoot(_ body: (_ recordings: URL, _ outside: URL) throws -> Void) throws {
    let base = FileManager.default.temporaryDirectory
        .appendingPathComponent("hyperwhisper-733-\(UUID().uuidString)", isDirectory: true)
    let recordings = base.appendingPathComponent("recordings", isDirectory: true)
    let outside = base.appendingPathComponent("outside", isDirectory: true)
    try FileManager.default.createDirectory(at: recordings, withIntermediateDirectories: true)
    try FileManager.default.createDirectory(at: outside, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: base) }

    let previous = UserDefaults.standard.object(forKey: "recordingsFolder")
    UserDefaults.standard.set(recordings.path, forKey: "recordingsFolder")
    defer {
        if let previous {
            UserDefaults.standard.set(previous, forKey: "recordingsFolder")
        } else {
            UserDefaults.standard.removeObject(forKey: "recordingsFolder")
        }
    }

    try body(recordings, outside)
}

extension RecordingsFolderRefusalTests {

    /// Positive control: a real file inside the recordings folder is accepted,
    /// and the root it is accepted under is that folder. Without this, the
    /// refusals below would also pass if the allow-list refused everything.
    @Test func aFileInsideTheRecordingsFolderIsAllowListed() throws {
        try withRecordingsRoot { recordings, _ in
            let file = recordings.appendingPathComponent("take.wav")
            #expect(FileManager.default.createFile(atPath: file.path, contents: Data([1, 2, 3])))

            let allowed = try #require(try TranscribeAudioSource.resolvedURLWithinAllowedRoots(file))
            #expect(allowed.resolvedURL.path == file.resolvingSymlinksInPath().standardizedFileURL.path)
            #expect(allowed.rootURL.path == recordings.resolvingSymlinksInPath().standardizedFileURL.path)
        }
    }

    /// A symlink inside the recordings folder that points at a file outside it
    /// is refused: the check runs on where the link leads, not where it sits.
    @Test func aSymlinkOutOfTheRecordingsFolderIsRefused() throws {
        try withRecordingsRoot { recordings, outside in
            let secret = outside.appendingPathComponent("secret.wav")
            #expect(FileManager.default.createFile(atPath: secret.path, contents: Data([4, 5, 6])))
            let link = recordings.appendingPathComponent("link.wav")
            try FileManager.default.createSymbolicLink(at: link, withDestinationURL: secret)

            #expect(try TranscribeAudioSource.resolvedURLWithinAllowedRoots(link) == nil,
                    "a link in the recordings folder must not let a caller read the file it points at")
        }
    }

    /// The same escape through a symlinked FOLDER inside the recordings folder.
    @Test func aSymlinkedFolderOutOfTheRecordingsFolderIsRefused() throws {
        try withRecordingsRoot { recordings, outside in
            let secret = outside.appendingPathComponent("secret.wav")
            #expect(FileManager.default.createFile(atPath: secret.path, contents: Data([7, 8, 9])))
            let linkedFolder = recordings.appendingPathComponent("linked", isDirectory: true)
            try FileManager.default.createSymbolicLink(at: linkedFolder, withDestinationURL: outside)

            let throughLink = linkedFolder.appendingPathComponent("secret.wav")
            #expect(try TranscribeAudioSource.resolvedURLWithinAllowedRoots(throughLink) == nil,
                    "a linked folder in the recordings folder must not reach files outside it")
        }
    }
}
