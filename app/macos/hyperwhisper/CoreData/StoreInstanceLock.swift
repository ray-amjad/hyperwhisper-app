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
        // O_NOFOLLOW + the fstat below: the lock file is truncated and written,
        // so it must be our own plain file. A symlink or hard link to the store
        // (`HyperWhisper.sqlite`) would otherwise be emptied. Refuse before the
        // flock and the truncate; the guard then fails open, writing nothing.
        let fd = open(url.path, O_RDWR | O_CREAT | O_CLOEXEC | O_NOFOLLOW, 0o644)
        guard fd >= 0 else { return .unavailable(code: errno) }

        var info = stat()
        guard fstat(fd, &info) == 0 else {
            let failure = errno
            close(fd)
            return .unavailable(code: failure)
        }
        guard (info.st_mode & S_IFMT) == S_IFREG else {
            close(fd)
            return .unavailable(code: EFTYPE)
        }
        guard info.st_nlink == 1 else {
            close(fd)
            return .unavailable(code: EMLINK)
        }

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

    /// Posted by a second launch, observed by the copy that owns the store.
    /// The notification's object is the lock file's path, so only the owner of
    /// THAT store answers — a copy on another store ignores it.
    static let activationRequest = Notification.Name("com.hyperwhisper.singleInstance.activationRequest")

    private static var activationObserver: NSObjectProtocol?

    /// Return when this process may open the data store. When another copy
    /// already owns it, bring that copy forward and exit without touching the
    /// store or starting the Local API — unless that copy exits during the
    /// hand-off, in which case this launch takes the store and carries on.
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
            own(lock)
        case .unavailable(let code):
            failOpen(code: code)
        case .heldByAnotherProcess(let ownerPID):
            let pid = ownerPID ?? waitForOwnerPID(at: lock.url)
            let pidText = pid.map { String($0) } ?? "unknown"
            AppLogger.coreData.notice("Another HyperWhisper owns this data store · pid=\(pidText, privacy: .public) — handing off")
            requestOwnerActivation(storeKey: lock.url.path, ownerPID: pid)

            // The owner may have been quitting while we asked: then nobody
            // answered, and exiting too would leave no copy running. Test the
            // lock once more after the hand-off's wait; a free lock means this
            // launch is now the owner and carries on.
            switch lock.acquire() {
            case .acquired:
                AppLogger.coreData.notice("The previous owner of this data store has exited — this launch takes over")
                own(lock)
            case .unavailable(let code):
                failOpen(code: code)
            case .heldByAnotherProcess:
                AppLogger.coreData.notice("Handed off to the running HyperWhisper · pid=\(pidText, privacy: .public) — exiting")
                exit(0)
            }
        }
    }

    private static func own(_ lock: StoreInstanceLock) {
        heldLock = lock
        listenForActivationRequests(storeKey: lock.url.path)
    }

    /// Fail open: a launch that cannot test the lock runs as before.
    private static func failOpen(code: Int32) {
        AppLogger.coreData.error("Single-instance lock unavailable · code=\(code, privacy: .public) — continuing without it")
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

    /// Second launch: ask the owner to come forward.
    ///
    /// Since macOS 14 activation is cooperative: a process that is still
    /// launching is not active, so `NSRunningApplication.activate` from here is
    /// ignored (seen on the Mac, macOS 26). The owner activates ITSELF on this
    /// request; the direct call stays as a second try.
    private static func requestOwnerActivation(storeKey: String, ownerPID: pid_t?) {
        DistributedNotificationCenter.default().postNotificationName(
            activationRequest,
            object: storeKey,
            userInfo: nil,
            deliverImmediately: true
        )
        if let ownerPID, let owner = NSRunningApplication(processIdentifier: ownerPID) {
            owner.activate(options: [.activateAllWindows])
        }
        // Let the post leave this process before it exits.
        usleep(100_000)
    }

    /// Owner: answer a second launch's request by coming to the front with the
    /// main window, as a click on the Dock icon would — including when there
    /// is no main window to show.
    private static func listenForActivationRequests(storeKey: String) {
        activationObserver = DistributedNotificationCenter.default().addObserver(
            forName: activationRequest,
            object: storeKey,
            queue: .main
        ) { _ in
            MainActor.assumeIsolated {
                bringToFront()
            }
        }
    }

    private static func bringToFront() {
        AppLogger.ui.info("Another launch of HyperWhisper on this data store asked this copy to come forward")
        // A deliberate open: `launchMinimized` must not hide this window.
        HyperWhisperApp.suppressLaunchMinimizedHide()
        NSApp.activate(ignoringOtherApps: true)
        // An existing main window, even ordered out by `launchMinimized`:
        // bring it forward, as MainAppView.openMainWindow() does.
        if let mainWindow = MainWindowStore.window
            ?? NSApp.windows.first(where: { $0.identifier == .hyperwhisperMainWindow }) {
            mainWindow.makeKeyAndOrderFront(nil)
            return
        }
        // No main window: it was closed, or this is a login-item launch whose
        // WindowGroup was never built. `openWindow(id:)` needs a SwiftUI view,
        // so take the Dock-click path instead: a reopen Apple Event to this
        // process runs AppDelegate.applicationShouldHandleReopen, and SwiftUI's
        // WindowGroup answers a reopen with no visible window by opening one.
        sendReopenToSelf()
    }

    private static func sendReopenToSelf() {
        let event = NSAppleEventDescriptor.appleEvent(
            withEventClass: AEEventClass(kCoreEventClass),
            eventID: AEEventID(kAEReopenApplication),
            targetDescriptor: NSAppleEventDescriptor.currentProcess(),
            returnID: AEReturnID(kAutoGenerateReturnID),
            transactionID: AETransactionID(kAnyTransactionID)
        )
        do {
            try event.sendEvent(options: [.noReply], timeout: 5)
        } catch {
            AppLogger.ui.error("Could not reopen the main window for a second launch · \(error.localizedDescription, privacy: .public)")
        }
    }
}
