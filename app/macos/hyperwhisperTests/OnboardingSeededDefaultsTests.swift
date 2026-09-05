//
//  OnboardingSeededDefaultsTests.swift
//  hyperwhisperTests
//
//  `LiveOnboardingSourceCommitter.apply` does not only RECONFIGURE the default
//  mode — it CREATES it, whenever onboarding runs before
//  `initializeDefaultModes()` has, or after the user has deleted every mode. On
//  that path each `existing?.x ?? …` arm is a seeded value, so it has to be the
//  value the seeder itself would have written.
//
//  It was not. macOS carried `language: existing?.language ?? "en"` from before
//  #285, while the shared seed (`hw-catalog::mode_seed`) says `"auto"`. A user
//  who reached onboarding first therefore ended up with a mode pinned to
//  English — silently, and in direct contradiction of the one-mode/auto-language
//  decision the whole change exists to make. `preset` had the same shape, and
//  `captureRestorePoint` additionally carried `model ?? "base"`.
//
//  What is pinned here is the CONTRACT rather than the call: the fallback source
//  onboarding uses must equal what `initializeDefaultModes()` actually lands in
//  Core Data. `apply` itself is not reachable from a unit test — the committer
//  requires an `AppState`, and building one instantiates
//  `PersistenceController.shared`, the real on-disk store, from inside
//  `setupSubscriptions()`. That is also why the assertions below go through a
//  really-seeded row instead of comparing the seed to itself.
//

import CoreData
import Foundation
import Testing

@testable import HyperWhisper

@MainActor
struct OnboardingSeededDefaultsTests {

    @Test("onboarding falls back to exactly what the seeder writes")
    func onboardingFallbacksMatchTheSeededRow() throws {
        // `PersistenceController(inMemory: true)` deliberately does not seed from
        // `init`, so the seeder is called explicitly — same as DefaultModeSeedTests.
        let persistence = PersistenceController(inMemory: true)
        persistence.initializeDefaultModes()

        let seeded = try #require(persistence.fetchAllModes().first)
        let fallbacks = LiveOnboardingSourceCommitter.seed

        // The three fields `apply` can fall back to when it is creating the row.
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
        // `apply` and `captureRestorePoint` both fall back to this id when no
        // default mode is found, so a drift here would have onboarding create a
        // SECOND row rather than adopt the seeded one.
        #expect(LiveOnboardingSourceCommitter.defaultModeID == LiveOnboardingSourceCommitter.seed.id)
        #expect(LiveOnboardingSourceCommitter.defaultModeID == SeededModeValues.fallbackID)
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
        // `apply` forwards `existing?.postProcessingProvider` and lets
        // `createOrUpdateMode` resolve a nil to `.storageValue`, so a created row
        // lands on the canonical token either way. Pinned here because it is the
        // field the sibling finding in this round was about.
        #expect(LiveOnboardingSourceCommitter.seed.postProcessingProvider
            == PostProcessingProvider.hyperwhisper.storageValue)
    }
}
