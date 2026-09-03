//
//  ModePreparationKeyTests.swift
//  hyperwhisperTests
//
//  Pins issue #318 — after macOS onboarding commits a transcription source, the
//  main window's bottom status bar kept advertising the PREVIOUS source until
//  the app was relaunched.
//
//  The committed Mode was always correct; only the indicator lied.
//  `LiveOnboardingSourceCommitter.apply` reconfigures the seeded Default Mode
//  IN PLACE and re-selects it with the same UUID, so `AppState.selectedModeId`
//  never changes value. The model-preparation sink was keyed
//  `$selectedModeId.removeDuplicates()`, which swallowed the emission, so
//  `prepareModel`/`prepareLocalRuntime` never re-ran and
//  `TranscriptionModelManager.modelReadyState` — what `ModelStatusBar` renders —
//  kept its pre-onboarding value. `removeDuplicates()` on an identity key is
//  wrong when the record is mutated in place. The fix re-keys that sink onto
//  `AppState.modePreparationTrigger`, a content key over exactly the Mode
//  fields preparation consumes.
//
//  WHAT THESE TESTS DO NOT PROVE
//
//  They do not prove a model loaded. This target cannot construct a
//  `TranscriptionPipeline`, so on the `AppState` built in
//  `onboardingSourceCommitRepublishesAChangedPreparationKey` below,
//  `appState.transcriptionPipeline` stays `nil` and the production sink's
//  `Task { … }` is a no-op. What is proven is that the TRIGGER fires with a new,
//  different key across an in-place source commit that leaves the mode id
//  untouched — the exact emission the old id-keyed `removeDuplicates()` ate.
//  That the sink then calls `prepareModel`/`prepareLocalRuntime` is pinned
//  statically by `preparationSinkIsKeyedOnTheContentTrigger`.
//
//  They also deliberately say nothing about the status bar's local-runtime
//  indicator. It is repaired by the same change, but it is NOT observably stale
//  in the #318 flow: onboarding's staged sources only ever write
//  `postProcessingMode` 0 or 1 (never 2 / `.local`), and onboarding only runs on
//  a launch that seeded the default Mode, whose row is `postProcessingMode: 1`
//  with the llama server stopped. Asserting it here would be theatre.
//

import Combine
import CoreData
import Foundation
import Testing
@testable import HyperWhisper

struct ModePreparationKeyTests {

    // MARK: - The key is a value, not an identity

    /// The heart of #318, asserted directly on the key type: the same Mode row,
    /// rewritten in place from the cloud source to an on-device one, keeps its
    /// UUID but must NOT compare equal. An id-keyed `removeDuplicates()` cannot
    /// tell these two states apart; a content-keyed one must.
    @MainActor
    @Test func inPlaceSourceRewriteKeepsTheIdButChangesTheKey() throws {
        let persistence = PersistenceController(inMemory: true)
        let id = UUID(uuidString: "00000000-0000-0000-0000-000000000001")!

        let cloudMode = persistence.createOrUpdateMode(
            id: id,
            name: "Default",
            preset: "hyper",
            language: "en",
            model: "cloud",
            punctuation: true,
            capitalization: true,
            profanityFilter: false,
            cloudProvider: "hyperwhisper",
            postProcessingMode: 1
        )
        let before = try #require(ModePreparationKey(ModeSnapshot(cloudMode)))

        let onDeviceMode = persistence.createOrUpdateMode(
            id: id,
            name: "Default",
            preset: "hyper",
            language: "en",
            model: "parakeet-tdt-0.6b-v2",
            punctuation: true,
            capitalization: true,
            profanityFilter: false,
            cloudProvider: nil,
            postProcessingMode: 0
        )
        let after = try #require(ModePreparationKey(ModeSnapshot(onDeviceMode)))

        // Same row, same UUID — this is what made the id-keyed trigger fatal.
        #expect(onDeviceMode.objectID == cloudMode.objectID)
        #expect(after.modeId == before.modeId)
        #expect(after.modeId == id.uuidString)

        // …and yet a different preparation input, so the key must differ.
        #expect(after != before)
        #expect(before.model == "cloud")
        #expect(after.model == "parakeet-tdt-0.6b-v2")
        #expect(before.postProcessingMode == 1)
        #expect(after.postProcessingMode == 0)
    }

