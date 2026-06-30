//
//  DeepgramStreamingStrategy.swift
//  hyperwhisper
//
//  DEEPGRAM DIRECT STREAMING STRATEGY
//  Implements the StreamingProviderStrategy protocol for direct WebSocket
//  connections to Deepgram's live transcription API (Nova-3 models).
//
//  ARCHITECTURE:
//  This strategy bypasses HyperWhisper Cloud and connects directly to
//  Deepgram's `wss://api.deepgram.com/v1/listen` endpoint. Users provide
//  their own Deepgram API key, removing the need for HyperWhisper credits.
//
//  WHY DIRECT STREAMING:
//  Some users prefer direct API access for:
//  - Lower latency (no intermediate server hop)
//  - Cost control (use their own Deepgram billing)
//  - Data privacy (audio goes directly to Deepgram, not through HW Cloud)
//
//  AUDIO FORMAT:
//  Raw binary PCM (same as HyperWhisper Cloud) — 16kHz mono Int16.
//  Deepgram accepts this natively via `encoding=linear16&sample_rate=16000`.
//
//  KEEPALIVE MECHANISM:
//  Deepgram closes WebSocket connections after ~10 seconds of silence.
//  This strategy tracks the last audio send time and injects a KeepAlive
//  heartbeat JSON message when >3 seconds have elapsed without audio.
//  The heartbeat is sent via `onAudioSendOpportunity()`, which the client
//  calls each time an audio chunk is about to be sent. If the user is
//  silent, the audio capture still fires (with near-zero-amplitude data),
//  so the opportunity callback still triggers regularly.
//
//  VOCABULARY BOOSTING:
//  Deepgram Nova-3 supports the `keyterm` query parameter for vocabulary
//  boosting, but ONLY when language is explicitly specified (monolingual mode).
//  When language is "auto" (auto-detect), keyterm is silently ignored by
//  Deepgram, so we omit it entirely in that case to keep URLs clean.
//  Each vocabulary term is added as a separate `&keyterm={term}` param.
//
//  SHUTDOWN SEQUENCE:
//  1. Send `{"type":"Finalize"}` to flush any pending transcription
//  2. Wait 0.5 seconds for the server to process the finalize
//  3. Send `{"type":"CloseStream"}` to signal clean shutdown
//  4. Close the WebSocket connection
//
//  RESPONSE PARSING:
//  Deepgram sends several message types over the WebSocket:
//  - "Results" — Contains transcription data with is_final/speech_final flags
//  - "Metadata" — Session metadata (request_id, model info, etc.)
//  - "UtteranceEnd" — Marks end of an utterance (used with endpointing)
//  - "SpeechStarted" — Indicates speech detected in audio stream
//  Only "Results" messages produce transcript events; others are logged as metadata.
//

import Foundation
import OSLog

// MARK: - DeepgramStreamingStrategy

/// Strategy for direct WebSocket streaming to Deepgram's live transcription API.
///
/// Connects to `wss://api.deepgram.com/v1/listen` and authenticates via
/// WebSocket subprotocols (`["token", apiKey]`). Sends raw binary PCM audio
/// and receives JSON transcription results with interim and final segments.
final class DeepgramStreamingStrategy: StreamingProviderStrategy {

    // MARK: - Private Properties

    /// Timestamp of the last audio chunk sent over the WebSocket.
    /// Used to determine when a KeepAlive heartbeat is needed.
    /// Reset each time audio is sent or a KeepAlive is triggered.
    ///
    /// THREAD SAFETY:
    /// This property is accessed from the audio capture callback (non-main thread)
    /// via onAudioSendOpportunity(). We use os_unfair_lock because it's the lightest
    /// synchronization primitive (~1-5ns vs ~100-200ns for NSLock) and this runs
    /// on every audio buffer (~10 times/sec).
    private var _lastAudioSentTime: Date = .now
    private var lastAudioSentTimeLock = os_unfair_lock()

    private var lastAudioSentTime: Date {
        get {
            os_unfair_lock_lock(&lastAudioSentTimeLock)
            defer { os_unfair_lock_unlock(&lastAudioSentTimeLock) }
            return _lastAudioSentTime
        }
        set {
            os_unfair_lock_lock(&lastAudioSentTimeLock)
            _lastAudioSentTime = newValue
            os_unfair_lock_unlock(&lastAudioSentTimeLock)
        }
    }

