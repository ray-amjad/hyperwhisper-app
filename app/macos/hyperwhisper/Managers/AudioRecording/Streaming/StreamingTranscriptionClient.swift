//
//  StreamingTranscriptionClient.swift
//  hyperwhisper
//
//  STREAMING TRANSCRIPTION SERVICE
//  Real-time WebSocket-based transcription using pluggable provider strategies.
//  Text types directly as you speak using CGEvent character typing.
//
//  ARCHITECTURE:
//  ┌─────────────────────┐     ┌───────────────────────────┐     ┌──────────────┐
//  │  StreamingAudioCapture│───▶│  StreamingTranscription   │────▶│  Provider    │
//  │  (16kHz PCM)         │    │  Client (WebSocket)       │     │  (Strategy)  │
//  └─────────────────────┘     └───────────────────────────┘     └──────────────┘
//           │                           │                              │
//           │                           │ transcript updates           │ WebSocket
//           ▼                           ▼                              ▼
//  ┌─────────────────┐     ┌───────────────────────┐     ┌──────────────────────┐
//  │  Mic Input      │     │  AccessibilityHelper  │     │  HW Cloud / Deepgram │
//  │  (installTap)   │     │  (typeText)           │     │  / ElevenLabs        │
//  └─────────────────┘     └───────────────────────┘     └──────────────────────┘
//
//  STRATEGY PATTERN:
//  The client delegates provider-specific behavior to a StreamingProviderStrategy:
//  - URL construction → strategy.buildWebSocketURL(config:)
//  - Auth headers → strategy.buildWebSocketRequest(url:config:)
//  - Audio encoding → strategy.encodeAudioChunk(_:)
//  - Message parsing → strategy.parseMessage(_:)
//  - Shutdown → strategy.stopSequence()
//  - Keepalive → strategy.onAudioSendOpportunity(webSocketSend:)
//
//  The client owns the shared concerns:
//  - WebSocket connection lifecycle
//  - Audio capture via StreamingAudioCapture
//  - Connection state machine
//  - Callback wiring (onTranscriptUpdate, onError, etc.)
//  - Auto-reconnect logic (one attempt within 3 seconds)
//
//  FLOW:
//  1. Client builds URL via strategy and connects WebSocket
//  2. Strategy parses "session started" event from provider
//  3. Client starts StreamingAudioCapture, encodes audio via strategy
//  4. Strategy parses incoming messages → normalized StreamingProviderEvent
//  5. Client dispatches events to callbacks
//  6. On stop, client executes strategy's stop sequence
//
//  AUTO-RECONNECT:
//  If the WebSocket drops unexpectedly (not user-initiated), the client:
//  1. Enters .reconnecting state (amber UI indicator)
//  2. Keeps audio capture running (engine stays warm)
//  3. Waits 1 second, then attempts to reconnect with same URL
//  4. If reconnect succeeds → back to .streaming
//  5. If reconnect fails → stops audio, enters .error state
//  Audio data produced during reconnection is discarded (not buffered).
//

import Foundation
import AVFAudio
import os

// MARK: - Streaming Transcription Service

/// Real-time streaming transcription service using WebSockets.
/// Uses a pluggable StreamingProviderStrategy to support multiple providers
/// (HyperWhisper Cloud, Deepgram, ElevenLabs) through a single client.
///
/// USAGE:
/// ```swift
/// let strategy = HyperWhisperCloudStrategy()
/// let service = StreamingTranscriptionClient(strategy: strategy)
/// service.onTranscriptUpdate = { text, isFinal in
///     if isFinal {
///         await TextInputService.shared.typeSegment(text + " ", language: "en")
///     }
/// }
/// let config = StreamingSessionConfig(...)
/// try await service.startSession(config: config)
/// // Audio chunks are sent automatically via the audio capture
/// await service.stopSession()
/// ```
@MainActor
class StreamingTranscriptionClient: NSObject, ObservableObject, StreamingClientProtocol {

    // MARK: - Published State

    /// Whether the WebSocket is connected to the server
    @Published private(set) var isConnected = false

    /// Whether audio is actively being streamed
    @Published private(set) var isStreaming = false

    /// Current session ID (set when provider sends session started event)
    @Published private(set) var sessionId: String?

    /// Last error that occurred
    @Published private(set) var lastError: Error?

    // MARK: - Callbacks

    /// Called when a transcript update is received.
    /// - Parameters:
    ///   - text: The transcript text
    ///   - isFinal: If true, this is committed text that won't change
    var onTranscriptUpdate: ((String, Bool) -> Void)?

