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
                throw NSError(domain: "Percy1626", code: 2)
            }
        }

        func release() {
            guard let db else { return }
            sqlite3_exec(db, "ROLLBACK", nil, nil, nil)
            sqlite3_close_v2(db)
            self.db = nil
        }

        deinit { release() }
    }

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

        let started = Date()
        #expect(throws: PersistenceController.BackupImportStoreLockedError.self) {
            try PersistenceController.waitForStoreWriteLock(at: store.url, timeout: 0.5)
        }
        let waited = Date().timeIntervalSince(started)
        #expect(waited >= 0.4)
        #expect(waited < 5)
    }

    @Test("A write lock let go during the wait lets the save go ahead")
    func releasedWriteLockPasses() async throws {
        let store = try await MainActor.run { try Self.makeStore() }
        defer { store.remove() }
        let holder = try WriteLockHolder(url: store.url)
        DispatchQueue.global().asyncAfter(deadline: .now() + 0.3) { holder.release() }

        try PersistenceController.waitForStoreWriteLock(at: store.url, timeout: 10)
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
}
