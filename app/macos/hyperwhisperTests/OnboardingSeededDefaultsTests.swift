//
//  OnboardingSeededDefaultsTests.swift
//  hyperwhisperTests
//
//  `LiveOnboardingSourceCommitter.apply` does not only RECONFIGURE the default
//  mode — it CREATES it, whenever onboarding runs before
//  `initializeDefaultModes()` has, whenever the flagged default mode was deleted
//  while other modes remained (both delete guards only protect the LAST mode),
//  or whenever the seeder's save failed. The row it creates is permanent:
//  `initializeDefaultModes()` returns early once ANY mode exists, so it will
//  never be corrected.
//
//  Two rounds of bugs have come out of that one sentence:
//
//    * the create path carried pre-seed literals — `language: existing?.language
//      ?? "en"` while the shared seed says `"auto"`, so a user who reached
//      onboarding first got a mode pinned to English; and
//    * it created the row through `createOrUpdateMode`, whose CREATE branch
//      hardcodes `isSystemProvided = false` and `sortOrder = maxSortOrder + 1`
//      against the seed's `true` and `0`, so `GET /modes` reported that
//      install's default as `isSystemProvided: false` at sortOrder 1.
//
//  This file used to assert neither. It compared `LiveOnboardingSourceCommitter.seed`
//  against a Core Data row written FROM that same seed — true by construction,
//  and reverting `language` to `?? "en"` left every assertion passing. It said
//  so in its own header, and explained that `apply` needs an `AppState` whose
//  construction instantiates `PersistenceController.shared`, the real on-disk
//  store. That explained the difficulty; it did not make the file a guard.
//
//  So the code moved instead of the test: the Core Data half of `apply` is now
//  `LiveOnboardingSourceCommitter.commitStagedSource(_:to:)`, a static function
//  with no `AppState`, and these tests drive it against an in-memory store —
//  including on the create path, which is the one that was never covered.
//

import CoreData
import Foundation
import Testing

@testable import HyperWhisper

@MainActor
struct OnboardingSeededDefaultsTests {

    /// What onboarding stages when the user picks HyperWhisper Cloud — the only
    /// one of the three options that turns post-processing on, and so the one
    /// that touches the most seeded columns.
    private static let cloudSource = OnboardingStagedSource(
        source: .hyperwhisperCloud,
        model: "cloud",
        cloudProvider: "hyperwhisper",
        postProcessingMode: 1,
        cloudAccuracyTier: CloudAccuracyTier.elevenLabsScribeV2.rawValue
    )

    /// The on-device option: a local model and post-processing OFF. Included
    /// because it is the staged source that overwrites the most seeded values,
    /// so it is where a "seeded" field that is really a staged one would show.
    private static let onDeviceSource = OnboardingStagedSource(
        source: .onDevice,
        model: "base",
        cloudProvider: nil,
        postProcessingMode: 0,
        cloudAccuracyTier: nil
    )

    // MARK: - The create path

    @Test("onboarding creates the default mode exactly as the seeder would")
    func creatingTheDefaultModeMatchesTheSeeder() throws {
        // An empty store — onboarding reached before `initializeDefaultModes()`.
        let onboarded = PersistenceController(inMemory: true)
        LiveOnboardingSourceCommitter.commitStagedSource(Self.cloudSource, to: onboarded)

        // The same store, seeded the normal way, for comparison.
        let seeded = PersistenceController(inMemory: true)
        seeded.initializeDefaultModes()

        let created = try #require(onboarded.fetchAllModes().first)
        let reference = try #require(seeded.fetchAllModes().first)

        #expect(onboarded.fetchAllModes().count == 1, "onboarding created more than one mode")

        // The two fields the CREATE branch of `createOrUpdateMode` gets wrong.
        // `GET /modes` reports both.
        #expect(created.isSystemProvided == reference.isSystemProvided)
        #expect(created.isSystemProvided, "the install's own default mode was reported as user-created")
        #expect(created.sortOrder == reference.sortOrder)
        #expect(created.sortOrder == 0, "the default mode was pushed to the end of the list")

        // The seeded values that are not staged.
        //
        // Be precise about what this catches. Restoring the PRE-FIX shape —
        // `createOrUpdateMode(language: existing?.language ?? "en", …)` with no
        // row resolved first — fails here on `language`, `isSystemProvided` and
        // `sortOrder` together. Changing the surviving `?? Self.seed.language`
        // arm on its own does not, and cannot: once the row is resolved through
        // the seeder it always HAS a language, so that arm is now only about
        // Core Data columns being optional in Swift. The bug class was removed
        // rather than merely asserted against.
        #expect(created.id == reference.id)
        #expect(created.name == reference.name)
        #expect(created.preset == reference.preset)
        #expect(created.language == reference.language)
        #expect(created.language == "auto")
        #expect(created.isDefault == reference.isDefault)
        #expect(created.isDefault, "onboarding left the chosen source on a non-default mode")
        #expect(created.englishSpelling == reference.englishSpelling)
        #expect(created.customInstructions == reference.customInstructions)
        #expect(created.punctuation == reference.punctuation)
        #expect(created.capitalization == reference.capitalization)
        #expect(created.profanityFilter == reference.profanityFilter)
        #expect(created.postProcessingProvider == reference.postProcessingProvider)
        #expect(created.postProcessingProvider == PostProcessingProvider.hyperwhisper.storageValue)
        #expect(created.cloudPostProcessingModel == reference.cloudPostProcessingModel)

        // Staged, so these are the user's choice and NOT the seed's.
        #expect(created.model == "cloud")
        #expect(created.cloudProvider == "hyperwhisper")
        #expect(created.postProcessingMode == 1)
    }

