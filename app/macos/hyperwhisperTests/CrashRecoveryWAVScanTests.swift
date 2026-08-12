//
//  CrashRecoveryWAVScanTests.swift
//  hyperwhisperTests
//

import Foundation
import Testing
@testable import HyperWhisper

/// Regression guard for the orphan-WAV sweep's directory scan (HYPERWHISPER-VM).
///
/// The scan moved off the main actor onto a detached task; it must keep matching
/// exactly the same files it did before — in particular the dot-prefixed
/// `.incomplete_*.wav` ones, which `.skipsHiddenFiles` would silently drop.
struct CrashRecoveryWAVScanTests {

    private func makeTemporaryDirectory() throws -> URL {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("CrashRecoveryWAVScanTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }

    @Test func findsOnlyDotPrefixedIncompleteWAVs() async throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let sessionId = UUID().uuidString
        let incompleteName = ".incomplete_\(sessionId).wav"
        let incompleteWAV = directory.appendingPathComponent(incompleteName)

        // The one file the sweep must find, plus three it must ignore.
        try Data([0x00]).write(to: incompleteWAV)
        try Data([0x00]).write(to: directory.appendingPathComponent("finished.wav"))
        try Data([0x00]).write(to: directory.appendingPathComponent(".incomplete_\(UUID().uuidString).caf"))
        try Data([0x00]).write(to: directory.appendingPathComponent("notes.txt"))

        let candidates = await CrashRecoveryManager.scanForUnclaimedWAVCandidates(in: directory)

        #expect(candidates.count == 1)
        let candidate = try #require(candidates.first)
        #expect(candidate.url.lastPathComponent == incompleteName)
        #expect(FileManager.default.fileExists(atPath: candidate.url.path))
        // Creation date drives the staleness guard, so it must survive the hop.
        #expect(candidate.creationDate != nil)
    }

    @Test func returnsEmptyForDirectoryWithNoIncompleteWAVs() async throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        try Data([0x00]).write(to: directory.appendingPathComponent("finished.wav"))

        let candidates = await CrashRecoveryManager.scanForUnclaimedWAVCandidates(in: directory)

        #expect(candidates.isEmpty)
    }

    @Test func returnsEmptyForMissingDirectory() async {
        let missing = FileManager.default.temporaryDirectory
            .appendingPathComponent("CrashRecoveryWAVScanTests-missing-\(UUID().uuidString)", isDirectory: true)

        let candidates = await CrashRecoveryManager.scanForUnclaimedWAVCandidates(in: missing)

        #expect(candidates.isEmpty)
    }
}
