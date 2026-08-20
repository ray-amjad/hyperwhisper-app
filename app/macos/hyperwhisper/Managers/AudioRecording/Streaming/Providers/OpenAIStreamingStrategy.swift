//
//  OpenAIStreamingStrategy.swift
//  hyperwhisper
//
//  Direct WebSocket streaming to OpenAI Realtime transcription using
//  gpt-realtime-whisper.
//

import Foundation
import OSLog

final class OpenAIStreamingStrategy: StreamingProviderStrategy {
    private enum EventType {
        static let sessionUpdate = "session.update"
        static let appendAudio = "input_audio_buffer.append"
        static let commitAudio = "input_audio_buffer.commit"
        static let sessionUpdated = "session.updated"
        static let transcriptionDelta = "conversation.item.input_audio_transcription.delta"
        static let transcriptionCompleted = "conversation.item.input_audio_transcription.completed"
        static let error = "error"
    }

    private static let modelId = "gpt-realtime-whisper"
    private static let commitMessage = #"{"type":"\#(EventType.commitAudio)"}"#

    /// Minimum amount of appended audio a commit frame is allowed to cover.
    ///
    /// OpenAI Realtime rejects `input_audio_buffer.commit` with
    /// "buffer too small. Expected at least 100ms of audio" when less than 100 ms
    /// has been appended since the previous commit (HYPERWHISPER-S8 /
    /// HYPERWHISPER-S9). This is the server's rule EXACTLY — no safety margin.
    ///
    /// A margin would be actively harmful here. The counter below is an exact
    /// running sum of appended PCM bytes, so there is no imprecision for a margin
    /// to absorb; all a stricter threshold buys is a dead band in which we
    /// silently discard a tail the server would have accepted. `turn_detection`
    /// is null, so there is no server-side VAD auto-commit to rescue it.
    private static let minimumCommitMilliseconds = 100

