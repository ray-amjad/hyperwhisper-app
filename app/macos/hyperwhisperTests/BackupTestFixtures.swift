//
//  BackupTestFixtures.swift
//  hyperwhisperTests
//
//  Fixtures shared by the backup-import tests (#1720): one `BackupMode`
//  builder, the two Core Data save-poison observers that make a real save
//  fail validation (#1613, #1627), and a reader for the rows in the STORE.
//
//  `BackupImportInPlaceTests` keeps its own `backupMode`: it decodes JSON on
//  purpose to drive the Codable path.
//

import CoreData
import Foundation
@testable import HyperWhisper

enum BackupTestFixtures {

    /// A plain "hyper" mode. `BackupMode.init` has no defaults, so a new
    /// required field is added here once.
    static func backupMode(id: UUID = UUID(), name: String, isDefault: Bool = false) -> BackupMode {
        BackupMode(
            id: id,
            name: name,
            preset: "hyper",
            language: "en",
            model: "base",
            punctuation: true,
            capitalization: true,
            profanityFilter: false,
            customInstructions: nil,
            languageModel: nil,
            cloudProvider: nil,
            cloudTranscriptionModel: nil,
            postProcessingMode: 0,
            postProcessingProvider: nil,
            englishSpelling: nil,
            userSystemPrompt: nil,
            isDefault: isDefault,
            sortOrder: 0,
            cloudAccuracyTier: nil,
            removeTrailingPeriod: nil,
            geminiCustomPrompt: nil,
            cloudPostProcessingModel: nil,
            cloudTranscriptionDomain: nil
        )
    }

    /// Rows in the STORE, read on a fresh background context: unsaved changes
    /// on `viewContext` do not count.
    @MainActor
    static func storedCount(_ entityName: String, in persistence: PersistenceController) throws -> Int {
        let context = persistence.container.newBackgroundContext()
        var count = 0
        var failure: Error?
        context.performAndWait {
            do {
                count = try context.count(for: NSFetchRequest<NSFetchRequestResult>(entityName: entityName))
            } catch {
                failure = error
            }
        }
        if let failure { throw failure }
        return count
    }
}

/// Blanks the required `name` of the poison mode each time a save is about to
/// write it, so that save fails validation. Removed by the caller.
@MainActor
final class ModeSavePoison {
    private(set) var fired = false
    private var token: NSObjectProtocol?

    init(context: NSManagedObjectContext, poisonName: String) {
        token = NotificationCenter.default.addObserver(
            forName: NSManagedObjectContext.willSaveObjectsNotification,
            object: context,
            queue: nil
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                for case let mode as Mode in context.insertedObjects where mode.name == poisonName {
                    mode.name = nil
                    self?.fired = true
                }
            }
        }
    }

    func remove() {
        if let token {
            NotificationCenter.default.removeObserver(token)
        }
        token = nil
    }
}

/// Makes the save that writes the vocabulary word `poisonWord` fail
/// validation: when such a row is about to be saved, a Mode with no name
/// (a required attribute) is inserted into the same save. The rollback
/// discards it with the vocabulary. Removed by the caller.
@MainActor
final class VocabularySavePoison {
    private(set) var fired = false
    private var token: NSObjectProtocol?

    init(context: NSManagedObjectContext, poisonWord: String) {
        token = NotificationCenter.default.addObserver(
            forName: NSManagedObjectContext.willSaveObjectsNotification,
            object: context,
            queue: nil
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                let poisoned = context.insertedObjects.contains { ($0 as? Vocabulary)?.word == poisonWord }
                if poisoned {
                    _ = Mode(context: context)
                    self?.fired = true
                }
            }
        }
    }

    func remove() {
        if let token {
            NotificationCenter.default.removeObserver(token)
        }
        token = nil
    }
}
