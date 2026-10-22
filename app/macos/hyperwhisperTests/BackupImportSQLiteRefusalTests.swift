//
//  BackupImportSQLiteRefusalTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1626.
//
//  When SQLite itself refused a backup import's save, the import's `catch`
//  never ran: a `SQLITE_CONSTRAINT` (a `RAISE(ABORT)` trigger) sent Core Data
//  into its optimistic-locking retry loop until it ended the process, and a
//  write lock held by another connection made the save wait with no limit.
//
//  These run against a real on-disk SQLite store, with the fault put there
//  through SQLite's own C API, the way the issue's evidence did it with the
//  `sqlite3` shell.
//

import CoreData
import Foundation
import SQLite3
import Testing

@testable import HyperWhisper

@Suite("Backup import survives a SQLite-level write refusal (#1626)")
struct BackupImportSQLiteRefusalTests {

    // MARK: - Fixtures

    /// A one-entity model, so these tests need no `Mode` fields filled in.
    private static let model: NSManagedObjectModel = {
        let entity = NSEntityDescription()
        entity.name = "Percy1626Row"
        entity.managedObjectClassName = NSStringFromClass(NSManagedObject.self)
        let name = NSAttributeDescription()
        name.name = "name"
        name.attributeType = .stringAttributeType
        name.isOptional = true
        entity.properties = [name]
        let model = NSManagedObjectModel()
        model.entities = [entity]
        return model
    }()

    private struct Store {
        let directory: URL
        let url: URL
        let container: NSPersistentContainer

        func remove() {
            for store in container.persistentStoreCoordinator.persistentStores {
                try? container.persistentStoreCoordinator.remove(store)
            }
            try? FileManager.default.removeItem(at: directory)
        }
    }

