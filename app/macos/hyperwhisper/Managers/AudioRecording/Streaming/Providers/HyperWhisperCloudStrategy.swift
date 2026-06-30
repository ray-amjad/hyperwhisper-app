//
//  HyperWhisperCloudStrategy.swift
//  hyperwhisper
//
//  HYPERWHISPER CLOUD STREAMING STRATEGY
//  Implements the StreamingProviderStrategy protocol for HyperWhisper Cloud,
//  the default streaming provider that proxies through Fly.io to Deepgram Live API.
//
//  ARCHITECTURE:
//  ┌─────────────────┐     ┌───────────────────────┐     ┌──────────────┐
//  │  Audio Engine   │────▶│  HyperWhisper Cloud   │────▶│   Deepgram   │
//  │  (16kHz PCM)    │     │  (WebSocket Proxy)    │     │  (Nova-3)    │
//  └─────────────────┘     └───────────────────────┘     └──────────────┘
//
//  This strategy was extracted from StreamingTranscriptionClient to support
//  the unified streaming provider pattern. It encapsulates:
//  - WebSocket URL construction with license/device auth via query params
//  - Raw binary PCM audio encoding (no wrapping needed)
//  - HW Cloud server message JSON parsing → normalized StreamingProviderEvent
//  - Graceful shutdown sequence (stop JSON → wait → close)
//
//  PROTOCOL (HW Cloud → Client):
//  - {"type":"ready", "sessionId":"..."}
//  - {"type":"transcript", "text":"...", "is_final":true/false, "speech_final":true/false}
//  - {"type":"session_complete", "duration_seconds":X, "credits_used":Y}
//  - {"type":"error", "message":"..."}
//
//  PROTOCOL (Client → HW Cloud):
//  - Binary: Raw 16kHz mono Int16 PCM audio chunks
//  - JSON: {"type":"stop"} to end session
//
//  AUTH:
//  Authentication is done via query parameters (not headers):
//  - Licensed users: ?license_key=...
//  - Trial users: ?device_id=...
//
//  VOCABULARY:
//  Custom vocabulary terms are passed via the `vocabulary` query parameter.
//  The backend converts these to Deepgram `keyterm` parameters with boost intensifiers.
//  Only works when language is explicitly set (not auto-detect).
//

import Foundation
import os

// MARK: - HyperWhisper Cloud Streaming Strategy

/// Streaming strategy for HyperWhisper Cloud, the default provider.
///
/// Routes audio through HyperWhisper's Fly.io edge servers to Deepgram Live API.
/// Handles credit management, vocabulary boosting, and post-processing on the server side.
///
/// WHY SEPARATE FROM DIRECT DEEPGRAM:
/// HW Cloud adds server-side value: credit management, vocabulary boosting for auto-detect,
/// post-processing pipeline, and multi-region edge routing. Direct Deepgram bypasses all
/// of this for users who want raw speed and have their own API key.
class HyperWhisperCloudStrategy: StreamingProviderStrategy {

    // MARK: - Private Types

    /// Message received from the HyperWhisper Cloud streaming server.
    ///
    /// Maps to the WebSocket protocol defined in the backend's streaming endpoint.
    /// All fields are optional because different message types use different fields:
    /// - "ready": sessionId
    /// - "transcript": text, is_final, speech_final
    /// - "session_complete": duration_seconds, credits_used
    /// - "error": message
    private struct ServerMessage: Decodable {
        let type: String
        let sessionId: String?
        let text: String?
        let is_final: Bool?
        let duration_seconds: Double?
        let credits_used: Double?
        let message: String?
        let remaining_seconds: Double?
    }

    // MARK: - Private Properties

