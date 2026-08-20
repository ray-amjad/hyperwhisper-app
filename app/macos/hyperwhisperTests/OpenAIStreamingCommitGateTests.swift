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
    /// the production events reported, and below the server's 100 ms floor
    /// (4800 bytes).
    private static let subThresholdChunk = Data(count: 4080)

    /// 4800 bytes = 2400 samples = EXACTLY 100 ms at 24 kHz — the boundary. The
    /// server's rule is "at least 100ms", so this must commit, and the gate must
    /// carry no margin over it. It is also the exact size of every Windows
    /// capture chunk (`CaptureBufferMilliseconds = 100`), which is why a
    /// threshold one byte higher would silently drop a whole final buffer there.
    private static let exactlyAtMinimumChunk = Data(count: 4800)

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

    @Test func stopSequenceCommitsAudioSittingExactlyOnTheServerMinimum() {
        let strategy = OpenAIStreamingStrategy()
        _ = strategy.encodeAudioChunk(Self.exactlyAtMinimumChunk)

        let steps = strategy.stopSequence()

        #expect(steps.count == 3)
        #expect(Self.isSendText(steps[0]) == true)
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

    // MARK: - Periodic commit path

    @Test func periodicCommitHoldsBackAudioUnderTheServerMinimum() {
        let clock = TestClock()
        let strategy = OpenAIStreamingStrategy(now: { clock.now })
        let sent = SentMessageRecorder()

        _ = strategy.encodeAudioChunk(Self.subThresholdChunk)
        clock.advance(2.0)
        strategy.onAudioSendOpportunity { sent.record($0) }

        #expect(sent.commitCount == 0)
    }

    @Test func periodicCommitSendsExactlyOneFrameOnceTheMinimumIsMet() {
        let clock = TestClock()
        let strategy = OpenAIStreamingStrategy(now: { clock.now })
        let sent = SentMessageRecorder()

        _ = strategy.encodeAudioChunk(Self.committableChunk)
        clock.advance(2.0)
        strategy.onAudioSendOpportunity { sent.record($0) }

        #expect(sent.commitCount == 1)

        // A commit DOES stamp `lastCommitTime`, so the very next opportunity —
        // with the clock held still — must stay quiet.
        _ = strategy.encodeAudioChunk(Self.committableChunk)
        strategy.onAudioSendOpportunity { sent.record($0) }

        #expect(sent.commitCount == 1)
    }

    @Test func periodicCommitFiresOnTheNextQualifyingChunkAfterAByteGateRejection() {
        let clock = TestClock()
        let strategy = OpenAIStreamingStrategy(now: { clock.now })
        let sent = SentMessageRecorder()

        // The interval has elapsed but only 85 ms has accumulated, so the byte
        // gate rejects — and must leave `lastCommitTime` STALE.
        _ = strategy.encodeAudioChunk(Self.subThresholdChunk)
        clock.advance(2.0)
        strategy.onAudioSendOpportunity { sent.record($0) }

        #expect(sent.commitCount == 0)

        // The clock does not move again. 4080 + 4080 = 8160 bytes clears the
        // 4800-byte floor, so this must commit immediately. Had the rejection
        // above stamped `lastCommitTime`, nothing could commit for another 1.2 s.
        _ = strategy.encodeAudioChunk(Self.subThresholdChunk)
        strategy.onAudioSendOpportunity { sent.record($0) }

        #expect(sent.commitCount == 1)
    }

    // MARK: - Periodic path and stop sequence composed on one session

    /// The bug this whole change exists to kill, reproduced end to end on a
    /// single strategy instance rather than in two isolated halves.
    ///
    /// Every other case here drives EITHER the periodic path OR the stop
    /// sequence, so both stay green even if the periodic path stopped zeroing
    /// the counter — and then a real session that periodically commits 250 ms
    /// and then captures one 85 ms buffer before the user releases the key
    /// would see 12000 + 4080 bytes still pending at stop, clear the gate on
    /// audio the server already has, and emit a commit covering 85 ms. That is
    /// exactly the rejected frame of HYPERWHISPER-S8 / S9. The periodic commit
    /// must CONSUME its bytes, not merely observe them.
    @Test func stopAfterAPeriodicCommitDropsASubThresholdTail() {
        let clock = TestClock()
        let strategy = OpenAIStreamingStrategy(now: { clock.now })
        let sent = SentMessageRecorder()

        _ = strategy.encodeAudioChunk(Self.committableChunk)
        clock.advance(2.0)
        strategy.onAudioSendOpportunity { sent.record($0) }

        #expect(sent.commitCount == 1)

        // The tail captured after that commit, and nothing more.
        _ = strategy.encodeAudioChunk(Self.subThresholdChunk)
        let steps = strategy.stopSequence()

        #expect(steps.contains(where: Self.isSendText) == false)
        #expect(steps.count == 2)
    }

    /// The other half of the same composition: consuming the bytes at the
    /// periodic commit must not make the stop sequence permanently silent. A
    /// tail that clears the floor on its own still has to be committed.
    @Test func stopAfterAPeriodicCommitStillCommitsATailOverTheMinimum() {
        let clock = TestClock()
        let strategy = OpenAIStreamingStrategy(now: { clock.now })
        let sent = SentMessageRecorder()

        _ = strategy.encodeAudioChunk(Self.committableChunk)
        clock.advance(2.0)
        strategy.onAudioSendOpportunity { sent.record($0) }

        #expect(sent.commitCount == 1)

        _ = strategy.encodeAudioChunk(Self.committableChunk)
        let steps = strategy.stopSequence()

        #expect(steps.count == 3)

        if case .sendText(let text) = steps[0] {
            #expect(text.contains("input_audio_buffer.commit"))
        } else {
            Issue.record("Expected the commit frame to lead the stop sequence, got \(steps[0])")
        }
    }
}

// MARK: - Test doubles

/// A clock the test moves by hand — the injected `now` closure reads this box,
/// so the periodic path can be driven without any wall-clock sleeping.
private final class TestClock {
    private(set) var now = Date(timeIntervalSince1970: 1_700_000_000)

    func advance(_ seconds: TimeInterval) {
        now = now.addingTimeInterval(seconds)
    }
}

/// Collects whatever the strategy hands to `webSocketSend`.
private final class SentMessageRecorder {
    private(set) var messages: [URLSessionWebSocketTask.Message] = []

    func record(_ message: URLSessionWebSocketTask.Message) {
        messages.append(message)
    }

    var commitCount: Int {
        messages.reduce(into: 0) { total, message in
            if case .string(let text) = message, text.contains("input_audio_buffer.commit") {
                total += 1
            }
        }
    }
}
