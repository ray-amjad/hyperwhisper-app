//
//  FileTranscriptionProgressIndeterminateTests.swift
//  hyperwhisperTests
//
//  The Transcribe File HUD has no real progress while an engine runs: the flow
//  starts a timed 15→85% ramp over 60 s. A longer job used to sit at exactly 85%
//  for minutes and look hung (#1437). The ramp now hands over to an indeterminate
//  bar when it reaches its target with the job still running. These cases pin
//  that hand-over and that every later step clears it.
//

import XCTest
@testable import HyperWhisper

@MainActor
final class FileTranscriptionProgressIndeterminateTests: XCTestCase {

    /// Spin the main run loop until `condition` holds, so the 60 fps timer and its
    /// MainActor hop can fire.
    private func waitUntil(timeout: TimeInterval = 2.0, _ condition: () -> Bool) {
        let deadline = Date().addingTimeInterval(timeout)
        while !condition() && Date() < deadline {
            RunLoop.main.run(until: Date().addingTimeInterval(0.02))
        }
    }

    func testOpenEndedRampTurnsIndeterminateAtItsTarget() {
        let progress = FileTranscriptionProgress()
        progress.beginTranscription(fileName: "long.wav", modeName: "Mode")
        progress.animateProgress(to: 0.85, duration: 0.05, thenIndeterminate: true)
        XCTAssertFalse(progress.isIndeterminate, "the ramp is still determinate while it runs")

        waitUntil { progress.isIndeterminate }

        XCTAssertTrue(progress.isIndeterminate)
        XCTAssertEqual(progress.progress, 0.85, accuracy: 0.0001)
    }

    func testPlainRampNeverTurnsIndeterminate() {
        let progress = FileTranscriptionProgress()
        progress.beginTranscription(fileName: "short.wav", modeName: "Mode")
        progress.animateProgress(to: 0.15, duration: 0.05)

        waitUntil { progress.progress >= 0.15 }
        RunLoop.main.run(until: Date().addingTimeInterval(0.1))

        XCTAssertEqual(progress.progress, 0.15, accuracy: 0.0001)
        XCTAssertFalse(progress.isIndeterminate)
    }

    func testFinishingStepLeavesTheIndeterminateState() {
        let progress = FileTranscriptionProgress()
        progress.beginTranscription(fileName: "long.wav", modeName: "Mode")
        progress.animateProgress(to: 0.85, duration: 0.05, thenIndeterminate: true)
        waitUntil { progress.isIndeterminate }
        XCTAssertTrue(progress.isIndeterminate)

        // What the flow does when the engine returns.
        progress.updateStage(.finishing)
        progress.animateProgress(to: 0.95, duration: 0.05)

        XCTAssertFalse(progress.isIndeterminate, "the bar shows a number again once the job moves on")
        waitUntil { progress.progress >= 0.95 }
        XCTAssertEqual(progress.progress, 0.95, accuracy: 0.0001)
        XCTAssertFalse(progress.isIndeterminate)
    }

    func testFastJobFinishesBeforeTheRampEndsAndNeverGoesIndeterminate() {
        let progress = FileTranscriptionProgress()
        progress.beginTranscription(fileName: "short.wav", modeName: "Mode")
        progress.animateProgress(to: 0.85, duration: 60.0, thenIndeterminate: true)

        // The engine returns long before the 60 s ramp ends.
        progress.animateProgress(to: 0.95, duration: 0.05)
        waitUntil { progress.progress >= 0.95 }
        RunLoop.main.run(until: Date().addingTimeInterval(0.1))

        XCTAssertFalse(progress.isIndeterminate)
    }

    func testCancelAndResetClearTheIndeterminateState() {
        let progress = FileTranscriptionProgress()
        progress.beginTranscription(fileName: "long.wav", modeName: "Mode")
        progress.animateProgress(to: 0.85, duration: 0.05, thenIndeterminate: true)
        progress.cancel()
        RunLoop.main.run(until: Date().addingTimeInterval(0.2))
        XCTAssertFalse(progress.isIndeterminate, "a cancelled ramp never hands over")

        progress.beginTranscription(fileName: "long.wav", modeName: "Mode")
        progress.animateProgress(to: 0.85, duration: 0.05, thenIndeterminate: true)
        waitUntil { progress.isIndeterminate }
        XCTAssertTrue(progress.isIndeterminate)

        progress.reset()
        XCTAssertFalse(progress.isIndeterminate)
    }
}