    @Test("the created mode is the built-in mode, not a user mode")
    func theCreatedModeIsRecognisedAsBuiltIn() throws {
        let persistence = PersistenceController(inMemory: true)
        LiveOnboardingSourceCommitter.commitStagedSource(Self.cloudSource, to: persistence)

        let created = try #require(persistence.fetchAllModes().first)
        #expect(created.isSeededDefault)
        #expect(created.id == SeededModeValues.seededID)
    }

    /// The reachable path the finding named: the flagged default was deleted
    /// while other modes remained, so the store is NOT empty and
    /// `initializeDefaultModes()` will never act on it again.
    @Test("a store with other modes but no default still gets a conformant one")
    func creatingAlongsideExistingModes() throws {
        let persistence = PersistenceController(inMemory: true)
        let mine = persistence.createOrUpdateMode(
            name: "My own mode",
            preset: "hyper",
            language: "fr",
            model: "base",
            punctuation: false,
            capitalization: false,
            profanityFilter: true
        )
        #expect(persistence.findDefaultMode() == nil, "the fixture must not be flagged default")

        LiveOnboardingSourceCommitter.commitStagedSource(Self.cloudSource, to: persistence)

        let modes = persistence.fetchAllModes()
        #expect(modes.count == 2, "onboarding must not disturb the user's own modes")
        let created = try #require(modes.first(where: { $0.id == SeededModeValues.seededID }))

        // `createOrUpdateMode` would have written maxSortOrder + 1 here, which
        // is what put the install's default below the user's own mode.
        #expect(created.sortOrder == 0)
        #expect(created.isSystemProvided)
        #expect(created.isDefault)
        #expect(created.language == "auto")

        // The user's mode is untouched.
        #expect(mine.language == "fr")
        #expect(!mine.isSystemProvided)
        #expect(!mine.isDefault)
    }

    @Test("the on-device source is committed onto a conformant row too")
    func theOnDeviceSourceAlsoCreatesAConformantRow() throws {
        let persistence = PersistenceController(inMemory: true)
        LiveOnboardingSourceCommitter.commitStagedSource(Self.onDeviceSource, to: persistence)

        let created = try #require(persistence.fetchAllModes().first)
        // Staged: a local model with post-processing off.
        #expect(created.model == "base")
        #expect(created.postProcessingMode == 0)
        // Seeded: unchanged by the choice of source.
        #expect(created.isSystemProvided)
        #expect(created.sortOrder == 0)
        #expect(created.language == "auto")
        #expect(created.name == SeededModeValues.seededName)
    }

    // MARK: - The reconfigure path

    /// The common path, and the one that must NOT change: a default mode already
    /// exists, so onboarding rewrites its source and nothing else.
    @Test("an existing default mode is reconfigured, not replaced")
    func anExistingDefaultModeIsReconfigured() throws {
        let persistence = PersistenceController(inMemory: true)
        persistence.initializeDefaultModes()
        let before = try #require(persistence.fetchAllModes().first)
        let beforeID = try #require(before.id)
        let beforeCreated = before.createdDate

        LiveOnboardingSourceCommitter.commitStagedSource(Self.onDeviceSource, to: persistence)

        let modes = persistence.fetchAllModes()
        #expect(modes.count == 1, "onboarding created a second default mode")
        let after = try #require(modes.first)
        #expect(after.id == beforeID)
        #expect(after.createdDate == beforeCreated, "the row was recreated rather than updated")
        // Reconfigured.
        #expect(after.model == "base")
        #expect(after.postProcessingMode == 0)
        // Still the seeded row in every other respect.
        #expect(after.isSystemProvided)
        #expect(after.sortOrder == 0)
        #expect(after.isDefault)
        #expect(after.language == "auto")
    }