    /// The other half of the contract: the key must be narrow enough that a
    /// rename does not re-run preparation and tear down a resident model. `name`
    /// is deliberately excluded from `ModePreparationKey`, so rewriting the same
    /// row with a new name and identical source settings yields an EQUAL key.
    @MainActor
    @Test func renamingTheSelectedModeLeavesThePreparationKeyEqual() throws {
        let persistence = PersistenceController(inMemory: true)
        let id = UUID(uuidString: "00000000-0000-0000-0000-000000000002")!

        let original = persistence.createOrUpdateMode(
            id: id,
            name: "Default",
            preset: "hyper",
            language: "en",
            model: "parakeet-tdt-0.6b-v2",
            punctuation: true,
            capitalization: true,
            profanityFilter: false,
            cloudProvider: nil,
            postProcessingMode: 0
        )
        let before = try #require(ModePreparationKey(ModeSnapshot(original)))

        let renamed = persistence.createOrUpdateMode(
            id: id,
            name: "Meeting notes",
            preset: "hyper",
            language: "en",
            model: "parakeet-tdt-0.6b-v2",
            punctuation: true,
            capitalization: true,
            profanityFilter: false,
            cloudProvider: nil,
            postProcessingMode: 0
        )
        let after = try #require(ModePreparationKey(ModeSnapshot(renamed)))

        #expect(renamed.name == "Meeting notes")
        #expect(after == before)
    }

    // MARK: - The regression, through the production publisher

    /// The #318 regression itself, driven through the REAL committer and
    /// observed on the REAL publisher `AppState.setupSubscriptions()` subscribes
    /// to. On pre-fix code there is no such publisher at all, and the emission
    /// this asserts was swallowed by `$selectedModeId.removeDuplicates()`.
    ///
    /// `PersistenceController(inMemory: true)` does not seed the default Mode
    /// (`initializeDefaultModes()` is gated on `!inMemory`), so the test creates
    /// it and flags `isDefault` — otherwise `apply`'s `findDefaultMode()` returns
    /// nil and takes the create-new branch instead of the in-place branch that
    /// causes the bug.
    ///
    /// Everything is asserted synchronously: `apply` ends in
    /// `appState.selectMode(updated, persist: true)`, which writes
    /// `selectedModeSnapshot` on the spot, so the emission has already happened
    /// by the time `apply` returns. Do not add an `await` here — the only thing
    /// a yield would let in is the asynchronous snapshot back-fill, which
    /// re-emits an equal key and is deduped anyway.
    @MainActor
    @Test func onboardingSourceCommitRepublishesAChangedPreparationKey() throws {
        let persistence = PersistenceController(inMemory: true)
        let defaultModeID = LiveOnboardingSourceCommitter.defaultModeID

        // The fresh-install seed shape: cloud source, cloud post-processing.
        let seeded = persistence.createOrUpdateMode(
            id: defaultModeID,
            name: "Default",
            preset: "hyper",
            language: "en",
            model: "cloud",
            punctuation: true,
            capitalization: true,
            profanityFilter: false,
            cloudProvider: "hyperwhisper",
            postProcessingMode: 1
        )
        seeded.isDefault = true
        persistence.save()

        let appState = AppState()
        // `settingsManager` is nil on a bare AppState, so `persist:` is a no-op
        // and nothing reaches UserDefaults. Keep it that way.
        appState.selectMode(seeded, persist: false)
        let idBefore = appState.selectedModeId
        #expect(idBefore == defaultModeID.uuidString)

        // Subscribing replays the current key, so element 0 is the pre-commit
        // state. This subscription gets its own `removeDuplicates` state, which
        // is fine — `preparationSinkIsKeyedOnTheContentTrigger` below is what
        // pins that production subscribes to this same stored publisher.
        var keys: [ModePreparationKey?] = []
        let cancellable = appState.modePreparationTrigger.sink { keys.append($0) }
        defer { cancellable.cancel() }

        #expect(keys.count == 1)
        #expect(keys.first??.model == "cloud")

        let committer = LiveOnboardingSourceCommitter(
            persistence: persistence,
            appState: appState
        )
        committer.apply(
            OnboardingStagedSource(
                source: .onDevice,
                model: "parakeet-tdt-0.6b-v2",
                cloudProvider: nil,
                postProcessingMode: 0,
                cloudAccuracyTier: nil
            )
        )

        // The fact that made `removeDuplicates()` on the id fatal: the commit
        // rewrote the Default Mode in place and re-selected the same UUID.
        #expect(appState.selectedModeId == idBefore)

        // …and the fix: the content-keyed trigger still emitted.
        #expect(keys.count == 2)
        let first = try #require(keys.first ?? nil)
        let second = try #require(keys.last ?? nil)
        #expect(second != first)
        #expect(second.modeId == first.modeId)
        #expect(second.model == "parakeet-tdt-0.6b-v2")
        #expect(second.postProcessingMode == 0)
    }

