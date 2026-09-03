//
//  DefaultModeSeedTests.swift
//  hyperwhisperTests
//
//  First-ever macOS coverage of `PersistenceController.initializeDefaultModes()`.
//
//  Before #285 this path had none, on the head that was hardest to change and
//  where the failure mode is silent: `Mode`'s Core Data attribute defaults are
//  hostile, so a field the seeder stops writing does not come out null, it comes
//  out `"openai"` / `"whisper-1"` / `"claudeHaiku"` / `"en"` / `"base"`. Nothing
//  crashes, nothing logs; the user just gets a mode configured for a provider
//  they never chose. `everyHostileCoreDataDefaultIsOverwritten` is the test that
//  exists specifically to fail on that, so keep it even if it looks redundant
//  next to the exact-value assertions.
//
//  `ModeSeedConformanceVectorTests` covers WHAT the seed says (against the
//  shared vector file, alongside Rust and C#). This file covers what actually
//  lands in Core Data, and the three rules around it:
//
//    * exactly one mode,
//    * seeding never runs twice,
//    * seeding never touches a store that already holds modes — the
//      "existing users are untouched" contract every head preserves.
//
//  `PersistenceController(inMemory: true)` deliberately does NOT seed from
//  `init` (see the `if !inMemory` block), so these tests call the seeder
//  explicitly. That is also why it is internal rather than private.
//

import CoreData
import Foundation
import Testing

@testable import HyperWhisper

@MainActor
struct DefaultModeSeedTests {

    // MARK: - The single seeded mode

    @Test("a fresh store gets exactly one mode")
    func seedsExactlyOneMode() {
        let persistence = PersistenceController(inMemory: true)

        persistence.initializeDefaultModes()

        #expect(persistence.fetchAllModes().count == 1)
        #expect(persistence.didSeedDefaultModesOnLaunch)
    }

    @Test("the seeded mode carries the shared core's values")
    func seededModeMatchesTheSharedSeed() throws {
        let persistence = PersistenceController(inMemory: true)
        persistence.initializeDefaultModes()

        let mode = try #require(persistence.fetchAllModes().first)
        let expected = SeededModeValues.forCurrentRegion

        #expect(mode.id == expected.id)
        #expect(mode.name == expected.name)
        #expect(mode.preset == expected.preset)
        #expect(mode.language == expected.language)
        // C7: the shared seed's `providerType` is macOS' `model` column. macOS
        // has no `providerType`, and `model` is what the cloud/local routing
        // reads — writing "cloud" here is what keeps the seeded mode a cloud one.
        #expect(mode.model == expected.model)
        #expect(mode.cloudProvider == expected.cloudProvider)
        #expect(mode.cloudAccuracyTier == expected.cloudAccuracyTier)
        #expect(mode.cloudTranscriptionModel == expected.cloudTranscriptionModel)
        #expect(mode.postProcessingMode == expected.postProcessingMode)
        #expect(mode.postProcessingProvider == expected.postProcessingProvider)
        #expect(mode.cloudPostProcessingModel == expected.cloudPostProcessingModel)
        #expect(mode.englishSpelling == expected.englishSpelling)
        #expect(mode.punctuation == expected.punctuation)
        #expect(mode.capitalization == expected.capitalization)
        #expect(mode.profanityFilter == expected.profanityFilter)
        #expect(mode.customInstructions == expected.customInstructions)
        #expect(mode.isDefault == expected.isDefault)
        #expect(mode.isSystemProvided == expected.isSystemProvided)
        #expect(mode.sortOrder == expected.sortOrder)
        #expect(mode.createdDate != nil)
        #expect(mode.modifiedDate != nil)
    }

