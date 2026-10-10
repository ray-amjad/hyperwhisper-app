//
//  StoreInstanceLock.swift
//  hyperwhisper
//
//  One running HyperWhisper per data store (issue #1483).
//
//  A second launch on the same profile (`open -n`, a login item racing a manual
//  launch, two copies of the app in different folders) used to run a full
//  second copy: its own Core Data stack on the same `HyperWhisper.sqlite`, its
//  own Local API, and a `local-api.json` that named whichever copy started last.
//
//  The guard is an exclusive `flock(2)` on a lock file that sits beside the
//  store, taken before anything opens the store and held for the life of the
//  process. It is keyed on the STORE, not on the bundle identifier: a copy run
//  with another home (`CFFIXED_USER_HOME`, a test rig) has another store and is
//  never stopped. The kernel drops the lock when the process exits for any
//  reason — crash, SIGKILL included — so a dead copy can never block a launch.
//

import AppKit
import CoreData
import Darwin
import Foundation

/// An exclusive, process-lifetime lock on one data-store directory.
final class StoreInstanceLock {

    /// The lock file's name. It sits in the same directory as `HyperWhisper.sqlite`.
    static let fileName = "HyperWhisper.lock"

    enum Outcome: Equatable {
        /// This process now owns the store.
        case acquired
        /// Another live process owns the store. `ownerPID` is the pid that
        /// process wrote into the lock file, or nil when it could not be read.
        case heldByAnotherProcess(ownerPID: pid_t?)
        /// The lock could not be taken or tested at all (for example the
        /// directory cannot be created). The caller decides; the launch guard
        /// lets the launch go on, as it did before this lock existed.
        case unavailable(code: Int32)
    }

    let url: URL
    private var descriptor: Int32 = -1

    /// True while this instance holds the lock.
    var isHeld: Bool { descriptor >= 0 }

    init(directory: URL) {
        url = directory.appendingPathComponent(Self.fileName, isDirectory: false)
    }

    deinit {
        release()
    }

    /// Try once to take the lock, without waiting.
    ///
    /// On success the lock file holds `pid` as decimal text, so a later launch
    /// can find this process and hand off to it.
    func acquire(pid: pid_t = getpid()) -> Outcome {
        guard descriptor < 0 else { return .acquired }

        let directory = url.deletingLastPathComponent()
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        } catch {
            return .unavailable(code: Int32(truncatingIfNeeded: (error as NSError).code))
        }

        // O_CLOEXEC: a helper the app spawns must not inherit the lock and keep
        // the store "owned" after the app itself has gone.
        let fd = open(url.path, O_RDWR | O_CREAT | O_CLOEXEC, 0o644)
        guard fd >= 0 else { return .unavailable(code: errno) }

        guard flock(fd, LOCK_EX | LOCK_NB) == 0 else {
            let failure = errno
            close(fd)
            if failure == EWOULDBLOCK {
                return .heldByAnotherProcess(ownerPID: Self.readOwnerPID(at: url))
            }
            return .unavailable(code: failure)
        }

        // The pid is a hint for the hand-off, not part of the lock: the flock is
        // the lock. A failed write leaves the lock held and the pid unreadable.
        let text = Array("\(pid)\n".utf8)
        _ = ftruncate(fd, 0)
        _ = text.withUnsafeBytes { pwrite(fd, $0.baseAddress, $0.count, 0) }

        descriptor = fd
        return .acquired
    }

    /// Give the lock up. The file stays: unlinking a flock file lets a third
    /// process lock a fresh inode while a second still holds the old one.
    func release() {
        guard descriptor >= 0 else { return }
        _ = flock(descriptor, LOCK_UN)
        close(descriptor)
        descriptor = -1
    }

    /// The pid recorded in a lock file, or nil when there is none.
    static func readOwnerPID(at url: URL) -> pid_t? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        let text = String(decoding: data, as: UTF8.self)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard let pid = pid_t(text), pid > 0 else { return nil }
        return pid
    }
}

/// Runs the one-copy-per-store check at launch (issue #1483).
@MainActor
enum SingleInstanceGuard {

    /// The lock this process holds for its whole life. Never released: the
    /// kernel frees it when the process exits, however it exits.
    private static var heldLock: StoreInstanceLock?

    /// Return when this process may open the data store. When another copy
    /// already owns it, bring that copy forward and exit without touching the
    /// store or starting the Local API.
    ///
    /// Must run before anything touches `PersistenceController.shared`.
    static func claimStoreOrHandOff() {
        guard heldLock == nil else { return }

        // The unit-test host is this app. It must not quit because the user's
        // own copy is running on the same profile, so tests skip the guard.
        guard !isRunningUnitTests else { return }

        let lock = StoreInstanceLock(directory: NSPersistentContainer.defaultDirectoryURL())
        switch lock.acquire() {
        case .acquired:
            heldLock = lock
        case .unavailable(let code):
            // Fail open: a launch that cannot test the lock runs as before.
            AppLogger.coreData.error("Single-instance lock unavailable · code=\(code, privacy: .public) — continuing without it")
        case .heldByAnotherProcess(let ownerPID):
            let pid = ownerPID ?? waitForOwnerPID(at: lock.url)
            let pidText = pid.map { String($0) } ?? "unknown"
            AppLogger.coreData.notice("Another HyperWhisper owns this data store · pid=\(pidText, privacy: .public) — handing off and exiting")
            if let pid {
                activateOwner(pid: pid)
            }
            exit(0)
        }
    }

    /// True inside an XCTest / Swift Testing host process.
    static var isRunningUnitTests: Bool {
        ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] != nil
    }

    /// The owner writes its pid just after it takes the lock, so a launch that
    /// lands in that gap reads an empty file. Wait briefly for the pid.
    private static func waitForOwnerPID(at url: URL) -> pid_t? {
        for _ in 0..<10 {
            usleep(50_000)
            if let pid = StoreInstanceLock.readOwnerPID(at: url) { return pid }
        }
        return nil
    }

    /// Bring the running copy to the front.
    private static func activateOwner(pid: pid_t) {
        guard let owner = NSRunningApplication(processIdentifier: pid) else { return }
        owner.activate(options: [.activateAllWindows])
    }
}
