//
//  LocalAPIStagingSweepTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1484.
//
//  `POST /transcribe` stages the caller's audio in
//  `$TMPDIR/hyperwhisper-local-api-…/audio.<ext>` and deletes it only when the
//  request finishes in-process. A `kill -9` mid-request left the folder, with
//  the user's speech in it, for good. These tests drive the launch sweep on an
//  injected directory with an injected pid-liveness check.
//

import Darwin
import Foundation
import Testing

@testable import HyperWhisper

@Suite("Local API staging folders do not outlive a dead process (#1484)")
struct LocalAPIStagingSweepTests {

    private static let transcribePath = "app/macos/hyperwhisper/Managers/LocalAPI/Endpoints/TranscribeEndpoint.swift"
    private static let appPath = "app/macos/hyperwhisper/hyperwhisperApp.swift"

    /// Stands in for this process in every sweep, so no fixture pid collides
    /// with it by accident.
    private static let me: pid_t = 4_000
    private static let deadPID: pid_t = 4_001
    private static let livePID: pid_t = 4_002

    private static func alive(_ pid: pid_t) -> Bool { pid == me || pid == livePID }

    // MARK: - Fixtures

    private static func makeSandbox() throws -> URL {
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("LocalAPIStagingSweepTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    /// A staging folder holding an `audio.wav`, like the endpoint makes.
    @discardableResult
    private static func makeFolder(_ name: String, in dir: URL, modified: Date = Date()) throws -> URL {
        let folder = dir.appendingPathComponent(name, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: false)
        try Data([0x52, 0x49, 0x46, 0x46]).write(to: folder.appendingPathComponent("audio.wav"))
        try FileManager.default.setAttributes([.modificationDate: modified], ofItemAtPath: folder.path)
        return folder
    }

    private static func exists(_ url: URL) -> Bool {
        FileManager.default.fileExists(atPath: url.path)
    }

    @discardableResult
    private static func sweep(_ dir: URL, now: Date = Date()) -> [URL] {
        LocalAPIStagingSweep.sweep(
            in: dir,
            now: now,
            currentPID: me,
            isProcessAlive: alive
        )
    }

    // MARK: - Naming

    @Test func aNewFolderNameCarriesThePidAndParsesBack() {
        let id = UUID()
        let name = LocalAPIStagingSweep.directoryName(pid: 1234, id: id)
        #expect(name == "hyperwhisper-local-api-1234-\(id.uuidString)")
        #expect(LocalAPIStagingSweep.owner(ofEntryNamed: name) == .process(1234))
    }

    @Test func theDefaultNameUsesThisProcess() {
        let name = LocalAPIStagingSweep.directoryName()
        #expect(LocalAPIStagingSweep.owner(ofEntryNamed: name) == .process(getpid()))
    }

    @Test func aPreFixFolderNameIsLegacy() {
        let name = "hyperwhisper-local-api-\(UUID().uuidString)"
        #expect(LocalAPIStagingSweep.owner(ofEntryNamed: name) == .legacy)
        // The issue's own folder name.
        #expect(LocalAPIStagingSweep.owner(ofEntryNamed: "hyperwhisper-local-api-9A152D68-933A-43A2-A02A-6358D1745FEB") == .legacy)
    }

    @Test func anyOtherNameIsNotOurs() {
        let uuid = UUID().uuidString
        let names = [
            "",
            "hyperwhisper-local-api-",
            "hyperwhisper-local-api",
            "hyperwhisper-local-api-notes",
            "hyperwhisper-local-api-12-",
            "hyperwhisper-local-api-12-notauuid",
            "hyperwhisper-local-api-0-\(uuid)",
            "hyperwhisper-local-api--12-\(uuid)",
            "hyperwhisper-local-api-+12-\(uuid)",
            "hyperwhisper-local-api-99999999999-\(uuid)",
            "hyperwhisper-local-api-12-\(uuid).bak",
            "hyperwhisper-local-api-\(uuid)x",
            "Hyperwhisper-local-api-12-\(uuid)",
            "x-hyperwhisper-local-api-12-\(uuid)",
            "hyperwhisper-diagnostics-1700000000",
            "hw-reencode-\(uuid).wav",
        ]
        for name in names {
            #expect(LocalAPIStagingSweep.owner(ofEntryNamed: name) == .notOurs, "\(name)")
        }
    }

    // MARK: - The sweep

    @Test func aFolderFromADeadRequestIsRemoved() throws {
        let dir = try Self.makeSandbox()
        defer { try? FileManager.default.removeItem(at: dir) }
        let stale = try Self.makeFolder(LocalAPIStagingSweep.directoryName(pid: Self.deadPID), in: dir)

        let removed = Self.sweep(dir)

        #expect(!Self.exists(stale))
        #expect(removed.map(\.lastPathComponent) == [stale.lastPathComponent])
    }

    @Test func aFreshFolderOfAnotherLiveInstanceIsKept() throws {
        let dir = try Self.makeSandbox()
        defer { try? FileManager.default.removeItem(at: dir) }
        let live = try Self.makeFolder(LocalAPIStagingSweep.directoryName(pid: Self.livePID), in: dir)

        #expect(Self.sweep(dir).isEmpty)
        #expect(Self.exists(live.appendingPathComponent("audio.wav")))
    }

    /// The sweep runs off the main actor while the server may already take a
    /// request, so this process's folders are never its to remove, however
    /// the liveness check answers and however old they look.
    @Test func thisProcessesOwnFolderIsKept() throws {
        let dir = try Self.makeSandbox()
        defer { try? FileManager.default.removeItem(at: dir) }
        let mine = try Self.makeFolder(
            LocalAPIStagingSweep.directoryName(pid: Self.me),
            in: dir,
            modified: Date(timeIntervalSinceNow: -3 * LocalAPIStagingSweep.liveOwnerMaxAge)
        )

        let removed = LocalAPIStagingSweep.sweep(
            in: dir,
            currentPID: Self.me,
            isProcessAlive: { _ in false }
        )

        #expect(removed.isEmpty)
        #expect(Self.exists(mine))
    }

    /// A live pid on a day-old folder is a reused pid: no request runs that long.
    @Test func aLivePidOnAnAncientFolderIsTreatedAsReused() throws {
        let dir = try Self.makeSandbox()
        defer { try? FileManager.default.removeItem(at: dir) }
        let now = Date()
        let old = try Self.makeFolder(
            LocalAPIStagingSweep.directoryName(pid: Self.livePID),
            in: dir,
            modified: now.addingTimeInterval(-LocalAPIStagingSweep.liveOwnerMaxAge - 60)
        )

        Self.sweep(dir, now: now)

        #expect(!Self.exists(old))
    }

    @Test func aPreFixFolderIsRemovedOnlyOnceItIsOld() throws {
        let dir = try Self.makeSandbox()
        defer { try? FileManager.default.removeItem(at: dir) }
        let now = Date()
        let fresh = try Self.makeFolder(
            "hyperwhisper-local-api-\(UUID().uuidString)",
            in: dir,
            modified: now.addingTimeInterval(-60)
        )
        let old = try Self.makeFolder(
            "hyperwhisper-local-api-\(UUID().uuidString)",
            in: dir,
            modified: now.addingTimeInterval(-LocalAPIStagingSweep.legacyMaxAge - 60)
        )

        Self.sweep(dir, now: now)

        #expect(Self.exists(fresh))
        #expect(!Self.exists(old))
    }

    @Test func entriesThatAreNotStagingFoldersAreUntouched() throws {
        let dir = try Self.makeSandbox()
        defer { try? FileManager.default.removeItem(at: dir) }
        let fm = FileManager.default
        let old = Date(timeIntervalSinceNow: -2 * LocalAPIStagingSweep.liveOwnerMaxAge)

        // Look-alike folder names.
        let lookAlikes = try [
            "hyperwhisper-local-api-notes",
            "hyperwhisper-local-api-\(Self.deadPID)-notauuid",
            "hyperwhisper-diagnostics-1700000000",
            "other-app-\(UUID().uuidString)",
        ].map { try Self.makeFolder($0, in: dir, modified: old) }

        // A plain FILE with a dead-pid staging name.
        let fileWithOurName = dir.appendingPathComponent(LocalAPIStagingSweep.directoryName(pid: Self.deadPID))
        try Data([1, 2, 3]).write(to: fileWithOurName)

        // A SYMLINK with a dead-pid staging name, pointing at a folder outside
        // the swept directory. Neither the link nor its target may go.
        let outside = try Self.makeSandbox()
        defer { try? fm.removeItem(at: outside) }
        let target = try Self.makeFolder("precious", in: outside)
        let link = dir.appendingPathComponent(LocalAPIStagingSweep.directoryName(pid: Self.deadPID))
        try fm.createSymbolicLink(at: link, withDestinationURL: target)

        let removed = Self.sweep(dir)

        #expect(removed.isEmpty)
        for folder in lookAlikes {
            #expect(Self.exists(folder.appendingPathComponent("audio.wav")), "\(folder.lastPathComponent)")
        }
        #expect(Self.exists(fileWithOurName))
        #expect((try? fm.destinationOfSymbolicLink(atPath: link.path)) != nil)
        #expect(Self.exists(target.appendingPathComponent("audio.wav")))
    }

    /// Only the top level is swept: a staging-shaped folder nested inside
    /// another folder is not the endpoint's and stays.
    @Test func theSweepDoesNotRecurse() throws {
        let dir = try Self.makeSandbox()
        defer { try? FileManager.default.removeItem(at: dir) }
        let parent = try Self.makeFolder("parent", in: dir)
        let nested = try Self.makeFolder(LocalAPIStagingSweep.directoryName(pid: Self.deadPID), in: parent)

        #expect(Self.sweep(dir).isEmpty)
        #expect(Self.exists(nested))
    }

    @Test func aMissingDirectoryIsANoOp() {
        let missing = FileManager.default.temporaryDirectory
            .appendingPathComponent("LocalAPIStagingSweepTests-missing-\(UUID().uuidString)")
        #expect(Self.sweep(missing).isEmpty)
    }

    // MARK: - The real liveness check

    @Test func theRealLivenessCheckSeesThisProcessAndNotABogusPid() {
        #expect(LocalAPIStagingSweep.isProcessAlive(getpid()))
        #expect(!LocalAPIStagingSweep.isProcessAlive(0))
        #expect(!LocalAPIStagingSweep.isProcessAlive(-1))
        // Far above macOS's pid ceiling (99,998), so no process can hold it.
        #expect(!LocalAPIStagingSweep.isProcessAlive(Int32.max))
    }

    // MARK: - Wiring

    @Test func theEndpointNamesItsFolderThroughTheSweep() throws {
        let staging = try ProductionSource.slice(
            of: Self.transcribePath,
            from: "private static func makePrivateStagedAudioFile(",
            to: "return StagedAudioFile("
        )
        #expect(staging.contains("LocalAPIStagingSweep.directoryName()"))
        let code = try ProductionSource.code(of: Self.transcribePath)
        #expect(!code.contains("\"hyperwhisper-local-api-\\("))
    }

    @Test func launchSweepsBeforeTheServerCanStart() throws {
        let code = try ProductionSource.code(of: Self.appPath)
        let sweep = try #require(code.range(of: "LocalAPIStagingSweep.sweep()"))
        let start = try #require(code.range(of: "LocalAPIServer.shared.start()"))
        #expect(sweep.lowerBound < start.lowerBound)
    }
}