    /// The literals, spelled out once, independent of `SeededModeValues`. The
    /// test above would still pass if the shared core and the macOS translation
    /// drifted together; this one pins the product decision itself.
    @Test("the seeded mode is the agreed one, by value")
    func seededModeIsTheAgreedOne() throws {
        let persistence = PersistenceController(inMemory: true)
        persistence.initializeDefaultModes()

        let mode = try #require(persistence.fetchAllModes().first)

        #expect(mode.id?.uuidString.lowercased() == "00000000-0000-0000-0000-000000000001")
        #expect(mode.name == "Hyper")
        #expect(mode.preset == "hyper")
        #expect(mode.language == "auto")
        #expect(mode.model == "cloud")
        #expect(mode.cloudProvider == "hyperwhisper")
        #expect(mode.cloudAccuracyTier == "elevenLabsScribeV2")
        #expect(mode.cloudTranscriptionModel == "scribe_v2")
        #expect(mode.postProcessingMode == 1)
        #expect(mode.postProcessingProvider == "hyperwhispercloud")
        #expect(mode.cloudPostProcessingModel == "anthropic:claude-haiku-4-5")
        #expect(mode.customInstructions == "")
        #expect(mode.isDefault)
        #expect(mode.isSystemProvided)
        #expect(mode.sortOrder == 0)
        #expect(mode.punctuation)
        #expect(mode.capitalization)
        #expect(!mode.profanityFilter)
        // Region-dependent, so pinned against the same region table the rest of
        // the app seeds from rather than against a literal.
        #expect(mode.englishSpelling == EnglishSpelling.defaultForCurrentRegion.rawValue)
        #expect(mode.englishSpelling?.isEmpty == false)
    }

    // MARK: - The hostile-defaults guard (C6)