    /// An on-disk store holding one row named "seed", with the app's
    /// view-context merge policy.
    @MainActor
    private static func makeStore() throws -> Store {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("percy1626-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let url = directory.appendingPathComponent("Store.sqlite")
        let container = NSPersistentContainer(name: "Percy1626", managedObjectModel: model)
        let description = NSPersistentStoreDescription(url: url)
        description.shouldAddStoreAsynchronously = false
        container.persistentStoreDescriptions = [description]
        var loadError: Error?
        container.loadPersistentStores { _, error in loadError = error }
        if let loadError { throw loadError }
        let context = container.viewContext
        context.mergePolicy = NSMergeByPropertyObjectTrumpMergePolicy
        insertRow(named: "seed", into: context)
        try context.save()
        return Store(directory: directory, url: url, container: container)
    }

    @MainActor
    @discardableResult
    private static func insertRow(named name: String, into context: NSManagedObjectContext) -> NSManagedObject {
        let row = NSEntityDescription.insertNewObject(forEntityName: "Percy1626Row", into: context)
        row.setValue(name, forKey: "name")
        return row
    }

    /// Runs SQL on a connection of its own, the way another process would.
    private static func execute(_ sql: String, at url: URL) throws {
        var db: OpaquePointer?
        defer { sqlite3_close_v2(db) }
        guard sqlite3_open_v2(url.path, &db, SQLITE_OPEN_READWRITE, nil) == SQLITE_OK,
              sqlite3_exec(db, sql, nil, nil, nil) == SQLITE_OK else {
            throw NSError(domain: "Percy1626", code: 1, userInfo: [NSLocalizedDescriptionKey: sql])
        }
    }

    /// The row names in the store file, read on a connection of its own.
    private static func storedNames(at url: URL) -> [String] {
        var db: OpaquePointer?
        var statement: OpaquePointer?
        defer {
            sqlite3_finalize(statement)
            sqlite3_close_v2(db)
        }
        guard sqlite3_open_v2(url.path, &db, SQLITE_OPEN_READONLY, nil) == SQLITE_OK,
              sqlite3_prepare_v2(db, "SELECT ZNAME FROM ZPERCY1626ROW ORDER BY Z_PK", -1, &statement, nil) == SQLITE_OK else {
            return []
        }
        var names: [String] = []
        while sqlite3_step(statement) == SQLITE_ROW {
            names.append(String(cString: sqlite3_column_text(statement, 0)))
        }
        return names
    }

    /// Holds the write lock on `url` until `release()`, like
    /// `BEGIN IMMEDIATE;` in a `sqlite3` shell.
    private final class WriteLockHolder: @unchecked Sendable {
        private var db: OpaquePointer?

        init(url: URL) throws {
            guard sqlite3_open_v2(url.path, &db, SQLITE_OPEN_READWRITE, nil) == SQLITE_OK,
                  sqlite3_exec(db, "BEGIN IMMEDIATE", nil, nil, nil) == SQLITE_OK else {
                sqlite3_close_v2(db)
                db = nil
                throw NSError(domain: "Percy1626", code: 2)
            }
        }

        func release() {
            guard let db else { return }
            sqlite3_exec(db, "ROLLBACK", nil, nil, nil)
            sqlite3_close_v2(db)
            self.db = nil
        }

        /// Lets the lock go `delay` seconds from now, on a thread of its own.
        /// Not `DispatchQueue.global().asyncAfter`: a loaded CI runner can
        /// hold a global-queue block back for longer than the wait it is
        /// meant to end (issue #2345). A plain thread is scheduled like any
        /// other.
        func release(after delay: TimeInterval) {
            Thread.detachNewThread {
                Thread.sleep(forTimeInterval: delay)
                self.release()
            }
        }

        deinit { release() }
    }

    private struct Wait: Sendable {
        let result: Result<Void, any Error>
        /// How long the wait took, timed on its own thread: a test that is
        /// slow to resume afterwards on a busy pool does not count.
        let seconds: TimeInterval
    }

    /// Runs a blocking lock wait on a thread of its own. Called inline from a
    /// nonisolated `async` test, the wait parks a Swift concurrency pool
    /// thread for as long as it lasts, and these tests run in parallel: a few
    /// of them can park the whole pool of a 3-core runner and stall every
    /// suite beside them (issue #2345; see SemaphoreWait.swift).
    private static func offThePool(_ body: @escaping @Sendable () throws -> Void) async -> Wait {
        await withCheckedContinuation { continuation in
            Thread.detachNewThread {
                let started = Date()
                let result = Result { try body() }
                continuation.resume(returning: Wait(result: result, seconds: Date().timeIntervalSince(started)))
            }
        }
    }

    /// How long a test lets a save wait for a lock it releases itself. The
    /// release ends the wait; this only stops a broken build from hanging, so
    /// it sits far above any delay a loaded runner adds (issue #2345). The
    /// app's own 10 s is checked in `unlockedStorePasses`.
    private static let releasedLockWaitLimit: TimeInterval = 60

    // MARK: - Constraint

    @Test("A trigger that refuses every insert throws, and the process lives")
    @MainActor
    func constraintRefusalThrows() throws {
        let store = try Self.makeStore()
        defer { store.remove() }
        try Self.execute(
            "CREATE TRIGGER percy1626 BEFORE INSERT ON ZPERCY1626ROW BEGIN SELECT RAISE(ABORT,'forced'); END;",
            at: store.url
        )
        let context = store.container.viewContext
        for index in 0..<500 {
            Self.insertRow(named: "imported-\(index)", into: context)
        }

        // On `main` this save never returns: Core Data retries, then ends the process.
        #expect(throws: (any Error).self) {
            try PersistenceController.saveWithoutRetryLoop(context: context)
        }
        context.rollback()

        #expect(Self.storedNames(at: store.url) == ["seed"])
        let policy = try #require(context.mergePolicy as? NSMergePolicy)
        #expect(policy.mergeType == .mergeByPropertyObjectTrumpMergePolicyType)
    }

    @Test("A real conflict is still resolved with the context's own policy")
    @MainActor
    func realConflictStillMerges() throws {
        let store = try Self.makeStore()
        defer { store.remove() }
        let context = store.container.viewContext
        let request = NSFetchRequest<NSManagedObject>(entityName: "Percy1626Row")
        let row = try #require(try context.fetch(request).first)

        // Another writer changes the row after the context read it.
        try Self.execute("UPDATE ZPERCY1626ROW SET ZNAME = 'theirs', Z_OPT = Z_OPT + 1", at: store.url)
        row.setValue("mine", forKey: "name")

        try PersistenceController.saveWithoutRetryLoop(context: context)

        // Object-trump: the import's value wins, as it did before #1626.
        #expect(Self.storedNames(at: store.url) == ["mine"])
    }

