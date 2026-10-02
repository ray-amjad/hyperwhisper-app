//
//  CloudProviderHealthPublishCountTests.swift
//  hyperwhisperTests
//

import Combine
import Testing
@testable import HyperWhisper

/// Issue #1042. `ModelLibraryManager` rebuilds the whole Model Library on every
/// publish of `$statuses` and `$postProcessingStatuses`, and the page's
/// `onAppear` calls `refreshAll()` + `refreshAllPostProcessing()`. Before the
/// fix each call wrote the dictionary once PER PROVIDER, equal value or not, so
/// one visit published 14 + 9 times and ran `rebuild()` ~55 times.
///
/// Every test here is synchronous on the main actor. A probe that `refresh`
/// schedules is a main-actor `Task`, so it cannot run before the assertions;
/// each count is exactly what the refresh call itself published. No
/// `apiKeyProvider` is configured, so a probe that does run later returns
/// `.unknown` without touching the network.
@MainActor
struct CloudProviderHealthPublishCountTests {

    /// Counts publications after subscription. `dropFirst()` discards the
    /// current value that `@Published` replays the moment a subscriber attaches.
    private final class EmissionCounter {
        var count = 0
        var cancellables: Set<AnyCancellable> = []
    }

    private static func count<Value>(
        _ publisher: Published<Value>.Publisher
    ) -> EmissionCounter {
        let counter = EmissionCounter()
        publisher
            .dropFirst()
            .sink { [counter] _ in counter.count += 1 }
            .store(in: &counter.cancellables)
        return counter
    }

    /// The issue's Done when: a warm transcription cache, then one Model
    /// Library `onAppear`'s worth of refreshes, publishes at most once per
    /// dictionary.
    @Test func aModelLibraryVisitPublishesEachDictionaryAtMostOnce() {
        let manager = CloudProviderHealthManager()
        for provider in CloudProvider.allCases {
            manager.setCachedTranscriptionStatusForTests(.healthy, for: provider)
        }

        let statuses = Self.count(manager.$statuses)
        let postProcessing = Self.count(manager.$postProcessingStatuses)

        manager.refreshAll()
        manager.refreshAllPostProcessing()

        // Every transcription status is served from the warm cache and is
        // unchanged, so nothing is published (main: one publish per provider).
        #expect(statuses.count == 0)
        // There is no test seam for the post-processing cache, so it is cold:
        // the probed providers move from `.unknown` to `.checking`. That is a
        // real change, published ONCE (main: one publish per provider).
        #expect(postProcessing.count == 1)

        #expect(CloudProvider.allCases.allSatisfy { manager.status(for: $0) == .healthy })
        #expect(manager.postProcessingStatuses[.anthropic] == .checking)
        #expect(manager.postProcessingStatuses[.hyperwhisper] == .healthy)
    }

    /// A cold refresh publishes once. A second refresh while every probe is
    /// still in flight changes nothing, so it publishes nothing. The dtrace in
    /// the issue saw `refreshAllPostProcessing` entered twice per visit.
    @Test func aRepeatedColdRefreshPublishesOnlyTheFirstTime() {
        let manager = CloudProviderHealthManager()
        let statuses = Self.count(manager.$statuses)
        let postProcessing = Self.count(manager.$postProcessingStatuses)

        manager.refreshAll()
        manager.refreshAllPostProcessing()
        #expect(statuses.count == 1)
        #expect(postProcessing.count == 1)
        #expect(CloudProvider.allCases.allSatisfy { manager.status(for: $0) == .checking })

        manager.refreshAll()
        manager.refreshAllPostProcessing()
        #expect(statuses.count == 1)
        #expect(postProcessing.count == 1)
    }

    /// The single-provider paths skip a write that would not change the value.
    @Test func aSingleProviderRefreshSkipsANoOpWrite() {
        let manager = CloudProviderHealthManager()
        manager.setCachedTranscriptionStatusForTests(.healthy, for: .googleSpeech)

        let statuses = Self.count(manager.$statuses)
        let postProcessing = Self.count(manager.$postProcessingStatuses)

        manager.refresh(CloudProvider.googleSpeech)
        // HyperWhisper Cloud post-processing needs no health check and is
        // `.healthy` from init, so its refresh writes the same value.
        manager.refresh(PostProcessingProvider.hyperwhisper)

        #expect(statuses.count == 0)
        #expect(postProcessing.count == 0)

        // A real change still publishes: a cold provider moves to `.checking`.
        manager.refresh(CloudProvider.deepgram)
        #expect(statuses.count == 1)
        #expect(manager.status(for: CloudProvider.deepgram) == .checking)
    }
}