    /// 16-bit mono PCM.
    private static let bytesPerSample = 2

    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "OpenAIStreaming")
    private let decoder = JSONDecoder()
    private let commitInterval: TimeInterval
    private let now: () -> Date
    private var committedItemTranscripts: [String: String] = [:]
    private var partialItemTranscripts: [String: String] = [:]
    private var lastCommitTime: Date

    /// Bytes of PCM appended since the last commit frame was sent.
    ///
    /// A plain "did any audio arrive" flag is not enough: the send-opportunity
    /// hook runs BEFORE the append, so right after a periodic commit exactly one
    /// capture buffer is outstanding — and that buffer can be short. Counting
    /// bytes is what lets both the periodic path and the stop sequence answer
    /// "how much", not just "whether".
    ///
    /// THREAD SAFETY:
    /// Written from the audio capture callback (non-main thread) via
    /// `encodeAudioChunk`/`onAudioSendOpportunity` and read from `@MainActor`
    /// `stopSession()` via `stopSequence()`. The lock is an `NSLock` rather than
    /// an `os_unfair_lock`: `os_unfair_lock` requires a stable, unique address
    /// for its whole lifetime, and Swift only guarantees that a `&someProperty`
    /// pointer is valid for the duration of the call it is passed to. `NSLock`
    /// is a class, so its identity is stable by construction. The tap runs at
    /// ~47 buffers/sec (1024 frames at 48 kHz) or faster, a rate at which the
    /// per-acquisition difference between the two is irrelevant.
    private var _pendingAudioBytes = 0
    private let pendingAudioBytesLock = NSLock()

    /// `minimumCommitMilliseconds` expressed in bytes of 16-bit mono PCM at
    /// `audioSampleRate`: exactly 4800 @ 24 kHz, which is the server's own
    /// floor. Integer arithmetic throughout, so the boundary lands on 4800 and
    /// not on a float that rounds to 4799.
    private var minimumCommitBytes: Int {
        Int(audioSampleRate) * Self.bytesPerSample * Self.minimumCommitMilliseconds / 1000
    }

    /// - Parameters:
    ///   - commitInterval: How long between periodic commits. Injectable so
    ///     tests can drive the periodic path without wall-clock sleeps; the
    ///     default is the production value.
    ///   - now: Clock, injectable for the same reason.
    init(commitInterval: TimeInterval = 1.2, now: @escaping () -> Date = { Date() }) {
        self.commitInterval = commitInterval
        self.now = now
        self.lastCommitTime = now()
    }

    var transcriptionProviderLabel: String { "OpenAI (Streaming)" }
    var supportsVocabulary: Bool { false }
    var audioSampleRate: Double { 24000 }

    func buildWebSocketURL(config: StreamingSessionConfig) -> URL? {
        URL(string: "wss://api.openai.com/v1/realtime?intent=transcription")
    }

    func buildWebSocketRequest(url: URL, config: StreamingSessionConfig) -> URLRequest? {
        guard let apiKey = config.apiKey, !apiKey.isEmpty else {
            logger.error("Cannot build OpenAI Realtime request: API key is missing")
            return nil
        }

        var request = URLRequest(url: url)
        request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        return request
    }

    func startMessages(config: StreamingSessionConfig) -> [URLSessionWebSocketTask.Message] {
        committedItemTranscripts.removeAll(keepingCapacity: true)
        partialItemTranscripts.removeAll(keepingCapacity: true)
        resetPendingAudio()
        lastCommitTime = self.now()

        var transcription: [String: Any] = [
            "model": Self.modelId
        ]

        if let language = normalizedLanguageCode(config.language) {
            transcription["language"] = language
        }

        let payload: [String: Any] = [
            "type": EventType.sessionUpdate,
            "session": [
                "type": "transcription",
                "audio": [
                    "input": [
                        "format": [
                            "type": "audio/pcm",
                            "rate": Int(audioSampleRate)
                        ],
                        "transcription": transcription,
                        "turn_detection": NSNull()
                    ]
                ]
            ]
        ]

        guard let data = try? JSONSerialization.data(withJSONObject: payload),
              let json = String(data: data, encoding: .utf8) else {
            logger.error("Failed to encode OpenAI Realtime session.update")
            return []
        }

        return [.string(json)]
    }

    func encodeAudioChunk(_ pcmData: Data) -> URLSessionWebSocketTask.Message {
        notePendingAudio(byteCount: pcmData.count)
        let base64 = pcmData.base64EncodedString()
        let json = #"{"type":"\#(EventType.appendAudio)","audio":"\#(base64)"}"#
        return .string(json)
    }

    func parseMessage(_ text: String) -> StreamingProviderEvent? {
        guard let data = text.data(using: .utf8) else { return nil }

        let message: OpenAIRealtimeMessage
        do {
            message = try decoder.decode(OpenAIRealtimeMessage.self, from: data)
        } catch {
            logger.warning("OpenAI parseMessage: failed to decode JSON: \(error.localizedDescription, privacy: .public)")
            return nil
        }

        switch message.type {
        case EventType.sessionUpdated:
            return .sessionStarted(sessionId: message.session?.id)

        case EventType.transcriptionDelta:
            guard let delta = message.delta, !delta.isEmpty else { return nil }
            if let itemId = message.item_id {
                let partial = (partialItemTranscripts[itemId] ?? "") + delta
                partialItemTranscripts[itemId] = partial
                return .partialTranscript(text: partial)
            }
            return .partialTranscript(text: delta)

        case EventType.transcriptionCompleted:
            guard let transcript = message.transcript,
                  let itemId = message.item_id,
                  let delta = committedDelta(itemId: itemId, transcript: transcript),
                  !delta.isEmpty else {
                return nil
            }
            return .finalTranscript(text: delta)

        case EventType.error:
            return .error(message: message.error?.message ?? "OpenAI Realtime transcription failed")

        default:
            return nil
        }
    }

    func stopSequence() -> [StreamingStopStep] {
        var steps: [StreamingStopStep] = []

        // COMMIT ONLY WHAT THE SERVER WILL ACCEPT:
        // A stop that lands shortly after a periodic commit leaves a tail of
        // under 100 ms outstanding, and committing that is rejected outright
        // ("buffer too small", HYPERWHISPER-S8 / HYPERWHISPER-S9) — which the
        // client surfaces as a spurious streaming-error toast. Dropping a tail
        // that short is the accepted trade: it is silence-or-a-syllable, and it
        // used to be lost to the rejection anyway.
        let claim = consumeCommittableAudio()
        if claim.claimed {
            steps.append(.sendText(Self.commitMessage))
        } else if claim.pendingBytes > 0 {
            // The one place audio is genuinely discarded rather than deferred —
            // log it so a transcript missing its last syllable can be correlated
            // with something. Deliberately NOT a Sentry capture: this is the
            // expected benign case this change exists to stop reporting. Runs
            // once per session, so it is not in any hot path.
            logger.warning(
                "Dropping \(claim.pendingBytes, privacy: .public) pending audio bytes at stop: under the \(self.minimumCommitBytes, privacy: .public)-byte (\(Self.minimumCommitMilliseconds, privacy: .public) ms) OpenAI Realtime commit minimum"
            )
        }

        // KEEP THE WAIT EVEN WHEN NOTHING WAS COMMITTED:
        // The receive loop is still live at this point, and the
        // `conversation.item.input_audio_transcription.completed` for the LAST
        // PERIODIC commit can still be in flight — exactly the timing window
        // this bug lives in. Closing immediately would trade the toast for a
        // truncated transcript.
        steps.append(.wait(1.0))
        steps.append(.closeWebSocket)

        return steps
    }

    func onAudioSendOpportunity(webSocketSend: @escaping (URLSessionWebSocketTask.Message) -> Void) {
        guard self.now().timeIntervalSince(lastCommitTime) >= commitInterval else {
            return
        }

        // Deliberately leaves `lastCommitTime` stale when the threshold is not
        // met: that is what makes the commit fire on the next chunk that clears
        // it, rather than waiting out another full interval. Nothing is lost on
        // this path — the bytes stay pending — so it deliberately does not log:
        // it runs on every captured buffer until the gate clears.
        guard consumeCommittableAudio().claimed else {
            return
        }

        webSocketSend(.string(Self.commitMessage))
        lastCommitTime = self.now()
    }

    // MARK: - Pending Audio Accounting

    /// Record PCM bytes handed to the WebSocket since the last commit.
    private func notePendingAudio(byteCount: Int) {
        pendingAudioBytesLock.lock()
        _pendingAudioBytes += byteCount
        pendingAudioBytesLock.unlock()
    }

    /// Claim the accumulated audio for a commit frame.
    ///
    /// Claims (and zeroes) the counter only when at least
    /// `minimumCommitBytes` has accumulated — the server's "at least 100ms"
    /// rule, so exactly 100 ms qualifies. The check and the reset happen under a
    /// single lock acquisition so the periodic path and the stop sequence can
    /// never both claim the same bytes and emit two commits for one buffer.
    ///
    /// - Returns: `claimed` — whether a commit frame may now be sent — and
    ///   `pendingBytes`, the counter's value as the decision was made (for logs).
    private func consumeCommittableAudio() -> (claimed: Bool, pendingBytes: Int) {
        pendingAudioBytesLock.lock()
        defer { pendingAudioBytesLock.unlock() }

        let pending = _pendingAudioBytes
        guard pending >= minimumCommitBytes else { return (false, pending) }
        _pendingAudioBytes = 0
        return (true, pending)
    }

    /// Drop any accumulated audio — a new session starts with an empty buffer.
    private func resetPendingAudio() {
        pendingAudioBytesLock.lock()
        _pendingAudioBytes = 0
        pendingAudioBytesLock.unlock()
    }
}

