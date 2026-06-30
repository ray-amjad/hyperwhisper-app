//
//  StreamingProviderStrategy.swift
//  hyperwhisper
//
//  STREAMING PROVIDER STRATEGY PROTOCOL
//  Defines the abstraction layer for pluggable streaming transcription providers.
//
//  ARCHITECTURE:
//  Each streaming provider (HyperWhisper Cloud, Deepgram, ElevenLabs, xAI) implements
//  this protocol to encapsulate its WebSocket protocol differences:
//  - URL construction (different endpoints, query params)
//  - Authentication (query params vs headers)
//  - Audio encoding (raw binary vs base64 JSON)
//  - Message parsing (provider-specific JSON → normalized events)
//  - Shutdown sequence (stop messages, delays, close)
//
//  The shared StreamingTranscriptionClient handles everything else:
//  - WebSocket connection lifecycle
//  - Audio capture via StreamingAudioCapture
//  - Connection state machine
//  - Callback wiring (onTranscriptUpdate, onError, etc.)
//  - Auto-reconnect logic
//
//  WHY STRATEGY PATTERN:
//  All three providers share the same flow (connect → stream audio → receive text → disconnect)
//  but differ in protocol details. The strategy pattern avoids duplicating the connection
//  state machine, audio pipeline, and callback wiring across separate client classes.
//
//  ADDING A NEW PROVIDER:
//  1. Create a new class conforming to StreamingProviderStrategy
//  2. Add a case to StreamingTranscriptionProvider enum
//  3. Add routing in RecordingTranscriptionFlow+Streaming.swift
//

import Foundation

// MARK: - Streaming Provider Event

/// Normalized events from any streaming provider.
///
/// The shared StreamingTranscriptionClient switches on these
/// instead of provider-specific JSON shapes. Each strategy's
/// `parseMessage(_:)` maps its native format to these events.
///
/// WHY NORMALIZED:
/// Each provider returns different JSON structures (e.g., Deepgram uses "Results"
/// with channel.alternatives, ElevenLabs uses "committed_transcript" with text field,
/// HW Cloud uses "transcript" with is_final). Normalizing lets the client handle
/// all providers identically.
enum StreamingProviderEvent {
    /// Provider session is ready to receive audio.
    /// - Parameter sessionId: Provider-assigned session ID (nil if not provided)
    case sessionStarted(sessionId: String?)

    /// Interim (non-final) transcript that may change as more audio arrives.
    /// - Parameter text: The partial transcript text
    case partialTranscript(text: String)

    /// Committed transcript segment that won't change.
    /// - Parameter text: The final transcript text to type
    case finalTranscript(text: String)

    /// Final transcript segment bundled with provider session completion.
    /// Used by providers whose final flush and completion arrive in one message.
    /// - Parameters:
    ///   - text: The final transcript text to type
    ///   - durationSeconds: Total audio duration processed
    ///   - creditsUsed: Credits deducted (0 for direct providers)
    case finalTranscriptAndSessionComplete(text: String, durationSeconds: Double, creditsUsed: Double)

    /// Session has ended normally with usage stats.
    /// - Parameters:
    ///   - durationSeconds: Total audio duration processed
    ///   - creditsUsed: Credits deducted (HW Cloud only, 0 for direct providers)
    case sessionComplete(durationSeconds: Double, creditsUsed: Double)

    /// Provider-level error occurred.
    /// - Parameter message: Human-readable error description
    case error(message: String)

    /// Server-side warning (e.g., session approaching max duration).
    /// - Parameter message: Human-readable warning description
    case warning(message: String)

    /// Debug-level metadata from the provider (logged but not surfaced to UI).
    /// - Parameter raw: Raw JSON string for debug logging
    case metadata(raw: String)
}

// MARK: - Streaming Stop Step