    /// Logger for HyperWhisper Cloud strategy operations
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "HWCloudStrategy")

    // MARK: - StreamingProviderStrategy Conformance

    /// Build the WebSocket URL for HyperWhisper Cloud's streaming endpoint.
    ///
    /// URL FORMAT:
    /// wss://{host}/ws/streaming-deepgram?license_key=...&language=...&vocabulary=...
    ///
    /// QUERY PARAMETERS:
    /// - license_key OR device_id (required): Authentication
    /// - language (optional): Language code. Omitted for auto-detect
    /// - vocabulary (optional): Comma-separated terms. Only sent with explicit language
    ///   because the backend uses Deepgram's `keyterm` parameter which requires monolingual mode
    ///
    /// - Parameter config: Session configuration with auth, language, and vocabulary
    /// - Returns: WebSocket URL, or nil if no auth credentials provided
    func buildWebSocketURL(config: StreamingSessionConfig) -> URL? {
        // Convert HTTPS to WSS for WebSocket connection
        let baseURL = NetworkConfig.hyperwhisperCloudURL
            .replacingOccurrences(of: "https://", with: "wss://")
            .replacingOccurrences(of: "http://", with: "ws://")

        var components = URLComponents(string: "\(baseURL)/ws/streaming-deepgram")

        var queryItems: [URLQueryItem] = []

        // AUTHENTICATION (required)
        // Licensed users authenticate with license_key, trial users with device_id.
        // At least one must be present for the backend to authorize the request.
        // Return nil early if neither is provided — the server would reject with 401 anyway,
        // and surfacing the error here gives a clearer failure path.
        if let key = config.licenseKey, !key.isEmpty {
            queryItems.append(URLQueryItem(name: "license_key", value: key))
        } else if let id = config.deviceId, !id.isEmpty {
            queryItems.append(URLQueryItem(name: "device_id", value: id))
        } else {
            logger.error("Cannot build HW Cloud URL: no license key or device ID provided")
            return nil
        }

        // LANGUAGE (optional)
        // When omitted, the backend uses Deepgram's auto-detect mode.
        // When set, enables vocabulary boosting via `keyterm` parameter.
        if let lang = config.language, !lang.isEmpty, lang != "auto" {
            queryItems.append(URLQueryItem(name: "language", value: lang))
        }

        // VOCABULARY (optional, only with explicit language)
        // Backend converts comma-separated terms to Deepgram `keyterm` params with boost.
        // Not sent with auto-detect because Nova-3 `keyterm` is silently ignored in
        // multilingual mode (see CLAUDE.md "Custom Vocabulary Boosting" section).
        if let vocab = config.vocabulary, !vocab.isEmpty,
           config.language != nil && config.language != "auto" {
            queryItems.append(URLQueryItem(name: "vocabulary", value: vocab))
        }

        components?.queryItems = queryItems.isEmpty ? nil : queryItems
        return components?.url
    }

    /// Encode a PCM audio chunk as raw binary data.
    ///
    /// HyperWhisper Cloud expects raw 16kHz mono Int16 PCM as binary WebSocket frames.
    /// No additional encoding or wrapping is needed (unlike ElevenLabs which requires
    /// base64 JSON).
    ///
    /// - Parameter pcmData: 16kHz mono Int16 PCM audio data
    /// - Returns: Binary WebSocket message
    func encodeAudioChunk(_ pcmData: Data) -> URLSessionWebSocketTask.Message {
        .data(pcmData)
    }

    /// Parse a JSON message from HyperWhisper Cloud into a normalized event.
    ///
    /// MESSAGE TYPE MAPPING:
    /// | Server Type        | Normalized Event         | Key Fields                    |
    /// |-------------------|-------------------------|-------------------------------|
    /// | "ready"           | .sessionStarted          | sessionId                     |
    /// | "transcript"      | .finalTranscript         | text (when is_final=true)     |
    /// | "transcript"      | .partialTranscript       | text (when is_final=false)    |
    /// | "session_complete"| .sessionComplete         | duration_seconds, credits_used|
    /// | "error"           | .error                   | message                       |
    ///
    /// - Parameter text: Raw JSON string from the WebSocket
    /// - Returns: Normalized event, or nil if message type is unrecognized or unparseable
    func parseMessage(_ text: String) -> StreamingProviderEvent? {
        guard let data = text.data(using: .utf8) else { return nil }

        do {
            let message = try JSONDecoder().decode(ServerMessage.self, from: data)

            switch message.type {
            case "ready":
                // Server has connected to Deepgram and is ready to receive audio
                return .sessionStarted(sessionId: message.sessionId)

            case "transcript":
                // Transcript update from Deepgram via the HW Cloud proxy.
                // Drop empty transcripts: Deepgram emits empty `is_final=true`
                // results at long-silence segments and `from_finalize` boundaries.
                // An empty final would wipe the live preview and contribute nothing,
                // so we filter it here (mirrors the Deepgram/ElevenLabs/xAI strategies).
                guard let transcriptText = message.text, !transcriptText.isEmpty else { return nil }
                let isFinal = message.is_final ?? false

                if isFinal {
                    return .finalTranscript(text: transcriptText)
                } else {
                    return .partialTranscript(text: transcriptText)
                }

            case "session_complete":
                // Server has closed the Deepgram connection and calculated credit usage
                let duration = message.duration_seconds ?? 0
                let credits = message.credits_used ?? 0
                return .sessionComplete(durationSeconds: duration, creditsUsed: credits)

            case "error":
                // Server-side error (auth failure, Deepgram error, credit exhaustion, etc.)
                let errorMessage = message.message ?? "Unknown server error"
                return .error(message: errorMessage)

            case "warning":
                // Server-side warning (e.g., session approaching max duration)
                let warningMessage = message.message ?? "Server warning"
                return .warning(message: warningMessage)

            default:
                logger.debug("Unknown HW Cloud message type: \(message.type, privacy: .public)")
                return nil
            }
        } catch {
            logger.warning("Failed to decode HW Cloud message: \(error.localizedDescription, privacy: .public)")
            return nil
        }
    }

    /// Define the shutdown sequence for HyperWhisper Cloud.
    ///
    /// SEQUENCE:
    /// 1. Send {"type":"stop"} — tells the server to close the Deepgram connection
    /// 2. Wait 0.5s — gives the server time to send session_complete with credit info
    /// 3. Close WebSocket — clean connection teardown
    ///
    /// WHY THE WAIT:
    /// The server needs time to receive the stop signal, close the Deepgram stream,
    /// calculate credit usage, and send back the session_complete message. Without
    /// the delay, we'd close the WebSocket before receiving the credit deduction info.
    func stopSequence() -> [StreamingStopStep] {
        [
            .sendText(#"{"type":"stop"}"#),
            .wait(0.5),
            .closeWebSocket
        ]
    }

    /// Human-readable label for history entries.
    ///
    /// Used when saving transcription history to identify the provider.
    /// Format matches the pattern used by batch providers (e.g., "HyperWhisper Cloud").
    var transcriptionProviderLabel: String {
        "HyperWhisper Cloud (Streaming)"
    }

}
