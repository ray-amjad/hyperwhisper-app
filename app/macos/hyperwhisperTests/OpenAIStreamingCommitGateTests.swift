//
//  OpenAIStreamingCommitGateTests.swift
//  hyperwhisperTests
//
//  Pins the accumulated-audio gate on OpenAI Realtime's
//  `input_audio_buffer.commit` frame (HYPERWHISPER-S8 / HYPERWHISPER-S9).
//
//  The server rejects a commit that covers less than 100 ms of appended audio.
//  The stop sequence used to send one unconditionally, so a stop landing shortly
//  after a periodic commit committed a sub-threshold tail and drew a
//  "buffer too small" error frame plus a spurious streaming-error toast.
//
//  Fixtures are zero-filled PCM buffers only: no real endpoints, keys,
//  organisation ids or user ids.
//

import Foundation
import Testing
@testable import HyperWhisper

struct OpenAIStreamingCommitGateTests {

    // MARK: - Fixtures

    /// 4080 bytes = 2040 samples = 85 ms of 24 kHz 16-bit mono PCM — the shape
    /// the production events reported. Below both the server's 100 ms floor
    /// (4800 bytes) and the strategy's 120 ms threshold (5760 bytes).
    private static let subThresholdChunk = Data(count: 4080)

    /// 12000 bytes = 6000 samples = 250 ms — comfortably over the threshold.
    private static let committableChunk = Data(count: 12000)

    private static func isSendText(_ step: StreamingStopStep) -> Bool {
        if case .sendText = step { return true }
        return false
    }

    private static func makeConfig() -> StreamingSessionConfig {
        StreamingSessionConfig(
            licenseKey: nil,
            deviceId: nil,
            language: "en",
            vocabulary: nil,
            apiKey: nil,
            model: nil,
            fastFormatting: false
        )
    }

    // MARK: - Tests

    @Test func stopSequenceOmitsCommitBelowTheServerMinimum() {
        let strategy = OpenAIStreamingStrategy()
        _ = strategy.encodeAudioChunk(Self.subThresholdChunk)

        let steps = strategy.stopSequence()
        let commits = steps.contains(where: Self.isSendText)

        #expect(commits == false)
        #expect(steps.count == 2)

        if case .wait(let seconds) = steps[0] {
            #expect(seconds == 1.0)
        } else {
            Issue.record("Expected the stop sequence to still wait, got \(steps[0])")
        }

        if case .closeWebSocket = steps[1] {
            // Expected.
        } else {
            Issue.record("Expected the stop sequence to still close the WebSocket, got \(steps[1])")
        }
    }

    @Test func stopSequenceCommitsOnceEnoughAudioHasAccumulated() {
        let strategy = OpenAIStreamingStrategy()
        _ = strategy.encodeAudioChunk(Self.committableChunk)

        let steps = strategy.stopSequence()

        #expect(steps.count == 3)

        if case .sendText(let text) = steps[0] {
            #expect(text.contains("input_audio_buffer.commit"))
        } else {
            Issue.record("Expected the commit frame to lead the stop sequence, got \(steps[0])")
        }
    }

    @Test func stopSequenceCommitsTheSameAudioOnlyOnce() {
        let strategy = OpenAIStreamingStrategy()
        _ = strategy.encodeAudioChunk(Self.committableChunk)

        let first = strategy.stopSequence()
        let second = strategy.stopSequence()
        let firstCommits = first.contains(where: Self.isSendText)
        let secondCommits = second.contains(where: Self.isSendText)

        #expect(firstCommits == true)
        #expect(secondCommits == false)
        #expect(second.count == 2)
    }

    @Test func startMessagesClearsAudioAccumulatedByAPreviousSession() {
        let strategy = OpenAIStreamingStrategy()
        _ = strategy.encodeAudioChunk(Self.committableChunk)

        _ = strategy.startMessages(config: Self.makeConfig())
        let steps = strategy.stopSequence()
        let commits = steps.contains(where: Self.isSendText)

        #expect(commits == false)
        #expect(steps.count == 2)
    }
}
