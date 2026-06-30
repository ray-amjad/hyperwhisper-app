//
//  ElevenLabsStreamingStrategy.swift
//  hyperwhisper
//
//  ELEVENLABS DIRECT STREAMING STRATEGY
//  Implements the StreamingProviderStrategy protocol for direct WebSocket
//  connections to ElevenLabs' realtime speech-to-text API (Scribe v2 Realtime).
//
//  ARCHITECTURE:
//  This strategy connects directly to ElevenLabs' `wss://api.elevenlabs.io/v1/
//  speech-to-text/realtime` endpoint using the user's own API key.
//  Unlike HyperWhisper Cloud and Deepgram which accept raw binary PCM,
//  ElevenLabs requires audio to be base64-encoded inside JSON messages.
//
//  WHY DIRECT STREAMING:
//  Some users prefer direct API access for:
//  - Lower latency (no intermediate server hop)
//  - Cost control (use their own ElevenLabs billing)
//  - Data privacy (audio goes directly to ElevenLabs, not through HW Cloud)
//
//  AUDIO FORMAT:
//  ElevenLabs requires JSON-wrapped base64-encoded PCM audio. Each chunk
//  is sent as:
//  ```json
//  {
//    "message_type": "input_audio_chunk",
//    "audio_base_64": "<base64-encoded-pcm>",
//    "commit": false,
//    "sample_rate": 16000
//  }
//  ```
//  This is different from Deepgram/HW Cloud which accept raw binary.
//
//  VOCABULARY:
//  ElevenLabs' realtime API does NOT support custom vocabulary boosting.
//  The batch API (Scribe v2) supports keyterms, but the realtime WebSocket
//  API has no vocabulary parameter. `supportsVocabulary` returns false.
//
//  SHUTDOWN:
//  ElevenLabs requires no special shutdown sequence — just close the WebSocket.
//  The server handles cleanup automatically on connection close.
//
//  LANGUAGE NORMALIZATION:
//  ElevenLabs uses ISO 639-1 language codes (e.g., "en", "ja", "es").
//  If the app passes a locale with region (e.g., "en-US", "zh-Hans"),
//  this strategy extracts just the primary language subtag.
//  This matches the normalization logic in ElevenLabsProvider.swift.
//
//  RESPONSE PARSING:
//  ElevenLabs sends several message types over the WebSocket:
//  - "session_started" — Session is ready to receive audio
//  - "partial_transcript" — Interim transcript that may change
//  - "committed_transcript" — Final committed transcript segment
//  - "auth_error" — Authentication failure (invalid or expired API key)
//  - "quota_exceeded" — Account has run out of credits
//  - "rate_limited" — Too many concurrent requests
//

import Foundation
import OSLog

// MARK: - ElevenLabsStreamingStrategy

/// Strategy for direct WebSocket streaming to ElevenLabs' realtime speech-to-text API.
///
/// Connects to `wss://api.elevenlabs.io/v1/speech-to-text/realtime` with
/// authentication via `xi-api-key` header. Sends base64-encoded JSON audio
/// chunks and receives JSON transcription events.
final class ElevenLabsStreamingStrategy: StreamingProviderStrategy {