/// Steps for graceful shutdown of a streaming session.
/// Executed in order by the client's stopSession() method.
///
/// WHY ORDERED STEPS:
/// Different providers require different shutdown sequences:
/// - HW Cloud: send stop JSON → wait → close
/// - Deepgram: send Finalize → wait → send CloseStream → close
/// - ElevenLabs: just close (no stop message needed)
/// - xAI: send audio.done → wait → close
///
/// Using an array of steps lets each strategy define its own sequence
/// without the client needing provider-specific shutdown logic.
enum StreamingStopStep {
    /// Send a text message over the WebSocket (e.g., `{"type":"stop"}`)
    case sendText(String)

    /// Wait for a specified duration (e.g., 0.5s for server to process)
    case wait(TimeInterval)

    /// Wait until the provider emits a session-complete event, with timeout.
    case waitForSessionComplete(timeout: TimeInterval)

    /// Close the WebSocket connection with normal closure code
    case closeWebSocket
}

// MARK: - Streaming Session Config

/// Configuration for starting a streaming session.
///
/// Superset of fields needed by all providers. Each strategy uses only
/// the fields relevant to its provider:
/// - HW Cloud: licenseKey/deviceId, language, vocabulary
/// - Deepgram: apiKey, language, vocabulary, model, fastFormatting
/// - ElevenLabs: apiKey, language
/// - xAI: apiKey, language
///
/// WHY SINGLE STRUCT:
/// Avoids multiple init() signatures on the client. The strategy picks
/// what it needs; unused fields are ignored (not an error).
struct StreamingSessionConfig {
    /// License key for HyperWhisper Cloud authenticated users
    let licenseKey: String?

    /// Device ID for HyperWhisper Cloud trial users
    let deviceId: String?

    /// Language code (e.g., "en", "ja"). nil = auto-detect
    let language: String?

    /// Comma-separated vocabulary terms for boosting (HW Cloud + Deepgram only)
    let vocabulary: String?

    /// API key for direct providers (Deepgram/ElevenLabs/xAI)
    let apiKey: String?

    /// Deepgram model ID (e.g., "nova-3-general", "nova-3-medical")
    let model: String?

    /// Deepgram no_delay flag for faster smart formatting
    let fastFormatting: Bool
}

// MARK: - Streaming Provider Strategy Protocol

/// Protocol each streaming provider strategy implements.
///
/// Encapsulates WebSocket protocol differences while sharing
/// the audio pipeline, connection state machine, and callback wiring.
///
/// PROTOCOL METHODS:
/// - `buildWebSocketURL` — Construct the provider's WebSocket endpoint URL
/// - `buildWebSocketRequest` — Optional: create URLRequest with auth headers
/// - `webSocketSubprotocols` — Optional: provide WebSocket subprotocols for auth
/// - `encodeAudioChunk` — Encode PCM audio data for the provider's expected format
/// - `parseMessage` — Parse provider-specific JSON into normalized events
/// - `stopSequence` — Define the ordered shutdown steps
/// - `transcriptionProviderLabel` — Human-readable label for history entries
/// - `supportsVocabulary` — Whether this provider supports custom vocabulary
/// - `sessionStartsOnWebSocketOpen` — Whether WebSocket open implies provider session started
/// - `onAudioSendOpportunity` — Hook for provider-specific keepalive logic
protocol StreamingProviderStrategy {
    /// Build the WebSocket URL with provider-specific query parameters.
    ///
    /// - Parameter config: Session configuration with auth, language, vocab, etc.
    /// - Returns: The WebSocket URL, or nil if configuration is invalid
    func buildWebSocketURL(config: StreamingSessionConfig) -> URL?

    /// Optionally build a URLRequest with custom headers (e.g., Authorization).
    ///
    /// If nil is returned, the client creates a basic WebSocket task from the URL.
    /// If a URLRequest is returned, the client uses it (enabling auth headers).
    ///
    /// - Parameters:
    ///   - url: The WebSocket URL from buildWebSocketURL
    ///   - config: Session configuration
    /// - Returns: A URLRequest with headers, or nil to use URL directly
    func buildWebSocketRequest(url: URL, config: StreamingSessionConfig) -> URLRequest?

