//
//  LaunchMigrationSaveTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1063.
//
//  The three one-time Mode migrations in `hyperwhisperApp.init` saved with
//  `try? save()` and set their `@AppStorage` done-flag anyway. A save that
//  failed was never logged, never counted in the `viewContext` failure streak,
//  and never retried: the flag said the migration was done.
//
//  `LaunchMigrationSave.save` is called against a real in-memory store, so a
//  real validation failure goes through it. The flag wiring lives in
//  `init()`, which builds every manager in the app, so that part is read back
//  off the source.
//

import CoreData
import Foundation
import Testing

@testable import HyperWhisper

@MainActor
@Suite("Launch Mode migrations set their flag only after a save (#1063)")
struct LaunchMigrationSaveTests {

    private static let appPath = "app/macos/hyperwhisper/hyperwhisperApp.swift"

    // MARK: - Fixtures

    /// A one-entity model with a REQUIRED attribute, so leaving it unset makes
    /// `save()` throw a real `NSValidationMissingMandatoryPropertyError`.
    private static func makeContext() throws -> NSManagedObjectContext {
        let attribute = NSAttributeDescription()
        attribute.name = "languageModel"
        attribute.attributeType = .stringAttributeType
        attribute.isOptional = false

        let entity = NSEntityDescription()
        entity.name = "MigrationRow"
        entity.managedObjectClassName = NSStringFromClass(NSManagedObject.self)
        entity.properties = [attribute]

        let model = NSManagedObjectModel()
        model.entities = [entity]

        let coordinator = NSPersistentStoreCoordinator(managedObjectModel: model)
        try coordinator.addPersistentStore(ofType: NSInMemoryStoreType, configurationName: nil, at: nil)
        let context = NSManagedObjectContext(concurrencyType: .mainQueueConcurrencyType)
        context.persistentStoreCoordinator = coordinator
        return context
    }

    private static func insertRow(into context: NSManagedObjectContext, languageModel: String?) {
        let row = NSEntityDescription.insertNewObject(forEntityName: "MigrationRow", into: context)
        row.setValue(languageModel, forKey: "languageModel")
    }

    /// A streak key no other suite uses, so parallel tests cannot move it.
    private static func uniqueKey() -> String {
        "launch_migration_test_\(UUID().uuidString)"
    }

    // MARK: - The save step

    @Test("A save that throws returns false and counts in the context's failure streak")
    func failedSaveReportsFailure() throws {
        let context = try Self.makeContext()
        Self.insertRow(into: context, languageModel: nil)
        let key = Self.uniqueKey()

        let saved = LaunchMigrationSave.save(context, site: "launch_migration_test", contextKey: key)

        #expect(saved == false)
        // The save above was failure 1, so the next failure on this key is 2.
        #expect(CoreDataSaveDiagnostics.recordFailure(contextKey: key).failureCount == 2)
        CoreDataSaveDiagnostics.recordSuccess(contextKey: key)
    }

    @Test("A save that works returns true, persists the edit and ends the streak")
    func successfulSaveReportsSuccess() throws {
        let context = try Self.makeContext()
        Self.insertRow(into: context, languageModel: "gemma-4-12b-it-Q4_K_M.gguf")
        let key = Self.uniqueKey()
        _ = CoreDataSaveDiagnostics.recordFailure(contextKey: key)

        let saved = LaunchMigrationSave.save(context, site: "launch_migration_test", contextKey: key)

        #expect(saved == true)
        #expect(context.hasChanges == false)
        let stored = try context.fetch(NSFetchRequest<NSManagedObject>(entityName: "MigrationRow"))
        #expect(stored.count == 1)
        // The success cleared the earlier failure, so a new one starts at 1.
        #expect(CoreDataSaveDiagnostics.recordFailure(contextKey: key).failureCount == 1)
        CoreDataSaveDiagnostics.recordSuccess(contextKey: key)
    }

    // MARK: - The wiring in hyperwhisperApp.init

    @Test("No macOS app file saves with try?")
    func noTryQuestionSave() throws {
        let directory = ProductionSource.url("app/macos/hyperwhisper")
        var offenders: [String] = []
        for file in try ProductionSource.swiftFiles(under: directory) {
            let lines = try ProductionSource.text(of: file).components(separatedBy: .newlines)
            for line in lines where line.contains("try? ") && line.contains(".save()") {
                offenders.append("\(file.lastPathComponent): \(line.trimmingCharacters(in: .whitespaces))")
            }
        }
        #expect(offenders.isEmpty, "try? save() hides a failed save: \(offenders)")
    }

    @Test(
        "Each migration flag is set only after a successful save, or when nothing changed",
        arguments: [
            "didShowGemmaMigrationAlert",
            "didMigrateQwenToLocalLLM",
            "didMigrateGemma12bIdCasing",
        ]
    )
    func flagIsGatedOnTheSave(flag: String) throws {
        let lines = try ProductionSource.code(of: Self.appPath)
            .components(separatedBy: .newlines)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }

        // A flag assignment may follow only one of these: the result of a
        // `LaunchMigrationSave.save`, or the branch that found nothing to migrate.
        let gates = ["if saved {", "} else {"]
        var assignments = 0
        var gatedOnSave = 0
        for (index, line) in lines.enumerated() where line == "\(flag) = true" {
            assignments += 1
            let previous = index > 0 ? lines[index - 1] : ""
            let isSaveGate = previous == "if saved {" || previous.contains("LaunchMigrationSave.save(")
            if isSaveGate { gatedOnSave += 1 }
            #expect(
                isSaveGate || gates.contains(previous),
                "\(flag) is set after '\(previous)', not after a checked save"
            )
        }
        #expect(assignments > 0, "\(flag) is no longer assigned in \(Self.appPath)")
        #expect(gatedOnSave > 0, "\(flag) has no assignment behind LaunchMigrationSave.save")
    }
}
