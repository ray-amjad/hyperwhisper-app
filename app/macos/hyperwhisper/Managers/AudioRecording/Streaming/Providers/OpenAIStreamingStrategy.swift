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

    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "OpenAIStreaming")
    private let decoder = JSONDecoder()
    private let commitInterval: TimeInterval = 1.2
    private var committedItemTranscripts: [String: String] = [:]
    private var partialItemTranscripts: [String: String] = [:]
    private var hasUncommittedAudio = false
    private var lastCommitTime = Date()

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
        hasUncommittedAudio = false
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
        hasUncommittedAudio = true
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
        [
            .sendText(Self.commitMessage),
            .wait(1.0),
            .closeWebSocket
        ]
    }

    func onAudioSendOpportunity(webSocketSend: @escaping (URLSessionWebSocketTask.Message) -> Void) {
        guard hasUncommittedAudio, Date().timeIntervalSince(lastCommitTime) >= commitInterval else {
            return
        }

        webSocketSend(.string(Self.commitMessage))
        hasUncommittedAudio = false
        lastCommitTime = Date()
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
