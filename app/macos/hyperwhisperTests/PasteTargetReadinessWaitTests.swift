//
//  PasteTargetReadinessWaitTests.swift
//  hyperwhisperTests
//
//  Guards the auto-paste settle after the target app is reactivated (#994):
//  the paste goes as soon as the target is ready, and the old fixed
//  100/180/250 ms settle stays the upper bound, so no app waits longer.
//

import Foundation
import Testing
@testable import HyperWhisper

@MainActor
struct PasteTargetReadinessWaitTests {

    @Test func readyTargetDoesNotWait() async {
        var probes = 0
        let wait = await AccessibilityHelper.waitForPasteTargetReady(upperBoundMs: 250) {
            probes += 1
            return true
        }
        #expect(wait.ready)
        #expect(probes == 1)
        #expect(wait.waitedMs < 50)
    }

    @Test func returnsOnTheFirstReadyPoll() async {
        var probes = 0
        let wait = await AccessibilityHelper.waitForPasteTargetReady(upperBoundMs: 5_000,
                                                                      pollIntervalMs: 10) {
            probes += 1
            return probes == 3
        }
        #expect(wait.ready)
        #expect(probes == 3)
        // Two 10 ms polls, far below the bound even on a loaded CI runner.
        #expect(wait.waitedMs < 2_000)
    }

    @Test func neverReadyStopsAtTheUpperBound() async {
        var probes = 0
        let start = ContinuousClock.now
        let wait = await AccessibilityHelper.waitForPasteTargetReady(upperBoundMs: 100,
                                                                      pollIntervalMs: 15) {
            probes += 1
            return false
        }
        let elapsed = start.duration(to: .now)
        #expect(!wait.ready)
        #expect(probes >= 2)
        #expect(elapsed >= .milliseconds(100))
        #expect(wait.waitedMs >= 100)
    }

    @Test func cancelledTaskStopsWaiting() async {
        let task = Task { @MainActor in
            await AccessibilityHelper.waitForPasteTargetReady(upperBoundMs: 60_000,
                                                              pollIntervalMs: 15) { false }
        }
        task.cancel()
        let start = ContinuousClock.now
        let wait = await task.value
        #expect(!wait.ready)
        #expect(start.duration(to: .now) < .seconds(10))
    }
}
