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
//  wrong when the record is mutated in place.
//
//  The fix re-keys preparation onto the Mode's CONTENT, and onto TWO keys
//  rather than one, because the two halves of preparation read disjoint fields:
//
//    - `ASRPreparationKey` (modeId, model, language) drives `prepareModel`.
//    - `LocalRuntimePreparationKey` (modeId, postProcessingMode,
//      postProcessingProvider, languageModel) drives `prepareLocalRuntime`.
//
//  Half these tests are about the split rather than about #318 directly: a
//  fused key would re-run the whole ASR preparation — including its
//  unconditional `cancelTranscription()` — for an edit that only touched
//  post-processing, and could not see a language edit at all.
//
//  The rest are about what content keys made newly REACHABLE. Under the old id
//  key, an in-place edit of the already-selected Mode never reached either
//  prepare function, so neither could run against a Mode that was, at that
//  moment, mid-dictation. Now both can, and neither is safe there —
//  `ModePreparationGate` is what refuses, and the pipeline's `state` observer
//  is what replays afterwards.
//
//  WHAT THESE TESTS DO NOT PROVE
//
//  They do not prove a model loaded. This target cannot construct a
//  `TranscriptionPipeline`, so on the `AppState` built in
//  `onboardingSourceCommitRepublishesChangedPreparationKeys` below,
//  `appState.transcriptionPipeline` stays `nil` and the production sinks'
//  `Task { … }` bodies are no-ops. What is proven is that the TRIGGERS fire
//  with new, different keys across an in-place source commit that leaves the
//  mode id untouched — the exact emission the old id-keyed `removeDuplicates()`
//  ate. That each sink then calls its own half of preparation is pinned
//  statically by `preparationSinksAreKeyedOnTheirOwnContentTriggers`.
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

    /// The heart of #318, asserted directly on the key types: the same Mode row,
    /// rewritten in place from the cloud source to an on-device one, keeps its
    /// UUID but must NOT compare equal. An id-keyed `removeDuplicates()` cannot
    /// tell these two states apart; a content-keyed one must.
    @MainActor
    @Test func inPlaceSourceRewriteKeepsTheIdButChangesTheKeys() throws {
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
        let beforeSnapshot = ModeSnapshot(cloudMode)
        let beforeASR = try #require(ASRPreparationKey(beforeSnapshot))
        let beforeRuntime = try #require(LocalRuntimePreparationKey(beforeSnapshot))

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
        let afterSnapshot = ModeSnapshot(onDeviceMode)
        let afterASR = try #require(ASRPreparationKey(afterSnapshot))
        let afterRuntime = try #require(LocalRuntimePreparationKey(afterSnapshot))

        // Same row, same UUID — this is what made the id-keyed trigger fatal.
        #expect(onDeviceMode.objectID == cloudMode.objectID)
        #expect(afterASR.modeId == beforeASR.modeId)
        #expect(afterASR.modeId == id.uuidString)

        // …and yet a different preparation input on both halves, so both keys
        // must differ: the source moved cloud → on-device AND post-processing
        // moved cloud → off.
        #expect(afterASR != beforeASR)
        #expect(beforeASR.model == "cloud")
        #expect(afterASR.model == "parakeet-tdt-0.6b-v2")
        #expect(afterRuntime != beforeRuntime)
        #expect(beforeRuntime.postProcessingMode == 1)
        #expect(afterRuntime.postProcessingMode == 0)
    }

    /// The other half of the contract: the keys must be narrow enough that a
    /// rename does not re-run preparation and tear down a resident model.
    /// `name` is deliberately in neither key, so rewriting the same row with a
    /// new name and identical settings yields two EQUAL keys.
    @MainActor
    @Test func renamingTheSelectedModeLeavesBothPreparationKeysEqual() throws {
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
        let beforeSnapshot = ModeSnapshot(original)
        let beforeASR = try #require(ASRPreparationKey(beforeSnapshot))
        let beforeRuntime = try #require(LocalRuntimePreparationKey(beforeSnapshot))

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
        let afterSnapshot = ModeSnapshot(renamed)

        #expect(renamed.name == "Meeting notes")
        #expect(ASRPreparationKey(afterSnapshot) == beforeASR)
        #expect(LocalRuntimePreparationKey(afterSnapshot) == beforeRuntime)
    }

    // MARK: - The two halves of preparation have disjoint inputs

    /// A language edit is an ASR-preparation input and nothing else.
    ///
    /// `prepareModel` feeds `extractLanguage(from:)` into
    /// `preloadExclusively(_:language:preferEnglishOptimized:)`, which swaps to
    /// a physically different weights file for English-only whisper models. Before
    /// this key carried `language`, `ModeSnapshot` did not carry it either, so a
    /// language-only edit to the selected Mode was structurally invisible to
    /// preparation and English-only weights stayed resident against non-English
    /// audio. `prepareLocalRuntime` reads none of it, so its key must not move.
    @MainActor
    @Test func aLanguageEditMovesOnlyTheASRKey() throws {
        let persistence = PersistenceController(inMemory: true)
        let id = UUID(uuidString: "00000000-0000-0000-0000-000000000003")!

        let mode = persistence.createOrUpdateMode(
            id: id,
            name: "Default",
            preset: "hyper",
            language: "en",
            model: "base",
            punctuation: true,
            capitalization: true,
            profanityFilter: false,
            cloudProvider: nil,
            postProcessingMode: 0
        )
        let englishSnapshot = ModeSnapshot(mode)
        let englishASR = try #require(ASRPreparationKey(englishSnapshot))
        let englishRuntime = try #require(LocalRuntimePreparationKey(englishSnapshot))
        #expect(englishASR.language != nil)

        let edited = persistence.createOrUpdateMode(
            id: id,
            name: "Default",
            preset: "hyper",
            language: "fr",
            model: "base",
            punctuation: true,
            capitalization: true,
            profanityFilter: false,
            cloudProvider: nil,
            postProcessingMode: 0
        )
        let frenchSnapshot = ModeSnapshot(edited)
        let frenchASR = try #require(ASRPreparationKey(frenchSnapshot))

        #expect(frenchASR.modeId == englishASR.modeId)
        #expect(frenchASR.model == englishASR.model)
        #expect(frenchASR.language != englishASR.language)
        #expect(frenchASR != englishASR)

        // …and the local runtime, which reads none of this, does not re-run.
        #expect(LocalRuntimePreparationKey(frenchSnapshot) == englishRuntime)
    }

    /// `ASRPreparationKey` normalizes the language through the same function
    /// preparation does — `ModeSnapshot.effectiveLanguage(_:)`, lowercased with
    /// `"auto"` collapsed onto `nil` — so the key moves when, and only when, the
    /// value that actually reaches a provider moves.
    ///
    /// Both halves are asserted on purpose. The literals pin today's RULE. The
    /// comparisons against `effectiveLanguage` pin that the key gets its answer
    /// from that function rather than from its own copy of the rule: harden the
    /// normalizer later — trim whitespace, treat `""` as unset — and these stay
    /// true, where a second copy would have drifted and silently stopped
    /// re-preparing. `preparationReadsTheLanguageThroughTheOneSharedNormalizer`
    /// below pins the other three callers.
    ///
    /// Written straight onto the managed object rather than through
    /// `createOrUpdateMode`, which canonicalizes the code on the way in. The
    /// point here is the key's own normalization, not the write path's.
    @MainActor
    @Test func theASRKeyNormalizesTheLanguageTheWayPreparationDoes() throws {
        let persistence = PersistenceController(inMemory: true)
        let mode = persistence.createOrUpdateMode(
            id: UUID(uuidString: "00000000-0000-0000-0000-000000000004")!,
            name: "Default",
            preset: "hyper",
            language: "en",
            model: "base",
            punctuation: true,
            capitalization: true,
            profanityFilter: false,
            cloudProvider: nil,
            postProcessingMode: 0
        )

        mode.language = "Auto"
        let autoKey = try #require(ASRPreparationKey(ModeSnapshot(mode)))
        #expect(autoKey.language == nil)
        #expect(autoKey.language == ModeSnapshot.effectiveLanguage(mode.language))

        mode.language = "EN"
        let englishKey = try #require(ASRPreparationKey(ModeSnapshot(mode)))
        #expect(englishKey.language == "en")
        #expect(englishKey.language == ModeSnapshot.effectiveLanguage(mode.language))
    }

    /// The rule that turns `"auto"` into `nil` has exactly one copy, and the key
    /// and the three preparation/transcription paths all read it from there.
    ///
    /// This is what makes the assertions above more than a coincidence. The key
    /// is a CACHE KEY over the value `extractLanguage(from:)` hands a provider:
    /// if one copy of the rule were hardened and the key's were not, a Mode
    /// whose language is `" auto"` or `""` would get a changed effective
    /// language while the key compared equal, `removeDuplicates()` would swallow
    /// the emission, and `prepareModel` would never re-run — #318 recreated on
    /// the language axis. Both `extractLanguage(from:)` copies are `private` and
    /// live on different types, so nothing but this can observe that they agree.
    @Test func preparationReadsTheLanguageThroughTheOneSharedNormalizer() throws {
        let extractors = [
            "app/macos/hyperwhisper/Managers/Transcription/Coordinators/TranscriptionModelManager.swift",
            "app/macos/hyperwhisper/Managers/Transcription/Coordinators/TranscriptionProviderRouter.swift"
        ]
        for path in extractors {
            let body = try ProductionSource.slice(
                of: path,
                from: "private func extractLanguage(from mode: Mode?) -> String? {",
                to: "}"
            )
            #expect(body.contains("ModeSnapshot.effectiveLanguage(mode?.language)"))
            #expect(!body.contains("lowercased()"))
            #expect(!body.contains("\"auto\""))
        }

        let transcriptionArgument = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/Transcription/Pipeline/TranscriptionPipeline+Transcription.swift",
            from: "let languageArg: String? =",
            to: "\n"
        )
        #expect(transcriptionArgument.contains("ModeSnapshot.effectiveLanguage(mode?.language)"))

        let key = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Models/AppState.swift",
            from: "struct ASRPreparationKey",
            to: "struct LocalRuntimePreparationKey"
        )
        #expect(key.contains("ModeSnapshot.effectiveLanguage(snapshot.language)"))
        #expect(!key.contains("lowercased()"))
        #expect(!key.contains("\"auto\""))
    }

    /// The symmetric case, and the reason the keys are split at all: a
    /// post-processing-only edit must leave the ASR key untouched.
    ///
    /// `prepareModel` reads none of `postProcessingMode`,
    /// `postProcessingProvider` or `languageModel`, yet it bumps
    /// `preparationGeneration`, calls `cancelTranscription()` unconditionally,
    /// stats the model on disk and writes `modelReadyState = .loading(name:)` —
    /// a pulsing dot and a literal "(Loading…)" in the very window the user is
    /// editing in, and, because its only bail-out covers `.transcribing` and not
    /// `.postProcessing`, a cancelled dictation if the save lands while one is
    /// being cleaned up. A fused key would run all of that here.
    @MainActor
    @Test func aPostProcessingEditMovesOnlyTheLocalRuntimeKey() throws {
        let persistence = PersistenceController(inMemory: true)
        let id = UUID(uuidString: "00000000-0000-0000-0000-000000000005")!

        let cloudPostProcessing = persistence.createOrUpdateMode(
            id: id,
            name: "Default",
            preset: "hyper",
            language: "en",
            model: "parakeet-tdt-0.6b-v2",
            punctuation: true,
            capitalization: true,
            profanityFilter: false,
            cloudProvider: nil,
            postProcessingMode: 1
        )
        let beforeSnapshot = ModeSnapshot(cloudPostProcessing)
        let beforeASR = try #require(ASRPreparationKey(beforeSnapshot))
        let beforeRuntime = try #require(LocalRuntimePreparationKey(beforeSnapshot))

        // Same transcription source; only post-processing moves, cloud → local.
        let localPostProcessing = persistence.createOrUpdateMode(
            id: id,
            name: "Default",
            preset: "hyper",
            language: "en",
            model: "parakeet-tdt-0.6b-v2",
            punctuation: true,
            capitalization: true,
            profanityFilter: false,
            cloudProvider: nil,
            postProcessingMode: 2
        )
        let afterSnapshot = ModeSnapshot(localPostProcessing)
        let afterRuntime = try #require(LocalRuntimePreparationKey(afterSnapshot))

        #expect(afterRuntime != beforeRuntime)
        #expect(afterRuntime.postProcessingMode == 2)

        // The ASR half must not move — this is the assertion that fails if the
        // two keys are ever fused back into one.
        #expect(ASRPreparationKey(afterSnapshot) == beforeASR)
    }

    // MARK: - The regression, through the production publishers

    /// The #318 regression itself, driven through the REAL committer and
    /// observed on the REAL publishers `AppState.setupSubscriptions()`
    /// subscribes to. On pre-fix code there are no such publishers at all, and
    /// the emission this asserts was swallowed by
    /// `$selectedModeId.removeDuplicates()`.
    ///
    /// `PersistenceController(inMemory: true)` does not seed the default Mode
    /// (`initializeDefaultModes()` is gated on `!inMemory`), so the test creates
    /// it and flags `isDefault` — otherwise `apply`'s `findDefaultMode()` returns
    /// nil and takes the create-new branch instead of the in-place branch that
    /// causes the bug.
    ///
    /// **Do not add an `await` or a yield anywhere in this test.** Everything it
    /// asserts is already synchronous: `apply` ends in
    /// `appState.selectMode(updated, persist: true)`, which writes
    /// `selectedModeSnapshot` on the spot. What a yield would let in is NOT
    /// harmless. Constructing `AppState()` starts `refreshSelectedModeSnapshot`
    /// on the seeded id, and that back-fill reads
    /// `PersistenceController.shared` — the REAL on-disk store, not the
    /// in-memory controller this test injects. The seeded id
    /// (`00000000-…-0001`) is both `selectedModeId`'s default and the id
    /// `initializeDefaultModes()` writes to the shared store, so the staleness
    /// guard in `refreshSelectedModeSnapshot` PASSES and the on-disk row's
    /// snapshot is published — a third, different key from a store this test
    /// never wrote. The counts below would then fail for a reason that has
    /// nothing to do with #318.
    @MainActor
    @Test func onboardingSourceCommitRepublishesChangedPreparationKeys() throws {
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
        // state. Each subscription gets its own `removeDuplicates` state — these
        // publishers are deliberately not shared — which is fine:
        // `preparationSinksAreKeyedOnTheirOwnContentTriggers` below is what pins
        // that production subscribes to these same stored publishers.
        var asrKeys: [ASRPreparationKey?] = []
        var runtimeKeys: [LocalRuntimePreparationKey?] = []
        let asrCancellable = appState.asrPreparationTrigger.sink { asrKeys.append($0) }
        let runtimeCancellable = appState.localRuntimePreparationTrigger.sink { runtimeKeys.append($0) }
        defer {
            asrCancellable.cancel()
            runtimeCancellable.cancel()
        }

        #expect(asrKeys.count == 1)
        #expect(asrKeys.first??.model == "cloud")
        #expect(runtimeKeys.count == 1)
        #expect(runtimeKeys.first??.postProcessingMode == 1)

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

        // …and the fix: both content-keyed triggers still emitted, because this
        // commit changes an input on both halves — the source moved cloud →
        // on-device (the #318 symptom) and post-processing moved cloud → off.
        #expect(asrKeys.count == 2)
        let firstASR = try #require(asrKeys.first ?? nil)
        let secondASR = try #require(asrKeys.last ?? nil)
        #expect(secondASR != firstASR)
        #expect(secondASR.modeId == firstASR.modeId)
        #expect(secondASR.model == "parakeet-tdt-0.6b-v2")

        #expect(runtimeKeys.count == 2)
        let firstRuntime = try #require(runtimeKeys.first ?? nil)
        let secondRuntime = try #require(runtimeKeys.last ?? nil)
        #expect(secondRuntime != firstRuntime)
        #expect(secondRuntime.modeId == firstRuntime.modeId)
        #expect(secondRuntime.postProcessingMode == 0)
    }

    // MARK: - Preparation must not run while a dictation is in flight

    /// The gate that decides whether preparation may run at all, asserted
    /// directly on its truth table.
    ///
    /// `.postProcessing` is the case this exists for, and the one a reasonable
    /// reader gets wrong: `prepareModel`'s own bail-out covers `.transcribing`
    /// only, and it then falls through to an unconditional
    /// `cancelTranscription()`. `TranscriptionPipeline.currentTask` spans BOTH
    /// transcription and post-processing, so cancelling it while the AI cleanup
    /// is running loses the enhancement — and, on the rethrowing branch, the
    /// transcript. `prepareLocalRuntime` is worse: it reads no state at all and
    /// falls through to a `SIGTERM` of the llama-server that a local
    /// post-processing pass is mid-request against.
    ///
    /// `nil` — no pipeline wired yet — must ALLOW preparation. At launch the
    /// sinks fire before `bootstrapAppServices()` wires the pipeline; deferring
    /// there would queue work that nothing is left to replay.
    @Test func preparationIsRefusedWhileTheTranscriptionPipelineIsBusy() {
        #expect(ModePreparationGate.allowsPreparation(during: nil))
        #expect(ModePreparationGate.allowsPreparation(during: .idle))
        #expect(ModePreparationGate.allowsPreparation(during: .error(message: "boom")))

        #expect(!ModePreparationGate.allowsPreparation(during: .transcribing(provider: "Cloud", progress: 0.5)))
        #expect(!ModePreparationGate.allowsPreparation(during: .postProcessing))
    }

    // MARK: - Each key really drives its own half, and nothing runs mid-dictation

    /// Backstop for the wiring the test above can only observe indirectly, and
    /// the piece that survives if that test ever has to be dropped: without it,
    /// "the key types behave correctly" and "each key drives its own half" are
    /// two different claims.
    ///
    /// This reads production Swift text, which is the weakest tool in this
    /// target (see `ProductionSource.swift`'s header) — justified here only
    /// because the subscriptions it describes cannot be observed from a test:
    /// they are private, they are installed inside `init()`, and their bodies
    /// need a `TranscriptionPipeline` this target cannot build.
    ///
    /// `ProductionSource.code(of:)` strips comment lines first, so a doc comment
    /// mentioning `$selectedModeId` cannot satisfy or break any assertion here.
    @Test func preparationSinksAreKeyedOnTheirOwnContentTriggers() throws {
        let appStatePath = "app/macos/hyperwhisper/Models/AppState.swift"

        // 1. Both triggers are built from the Mode's CONTENT. Two-anchor slices
        //    over the declarations, whose opening anchors are unique in the file.
        let asrDeclaration = try ProductionSource.slice(
            of: appStatePath,
            from: "lazy var asrPreparationTrigger",
            to: ".eraseToAnyPublisher()"
        )
        #expect(asrDeclaration.contains("$selectedModeSnapshot"))
        #expect(asrDeclaration.contains("ASRPreparationKey($0)"))
        #expect(asrDeclaration.contains(".removeDuplicates()"))
        #expect(!asrDeclaration.contains("$selectedModeId"))

        let runtimeDeclaration = try ProductionSource.slice(
            of: appStatePath,
            from: "lazy var localRuntimePreparationTrigger",
            to: ".eraseToAnyPublisher()"
        )
        #expect(runtimeDeclaration.contains("$selectedModeSnapshot"))
        #expect(runtimeDeclaration.contains("LocalRuntimePreparationKey($0)"))
        #expect(runtimeDeclaration.contains(".removeDuplicates()"))
        #expect(!runtimeDeclaration.contains("$selectedModeId"))

        // 2. Each sink records ONLY its own key and then hands off. Neither one
        //    calls a prepare function directly any more: the gate and the
        //    single-fetch ordering live in the shared runner, and a sink that
        //    prepared on its own would bypass both. These are the assertions
        //    that fail if the two halves are ever fused back together.
        let normalized = try normalizedCode(of: appStatePath)

        let asrSink = try subscriptionBody(attachedTo: "asrPreparationTrigger", in: normalized)
        #expect(asrSink.contains("pendingASRPreparationKey = key"))
        #expect(asrSink.contains("scheduleModePreparation()"))
        #expect(!asrSink.contains("pendingLocalRuntimePreparationKey"))
        #expect(!asrSink.contains("prepareModel"))
        #expect(!asrSink.contains("$selectedModeId"))

        let runtimeSink = try subscriptionBody(attachedTo: "localRuntimePreparationTrigger", in: normalized)
        #expect(runtimeSink.contains("pendingLocalRuntimePreparationKey = key"))
        #expect(runtimeSink.contains("scheduleModePreparation()"))
        #expect(!runtimeSink.contains("pendingASRPreparationKey"))
        #expect(!runtimeSink.contains("prepareLocalRuntime"))
        #expect(!runtimeSink.contains("$selectedModeId"))
    }

    /// The half of the gate that a truth table cannot state: a refused
    /// preparation must be DEFERRED, not dropped, and something must replay it.
    ///
    /// Dropping it would trade a cancelled dictation for a status bar that is
    /// permanently stale — #318 again, one room over. So this pins three things
    /// in production text:
    ///
    ///   1. the scheduler consults the gate, and clears no pending key when the
    ///      gate refuses (the keys ARE the queue),
    ///   2. the runner asks the gate again after its background fetch — which
    ///      suspends, and a dictation can start inside that window — and hands
    ///      the claimed work back rather than dropping it, and
    ///   3. `TranscriptionPipeline`'s own `state` observer calls back into
    ///      `AppState` when it returns to a state that can accept preparation,
    ///      which is the only thing that ever replays a deferred key.
    ///
    /// Source text is the last resort here for the same reason as above: this
    /// target cannot build a `TranscriptionPipeline`, so `AppState`'s gate can
    /// only ever see `nil` in a test, and `nil` is the branch that allows.
    @Test func aRefusedPreparationIsHeldAndReplayedWhenThePipelineGoesIdle() throws {
        let appStatePath = "app/macos/hyperwhisper/Models/AppState.swift"

        // 1. The scheduler gates, and holds.
        let scheduler = try ProductionSource.slice(
            of: appStatePath,
            from: "private func scheduleModePreparation() {",
            to: "func resumeModePreparationIfPending"
        )
        #expect(scheduler.contains("canRunModePreparationNow()"))
        #expect(!scheduler.contains("pendingASRPreparationKey = nil"))
        #expect(!scheduler.contains("pendingLocalRuntimePreparationKey = nil"))

        // 2. The runner re-checks after the fetch and requeues what it claimed.
        //    The closing anchor is the declaration that follows the runner, so a
        //    reorder throws `anchorNotFound` rather than quietly asserting less.
        let runner = try ProductionSource.slice(
            of: appStatePath,
            from: "private func runPendingModePreparation() async {",
            to: "private func requeueModePreparation"
        )
        #expect(runner.contains("canRunModePreparationNow()"))
        #expect(runner.contains("requeueModePreparation("))
        #expect(runner.contains("await transcriptionPipeline?.prepareModel(for: mode)"))
        #expect(runner.contains("await transcriptionPipeline?.prepareLocalRuntime(for: mode)"))
        // One fetch for both halves — the whole reason the two sinks share a
        // runner instead of each resolving the Mode themselves.
        #expect(runner.components(separatedBy: "fetchModeInBackground").count == 2)

        // 3. The replay exists, and hangs off the pipeline's own state observer.
        let pipelinePath = "app/macos/hyperwhisper/Managers/Transcription/Pipeline/TranscriptionPipeline.swift"
        let stateObserver = try ProductionSource.slice(
            of: pipelinePath,
            from: "@Published var state: TranscriptionState = .idle {",
            to: "@Published var availableModels"
        )
        #expect(stateObserver.contains("state_isReadyForTranscription()"))
        #expect(stateObserver.contains("appState?.resumeModePreparationIfPending()"))
    }

    /// A production file with comments stripped and every run of whitespace
    /// collapsed to a single space.
    ///
    /// The collapse is the point. An earlier version of this test bounded the
    /// sink by walking backwards over contiguous non-blank lines, which made the
    /// assertion depend on blank lines in `AppState.swift`: deleting one silently
    /// widened the slice to swallow the neighbouring subscription while both
    /// expectations still passed. Normalizing first means the anchors below are
    /// the only thing that bounds anything.
    private func normalizedCode(of repoRelativePath: String) throws -> String {
        try ProductionSource.code(of: repoRelativePath)
            .split(whereSeparator: { $0.isWhitespace })
            .joined(separator: " ")
    }

    /// The subscription hanging directly off `trigger`, from `<trigger> .sink`
    /// to the `.store(in: &cancellables)` that closes it.
    ///
    /// Two anchors, both required, both on normalized text. The opening anchor
    /// demands that `.sink` follow the trigger name immediately, so it cannot
    /// match the property declaration (which is followed by a type annotation)
    /// and cannot match a chain with an operator spliced in between. A rename of
    /// either anchor throws `anchorNotFound` rather than quietly matching less.
    private func subscriptionBody(attachedTo trigger: String, in normalized: String) throws -> String {
        let file = "AppState.swift"
        let opening = "\(trigger) .sink"
        guard let start = normalized.range(of: opening) else {
            throw ProductionSource.Failure.anchorNotFound(anchor: opening, file: file)
        }
        let rest = normalized[start.upperBound...]
        guard let end = rest.range(of: ".store(in: &cancellables)") else {
            throw ProductionSource.Failure.anchorNotFound(
                anchor: ".store(in: &cancellables) closing the \(trigger) subscription",
                file: file
            )
        }
        return String(rest[..<end.lowerBound])
    }
}