    /// Threshold in seconds after which a KeepAlive heartbeat is sent.
    /// Deepgram closes connections after ~10s of silence; 3s gives ample margin.
    private let keepAliveThreshold: TimeInterval = 3.0

    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "DeepgramStreaming")

    // MARK: - StreamingProviderStrategy Conformance

    /// Build the Deepgram WebSocket URL with all required query parameters.
    ///
    /// QUERY PARAMETERS:
    /// - `model` — Deepgram model ID (e.g., "nova-3-general", "nova-3-medical")
    /// - `encoding=linear16` — Raw PCM audio encoding
    /// - `sample_rate=16000` — 16kHz audio sample rate
    /// - `channels=1` — Mono audio
    /// - `smart_format=true` — Enables punctuation, casing, and number formatting
    /// - `no_delay` — When true, reduces formatting delay for faster typing output
    /// - `interim_results=true` — Enables partial/interim transcripts
    /// - `punctuate=true` — Adds punctuation to transcripts
    /// - `endpointing=300` — 300ms silence threshold for utterance boundaries
    /// - `language` — Explicit language code (omitted for auto-detect)
    /// - `keyterm` — Vocabulary boosting terms (only with explicit language)
    ///
    /// AUTHENTICATION:
    /// Deepgram direct streaming authenticates via WebSocket subprotocols rather
    /// than URL query params. See `webSocketSubprotocols(config:)`.
    ///
    /// - Parameter config: Session configuration with API key, language, model, etc.
    /// - Returns: The constructed WebSocket URL, or nil if API key is missing
    func buildWebSocketURL(config: StreamingSessionConfig) -> URL? {
        guard !(config.apiKey?.isEmpty ?? true) else {
            logger.error("Cannot build Deepgram URL: API key is missing")
            return nil
        }

        let model = CloudTranscriptionModels.resolveDeepgramModelAlias(config.model) ?? "nova-3-general"

        // BUILD URL COMPONENTS:
        // Start with the base endpoint and add all required query parameters.
        // Deepgram uses query params for all configuration (no request body on connect).
        var components = URLComponents()
        components.scheme = "wss"
        components.host = "api.deepgram.com"
        components.path = "/v1/listen"

        var queryItems: [URLQueryItem] = [
            URLQueryItem(name: "model", value: model),
            URLQueryItem(name: "encoding", value: "linear16"),
            URLQueryItem(name: "sample_rate", value: "16000"),
            URLQueryItem(name: "channels", value: "1"),
            URLQueryItem(name: "smart_format", value: "true"),
            URLQueryItem(name: "no_delay", value: config.fastFormatting ? "true" : "false"),
            URLQueryItem(name: "interim_results", value: "true"),
            URLQueryItem(name: "punctuate", value: "true"),
            URLQueryItem(name: "endpointing", value: "300"),
            URLQueryItem(name: "mip_opt_out", value: "true")
        ]

        // LANGUAGE PARAMETER:
        // Only add explicit language code. When nil (auto-detect), Deepgram
        // uses its built-in language detection. Adding language explicitly
        // enables monolingual mode which is required for keyterm vocabulary.
        let hasExplicitLanguage = config.language != nil && !config.language!.isEmpty
        if hasExplicitLanguage {
            queryItems.append(URLQueryItem(name: "language", value: config.language!))
        }

        // VOCABULARY BOOSTING (keyterm):
        // Nova-3 only supports keyterm in monolingual mode (explicit language).
        // Each term is added as a separate &keyterm={term} query parameter.
        // Terms are extracted from the comma-separated vocabulary string.
        if hasExplicitLanguage, let vocabulary = config.vocabulary, !vocabulary.isEmpty {
            let terms = vocabulary
                .split(separator: ",")
                .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
                .filter { !$0.isEmpty }

            for term in terms {
                queryItems.append(URLQueryItem(name: "keyterm", value: term))
            }
            logger.info("Deepgram vocabulary boosting enabled: \(terms.count, privacy: .public) keyterms added")
        }

        components.queryItems = queryItems
        return components.url
    }

    /// Returns nil — no custom URLRequest is needed for Deepgram.
    ///
    /// AUTHENTICATION:
    /// Auth is handled via WebSocket subprotocols (`webSocketSubprotocols`),
    /// not custom headers or URL query params.
    func buildWebSocketRequest(url: URL, config: StreamingSessionConfig) -> URLRequest? {
        return nil
    }

    /// Deepgram WebSocket authentication via subprotocol negotiation.
    ///
    /// Deepgram expects direct client auth as:
    /// `Sec-WebSocket-Protocol: token, <API_KEY>`
    /// which maps to `["token", apiKey]` in URLSession's API.
    func webSocketSubprotocols(config: StreamingSessionConfig) -> [String]? {
        guard let apiKey = config.apiKey, !apiKey.isEmpty else {
            return nil
        }
        return ["token", apiKey]
    }

    /// Encode a PCM audio chunk as raw binary data for Deepgram.
    ///
    /// Deepgram accepts raw binary PCM directly over the WebSocket when
    /// `encoding=linear16` is specified in the URL. No JSON wrapping or
    /// base64 encoding is needed — just send the bytes.
    ///
    /// - Parameter pcmData: 16kHz mono Int16 PCM audio data
    /// - Returns: Binary WebSocket message containing the raw PCM data
    func encodeAudioChunk(_ pcmData: Data) -> URLSessionWebSocketTask.Message {
        .data(pcmData)
    }

    /// Parse a Deepgram WebSocket message into a normalized StreamingProviderEvent.
    ///
    /// MESSAGE TYPES:
    /// - "Results" — Transcription data. Contains `is_final` flag:
    ///   - `is_final=true` → `.finalTranscript` (committed, won't change)
    ///   - `is_final=false` → `.partialTranscript` (interim, may change)
    ///   - Empty transcript text is filtered out (no event emitted)
    /// - "Metadata" — Session metadata (request_id, model info). Logged as debug.
    /// - "UtteranceEnd" — End of utterance marker from endpointing. Logged as debug.
    /// - "SpeechStarted" — Speech detection start. Logged as debug.
    ///
    /// JSON STRUCTURE (Results):
    /// ```json
    /// {
    ///   "type": "Results",
    ///   "channel": {
    ///     "alternatives": [
    ///       { "transcript": "hello world", "confidence": 0.98 }
    ///     ]
    ///   },
    ///   "is_final": true,
    ///   "speech_final": true,
    ///   "from_finalize": false
    /// }
    /// ```
    ///
    /// - Parameter text: Raw JSON string from the Deepgram WebSocket
    /// - Returns: Normalized event, or nil for unrecognized message types
    func parseMessage(_ text: String) -> StreamingProviderEvent? {
        guard let data = text.data(using: .utf8) else {
            logger.error("Deepgram parseMessage: failed to convert text to UTF-8 data")
            return nil
        }

        // DECODE JSON:
        // Use private Decodable struct for type-safe parsing.
        // JSONDecoder is preferred over JSONSerialization for compile-time safety.
        let result: DeepgramMessage
        do {
            result = try JSONDecoder().decode(DeepgramMessage.self, from: data)
        } catch {
            logger.warning("Deepgram parseMessage: failed to decode JSON: \(error.localizedDescription, privacy: .public)")
            return nil
        }

        // ROUTE BY MESSAGE TYPE:
        // Deepgram sends multiple message types. Only "Results" contains transcription text.
        // Other types are informational metadata logged for debugging.
        switch result.type {
        case "Results":
            // EXTRACT TRANSCRIPT TEXT:
            // Navigate channel → alternatives[0] → transcript.
            // If no alternatives or empty transcript, return nil (no event).
            guard let transcript = result.channel?.alternatives.first?.transcript,
                  !transcript.isEmpty else {
                return nil
            }

            // FINAL VS PARTIAL:
            // is_final=true means Deepgram has committed this segment and won't revise it.
            // is_final=false means it's an interim result that may change with more audio.
            if result.is_final == true {
                return .finalTranscript(text: transcript)
            } else {
                return .partialTranscript(text: transcript)
            }

        case "Metadata":
            return .sessionStarted(sessionId: result.request_id)

        case "UtteranceEnd", "SpeechStarted":
            return .metadata(raw: text)

        default:
            logger.debug("Deepgram unrecognized message type: \(result.type, privacy: .public)")
            return nil
        }
    }

    /// Define the Deepgram shutdown sequence.
    ///
    /// SHUTDOWN STEPS:
    /// 1. Finalize — Tells Deepgram to flush any buffered audio and emit final results
    /// 2. Wait 0.5s — Give the server time to process and return final transcripts
    /// 3. CloseStream — Signal the server to close its side of the connection
    /// 4. Close WebSocket — Client-side connection termination
    ///
    /// WHY FINALIZE BEFORE CLOSE:
    /// Without Finalize, any audio buffered on the server (up to the endpointing
    /// window of 300ms) would be lost. Finalize forces the server to process
    /// everything and return a final result with `from_finalize: true`.
    func stopSequence() -> [StreamingStopStep] {
        [
            .sendText(#"{"type":"Finalize"}"#),
            .wait(0.5),
            .sendText(#"{"type":"CloseStream"}"#),
            .closeWebSocket
        ]
    }

    /// Human-readable label for history entries.
    /// Includes "(Streaming)" suffix to distinguish from batch transcription.
    var transcriptionProviderLabel: String { "Deepgram (Streaming)" }

    /// Deepgram Nova-3 supports vocabulary boosting via keyterm parameters.
    /// Note: Only effective when language is explicitly specified (not auto-detect).
    var supportsVocabulary: Bool { true }

    /// Deepgram direct streaming is ready as soon as WebSocket handshake completes.
    /// Metadata may arrive later, so startup should not block on it.
    var sessionStartsOnWebSocketOpen: Bool { true }

    /// KeepAlive heartbeat logic for Deepgram.
    ///
    /// HOW IT WORKS:
    /// Called by the client each time an audio chunk is about to be sent.
    /// Checks if >3 seconds have elapsed since the last audio send:
    /// - If yes: sends a `{"type":"KeepAlive"}` message to prevent timeout
    /// - If no: no action needed (regular audio keeps the connection alive)
    /// Always updates lastAudioSentTime to track the current send.
    ///
    /// WHY IN AUDIO CALLBACK:
    /// Instead of a separate timer, we piggyback on the audio capture callback.
    /// Even during silence, the audio engine still fires its tap callback with
    /// near-zero-amplitude buffers, so this check runs regularly (~10 times/sec).
    /// This avoids the complexity of managing a separate KeepAlive timer.
    ///
    /// - Parameter webSocketSend: Closure to send a message on the active WebSocket
    func onAudioSendOpportunity(webSocketSend: @escaping (URLSessionWebSocketTask.Message) -> Void) {
        let now = Date()
        if now.timeIntervalSince(lastAudioSentTime) > keepAliveThreshold {
            webSocketSend(.string(#"{"type":"KeepAlive"}"#))
            logger.debug("Deepgram KeepAlive sent (silence threshold exceeded)")
        }
        lastAudioSentTime = now
    }
}

// MARK: - Private Decodable Types

/// Top-level Deepgram WebSocket message structure.
///
/// Deepgram sends JSON messages with a `type` field that determines the payload.
/// Only "Results" type contains transcription data; other types are metadata.
///
/// WHY PRIVATE:
/// These types are internal to the Deepgram strategy and should not leak
/// into the rest of the app. The strategy's parseMessage() converts these
/// into normalized StreamingProviderEvent values.
private struct DeepgramMessage: Decodable {
    /// Message type identifier: "Results", "Metadata", "UtteranceEnd", "SpeechStarted"
    let type: String

    /// Transcription channel data (only present in "Results" messages)
    let channel: Channel?

    /// Whether this is a final (committed) result. true = won't change, false = interim.
    let is_final: Bool?

    /// Request identifier assigned by Deepgram (present in "Metadata" messages)
    let request_id: String?

    /// Channel containing transcription alternatives ranked by confidence.
    struct Channel: Decodable {
        let alternatives: [Alternative]
    }

    /// Single transcription alternative with text.
    struct Alternative: Decodable {
        /// The transcribed text for this alternative
        let transcript: String
    }
}
