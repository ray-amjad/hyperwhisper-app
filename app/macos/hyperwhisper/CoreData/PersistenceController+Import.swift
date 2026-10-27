//
//  PersistenceController+Import.swift
//  hyperwhisper
//
//  Backup import entry points and store write locks.
//  Split out of PersistenceController.swift (#697); every body moved unchanged.
//

import CoreData
import Foundation
import SQLite3

extension PersistenceController {

    // MARK: - Bulk Import Operations (Backup/Restore)

    /// What one backup store import changed.
    struct BackupStoreImportResult {
        var modesImported = 0
        var modesSkipped = 0
        /// Old-to-new mode ids created by `.keepBoth`.
        var modeIdRemap: [UUID: UUID] = [:]
        var vocabularyImported = 0
        var vocabularySkipped = 0
    }

    /// A backup store import whose save failed and was rolled back (#1613).
    struct BackupStoreImportError: Error {
        enum Section {
            case modes
            case vocabulary
        }

        /// The section whose save failed. `.modes`: nothing was written (the
        /// vocabulary is not attempted after a failed modes save).
        /// `.vocabulary`: the vocabulary was left as it was, and the modes in
        /// `committed` (when modes were selected) were saved before it.
        let failedSection: Section
        /// What was saved before the failure: the modes counts and id remap
        /// when the modes section committed; empty otherwise.
        let committed: BackupStoreImportResult
        let underlying: Error
    }

    /// How long a backup import save waits for another process's write lock
    /// on a store file before it fails (issue #1626; Ray chose 10 s).
    nonisolated static let backupImportWriteLockTimeout: TimeInterval = 10

    /// A backup import save that never started: another process held a
    /// store's write lock for longer than `backupImportWriteLockTimeout`.
    struct BackupImportStoreLockedError: Error {
        /// The store file's name (not its path: the path holds the account name).
        let storeFileName: String
        /// The SQLite result code (`SQLITE_BUSY` or `SQLITE_LOCKED`).
        let sqliteCode: Int32
    }

    /// Write locks taken by `acquireStoreWriteLocks` on our own connections,
    /// held until `release()`.
    final class StoreWriteLocks {
        private var connections: [OpaquePointer]

        fileprivate init(connections: [OpaquePointer]) {
            self.connections = connections
        }

        func release() {
            for db in connections {
                sqlite3_exec(db, "ROLLBACK", nil, nil, nil)
                sqlite3_close_v2(db)
            }
            connections = []
        }

        deinit { release() }
    }

    /// Takes the write lock on every SQLite file in `urls`, all within one
    /// `timeout`, and holds them until the result is released. Throws
    /// `BackupImportStoreLockedError` (with every lock already taken let go)
    /// when another connection still holds one at the deadline. Any other
    /// SQLite failure skips that file and is left to the Core Data save,
    /// which reports it.
    nonisolated static func acquireStoreWriteLocks(at urls: [URL], timeout: TimeInterval) throws -> StoreWriteLocks {
        var held: [OpaquePointer] = []
        let deadline = Date().addingTimeInterval(timeout)
        for url in urls {
            var db: OpaquePointer?
            guard sqlite3_open_v2(url.path, &db, SQLITE_OPEN_READWRITE, nil) == SQLITE_OK, let db else {
                sqlite3_close_v2(db)
                continue
            }
            let remaining = max(0, deadline.timeIntervalSinceNow)
            sqlite3_busy_timeout(db, Int32(remaining * 1000))
            let code = sqlite3_exec(db, "BEGIN IMMEDIATE", nil, nil, nil)
            if code == SQLITE_OK {
                held.append(db)
                continue
            }
            sqlite3_close_v2(db)
            let primaryCode = code & 0xFF
            if primaryCode == SQLITE_BUSY || primaryCode == SQLITE_LOCKED {
                StoreWriteLocks(connections: held).release()
                AppLogger.coreData.error("Backup import: \(url.lastPathComponent, privacy: .public) stayed write-locked by another connection for \(Int(timeout))s · not saving")
                throw BackupImportStoreLockedError(storeFileName: url.lastPathComponent, sqliteCode: code)
            }
        }
        return StoreWriteLocks(connections: held)
    }

