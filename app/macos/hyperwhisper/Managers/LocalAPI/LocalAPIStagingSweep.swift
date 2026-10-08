//
//  LocalAPIStagingSweep.swift
//  hyperwhisper
//
//  Deletes the per-request audio folders a dead process left in the temp
//  directory (issue #1484).
//

import AppKit
import Darwin
import Foundation

/// Names, and later deletes, the private temp folders `POST /transcribe`
/// stages the caller's audio in.
///
/// # Why (issue #1484)
///
/// Both `/transcribe` inputs (`audio_base64` and `file`) copy the caller's
/// audio into `$TMPDIR/hyperwhisper-local-api-…/audio.<ext>`. The request
/// deletes that folder when it finishes, but only in-process: after a
/// `kill -9` or a crash mid-request the folder, with the user's speech in it,
/// stayed for good. `sweep()` runs once per launch and removes them.
///
/// # Which folders are stale
///
/// A new folder carries the pid of the process that made it:
/// `hyperwhisper-local-api-<pid>-<UUID>`. Two copies of the app can run at
/// once (issue #1483), so "this launch did not make it" is not enough: the
/// other copy may be mid-request. A pid-tagged folder is kept only while its
/// OWNER is alive, and the owner is alive when the pid is alive AND the process
/// holding that pid started no later than the folder's modification date. A
/// process that started after the folder was last written cannot have made
/// it: the owner died and the pid was reused, so the folder goes at once.
/// When the start time cannot be read, a live pid keeps the folder until it
/// is older than `liveOwnerMaxAge`. Folders of THIS process are never
/// removed, so the sweep cannot race a request the server starts while it
/// runs.
///
/// A folder made by a build before this fix has no pid
/// (`hyperwhisper-local-api-<UUID>`). Only an older build makes one, so when no
/// other copy of the app is running its owner is dead and it goes at once.
/// When another copy is running, that copy may be an older build mid-request,
/// so the folder is kept until it is older than `liveOwnerMaxAge`.
///
/// Only real directories (never a symlink) owned by this user, directly in
/// the swept directory, whose name is exactly one of the two shapes above, are
/// touched. `removeItem` unlinks a symlink inside a folder rather than
/// following it.
enum LocalAPIStagingSweep {

    /// The prefix of every staging folder name.
    static let prefix = "hyperwhisper-local-api-"

    /// How old a folder must be before a sweep removes it even though its
    /// owner may still be alive: a pid-tagged folder whose live pid has no
    /// readable start time, or a pre-#1484 folder while another copy of the
    /// app runs. No request runs that long.
    static let liveOwnerMaxAge: TimeInterval = 24 * 60 * 60

    /// Slack for the start-time test. A pid whose process started up to this
    /// long after the folder's modification date still counts as the owner,
    /// so a wall-clock step back between the process start and the request
    /// can never make a live request's folder look orphaned.
    static let startTimeTolerance: TimeInterval = 60

    /// Who made a temp-directory entry, read from its name alone.
    enum Owner: Equatable {
        /// A staging folder made by the process with this pid.
        case process(pid_t)
        /// A staging folder from a build before #1484, with no pid in its name.
        case legacy
        /// Not a Local API staging folder. Never touched.
        case notOurs
    }

    // MARK: - Naming

    /// The name of a new staging folder: `hyperwhisper-local-api-<pid>-<UUID>`.
    static func directoryName(pid: pid_t = getpid(), id: UUID = UUID()) -> String {
        "\(prefix)\(pid)-\(id.uuidString)"
    }

    /// Parses a temp-directory entry name. Anything but the exact two shapes
    /// `directoryName` and the pre-#1484 builds produced is `.notOurs`.
    static func owner(ofEntryNamed name: String) -> Owner {
        guard name.hasPrefix(prefix) else { return .notOurs }
        let rest = String(name.dropFirst(prefix.count))
        if isCanonicalUUID(rest) {
            return .legacy
        }
        guard let dash = rest.firstIndex(of: "-") else { return .notOurs }
        let pidText = rest[rest.startIndex..<dash]
        let uuidText = String(rest[rest.index(after: dash)...])
        guard !pidText.isEmpty,
              pidText.count <= 10,
              pidText.allSatisfy({ $0.isASCII && $0.isNumber }),
              let pid = Int32(pidText),
              pid > 0,
              isCanonicalUUID(uuidText)
        else { return .notOurs }
        return .process(pid)
    }

    /// True for a 36-character UUID string, the only form `UUID.uuidString`
    /// writes. `UUID(uuidString:)` alone would also be enough today, but the
    /// length check keeps the parser exact whatever Foundation tolerates.
    private static func isCanonicalUUID(_ text: String) -> Bool {
        text.utf8.count == 36 && UUID(uuidString: text) != nil
    }

    // MARK: - Decision

