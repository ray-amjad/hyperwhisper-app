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
//  `LocalAPIServer.deletePortFileIfOwned(at:ownerPID:)` on a temporary file:
//  it may remove a file naming this pid or a dead one, never a live other.
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

    // MARK: - The hand-off

    private static let lockPath = "app/macos/hyperwhisper/CoreData/StoreInstanceLock.swift"

    /// If the owner quits while a second launch hands off, nobody answers;
    /// the second launch must test the lock again before it exits, so one
    /// copy still runs.
    @Test func aSecondLaunchRetriesTheLockBeforeItExits() throws {
        let arm = try ProductionSource.slice(
            of: Self.lockPath,
            from: "case .heldByAnotherProcess(let ownerPID):",
            to: "private static func own("
        )
        let handOff = try #require(arm.range(of: "requestOwnerActivation("))
        let retry = try #require(arm.range(of: "lock.acquire()", range: handOff.upperBound..<arm.endIndex))
        let exitCall = try #require(arm.range(of: "exit(0)"))
        #expect(retry.lowerBound < exitCall.lowerBound, "the lock retry must come before exit(0)")
        #expect(arm.contains("own(lock)"), "a free lock on retry must be taken and the launch go on")
    }

    /// With no main window (closed, or a login-item launch that never built
    /// one) the owner still shows one, through the Dock-click reopen path.
    @Test func theOwnerReopensAWindowWhenItHasNone() throws {
        let body = try ProductionSource.slice(
            of: Self.lockPath,
            from: "private static func bringToFront() {",
            to: "private static func sendReopenToSelf() {"
        )
        #expect(body.contains("makeKeyAndOrderFront"))
        #expect(body.contains("sendReopenToSelf()"))
        let send = try ProductionSource.slice(
            of: Self.lockPath,
            from: "private static func sendReopenToSelf() {",
            to: "\n}"
        )
        #expect(send.contains("kAEReopenApplication"))
        #expect(send.contains("NSAppleEventDescriptor.currentProcess()"))
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

    /// The pid of a process that has exited and been reaped: no process has it.
    private static func deadPID() throws -> Int32 {
        let helper = Process()
        helper.executableURL = URL(fileURLWithPath: "/usr/bin/true")
        try helper.run()
        helper.waitUntilExit()
        return helper.processIdentifier
    }

    /// The bug: a clean quit of either of two copies deleted the file even
    /// when it named the other, live copy. launchd (pid 1) stands in for that
    /// copy: it is always alive, and `kill(1, 0)` answers EPERM, not ESRCH.
    @MainActor
    @Test func quittingLeavesAnotherLiveProcesssDiscoveryFileAlone() throws {
        let directory = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("local-api.json")
        try Self.writePortFile(pid: 1, to: url)

        #expect(LocalAPIServer.isProcessAlive(1))
        #expect(!LocalAPIServer.deletePortFileIfOwned(at: url, ownerPID: 4_242))
        #expect(FileManager.default.fileExists(atPath: url.path))
        let kept = try LocalAPIResponder.decoder.decode(LocalAPIPortFile.self, from: Data(contentsOf: url))
        #expect(kept.pid == 1)
    }

    /// A real second process, alive and owned by this user, names the file.
    @MainActor
    @Test func quittingLeavesASiblingProcesssDiscoveryFileAlone() throws {
        let sibling = Process()
        sibling.executableURL = URL(fileURLWithPath: "/bin/sleep")
        sibling.arguments = ["30"]
        try sibling.run()
        defer {
            sibling.terminate()
            sibling.waitUntilExit()
        }

        let directory = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("local-api.json")
        try Self.writePortFile(pid: sibling.processIdentifier, to: url)

        #expect(!LocalAPIServer.deletePortFileIfOwned(at: url, ownerPID: ProcessInfo.processInfo.processIdentifier))
        #expect(FileManager.default.fileExists(atPath: url.path))
    }

    /// #655 must stay fixed: a file left by a copy that crashed or was
    /// SIGKILLed names a dead pid, and `stop()` / a failed write remove it.
    @MainActor
    @Test func aDiscoveryFileThatNamesADeadProcessIsDeleted() throws {
        let directory = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("local-api.json")
        let dead = try Self.deadPID()
        #expect(!LocalAPIServer.isProcessAlive(dead))
        try Self.writePortFile(pid: dead, to: url)

        #expect(LocalAPIServer.deletePortFileIfOwned(at: url, ownerPID: ProcessInfo.processInfo.processIdentifier))
        #expect(!FileManager.default.fileExists(atPath: url.path))
    }

    /// A file no client can parse names no process; it goes, as before #1483.
    @MainActor
    @Test func aGarbledDiscoveryFileIsDeleted() throws {
        let directory = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("local-api.json")
        try Data("{ not json".utf8).write(to: url)

        #expect(LocalAPIServer.deletePortFileIfOwned(at: url, ownerPID: 4_242))
        #expect(!FileManager.default.fileExists(atPath: url.path))
    }

    @Test func aPidOfZeroOrBelowIsNotALiveProcess() {
        #expect(!LocalAPIServer.isProcessAlive(0))
        #expect(!LocalAPIServer.isProcessAlive(-1))
        #expect(LocalAPIServer.isProcessAlive(ProcessInfo.processInfo.processIdentifier))
    }

    @MainActor
    @Test func aMissingDiscoveryFileIsNotAnError() throws {
        let directory = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("local-api.json")

        #expect(!LocalAPIServer.deletePortFileIfOwned(at: url, ownerPID: 4_242))
    }

    /// `stop()`, the run-failure path and the failed-write cleanup all reach
    /// the liveness-checked delete; none removes the file unconditionally.
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

        let cleanup = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/LocalAPI/LocalAPIServer.swift",
            from: "private func deleteExistingPortFileIfStale(",
            to: "private static func existingPortFileIsStale("
        )
        #expect(
            cleanup.contains("Self.deletePortFileIfOwned(at: url, ownerPID: ProcessInfo.processInfo.processIdentifier)"),
            "the failed-write cleanup must not remove a file naming another live copy (issue #1483)"
        )
        #expect(!cleanup.contains("removePortFile"), "the failed-write cleanup must not delete the file unconditionally")
    }
}