    /// A row carrying the well-known UUID but not flagged default is ADOPTED,
    /// not duplicated. `createOrUpdateMode(id:)` used to do this implicitly by
    /// fetching on id; the explicit lookup keeps that behaviour now that the
    /// row is resolved before it is written.
    @Test("a row with the well-known id is adopted rather than duplicated")
    func aRowWithTheWellKnownIdIsAdopted() throws {
        let persistence = PersistenceController(inMemory: true)
        let orphan = persistence.createOrUpdateMode(
            id: SeededModeValues.seededID,
            name: "Hyper",
            preset: "hyper",
            language: "auto",
            model: "cloud",
            punctuation: true,
            capitalization: true,
            profanityFilter: false
        )
        #expect(!orphan.isDefault)

        LiveOnboardingSourceCommitter.commitStagedSource(Self.cloudSource, to: persistence)

        let modes = persistence.fetchAllModes()
        #expect(modes.count == 1, "a second row with the well-known id was created")
        #expect(modes.first?.isDefault == true, "the adopted row was not flagged default")
    }

    // MARK: - The seed itself

    @Test("onboarding falls back to exactly what the seeder writes")
    func onboardingFallbacksMatchTheSeededRow() throws {
        // `PersistenceController(inMemory: true)` deliberately does not seed from
        // `init`, so the seeder is called explicitly — same as DefaultModeSeedTests.
        let persistence = PersistenceController(inMemory: true)
        persistence.initializeDefaultModes()

        let seeded = try #require(persistence.fetchAllModes().first)
        let fallbacks = LiveOnboardingSourceCommitter.seed

        // The Core-Data-optionality fallbacks `commitStagedSource` still carries.
        #expect(fallbacks.name == seeded.name)
        #expect(fallbacks.preset == seeded.preset)
        #expect(fallbacks.language == seeded.language)

        // `captureRestorePoint` also falls back to `model`. Note this is the
        // shared seed's `providerType` mapped onto macOS' `mode.model` column
        // (see SeededModeValues) — "cloud", not a Whisper catalog name.
        #expect(fallbacks.model == seeded.model)
    }

    @Test("the well-known default mode id is the seed's own id")
    func theCommitterLooksUpTheModeTheSeederWrote() {
        // `commitStagedSource` and `captureRestorePoint` both fall back to this
        // id when no default mode is found, so a drift here would have
        // onboarding create a SECOND row rather than adopt the seeded one.
        #expect(LiveOnboardingSourceCommitter.defaultModeID == LiveOnboardingSourceCommitter.seed.id)
        #expect(LiveOnboardingSourceCommitter.defaultModeID == SeededModeValues.seededID)
    }

    @Test("the pre-seed literals are not what onboarding would create")
    func theOldLiteralsAreGone() {
        let fallbacks = LiveOnboardingSourceCommitter.seed

        // The exact literals this fix removed. Asserting the positive value too,
        // so the test says what the answer IS and not only what it is not.
        #expect(fallbacks.language == LanguageData.automaticCode)
        #expect(fallbacks.language == "auto")
        #expect(fallbacks.language != "en")

        #expect(fallbacks.name == "Hyper")
        #expect(fallbacks.name != "Default")

        #expect(fallbacks.model != "base")

        // `preset` was already the seed's value spelled as a literal — it reads
        // from the seed now purely so the two cannot drift apart later.
        #expect(fallbacks.preset == "hyper")
    }

    @Test("onboarding and the seeder agree on the cloud post-processing provider")
    func theProviderTokenIsTheCanonicalOne() {
        #expect(LiveOnboardingSourceCommitter.seed.postProcessingProvider
            == PostProcessingProvider.hyperwhisper.storageValue)
    }

    // MARK: - What is still NOT covered here
    //
    // `captureRestorePoint` and `restore` are not exercised: both read or write
    // `appState.selectedModeSnapshot`, so they cannot be reached without the
    // `AppState` this file exists to avoid. Their `??` arms are inert by
    // inspection — they yield a value only when `existing == nil`, which is
    // `modeExisted == false`, and on that path `restore` deletes the row rather
    // than writing any field back — but "by inspection" is what it says, and no
    // assertion below stands behind it. Splitting them the way
    // `commitStagedSource` was split would need the snapshot lifted out of
    // `AppState` first, which is a wider change than this round.
}