    /// Optionally provide WebSocket subprotocols for the handshake.
    ///
    /// Used by providers that authenticate via subprotocol negotiation
    /// (for example, Deepgram's `["token", apiKey]` format).
    ///
    /// - Parameter config: Session configuration
    /// - Returns: Subprotocol list, or nil to use no subprotocols
    func webSocketSubprotocols(config: StreamingSessionConfig) -> [String]?

    /// Encode a PCM audio chunk for sending over the WebSocket.
    ///
    /// FORMAT DIFFERENCES:
    /// - HW Cloud & Deepgram: Raw binary PCM → .data(pcmData)
    /// - ElevenLabs: Base64-encoded JSON → .string(json)
    ///
    /// - Parameter pcmData: 16kHz mono Int16 PCM audio data
    /// - Returns: WebSocket message ready to send
    func encodeAudioChunk(_ pcmData: Data) -> URLSessionWebSocketTask.Message

    /// Parse a text message received from the WebSocket.
    ///
    /// Maps provider-specific JSON to normalized StreamingProviderEvent.
    /// Returns nil for unrecognized message types (logged at debug level).
    ///
    /// - Parameter text: Raw JSON string from the WebSocket
    /// - Returns: Normalized event, or nil if message type is unrecognized
    func parseMessage(_ text: String) -> StreamingProviderEvent?

    /// Define the ordered shutdown steps for graceful session termination.
    ///
    /// The client executes these steps sequentially when stopSession() is called.
    ///
    /// - Returns: Array of stop steps to execute in order
    func stopSequence() -> [StreamingStopStep]

    /// Human-readable label for this provider, used in history entries.
    ///
    /// Examples: "HyperWhisper Cloud (Streaming)", "Deepgram (Streaming)", "ElevenLabs (Streaming)", "xAI (Streaming)"
    var transcriptionProviderLabel: String { get }

    /// Whether this provider supports custom vocabulary boosting.
    ///
    /// - HW Cloud: true (via Deepgram keyterm on backend)
    /// - Deepgram: true (via keyterm query params)
    /// - ElevenLabs: false (realtime API doesn't support vocabulary)
    /// - xAI: false (streaming API has no vocabulary parameter)
    var supportsVocabulary: Bool { get }

    /// Whether this provider should treat WebSocket open as session started.
    ///
    /// Some direct providers are ready to receive audio as soon as the socket opens
    /// and may not emit an explicit "session started" event immediately.
    var sessionStartsOnWebSocketOpen: Bool { get }

    /// Called each time an audio chunk is about to be sent.
    ///
    /// USE CASE: Deepgram requires a KeepAlive heartbeat if no audio
    /// has been sent for >3 seconds (e.g., during silence). This hook
    /// lets the strategy inject provider-specific messages alongside audio.
    ///
    /// - Parameter webSocketSend: Closure to send a message on the WebSocket
    func onAudioSendOpportunity(webSocketSend: @escaping (URLSessionWebSocketTask.Message) -> Void)

    /// Audio sample rate expected by this provider for PCM chunks.
    var audioSampleRate: Double { get }

    /// Messages to send immediately after the WebSocket opens.
    ///
    /// Used by providers such as OpenAI Realtime that require an explicit
    /// session configuration event before audio is appended.
    func startMessages(config: StreamingSessionConfig) -> [URLSessionWebSocketTask.Message]
}

// MARK: - Default Implementations

/// Default implementations for optional protocol methods.
///
/// Most providers don't need custom URLRequests (HW Cloud uses query params)
/// and don't need keepalive logic (only Deepgram does).
extension StreamingProviderStrategy {