    // MARK: - Private Properties

    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "ElevenLabsStreaming")

    // MARK: - StreamingProviderStrategy Conformance

    /// Build the ElevenLabs WebSocket URL with all required query parameters.
    ///
    /// QUERY PARAMETERS:
    /// - `model_id=scribe_v2_realtime` — The only available realtime STT model
    /// - `audio_format=pcm_16000` — PCM audio at 16kHz sample rate
    /// - `commit_strategy=vad` — Use Voice Activity Detection to commit transcripts
    /// - `vad_silence_threshold_secs=1.5` — Seconds of silence before committing
    /// - `vad_threshold=0.4` — VAD sensitivity (0.0-1.0, lower = more sensitive)
    /// - `language_code` — Explicit language (omitted for auto-detect)
    ///
    /// WHY VAD COMMIT STRATEGY:
    /// ElevenLabs offers two commit strategies: "manual" and "vad". VAD (Voice
    /// Activity Detection) automatically commits transcript segments when it
    /// detects pauses in speech, which matches the natural dictation flow
    /// expected by HyperWhisper users. Manual would require explicit commit
    /// messages from the client.
    ///
    /// - Parameter config: Session configuration with API key, language, etc.
    /// - Returns: The constructed WebSocket URL, or nil if API key is missing
    func buildWebSocketURL(config: StreamingSessionConfig) -> URL? {
        guard let apiKey = config.apiKey, !apiKey.isEmpty else {
            logger.error("Cannot build ElevenLabs URL: API key is missing")
            return nil
        }

        // BUILD URL COMPONENTS:
        // ElevenLabs uses query params for model selection, audio format,
        // and VAD configuration. Authentication is via header (not query param).
        var components = URLComponents()
        components.scheme = "wss"
        components.host = "api.elevenlabs.io"
        components.path = "/v1/speech-to-text/realtime"

        var queryItems: [URLQueryItem] = [
            URLQueryItem(name: "model_id", value: "scribe_v2_realtime"),
            URLQueryItem(name: "audio_format", value: "pcm_16000"),
            URLQueryItem(name: "commit_strategy", value: "vad"),
            URLQueryItem(name: "vad_silence_threshold_secs", value: "1.5"),
            URLQueryItem(name: "vad_threshold", value: "0.4")
        ]

        // LANGUAGE PARAMETER:
        // Only add when explicitly specified (not auto-detect).
        // ElevenLabs uses `language_code` with ISO 639-1 codes (e.g., "en", "ja").
        // When omitted, ElevenLabs performs automatic language detection.
        if let language = config.language, !language.isEmpty {
            let normalized = normalizeLanguageCode(language)
            queryItems.append(URLQueryItem(name: "language_code", value: normalized))
            logger.info("ElevenLabs language set: \(normalized, privacy: .public)")
        }

        components.queryItems = queryItems
        return components.url
    }

    /// Build a URLRequest with ElevenLabs' API key header.
    ///
    /// AUTHENTICATION:
    /// ElevenLabs uses `xi-api-key` header for authentication.
    /// This is their standard auth header across all API endpoints.
    ///
    /// - Parameters:
    ///   - url: The WebSocket URL from buildWebSocketURL
    ///   - config: Session configuration containing the API key
    /// - Returns: URLRequest with the xi-api-key header set
    func buildWebSocketRequest(url: URL, config: StreamingSessionConfig) -> URLRequest? {
        guard let apiKey = config.apiKey, !apiKey.isEmpty else {
            logger.error("Cannot build ElevenLabs request: API key is missing")
            return nil
        }
        var request = URLRequest(url: url)
        request.setValue(apiKey, forHTTPHeaderField: "xi-api-key")
        return request
    }

    /// Encode a PCM audio chunk as a base64 JSON message for ElevenLabs.
    ///
    /// ENCODING FORMAT:
    /// ElevenLabs requires audio wrapped in a JSON message with:
    /// - `message_type: "input_audio_chunk"` — Identifies this as audio data
    /// - `audio_base_64` — Base64-encoded raw PCM bytes
    /// - `commit: false` — Don't force-commit on this chunk (let VAD decide)
    /// - `sample_rate: 16000` — Audio sample rate for decoding
    ///
    /// WHY JSON+BASE64 (not raw binary):
    /// ElevenLabs' WebSocket protocol is entirely JSON-based. They don't
    /// accept raw binary frames. This adds ~33% overhead from base64 encoding
    /// but keeps the protocol uniform and self-describing.
    ///
    /// - Parameter pcmData: 16kHz mono Int16 PCM audio data
    /// - Returns: Text WebSocket message containing the JSON-encoded audio chunk
    func encodeAudioChunk(_ pcmData: Data) -> URLSessionWebSocketTask.Message {
        let base64 = pcmData.base64EncodedString()
        let json = #"{"message_type":"input_audio_chunk","audio_base_64":"\#(base64)","commit":false,"sample_rate":16000}"#
        return .string(json)
    }

    /// Parse an ElevenLabs WebSocket message into a normalized StreamingProviderEvent.
    ///
    /// MESSAGE TYPES:
    /// - "session_started" → `.sessionStarted` — Session is ready for audio
    /// - "partial_transcript" → `.partialTranscript` — Interim text (may change)
    /// - "committed_transcript" → `.finalTranscript` — Committed text (won't change)
    /// - "auth_error" → `.error` — Invalid or expired API key
    /// - "quota_exceeded" → `.error` — Account credits exhausted
    /// - "rate_limited" → `.error` — Too many concurrent connections
    ///
    /// ERROR MESSAGES:
    /// Error types are mapped to user-friendly messages that explain the issue
    /// and suggest corrective action (check API key, check billing, try later).
    ///
    /// - Parameter text: Raw JSON string from the ElevenLabs WebSocket
    /// - Returns: Normalized event, or nil for unrecognized message types
    func parseMessage(_ text: String) -> StreamingProviderEvent? {
        guard let data = text.data(using: .utf8) else {
            logger.error("ElevenLabs parseMessage: failed to convert text to UTF-8 data")
            return nil
        }

        // DECODE JSON:
        // Use private Decodable struct for type-safe parsing.
        let message: ElevenLabsMessage
        do {
            message = try JSONDecoder().decode(ElevenLabsMessage.self, from: data)
        } catch {
            logger.warning("ElevenLabs parseMessage: failed to decode JSON: \(error.localizedDescription, privacy: .public)")
            return nil
        }

        // ROUTE BY MESSAGE TYPE:
        // Each message type maps to a specific StreamingProviderEvent.
        // Error types get user-friendly descriptions.
        switch message.message_type {
        case "session_started":
            logger.info("ElevenLabs session started")
            return .sessionStarted(sessionId: nil)

        case "partial_transcript":
            // PARTIAL TRANSCRIPT:
            // Interim text that may change as more audio is processed.
            // Filter out empty transcripts to avoid unnecessary UI updates.
            guard let transcript = message.text, !transcript.isEmpty else {
                return nil
            }
            return .partialTranscript(text: transcript)

        case "committed_transcript":
            // COMMITTED TRANSCRIPT:
            // Final text segment that won't change. Triggered by VAD detecting
            // a pause in speech (per commit_strategy=vad configuration).
            guard let transcript = message.text, !transcript.isEmpty else {
                return nil
            }
            return .finalTranscript(text: transcript)

        case "auth_error":
            // AUTHENTICATION ERROR:
            // API key is invalid, expired, or missing. User needs to check their key.
            logger.error("ElevenLabs auth error received")
            return .error(message: "ElevenLabs authentication failed. Please check your API key in Settings.")

        case "quota_exceeded":
            // QUOTA EXCEEDED:
            // User's ElevenLabs account has run out of credits/characters.
            logger.error("ElevenLabs quota exceeded")
            return .error(message: "ElevenLabs quota exceeded. Please check your account billing.")

        case "rate_limited":
            // RATE LIMITED:
            // Too many concurrent WebSocket connections from this API key.
            logger.error("ElevenLabs rate limited")
            return .error(message: "ElevenLabs rate limit reached. Please try again in a moment.")

        default:
            logger.debug("ElevenLabs unrecognized message type: \(message.message_type, privacy: .public)")
            return nil
        }
    }

    /// Define the ElevenLabs shutdown sequence.
    ///
    /// SIMPLEST SHUTDOWN:
    /// ElevenLabs requires no special shutdown messages. Just close the
    /// WebSocket and the server handles cleanup automatically. This is
    /// the simplest shutdown of the three providers (HW Cloud needs stop+wait,
    /// Deepgram needs Finalize+wait+CloseStream).
    func stopSequence() -> [StreamingStopStep] {
        [.closeWebSocket]
    }

    /// Human-readable label for history entries.
    /// Includes "(Streaming)" suffix to distinguish from batch transcription.
    var transcriptionProviderLabel: String { "ElevenLabs (Streaming)" }

    /// ElevenLabs realtime API does NOT support custom vocabulary boosting.
    /// The batch API (Scribe v2) supports keyterms via multipart form fields,
    /// but the WebSocket realtime API has no vocabulary parameter.
    var supportsVocabulary: Bool { false }
}

