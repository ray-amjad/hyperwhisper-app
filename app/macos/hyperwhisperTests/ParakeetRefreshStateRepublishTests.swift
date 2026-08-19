//
//  ParakeetRefreshStateRepublishTests.swift
//  hyperwhisperTests
//

import Combine
import Testing
@testable import HyperWhisper

@MainActor
struct ParakeetRefreshStateRepublishTests {

    /// `refreshState()` derives `availableModels` entirely from what is on disk.
    /// Two consecutive calls with no download or delete in between therefore
    /// describe an identical state, so the second must not publish.
    ///
    /// Publishing regardless closes a feedback cycle through the root view:
    ///
    ///   hyperwhisperApp.swift  .onReceive(parakeetModelManager.$availableModels)
    ///     -> TranscriptionPipeline.refreshParakeetReadiness(forModeId:)
    ///     -> TranscriptionPipeline.refreshPendingParakeetReadinessIfReady()
    ///     -> TranscriptionModelManager.prepareModel(for:)
    ///     -> ParakeetModelManager.refreshState()
    ///     -> publish, and round again
    ///
    /// The `.dropFirst().removeDuplicates()` on that subscription cannot break
    /// the cycle, because `prepareModel` moves `modelReadyState` from `.loading`
    /// to `.ready`, which re-evaluates the root body and rebuilds the operator
    /// chain; a rebuilt `removeDuplicates` has no memory of the previous value.
    ///
    /// Measured on 2.43.0 before the fix: one core saturated, the menu bar
    /// status item re-committing its scene about 74 times a second, and the
    /// status label flipping between "Parakeet V3" and "Parakeet V3 (Loading...)"
    /// about 22 times a second.
    ///
    /// This test is deliberately independent of whether any Parakeet weights are
    /// installed: it asserts that two consecutive reads of the same disk state
    /// agree, not what that state is.
    @Test func refreshStateDoesNotRepublishWhenNothingChanged() {
        let manager = ParakeetModelManager()

        // `init()` already performed the one legitimate refresh, so the manager
        // is settled by this point. `dropFirst()` then discards the current
        // value that Combine replays on subscription, leaving only genuinely
        // new publications to be counted.
        var republishCount = 0
        let cancellable = manager.$availableModels
            .dropFirst()
            .sink { _ in republishCount += 1 }

        manager.refreshState()
        manager.refreshState()

        cancellable.cancel()

        #expect(republishCount == 0)
    }
}
