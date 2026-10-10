//
//  BackupImportInPlaceTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1479.
//
//  Settings > Backup > Import passes `modeConflict: .replace`, and `.replace`
//  deleted every same-name mode and re-created it. So each import of your own
//  backup gave every mode a new row, the default mode "Hyper" lost
//  `isSystemProvided` (the backup has no such key, and the create path writes
//  `false`), and every `sortOrder` grew by the mode count. `.replace` now
//  updates the local counterpart in place.
//
//  These drive the real export projection (`BackupMode(from:)`, through JSON
//  as the file carries it) and the real restore (`importBackupStore`).
//

import CoreData
import Foundation
import Testing

@testable import HyperWhisper

@Suite("Importing a backup updates same-name modes in place (#1479)")
struct BackupImportInPlaceTests {

    // MARK: - Fixtures

    /// The seeded default mode "Hyper" (isSystemProvided, sortOrder 0) plus
    /// two user modes. Saved, so every row has a permanent objectID.
    /// The controller is a PARAMETER the caller holds for the whole test (see
    /// `DefaultModeInvariantTests`: a store dropped in a helper deallocates).
    @MainActor
    private func seedLocalStore(_ persistence: PersistenceController) throws {
        persistence.initializeDefaultModes()
        persistence.createOrUpdateMode(
            id: UUID(), name: "Notes", preset: "custom", language: "en", model: "base",
            punctuation: true, capitalization: true, profanityFilter: false
        )
        persistence.createOrUpdateMode(
            id: UUID(), name: "Email", preset: "custom", language: "en", model: "base",
            punctuation: true, capitalization: true, profanityFilter: false
        )
        try persistence.container.viewContext.save()
        let hyper = try #require(persistence.fetchAllModes().first { $0.id == SeededModeValues.seededID })
        #expect(hyper.isSystemProvided)
        #expect(persistence.fetchAllModes().count == 3)
    }

    /// The store's modes as a backup file carries them: exported, encoded,
    /// decoded.
    @MainActor
    private func exportModes(_ persistence: PersistenceController) throws -> [BackupMode] {
        let data = try JSONEncoder().encode(persistence.fetchAllModes().map { BackupMode(from: $0) })
        return try JSONDecoder().decode([BackupMode].self, from: data)
    }

    /// A v1 backup mode object with only the required keys.
    private static func backupMode(
        id: UUID, name: String, model: String = "base", sortOrder: Int = 0, isDefault: Bool = false
    ) throws -> BackupMode {
        let json = """
            {"id":"\(id.uuidString)","name":"\(name)","preset":"custom","language":"en",
             "model":"\(model)","punctuation":true,"capitalization":true,"profanityFilter":false,
             "postProcessingMode":0,"isDefault":\(isDefault),"sortOrder":\(sortOrder)}
            """
        return try JSONDecoder().decode(BackupMode.self, from: Data(json.utf8))
    }

    private struct RowShape: Equatable {
        let objectID: NSManagedObjectID
        let name: String?
        let isSystemProvided: Bool
        let isDefault: Bool
        let sortOrder: Int16
    }

    @MainActor
    private func shape(_ persistence: PersistenceController) -> [UUID: RowShape] {
        var rows: [UUID: RowShape] = [:]
        for mode in persistence.fetchAllModes() {
            guard let id = mode.id else { continue }
            rows[id] = RowShape(
                objectID: mode.objectID,
                name: mode.name,
                isSystemProvided: mode.isSystemProvided,
                isDefault: mode.isDefault,
                sortOrder: mode.sortOrder
            )
        }
        return rows
    }

    // MARK: - Your own backup

    @MainActor
    @Test func reImportingYourOwnBackupThreeTimesChangesNothing() throws {
        let persistence = PersistenceController(inMemory: true)
        try seedLocalStore(persistence)
        let before = shape(persistence)
        let backup = try exportModes(persistence)
        #expect(backup.count == 3)

        for round in 1...3 {
            let result = try persistence.importBackupStore(
                modes: backup, modeResolution: .replace,
                vocabulary: nil, vocabularyResolution: .skip
            )
            #expect(result.modesImported == 3, "round \(round)")
            #expect(result.modesSkipped == 0, "round \(round)")
            #expect(result.modeIdRemap.isEmpty, "round \(round)")
            #expect(persistence.container.viewContext.hasChanges == false)

            // Same rows (objectIDs), same ids, same flags, same sort order.
            #expect(shape(persistence) == before, "round \(round) changed the modes")
        }

        let hyper = try #require(persistence.fetchAllModes().first { $0.id == SeededModeValues.seededID })
        #expect(hyper.isSystemProvided)
        #expect(hyper.isDefault)
        #expect(hyper.sortOrder == 0)
        #expect(persistence.fetchAllModes().filter(\.isDefault).count == 1)
    }

