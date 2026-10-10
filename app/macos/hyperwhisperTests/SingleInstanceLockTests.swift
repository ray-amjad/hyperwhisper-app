//
//  SingleInstanceLockTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1483: two copies of the app ran at once on one
//  `HyperWhisper.sqlite`, each with its own Local API, and `local-api.json`
//  ended up naming a process that was gone.
//
//  The lock tests drive `StoreInstanceLock` on a temporary directory. flock
//  locks belong to an open file description, so two locks in this one process
//  contend exactly as two app processes do; one test also uses a real second
//  process and kills it with SIGKILL. The port-file tests drive
//  `LocalAPIServer.deletePortFileIfOwned(at:ownerPID:)` on a temporary file.
//

import Darwin
import Foundation
import Testing

@testable import HyperWhisper

@Suite("One HyperWhisper per data store (#1483)")
struct SingleInstanceLockTests {

    private static let appPath = "app/macos/hyperwhisper/hyperwhisperApp.swift"

    private static func makeDirectory() throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("hw-1483-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    // MARK: - The store lock

    @Test func theFirstCopyTakesTheLockAndRecordsItsPid() throws {
        let directory = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let lock = StoreInstanceLock(directory: directory)
        #expect(lock.acquire(pid: 4_242) == .acquired)
        #expect(lock.isHeld)
        #expect(lock.url.lastPathComponent == "HyperWhisper.lock")
        #expect(StoreInstanceLock.readOwnerPID(at: lock.url) == 4_242)
    }

    @Test func aSecondCopyOnTheSameStoreIsToldWhoOwnsIt() throws {
        let directory = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let first = StoreInstanceLock(directory: directory)
        #expect(first.acquire(pid: 4_242) == .acquired)

        let second = StoreInstanceLock(directory: directory)
        #expect(second.acquire(pid: 5_353) == .heldByAnotherProcess(ownerPID: 4_242))
        #expect(!second.isHeld)
        // The losing launch must not overwrite the owner's pid.
        #expect(StoreInstanceLock.readOwnerPID(at: first.url) == 4_242)
    }

    @Test func theLockIsFreeAgainOnceTheOwnerLetsGo() throws {
        let directory = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let first = StoreInstanceLock(directory: directory)
        #expect(first.acquire(pid: 4_242) == .acquired)
        first.release()
        #expect(!first.isHeld)

        let second = StoreInstanceLock(directory: directory)
        #expect(second.acquire(pid: 5_353) == .acquired)
        #expect(StoreInstanceLock.readOwnerPID(at: second.url) == 5_353)
    }

    /// The guard is per store: a copy with another home (a verify rig under
    /// `CFFIXED_USER_HOME`) has another store and must still start.
    @Test func aCopyOnAnotherStoreIsNotBlocked() throws {
        let one = try Self.makeDirectory()
        let two = try Self.makeDirectory()
        defer {
            try? FileManager.default.removeItem(at: one)
            try? FileManager.default.removeItem(at: two)
        }

        let first = StoreInstanceLock(directory: one)
        let second = StoreInstanceLock(directory: two)
        #expect(first.acquire(pid: 4_242) == .acquired)
        #expect(second.acquire(pid: 5_353) == .acquired)
    }

    /// A lock file left by a copy that died names a dead pid but holds no
    /// lock. It must not block the next launch.
    @Test func aLeftoverLockFileFromADeadCopyDoesNotBlock() throws {
        let directory = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let url = directory.appendingPathComponent(StoreInstanceLock.fileName)
        try Data("99999\n".utf8).write(to: url)

        let lock = StoreInstanceLock(directory: directory)
        #expect(lock.acquire(pid: 4_242) == .acquired)
        #expect(StoreInstanceLock.readOwnerPID(at: url) == 4_242)
    }

    @Test func aMissingStoreDirectoryIsCreated() throws {
        let parent = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: parent) }