    /// Default: no custom URLRequest needed (use URL directly)
    func buildWebSocketRequest(url: URL, config: StreamingSessionConfig) -> URLRequest? { nil }

    /// Default: no custom subprotocols
    func webSocketSubprotocols(config: StreamingSessionConfig) -> [String]? { nil }

    /// Default: no action needed on audio send opportunity
    func onAudioSendOpportunity(webSocketSend: @escaping (URLSessionWebSocketTask.Message) -> Void) {}

    /// Default: vocabulary is supported
    var supportsVocabulary: Bool { true }

    /// Default: provider must emit explicit session started event
    var sessionStartsOnWebSocketOpen: Bool { false }

    /// Default cloud streaming capture format.
    var audioSampleRate: Double { 16000 }

    /// Default: no startup messages.
    func startMessages(config: StreamingSessionConfig) -> [URLSessionWebSocketTask.Message] { [] }
}

// MARK: - Streaming Transcription Provider Enum

/// Available streaming transcription providers.
///
/// Used for:
/// 1. Settings storage (AppStorage raw value)
/// 2. UI picker in StreamingView
/// 3. Strategy routing in RecordingTranscriptionFlow+Streaming
///
/// PROVIDER CAPABILITIES:
/// | Provider         | Auth Method    | Vocabulary | Post-Processing |
/// |-----------------|----------------|------------|-----------------|
/// | HyperWhisper    | license/device | Yes        | Yes (server)    |
/// | Deepgram        | WebSocket subprotocol | Yes* | No              |
/// | ElevenLabs      | API key header | No         | No              |
/// | xAI             | Bearer header  | No         | No              |
///
/// *Deepgram vocabulary only works with explicit language (not auto-detect)
enum StreamingTranscriptionProvider: String, CaseIterable, Identifiable {
    case hyperwhisperCloud = "hyperwhisperCloud"
    case deepgram = "deepgram"
    case elevenLabs = "elevenLabs"
    case openAI = "openAI"
    case xai = "xai"
    case parakeetLocal = "parakeetLocal"
    case nemotronLocal = "nemotronLocal"

    /// Identifiable conformance for SwiftUI Picker
    var id: String { rawValue }

    /// User-facing display name shown in the provider picker
    var displayName: String {
        switch self {
        case .hyperwhisperCloud: return "HyperWhisper Cloud"
        case .deepgram: return "Deepgram"
        case .elevenLabs: return "ElevenLabs"
        case .openAI: return "OpenAI"
        case .xai: return "xAI"
        case .parakeetLocal: return "Parakeet (On-Device)"
        case .nemotronLocal: return "Nemotron 3.5 (On-Device)"
        }
    }

    /// Whether this provider requires a user-provided API key.
    ///
    /// HyperWhisper Cloud uses license key or device ID (managed internally).
    /// Direct providers require the user to configure their own API key.
    /// Parakeet / Nemotron run locally and need no API key.
    var requiresAPIKey: Bool {
        switch self {
        case .hyperwhisperCloud, .parakeetLocal, .nemotronLocal: return false
        case .deepgram, .elevenLabs, .openAI, .xai: return true
        }
    }

    /// Whether this provider runs entirely on-device (no network required).
    var isLocal: Bool {
        switch self {
        case .parakeetLocal, .nemotronLocal: return true
        case .hyperwhisperCloud, .deepgram, .elevenLabs, .openAI, .xai: return false
        }
    }

    /// Maps to KeychainManager's API key type for key retrieval.
    ///
    /// Returns nil for HyperWhisper Cloud since it doesn't use the keychain.
    var apiKeyType: KeychainManager.APIKeyType? {
        switch self {
        case .hyperwhisperCloud, .parakeetLocal, .nemotronLocal: return nil
        case .deepgram: return .deepgram
        case .elevenLabs: return .elevenLabs
        case .openAI: return .openAI
        case .xai: return .grok
        }
    }
}
