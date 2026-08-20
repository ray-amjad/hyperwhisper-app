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
    /// HYPERWHISPER-S9). 0.12 keeps a 20% margin over that server rule so a
    /// single short resampler chunk can't leave us one buffer under the line.
    private static let minimumCommitSeconds: Double = 0.12

    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "OpenAIStreaming")
    private let decoder = JSONDecoder()
    private let commitInterval: TimeInterval = 1.2
    private var committedItemTranscripts: [String: String] = [:]
    private var partialItemTranscripts: [String: String] = [:]
    private var lastCommitTime = Date()

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
    /// `stopSession()` via `stopSequence()`. `os_unfair_lock` is the lightest
    /// primitive (~1-5ns vs ~100-200ns for NSLock) and this runs on every audio
    /// buffer (~10 times/sec) — same pattern as `DeepgramStreamingStrategy`.
    private var _pendingAudioBytes = 0
    private var pendingAudioBytesLock = os_unfair_lock()

    /// `minimumCommitSeconds` expressed in bytes of 16-bit mono PCM at
    /// `audioSampleRate` (2 bytes per sample): 5760 @ 24 kHz. The server's own
    /// 100 ms floor is 4800 bytes.
    private var minimumCommitBytes: Int {
        Int(audioSampleRate * 2 * Self.minimumCommitSeconds)
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
        lastCommitTime = Date()

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
        if consumeCommittableAudio() {
            steps.append(.sendText(Self.commitMessage))
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
        guard Date().timeIntervalSince(lastCommitTime) >= commitInterval else {
            return
        }

        // Deliberately leaves `lastCommitTime` stale when the threshold is not
        // met: that is what makes the commit fire on the next chunk that clears
        // it, rather than waiting out another full interval.
        guard consumeCommittableAudio() else {
            return
        }

        webSocketSend(.string(Self.commitMessage))
        lastCommitTime = Date()
    }

    // MARK: - Pending Audio Accounting

    /// Record PCM bytes handed to the WebSocket since the last commit.
    private func notePendingAudio(byteCount: Int) {
        os_unfair_lock_lock(&pendingAudioBytesLock)
        _pendingAudioBytes += byteCount
        os_unfair_lock_unlock(&pendingAudioBytesLock)
    }

    /// Claim the accumulated audio for a commit frame.
    ///
    /// Returns true (and zeroes the counter) only when enough has accumulated to
    /// clear the server's minimum. The check and the reset happen under a single
    /// lock acquisition so the periodic path and the stop sequence can never both
    /// claim the same bytes and emit two commits for one buffer.
    private func consumeCommittableAudio() -> Bool {
        os_unfair_lock_lock(&pendingAudioBytesLock)
        defer { os_unfair_lock_unlock(&pendingAudioBytesLock) }

        guard _pendingAudioBytes >= minimumCommitBytes else { return false }
        _pendingAudioBytes = 0
        return true
    }

    /// Drop any accumulated audio — a new session starts with an empty buffer.
    private func resetPendingAudio() {
        os_unfair_lock_lock(&pendingAudioBytesLock)
        _pendingAudioBytes = 0
        os_unfair_lock_unlock(&pendingAudioBytesLock)
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
