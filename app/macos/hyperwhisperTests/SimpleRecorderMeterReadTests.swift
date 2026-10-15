//
//  SimpleRecorderMeterReadTests.swift
//  hyperwhisperTests
//
//  HYPERWHISPER-KB (#1104): `AVAudioRecorder.updateMeters()` waits on an
//  AudioQueue lock that CoreAudio can hold for 10 s or more. The 30 FPS meter
//  poll called it on the main actor, so the app froze mid-recording. These pin
//  the fix: the read runs off the main thread, and a read that blocks is never
//  joined by a second one.
//
//  #1359: `stop()` on the same recorder used to run on the main actor, with
//  nothing ordering it against a read on the meter queue. The stop tests pin
//  that `stop()` now waits behind an in-flight read on the meter queue, and that
//  `stopRecording()` keeps the main thread free and gives up after its deadline.
//

import AVFoundation
import Foundation
import os
import Testing
@testable import HyperWhisper

/// A fake meter reader that blocks until the test releases it, and records the
/// thread and the number of reads.
private final class BlockingMeterReader: @unchecked Sendable {
    private struct State {
        var readCount = 0
        var readsInProgress = 0
        var ranOnMainThread: Bool?
    }

    private let blocks: Bool
    private let level: Float
    private let state = OSAllocatedUnfairLock(initialState: State())
    private let entered = DispatchSemaphore(value: 0)
    private let releaseSemaphore = DispatchSemaphore(value: 0)

    init(blocks: Bool = true, level: Float = 0.5) {
        self.blocks = blocks
        self.level = level
    }

    var readCount: Int { state.withLock { $0.readCount } }
    var readsInProgress: Int { state.withLock { $0.readsInProgress } }
    var ranOnMainThread: Bool? { state.withLock { $0.ranOnMainThread } }

    func read() -> Float {
        let onMain = Thread.isMainThread
        state.withLock { current in
            current.readCount += 1
            current.readsInProgress += 1
            current.ranOnMainThread = onMain
        }
        entered.signal()
        if blocks {
            // Only the test's `release()` should end the block (#1216).
            _ = releaseSemaphore.wait(timeout: .now() + 60)
        }
        state.withLock { $0.readsInProgress -= 1 }
        return level
    }

    /// True once a read has been entered, or false after `timeout` seconds.
    func waitUntilEntered(timeout: Double = 30) async -> Bool {
        await waitOffThePool(for: entered, seconds: timeout)
    }

    func release() {
        releaseSemaphore.signal()
    }
}

/// A fake `stop()` that records when it ran: on which thread, and how many
/// meter reads were still inside the reader at that moment.
private final class RecordingStopper: @unchecked Sendable {
    private struct State {
        var stopCount = 0
        var ranOnMainThread: Bool?
        var readsInProgressAtStop: Int?
    }

    private let reader: BlockingMeterReader
    private let state = OSAllocatedUnfairLock(initialState: State())
    private let stopped = DispatchSemaphore(value: 0)

    init(watching reader: BlockingMeterReader) {
        self.reader = reader
    }

    var stopCount: Int { state.withLock { $0.stopCount } }
    var ranOnMainThread: Bool? { state.withLock { $0.ranOnMainThread } }
    var readsInProgressAtStop: Int? { state.withLock { $0.readsInProgressAtStop } }

    func stop(_ recorder: AVAudioRecorder) {
        let onMain = Thread.isMainThread
        let inProgress = reader.readsInProgress
        state.withLock { current in
            current.stopCount += 1
            current.ranOnMainThread = onMain
            current.readsInProgressAtStop = inProgress
        }
        stopped.signal()
    }

    /// True once a stop has run, or false after `timeout` seconds.
    func waitUntilStopped(timeout: Double = 30) async -> Bool {
        await waitOffThePool(for: stopped, seconds: timeout)
    }
}

/// Set from a `Task` and read by the test, both on the main actor.
@MainActor
private final class Flag {
    var isSet = false
}

@MainActor
struct SimpleRecorderMeterReadTests {

    /// A recorder the reader can be handed. Never started, so no microphone and
    /// no file: the same `/dev/null` target the onboarding preview uses.
    private static func makeAVRecorder() throws -> AVAudioRecorder {
        let settings: [String: Any] = [
            AVFormatIDKey: Int(kAudioFormatLinearPCM),
            AVSampleRateKey: 16000.0,
            AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 16,
            AVLinearPCMIsFloatKey: false,
            AVLinearPCMIsBigEndianKey: false
        ]
        return try AVAudioRecorder(url: URL(fileURLWithPath: "/dev/null"), settings: settings)
    }

    private static func makeRecorder(reader: BlockingMeterReader) -> SimpleRecorder {
        let recorder = SimpleRecorder()
        recorder.meterReader = { _ in reader.read() }
        return recorder
    }

    /// True once the pending read has come back to the main actor.
    private static func waitUntilReadSettles(_ recorder: SimpleRecorder, seconds: Double = 30) async -> Bool {
        let deadline = Date().addingTimeInterval(seconds)
        while recorder.meterReadInFlight {
            if Date() > deadline { return false }
            try? await Task.sleep(nanoseconds: 10_000_000)
        }
        return true
    }

