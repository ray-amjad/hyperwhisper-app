//
//  BackupImportAtomicityTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1613 (macOS sibling of #1605).
//
//  `importModes` and `importVocabulary` saved after every row, so a crash, a
//  kill or a failed save part-way through a backup left a silent partial
//  import. The store must hold the old modes and vocabulary or all of the
//  imported ones, never a part.
//
//  The failure is a real Core Data validation failure, not a seam in the
//  production code: a `willSave` observer blanks the required `name` of ONE
//  backup row (row 300 of 450) whenever a save is about to write it. On `main`
//  rows 0-299 were already committed by then, one save each, so the store
//  ends with 300 of the 450 modes; the first test is written against
//  `importModes`, which exists on `main`, so it shows that partial count there.
//

import CoreData
import Foundation
import Testing

@testable import HyperWhisper

@Suite("Backup import is all or nothing (#1613)")
struct BackupImportAtomicityTests {

    private static let prefix = "percy1613-"
    private static let poisonName = "percy1613-poison"
    private static let total = 450
    private static let poisonIndex = 300

    // MARK: - Fixtures

    /// Blanks the required `name` of the poison row each time a save is about
    /// to write it, so that save fails validation. Removed by the caller.
    @MainActor
    private final class SavePoison {
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
    private final class VocabularySavePoison {
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

