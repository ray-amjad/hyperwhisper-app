//
//  LocalAPIStagingSweep.swift
//  hyperwhisper
//
//  Deletes the per-request audio folders a dead process left in the temp
//  directory (issue #1484).
//

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
/// other copy may be mid-request. A pid-tagged folder is removed only when that
/// pid is no longer alive. Folders of THIS process are never removed, so the
/// sweep cannot race a request the server starts while it runs. A folder whose
/// pid is alive but which is older than `liveOwnerMaxAge` is removed anyway: no
/// request runs that long, so its pid has been reused by an unrelated process.
///
/// A folder made by a build before this fix has no pid
/// (`hyperwhisper-local-api-<UUID>`). Its owner cannot be known, so it is
/// removed only once it is older than `legacyMaxAge`.
///
/// Only real directories (never a symlink) owned by this user, directly in
/// the swept directory, whose name is exactly one of the two shapes above, are
/// touched. `removeItem` unlinks a symlink inside a folder rather than
/// following it.
enum LocalAPIStagingSweep {

    /// The prefix of every staging folder name.
    static let prefix = "hyperwhisper-local-api-"

    /// How old a pre-#1484 folder (no pid in its name) must be before a sweep
    /// removes it. Long enough that no request a still-running older build
    /// started can be in flight.
    static let legacyMaxAge: TimeInterval = 60 * 60

    /// How old a pid-tagged folder must be before a sweep removes it even
    /// though its pid is alive (pid reuse).
    static let liveOwnerMaxAge: TimeInterval = 24 * 60 * 60

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
    ///   - modified: the folder's modification date; `nil` keeps a legacy
    ///     folder and an alive-pid folder, since their age cannot be proven.
    ///   - now: the sweep's clock.
    ///   - currentPID: this process. Its folders are always kept.
    ///   - isProcessAlive: the liveness check, injected for tests.
    static func shouldRemove(
        owner: Owner,
        modified: Date?,
        now: Date,
        currentPID: pid_t,
        isProcessAlive: (pid_t) -> Bool
    ) -> Bool {
        switch owner {
        case .notOurs:
            return false
        case .legacy:
            guard let modified else { return false }
            return now.timeIntervalSince(modified) > legacyMaxAge
        case .process(let pid):
            if pid == currentPID { return false }
            if !isProcessAlive(pid) { return true }
            guard let modified else { return false }
            return now.timeIntervalSince(modified) > liveOwnerMaxAge
        }
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
        fileManager: FileManager = .default
    ) -> [URL] {
        let names: [String]
        do {
            names = try fileManager.contentsOfDirectory(atPath: directory.path)
        } catch {
            return []
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
                isProcessAlive: isProcessAlive
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
