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
    var ranOnMainThread: Bool? { state.withLock { $0.ranOnMainThread } }

    func read() -> Float {
        let onMain = Thread.isMainThread
        state.withLock { current in
            current.readCount += 1
            current.ranOnMainThread = onMain
        }
        entered.signal()
        if blocks {
            // Only the test's `release()` should end the block (#1216).
            _ = releaseSemaphore.wait(timeout: .now() + 60)
        }
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

        recorder.stopRecording()
        // `stopRecording()` empties the slot, which alone would drop the level.
        // Put the same recorder back so the identity guard passes: the
        // `startGeneration` bump from the stop is then the only thing that can
        // drop this stale read.
        recorder.installRecorderForTesting(avRecorder)
        reader.release()

        #expect(await Self.waitUntilReadSettles(recorder))
        #expect(recorder.audioLevel == 0)
    }
}
