//
//  ModesEndpointTests.swift
//  hyperwhisperTests
//


import Foundation
import CoreData
import Testing
@testable import HyperWhisper

struct ModesEndpointTests {
    @Test func emptyNameIsRejected() {
        #expect(ModesEndpoint.normalizedName("") == nil)
    }

    @Test func whitespaceNameIsRejected() {
        #expect(ModesEndpoint.normalizedName("   ") == nil)
    }

    @Test func whitespaceAndNewlinesNameIsRejected() {
        #expect(ModesEndpoint.normalizedName(" \n\t\r ") == nil)
    }

    @Test func paddedNameIsNormalized() {
        #expect(ModesEndpoint.normalizedName("  Work\n") == "Work")
    }

    @Test func invisibleOnlyNamesAreRejected() {
        #expect(ModesEndpoint.normalizedName("\u{200B}") == nil)
        #expect(ModesEndpoint.normalizedName("\u{2060}\u{FEFF}") == nil)
        #expect(ModesEndpoint.normalizedName("\u{0301}\u{FE0F}") == nil)
    }

    @Test func invisibleBoundaryCharactersAreTrimmed() {
        #expect(ModesEndpoint.normalizedName("\u{200B}  Work \u{2060}") == "Work")
    }

    @Test func nameNormalizationIsCanonicalAndGraphemeSafe() {
        #expect(ModesEndpoint.normalizedName("Cafe\u{301}") == "Café")
        #expect(ModesEndpoint.normalizedName("\u{301}Work\u{200B}") == "Work")
        #expect(ModesEndpoint.normalizedName("👩‍💻 Notes") == "👩‍💻 Notes")
    }

    @Test func modeIntegerFieldsUseExactInt16Conversion() {
        #expect(ModesEndpoint.int16Value(Int(Int16.min)) == Int16.min)
        #expect(ModesEndpoint.int16Value(Int(Int16.max)) == Int16.max)
        #expect(ModesEndpoint.int16Value(Int(Int16.min) - 1) == nil)
        #expect(ModesEndpoint.int16Value(Int(Int16.max) + 1) == nil)
    }

    @Test func postProcessingModeAcceptsOnlyDefinedRawValues() {
        #expect(ModesEndpoint.postProcessingModeValue(0) == 0)
        #expect(ModesEndpoint.postProcessingModeValue(1) == 1)
        #expect(ModesEndpoint.postProcessingModeValue(2) == 2)
        #expect(ModesEndpoint.postProcessingModeValue(-1) == nil)
        #expect(ModesEndpoint.postProcessingModeValue(3) == nil)
        #expect(ModesEndpoint.postProcessingModeValue(Int(Int16.max)) == nil)
    }

    @Test func omittedNullablePatchFieldStaysUnchanged() throws {
        let patch = try JSONDecoder().decode(ModePatchDTO.self, from: Data("{}".utf8))

        guard case .omitted = patch.$customInstructions else {
            Issue.record("An omitted customInstructions key must stay omitted")
            return
        }
    }

    @Test func explicitNullNullablePatchFieldCanClearStoredValue() throws {
        let json = #"{"customInstructions":null}"#
        let patch = try JSONDecoder().decode(ModePatchDTO.self, from: Data(json.utf8))

        guard case .value(let value) = patch.$customInstructions else {
            Issue.record("An explicit customInstructions null must be present")
            return
        }
        #expect(value == nil)
    }

    @Test func nullablePatchFieldDecodesAReplacementValue() throws {
        let json = #"{"customInstructions":"Use short sentences"}"#
        let patch = try JSONDecoder().decode(ModePatchDTO.self, from: Data(json.utf8))

        guard case .value(let value) = patch.$customInstructions else {
            Issue.record("A customInstructions value must be present")
            return
        }
        #expect(value == "Use short sentences")
    }

    @Test func everyNullableModeFieldDistinguishesOmittedFromNull() throws {
        let omitted = try JSONDecoder().decode(ModePatchDTO.self, from: Data("{}".utf8))
        let cleared = try JSONDecoder().decode(
            ModePatchDTO.self,
            from: Data(
                """
                {
                  "customInstructions": null,
                  "userSystemPrompt": null,
                  "languageModel": null,
                  "cloudTranscriptionModel": null,
                  "cloudTranscriptionDomain": null,
                  "cloudProvider": null,
                  "postProcessingProvider": null,
                  "englishSpelling": null,
                  "cloudAccuracyTier": null,
                  "geminiCustomPrompt": null,
                  "cloudPostProcessingModel": null
                }
                """.utf8
            )
        )

        let omittedStates = [
            omitted.$customInstructions, omitted.$userSystemPrompt,
            omitted.$languageModel, omitted.$cloudTranscriptionModel,
            omitted.$cloudTranscriptionDomain, omitted.$cloudProvider,
            omitted.$postProcessingProvider, omitted.$englishSpelling,
            omitted.$cloudAccuracyTier, omitted.$geminiCustomPrompt,
            omitted.$cloudPostProcessingModel,
        ]
        let clearedStates = [
            cleared.$customInstructions, cleared.$userSystemPrompt,
            cleared.$languageModel, cleared.$cloudTranscriptionModel,
            cleared.$cloudTranscriptionDomain, cleared.$cloudProvider,
            cleared.$postProcessingProvider, cleared.$englishSpelling,
            cleared.$cloudAccuracyTier, cleared.$geminiCustomPrompt,
            cleared.$cloudPostProcessingModel,
        ]

        for state in omittedStates {
            guard case .omitted = state else {
                Issue.record("Every omitted nullable Mode field must stay omitted")
                continue
            }
        }
        for state in clearedStates {
            guard case .value(nil) = state else {
                Issue.record("Every explicit null nullable Mode field must decode as a clear")
                continue
            }
        }
    }

