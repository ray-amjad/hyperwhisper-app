//
//  AutoDeleteFileDeletionTests.swift
//  hyperwhisperTests
//

import Foundation
import Testing
@testable import HyperWhisper

/// Regression guard for the auto-delete cleanup's file deletion batch
/// (HYPERWHISPER-HF).
///
/// The per-file `deleteFileIfExists` moved off the main actor into one detached
/// batch, `AutoDeleteCleanupService.deleteFiles(at:)`. The batch must keep the
/// old per-file semantics exactly: results index-aligned with the input, paths
/// processed sequentially in order, duplicates NOT collapsed (a repeated path
/// must count once, as it did when the second call found the file already gone),
/// and a failed removal reported without any bytes credited.
struct AutoDeleteFileDeletionTests {

    private func makeTemporaryDirectory() throws -> URL {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("AutoDeleteFileDeletionTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }

    /// Writes a file of exactly `byteCount` bytes and returns its path.
    @discardableResult
    private func makeFile(in directory: URL, byteCount: Int) throws -> String {
        let url = directory.appendingPathComponent("\(UUID().uuidString).wav")
        try Data(repeating: 0x41, count: byteCount).write(to: url)
        return url.path
    }

    @Test func deletesExistingFileAndReportsItsSize() async throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let byteCount = 2048
        let path = try makeFile(in: directory, byteCount: byteCount)

        let results = await AutoDeleteCleanupService.deleteFiles(at: [path])

        #expect(results.count == 1)
        let result = try #require(results.first)
        #expect(result.deleted)
        #expect(result.bytesFreed == Int64(byteCount))
        #expect(result.failureDescription == nil)
        #expect(!FileManager.default.fileExists(atPath: path))
    }

    @Test func missingPathIsNotAFailure() async throws {
        let missing = FileManager.default.temporaryDirectory
            .appendingPathComponent("AutoDeleteFileDeletionTests-missing-\(UUID().uuidString).wav")
            .path

        let results = await AutoDeleteCleanupService.deleteFiles(at: [missing])

        #expect(results.count == 1)
        let result = try #require(results.first)
        #expect(!result.deleted)
        #expect(result.bytesFreed == 0)
        // "Not found" is the normal case for an already-cleaned recording, so it
        // must not be logged as a deletion failure.
        #expect(result.failureDescription == nil)
    }

    /// A transcript whose original and trimmed audio paths are the same string
    /// puts that path in the batch twice. It must be counted once — the second
    /// entry has to see the file already gone, exactly as the sequential
    /// per-file version did. De-duplicating the batch would change the stats.
    @Test func duplicatePathCountsOnlyOnce() async throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let byteCount = 512
        let path = try makeFile(in: directory, byteCount: byteCount)

        let results = await AutoDeleteCleanupService.deleteFiles(at: [path, path])

        #expect(results.count == 2)
        #expect(results[0].deleted)
        #expect(results[0].bytesFreed == Int64(byteCount))
        #expect(results[0].failureDescription == nil)
        #expect(!results[1].deleted)
        #expect(results[1].bytesFreed == 0)
        #expect(results[1].failureDescription == nil)
        #expect(!FileManager.default.fileExists(atPath: path))
    }

    @Test func resultsStayAlignedWithInputOrder() async throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let first = try makeFile(in: directory, byteCount: 100)
        let missing = directory.appendingPathComponent("\(UUID().uuidString).wav").path
        let third = try makeFile(in: directory, byteCount: 300)

        let results = await AutoDeleteCleanupService.deleteFiles(at: [first, missing, third])

        #expect(results.count == 3)
        #expect(results[0].deleted)
        #expect(results[0].bytesFreed == 100)
        #expect(!results[1].deleted)
        #expect(results[1].bytesFreed == 0)
        #expect(results[2].deleted)
        #expect(results[2].bytesFreed == 300)
        #expect(!FileManager.default.fileExists(atPath: first))
        #expect(!FileManager.default.fileExists(atPath: third))
    }

    /// `removeItem` failing must surface a description for the caller to log,
    /// credit no bytes, and leave the target alone.
    @Test func undeletableTargetReportsFailure() async throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        // A non-empty directory inside a read-only parent: `removeItem` cannot
        // unlink it, but `fileExists` still says it is there.
        let parent = directory.appendingPathComponent("locked", isDirectory: true)
        let target = parent.appendingPathComponent("target", isDirectory: true)
        try FileManager.default.createDirectory(at: target, withIntermediateDirectories: true)
        try Data([0x00]).write(to: target.appendingPathComponent("child.wav"))
        try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: parent.path)
        defer {
            try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: parent.path)
        }

        let results = await AutoDeleteCleanupService.deleteFiles(at: [target.path])

        #expect(results.count == 1)
        let result = try #require(results.first)
        #expect(!result.deleted)
        #expect(result.bytesFreed == 0)
        #expect(result.failureDescription != nil)
        #expect(FileManager.default.fileExists(atPath: target.path))
    }

    @Test func emptyInputProducesEmptyOutput() async {
        let results = await AutoDeleteCleanupService.deleteFiles(at: [])

        #expect(results.isEmpty)
    }
}
