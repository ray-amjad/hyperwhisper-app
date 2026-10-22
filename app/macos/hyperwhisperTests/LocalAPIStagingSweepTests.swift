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
//  injected directory with injected process facts: pid liveness, process
//  start time, and whether another copy of the app is running.
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

    /// By default no start time can be read, which is the pre-start-time
    /// behaviour: a live pid keeps its folder until the 24 h backstop.
    private static func noStartTime(_ pid: pid_t) -> Date? { nil }

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
    private static func sweep(
        _ dir: URL,
        now: Date = Date(),
        processStartTime: (pid_t) -> Date? = noStartTime,
        anotherCopyIsRunning: () -> Bool = { true }
    ) -> [URL] {
        LocalAPIStagingSweep.sweep(
            in: dir,
            now: now,
            currentPID: me,
            isProcessAlive: alive,
            processStartTime: processStartTime,
            anotherCopyIsRunning: anotherCopyIsRunning
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
            isProcessAlive: { _ in false },
            processStartTime: { _ in .distantFuture },
            anotherCopyIsRunning: { false }
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

    /// Finding 1: the pid is alive, but the process holding it started after
    /// the folder was written, so it cannot be the owner. The owner is dead
    /// and its pid was reused: the folder goes at once, not after 24 h.
    @Test func aFolderWhosePidWasReusedIsRemoved() throws {
        let dir = try Self.makeSandbox()
        defer { try? FileManager.default.removeItem(at: dir) }
        let now = Date()
        let written = now.addingTimeInterval(-2 * 60 * 60)
        let orphan = try Self.makeFolder(
            LocalAPIStagingSweep.directoryName(pid: Self.livePID),
            in: dir,
            modified: written
        )

        let removed = Self.sweep(dir, now: now, processStartTime: { pid in
            pid == Self.livePID ? written.addingTimeInterval(60 * 60) : nil
        })

        #expect(!Self.exists(orphan))
        #expect(removed.map(\.lastPathComponent) == [orphan.lastPathComponent])
    }

    /// The pid is alive and its process started before the folder was
    /// written: that process is the owner, maybe mid-request. Kept.
    @Test func aFolderWhoseLiveOwnerStartedBeforeItIsKept() throws {
        let dir = try Self.makeSandbox()
        defer { try? FileManager.default.removeItem(at: dir) }
        let now = Date()
        let written = now.addingTimeInterval(-2 * 60 * 60)
        let live = try Self.makeFolder(
            LocalAPIStagingSweep.directoryName(pid: Self.livePID),
            in: dir,
            modified: written
        )

        let removed = Self.sweep(dir, now: now, processStartTime: { pid in
            pid == Self.livePID ? written.addingTimeInterval(-30 * 60) : nil
        })

        #expect(removed.isEmpty)
        #expect(Self.exists(live.appendingPathComponent("audio.wav")))
    }

    /// A start time a few seconds after the folder's date is inside the
    /// clock-step slack, so the folder is kept: erring toward keeping.
    @Test func aStartTimeWithinTheToleranceStillCountsAsTheOwner() throws {
        let dir = try Self.makeSandbox()
        defer { try? FileManager.default.removeItem(at: dir) }
        let now = Date()
        let written = now.addingTimeInterval(-60 * 60)
        let live = try Self.makeFolder(
            LocalAPIStagingSweep.directoryName(pid: Self.livePID),
            in: dir,
            modified: written
        )

        let removed = Self.sweep(dir, now: now, processStartTime: { _ in
            written.addingTimeInterval(LocalAPIStagingSweep.startTimeTolerance / 2)
        })

        #expect(removed.isEmpty)
        #expect(Self.exists(live))
    }

    /// Finding 2: with no other copy of the app running, a pre-fix folder's
    /// owner (an older build) is dead, so it goes at once however young.
    @Test func aPreFixFolderIsRemovedAtOnceWhenNoOtherCopyRuns() throws {
        let dir = try Self.makeSandbox()
        defer { try? FileManager.default.removeItem(at: dir) }
        let now = Date()
        let fresh = try Self.makeFolder(
            "hyperwhisper-local-api-\(UUID().uuidString)",
            in: dir,
            modified: now.addingTimeInterval(-60)
        )

        Self.sweep(dir, now: now, anotherCopyIsRunning: { false })

        #expect(!Self.exists(fresh))
    }

    /// Finding 2: another copy may be an older build mid-request (#1483), so
    /// a pre-fix folder stays until it is older than any request can be.
    @Test func aPreFixFolderIsKeptUntil24HoursWhileAnotherCopyRuns() throws {
        let dir = try Self.makeSandbox()
        defer { try? FileManager.default.removeItem(at: dir) }
        let now = Date()
        let hoursOld = try Self.makeFolder(
            "hyperwhisper-local-api-\(UUID().uuidString)",
            in: dir,
            modified: now.addingTimeInterval(-3 * 60 * 60)
        )
        let dayOld = try Self.makeFolder(
            "hyperwhisper-local-api-\(UUID().uuidString)",
            in: dir,
            modified: now.addingTimeInterval(-LocalAPIStagingSweep.liveOwnerMaxAge - 60)
        )

        Self.sweep(dir, now: now, anotherCopyIsRunning: { true })

        #expect(Self.exists(hoursOld.appendingPathComponent("audio.wav")))
        #expect(!Self.exists(dayOld))
    }

    /// The other-copy check is asked only when a pre-fix folder is there,
    /// and at most once per sweep.
    @Test func theOtherCopyCheckIsAskedOnlyForPreFixFolders() throws {
        let dir = try Self.makeSandbox()
        defer { try? FileManager.default.removeItem(at: dir) }
        var asked = 0
        try Self.makeFolder(LocalAPIStagingSweep.directoryName(pid: Self.deadPID), in: dir)
        Self.sweep(dir, anotherCopyIsRunning: { asked += 1; return true })
        #expect(asked == 0)

        try Self.makeFolder("hyperwhisper-local-api-\(UUID().uuidString)", in: dir)
        try Self.makeFolder("hyperwhisper-local-api-\(UUID().uuidString)", in: dir)
        Self.sweep(dir, anotherCopyIsRunning: { asked += 1; return true })
        #expect(asked == 1)
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

    @Test func theRealStartTimeOfThisProcessIsInThePastAndABogusPidHasNone() throws {
        let started = try #require(LocalAPIStagingSweep.processStartTime(getpid()))
        #expect(started <= Date())
        #expect(started > Date(timeIntervalSinceNow: -7 * 24 * 60 * 60))
        #expect(LocalAPIStagingSweep.processStartTime(0) == nil)
        #expect(LocalAPIStagingSweep.processStartTime(Int32.max) == nil)
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

    /// The launch path runs the sweep. It is a detached task with no ordering
    /// against the server start; `thisProcessesOwnFolderIsKept` is what
    /// guarantees it never deletes a request this launch is serving.
    @Test func launchRunsTheSweep() throws {
        let bootstrap = try ProductionSource.slice(
            of: Self.appPath,
            from: "private func bootstrapAppServices() {",
            to: "LocalAPIServer.shared.configure("
        )
        #expect(bootstrap.contains("LocalAPIStagingSweep.sweep()"))
    }
}