    // MARK: - The sink really is keyed on the trigger

    /// Backstop for the wiring the test above can only observe indirectly, and
    /// the piece that survives if that test ever has to be dropped: without it,
    /// "the key type behaves correctly" and "the preparation sink uses it" are
    /// two different claims.
    ///
    /// This reads production Swift text, which is the weakest tool in this
    /// target (see `ProductionSource.swift`'s header) — justified here only
    /// because the subscription it describes cannot be observed from a test:
    /// the sink is private, it is installed inside `init()`, and its body needs
    /// a `TranscriptionPipeline` this target cannot build.
    ///
    /// `ProductionSource.code(of:)` strips comment lines first, so a doc comment
    /// mentioning `$selectedModeId` cannot satisfy or break either assertion.
    @Test func preparationSinkIsKeyedOnTheContentTrigger() throws {
        let appStatePath = "app/macos/hyperwhisper/Models/AppState.swift"

        // 1. The trigger is built from the Mode's CONTENT.
        let declaration = try ProductionSource.slice(
            of: appStatePath,
            from: "modePreparationTrigger",
            to: ".eraseToAnyPublisher()"
        )
        #expect(declaration.contains("$selectedModeSnapshot"))
        #expect(declaration.contains("ModePreparationKey($0)"))
        #expect(declaration.contains(".removeDuplicates()"))
        #expect(!declaration.contains("$selectedModeId"))

        // 2. The preparation sink subscribes to THAT publisher, not to the id.
        //    Located from its own log line rather than by position, so
        //    reordering the subscriptions in `setupSubscriptions()` cannot make
        //    this pass or fail for the wrong reason.
        let chain = try preparationSinkPublisherChain(in: appStatePath)
        #expect(chain.contains("modePreparationTrigger"))
        #expect(!chain.contains("$selectedModeId"))
    }

    /// The publisher chain the model-preparation sink is attached to: every
    /// contiguous non-blank line ending at the `.sink {` that owns the
    /// "Mode changed:" log line.
    private func preparationSinkPublisherChain(in repoRelativePath: String) throws -> String {
        let file = URL(fileURLWithPath: repoRelativePath).lastPathComponent
        let lines = try ProductionSource.code(of: repoRelativePath)
            .components(separatedBy: .newlines)

        guard let logIndex = lines.firstIndex(where: { $0.contains("Mode changed:") }) else {
            throw ProductionSource.Failure.anchorNotFound(
                anchor: "Mode changed:", file: file
            )
        }
        guard let sinkIndex = lines[..<logIndex].lastIndex(where: { $0.contains(".sink {") }) else {
            throw ProductionSource.Failure.anchorNotFound(
                anchor: ".sink { preceding the model-preparation log line", file: file
            )
        }

        var headIndex = sinkIndex
        while headIndex > 0,
              !lines[headIndex - 1].trimmingCharacters(in: .whitespaces).isEmpty {
            headIndex -= 1
        }
        return lines[headIndex...sinkIndex].joined(separator: "\n")
    }
}
