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
}