    /// Every attribute below has a non-null default in
    /// `HyperWhisper_v30.xcdatamodel`. If the seeder stops writing one, the
    /// value it inherits is a plausible-looking legacy string, not an obvious
    /// blank — which is why this is asserted as "not the default" separately
    /// from "equals the right thing".
    @Test("no seeded field falls through to its Core Data default")
    func everyHostileCoreDataDefaultIsOverwritten() throws {
        let persistence = PersistenceController(inMemory: true)
        persistence.initializeDefaultModes()

        let mode = try #require(persistence.fetchAllModes().first)

        #expect(mode.language != "en", "language inherited the model default")
        #expect(mode.model != "base", "model inherited the model default")
        #expect(mode.cloudProvider != "openai", "cloudProvider inherited the model default")
        #expect(
            mode.postProcessingProvider != "openai",
            "postProcessingProvider inherited the model default")
        #expect(
            mode.cloudTranscriptionModel != "whisper-1",
            "cloudTranscriptionModel inherited the model default")
        #expect(
            mode.cloudPostProcessingModel != "claudeHaiku",
            "cloudPostProcessingModel inherited the legacy single-token default")
        // `customInstructions` has no default at all, so the failure here is a
        // nil rather than a wrong string — still a field the seeder must write.
        #expect(mode.customInstructions != nil, "customInstructions was left nil")
    }

    // MARK: - The macOS readers

    /// The C1 regression guard. macOS learned `"hyperwhispercloud"` in the phase
    /// immediately before this one; if that parser is ever narrowed back,
    /// `AIPostProcessor` starts returning the raw transcript while the UI still
    /// says "HyperWhisper Cloud" — a silent no-op on every fresh install. This
    /// test is the tripwire.
    @Test("the seeded provider reads back as HyperWhisper Cloud")
    func seededProviderIsUnderstood() throws {
        let persistence = PersistenceController(inMemory: true)
        persistence.initializeDefaultModes()

        let mode = try #require(persistence.fetchAllModes().first)
        let stored = try #require(mode.postProcessingProvider)

        #expect(PostProcessingProvider(rawValue: stored) == .hyperwhisper)
        #expect(stored == PostProcessingProvider.hyperwhisper.storageValue)
    }

    /// C2. `fromStorageValue` falls back to **Grok** on anything it cannot
    /// split, so a seed that lost its `<engineId>:` prefix would silently ship
    /// the wrong vendor with no error anywhere.
    @Test("the seeded post-processing model parses back to Anthropic Haiku")
    func seededPostProcessingModelParsesBack() throws {
        let persistence = PersistenceController(inMemory: true)
        persistence.initializeDefaultModes()

        let mode = try #require(persistence.fetchAllModes().first)
        let resolved = CloudPostProcessingModel.fromStorageValue(mode.cloudPostProcessingModel)

        #expect(resolved.engineId == "anthropic")
        #expect(resolved.modelId == "claude-haiku-4-5")
        #expect(resolved.storageValue == mode.cloudPostProcessingModel)
        #expect(resolved != CloudPostProcessingModel.fallback, "fell through to the Grok fallback")
    }

    /// The transcription half of the same idea: the seeded model id has to be
    /// the one the seeded tier resolves to, or the provider silently substitutes
    /// its own while the stored value stays misleading.
    @Test("the seeded tier and transcription model agree")
    func seededTierAndModelAgree() throws {
        let persistence = PersistenceController(inMemory: true)
        persistence.initializeDefaultModes()

        let mode = try #require(persistence.fetchAllModes().first)

        #expect(CloudAccuracyTier.fromStorageValue(mode.cloudAccuracyTier) == .elevenLabsScribeV2)
        #expect(mode.cloudTranscriptionModel == CloudAccuracyTier.elevenLabsScribeV2.defaultModelId)
    }

    // MARK: - The "existing users are untouched" contract

    @Test("seeding twice still leaves one mode")
    func seedingIsIdempotent() throws {
        let persistence = PersistenceController(inMemory: true)

        persistence.initializeDefaultModes()
        persistence.initializeDefaultModes()

        let modes = persistence.fetchAllModes()
        #expect(modes.count == 1)
        #expect(modes.first?.id == SeededModeValues.forCurrentRegion.id)
    }

    /// The guard that matters most on an upgrade: a store that already holds
    /// modes is never supplemented, never re-seeded and never edited. Its
    /// equivalent lives on the .NET side at
    /// `HyperWhisper.Application.Tests/Program.cs`.
    @Test("a store that already has a mode is left alone")
    func existingModesAreNeverTouched() throws {
        let persistence = PersistenceController(inMemory: true)
        let existing = persistence.createOrUpdateMode(
            name: "My own mode",
            preset: "hyper",
            language: "fr",
            model: "base",
            punctuation: false,
            capitalization: false,
            profanityFilter: true
        )
        // Snapshot what the store settled on rather than what was passed in —
        // `createOrUpdateMode` normalises some of it, and the assertion here is
        // "nothing moved", not "these exact strings".
        let existingID = try #require(existing.id)
        let existingName = existing.name
        let existingLanguage = existing.language
        let existingModel = existing.model

        persistence.initializeDefaultModes()

        let modes = persistence.fetchAllModes()
        #expect(modes.count == 1, "seeding supplemented an existing library")
        let survivor = try #require(modes.first)
        #expect(survivor.id == existingID)
        #expect(survivor.name == existingName)
        #expect(survivor.language == existingLanguage)
        #expect(survivor.model == existingModel)
        #expect(survivor.name != "Hyper", "the fixture collided with the seeded mode")
        #expect(!survivor.punctuation)
        #expect(!survivor.capitalization)
        #expect(survivor.profanityFilter)
        #expect(
            !persistence.didSeedDefaultModesOnLaunch,
            "an upgrade was reported as a fresh install, which would re-run onboarding")
    }

    // MARK: - The pure translation

    /// `SeededModeValues` is where the shared record becomes macOS columns, and
    /// the mapping is only interesting in one place — so pin that place without
    /// a Core Data stack in the way.
    @Test("the shared record maps onto the macOS columns")
    func sharedRecordMapsOntoMacOSColumns() {
        let seed = modeSeedDefault(region: "GB")
        let values = SeededModeValues(seed: seed)

        #expect(values.model == seed.providerType)
        #expect(values.id.uuidString.lowercased() == seed.id.lowercased())
        #expect(Int32(values.postProcessingMode) == seed.postProcessingMode)
        #expect(Int32(values.sortOrder) == seed.sortOrder)
        #expect(values.englishSpelling == "british")
        // There is no `providerType` column on macOS; `model` must NOT be the
        // shared record's own `model`-shaped field, because it has none.
        #expect(values.model == "cloud")
    }
}