private extension OpenAIStreamingStrategy {
    func normalizedLanguageCode(_ code: String?) -> String? {
        guard let raw = code?.trimmingCharacters(in: .whitespacesAndNewlines),
              !raw.isEmpty else { return nil }
        let lower = raw.lowercased()
        if lower == "auto" { return nil }
        return lower.split(separator: "-").first.map(String.init) ?? lower
    }

    func committedDelta(itemId: String, transcript: String) -> String? {
        let normalized = transcript.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !normalized.isEmpty else { return nil }

        let previous = committedItemTranscripts[itemId] ?? ""
        committedItemTranscripts[itemId] = normalized
        partialItemTranscripts.removeValue(forKey: itemId)

        if previous.isEmpty { return normalized }
        if normalized.hasPrefix(previous) {
            let suffix = normalized.dropFirst(previous.count).trimmingCharacters(in: .whitespacesAndNewlines)
            return suffix.isEmpty ? nil : String(suffix)
        }
        return normalized
    }
}

private struct OpenAIRealtimeMessage: Decodable {
    let type: String
    let session: OpenAIRealtimeSession?
    let item_id: String?
    let delta: String?
    let transcript: String?
    let error: OpenAIRealtimeError?
}

private struct OpenAIRealtimeSession: Decodable {
    let id: String?
}

private struct OpenAIRealtimeError: Decodable {
    let message: String?
}
