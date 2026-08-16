//
//  RecordingStartBackoffTests.swift
//  hyperwhisperTests
//

import Foundation
import Testing
@testable import HyperWhisper

/// Regression guard for the input-device retry schedule (HYPERWHISPER-NF).
///
/// CoreAudio transiently reports zero input devices during route changes and
/// wake-from-sleep, so `RecordingLifecycle.startRecording()` re-probes before giving up
/// with `AudioError.noMicrophoneAvailable`. The first fix waited 2 × 250 ms = 500 ms
/// total, which was too short, and the issue regressed on v2.33.1. 3200 ms is what
/// actually stopped it.
///
/// These tests exist so shortening the schedule fails CI instead of shipping. If one of
/// them fails because the array changed, the fix is to restore the array, not to update
/// the expectation.
///
/// Every test here is `async` on purpose: a swift-testing `async` test runs off the main
/// actor, so this file compiles only while `RecordingStartBackoff` stays nonisolated. If
/// someone later annotates it `@MainActor`, CI goes red — which is the point, because a
/// main-actor-isolated constant cannot be read from the off-actor probe loop that
/// HYPERWHISPER-F7 moved this work into.
struct RecordingStartBackoffTests {

    @Test func inputDeviceRetryScheduleIsExactlyTheKnownGoodOne() async {
        #expect(RecordingStartBackoff.inputDeviceRetryDelaysMs == [150, 250, 400, 600, 800, 1000])
    }

    @Test func inputDeviceRetryBudgetCoversTheRegressionThreshold() async {
        // 3200 ms is the value that stopped HYPERWHISPER-NF recurring. Anything less
        // is the v2.33.1 regression coming back.
        #expect(RecordingStartBackoff.totalInputDeviceRetryBudgetMs >= 3_200)
    }

    @Test func retryBudgetIsDerivedFromTheSchedule() async {
        // The budget must never be able to disagree with the array it summarizes.
        let summed = RecordingStartBackoff.inputDeviceRetryDelaysMs.reduce(0, +)
        #expect(RecordingStartBackoff.totalInputDeviceRetryBudgetMs == summed)
    }

    @Test func scheduleHasMoreRetriesThanTheRegressedTwo() async {
        // The regressed schedule was two attempts. Six retries plus the initial probe
        // is seven probes in total.
        #expect(RecordingStartBackoff.inputDeviceRetryDelaysMs.count == 6)
    }

    @Test func delaysIncreaseMonotonically() async {
        // Exponential-ish backoff: short first so the common transient recovers fast,
        // longer later so a slow wake-from-sleep still gets covered.
        let delays = RecordingStartBackoff.inputDeviceRetryDelaysMs
        for (previous, next) in zip(delays, delays.dropFirst()) {
            #expect(next > previous)
        }
    }
}