// MARK: - Private Helpers

private extension ElevenLabsStreamingStrategy {

    /// Normalize a language code to ISO 639-1 format for ElevenLabs.
    ///
    /// NORMALIZATION RULES:
    /// - "en-US" → "en" (strip region subtag)
    /// - "zh-Hans" → "zh" (strip script subtag)
    /// - "ja" → "ja" (already correct)
    /// - "pt-BR" → "pt" (strip region)
    ///
    /// WHY NORMALIZE:
    /// The app may pass locale codes with region/script subtags (e.g., "en-US",
    /// "zh-Hans") but ElevenLabs expects simple ISO 639-1 codes. This matches
    /// the normalization logic in ElevenLabsProvider.swift (batch provider).
    ///
    /// - Parameter code: Language code that may include region/script subtags
    /// - Returns: Primary language subtag (ISO 639-1)
    func normalizeLanguageCode(_ code: String) -> String {
        let trimmed = code.trimmingCharacters(in: .whitespacesAndNewlines)
        if let primary = trimmed.split(separator: "-").first {
            return String(primary)
        }
        return trimmed
    }
}

// MARK: - Private Decodable Types

/// ElevenLabs WebSocket message structure.
///
/// All ElevenLabs realtime messages share a common shape with `message_type`
/// as the discriminator field. The `text` field is present in transcript messages
/// and absent in session/error messages.
///
/// WHY PRIVATE:
/// These types are internal to the ElevenLabs strategy. The strategy's
/// parseMessage() converts these into normalized StreamingProviderEvent values
/// that the rest of the app understands.
private struct ElevenLabsMessage: Decodable {
    /// Message type discriminator.
    /// Known values: "session_started", "partial_transcript", "committed_transcript",
    /// "auth_error", "quota_exceeded", "rate_limited"
    let message_type: String

    /// Transcript text (present in "partial_transcript" and "committed_transcript").
    /// nil for session and error messages.
    let text: String?
}