    @MainActor
    @Test func theImportStillWritesTheBackupsValuesOntoTheKeptRow() throws {
        let persistence = PersistenceController(inMemory: true)
        try seedLocalStore(persistence)
        let notes = try #require(persistence.fetchAllModes().first { $0.name == "Notes" })
        let notesId = try #require(notes.id)
        let notesObject = notes.objectID
        let notesSort = notes.sortOrder

        // The backup's Notes uses another model and claims sortOrder 0.
        let backup = try Self.backupMode(id: notesId, name: "Notes", model: "large-v3", sortOrder: 0)
        _ = try persistence.importBackupStore(
            modes: [backup], modeResolution: .replace,
            vocabulary: nil, vocabularyResolution: .skip
        )

        let kept = try #require(persistence.fetchAllModes().first { $0.id == notesId })
        #expect(kept.objectID == notesObject)
        #expect(kept.model == "large-v3")
        // The local sort order is kept: the backup's 0 would collide with Hyper.
        #expect(kept.sortOrder == notesSort)
        #expect(persistence.fetchAllModes().count == 3)
    }

    // MARK: - The kept row ends as a fresh create would

    /// A local Notes holds a Windows slice from an earlier Windows backup
    /// import. Restoring a backup whose Notes has no foreign slices (an older
    /// macOS or a Linux one) must clear it, as the delete-and-re-create did,
    /// or `BackupMode(from:)` writes the stale slice into every later export.
    @MainActor
    @Test func aStaleForeignSliceIsClearedWhenTheBackupRowHasNone() throws {
        let persistence = PersistenceController(inMemory: true)
        try seedLocalStore(persistence)
        let notes = try #require(persistence.fetchAllModes().first { $0.name == "Notes" })
        let notesId = try #require(notes.id)
        let notesObject = notes.objectID
        notes.foreignPlatformExtensions = #"{"windows":{"hotkey":"Ctrl+Alt+N"}}"#
        try persistence.container.viewContext.save()

        let backup = try Self.backupMode(id: notesId, name: "Notes")
        #expect(backup.foreignPlatformExtensions == nil)
        _ = try persistence.importBackupStore(
            modes: [backup], modeResolution: .replace,
            vocabulary: nil, vocabularyResolution: .skip
        )

        let kept = try #require(persistence.fetchAllModes().first { $0.id == notesId })
        #expect(kept.objectID == notesObject)
        #expect(kept.foreignPlatformExtensions == nil)
        // The v2 export of the kept row carries no `windows` slice.
        let exported = UniversalModeDTO(from: BackupMode(from: kept))
        let slices = try #require(exported.platformExtensions?.objectValue)
        #expect(slices["windows"] == nil)
    }

    /// The same clear for a same-name row with ANOTHER id (no id match, so
    /// only the in-place target reaches `createOrUpdateMode`).
    @MainActor
    @Test func aStaleForeignSliceIsClearedOnASameNameRowWithAnotherId() throws {
        let persistence = PersistenceController(inMemory: true)
        try seedLocalStore(persistence)
        let notes = try #require(persistence.fetchAllModes().first { $0.name == "Notes" })
        notes.foreignPlatformExtensions = #"{"linux":{"x":1}}"#
        try persistence.container.viewContext.save()

        let backupId = UUID()
        _ = try persistence.importBackupStore(
            modes: [try Self.backupMode(id: backupId, name: "Notes")], modeResolution: .replace,
            vocabulary: nil, vocabularyResolution: .skip
        )

        let kept = try #require(persistence.fetchAllModes().first { $0.id == backupId })
        #expect(kept.foreignPlatformExtensions == nil)
    }

