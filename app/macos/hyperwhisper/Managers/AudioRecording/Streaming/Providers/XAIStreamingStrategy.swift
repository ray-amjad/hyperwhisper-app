//
//  XAIStreamingStrategy.swift
//  hyperwhisper
//
//  Direct WebSocket streaming to xAI's speech-to-text API using the user's
//  own Grok/xAI API key.
//

import Foundation
import OSLog

final class XAIStreamingStrategy: StreamingProviderStrategy {
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "XAIStreaming")
    private var committedTranscript = ""

    func buildWebSocketURL(config: StreamingSessionConfig) -> URL? {
        guard let apiKey = config.apiKey, !apiKey.isEmpty else {
            logger.error("Cannot build xAI URL: API key is missing")
            return nil
        }

        var components = URLComponents()
        components.scheme = "wss"
        components.host = "api.x.ai"
        components.path = "/v1/stt"

        var queryItems: [URLQueryItem] = [
            URLQueryItem(name: "sample_rate", value: "16000"),
            URLQueryItem(name: "encoding", value: "pcm"),
            URLQueryItem(name: "interim_results", value: "true"),
            URLQueryItem(name: "endpointing", value: "300")
        ]

        if let language = Self.supportedFormattingLanguage(for: config.language) {
            queryItems.append(URLQueryItem(name: "language", value: language))
            logger.info("xAI formatting language set: \(language, privacy: .public)")
        } else if let language = config.language, !language.isEmpty {
            logger.info("xAI formatting disabled: \(language, privacy: .public) is not in supported formatting set")
        }

        components.queryItems = queryItems
        return components.url
    }

    func buildWebSocketRequest(url: URL, config: StreamingSessionConfig) -> URLRequest? {
        guard let apiKey = config.apiKey, !apiKey.isEmpty else {
            logger.error("Cannot build xAI request: API key is missing")
            return nil
        }

        var request = URLRequest(url: url)
        request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        return request
    }

    func startMessages(config: StreamingSessionConfig) -> [URLSessionWebSocketTask.Message] {
        // Reset the committed-transcript baseline so a reconnect doesn't
        // prefix-strip xAI's fresh server-side transcript against stale
        // pre-disconnect state. xAI sends no startup config message, so the
        // session begins on socket open and we return no messages here.
        committedTranscript = ""
        return []
    }

    func encodeAudioChunk(_ pcmData: Data) -> URLSessionWebSocketTask.Message {
        .data(pcmData)
    }

    func parseMessage(_ text: String) -> StreamingProviderEvent? {
        guard let data = text.data(using: .utf8) else {
            logger.error("xAI parseMessage: failed to convert text to UTF-8 data")
            return nil
        }

        let message: XAIMessage
        do {
            message = try JSONDecoder().decode(XAIMessage.self, from: data)
        } catch {
            logger.warning("xAI parseMessage: failed to decode JSON: \(error.localizedDescription, privacy: .public)")
            return nil
        }

        switch message.type {
        case "transcript.created":
            logger.info("xAI transcript session created")
            return .sessionStarted(sessionId: nil)

        case "transcript.partial":
            guard let transcript = message.text, !transcript.isEmpty else { return nil }
            if message.is_final == true {
                guard let delta = committedDelta(from: transcript), !delta.isEmpty else {
                    return nil
                }
                return .finalTranscript(text: delta)
            }
            return .partialTranscript(text: transcript)

        case "transcript.done":
            if let transcript = message.text,
               let delta = committedDelta(from: transcript),
               !delta.isEmpty {
                return .finalTranscriptAndSessionComplete(
                    text: delta,
                    durationSeconds: message.duration ?? 0,
                    creditsUsed: 0
                )
            }
            return .sessionComplete(durationSeconds: message.duration ?? 0, creditsUsed: 0)

        case "error":
            return .error(message: message.message ?? "xAI streaming transcription failed")

        default:
            logger.debug("xAI unrecognized message type: \(message.type, privacy: .public)")
            return nil
        }
    }

    func stopSequence() -> [StreamingStopStep] {
        [
            .sendText(#"{"type":"audio.done"}"#),
            .waitForSessionComplete(timeout: 10.0),
            .closeWebSocket
        ]
    }

    var transcriptionProviderLabel: String { "xAI (Streaming)" }

    var supportsVocabulary: Bool { false }
}

private extension XAIStreamingStrategy {
    static let supportedFormattingLanguages: Set<String> = [
        "ar", "cs", "da", "de", "en", "es", "fa", "fil", "fr", "hi",
        "id", "it", "ja", "ko", "mk", "ms", "nl", "pl", "pt", "ro",
        "ru", "sv", "th", "tr", "vi"
    ]

    static let languageAliases: [String: String] = [
        "tl": "fil"
    ]

    static func supportedFormattingLanguage(for code: String?) -> String? {
        guard let raw = code?.trimmingCharacters(in: .whitespacesAndNewlines),
              !raw.isEmpty else { return nil }
        let lower = raw.lowercased()
        if lower == "auto" { return nil }
        let primary = lower.split(separator: "-").first.map(String.init) ?? lower
        let normalized = languageAliases[primary] ?? primary
        return supportedFormattingLanguages.contains(normalized) ? normalized : nil
    }

    func committedDelta(from transcript: String) -> String? {
        let normalized = transcript.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !normalized.isEmpty else { return nil }

        if committedTranscript.isEmpty {
            committedTranscript = normalized
            return normalized
        }

        if normalized.hasPrefix(committedTranscript) {
            let suffix = normalized.dropFirst(committedTranscript.count)
                .trimmingCharacters(in: .whitespacesAndNewlines)
            committedTranscript = normalized
            return suffix.isEmpty ? nil : String(suffix)
        }

        if committedTranscript.hasPrefix(normalized) {
            return nil
        }

        committedTranscript += " " + normalized
        return normalized
    }
}

private struct XAIMessage: Decodable {
    let type: String
    let text: String?
    let is_final: Bool?
    let duration: Double?
    let message: String?
}