    @Test func meterReadRunsOffTheMainThread() async throws {
        let reader = BlockingMeterReader()
        defer { reader.release() }
        let recorder = Self.makeRecorder(reader: reader)

        recorder.requestMeterRead(from: try Self.makeAVRecorder())

        // An inline read would have blocked here to its 60 s cap.
        #expect(await reader.waitUntilEntered())
        #expect(reader.ranOnMainThread == false)
        #expect(recorder.meterReadInFlight)
    }

    @Test func blockedReadDoesNotStartASecondRead() async throws {
        let reader = BlockingMeterReader()
        defer { reader.release() }
        let recorder = Self.makeRecorder(reader: reader)
        let avRecorder = try Self.makeAVRecorder()

        recorder.requestMeterRead(from: avRecorder)
        #expect(await reader.waitUntilEntered())

        // Ticks while the first read is stuck are skipped, not queued.
        for _ in 0..<5 {
            recorder.requestMeterRead(from: avRecorder)
        }
        #expect(await reader.waitUntilEntered(timeout: 0.3) == false)
        #expect(reader.readCount == 1)
        #expect(recorder.meterReadInFlight)

        // Once it returns, the next tick reads again.
        reader.release()
        #expect(await Self.waitUntilReadSettles(recorder))
        recorder.requestMeterRead(from: avRecorder)
        #expect(await reader.waitUntilEntered())
        #expect(reader.readCount == 2)
        #expect(reader.ranOnMainThread == false)
    }

    /// Positive control: with the read's recorder still installed and no stop,
    /// the level IS published. Without this, the drop test below could pass
    /// because nothing is ever published.
    @Test func readThatReturnsWhileStillInstalledPublishesItsLevel() async throws {
        let reader = BlockingMeterReader(level: 0.8)
        defer { reader.release() }
        let recorder = Self.makeRecorder(reader: reader)
        let avRecorder = try Self.makeAVRecorder()
        recorder.installRecorderForTesting(avRecorder)

        recorder.requestMeterRead(from: avRecorder)
        #expect(await reader.waitUntilEntered())
        reader.release()

        #expect(await Self.waitUntilReadSettles(recorder))
        #expect(recorder.audioLevel == 0.8)
    }

    @Test func readThatReturnsAfterStopDoesNotPublishALevel() async throws {
        let reader = BlockingMeterReader(level: 0.8)
        defer { reader.release() }
        let recorder = Self.makeRecorder(reader: reader)
        let avRecorder = try Self.makeAVRecorder()
        recorder.installRecorderForTesting(avRecorder)

        recorder.requestMeterRead(from: avRecorder)
        #expect(await reader.waitUntilEntered())

        // The stop now waits behind the stuck read (#1359); a short deadline lets
        // it give up so the test can go on while the read is still blocked.
        recorder.stopTimeout = .milliseconds(100)
        _ = await recorder.stopRecording()
        // `stopRecording()` empties the slot, which alone would drop the level.
        // Put the same recorder back so the identity guard passes: the
        // `startGeneration` bump from the stop is then the only thing that can
        // drop this stale read.
        recorder.installRecorderForTesting(avRecorder)
        reader.release()

        #expect(await Self.waitUntilReadSettles(recorder))
        #expect(recorder.audioLevel == 0)
    }

    // MARK: - #1359: stop() behind the meter read

    @Test func stopDoesNotRunWhileAMeterReadIsInFlight() async throws {
        let reader = BlockingMeterReader()
        defer { reader.release() }
        let stopper = RecordingStopper(watching: reader)
        let recorder = Self.makeRecorder(reader: reader)
        recorder.recorderStopper = { stopper.stop($0) }
        let avRecorder = try Self.makeAVRecorder()
        recorder.installRecorderForTesting(avRecorder)

        recorder.requestMeterRead(from: avRecorder)
        #expect(await reader.waitUntilEntered())

        let stop = Task { await recorder.stopRecording() }

        // The read is stuck, so the stop must still be queued behind it.
        #expect(await stopper.waitUntilStopped(timeout: 0.5) == false)
        #expect(stopper.stopCount == 0)

        reader.release()
        #expect(await stop.value == .stopped)
        #expect(stopper.stopCount == 1)
        #expect(stopper.readsInProgressAtStop == 0)
        #expect(stopper.ranOnMainThread == false)
    }

