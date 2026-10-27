//
//  PasteTargetReadinessWaitTests.swift
//  hyperwhisperTests
//
//  Guards the auto-paste settle after the target app is reactivated (#994):
//  the paste goes as soon as the target is ready, and the old fixed
//  100/180/250 ms settle stays the upper bound, so no app waits longer.
//
//  No assertion here bounds wall-clock time from above: in the full macos-ci
//  suite other @MainActor suites can hold the main actor for tens of seconds,
//  so a short sleep can return very late. Bounds are minutes, or none.
//

import Foundation
import Testing
@testable import HyperWhisper

@MainActor
struct PasteTargetReadinessWaitTests {

    @Test func readyTargetDoesNotWait() async {
        var probes = 0
        let wait = await AccessibilityHelper.waitForPasteTargetReady(upperBoundMs: 250) { _ in
            probes += 1
            return true
        }
        #expect(wait.ready)
        #expect(probes == 1)
    }

    @Test func returnsOnTheFirstReadyPoll() async {
        var probes = 0
        let wait = await AccessibilityHelper.waitForPasteTargetReady(upperBoundMs: 600_000,
                                                                      pollIntervalMs: 10) { _ in
            probes += 1
            return probes == 3
        }
        #expect(wait.ready)
        #expect(probes == 3)
        #expect(wait.waitedMs < 600_000)
    }

    @Test func neverReadyStopsAtTheUpperBound() async {
        var probes = 0
        let start = ContinuousClock.now
        let wait = await AccessibilityHelper.waitForPasteTargetReady(upperBoundMs: 100,
                                                                      pollIntervalMs: 15) { remaining in
            probes += 1
            #expect(remaining > .zero)
            #expect(remaining <= .milliseconds(100))
            return false
        }
        let elapsed = start.duration(to: .now)
        #expect(!wait.ready)
        #expect(probes >= 1)
        #expect(elapsed >= .milliseconds(100))
        #expect(wait.waitedMs >= 100)
    }

    @Test func cancelBeforeTheFirstPollStopsWaiting() async {
        let probes = ProbeCount()
        let task = Task { @MainActor in
            await AccessibilityHelper.waitForPasteTargetReady(upperBoundMs: 600_000,
                                                              pollIntervalMs: 15) { _ in
                probes.value += 1
                return false
            }
        }
        task.cancel()
        let wait = await task.value
        #expect(!wait.ready)
        // A cancelled paste sends no AX message to the target.
        #expect(probes.value == 0)
        // Stopped long before the 10-minute bound.
        #expect(wait.waitedMs < 300_000)
    }

    /// Cancels while the wait is inside its sleep between polls, so the
    /// swallowed `CancellationError` must still end the loop.
    @Test func cancelDuringTheSleepStopsWaiting() async {
        let probes = ProbeCount()
        let task = Task { @MainActor in
            await AccessibilityHelper.waitForPasteTargetReady(upperBoundMs: 600_000,
                                                              pollIntervalMs: 120_000) { _ in
                probes.value += 1
                return false
            }
        }
        // Bounded, so a wait that never probes fails here instead of hanging the suite.
        var spins = 0
        while probes.value == 0 && spins < 1_000_000 {
            spins += 1
            await Task.yield()
        }
        #expect(probes.value == 1)
        task.cancel()
        let wait = await task.value
        #expect(!wait.ready)
        #expect(probes.value == 1)
        // A 2-minute poll sleep that ignored the cancel would take far longer.
        #expect(wait.waitedMs < 100_000)
    }
}

/// A main-actor counter the cancelled task and the test can both read.
@MainActor
private final class ProbeCount {
    var value = 0
}