    /// Whether a sweep removes a staging folder.
    ///
    /// - Parameters:
    ///   - owner: from `owner(ofEntryNamed:)`.
    ///   - modified: the folder's modification date. It is no earlier than
    ///     the folder's creation and no later than its owner's death (only
    ///     the owner writes in it), so it can stand in for both. `nil` keeps
    ///     any folder whose owner may be alive, since nothing can be proven.
    ///   - now: the sweep's clock.
    ///   - currentPID: this process. Its folders are always kept.
    ///   - isProcessAlive: the pid liveness check, injected for tests.
    ///   - processStartTime: when the process now holding a pid started, or
    ///     `nil` when that cannot be read. Injected for tests.
    ///   - anotherCopyIsRunning: whether any other copy of the app runs now.
    ///     Asked only for a pre-#1484 folder. Injected for tests.
    static func shouldRemove(
        owner: Owner,
        modified: Date?,
        now: Date,
        currentPID: pid_t,
        isProcessAlive: (pid_t) -> Bool,
        processStartTime: (pid_t) -> Date?,
        anotherCopyIsRunning: () -> Bool
    ) -> Bool {
        switch owner {
        case .notOurs:
            return false
        case .legacy:
            // Only a pre-#1484 build makes this shape, and this process is not
            // one, so with no other copy running its owner is dead.
            if !anotherCopyIsRunning() { return true }
            return isOlderThanAnyRequest(modified: modified, now: now)
        case .process(let pid):
            if pid == currentPID { return false }
            if !isProcessAlive(pid) { return true }
            if let modified, let started = processStartTime(pid),
               started.timeIntervalSince(modified) > startTimeTolerance {
                // The process holding this pid started after the folder was
                // last written, so it is not the one that made it.
                return true
            }
            return isOlderThanAnyRequest(modified: modified, now: now)
        }
    }

    /// The backstop for a folder whose owner may still be alive.
    private static func isOlderThanAnyRequest(modified: Date?, now: Date) -> Bool {
        guard let modified else { return false }
        return now.timeIntervalSince(modified) > liveOwnerMaxAge
    }

    /// True while a process with this pid exists. `EPERM` means it exists but
    /// belongs to another user, which still counts as alive.
    static func isProcessAlive(_ pid: pid_t) -> Bool {
        guard pid > 0 else { return false }
        if kill(pid, 0) == 0 { return true }
        return errno != ESRCH
    }

    // MARK: - Sweep

    /// Removes every stale staging folder directly inside `directory`.
    ///
    /// - Returns: the folders it removed.
    @discardableResult
    static func sweep(
        in directory: URL = FileManager.default.temporaryDirectory,
        now: Date = Date(),
        currentPID: pid_t = getpid(),
        currentUID: uid_t = getuid(),
        isProcessAlive: (pid_t) -> Bool = LocalAPIStagingSweep.isProcessAlive,
        processStartTime: (pid_t) -> Date? = LocalAPIStagingSweep.processStartTime,
        anotherCopyIsRunning: () -> Bool = LocalAPIStagingSweep.anotherCopyIsRunning,
        fileManager: FileManager = .default
    ) -> [URL] {
        let names: [String]
        do {
            names = try fileManager.contentsOfDirectory(atPath: directory.path)
        } catch {
            return []
        }

        // Asked at most once per sweep, and only if a pre-#1484 folder exists.
        var anotherCopy: Bool?
        func anotherCopyOnce() -> Bool {
            if let anotherCopy { return anotherCopy }
            let answer = anotherCopyIsRunning()
            anotherCopy = answer
            return answer
        }

        var removed: [URL] = []
        for name in names {
            let entryOwner = Self.owner(ofEntryNamed: name)
            guard entryOwner != .notOurs else { continue }

            let entry = directory.appendingPathComponent(name, isDirectory: true)
            // `attributesOfItem` is an lstat: a symlink reports
            // `.typeSymbolicLink`, never its target's type.
            guard let attributes = try? fileManager.attributesOfItem(atPath: entry.path),
                  (attributes[.type] as? FileAttributeType) == .typeDirectory
            else { continue }
            if let ownerID = (attributes[.ownerAccountID] as? NSNumber)?.uint32Value,
               ownerID != currentUID {
                continue
            }

            guard shouldRemove(
                owner: entryOwner,
                modified: attributes[.modificationDate] as? Date,
                now: now,
                currentPID: currentPID,
                isProcessAlive: isProcessAlive,
                processStartTime: processStartTime,
                anotherCopyIsRunning: { anotherCopyOnce() }
            ) else { continue }

            do {
                try fileManager.removeItem(at: entry)
                removed.append(entry)
            } catch {
                AppLogger.settings.error("LocalAPI staging sweep: could not remove a stale folder · \(error.localizedDescription, privacy: .public)")
            }
        }

        if !removed.isEmpty {
            AppLogger.settings.info("LocalAPI staging sweep: removed \(removed.count, privacy: .public) stale request folder(s)")
        }
        return removed
    }
}

// MARK: - Darwin process facts

extension LocalAPIStagingSweep {

    /// When the process now holding `pid` started, from `sysctl`
    /// `KERN_PROC_PID`. `nil` when there is no such process or the call fails.
    static func processStartTime(_ pid: pid_t) -> Date? {
        guard pid > 0 else { return nil }
        var mib: [Int32] = [CTL_KERN, KERN_PROC, KERN_PROC_PID, pid]
        let mibCount = UInt32(mib.count)
        var info = kinfo_proc()
        var size = MemoryLayout<kinfo_proc>.stride
        guard sysctl(&mib, mibCount, &info, &size, nil, 0) == 0,
              size >= MemoryLayout<kinfo_proc>.stride,
              info.kp_proc.p_pid == pid
        else { return nil }
        // Same read as `CrashRecoveryManager.kernelProcessStartDate()`.
        let started = info.kp_proc.p_starttime
        guard started.tv_sec > 0 else { return nil }
        return Date(timeIntervalSince1970: TimeInterval(started.tv_sec) + TimeInterval(started.tv_usec) / 1_000_000)
    }

    /// Whether another copy of this app (same bundle identifier, another pid)
    /// is running. With no bundle identifier the answer is unknown, so it
    /// reports `true`, which keeps pre-#1484 folders.
    static func anotherCopyIsRunning() -> Bool {
        guard let bundleID = Bundle.main.bundleIdentifier else { return true }
        let me = getpid()
        return NSRunningApplication.runningApplications(withBundleIdentifier: bundleID)
            .contains { $0.processIdentifier != me }
    }
}