    @Test func stopRecordingDoesNotBlockTheMainThreadWhileAReadIsStuck() async throws {
        let reader = BlockingMeterReader()
        defer { reader.release() }
        let stopper = RecordingStopper(watching: reader)
        let recorder = Self.makeRecorder(reader: reader)
        recorder.recorderStopper = { stopper.stop($0) }
        recorder.stopTimeout = .milliseconds(500)
        let avRecorder = try Self.makeAVRecorder()
        recorder.installRecorderForTesting(avRecorder)

        recorder.requestMeterRead(from: avRecorder)
        #expect(await reader.waitUntilEntered())

        let returned = Flag()
        let started = Date()
        let stop = Task { () -> RecorderStopOutcome in
            let outcome = await recorder.stopRecording()
            returned.isSet = true
            return outcome
        }

        // This test runs on the main actor. Getting back here while the stop is
        // still waiting proves the wait does not hold the main thread.
        try await Task.sleep(nanoseconds: 100_000_000)
        #expect(returned.isSet == false)
        #expect(recorder.isRecording == false)
        #expect(recorder.audioLevel == 0)

        // The read is still stuck, so the stop gives up at its deadline instead
        // of waiting for the read (the reader blocks for up to 60 s).
        #expect(await stop.value == .timedOut)
        #expect(Date().timeIntervalSince(started) < 10)
        #expect(stopper.stopCount == 0)

        // The stop is not abandoned: it runs as soon as the read returns, so the
        // microphone is released then.
        reader.release()
        #expect(await stopper.waitUntilStopped())
        #expect(stopper.readsInProgressAtStop == 0)
        #expect(stopper.ranOnMainThread == false)
    }

    @Test func stopWithNoReadInFlightStopsOffTheMainThread() async throws {
        let reader = BlockingMeterReader(blocks: false)
        let stopper = RecordingStopper(watching: reader)
        let recorder = Self.makeRecorder(reader: reader)
        recorder.recorderStopper = { stopper.stop($0) }
        recorder.installRecorderForTesting(try Self.makeAVRecorder())

        #expect(await recorder.stopRecording() == .stopped)
        #expect(stopper.stopCount == 1)
        #expect(stopper.ranOnMainThread == false)
    }

    @Test func stopWithNoRecorderInstalledReturnsAtOnce() async {
        let reader = BlockingMeterReader(blocks: false)
        let stopper = RecordingStopper(watching: reader)
        let recorder = Self.makeRecorder(reader: reader)
        recorder.recorderStopper = { stopper.stop($0) }

        #expect(await recorder.stopRecording() == .stopped)
        #expect(stopper.stopCount == 0)
    }

    /// PR #1767 (Ray, 2026-10-13): a CANCELLED stop that times out keeps its WAV
    /// for `CrashRecoveryManager` and shows the error, like any other timeout. The
    /// cancelled branch deletes the raw file, so the timeout check must run first.
    /// `RecordingLifecycle` needs a live recorder, so the order is read from source.
    @Test func timedOutStopIsHandledBeforeTheCancelledBranchDeletesTheFile() throws {
        let body = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/AudioRecording/Recording/RecordingLifecycle.swift",
            from: "func stopRecording(cancelled: Bool = false) async",
            to: "if let rawURL = ownedRawURL, let dstURL = ownedFinalURL {"
        )

        let timeoutCheck = try #require(body.range(of: "if recorderStopOutcome == .timedOut {"))
        let cancelledBranch = try #require(body.range(of: "if cancelled {"))
        #expect(timeoutCheck.lowerBound < cancelledBranch.lowerBound)
    }

    /// PR #1767 review: a new recording can start while this stop waits up to 5 s
    /// for the recorder. The stop must take its URLs and session row before that
    /// wait, and after it touch only what it took, never the shared fields the new
    /// recording has written by then.
    @Test func stopTakesItsFilesAndSessionBeforeTheRecorderWait() throws {
        let body = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/AudioRecording/Recording/RecordingLifecycle.swift",
            from: "func stopRecording(cancelled: Bool = false) async",
            to: "private struct ConversionRetryResult {"
        )

        let detach = try #require(body.range(of: "sessionManager.detachCurrentSession()"))
        let wait = try #require(body.range(of: "await simpleRecorder.stopRecording()"))
        #expect(detach.lowerBound < wait.lowerBound)

        let afterWait = String(body[wait.upperBound...])
        for shared in ["self.finalURL", "self.rawURL", " rawURL = nil", "resolveCurrentSession()",
                       "clearCurrentSession()", "deleteCurrentSession()", "currentRecordingSession"] {
            #expect(!afterWait.contains(shared), "stop reads or clears shared state after its wait: \(shared)")
        }
    }

    /// PR #1767 review: the validation-cancel path also shows the timeout error, and
    /// no cancel caller deletes "the current session", which may be a newer recording's.
    @Test func cancelCallersShowTheTimeoutAndLeaveTheCurrentSessionAlone() throws {
        let cancel = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/AudioRecording/RecordingFlow/RecordingTranscriptionFlow+StartRecording.swift",
            from: "func cancelRecordingWithError(",
            to: "private func armRecordingMaxDurationTimer("
        )
        #expect(cancel.contains("recordingLifecycle.lastStopTimedOut"))
        #expect(cancel.contains("AudioError.recorderStopTimedOut.localizedDescription"))
        #expect(!cancel.contains("deleteCurrentSession()"))

        let userCancel = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/AudioRecording/RecordingFlow/RecordingTranscriptionFlow+StopRecording.swift",
            from: "wavReadyMs = Int(Date().timeIntervalSince(wavReadyStart) * 1000)",
            to: "let audioURL = result.url!"
        )
        #expect(!userCancel.contains("deleteCurrentSession()"))
    }
}