    private static func backupMode(id: UUID = UUID(), name: String, isDefault: Bool = false) -> BackupMode {
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

    /// `total` modes; the row at `poisonIndex` carries the poison name.
    private static func largeBackup() -> [BackupMode] {
        (0..<Self.total).map { index in
            Self.backupMode(name: index == Self.poisonIndex ? Self.poisonName : String(format: "\(Self.prefix)%03ld", index))
        }
    }

    private static func vocabularyItem(_ word: String, _ replacement: String?) -> BackupVocabularyItem {
        BackupVocabularyItem(id: UUID(), word: word, replacement: replacement, sortOrder: 0, source: "manual")
    }

    /// The local store before the import: Alpha (the default), Beta and Gamma,
    /// and two vocabulary words. Saved, so the store holds them.
    /// The controller is a PARAMETER the caller holds for the whole test (see
    /// `DefaultModeInvariantTests`: a store dropped in a helper deallocates).
    @MainActor
    @discardableResult
    private func seedLocalStore(_ persistence: PersistenceController) throws -> (alpha: UUID, beta: UUID) {
        let alpha = persistence.createOrUpdateMode(
            id: UUID(), name: "Alpha", preset: "hyper", language: "en", model: "base",
            punctuation: true, capitalization: true, profanityFilter: false
        )
        let beta = persistence.createOrUpdateMode(
            id: UUID(), name: "Beta", preset: "hyper", language: "en", model: "base",
            punctuation: true, capitalization: true, profanityFilter: false
        )
        persistence.createOrUpdateMode(
            id: UUID(), name: "Gamma", preset: "hyper", language: "en", model: "base",
            punctuation: true, capitalization: true, profanityFilter: false
        )
        alpha.isDefault = true
        #expect(persistence.addVocabularyItem(word: "teh", replacement: "the"))
        #expect(persistence.addVocabularyItem(word: "hw", replacement: nil))
        try persistence.container.viewContext.save()
        return (try #require(alpha.id), try #require(beta.id))
    }

    /// Rows in the STORE, read on a fresh background context: unsaved changes
    /// on `viewContext` do not count.
    @MainActor
    private func storedCount(_ entityName: String, in persistence: PersistenceController) throws -> Int {
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

    @MainActor
    private func storedPrefixedModeCount(in persistence: PersistenceController) throws -> Int {
        let context = persistence.container.newBackgroundContext()
        var count = 0
        var failure: Error?
        context.performAndWait {
            let request = NSFetchRequest<NSFetchRequestResult>(entityName: "Mode")
            request.predicate = NSPredicate(format: "name BEGINSWITH %@", Self.prefix)
            do {
                count = try context.count(for: request)
            } catch {
                failure = error
            }
        }
        if let failure { throw failure }
        return count
    }

    // MARK: - A failure part-way

    @MainActor
    @Test func aSaveThatFailsPartWayLeavesTheModesAsTheyWere() throws {
        let persistence = PersistenceController(inMemory: true)
        try seedLocalStore(persistence)
        let before = try storedCount("Mode", in: persistence)
        #expect(before == 3)

        let poison = SavePoison(context: persistence.container.viewContext, poisonName: Self.poisonName)
        defer { poison.remove() }

        _ = persistence.importModes(Self.largeBackup(), resolution: .replace)

        // Without this the case proves nothing.
        #expect(poison.fired, "the poison row was never saved")

        let after = try storedCount("Mode", in: persistence)
        #expect(after == before, "the failed import left \(after - before) of its \(Self.total) modes in the store")
        #expect(try storedPrefixedModeCount(in: persistence) == 0)

        // Nothing half-applied stays pending on the view context either: a
        // rejected row there would poison every later save in the app.
        #expect(persistence.container.viewContext.hasChanges == false)
        #expect(persistence.fetchAllModes().count == before)
        #expect(persistence.fetchAllModes().filter(\.isDefault).count == 1)
    }

    /// A failed modes save stops the import: the vocabulary is not attempted,
    /// so both are left as they were.
    @MainActor
    @Test func aFailedModesSaveThrowsAndLeavesModesAndVocabularyAsTheyWere() throws {
        let persistence = PersistenceController(inMemory: true)
        try seedLocalStore(persistence)
        let modesBefore = try storedCount("Mode", in: persistence)
        let vocabularyBefore = try storedCount("Vocabulary", in: persistence)

        let poison = SavePoison(context: persistence.container.viewContext, poisonName: Self.poisonName)
        defer { poison.remove() }

        var failure: PersistenceController.BackupStoreImportError?
        do {
            _ = try persistence.importBackupStore(
                modes: Self.largeBackup(),
                modeResolution: .replace,
                vocabulary: [
                    Self.vocabularyItem("TEH", "The"),   // would replace "teh" in place
                    Self.vocabularyItem("brand-new", nil),
                ],
                vocabularyResolution: .replace
            )
        } catch let error as PersistenceController.BackupStoreImportError {
            failure = error
        }

        #expect(poison.fired)
        let thrown = try #require(failure, "a failed save must reach the caller so it can report it")
        #expect(thrown.failedSection == .modes)
        #expect(thrown.committed.modesImported == 0)
        #expect(thrown.committed.modeIdRemap.isEmpty)
        #expect(try storedCount("Mode", in: persistence) == modesBefore)
        #expect(try storedCount("Vocabulary", in: persistence) == vocabularyBefore)
        #expect(persistence.container.viewContext.hasChanges == false)

        // The vocabulary was never written.
        let teh = persistence.fetchAllVocabularyItems().first { $0.word?.lowercased() == "teh" }
        #expect(teh?.word == "teh")
        #expect(teh?.replacement == "the")
        #expect(persistence.fetchAllVocabularyItems().contains { $0.word == "brand-new" } == false)

        // The context still saves afterwards: nothing poisoned it.
        persistence.createOrUpdateMode(
            id: UUID(), name: "After", preset: "hyper", language: "en", model: "base",
            punctuation: true, capitalization: true, profanityFilter: false
        )
        #expect(try storedCount("Mode", in: persistence) == modesBefore + 1)
    }

    /// Modes and vocabulary live in different stores, so each is its own save.
    /// When the vocabulary save fails after the modes saved, the modes stay
    /// imported (with exactly one default) and the vocabulary is as it was.
    @MainActor
    @Test func aFailedVocabularySaveKeepsTheSavedModesAndLeavesTheVocabularyAsItWas() throws {
        let persistence = PersistenceController(inMemory: true)
        try seedLocalStore(persistence)
        let backup = Self.successBackup()

        let poisonWord = "percy1613-poison-word"
        let poison = VocabularySavePoison(context: persistence.container.viewContext, poisonWord: poisonWord)
        defer { poison.remove() }

        var failure: PersistenceController.BackupStoreImportError?
        do {
            _ = try persistence.importBackupStore(
                modes: backup.modes,
                modeResolution: .replace,
                vocabulary: [
                    Self.vocabularyItem("TEH", "The"),   // would replace "teh" in place
                    Self.vocabularyItem(poisonWord, nil),
                ],
                vocabularyResolution: .replace
            )
        } catch let error as PersistenceController.BackupStoreImportError {
            failure = error
        }

        #expect(poison.fired, "the poisoned vocabulary save never ran")
        let thrown = try #require(failure)
        #expect(thrown.failedSection == .vocabulary)
        #expect(thrown.committed.modesImported == 301)
        #expect(thrown.committed.vocabularyImported == 0)

        // The modes committed: Beta replaced, 301 imported, one default.
        #expect(try storedCount("Mode", in: persistence) == 3 - 1 + 301)
        #expect(try storedPrefixedModeCount(in: persistence) == 300)
        #expect(persistence.fetchAllModes().filter(\.isDefault).map(\.id) == [Self.conflictingBetaId])

        // The vocabulary is as it was.
        #expect(try storedCount("Vocabulary", in: persistence) == 2)
        let vocabulary = persistence.fetchAllVocabularyItems()
        #expect(vocabulary.first { $0.word?.lowercased() == "teh" }?.replacement == "the")
        #expect(vocabulary.contains { $0.word == poisonWord } == false)
        #expect(persistence.container.viewContext.hasChanges == false)
    }

    // MARK: - A successful import still writes everything

    private static let conflictingBetaId = UUID()

    /// 300 new modes, a "beta" that conflicts with the local Beta and claims
    /// the default, and vocabulary that exercises every branch.
    private static func successBackup() -> (modes: [BackupMode], vocabulary: [BackupVocabularyItem]) {
        var modes = (0..<300).map { Self.backupMode(name: String(format: "\(Self.prefix)%03ld", $0)) }
        modes.append(Self.backupMode(id: Self.conflictingBetaId, name: "beta", isDefault: true))
        let vocabulary = [
            Self.vocabularyItem("TEH", "The"),     // conflicts with the stored "teh"
            Self.vocabularyItem("new-one", "N1"),
            Self.vocabularyItem("dup", "first"),
            Self.vocabularyItem("Dup", "second"),  // conflicts with the row ABOVE, still unsaved
            Self.vocabularyItem("   ", "blank"),   // never imported
        ]
        return (modes, vocabulary)
    }

    @MainActor
    @Test func replaceWritesTheWholeBackupInOneSave() throws {
        let persistence = PersistenceController(inMemory: true)
        let local = try seedLocalStore(persistence)
        let backup = Self.successBackup()

        let result = try persistence.importBackupStore(
            modes: backup.modes, modeResolution: .replace,
            vocabulary: backup.vocabulary, vocabularyResolution: .replace
        )

        #expect(result.modesImported == 301)
        #expect(result.modesSkipped == 0)
        #expect(result.modeIdRemap.isEmpty)
        // TEH (in place), new-one, dup, Dup (in place on the staged dup).
        #expect(result.vocabularyImported == 4)
        #expect(result.vocabularySkipped == 1)

        #expect(persistence.container.viewContext.hasChanges == false)
        #expect(try storedCount("Mode", in: persistence) == 3 - 1 + 301)
        #expect(try storedPrefixedModeCount(in: persistence) == 300)
        #expect(try storedCount("Vocabulary", in: persistence) == 2 + 2)

        let modes = persistence.fetchAllModes()
        #expect(modes.contains { $0.id == local.beta } == false)
        let beta = try #require(modes.first { $0.id == Self.conflictingBetaId })
        #expect(beta.name == "beta")
        // The backup's default wins, and only one mode carries the flag.
        #expect(modes.filter(\.isDefault).map(\.id) == [Self.conflictingBetaId])

        let vocabulary = persistence.fetchAllVocabularyItems()
        let teh = try #require(vocabulary.first { $0.word?.lowercased() == "teh" })
        #expect(teh.replacement == "The")
        let dup = try #require(vocabulary.first { $0.word?.lowercased() == "dup" })
        #expect(dup.replacement == "second")
        #expect(vocabulary.filter { $0.word?.lowercased() == "dup" }.count == 1)
        // New rows go to the end, one sortOrder each.
        let newOne = try #require(vocabulary.first { $0.word == "new-one" })
        #expect(newOne.sortOrder > teh.sortOrder)
        #expect(dup.sortOrder == newOne.sortOrder + 1)
    }

    @MainActor
    @Test func keepBothImportsTheConflictUnderANewNameAndId() throws {
        let persistence = PersistenceController(inMemory: true)
        let local = try seedLocalStore(persistence)
        let backup = Self.successBackup()

        let result = try persistence.importBackupStore(
            modes: backup.modes, modeResolution: .keepBoth,
            vocabulary: nil, vocabularyResolution: .skip
        )

        #expect(result.modesImported == 301)
        #expect(result.modesSkipped == 0)
        let newBetaId = try #require(result.modeIdRemap[Self.conflictingBetaId])
        #expect(try storedCount("Mode", in: persistence) == 3 + 301)
        #expect(try storedCount("Vocabulary", in: persistence) == 2)

        let modes = persistence.fetchAllModes()
        #expect(modes.contains { $0.id == local.beta && $0.name == "Beta" })
        #expect(modes.first { $0.id == newBetaId }?.name == "beta (imported)")
        // A `.keepBoth` copy never takes the default.
        #expect(modes.filter(\.isDefault).map(\.id) == [local.alpha])
    }

    @MainActor
    @Test func skipLeavesTheConflictsAndSeesRowsStagedEarlierInTheSameImport() throws {
        let persistence = PersistenceController(inMemory: true)
        let local = try seedLocalStore(persistence)
        let backup = Self.successBackup()

        let result = try persistence.importBackupStore(
            modes: backup.modes, modeResolution: .skip,
            vocabulary: backup.vocabulary, vocabularyResolution: .skip
        )

        #expect(result.modesImported == 300)
        #expect(result.modesSkipped == 1)
        // new-one and dup; TEH, Dup (the staged dup) and the blank are skipped.
        #expect(result.vocabularyImported == 2)
        #expect(result.vocabularySkipped == 3)

        #expect(try storedCount("Mode", in: persistence) == 3 + 300)
        #expect(try storedCount("Vocabulary", in: persistence) == 2 + 2)

        let modes = persistence.fetchAllModes()
        #expect(modes.contains { $0.id == local.beta })
        #expect(modes.contains { $0.id == Self.conflictingBetaId } == false)
        #expect(modes.filter(\.isDefault).map(\.id) == [local.alpha])

        let vocabulary = persistence.fetchAllVocabularyItems()
        #expect(vocabulary.first { $0.word == "teh" }?.replacement == "the")
        #expect(vocabulary.first { $0.word?.lowercased() == "dup" }?.replacement == "first")
    }

    @MainActor
    @Test func aBackupWithNoDefaultStillLeavesExactlyOne() throws {
        // Issue #536's repair now runs inside the transaction, without its own save.
        let persistence = PersistenceController(inMemory: true)
        let backup = (0..<5).map { Self.backupMode(name: "\(Self.prefix)\($0)") }

        let result = persistence.importModes(backup, resolution: .replace)

        #expect(result.imported == 5)
        #expect(persistence.container.viewContext.hasChanges == false)
        #expect(try storedCount("Mode", in: persistence) == 5)
        #expect(persistence.fetchAllModes().filter(\.isDefault).count == 1)
    }

    // MARK: - Failure message

    /// Each message names the section that failed and claims only what
    /// happened: a vocabulary-only failure never mentions modes, a modes
    /// failure says the vocabulary was left too, and "imported" / "applied"
    /// appear only when something was.
    @Test func theStoreFailureMessageClaimsOnlyWhatHappened() {
        func message(
            _ section: PersistenceController.BackupStoreImportError.Section,
            modesSaved: Bool = false,
            vocabularySelected: Bool = true,
            other: Bool = false,
            licence: Bool = false
        ) -> String {
            BackupManager.storeImportFailureMessage(
                failedSection: section,
                modesSaved: modesSaved,
                vocabularySelected: vocabularySelected,
                otherSectionsApplied: other,
                licenseImportFailed: licence
            )
        }

        let modesOnly = message(.modes, vocabularySelected: false)
        let modesAndVocabulary = message(.modes)
        let vocabularyOnly = message(.vocabulary)
        let vocabularyAfterModes = message(.vocabulary, modesSaved: true)

        for text in [modesOnly, modesAndVocabulary, vocabularyOnly, vocabularyAfterModes] {
            #expect(text.contains("could not be saved"))
            #expect(!text.contains("were applied"))
            #expect(!text.contains("license key"))
        }
        #expect(modesOnly.contains("modes could not be saved"))
        #expect(!modesOnly.contains("vocabulary"))
        #expect(modesAndVocabulary.contains("modes and vocabulary were left as they were"))
        #expect(!modesAndVocabulary.contains("imported"))
        #expect(vocabularyOnly.contains("vocabulary could not be saved"))
        #expect(!vocabularyOnly.contains("mode"))
        #expect(vocabularyAfterModes.contains("modes were imported"))
        #expect(vocabularyAfterModes.contains("vocabulary could not be saved"))

        let partial = message(.vocabulary, other: true)
        let licence = message(.modes, licence: true)
        let licencePartial = message(.vocabulary, modesSaved: true, other: true, licence: true)
        #expect(partial.contains("were applied"))
        #expect(!licence.contains("were applied"))
        #expect(licence.contains("license key could not be securely imported"))
        #expect(licencePartial.contains("modes were imported"))
        #expect(licencePartial.contains("license key could not be securely imported"))
        #expect(licencePartial.hasSuffix("The other selected sections were applied."))
    }
}
