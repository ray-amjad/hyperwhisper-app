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

    /// Issue #356 Decision B. Pins the published seven and the wording of the
    /// hint a decode failure sends, both of which a client reads.
    ///
    /// It does NOT pin the equivalence with `ModeDTO` — restating the same seven
    /// literals cannot do that. `modeDTORequiresExactlyTheSharedRequiredModeKeys`
    /// derives that from the type.
    @Test func theSharedRequiredModeKeysAreTheOnesModeDTORequires() {
        #expect(
            localApiRequiredModeKeys() == [
                "name",
                "preset",
                "language",
                "model",
                "punctuation",
                "capitalization",
                "profanityFilter",
            ],
            "the shared required-key list no longer matches ModeDTO's non-optional properties"
        )

        // The hint `create` sends on a decode failure lists the same set, and a
        // client reads that hint to fix its body.
        for key in localApiRequiredModeKeys() {
            #expect(
                "Required: name, preset, language, model, punctuation, capitalization, profanityFilter. See /modes GET for the full shape."
                    .contains(key),
                "the decode-failure hint does not name the required key '\(key)'"
            )
        }
    }

    /// Issue #356 item 2, review round 1. The equivalence between
    /// `REQUIRED_MODE_KEYS` and `ModeDTO`, derived from `ModeDTO` itself rather
    /// than restating the seven literals a third time.
    ///
    /// A property of `ModeDTO` is required exactly when its declared type is not
    /// an `Optional`, which `Mirror` reports on the runtime value. So this fails
    /// if a key is added to the crate's list and not to `ModeDTO` (macOS would
    /// then be the head that no longer refuses it in the decoder), and it fails
    /// if `ModeDTO` gains a non-optional property the crate does not list (macOS
    /// would then refuse a body the other two heads accept).
    @Test func modeDTORequiresExactlyTheSharedRequiredModeKeys() throws {
        // Fixture, not an assertion: the smallest body `ModeDTO` accepts. If
        // `ModeDTO` gains a required property, this decode throws and the test
        // fails before the comparison below runs — which is also a real answer.
        let body = Data(#"""
        {"name":"N","preset":"hyper","language":"en","model":"base",
         "punctuation":true,"capitalization":true,"profanityFilter":false}
        """#.utf8)
        let dto = try LocalAPIResponder.decoder.decode(ModeDTO.self, from: body)

        let required = Mirror(reflecting: dto).children.compactMap { child -> String? in
            guard let label = child.label else { return nil }
            return Mirror(reflecting: child.value).displayStyle == .optional ? nil : label
        }

        #expect(
            Set(required) == Set(localApiRequiredModeKeys()),
            """
            ModeDTO's non-optional properties \(Set(required).sorted()) and the shared \
            required-key list \(Set(localApiRequiredModeKeys()).sorted()) have drifted
            """
        )
    }

    /// Issue #356 item 2, review round 1. `create` derives the present-key set
    /// from the body it was handed, so the shared required-key rule is actually
    /// evaluated on this head. A body that is not a JSON object has no keys to
    /// classify and stays the protocol failure it always was.
    @Test func topLevelKeysComeFromTheBodyAndNotFromTheRequiredList() {
        #expect(
            ModesEndpoint.topLevelKeys(in: Data(#"{"name":"Only"}"#.utf8)) == ["name"]
        )
        #expect(
            ModesEndpoint.topLevelKeys(in: Data("{}".utf8)) == []
        )
        #expect(ModesEndpoint.topLevelKeys(in: Data("[1,2,3]".utf8)) == nil)
        #expect(ModesEndpoint.topLevelKeys(in: Data("not json".utf8)) == nil)

        // The rule this hook exists to feed: `{"name":"Only"}` is missing six of
        // the seven, and the shared validator says so by name.
        let failure = localApiValidateMode(input: HwLocalApiModeValidationInput(
            operation: .create,
            presentKeys: ModesEndpoint.topLevelKeys(in: Data(#"{"name":"Only"}"#.utf8)) ?? [],
            name: nil,
            language: nil,
            preset: nil,
            postProcessingMode: nil,
            sortOrder: nil,
            userSystemPrompt: nil,
            geminiCustomPrompt: nil,
            customVocabulary: nil
        ))
        #expect(failure != nil, "a create body carrying only `name` must fail the shared required-key rule")
        #expect(failure?.message.contains("preset") == true)
        // HTTP 200, not 400 (issue #356, review round 1). `openapi.yaml`'s
        // `info.description` reserves 4xx for malformed JSON, a bad bearer token
        // and a rejected origin; a well-formed object that is merely incomplete
        // is a business failure. All three heads now answer this identically —
        // before this round macOS answered 400 "Invalid JSON body" for it.
        #expect(failure?.httpStatus == 200)
    }

    /// Issue #356 Decisions C and D, from the macOS side of the FFI. The
    /// endpoint wiring cannot be exercised here — `create`/`patch` reach
    /// `PersistenceController.shared` — so this pins the contract the wiring
    /// hands over: the same range `int16Value` has always applied, and a
    /// comparison key that still matches what `ModeNamePolicy.normalized`
    /// produces.
    @Test func theSharedModeContractMatchesThisHeadsOwnRules() {
        let base = HwLocalApiModeValidationInput(
            operation: .patch,
            presentKeys: [],
            name: nil,
            language: nil,
            preset: nil,
            postProcessingMode: nil,
            sortOrder: nil,
            userSystemPrompt: nil,
            geminiCustomPrompt: nil,
            customVocabulary: nil
        )
        var atMax = base
        atMax.sortOrder = Int64(Int16.max)
        var overMax = base
        overMax.sortOrder = Int64(Int16.max) + 1
        var atMin = base
        atMin.sortOrder = Int64(Int16.min)
        var underMin = base
        underMin.sortOrder = Int64(Int16.min) - 1
        #expect(localApiValidateMode(input: atMax) == nil && localApiValidateMode(input: atMin) == nil)
        #expect(localApiValidateMode(input: overMax) != nil && localApiValidateMode(input: underMin) != nil)
        #expect(
            localApiValidateMode(input: overMax)?.message
                == "Mode 'sortOrder' must be between \(Int16.min) and \(Int16.max)",
            "the shared sortOrder message drifted from the one this head shipped"
        )

        // The NFC pre-step runs FIRST and stays native; the shared key is what
        // decides "the same name" after it.
        let normalized = ModesEndpoint.normalizedName("  Cafe\u{301}  ")
        #expect(normalized == "Café")
        #expect(localApiModeNameConflict(candidate: normalized ?? "", otherNames: ["CAFÉ"]))
        #expect(!localApiModeNameConflict(candidate: normalized ?? "", otherNames: ["Work"]))

        let taken = localApiModeNameTakenFailure(name: "Work", operation: .create)
        #expect(taken.code == .modeNameTaken && taken.httpStatus == 200)
        #expect(taken.message == "A mode named 'Work' already exists")
        #expect(taken.hint != nil)
        #expect(
            localApiModeNameTakenFailure(name: "Work", operation: .patch).hint == nil,
            "the patch collision grew a hint this head has never sent"
        )
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