    /// Called when the session completes.
    /// - Parameters:
    ///   - durationSeconds: Total audio duration processed
    ///   - creditsUsed: Credits deducted for this session (0 for direct providers)
    var onSessionComplete: ((Double, Double) -> Void)?

    /// Called when an error occurs.
    var onError: ((Error) -> Void)?

    /// Called when the server sends a warning (e.g., session approaching max duration).
    var onWarning: ((String) -> Void)?

    /// Called when the connection state changes.
    /// Provides real-time feedback about WebSocket connection and audio streaming status.
    var onConnectionStateChange: ((StreamingConnectionState) -> Void)?

    /// Called with normalized input levels for waveform visualization.
    var onAudioLevel: ((Float) -> Void)?

    // MARK: - Private Properties

    /// Logger for streaming operations
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "StreamingTranscription")

    /// Provider strategy that encapsulates WebSocket protocol differences.
    /// Set once at init and used throughout the session lifecycle.
    private let strategy: StreamingProviderStrategy

    /// Audio capture component that manages the AVAudioEngine lifecycle.
    /// Created when the session starts, destroyed when it stops.
    private var audioCapture: StreamingAudioCapture?

    /// WebSocket task for server communication
    private var webSocketTask: URLSessionWebSocketTask?

    /// URL session for WebSocket
    private var urlSession: URLSession?

    /// Task for receiving WebSocket messages
    private var receiveTask: Task<Void, Never>?

    /// Track if we initiated the close (to distinguish user-initiated stop from unexpected disconnect)
    private var didInitiateClose = false

    /// The session config, stored for reconnect attempts.
    /// Saved when startSession is called so handleUnexpectedDisconnect can rebuild the connection.
    private var currentConfig: StreamingSessionConfig?

    /// Number of reconnect attempts in the current session.
    /// Reset to 0 on successful startSession(), and lazily in handleUnexpectedDisconnect()
    /// when the previous connection was stable for a while (isolated network blip).
    /// If >= 3, skip reconnect and go straight to error.
    private var reconnectCount = 0

    /// When the current WebSocket connection was (re)established.
    /// Used to distinguish isolated network blips spread across a long session
    /// (connection stable between drops) from a rapid flapping loop.
    private var connectionEstablishedAt = Date()

    /// If a connection stayed up at least this long before dropping, the drop is
    /// treated as an isolated blip and the reconnect budget is reset.
    private static let stableConnectionResetInterval: TimeInterval = 60

    /// True while handleUnexpectedDisconnect is mid-flight.
    /// Although the class is @MainActor, handleUnexpectedDisconnect suspends
    /// (Task.sleep, waitForSessionStarted) while its own freshly started
    /// receiveLoop runs interleaved on the same actor. If that new socket drops,
    /// the loop would re-enter handleUnexpectedDisconnect concurrently —
    /// orphaning the in-flight socket and double-counting reconnect attempts.
    private var isReconnecting = false

    /// True if a disconnect was swallowed by the re-entrancy guard while a
    /// reconnect was in flight. Checked after waitForSessionStarted() so a
    /// reconnect whose fresh socket opened and then immediately dropped is
    /// treated as a failure instead of a false success (the WebSocket-open
    /// delegate can set sessionId before the drop, so waitForSessionStarted
    /// alone would report success on a dead socket).
    private var disconnectedDuringReconnect = false

    /// True once the provider has acknowledged the final session flush.
    /// Used by stop sequences that must wait for a completion event before closing.
    private var didReceiveSessionComplete = false

    // MARK: - Initialization

    /// Create a streaming transcription client with a specific provider strategy.
    ///
    /// The strategy determines how the client communicates with the provider:
    /// URL format, auth headers, audio encoding, message parsing, and shutdown sequence.
    ///
    /// - Parameter strategy: The provider strategy to use for this session
    init(strategy: StreamingProviderStrategy) {
        self.strategy = strategy
        super.init()
    }

    // MARK: - Public Accessors

    /// Human-readable label for the current provider, used in history entries.
    /// Delegates to the strategy (e.g., "HyperWhisper Cloud (Streaming)", "Deepgram (Streaming)")
    var transcriptionProviderLabel: String {
        strategy.transcriptionProviderLabel
    }

    // MARK: - Public Methods

    /// Start a streaming transcription session.
    ///
    /// FLOW:
    /// 1. Build WebSocket URL via strategy
    /// 2. Create WebSocket task (with optional auth headers from strategy)
    /// 3. Wait for session started event from provider
    /// 4. Start StreamingAudioCapture, wire audio data to WebSocket
    /// 5. Enter .streaming state
    ///
    /// - Parameter config: Session configuration with auth, language, vocabulary, etc.
    /// - Throws: StreamingError if connection fails or times out
    func startSession(config: StreamingSessionConfig) async throws {
        logger.info("Starting streaming session...")

        // Save config for potential reconnect
        currentConfig = config

        // Emit connecting state
        onConnectionStateChange?(.connecting)

        // Reset state
        lastError = nil
        didInitiateClose = false
        reconnectCount = 0
        connectionEstablishedAt = Date()
        didReceiveSessionComplete = false

        // STEP 1: Build WebSocket URL via strategy
        guard let url = strategy.buildWebSocketURL(config: config) else {
            throw StreamingError.invalidURL
        }

        // Log only host + path; the query string carries the license_key/device_id
        // bearer credential and must never be written to the unified log.
        logger.debug("WebSocket connecting to host=\(url.host() ?? "?", privacy: .public) path=\(url.path, privacy: .public)")

        // STEP 2: Create URL session if needed
        if urlSession == nil {
            let sessionConfig = URLSessionConfiguration.default
            sessionConfig.timeoutIntervalForRequest = 30
            sessionConfig.timeoutIntervalForResource = 300
            urlSession = URLSession(configuration: sessionConfig, delegate: self, delegateQueue: nil)
        }

        // STEP 3: Create WebSocket task
        webSocketTask = makeWebSocketTask(url: url, config: config)
        webSocketTask?.resume()

        // Start receiving messages
        startReceivingMessages()

        for message in strategy.startMessages(config: config) {
            try await webSocketTask?.send(message)
        }

        // STEP 4: Wait for session started event
        do {
            try await waitForSessionStarted()
        } catch {
            // CLEANUP ON FAILED CONNECTION:
            // The receiveLoop (started above) is still running on the dead socket.
            // If we don't suppress reconnect, it will hit an error → call
            // handleUnexpectedDisconnect() → rebuild WebSocket → new receiveLoop
            // → infinite cycle. Setting didInitiateClose prevents reconnect for
            // connections that were never successfully established.
            didInitiateClose = true
            receiveTask?.cancel()
            receiveTask = nil
            webSocketTask?.cancel(with: .abnormalClosure, reason: nil)
            webSocketTask = nil

            // Emit error state for connection timeout before rethrowing
            if let streamingError = error as? StreamingError,
               case .connectionTimeout = streamingError {
                onConnectionStateChange?(.error("Connection timed out"))
            }
            throw error
        }

        // STEP 5: Start audio capture and wire to WebSocket
        let capture = StreamingAudioCapture(targetSampleRate: strategy.audioSampleRate)
        audioCapture = capture
        wireAudioLevelCallback(to: capture)

        // Wire audio data: capture → strategy.encode → WebSocket send
        //
        // FLOW FOR EACH AUDIO CHUNK:
        // 1. StreamingAudioCapture delivers 16kHz mono Int16 PCM via callback
        // 2. Strategy's onAudioSendOpportunity fires (e.g., Deepgram KeepAlive check)
        // 3. Strategy encodes the PCM data (raw binary for HW Cloud/Deepgram, base64 JSON for ElevenLabs)
        // 4. Encoded message is sent over WebSocket
        //
        // SOCKET BINDING: capture the socket at wiring time instead of re-reading
        // self.webSocketTask inside the callback. The callback fires on the audio
        // tap thread, so an in-flight invocation can race a reconnect: it would
        // otherwise read the freshly swapped-in socket and send audio before that
        // socket's startMessages configure the session. Binding to one socket
        // generation means a stale closure can only ever hit its own (dead) socket.
        let connectedSocket = webSocketTask
        capture.onAudioData = { [weak self] pcmData in
            guard let self = self, let ws = connectedSocket else { return }

            // Let strategy handle provider-specific periodic tasks (e.g., Deepgram KeepAlive)
            self.strategy.onAudioSendOpportunity { msg in
                ws.send(msg) { _ in }
            }

            // Encode and send the audio chunk
            let encoded = self.strategy.encodeAudioChunk(pcmData)
            ws.send(encoded) { _ in }
        }

        try await capture.start()

        isConnected = true
        isStreaming = true
        onConnectionStateChange?(.streaming)
        logger.info("Streaming session started successfully")

        // Log WebSocket connected for diagnostics
        SentryService.addBreadcrumb(
            message: "Direct WebSocket connected",
            category: "audio.streaming",
            data: [
                "provider": strategy.transcriptionProviderLabel,
                "model": config.model ?? "default",
                "sessionId": sessionId ?? "unknown"
            ]
        )
    }

    /// Stop the streaming session gracefully.
    ///
    /// FLOW:
    /// 1. Stop audio capture
    /// 2. Execute strategy's stop sequence (e.g., send stop JSON → wait → close)
    /// 3. Clean up WebSocket and receive task
    /// 4. Enter .idle state
    func stopSession() async {
        logger.info("Stopping streaming session...")

        onConnectionStateChange?(.disconnecting)
        isStreaming = false
        didInitiateClose = true

        // STEP 1: Stop audio capture first to prevent sending audio during shutdown
        audioCapture?.stop()
        audioCapture = nil
        onAudioLevel?(0)

        // STEP 2: Execute strategy's stop sequence with a bounded timeout.
        // Without a timeout, sending on a dead socket can hang for up to 30s → rainbow spinner.
        // Each provider defines its own graceful shutdown steps:
        // - HW Cloud: send stop JSON → wait 0.5s → close WebSocket
        // - Deepgram: send Finalize → wait 0.5s → send CloseStream → close WebSocket
        // - ElevenLabs: just close WebSocket (no stop message needed)
        let stopSequence = strategy.stopSequence()
        let stopTask = Task {
            for step in stopSequence {
                try Task.checkCancellation()
                switch step {
                case .sendText(let text):
                    do {
                        try await webSocketTask?.send(.string(text))
                        logger.debug("Sent stop sequence message")
                    } catch {
                        logger.warning("Failed to send stop message: \(error.localizedDescription, privacy: .public)")
                    }
                case .wait(let seconds):
                    try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
                case .waitForSessionComplete(let seconds):
                    do {
                        try await waitForSessionComplete(timeout: seconds)
                    } catch {
                        logger.warning("Timed out waiting for session completion: \(error.localizedDescription, privacy: .public)")
                    }
                case .closeWebSocket:
                    webSocketTask?.cancel(with: .normalClosure, reason: nil)
                }
            }
        }

        // Race the stop sequence against a bounded timeout. Providers that wait
        // for an explicit completion event need enough headroom to flush.
        let stopTimeout = stopSequence.reduce(5.0) { timeout, step in
            switch step {
            case .waitForSessionComplete(let seconds):
                return max(timeout, seconds + 1.0)
            case .wait(let seconds):
                return max(timeout, seconds + 1.0)
            case .sendText, .closeWebSocket:
                return timeout
            }
        }
        let timeoutTask = Task {
            try await Task.sleep(nanoseconds: UInt64(stopTimeout * 1_000_000_000))
            stopTask.cancel()
            logger.warning("Stop sequence timed out after \(stopTimeout, privacy: .public)s — force-closing WebSocket")
            webSocketTask?.cancel(with: .abnormalClosure, reason: nil)
        }

        try? await stopTask.value
        timeoutTask.cancel()

        // STEP 3: Clean up
        webSocketTask = nil
        receiveTask?.cancel()
        receiveTask = nil
        currentConfig = nil

        // Break URLSession → delegate retain cycle
        urlSession?.invalidateAndCancel()
        urlSession = nil

        isConnected = false
        sessionId = nil
        onConnectionStateChange?(.idle)
        logger.info("Streaming session stopped")
    }

    // MARK: - Private Methods

    /// Start receiving WebSocket messages in a background task.
    private func startReceivingMessages() {
        receiveTask = Task { [weak self] in
            await self?.receiveLoop()
        }
    }

    /// Route capture metering back to the main actor before notifying UI state.
    private func wireAudioLevelCallback(to capture: StreamingAudioCapture) {
        capture.onAudioLevel = { [weak self] level in
            Task { @MainActor [weak self] in
                self?.onAudioLevel?(level)
            }
        }
    }

    /// Continuous loop to receive WebSocket messages.
    ///
    /// Runs until the task is cancelled or an error occurs.
    /// On unexpected disconnect (not user-initiated), triggers auto-reconnect.
    private func receiveLoop() async {
        guard let task = webSocketTask else { return }

        while !Task.isCancelled {
            do {
                let message = try await task.receive()
                await handleMessage(message)
            } catch {
                // SERVER-INITIATED CLOSE (4001 credits exhausted / 4002 max duration):
                // The didCloseWith delegate sets didInitiateClose via a separately
                // enqueued Task { @MainActor in ... }, which can land AFTER this catch
                // runs — receive() errors resume this loop on the main actor directly,
                // with no ordering guarantee relative to that delegate task. Read the
                // close code off the task synchronously (URLSession populates it when
                // the close frame is processed, before failing pending receives) so we
                // never start a doomed reconnect cycle for these codes.
                let rawCloseCode = task.closeCode.rawValue
                if rawCloseCode == 4001 || rawCloseCode == 4002 {
                    logger.info("Server-initiated close (code=\(rawCloseCode, privacy: .public)) — suppressing reconnect")
                    didInitiateClose = true
                }

                if !didInitiateClose {
                    // UNEXPECTED DISCONNECT — attempt auto-reconnect
                    await MainActor.run {
                        self.logger.error("WebSocket receive error: \(error.localizedDescription, privacy: .public)")
                    }
                    SentryService.capture(
                        error: error,
                        message: "WebSocket unexpected disconnect",
                        tags: [
                            "component": "StreamingTranscriptionClient",
                            "provider": strategy.transcriptionProviderLabel,
                            "operation": "receiveLoop",
                            "reconnectCount": "\(reconnectCount)"
                        ]
                    )
                    await handleUnexpectedDisconnect()
                }
                break
            }
        }
    }

    /// Handle a received WebSocket message.
    ///
    /// WebSocket messages arrive as either .string (JSON text) or .data (binary).
    /// Both are routed to processServerMessage for strategy-based parsing.
    private func handleMessage(_ message: URLSessionWebSocketTask.Message) async {
        switch message {
        case .string(let text):
            await processServerMessage(text)
        case .data(let data):
            if let text = String(data: data, encoding: .utf8) {
                await processServerMessage(text)
            }
        @unknown default:
            logger.warning("Unknown WebSocket message type")
        }
    }

    /// Process a JSON message from the provider using the strategy's parser.
    ///
    /// DELEGATION TO STRATEGY:
    /// The strategy's parseMessage() converts provider-specific JSON into normalized
    /// StreamingProviderEvent values. This method then dispatches each event type
    /// to the appropriate callback.
    ///
    /// EVENT DISPATCH:
    /// | Event              | Action                                          |
    /// |-------------------|------------------------------------------------|
    /// | .sessionStarted   | Store session ID, log                           |
    /// | .finalTranscript  | Call onTranscriptUpdate with isFinal=true       |
    /// | .partialTranscript| Call onTranscriptUpdate with isFinal=false      |
    /// | .sessionComplete  | Call onSessionComplete with duration and credits |
    /// | .error            | Store error, emit error state, call onError     |
    /// | .metadata         | Debug log only (not surfaced to UI)             |
    private func processServerMessage(_ jsonString: String) async {
        guard let event = strategy.parseMessage(jsonString) else {
            logger.debug("Unhandled message from provider")
            return
        }

        switch event {
        case .sessionStarted(let id):
            await MainActor.run {
                self.sessionId = id ?? "direct"
                self.logger.info("Session started: \(self.sessionId ?? "unknown", privacy: .public)")
            }

        case .finalTranscript(let text):
            await MainActor.run {
                self.onTranscriptUpdate?(text, true)
            }

        case .finalTranscriptAndSessionComplete(let text, let duration, let credits):
            await MainActor.run {
                self.onTranscriptUpdate?(text, true)
                self.didReceiveSessionComplete = true
                self.logger.info("Session complete: \(duration, privacy: .public)s, \(credits, privacy: .public) credits")
                self.onSessionComplete?(duration, credits)
            }

        case .partialTranscript(let text):
            await MainActor.run {
                self.onTranscriptUpdate?(text, false)
            }

        case .sessionComplete(let duration, let credits):
            await MainActor.run {
                self.didReceiveSessionComplete = true
                self.logger.info("Session complete: \(duration, privacy: .public)s, \(credits, privacy: .public) credits")
                self.onSessionComplete?(duration, credits)
            }

        case .error(let message):
            await MainActor.run {
                self.logger.error("Provider error: \(message, privacy: .public)")
                let error = StreamingError.serverError(message)
                self.lastError = error
                SentryService.capture(
                    error: error,
                    message: "WebSocket provider error",
                    extras: ["serverMessage": message],
                    tags: [
                        "component": "StreamingTranscriptionClient",
                        "provider": self.strategy.transcriptionProviderLabel,
                        "operation": "processServerMessage"
                    ]
                )
                self.onConnectionStateChange?(.error(message))
                self.onError?(error)
            }

        case .warning(let message):
            await MainActor.run {
                self.logger.warning("Server warning: \(message, privacy: .public)")
                self.onWarning?(message)
            }

        case .metadata(let raw):
            logger.debug("Provider metadata: \(raw, privacy: .public)")
        }
    }

    /// Wait for a provider completion event after graceful stop is requested.
    private func waitForSessionComplete(timeout: TimeInterval) async throws {
        if didReceiveSessionComplete { return }

        try await withThrowingTaskGroup(of: Void.self) { group in
            group.addTask {
                try await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
                throw StreamingError.connectionTimeout
            }

            group.addTask { [weak self] in
                while await MainActor.run(body: { self?.didReceiveSessionComplete == false }) {
                    try Task.checkCancellation()
                    try await Task.sleep(nanoseconds: 50_000_000)
                }
            }

            try await group.next()
            group.cancelAll()
        }
    }

    /// Wait for the provider to send a session started event.
    ///
    /// Polls sessionId with a 10-second timeout. The sessionId is set by
    /// processServerMessage when it receives a .sessionStarted event from
    /// the strategy's parser.
    ///
    /// TIMEOUT:
    /// If the provider doesn't send a ready/session_started message within 10 seconds,
    /// throws StreamingError.connectionTimeout. This prevents hanging indefinitely
    /// if the server is unreachable or authentication fails silently.
    private func waitForSessionStarted() async throws {
        let timeout: TimeInterval = 10

        try await withThrowingTaskGroup(of: Void.self) { group in
            // Timeout task
            group.addTask {
                try await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
                throw StreamingError.connectionTimeout
            }

            // Wait for session started task
            group.addTask { [weak self] in
                while await MainActor.run(body: { self?.sessionId }) == nil {
                    try Task.checkCancellation()
                    try await Task.sleep(nanoseconds: 50_000_000) // 50ms poll interval
                }
            }

            // Wait for first completion (either session started or timeout)
            try await group.next()
            group.cancelAll()
        }
    }

    /// Handle an unexpected WebSocket disconnect by attempting one auto-reconnect.
    ///
    /// AUTO-RECONNECT BEHAVIOR:
    /// 1. Enter .reconnecting state (shows amber indicator in UI)
    /// 2. Keep audio capture running (engine stays warm, audio data is discarded)
    /// 3. Wait 1 second before attempting reconnect
    /// 4. Rebuild WebSocket connection using the saved config
    /// 5. If successful: back to .streaming, audio data flows again
    /// 6. If failed: stop audio capture, enter .error state
    ///
    /// WHY ONLY ONE ATTEMPT:
    /// Multiple retries with backoff adds complexity and delays the inevitable.
    /// A single reconnect handles transient network blips (WiFi handoff, brief
    /// packet loss). Persistent failures should surface to the user immediately.
    ///
    /// WHY KEEP AUDIO RUNNING:
    /// Stopping and restarting AVAudioEngine takes ~200ms and can cause audible
    /// glitches. Keeping it warm means reconnection is near-instant if it succeeds.
    private func handleUnexpectedDisconnect() async {
        // RE-ENTRANCY GUARD:
        // The receiveLoop started by an in-flight reconnect (below) can fail and
        // call back into this method while the original call is still suspended
        // in waitForSessionStarted. Without this guard, two interleaved handlers
        // each build a socket (orphaning the other's), bump reconnectCount twice
        // per blip, and the stale handler's timeout path can cancel the live
        // socket of a reconnect that just succeeded. Ignore the re-entrant call,
        // but record it: the original handler checks disconnectedDuringReconnect
        // after waitForSessionStarted so a socket that opened (setting sessionId
        // via the open delegate) and then dropped is treated as a failed
        // reconnect rather than a false success on a dead socket.
        guard !isReconnecting else {
            logger.debug("Reconnect already in progress — recording re-entrant disconnect")
            disconnectedDuringReconnect = true
            return
        }
        isReconnecting = true
        defer { isReconnecting = false }

        // A connection that stayed up for a while before dropping is an isolated
        // network blip, not part of a flapping loop — reset the reconnect budget
        // so long sessions don't exhaust it after 3 lifetime blips (#246).
        // Rapid drop cycles (connect → drop within seconds) keep accumulating
        // and still hit the limit below.
        if Date().timeIntervalSince(connectionEstablishedAt) >= Self.stableConnectionResetInterval {
            reconnectCount = 0
        }

        reconnectCount += 1

        // Guard against unbounded reconnect cycles.
        // After 3 failed reconnects, stop trying and surface the error.
        if reconnectCount > 3 {
            logger.error("Reconnect cycle limit reached (\(self.reconnectCount) attempts) — giving up")
            SentryService.capture(
                error: StreamingError.serverError("Reconnect cycle limit reached"),
                message: "WebSocket reconnect cycle exhausted",
                extras: ["reconnectCount": "\(reconnectCount)"],
                tags: [
                    "component": "StreamingTranscriptionClient",
                    "provider": strategy.transcriptionProviderLabel,
                    "operation": "handleUnexpectedDisconnect"
                ]
            )
            didInitiateClose = true
            receiveTask?.cancel()
            receiveTask = nil
            webSocketTask?.cancel(with: .abnormalClosure, reason: nil)
            webSocketTask = nil
            await MainActor.run {
                self.audioCapture?.stop()
                self.audioCapture = nil
                self.isConnected = false
                self.isStreaming = false
                self.onAudioLevel?(0)
                self.onConnectionStateChange?(.error("Connection lost after multiple retries"))
                self.onError?(StreamingError.serverError("Connection lost after multiple retries"))
            }
            return
        }

        await MainActor.run {
            self.isConnected = false
            self.onConnectionStateChange?(.reconnecting)
        }

        // Log reconnect attempt for diagnostics
        SentryService.addBreadcrumb(
            message: "Reconnect attempt",
            category: "audio.streaming",
            data: [
                "provider": strategy.transcriptionProviderLabel,
                "attempt": reconnectCount
            ]
        )

        // Wait before reconnect attempt
        try? await Task.sleep(nanoseconds: 1_000_000_000) // 1 second

        // Attempt to reconnect using saved config
        guard let config = currentConfig,
              let url = strategy.buildWebSocketURL(config: config) else {
            await MainActor.run {
                self.audioCapture?.stop()
                self.audioCapture = nil
                self.isStreaming = false
                self.onAudioLevel?(0)
                self.onConnectionStateChange?(.error("Connection lost"))
                self.onError?(StreamingError.serverError("Connection lost and reconnect failed"))
            }
            return
        }

        // Detach the audio callback before assigning the new WebSocket task.
        // The capture engine keeps running during reconnect, and the callback
        // re-reads self.webSocketTask on every invocation — without this, audio
        // chunks land on the new socket before startMessages configure the
        // session (OpenAI Realtime rejects appends sent before session.update).
        // The callback is re-wired below once the session is re-established.
        audioCapture?.onAudioData = nil

        // Rebuild WebSocket connection
        webSocketTask = makeWebSocketTask(url: url, config: config)
        webSocketTask?.resume()

        // Cancel the old receive task before starting a new one.
        // Without this, the old receiveLoop could still be running (blocked on receive())
        // and we'd end up with two concurrent receive loops on different WebSocket tasks.
        receiveTask?.cancel()
        receiveTask = nil

        // Reset session ID so waitForSessionStarted can detect the new ready message
        await MainActor.run {
            self.sessionId = nil
        }

        disconnectedDuringReconnect = false
        startReceivingMessages()

        // Wait for session to be re-established
        do {
            for message in strategy.startMessages(config: config) {
                try await webSocketTask?.send(message)
            }
            try await waitForSessionStarted()

            // The fresh socket may have opened (satisfying waitForSessionStarted
            // via the open delegate) and then dropped while we were suspended —
            // that re-entrant disconnect was swallowed by the guard above.
            // Treat it as a failed reconnect instead of wiring audio to a dead socket.
            if disconnectedDuringReconnect {
                throw StreamingError.serverError("Socket dropped during reconnect")
            }

            // Mark when this connection came up so the next disconnect can tell
            // an isolated blip from a rapid flapping loop.
            connectionEstablishedAt = Date()

            // Reconnect succeeded — re-wire audio capture to new WebSocket.
            // Bind the closure to the post-handshake socket rather than re-reading
            // self.webSocketTask: a stale closure invocation already in flight on
            // the audio tap thread can otherwise read the new socket and send
            // audio before startMessages/session.updated complete.
            let reconnectedSocket = webSocketTask
            audioCapture?.onAudioData = { [weak self] pcmData in
                guard let self = self, let ws = reconnectedSocket else { return }
                self.strategy.onAudioSendOpportunity { msg in
                    ws.send(msg) { _ in }
                }
                let encoded = self.strategy.encodeAudioChunk(pcmData)
                ws.send(encoded) { _ in }
            }

            await MainActor.run {
                self.isConnected = true
                self.onConnectionStateChange?(.streaming)
                self.logger.info("Reconnect succeeded")
            }

            // Log reconnect success for diagnostics
            SentryService.addBreadcrumb(
                message: "Reconnect success",
                category: "audio.streaming",
                data: [
                    "provider": strategy.transcriptionProviderLabel,
                    "success": true
                ]
            )
        } catch {
            // Reconnect failed — prevent leftover receiveTask from triggering another cycle
            didInitiateClose = true
            receiveTask?.cancel()
            receiveTask = nil
            webSocketTask?.cancel(with: .abnormalClosure, reason: nil)
            webSocketTask = nil

            // Clean up and surface error
            await MainActor.run {
                self.audioCapture?.stop()
                self.audioCapture = nil
                self.isStreaming = false
                self.onAudioLevel?(0)
                self.onConnectionStateChange?(.error("Connection lost"))
                self.onError?(StreamingError.serverError("Connection lost and reconnect failed"))
                self.logger.error("Reconnect failed: \(error.localizedDescription, privacy: .public)")
            }

            // Capture reconnect failure to Sentry for diagnostics
            SentryService.capture(
                error: error,
                message: "WebSocket reconnect failed",
                extras: ["reconnectCount": "\(reconnectCount)"],
                tags: [
                    "component": "StreamingTranscriptionClient",
                    "provider": strategy.transcriptionProviderLabel,
                    "operation": "reconnect"
                ]
            )
        }
    }

    /// Build a provider-specific WebSocket task from URL/config.
    ///
    /// Priority order:
    /// 1. Custom URLRequest (header-based auth)
    /// 2. Subprotocols (handshake-based auth)
    /// 3. Plain URL task
    private func makeWebSocketTask(url: URL, config: StreamingSessionConfig) -> URLSessionWebSocketTask? {
        if let request = strategy.buildWebSocketRequest(url: url, config: config) {
            return urlSession?.webSocketTask(with: request)
        }
        if let subprotocols = strategy.webSocketSubprotocols(config: config), !subprotocols.isEmpty {
            return urlSession?.webSocketTask(with: url, protocols: subprotocols)
        }
        return urlSession?.webSocketTask(with: url)
    }
}

