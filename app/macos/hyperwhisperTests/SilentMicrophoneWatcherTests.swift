//
//  SilentMicrophoneWatcherTests.swift
//  hyperwhisperTests
//
//  Issue #2411: the live "mic hears nothing" rule. -60 dBFS block peak, 2.0 s
//  from capture start with nothing heard, disarmed by the first heard block,
//  driven by a clock so it also fires with no blocks, reset per recording.
//

import Foundation
import Testing
@testable import HyperWhisper

struct SilentMicrophoneWatcherTests {

    private static let start: TimeInterval = 1_000

    private static func started() -> SilentMicrophoneWatcher {
        var watcher = SilentMicrophoneWatcher()
        watcher.start(at: start)
        return watcher
    }

    // MARK: - Threshold

    @Test func peakAtTheThresholdIsHeard() {
        #expect(SilentMicrophoneWatcher.isHeard(peakDbfs: -60))
        #expect(SilentMicrophoneWatcher.isHeard(peakDbfs: -20))
        #expect(SilentMicrophoneWatcher.isHeard(peakDbfs: 0))
    }

    @Test func peakBelowTheThresholdIsSilence() {
        #expect(!SilentMicrophoneWatcher.isHeard(peakDbfs: -60.01))
        #expect(!SilentMicrophoneWatcher.isHeard(peakDbfs: -75))
        #expect(!SilentMicrophoneWatcher.isHeard(peakDbfs: -160))
        #expect(!SilentMicrophoneWatcher.isHeard(peakDbfs: -Float.infinity))
        #expect(!SilentMicrophoneWatcher.isHeard(peakDbfs: Float.nan))
    }

    @Test func peakDbfsOfSampleAmplitudes() {
        #expect(SilentMicrophoneWatcher.peakDbfs(maxAbsoluteSample: 1) == 0)
        #expect(abs(SilentMicrophoneWatcher.peakDbfs(maxAbsoluteSample: 0.001) - -60) < 0.001)
        #expect(SilentMicrophoneWatcher.peakDbfs(maxAbsoluteSample: 0) == -Float.infinity)
        #expect(SilentMicrophoneWatcher.peakDbfs(maxAbsoluteSample: .nan) == -Float.infinity)
    }

    // MARK: - 2 s

    @Test func silentBlocksWarnOnlyAfterTwoSeconds() {
        var watcher = Self.started()
        watcher.observe(peakDbfs: -90, at: Self.start + 0.5)
        #expect(!watcher.isWarning)
        watcher.observe(peakDbfs: -90, at: Self.start + 1.99)
        #expect(!watcher.isWarning)
        watcher.observe(peakDbfs: -90, at: Self.start + 2.0)
        #expect(watcher.isWarning)
    }

    @Test func quietButLiveRoomNoiseNeverWarns() {
        var watcher = Self.started()
        watcher.observe(peakDbfs: -55, at: Self.start + 0.05)
        watcher.tick(at: Self.start + 10)
        #expect(!watcher.isWarning)
    }

    // MARK: - No blocks

    @Test func noBlocksAtAllStillWarnsFromTheClock() {
        var watcher = Self.started()
        watcher.tick(at: Self.start + 1.9)
        #expect(!watcher.isWarning)
        watcher.tick(at: Self.start + 2.1)
        #expect(watcher.isWarning)
    }

    // MARK: - Disarm

    @Test func firstHeardBlockHidesTheWarning() {
        var watcher = Self.started()
        watcher.tick(at: Self.start + 3)
        #expect(watcher.isWarning)
        watcher.observe(peakDbfs: -30, at: Self.start + 3.1)
        #expect(!watcher.isWarning)
        #expect(watcher.phase == .heard)
    }

    @Test func pausesAfterSpeechNeverWarn() {
        var watcher = Self.started()
        watcher.observe(peakDbfs: -20, at: Self.start + 0.3)
        // Digital silence between words on a noise-gated headset, for a long time.
        for step in 1...200 {
            watcher.observe(peakDbfs: -Float.infinity, at: Self.start + 0.3 + Double(step) * 0.05)
        }
        watcher.tick(at: Self.start + 60)
        #expect(!watcher.isWarning)
    }

    // MARK: - Reset / idle

    @Test func idleWatcherIgnoresBlocksAndTicks() {
        var watcher = SilentMicrophoneWatcher()
        watcher.tick(at: Self.start + 100)
        watcher.observe(peakDbfs: -100, at: Self.start + 100)
        #expect(!watcher.isWarning)
        #expect(watcher.phase == .idle)
    }

    @Test func stopHidesTheWarning() {
        var watcher = Self.started()
        watcher.tick(at: Self.start + 5)
        #expect(watcher.isWarning)
        watcher.stop()
        #expect(!watcher.isWarning)
        watcher.tick(at: Self.start + 10)
        #expect(!watcher.isWarning)
    }

    @Test func newRecordingRearmsAfterAHeardOne() {
        var watcher = Self.started()
        watcher.observe(peakDbfs: -10, at: Self.start + 0.1)
        watcher.stop()

        let second = Self.start + 30
        watcher.start(at: second)
        watcher.tick(at: second + 1)
        #expect(!watcher.isWarning)
        watcher.tick(at: second + 2.5)
        #expect(watcher.isWarning)
    }

    @Test func newRecordingStartsItsOwnClock() {
        var watcher = Self.started()
        watcher.tick(at: Self.start + 5)
        #expect(watcher.isWarning)

        let second = Self.start + 6
        watcher.start(at: second)
        #expect(!watcher.isWarning)
        watcher.tick(at: second + 1)
        #expect(!watcher.isWarning)
    }

    // MARK: - Display name

    @Test func shortNamesAreShownWhole() {
        #expect(SilentMicrophoneWatcher.displayName("MacBook Pro Microphone") == "MacBook Pro Microphone")
    }

    @Test func longNamesAreCutWithAnEllipsis() {
        let name = String(repeating: "A", count: 50)
        let shown = SilentMicrophoneWatcher.displayName(name, maxLength: 10)
        #expect(shown == "AAAAAAAAA…")
        #expect(shown.count == 10)
    }
}

/// Where the warning panel goes relative to the recording pill (#2411).
struct SilentMicWarningPlacementTests {
    private let size = CGSize(width: 280, height: 26)
    private let screen = NSRect(x: 0, y: 0, width: 1440, height: 900)

    @Test func goesCentredBelowThePillWhenThereIsRoom() {
        let pill = NSRect(x: 620, y: 40, width: 200, height: 40)
        let frame = SilentMicWarningWindowManager.frame(size: size, below: pill, gap: 6, within: screen)
        #expect(frame.midX == pill.midX)
        #expect(frame.maxY == pill.minY - 6)
    }

    @Test func goesAboveThePillAtTheBottomEdge() {
        let pill = NSRect(x: 620, y: 0, width: 200, height: 40)
        let frame = SilentMicWarningWindowManager.frame(size: size, below: pill, gap: 6, within: screen)
        #expect(frame.minY == pill.maxY + 6)
    }
}