    /// A backup row that DOES carry a foreign slice still writes it onto the
    /// kept row, replacing the local one.
    @MainActor
    @Test func aBackupRowsForeignSliceReplacesTheLocalOne() throws {
        let persistence = PersistenceController(inMemory: true)
        try seedLocalStore(persistence)
        let notes = try #require(persistence.fetchAllModes().first { $0.name == "Notes" })
        let notesId = try #require(notes.id)
        notes.foreignPlatformExtensions = #"{"windows":{"old":true}}"#
        try persistence.container.viewContext.save()

        var backup = try Self.backupMode(id: notesId, name: "Notes")
        backup.foreignPlatformExtensions = #"{"linux":{"new":true}}"#
        _ = try persistence.importBackupStore(
            modes: [backup], modeResolution: .replace,
            vocabulary: nil, vocabularyResolution: .skip
        )

        let kept = try #require(persistence.fetchAllModes().first { $0.id == notesId })
        #expect(kept.foreignPlatformExtensions == #"{"linux":{"new":true}}"#)
    }

    /// A fresh create starts with `isDefault == false`; the kept row must not
    /// carry a local default flag the backup row does not have. Here the local
    /// default is Notes, the backup's Notes is not the default and the backup
    /// names no default, so the repair promotes the first mode in order
    /// (Hyper, sortOrder 0) exactly as it did for the re-created row.
    @MainActor
    @Test func aLocalDefaultFlagIsNotKeptWhenTheBackupRowIsNotTheDefault() throws {
        let persistence = PersistenceController(inMemory: true)
        try seedLocalStore(persistence)
        let notes = try #require(persistence.fetchAllModes().first { $0.name == "Notes" })
        let notesId = try #require(notes.id)
        DefaultModePolicy.apply(to: persistence.fetchAllModes(), preferred: notesId)
        try persistence.container.viewContext.save()
        #expect(notes.isDefault)

        _ = try persistence.importBackupStore(
            modes: [try Self.backupMode(id: notesId, name: "Notes")], modeResolution: .replace,
            vocabulary: nil, vocabularyResolution: .skip
        )

        let modes = persistence.fetchAllModes()
        let kept = try #require(modes.first { $0.id == notesId })
        #expect(kept.isDefault == false)
        #expect(modes.filter(\.isDefault).count == 1)
        let hyper = try #require(modes.first { $0.id == SeededModeValues.seededID })
        #expect(hyper.isDefault)
    }

    // MARK: - A same-name row with another id

    @MainActor
    @Test func aSameNameRowWithAnotherIdIsUpdatedInPlaceAndTakesTheBackupsId() throws {
        let persistence = PersistenceController(inMemory: true)
        try seedLocalStore(persistence)
        let notes = try #require(persistence.fetchAllModes().first { $0.name == "Notes" })
        let oldId = try #require(notes.id)
        let notesObject = notes.objectID
        let notesSort = notes.sortOrder

        let backupId = UUID()
        let backup = try Self.backupMode(id: backupId, name: "notes", sortOrder: 7)
        let result = try persistence.importBackupStore(
            modes: [backup], modeResolution: .replace,
            vocabulary: nil, vocabularyResolution: .skip
        )

        #expect(result.modesImported == 1)
        let modes = persistence.fetchAllModes()
        #expect(modes.count == 3)
        #expect(modes.contains { $0.id == oldId } == false)
        let kept = try #require(modes.first { $0.id == backupId })
        #expect(kept.objectID == notesObject)
        #expect(kept.name == "notes")
        #expect(kept.sortOrder == notesSort)
    }

    @MainActor
    @Test func duplicateSameNameBackupRowsStillMeanLastOneWins() throws {
        let persistence = PersistenceController(inMemory: true)
        try seedLocalStore(persistence)
        let notes = try #require(persistence.fetchAllModes().first { $0.name == "Notes" })
        let notesObject = notes.objectID

        let lastId = UUID()
        let backup = [
            try Self.backupMode(id: UUID(), name: "Notes", model: "small"),
            try Self.backupMode(id: lastId, name: "NOTES", model: "medium"),
        ]
        let result = try persistence.importBackupStore(
            modes: backup, modeResolution: .replace,
            vocabulary: nil, vocabularyResolution: .skip
        )

        #expect(result.modesImported == 2)
        let matches = persistence.fetchAllModes().filter { $0.name?.lowercased() == "notes" }
        #expect(matches.count == 1)
        let kept = try #require(matches.first)
        #expect(kept.id == lastId)
        #expect(kept.model == "medium")
        #expect(kept.objectID == notesObject)
        #expect(persistence.fetchAllModes().count == 3)
    }

    // MARK: - One in-place rule, however the counterpart was found

