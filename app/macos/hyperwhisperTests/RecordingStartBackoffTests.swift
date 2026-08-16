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
/// The total budget is the invariant. How it is spread across attempts is a tuning
/// decision that may legitimately change, so this asserts the budget and nothing else —
/// pinning the literal array, its element count or its monotonicity would only assert
/// that nobody edited the file.
///
/// The test is `async` on purpose: a swift-testing `async` test runs off the main actor,
/// so this file compiles only while `RecordingStartBackoff` stays nonisolated. If someone
/// later annotates it `@MainActor`, CI goes red — which is the point, because a
/// main-actor-isolated constant cannot be read from the off-actor probe loop that
/// HYPERWHISPER-F7 moved this work into.
struct RecordingStartBackoffTests {

    @Test func inputDeviceRetryBudgetCoversTheRegressionThreshold() async {
        // 3200 ms is the value that stopped HYPERWHISPER-NF recurring. Anything less is
        // the v2.33.1 regression coming back. If this fails because the schedule was
        // shortened, restore the schedule — do not lower the threshold.
        #expect(RecordingStartBackoff.totalInputDeviceRetryBudgetMs >= 3_200)
    }
}