    /// Waits up to `timeout` for the write lock on the SQLite file at `url`,
    /// then releases it (see `acquireStoreWriteLocks`).
    nonisolated static func waitForStoreWriteLock(at url: URL, timeout: TimeInterval) throws {
        try acquireStoreWriteLocks(at: [url], timeout: timeout).release()
    }

    /// Saves under `NSErrorMergePolicy`, resolving real conflicts once with the
    /// context's own policy (see `saveBackupImportChanges`). The context's
    /// policy is put back afterwards. Each save, the retry included, first
    /// takes and holds the write locks on `storeURLs` and lets them go on the
    /// line before it.
    @MainActor
    static func saveWithoutRetryLoop(
        context: NSManagedObjectContext,
        lockingStoresAt storeURLs: [URL] = [],
        lockTimeout: TimeInterval = backupImportWriteLockTimeout
    ) throws {
        let policy = context.mergePolicy
        context.mergePolicy = NSErrorMergePolicy
        defer { context.mergePolicy = policy }
        func save() throws {
            let locks = try acquireStoreWriteLocks(at: storeURLs, timeout: lockTimeout)
            locks.release()
            try context.save()
        }
        do {
            try save()
        } catch let error as NSError where error.domain == NSCocoaErrorDomain
            && (error.code == NSManagedObjectMergeError || error.code == NSManagedObjectConstraintMergeError) {
            let conflicts = error.userInfo[NSPersistentStoreSaveConflictsErrorKey] as? [Any] ?? []
            let mergeConflicts = conflicts.compactMap { $0 as? NSMergeConflict }
            let constraintConflicts = conflicts.compactMap { $0 as? NSConstraintConflict }
            // No conflict to resolve: SQLite refused the statement itself.
            guard let resolver = policy as? NSMergePolicy,
                  resolver.mergeType != .errorMergePolicyType,
                  !(mergeConflicts.isEmpty && constraintConflicts.isEmpty) else {
                throw error
            }
            if !mergeConflicts.isEmpty {
                try resolver.resolve(optimisticLockingConflicts: mergeConflicts)
            }
            if !constraintConflicts.isEmpty {
                try resolver.resolve(constraintConflicts: constraintConflicts)
            }
            try save()
        }
    }

    /// Imports modes from backup data with conflict resolution, as one
    /// transaction (see `importBackupStore`).
    ///
    /// - Returns: the counts and `.keepBoth` id remap; all zero/empty when the
    ///   save failed and the import was rolled back. `BackupManager` calls
    ///   `importBackupStore` instead, which reports that failure.
    @MainActor
    func importModes(_ backupModes: [BackupMode], resolution: ModeConflictResolution) -> (imported: Int, skipped: Int, idRemap: [UUID: UUID]) {
        guard let result = try? importBackupStore(
            modes: backupModes,
            modeResolution: resolution,
            vocabulary: nil,
            vocabularyResolution: .skip
        ) else {
            return (0, 0, [:])
        }
        return (result.modesImported, result.modesSkipped, result.modeIdRemap)
    }

    /// Imports vocabulary items from backup data, as one transaction (see
    /// `importBackupStore`). All zero when the save failed and was rolled back.
    @MainActor
    func importVocabulary(_ backupItems: [BackupVocabularyItem], resolution: VocabularyConflictResolution) -> (imported: Int, skipped: Int) {
        guard let result = try? importBackupStore(
            modes: nil,
            modeResolution: .skip,
            vocabulary: backupItems,
            vocabularyResolution: resolution
        ) else {
            return (0, 0)
        }
        return (result.vocabularyImported, result.vocabularySkipped)
    }
}