    // MARK: - Busy lock

    @Test("A write lock held past the timeout throws within the timeout")
    func heldWriteLockThrows() async throws {
        let store = try await MainActor.run { try Self.makeStore() }
        defer { store.remove() }
        let holder = try WriteLockHolder(url: store.url)
        defer { holder.release() }

        let url = store.url

        let wait = await Self.offThePool { try PersistenceController.waitForStoreWriteLock(at: url, timeout: 0.5) }
        #expect(throws: PersistenceController.BackupImportStoreLockedError.self) { try wait.result.get() }
        #expect(wait.seconds >= 0.4)
        #expect(wait.seconds < 5)
    }

    @Test("A write lock let go during the wait lets the save go ahead")
    func releasedWriteLockPasses() async throws {
        let store = try await MainActor.run { try Self.makeStore() }
        defer { store.remove() }
        let holder = try WriteLockHolder(url: store.url)
        let url = store.url

        holder.release(after: 0.3)
        let wait = await Self.offThePool {
            try PersistenceController.waitForStoreWriteLock(at: url, timeout: Self.releasedLockWaitLimit)
        }
        try wait.result.get()
    }

    @Test("An unlocked store passes at once, and the import timeout is 10 s")
    func unlockedStorePasses() async throws {
        let store = try await MainActor.run { try Self.makeStore() }
        defer { store.remove() }

        let started = Date()
        try PersistenceController.waitForStoreWriteLock(at: store.url, timeout: 10)
        #expect(Date().timeIntervalSince(started) < 1)
        #expect(PersistenceController.backupImportWriteLockTimeout == 10)
    }

    @Test("Every store's write lock is held together until released")
    func locksAreHeldUntilRelease() async throws {
        let first = try await MainActor.run { try Self.makeStore() }
        let second = try await MainActor.run { try Self.makeStore() }
        defer {
            first.remove()
            second.remove()
        }

        let locks = try PersistenceController.acquireStoreWriteLocks(at: [first.url, second.url], timeout: 1)
        // Another connection cannot take either lock while ours hold them.
        #expect(throws: (any Error).self) { _ = try WriteLockHolder(url: first.url) }
        #expect(throws: (any Error).self) { _ = try WriteLockHolder(url: second.url) }
        locks.release()
        try WriteLockHolder(url: first.url).release()
        try WriteLockHolder(url: second.url).release()
    }

    @Test("One deadline covers every store, and a timeout lets the taken locks go")
    func sharedDeadlineReleasesTakenLocks() async throws {
        let first = try await MainActor.run { try Self.makeStore() }
        let second = try await MainActor.run { try Self.makeStore() }
        defer {
            first.remove()
            second.remove()
        }
        let holder = try WriteLockHolder(url: second.url)
        defer { holder.release() }

        let urls = [first.url, second.url]

        let wait = await Self.offThePool { _ = try PersistenceController.acquireStoreWriteLocks(at: urls, timeout: 0.5) }
        #expect(throws: PersistenceController.BackupImportStoreLockedError.self) { try wait.result.get() }
        #expect(wait.seconds < 5)
        // The first store's lock was let go when the second timed out.
        try WriteLockHolder(url: first.url).release()
    }

    @Test("A save waits for a lock let go during the wait, and writes")
    @MainActor
    func saveTakesLocksBeforeWriting() async throws {
        let store = try Self.makeStore()
        defer { store.remove() }
        let holder = try WriteLockHolder(url: store.url)
        let context = store.container.viewContext
        Self.insertRow(named: "imported", into: context)

        // The save waits on the main actor, so the lock is let go soon: the
        // main actor is blocked for only as long as the release takes.
        holder.release(after: 0.3)
        try PersistenceController.saveWithoutRetryLoop(
            context: context,
            lockingStoresAt: [store.url],
            lockTimeout: Self.releasedLockWaitLimit
        )

        #expect(Self.storedNames(at: store.url) == ["seed", "imported"])
    }
}