// MARK: - URLSessionWebSocketDelegate

extension StreamingTranscriptionClient: URLSessionWebSocketDelegate {

    nonisolated func urlSession(
        _ session: URLSession,
        webSocketTask: URLSessionWebSocketTask,
        didOpenWithProtocol protocol: String?
    ) {
        Task { @MainActor in
            logger.info("WebSocket connected")
            if strategy.sessionStartsOnWebSocketOpen, sessionId == nil {
                sessionId = "direct"
            }
            onConnectionStateChange?(.ready)
        }
    }

    nonisolated func urlSession(
        _ session: URLSession,
        webSocketTask: URLSessionWebSocketTask,
        didCloseWith closeCode: URLSessionWebSocketTask.CloseCode,
        reason: Data?
    ) {
        Task { @MainActor in
            let reasonString = reason.flatMap { String(data: $0, encoding: .utf8) } ?? "none"
            logger.info("WebSocket closed: code=\(closeCode.rawValue, privacy: .public), reason=\(reasonString, privacy: .public)")

            // Server-initiated close for credits exhausted (4001) or max duration (4002):
            // Suppress auto-reconnect since reconnecting would just fail again immediately.
            let rawCode = closeCode.rawValue
            if rawCode == 4001 || rawCode == 4002 {
                didInitiateClose = true
            }

            if !didInitiateClose {
                isConnected = false
                isStreaming = false
                onConnectionStateChange?(.error("Connection lost"))
            }
        }
    }
}

// MARK: - Streaming Errors

/// Errors that can occur during streaming transcription.
///
/// Used by both the StreamingTranscriptionClient and StreamingAudioCapture.
/// Each case maps to a user-facing error message via LocalizedError conformance.
enum StreamingError: LocalizedError {
    case invalidURL
    case connectionTimeout
    case serverError(String)
    case audioEngineError(String)

    var errorDescription: String? {
        switch self {
        case .invalidURL:
            return "Invalid WebSocket URL"
        case .connectionTimeout:
            return "Connection timed out"
        case .serverError(let message):
            return "Server error: \(message)"
        case .audioEngineError(let message):
            return "Audio error: \(message)"
        }
    }
}