        let directory = parent.appendingPathComponent("HyperWhisper", isDirectory: true)
        let lock = StoreInstanceLock(directory: directory)
        #expect(lock.acquire(pid: 4_242) == .acquired)
        #expect(FileManager.default.fileExists(atPath: lock.url.path))
    }

    /// When the lock cannot even be tested, the outcome says so rather than
    /// claiming another owner — the launch guard then lets the launch go on.
    @Test func anUnusableDirectoryIsReportedUnavailable() throws {
        let parent = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: parent) }

        // A regular file where the store directory should be.
        let notADirectory = parent.appendingPathComponent("HyperWhisper")
        try Data("x".utf8).write(to: notADirectory)

        let lock = StoreInstanceLock(directory: notADirectory)
        guard case .unavailable = lock.acquire(pid: 4_242) else {
            Issue.record("a lock that cannot be created must be .unavailable, not acquired or owned")
            return
        }
        #expect(!lock.isHeld)
    }

    @Test func anEmptyOrGarbledLockFileHasNoOwner() throws {
        let directory = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let url = directory.appendingPathComponent(StoreInstanceLock.fileName)
        try Data().write(to: url)
        #expect(StoreInstanceLock.readOwnerPID(at: url) == nil)
        try Data("not a pid".utf8).write(to: url)
        #expect(StoreInstanceLock.readOwnerPID(at: url) == nil)
        try Data("-4\n".utf8).write(to: url)
        #expect(StoreInstanceLock.readOwnerPID(at: url) == nil)
    }

    /// A real second process holds the lock, then dies by SIGKILL. While it
    /// lives this process is refused and told its pid; once the kernel has
    /// reaped it, the lock is free with no cleanup by anyone.
    @Test(.enabled(if: FileManager.default.isExecutableFile(atPath: "/usr/bin/perl")))
    func aKilledOwnerNeverBlocksTheNextLaunch() throws {
        let directory = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent(StoreInstanceLock.fileName)

        let script = """
            use Fcntl qw(:flock); $| = 1;
            open(my $f, '+>>', $ARGV[0]) or die "open: $!";
            flock($f, LOCK_EX | LOCK_NB) or die "flock: $!";
            truncate($f, 0); syswrite($f, "$$\\n");
            print "locked\\n"; sleep 60;
            """
        let owner = Process()
        owner.executableURL = URL(fileURLWithPath: "/usr/bin/perl")
        owner.arguments = ["-e", script, url.path]
        let output = Pipe()
        owner.standardOutput = output
        try owner.run()
        defer {
            if owner.isRunning {
                kill(owner.processIdentifier, SIGKILL)
                owner.waitUntilExit()
            }
        }

        let signal = String(decoding: output.fileHandleForReading.availableData, as: UTF8.self)
        try #require(signal.contains("locked"), "the helper process never took the lock")

        let lock = StoreInstanceLock(directory: directory)
        #expect(lock.acquire(pid: 4_242) == .heldByAnotherProcess(ownerPID: owner.processIdentifier))

        kill(owner.processIdentifier, SIGKILL)
        owner.waitUntilExit()

        #expect(lock.acquire(pid: 4_242) == .acquired)
    }

    // MARK: - The guard runs before the store opens

    /// The guard is the first statement of the app's init, and the store is
    /// not opened by a stored property — whose initial value would run before
    /// that init body.
    @Test func theGuardRunsBeforeAnythingOpensTheStore() throws {
        let prologue = try ProductionSource.slice(
            of: Self.appPath,
            from: "init() {",
            to: "let sharedLicenseManager = LicenseManager()"
        )
        #expect(
            prologue.contains("SingleInstanceGuard.claimStoreOrHandOff()"),
            "HyperWhisperApp.init() must claim the store before it builds anything (issue #1483)"
        )

        let code = try ProductionSource.code(of: Self.appPath)
        #expect(
            !code.contains("let persistenceController = PersistenceController.shared"),
            """
            a stored `persistenceController` opens the Core Data store before init()'s body runs, \
            so a second copy opens the store before the single-instance guard can stop it.
            """
        )
    }

    // MARK: - The discovery file is only withdrawn by its owner

    private static func writePortFile(pid: Int32, to url: URL) throws {
        let payload = LocalAPIPortFile(
            port: 50_554,
            pid: pid,
            started_at: "2026-10-10T00:00:00Z",
            api_version: 1,
            app_version: "0",
            token: "test-token"
        )
        try LocalAPIResponder.encoder.encode(payload).write(to: url)
    }

    @MainActor
    @Test func quittingDeletesTheDiscoveryFileThatNamesThisProcess() throws {
        let directory = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("local-api.json")
        try Self.writePortFile(pid: 4_242, to: url)

        #expect(LocalAPIServer.deletePortFileIfOwned(at: url, ownerPID: 4_242))
        #expect(!FileManager.default.fileExists(atPath: url.path))
    }

    /// The bug: a clean quit of either of two copies deleted the file even
    /// when it named the other, live copy.
    @MainActor
    @Test func quittingLeavesAnotherProcesssDiscoveryFileAlone() throws {
        let directory = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("local-api.json")
        try Self.writePortFile(pid: 5_353, to: url)

        #expect(!LocalAPIServer.deletePortFileIfOwned(at: url, ownerPID: 4_242))
        #expect(FileManager.default.fileExists(atPath: url.path))
        let kept = try LocalAPIResponder.decoder.decode(LocalAPIPortFile.self, from: Data(contentsOf: url))
        #expect(kept.pid == 5_353)
    }

    @MainActor
    @Test func aDiscoveryFileThatNamesNoProcessIsLeftAlone() throws {
        let directory = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("local-api.json")
        try Data("{ not json".utf8).write(to: url)

        #expect(!LocalAPIServer.deletePortFileIfOwned(at: url, ownerPID: 4_242))
        #expect(FileManager.default.fileExists(atPath: url.path))
    }

    @MainActor
    @Test func aMissingDiscoveryFileIsNotAnError() throws {
        let directory = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("local-api.json")

        #expect(!LocalAPIServer.deletePortFileIfOwned(at: url, ownerPID: 4_242))
    }

    /// `stop()` and the run-failure path reach the owner-checked delete.
    @Test func theServerDeletesThroughTheOwnerCheck() throws {
        let body = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/LocalAPI/LocalAPIServer.swift",
            from: "private func deletePortFile() {",
            to: "static func deletePortFileIfOwned("
        )
        #expect(
            body.contains("Self.deletePortFileIfOwned(at: Self.portFileURL, ownerPID: ProcessInfo.processInfo.processIdentifier)"),
            "deletePortFile() must only remove local-api.json when it names this process (issue #1483)"
        )
        #expect(!body.contains("removeItem"), "deletePortFile() must not delete the file unconditionally")
    }
}