    @MainActor
    @Test func creationAtMaximumStoredSortOrderDoesNotOverflow() {
        let persistence = PersistenceController(inMemory: true)
        let existing = makeMode(in: persistence)
        existing.sortOrder = .max
        persistence.save()

        let created = makeMode(in: persistence, name: "Second mode")

        #expect(created.sortOrder == .max)
    }

    @MainActor
    @Test func isolatedPatchRollbackPreservesPendingViewContextEdits() throws {
        let persistence = PersistenceController(inMemory: true)
        let mode = makeMode(in: persistence)
        let storedID = mode.id!.uuidString

        mode.name = "Pending UI edit"
        #expect(mode.hasChanges)
        let transaction = ModesEndpoint.mutationContext(for: persistence)
        let request: NSFetchRequest<Mode> = Mode.fetchRequest()
        request.predicate = NSPredicate(format: "id == %@", UUID(uuidString: storedID)! as CVarArg)
        let isolatedMode = try #require(transaction.fetch(request).first)
        isolatedMode.name = "Rejected API edit"
        transaction.rollback()

        #expect(mode.name == "Pending UI edit")
        #expect(mode.hasChanges)
    }

    @MainActor
    @Test func isolatedCreateRollbackPreservesPendingViewContextEdits() {
        let persistence = PersistenceController(inMemory: true)
        let existing = makeMode(in: persistence)
        existing.name = "Pending UI edit"
        let transaction = ModesEndpoint.mutationContext(for: persistence)
        let mode = persistence.createOrUpdateMode(
            name: "Rejected create",
            preset: "hyper",
            language: "en",
            model: "base",
            punctuation: true,
            capitalization: true,
            profanityFilter: false,
            persist: false,
            in: transaction
        )

        #expect(mode.isInserted)
        transaction.rollback()

        #expect(!mode.isInserted)
        #expect(existing.name == "Pending UI edit")
        #expect(existing.hasChanges)
    }

    @MainActor
    @Test func persistenceNormalizesNamesOutsideTheLocalAPI() {
        let persistence = PersistenceController(inMemory: true)

        let padded = makeMode(in: persistence, name: "  Stored name\n")
        let invisible = makeMode(in: persistence, name: "\u{200B}")

        #expect(padded.name == "Stored name")
        #expect(invisible.name == "Untitled")
    }

    @MainActor
    @Test func storedNameRepairIsSafeAndIdempotent() {
        let persistence = PersistenceController(inMemory: true)
        let valid = makeMode(in: persistence, name: "Untitled")
        let blank = makeMode(in: persistence, name: "Temporary")
        let invisible = makeMode(in: persistence, name: "Invisible")
        let padded = makeMode(in: persistence, name: "Padded")
        blank.name = "   "
        invisible.name = "\u{200B}\u{301}"
        padded.name = "\u{200B} Work \u{2060}"
        persistence.save()

        #expect(persistence.repairModeNames() == 3)
        #expect(valid.name == "Untitled")
        #expect(blank.name == "Untitled 2")
        #expect(invisible.name == "Untitled 3")
        #expect(padded.name == "Work")
        #expect(persistence.repairModeNames() == 0)
    }

    @MainActor
    @Test func backupConflictsUseTheFinalNormalizedNameForEveryResolution() {
        let skipStore = PersistenceController(inMemory: true)
        _ = makeMode(in: skipStore, name: "Work")
        let skipBackup = makeBackupMode(name: "\u{200B} Work \u{2060}")
        let skipped = skipStore.importModes([skipBackup], resolution: .skip)
        #expect(skipped.imported == 0)
        #expect(skipped.skipped == 1)

        let replaceStore = PersistenceController(inMemory: true)
        _ = makeMode(in: replaceStore, name: "Work")
        let replaceBackup = makeBackupMode(name: "\u{200B} Work \u{2060}")
        let replaced = replaceStore.importModes([replaceBackup], resolution: .replace)
        #expect(replaced.imported == 1)
        #expect(replaceStore.fetchAllModes().count == 1)
        #expect(replaceStore.fetchAllModes().first?.name == "Work")
        #expect(replaceStore.fetchAllModes().first?.id == replaceBackup.id)

        let keepBothStore = PersistenceController(inMemory: true)
        _ = makeMode(in: keepBothStore, name: "Work")
        let keepBothBackup = makeBackupMode(name: "\u{200B} Work \u{2060}")
        let kept = keepBothStore.importModes([keepBothBackup], resolution: .keepBoth)
        #expect(kept.imported == 1)
        #expect(keepBothStore.fetchAllModes().compactMap(\.name).sorted() == ["Work", "Work (imported)"])
        #expect(kept.idRemap[keepBothBackup.id] != nil)
    }

    @MainActor
    private func makeMode(
        in persistence: PersistenceController,
        name: String = "Stored name",
        persist: Bool = true
    ) -> Mode {
        persistence.createOrUpdateMode(
            name: name,
            preset: "hyper",
            language: "en",
            model: "base",
            punctuation: true,
            capitalization: true,
            profanityFilter: false,
            persist: persist
        )
    }

    private func makeBackupMode(name: String) -> BackupMode {
        BackupMode(
            id: UUID(),
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
            isDefault: false,
            sortOrder: 0,
            cloudAccuracyTier: nil,
            removeTrailingPeriod: nil,
            geminiCustomPrompt: nil,
            cloudPostProcessingModel: nil,
            cloudTranscriptionDomain: nil
        )
    }
}
