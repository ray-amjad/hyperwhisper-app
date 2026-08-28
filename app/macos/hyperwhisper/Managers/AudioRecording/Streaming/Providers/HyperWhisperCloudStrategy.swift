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

    /// The `sttProvider` this session's route is derived from. Resolved once at
    /// init from the tier the user picked, so the strategy never reads settings.
    private let sttProvider: String

    /// The tier whose route reproduces the path this class hard-coded before the
    /// live tier picker existed. Anything unrecognised lands back here.
    static let defaultCloudTier = "deepgramNova3"

    /// - Parameter cloudTier: a `cloud-stt-catalog.json` entry id — the global
    ///   `streamingCloudTier` setting. A path selector only: this is deliberately
    ///   NOT a new `StreamingTranscriptionProvider` case, because the credit and
    ///   entitlement wiring in `RecordingTranscriptionFlow+Streaming` keys off
    ///   `provider == "hyperwhisperCloud"` and must keep matching.
    init(cloudTier: String = HyperWhisperCloudStrategy.defaultCloudTier) {
        self.sttProvider = Self.resolveSttProvider(cloudTier)
    }

    /// The route is DERIVED, never a table: `/ws/streaming-{sttProvider}`, where
    /// `sttProvider` comes from the catalog entry the tier names.
    /// `deepgramNova3` gives `/ws/streaming-deepgram`, byte-identical to the
    /// literal this replaced, so every installed client keeps working;
    /// `geminiTranscribe` gives `/ws/streaming-gemini-transcribe`.
    ///
    /// A tier outside the live-eligible set falls back to Deepgram rather than
    /// deriving a path the backend will 404 — the catalog has no `enabled` gate,
    /// so this is the client half of that guard.
    static func resolveSttProvider(_ cloudTier: String?) -> String {
        CloudSTTCatalog.shared.sttProvider(forEntryId: normalizedCloudTier(cloudTier)) ?? "deepgram"
    }

    /// The stored tier id clamped to the live-eligible set, in the catalog's own
    /// casing. Shared by the route derivation above and by the settings picker,
    /// which binds through it: `Picker` renders BLANK when the selection matches
    /// no tag, so a stale or imported value — the Local API and a backup restore
    /// both write this setting, and neither is the picker — would otherwise show
    /// an empty row while the session quietly ran on Deepgram.
    static func normalizedCloudTier(_ cloudTier: String?) -> String {
        let trimmed = cloudTier?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let eligible = CloudSTTCatalog.shared.streamingCloudTierEntries.map(\.id)
        return eligible.first { $0.caseInsensitiveCompare(trimmed) == .orderedSame }
            ?? Self.defaultCloudTier
    }

    /// Whether the tier's live vendor needs an explicit language before it
    /// honours vocabulary terms.
    ///
    /// True for Deepgram Nova-3, which silently ignores `keyterm` in
    /// multilingual mode — that is the ONLY reason this class ever withheld
    /// vocabulary in auto-detect. Gemini accepts `custom_vocabulary` in
    /// auto-detect (verified live), and vocabulary is the whole reason to pick
    /// that tier, so applying Deepgram's rule there would silently delete the
    /// headline feature for every auto-detect user.
    static func tierRequiresLanguageForVocabulary(_ cloudTier: String?) -> Bool {
        resolveSttProvider(cloudTier) == "deepgram"
    }

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

        var components = URLComponents(string: "\(baseURL)/ws/streaming-\(sttProvider)")

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

        // VOCABULARY (optional; gated on an explicit language for Deepgram ONLY)
        // The backend converts comma-separated terms to the upstream vendor's
        // vocabulary parameter. Withholding them in auto-detect is a DEEPGRAM
        // constraint — Nova-3 silently ignores `keyterm` in multilingual mode
        // (see CLAUDE.md "Custom Vocabulary Boosting") — not a HyperWhisper Cloud
        // one. Gemini accepts `custom_vocabulary` with no language at all, and
        // vocabulary is the whole reason to pick that tier, so gating it there
        // would silently remove the headline feature for auto-detect users.
        let hasExplicitLanguage = config.language != nil && config.language != "auto"
        if let vocab = config.vocabulary, !vocab.isEmpty,
           hasExplicitLanguage || sttProvider != "deepgram" {
            queryItems.append(URLQueryItem(name: "vocabulary", value: vocab))
        }

        components?.queryItems = queryItems.isEmpty ? nil : queryItems
        return components?.url
    }

    /// Carry the platform + app version headers into the WebSocket handshake.
    ///
    /// Auth stays in the query string (see above); this request exists only so
    /// the handshake carries the same client identity as the POST /transcribe
    /// path. Note the backend does not record it yet: `/ws/streaming-deepgram`
    /// emits no structured log lines, so nothing calls `readClientInfo` there.
    ///
    /// - Parameters:
    ///   - url: The WebSocket URL from buildWebSocketURL
    ///   - config: Session configuration (unused — no per-session headers)
    /// - Returns: The upgrade request carrying the client headers
    func buildWebSocketRequest(url: URL, config: StreamingSessionConfig) -> URLRequest? {
        var request = URLRequest(url: url)
        HyperWhisperClientInfo.apply(to: &request)
        return request
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
    /// 1. Send {"type":"stop"} — tells the server to end the upstream session
    /// 2. Wait for `session_complete` (10 s cap) — the last final, then the bill
    /// 3. Close WebSocket — clean connection teardown
    ///
    /// WHY IT WAITS FOR THE EVENT AND NOT A FIXED 0.5 s:
    /// The flat half-second predates the vendors behind this route needing a
    /// flush. It is enough for Deepgram, which finalizes on the marker, and it
    /// is NOT enough for Gemini: the backend forwards `audio_stream_end` and
    /// then holds the socket open for its whole `STOP_GRACE_MS` (5 s in
    /// `ws-streaming-gemini-transcribe.ts`) because Google delivers the last
    /// turn's final ~0.5 s AFTER the marker — right on top of this deadline.
    /// Closing at 0.5 s drops the last utterance of every live session and the
    /// credit figure with it, and the client would have no `session_complete`
    /// to reconcile against.
    ///
    /// `waitForSessionComplete` returns the instant the event lands, so Deepgram
    /// is not slowed down: the 10 s is a cap, not a delay, and it matches the
    /// budget `HyperWhisperCloudStreamingStrategy` on Windows and the
    /// shared-.NET live service already use.
    func stopSequence() -> [StreamingStopStep] {
        [
            .sendText(#"{"type":"stop"}"#),
            .waitForSessionComplete(timeout: 10.0),
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