    /// The seeded flag belongs to the seeded id. A backup from an install
    /// where the seeded mode was renamed "Work" and a user mode "Hyper" (id U)
    /// was made: the local seeded "Hyper" is a same-name row with ANOTHER id
    /// when backup "Hyper" comes first, so it takes U and must drop
    /// `isSystemProvided`, as a create would. In either order no row but the
    /// seeded id may carry the flag.
    @MainActor
    @Test(arguments: [true, false])
    func theSeededFlagNeverLandsOnAUserModeThatSharesItsName(userHyperFirst: Bool) throws {
        let persistence = PersistenceController(inMemory: true)
        try seedLocalStore(persistence)
        let seededObject = try #require(
            persistence.fetchAllModes().first { $0.id == SeededModeValues.seededID }
        ).objectID

        let userHyperId = UUID()
        let work = try Self.backupMode(id: SeededModeValues.seededID, name: "Work", isDefault: true)
        let userHyper = try Self.backupMode(id: userHyperId, name: "Hyper", sortOrder: 3)
        let result = try persistence.importBackupStore(
            modes: userHyperFirst ? [userHyper, work] : [work, userHyper], modeResolution: .replace,
            vocabulary: nil, vocabularyResolution: .skip
        )

        #expect(result.modesImported == 2)
        let modes = persistence.fetchAllModes()
        let hyper = try #require(modes.first { $0.id == userHyperId })
        #expect(hyper.name == "Hyper")
        #expect(hyper.isSystemProvided == false)
        for mode in modes where mode.isSystemProvided {
            #expect(mode.id == SeededModeValues.seededID)
        }
        let workRow = try #require(modes.first { $0.id == SeededModeValues.seededID })
        #expect(workRow.name == "Work")
        #expect(workRow.isDefault)
        #expect(modes.filter(\.isDefault).count == 1)
        if userHyperFirst {
            // The local seeded row was the same-name counterpart of user Hyper.
            #expect(hyper.objectID == seededObject)
        } else {
            // The seeded row matched "Work" by id and keeps its flag.
            #expect(workRow.objectID == seededObject)
            #expect(workRow.isSystemProvided)
        }
    }

    /// Two backup rows share id X: "Notes" (a name match for local Notes, id
    /// Y, which takes X in place) then "Brand". The second row must update
    /// that same object — found in Swift, not by an `id ==` predicate fetch
    /// that may miss the unsaved id change — and not create a second row with
    /// id X.
    @MainActor
    @Test func aSecondBackupRowWithTheSameIdUpdatesTheRowAnEarlierRowReIded() throws {
        let persistence = PersistenceController(inMemory: true)
        try seedLocalStore(persistence)
        let notes = try #require(persistence.fetchAllModes().first { $0.name == "Notes" })
        let notesObject = notes.objectID

        let sharedId = UUID()
        let result = try persistence.importBackupStore(
            modes: [
                try Self.backupMode(id: sharedId, name: "Notes", model: "small"),
                try Self.backupMode(id: sharedId, name: "Brand", model: "medium"),
            ],
            modeResolution: .replace,
            vocabulary: nil, vocabularyResolution: .skip
        )

        #expect(result.modesImported == 2)
        let modes = persistence.fetchAllModes()
        #expect(modes.count == 3)
        let withId = modes.filter { $0.id == sharedId }
        #expect(withId.count == 1)
        let kept = try #require(withId.first)
        #expect(kept.objectID == notesObject)
        #expect(kept.name == "Brand")
        #expect(kept.model == "medium")
        #expect(modes.contains { $0.name == "Notes" } == false)
    }

    /// The local default Notes, renamed in the backup and not its default:
    /// a plain id match (no name conflict) drops the local flag exactly as the
    /// same-name case (`aLocalDefaultFlagIsNotKeptWhenTheBackupRowIsNotTheDefault`)
    /// does, so the restored default does not depend on the row's name.
    @MainActor
    @Test func aLocalDefaultFlagIsNotKeptOnARowTheBackupRenamed() throws {
        let persistence = PersistenceController(inMemory: true)
        try seedLocalStore(persistence)
        let notes = try #require(persistence.fetchAllModes().first { $0.name == "Notes" })
        let notesId = try #require(notes.id)
        let notesObject = notes.objectID
        DefaultModePolicy.apply(to: persistence.fetchAllModes(), preferred: notesId)
        try persistence.container.viewContext.save()
        #expect(notes.isDefault)

        _ = try persistence.importBackupStore(
            modes: [try Self.backupMode(id: notesId, name: "Meeting Notes")], modeResolution: .replace,
            vocabulary: nil, vocabularyResolution: .skip
        )

        let modes = persistence.fetchAllModes()
        let kept = try #require(modes.first { $0.id == notesId })
        #expect(kept.objectID == notesObject)
        #expect(kept.name == "Meeting Notes")
        #expect(kept.isDefault == false)
        #expect(modes.filter(\.isDefault).count == 1)
        let hyper = try #require(modes.first { $0.id == SeededModeValues.seededID })
        #expect(hyper.isDefault)
    }

    /// `.skip` keeps its pre-#1479 behaviour for a same-id row with no name
    /// conflict: the row is updated in place (it is not a conflict) and keeps
    /// the local default flag — `.skip` never promised to restore the
    /// backup's default.
    @MainActor
    @Test func skipStillUpdatesARenamedSameIdRowAndKeepsTheLocalDefault() throws {
        let persistence = PersistenceController(inMemory: true)
        try seedLocalStore(persistence)
        let notes = try #require(persistence.fetchAllModes().first { $0.name == "Notes" })
        let notesId = try #require(notes.id)
        let notesObject = notes.objectID
        DefaultModePolicy.apply(to: persistence.fetchAllModes(), preferred: notesId)
        try persistence.container.viewContext.save()

        let result = try persistence.importBackupStore(
            modes: [try Self.backupMode(id: notesId, name: "Meeting Notes", model: "small")],
            modeResolution: .skip,
            vocabulary: nil, vocabularyResolution: .skip
        )

        #expect(result.modesImported == 1)
        #expect(result.modesSkipped == 0)
        let modes = persistence.fetchAllModes()
        #expect(modes.count == 3)
        let kept = try #require(modes.first { $0.id == notesId })
        #expect(kept.objectID == notesObject)
        #expect(kept.name == "Meeting Notes")
        #expect(kept.model == "small")
        #expect(kept.isDefault)
        #expect(modes.filter(\.isDefault).count == 1)
    }

    // MARK: - New modes and the other resolutions

    @MainActor
    @Test func aNewModeInTheBackupStillAppends() throws {
        let persistence = PersistenceController(inMemory: true)
        try seedLocalStore(persistence)
        let before = shape(persistence)
        let maxBefore = try #require(persistence.fetchAllModes().map(\.sortOrder).max())

        let newId = UUID()
        var backup = try exportModes(persistence)
        backup.append(try Self.backupMode(id: newId, name: "Brand New", sortOrder: 0))

        let result = try persistence.importBackupStore(
            modes: backup, modeResolution: .replace,
            vocabulary: nil, vocabularyResolution: .skip
        )

        #expect(result.modesImported == 4)
        var after = shape(persistence)
        let removed = after.removeValue(forKey: newId)
        let fresh = try #require(removed)
        #expect(fresh.sortOrder == maxBefore + 1)
        #expect(fresh.isSystemProvided == false)
        #expect(after == before)
    }

    @MainActor
    @Test func keepBothStillCreatesACopyOfEveryConflict() throws {
        let persistence = PersistenceController(inMemory: true)
        try seedLocalStore(persistence)
        let before = shape(persistence)
        let backup = try exportModes(persistence)

        let result = try persistence.importBackupStore(
            modes: backup, modeResolution: .keepBoth,
            vocabulary: nil, vocabularyResolution: .skip
        )

        #expect(result.modesImported == 3)
        #expect(result.modeIdRemap.count == 3)
        let after = shape(persistence)
        #expect(after.count == 6)
        // The originals are untouched.
        for (id, row) in before {
            #expect(after[id] == row)
        }
        for (_, copyId) in result.modeIdRemap {
            let copy = try #require(after[copyId])
            #expect(copy.name?.hasSuffix("(imported)") == true)
            #expect(copy.isSystemProvided == false)
            #expect(copy.isDefault == false)
        }
    }

    @MainActor
    @Test func skipStillLeavesEveryConflictAlone() throws {
        let persistence = PersistenceController(inMemory: true)
        try seedLocalStore(persistence)
        let before = shape(persistence)
        let backup = try exportModes(persistence)

        let result = try persistence.importBackupStore(
            modes: backup, modeResolution: .skip,
            vocabulary: nil, vocabularyResolution: .skip
        )

        #expect(result.modesImported == 0)
        #expect(result.modesSkipped == 3)
        #expect(shape(persistence) == before)
    }
}
