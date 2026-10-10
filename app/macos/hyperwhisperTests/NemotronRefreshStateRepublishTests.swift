//
//  NemotronRefreshStateRepublishTests.swift
//  hyperwhisperTests
//

import Combine
import Testing
@testable import HyperWhisper

@MainActor
struct NemotronRefreshStateRepublishTests {

    /// Issue #1510: the menu bar menu went dead while a Local API loop called
    /// `/transcribe` with `engine=nemotron` every ~2 s.
    ///
    /// Each such request runs, on the main actor:
    ///
    ///   TranscriptionProviderRouter.selectLocalProvider
    ///     -> NemotronModelManager.refreshState()        (availableModels, brokenVariants)
    ///     -> NemotronProvider.prepareIfNeeded
    ///     -> NemotronModelManager.clearVariantBroken(_:)
    ///
    /// Each of those assignments published even when nothing changed.
    /// hyperwhisperApp holds this manager as a `@StateObject`, so every publish
    /// re-evaluated the whole Scene body, and the `.menu`-style MenuBarExtra
    /// rebuilt its NSMenu under the open menu: no highlight, no submenu.
    ///
    /// The Scene is invalidated by `objectWillChange`, not by any single
    /// property's publisher, so that is what this counts. Like the Parakeet
    /// test, it does not depend on whether any Nemotron weights are installed:
    /// it asserts that repeated reads of one disk state publish nothing.
    @Test func aSettledRequestPathDoesNotPublish() {
        let manager = NemotronModelManager()
        // `init()` already ran the one legitimate refresh. Settle once more
        // in case it deferred any work, then start counting.
        manager.refreshState()

        var publishCount = 0
        let cancellable = manager.objectWillChange.sink { _ in publishCount += 1 }

        for _ in 0..<3 {
            manager.refreshState()
            manager.clearVariantBroken(NemotronModelManager.Constants.latinModelId)
        }

        cancellable.cancel()

        #expect(publishCount == 0)
    }

    /// A real change still publishes, once: flagging a variant broken twice
    /// publishes on the first call only, and clearing it publishes once.
    @Test func brokenFlagPublishesOnlyOnAChange() {
        let manager = NemotronModelManager()
        let modelId = NemotronModelManager.Constants.latinModelId
        manager.clearVariantBroken(modelId)

        var publishCount = 0
        let cancellable = manager.objectWillChange.sink { _ in publishCount += 1 }

        manager.markVariantBroken(modelId)
        manager.markVariantBroken(modelId)
        #expect(manager.isVariantBroken(modelId))
        #expect(publishCount == 1)

        manager.clearVariantBroken(modelId)
        manager.clearVariantBroken(modelId)
        #expect(!manager.isVariantBroken(modelId))
        #expect(publishCount == 2)

        cancellable.cancel()
    }
}
