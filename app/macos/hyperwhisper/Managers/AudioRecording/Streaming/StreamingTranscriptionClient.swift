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

    /// Called when the socket supplies a definitive provider-down verdict.
    /// Unlike `onError`, this does not imply that retries or teardown finished.
    var onDefinitiveProviderFailure: ((Error) -> Void)?

    /// Called after useful provider work: a transcript or a completed session.
    /// A socket opening or session-start acknowledgement alone does not count.
    var onProviderSuccess: (() -> Void)?

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
    private let streamingProvider: StreamingTranscriptionProvider?

    /// Audio capture component that manages the AVAudioEngine lifecycle.
    /// Created when the session starts, destroyed when it stops.
    private var audioCapture: StreamingAudioCapture?

    /// WebSocket task for server communication
    private var webSocketTask: URLSessionWebSocketTask?

    /// URL session for WebSocket
    private var urlSession: URLSession?

    /// Task for receiving WebSocket messages
    private var receiveTask: Task<Void, Never>?

    /// The task that owns an in-flight reconnect.
    ///
    /// `handleUnexpectedDisconnect()` runs *inside* the receive task, and part
    /// way through it hands the `receiveTask` slot to the replacement listener
    /// it starts. From that moment `receiveTask` no longer points at the task
    /// performing the reconnect, so cancelling `receiveTask` alone cancels the
    /// listener and leaves the reconnect running unattached — it then polls a
    /// permanently-nil `sessionId` for the full 10s and reports a connection
    /// failure long after a clean stop (HYPERWHISPER-MG). This second handle is
    /// what `stopSession()` / `cancel()` cancel to genuinely abort a reconnect,
    /// and it is what makes `Task.isCancelled` in the reconnect's `catch` mean
    /// "teardown cancelled us" rather than "somebody cancelled our replacement".
    private var reconnectOwnerTask: Task<Void, Never>?

    /// Track if we initiated the close (to distinguish user-initiated stop from unexpected disconnect)
    private var didInitiateClose = false

    /// True once a terminal provider error (see `StreamingProviderErrorPolicy`)
    /// has been surfaced for this session. Providers commonly repeat the error
    /// frame before closing the socket; by then `onError` has already torn the
    /// flow down, so a second frame would only re-report the same fault and
    /// re-run the teardown. Reset alongside `didInitiateClose` in startSession.
    private var didHandleTerminalProviderError = false

    /// True for the WHOLE of `startSession()` — from before the socket exists
    /// until the session is genuinely up — not merely while
    /// `waitForSessionStarted()` is parked.
    ///
    /// A terminal provider error can arrive *before* the provider ever sends its
    /// session-started frame (ElevenLabs answers a dead key with `auth_error`
    /// straight away, OpenAI and xAI answer an exhausted balance while the
    /// `startMessages` send is still suspended). Startup has to know that, or it
    /// sits out the full 10s and fails with a generic `connectionTimeout` whose
    /// "Failed to start" toast overwrites the actionable provider message this
    /// classification exists to preserve.
    ///
    /// The span has to cover every suspension point in `startSession()`, at both
    /// ends. Raised late, an error during the `startMessages` send takes the
    /// ordinary terminal path and startup still times out. Lowered early — say
    /// the moment `waitForSessionStarted()` returns — a terminal error landing
    /// during the awaited `capture.start()` tears the session down while
    /// `startSession()` goes on to return success: a session reported as started
    /// that has no socket and no microphone.
    ///
    /// Owned by `startSession()` alone, which raises it before anything can
    /// deliver an event and lowers it in a `defer`, so success, throw and
    /// cancellation all leave it false.
    private var isStartingSession = false

    /// A terminal provider error that arrived while `startSession()` was still
    /// in flight.
    ///
    /// `waitForSessionStarted()` polls this alongside `sessionId` and throws it,
    /// and `startSession()` checks it once more after `capture.start()`, so a
    /// failed start carries the provider's own wording instead of a timeout — or
    /// instead of being lost behind a false success. Set only on the startup
    /// path; once the session is established the terminal error is reported
    /// through `onError` as usual. Cleared at both ends of `startSession()` so a
    /// failure from one session can never be observed by the next.
    private var pendingStartupFailure: StreamingError?

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

    /// True from the moment `stopSession()` starts until the next session begins.
    ///
    /// This is the client's own "the user has let go of the key" state, and it is
    /// what a `.sessionComplete` from a per-turn provider is measured against
    /// (see `completeEndsSessionBeforeStop` and the `.sessionComplete` arm of
    /// `processServerMessage`). It is a private flag rather than a read of the
    /// published `StreamingConnectionState.disconnecting` — which is the
    /// condition Windows uses — because this client only ever *publishes*
    /// connection state through `onConnectionStateChange` and never reads it
    /// back, so there is nothing to key on.
    ///
    /// `didInitiateClose` is close but is not the same thing: it is also raised
    /// by terminal-error teardown and by the close delegate, where "the session
    /// is finished" is already true for other reasons and gating a completion on
    /// it would be meaningless. Reset alongside `didReceiveSessionComplete` in
    /// `startSession`.
    private var stopRequested = false

    // MARK: - Session Diagnostics
    //
    // Every streaming fault this file reports used to arrive as a provider
    // sentence and a stack trace, with nothing about what the client had
    // actually done by then. "Error committing input audio buffer: buffer too
    // small. Expected at least 100ms of audio, but buffer only has 85.00ms"
    // (HYPERWHISPER-S8/-S9) is the shape of that: it names the server's
    // complaint and says nothing about how long the microphone had been open,
    // whether the stop flush was already running, or whether the user had been
    // given any transcript at all. The fields below are what answers that, and
    // they ride on every capture in this file plus, through the Sentry scope,
    // on the mirror the recording flow raises for the same fault.
    //
    // All of them are measurements, fixed slugs, or identifiers of code and of
    // a provider request. No transcript, audio, prompt or typed text is
    // recorded here, and nothing derived from one.

    /// When `startSession()` began, for `streaming_session_elapsed_ms`.
    private var sessionStartedAt = Date()

    /// When the microphone actually started delivering audio, or nil before
    /// `capture.start()` returns. The elapsed time from here is how much audio
    /// the client had sent when a provider complained about how little it had.
    private var audioStartedAt: Date?

    /// How many committed and interim transcript events the provider has
    /// delivered this session.
    ///
    /// COUNTS ONLY, AND NAMED SO THEY SURVIVE. `SentryService.beforeSend`
    /// redacts any extra whose KEY contains `transcript`, `text` or `prompt`
    /// without warning the call site, which is how three Windows fields shipped
    /// `[redacted]` for months (HYPERWHISPER-PA). `finals_delivered` arrives;
    /// `final_transcripts` would not.
    private var finalsDelivered = 0
    private var partialsDelivered = 0

    /// How many audio-chunk sends failed this session, and whether the first
    /// one has been reported. See `noteAudioSendFailure(generation:domain:code:)`.
    private var audioSendFailureCount = 0
    private var didReportAudioSendFailure = false

    /// Which session the audio callbacks belong to.
    ///
    /// Bumped once per `startSession()`. The audio-send completion handler hops
    /// to the main actor to report a failure, and that hop can land after the
    /// user has stopped and restarted: without a generation to compare, a chunk
    /// refused by the PREVIOUS socket consumes the new session's one report slot
    /// and is published under the new session's id, stage and elapsed times —
    /// a report describing a session that never had the fault.
    private var sessionGeneration = 0

    /// The stage the session has reached, as a stable slug.
    ///
    /// This is the breadcrumb trail this file appeared to have and did not:
    /// `beforeSend` sets `event.breadcrumbs = nil`, so the "Reconnect attempt"
    /// and "Reconnect success" crumbs below never leave the machine. Scope
    /// extras do, so the path taken is published there instead.
    private var stage = "idle"
    private var didReportProviderSuccess = false

    // MARK: - Initialization

    /// Create a streaming transcription client with a specific provider strategy.
    ///
    /// The strategy determines how the client communicates with the provider:
    /// URL format, auth headers, audio encoding, message parsing, and shutdown sequence.
    ///
    /// - Parameter strategy: The provider strategy to use for this session
    init(
        strategy: StreamingProviderStrategy,
        streamingProvider: StreamingTranscriptionProvider? = nil
    ) {
        self.strategy = strategy
        self.streamingProvider = streamingProvider
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
        didHandleTerminalProviderError = false
        didReportProviderSuccess = false
        pendingStartupFailure = nil
        reconnectCount = 0
        connectionEstablishedAt = Date()
        didReceiveSessionComplete = false
        // A client instance that survived one stop must not treat the NEXT
        // session's first turn boundary as terminal. Cleared here, ahead of
        // STEP 1, so it is cleared even on a start that fails to build a URL.
        stopRequested = false

        // Reset the diagnostics with the rest of the session state, so a report
        // can never carry a count or an elapsed time belonging to the previous
        // recording. `noteStage` republishes the whole set to the Sentry scope,
        // which is what clears the previous session's values there too.
        sessionStartedAt = Date()
        audioStartedAt = nil
        finalsDelivered = 0
        partialsDelivered = 0
        audioSendFailureCount = 0
        didReportAudioSendFailure = false
        sessionGeneration += 1
        noteStage("starting")

        // MARK STARTUP AS PENDING BEFORE ANYTHING CAN PRODUCE AN EVENT.
        //
        // Raised here, ahead of the socket and the receive loop, because the
        // first thing a provider can answer is a terminal error: OpenAI and xAI
        // report an exhausted balance while the `startMessages` send below is
        // still suspended, long before STEP 4. With the flag still down at that
        // moment, that error takes the ordinary terminal path — it tears the
        // session down and fires `onError` — and STEP 4 then waits its full 10s
        // on a `sessionId` that can never arrive and throws a generic
        // `connectionTimeout`, whose "Failed to start" replaces the provider's
        // actionable wording. That substitution is the whole thing this fast
        // path exists to prevent.
        //
        // Lowered in a `defer` rather than at any earlier point, so the flag
        // stays up across every suspension in this function — including
        // `capture.start()` — and is cleared on every exit: success, throw and
        // cancellation alike. `pendingStartupFailure` goes with it, so a failure
        // belonging to this session can never be observed by the next one.
        isStartingSession = true
        defer {
            isStartingSession = false
            pendingStartupFailure = nil
        }

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

        // STEP 4: Send the start messages, then wait for the session-started
        // event.
        //
        // A terminal provider error frame arriving before the session-started
        // frame fails the wait immediately with the provider's own message —
        // see `pendingStartupFailure`, which `isStartingSession` (raised above)
        // is what routes the error into.
        //
        // The send shares this `catch` rather than throwing straight out,
        // because a refused upgrade fails it too: on a 402/401/403 the socket is
        // already dead by the time the first start message goes out, so `send`
        // throws a generic transport error, and an unguarded rethrow leaves this
        // function before the cleanup and the classification below can run.
        do {
            for message in strategy.startMessages(config: config) {
                try await webSocketTask?.send(message)
            }

            try await waitForSessionStarted()
        } catch {
            // CLASSIFY BEFORE TEARING DOWN.
            //
            // A wait that ended in a timeout on a socket the server refused
            // outright is the timeout naming the wrong cause. The status is on
            // the task's response — and `teardownSession` below nils the task,
            // so this has to read it first. This is the last of the three doors
            // a refusal can come through, and the one that catches an ordering
            // nobody has predicted.
            let reported = refusalDuringStartup(shadowing: error)

            // CLEANUP ON FAILED CONNECTION:
            // The receiveLoop (started above) is still running on the dead socket.
            // If we don't suppress reconnect, it will hit an error → call
            // handleUnexpectedDisconnect() → rebuild WebSocket → new receiveLoop
            // → infinite cycle. Setting didInitiateClose prevents reconnect for
            // connections that were never successfully established.
            didInitiateClose = true
            // Runs on the caller's task, not the receive loop, so the receive
            // task really is a different task and must be cancelled.
            teardownSession(cancelReceiveTask: true, cancelReconnect: true)

            // Emit error state for connection timeout before rethrowing.
            //
            // Only the timeout needs this. Every route that turns `error` into a
            // refusal or a terminal frame has already emitted its own `.error`
            // state on the way to setting the failure this catch is rethrowing —
            // `handleTerminalCondition` for the first, `processServerMessage`
            // for the second — so repeating it here would publish the same state
            // twice.
            if let streamingError = reported as? StreamingError,
               case .connectionTimeout = streamingError {
                onConnectionStateChange?(.error("Connection timed out"))
            }
            throw reported
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
        let generation = sessionGeneration
        capture.onAudioData = { [weak self] pcmData in
            guard let self = self, let ws = connectedSocket else { return }

            // Let strategy handle provider-specific periodic tasks (e.g., Deepgram KeepAlive)
            self.strategy.onAudioSendOpportunity { msg in
                ws.send(msg) { _ in }
            }

            // Encode and send the audio chunk.
            //
            // The completion error is recorded, not acted on: no retry, no
            // buffering, no change to what this callback does. A refused chunk
            // is dropped exactly as it always was — the difference is that the
            // session now says so once instead of never.
            let encoded = self.strategy.encodeAudioChunk(pcmData)
            ws.send(encoded) { [weak self] error in
                guard let error else { return }
                // Classified here, on URLSession's queue, so only two Sendable
                // values cross to the main actor and the error's `userInfo` —
                // which holds the failing URL, and therefore the licence key —
                // is never carried anywhere.
                let nsError = error as NSError
                let domain = nsError.domain
                let code = nsError.code
                Task { @MainActor [weak self] in
                    self?.noteAudioSendFailure(generation: generation, domain: domain, code: code)
                }
            }
        }

        try await capture.start()
        audioStartedAt = Date()

        // DID A TERMINAL PROVIDER ERROR LAND WHILE capture.start() WAS AWAY?
        //
        // `StreamingAudioCapture` is not main-actor isolated, so awaiting its
        // `start()` hops off this actor and back — a real suspension, and the
        // last one before this function claims success. A terminal error frame
        // arriving in that window is routed to `pendingStartupFailure` (the flag
        // is still up) rather than to `onError`, and nothing else will ever read
        // it: `waitForSessionStarted()` has already returned. Without this check
        // startSession() would return normally on a session whose socket has
        // been torn down — the flow would show a recording in progress that can
        // never produce a transcript.
        if let startupFailure = pendingStartupFailure {
            logger.error("Terminal provider error during audio start — failing startup")

            // Already true on the only path that sets pendingStartupFailure;
            // restated so this exit can never leave a reconnect armed.
            didInitiateClose = true
            teardownSession(cancelReceiveTask: true, cancelReconnect: true)

            // The teardown above ran while this engine was still coming up on
            // another thread, so it released `audioCapture` before there was
            // anything to stop. `capture` is the live handle — stopping it is
            // what actually gives the microphone back. Safe twice over: stop()
            // is a documented no-op once the engine is gone.
            capture.onAudioData = nil
            capture.stop()

            // No onError here either: this throws, and the flow's catch is what
            // reports and unwinds a failed start.
            throw startupFailure
        }

        isConnected = true
        isStreaming = true
        onConnectionStateChange?(.streaming)
        logger.info("Streaming session started successfully")
        noteStage("streaming")

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
        didInitiateClose = true

        // RAISED BEFORE A SINGLE STOP STEP RUNS, on purpose. From here on a
        // provider completion is the end of the session rather than a turn
        // boundary, and the stop sequence below is precisely what waits for one
        // (`.waitForSessionComplete`). Set it any later and the completion the
        // flush asks for could arrive while the gate still reads "mid-session",
        // and the wait would burn its whole budget on an answer it had already
        // been given.
        stopRequested = true

        // Published before the flush starts, so an error raised DURING the stop
        // is distinguishable from one raised mid-session. A provider that
        // rejects the final commit for holding too little audio is complaining
        // about this step and not about the recording, and the stage is what
        // says which of the two a report is looking at.
        noteStage("stopping")

        // STEP 0: Abort an in-flight reconnect.
        //
        // Cancelling `receiveTask` alone is not enough: once
        // handleUnexpectedDisconnect() has handed that slot to its replacement
        // listener, the task actually performing the reconnect is only reachable
        // through `reconnectOwnerTask`. Without this, the reconnect survives the
        // stop, polls a nil sessionId for its full 10s timeout and then reports
        // "Connection lost and reconnect failed" — a toast and a Sentry event
        // ~10 seconds after a clean stop (HYPERWHISPER-MG). Cancelling here is
        // also what makes `Task.isCancelled` in that reconnect's catch honestly
        // mean "teardown cancelled us".
        reconnectOwnerTask?.cancel()
        reconnectOwnerTask = nil

        // STEP 1: Stop audio capture first to prevent sending audio during shutdown
        teardownAudioCapture()

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
                        // The provider never got the instruction to flush, so
                        // whatever it is still holding is lost. Same outcome as
                        // before — this only stops it happening in silence.
                        reportStopFailure(error, step: "send_stop_message")
                    }
                case .wait(let seconds):
                    try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
                case .waitForSessionComplete(let seconds):
                    do {
                        try await waitForSessionComplete(timeout: seconds)
                    } catch {
                        logger.warning("Timed out waiting for session completion: \(error.localizedDescription, privacy: .public)")
                        // The provider was asked to flush and never confirmed
                        // it. `streaming_finals_delivered` is what then says
                        // whether the user lost the tail of a transcript or the
                        // whole thing.
                        reportStopFailure(error, step: "wait_for_session_complete")
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
            // A force-close is the flush being abandoned. It is the loudest
            // thing that can happen to a user's last sentence and it reported
            // nothing.
            reportStopFailure(StreamingError.connectionTimeout, step: "stop_sequence_timeout")
            webSocketTask?.cancel(with: .abnormalClosure, reason: nil)
        }

        try? await stopTask.value
        timeoutTask.cancel()

        // STEP 3: Clean up. A reconnect started while the stop sequence was
        // running would have re-armed reconnectOwnerTask, so cancel it again.
        reconnectOwnerTask?.cancel()
        reconnectOwnerTask = nil
        teardownWebSocket(closeCode: .normalClosure)
        receiveTask?.cancel()
        receiveTask = nil
        currentConfig = nil

        // Break URLSession → delegate retain cycle
        teardownURLSession()

        isConnected = false
        sessionId = nil
        onConnectionStateChange?(.idle)
        logger.info("Streaming session stopped")
        noteStage("stopped")
    }

    // MARK: - Diagnostics

    /// Whole milliseconds between two instants, for a log field.
    private static func elapsedMs(since start: Date, to end: Date = Date()) -> Int {
        Int((end.timeIntervalSince(start) * 1000).rounded())
    }

    /// The shape of the current streaming session, as metadata only.
    ///
    /// Attached to every capture this file makes, so a provider complaint, a
    /// disconnect and a failed flush all answer the same questions: how long had
    /// the session been up, how long had the microphone been open, had anything
    /// been delivered to the user yet, which connection generation is this, and
    /// what had already gone wrong quietly.
    ///
    /// PRIVACY: provider label, model name and stage are constants of the code
    /// or of the model catalog; every other value is a count, a duration in ms,
    /// a boolean, a close code, or the provider's own request id. Nothing here
    /// is derived from what the user said, typed or pasted.
    private func sessionDiagnostics() -> [String: Any] {
        let now = Date()
        return [
            "streaming_provider": strategy.transcriptionProviderLabel,
            // Double-unwrapped on purpose: `currentConfig` is optional AND its
            // `model` is optional, so a single `??` leaves an `Optional<String>`
            // boxed into `Any` and the field ships as null on every session that
            // uses the provider's default model — which is most of them.
            "streaming_model": (currentConfig?.model ?? nil) ?? "default",
            "streaming_stage": stage,
            "streaming_session_elapsed_ms": Self.elapsedMs(since: sessionStartedAt, to: now),
            "streaming_connection_elapsed_ms": Self.elapsedMs(since: connectionEstablishedAt, to: now),
            "streaming_audio_elapsed_ms": audioStartedAt.map { Self.elapsedMs(since: $0, to: now) } ?? 0,
            "streaming_finals_delivered": finalsDelivered,
            "streaming_partials_delivered": partialsDelivered,
            "streaming_reconnect_count": reconnectCount,
            "streaming_audio_send_failures": audioSendFailureCount,
            "streaming_close_code": webSocketTask?.closeCode.rawValue ?? 0,
            "streaming_session_complete_received": didReceiveSessionComplete,
            "streaming_did_initiate_close": didInitiateClose,
            "streaming_session_id": sessionId ?? "none"
        ]
    }

    /// Record the stage the session has reached and republish the diagnostics to
    /// the Sentry scope.
    ///
    /// Scope extras, not breadcrumbs, for the reason `stage` documents. They
    /// survive `beforeSend`, so they also reach captures raised OUTSIDE this
    /// class — `RecordingTranscriptionFlow`'s own mirror of the same fault
    /// (HYPERWHISPER-S9) carries them without that file having to know about
    /// any of this.
    ///
    /// THE WHOLE SET IS WRITTEN EVERY TIME. Scope extras are global and
    /// `setExtras` never removes a key, so a partial write would leave an
    /// earlier session's value in place to be read as this one's.
    private func noteStage(_ newStage: String) {
        stage = newStage
        guard AppLogger.isErrorLoggingEnabled else { return }
        SentryService.setExtras(sessionDiagnostics())
    }

    /// Report a failure inside the graceful stop sequence.
    ///
    /// Each of these sites was a `logger.warning` and nothing more, so a session
    /// whose flush never reached the provider — or was never acknowledged —
    /// reached Sentry only as an absent transcript with no event of its own. The
    /// user loses the end of what they said, which is the visible fault; the
    /// `stopStep` tag names which step dropped it.
    ///
    /// The fingerprint is fixed rather than stack-derived, so the three steps
    /// stay one queryable issue split by step instead of three groups that
    /// re-split on the next release.
    ///
    /// `includeRecentLogs: false`: this runs on the main actor while the user
    /// waits for their text, and the log fetch shells out to `log show` and
    /// blocks (HYPERWHISPER-F7). The fields above are what the report needs.
    private func reportStopFailure(_ error: Error, step: String) {
        guard AppLogger.isErrorLoggingEnabled else { return }
        var extras = sessionDiagnostics()
        extras["detail"] = step
        SentryService.capture(
            error: StreamingErrorReportingPolicy.sentrySafeError(error),
            message: "Streaming stop sequence failed",
            extras: extras,
            tags: [
                "component": "StreamingTranscriptionClient",
                "provider": strategy.transcriptionProviderLabel,
                "operation": "stopSequence",
                "stopStep": step
            ],
            fingerprint: ["streaming-stop-sequence-failed", step],
            includeRecentLogs: false
        )
    }

    /// Note an audio chunk the socket refused.
    ///
    /// The send completion handler discarded its error outright, on both the
    /// initial wiring and the reconnected one. A socket that opens and then
    /// refuses every frame is exactly "the session ran, the provider heard
    /// nothing" — the fault behind an empty result and behind a provider
    /// complaining it was given 85 ms of audio — and it produced no line
    /// anywhere.
    ///
    /// ONLY THE FIRST FAILURE OF A SESSION IS REPORTED. The callback fires once
    /// per captured buffer, roughly 48 times a second, so reporting each one
    /// would flood both the unified log and Sentry from a per-frame path. The
    /// running count rides along on every later event through
    /// `sessionDiagnostics()`, which is what says whether this was one blip or
    /// the whole session.
    ///
    /// TAKES THE DOMAIN AND CODE, NOT THE ERROR. A `URLError` raised on this
    /// socket carries `NSURLErrorFailingURLStringErrorKey` in its `userInfo` —
    /// the whole `wss://` URL, whose query string is the licence key
    /// (`HyperWhisperCloudStrategy.buildWebSocketURL`). Classifying at the call
    /// site and rebuilding a bare `NSError` here means the credential has no
    /// route into an event at all, and the two values that identify the fault
    /// are kept.
    ///
    /// - Parameter generation: the session the failing chunk belonged to,
    ///   captured by the callback when it was wired. A completion handler that
    ///   lands after the next session has started is dropped rather than
    ///   attributed to a session that never had the fault.
    private func noteAudioSendFailure(generation: Int, domain: String, code: Int) {
        // A CANCELLED SEND IS THE TEARDOWN, NOT A FAULT.
        //
        // `teardownSession` cancels the socket and invalidates the URLSession,
        // which fails every send still queued with `NSURLErrorCancelled`. One
        // 20 ms chunk is almost always in flight when a session ends, so
        // without this every ordinary stop — and every session already reported
        // through a terminal provider error — would raise a second, meaningless
        // event and bury the real ones.
        guard generation == sessionGeneration, !didInitiateClose, code != NSURLErrorCancelled else { return }

        audioSendFailureCount += 1
        guard !didReportAudioSendFailure else { return }
        didReportAudioSendFailure = true

        logger.error(
            "Audio chunk send failed: domain=\(domain, privacy: .public) code=\(code, privacy: .public)"
        )

        guard AppLogger.isErrorLoggingEnabled else { return }
        var extras = sessionDiagnostics()
        extras["error_domain"] = domain
        extras["error_code"] = code
        SentryService.capture(
            error: NSError(domain: domain, code: code, userInfo: nil),
            message: "Streaming audio chunk send failed",
            extras: extras,
            tags: [
                "component": "StreamingTranscriptionClient",
                "provider": strategy.transcriptionProviderLabel,
                "operation": "sendAudioChunk",
                "errorCode": "\(code)"
            ],
            fingerprint: ["streaming-audio-chunk-send-failed", domain, "\(code)"],
            includeRecentLogs: false
        )
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
                // REFUSED UPGRADE (HTTP 402 no credits / 401 / 403):
                // The socket never opened, so nothing here is a disconnect and
                // nothing a reconnect can reach — every retry re-asks the same
                // question and gets the same refusal. The status the server
                // answered with is on the task's response; read it before the
                // generic disconnect handling below, which cannot tell this
                // apart from a dropped connection and used to report it as one.
                if let refusal = terminalUpgradeRefusal(for: task) {
                    handleRefusedUpgrade(refusal, task: task, cancelReceiveTask: false)
                    break
                }

                // A 5xx WebSocket upgrade is a definitive provider-down
                // verdict. Preserve that fact in the error type so the flow can
                // feed it into `/health` without treating a transport failure,
                // a user-account refusal, or a local audio error as an outage.
                if let status = providerUnavailableUpgradeStatus(for: task) {
                    reportDefinitiveProviderFailure(
                        TranscriptionError.serverError(
                            statusCode: status,
                            message: "Streaming WebSocket upgrade failed"
                        )
                    )
                }

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

                // PROTOCOL-LEVEL TERMINAL CLOSE (1002/1003/1007/1008/1009/1011):
                // A provider that closes a doomed session WITHOUT first sending a
                // matching error frame used to fall straight through to the
                // reconnect path below and retry into the same refusal. The
                // forcing case is Gemini 3.5 Transcribe Live, which answers a
                // malformed setup frame with 1007: the frame is identical on
                // every attempt, so every reconnect is guaranteed to reproduce
                // it. 1006 is deliberately NOT in this set — that is the ordinary
                // dropped connection auto-reconnect exists for.
                //
                // Reported, not merely suppressed, for the same reason 4001 is
                // below: setting `didInitiateClose` alone would end the session
                // in silence with the microphone still live and the flow still
                // believing it was recording. `handleTerminalCondition` is
                // idempotent, so when a provider sends BOTH an error frame and
                // this close — the HyperWhisper Cloud route does, closing 1011
                // after a terminal upstream fault — whichever arrives first wins
                // and the message the user reads is that frame's own wording.
                //
                // A 1011 can carry a provider-specific non-outage reason. The
                // policy combines code, provider and close reason before it can
                // change `/health`. Other protocol/input cases stay generic.
                if StreamingProviderErrorPolicy.isTerminalCloseCode(rawCloseCode) {
                    let closeReason = task.closeReason.flatMap { String(data: $0, encoding: .utf8) }
                    if StreamingProviderErrorPolicy.isProviderUnavailableClose(
                        code: rawCloseCode,
                        reason: closeReason,
                        provider: streamingProvider
                    ) {
                        reportDefinitiveProviderFailure(
                            TranscriptionError.providerNotAvailable(
                                provider: strategy.transcriptionProviderLabel,
                                reason: "Streaming service closed with 1011 Internal Error"
                            )
                        )
                    }
                    handleTerminalCondition(
                        .serverError("The transcription service ended the session (code \(rawCloseCode))"),
                        detail: "terminal close code \(rawCloseCode)",
                        cancelReceiveTask: false
                    )
                    break
                }

                // 4001 IS AN OUT-OF-CREDITS STOP, AND HAS TO SAY SO.
                // Suppressing the reconnect above is only half of it: the branch
                // as it stood then fell through to `break` and the session ended
                // in silence, with the microphone still live and the flow still
                // believing it was recording. Report it exactly as a terminal
                // provider error, so this close and the `Credit balance
                // exhausted` frame that today's server sends alongside it land
                // the user in the same place. `handleTerminalCondition` is
                // idempotent, so whichever of the two arrives first wins and the
                // other is dropped.
                if rawCloseCode == 4001 {
                    handleTerminalCondition(
                        .insufficientCredits,
                        detail: "close code 4001",
                        cancelReceiveTask: false
                    )
                    break
                }

                if !didInitiateClose {
                    // UNEXPECTED DISCONNECT — attempt auto-reconnect
                    //
                    // The report used to be the error's own sentence and four
                    // tags, which is why HYPERWHISPER-MH cannot say why any of
                    // its sockets dropped. The two facts that separate the
                    // causes are the close code the peer sent and the transport
                    // error's own domain/code — offline (-1009), connection lost
                    // (-1005) and timed out (-1001) are three different bugs
                    // wearing one title. Both are read here rather than one
                    // frame up, where `teardownSession` has already nil'd the
                    // task that holds them.
                    let nsError = error as NSError
                    let errorDomain = nsError.domain
                    let errorCode = nsError.code
                    await MainActor.run {
                        self.logger.error(
                            "WebSocket receive error: \(error.localizedDescription, privacy: .public) domain=\(errorDomain, privacy: .public) code=\(errorCode, privacy: .public) closeCode=\(rawCloseCode, privacy: .public)"
                        )
                    }
                    noteStage("disconnected")
                    if AppLogger.isErrorLoggingEnabled {
                        var extras = sessionDiagnostics()
                        extras["error_domain"] = errorDomain
                        extras["error_code"] = errorCode
                        SentryService.capture(
                            // The transport error rebuilt from its domain and
                            // code. A `URLError` on this socket carries the
                            // failing URL — and therefore the licence key — in
                            // its `userInfo`, which `beforeSend` does not look
                            // at. The domain and the code are the two values
                            // this report actually needs, and they are in the
                            // extras below as well.
                            error: StreamingErrorReportingPolicy.sentrySafeError(error),
                            message: "WebSocket unexpected disconnect",
                            extras: extras,
                            tags: [
                                "component": "StreamingTranscriptionClient",
                                "provider": strategy.transcriptionProviderLabel,
                                "operation": "receiveLoop",
                                "reconnectCount": "\(reconnectCount)",
                                "closeCode": "\(rawCloseCode)",
                                "errorCode": "\(errorCode)"
                            ]
                        )
                    }
                    await handleUnexpectedDisconnect()
                }
                break
            }
        }
    }

    /// The refusal a failed WebSocket task's HTTP response names, if the user
    /// has to act on it.
    ///
    /// `URLSessionWebSocketTask` keeps the response that came back in place of
    /// the `101 Switching Protocols` — the only record of *why* the socket never
    /// opened, since the receive error itself is a generic transport failure.
    /// A task that did upgrade has a 101 here and returns `nil` through
    /// `upgradeRefusal(forStatus:)`, so this stays quiet for every mid-session
    /// drop.
    private func terminalUpgradeRefusal(
        for task: URLSessionWebSocketTask
    ) -> StreamingProviderErrorPolicy.UpgradeRefusal? {
        guard let response = task.response as? HTTPURLResponse else { return nil }
        return StreamingProviderErrorPolicy.upgradeRefusal(forStatus: response.statusCode)
    }

    /// A failed WebSocket upgrade status that definitively says the provider,
    /// not the user's account or local network, failed the request.
    private func providerUnavailableUpgradeStatus(
        for task: URLSessionWebSocketTask
    ) -> Int? {
        guard let response = task.response as? HTTPURLResponse,
              StreamingProviderErrorPolicy.isProviderUnavailableUpgradeStatus(response.statusCode) else { return nil }
        return response.statusCode
    }

    /// Emit a health verdict for a concrete provider failure while leaving
    /// retry and teardown behavior unchanged. Repeated 5xx responses are fresh
    /// evidence and deliberately refresh the override window.
    private func reportDefinitiveProviderFailure(_ error: Error) {
        // A later useful event from a recovered stream must be able to clear
        // this failure, even if the session had produced text before it failed.
        didReportProviderSuccess = false
        onDefinitiveProviderFailure?(error)
    }

    private func reportProviderSuccess() {
        guard !didReportProviderSuccess else { return }
        didReportProviderSuccess = true
        onProviderSuccess?()
    }

    /// The specific upgrade failure the current socket's response names, or
    /// `error` unchanged.
    ///
    /// A startup failure on a socket the server refused outright *is* that
    /// refusal, whatever shape the failure happened to arrive in — a send that
    /// could not reach a dead socket, a 10-second poll that timed out waiting
    /// for a session that was never going to start. Both name the wrong cause;
    /// the response still carries the right one.
    ///
    /// A `CancellationError` is never shadowed: it is a user re-press, and
    /// `RecordingTranscriptionFlow` has a quiet path for it. Reporting one as a
    /// billing problem would put a toast on a screen the user just left.
    ///
    /// A `pendingStartupFailure` wins outright, because it is the more specific
    /// answer to the same question — either the provider's own wording for a
    /// terminal frame, or an upgrade failure the receive loop classified first.
    /// That second case is why this is returned rather than deferred to: the send
    /// and the receive both fail on a refused socket with no guaranteed order,
    /// and whichever loses would otherwise rethrow its own generic transport
    /// error over an answer already sitting in hand.
    ///
    /// Reporting goes through `handleRefusedUpgrade` rather than being done
    /// here, so a refusal is reported the same way — one Sentry capture, one
    /// teardown — whichever door it came through. That also *claims* it, so the
    /// loser of the race is dropped by `handleTerminalCondition`'s idempotence
    /// guard instead of firing `onError` behind a `startSession()` that has
    /// already thrown: one fault, one toast, either way round.
    private func refusalDuringStartup(shadowing error: Error) -> Error {
        if error is CancellationError { return error }
        if let failure = pendingStartupFailure { return failure }
        guard let task = webSocketTask else { return error }

        if let status = providerUnavailableUpgradeStatus(for: task) {
            reportDefinitiveProviderFailure(
                TranscriptionError.serverError(
                    statusCode: status,
                    message: "Streaming WebSocket upgrade failed"
                )
            )
            return error
        }

        guard let refusal = terminalUpgradeRefusal(for: task) else { return error }

        // The caller's own task, not the receive loop — so that loop is a
        // different task and cancelling it is what stops it reaching the
        // generic disconnect handling on a socket already accounted for.
        return handleRefusedUpgrade(refusal, task: task, cancelReceiveTask: true)
    }

    /// Report a refused upgrade, end the session, and hand back the error that
    /// describes it.
    ///
    /// - Parameter cancelReceiveTask: `false` when the caller is the receive
    ///   loop itself — the loop breaks the moment this returns.
    @discardableResult
    private func handleRefusedUpgrade(
        _ refusal: StreamingProviderErrorPolicy.UpgradeRefusal,
        task: URLSessionWebSocketTask,
        cancelReceiveTask: Bool
    ) -> StreamingError {
        let status = (task.response as? HTTPURLResponse)?.statusCode ?? 0
        let error: StreamingError = refusal == .insufficientCredits
            ? .insufficientCredits
            : .unauthorized(statusCode: status)
        handleTerminalCondition(error, detail: "HTTP \(status)", cancelReceiveTask: cancelReceiveTask)
        return error
    }

    /// Surface a terminal condition that arrived outside the provider's event
    /// stream — a refused upgrade or a 4001 close — with the same guarantees the
    /// terminal branch of `processServerMessage` gives.
    ///
    /// Those guarantees, in the order they have to happen:
    /// 1. Claim the close as expected, so `receiveLoop` neither reports an
    ///    unexpected disconnect nor starts a reconnect that cannot succeed.
    /// 2. Release the microphone and the socket *before* reporting, because
    ///    `StreamingClientProtocol.onError` promises the caller the session is
    ///    already torn down and `RecordingTranscriptionFlow` takes it at its
    ///    word — it never calls `stopSession()`.
    /// 3. Fail an in-flight `startSession()` through `pendingStartupFailure`
    ///    rather than through `onError`, so one fault produces one toast. The
    ///    refused-upgrade case is nearly always this one: the server answers
    ///    before the socket is up, so startup is still parked in
    ///    `waitForSessionStarted()` and would otherwise sit out its full 10s and
    ///    fail with a `connectionTimeout` naming the wrong problem.
    ///
    /// - Parameters:
    ///   - error: The condition, already carrying the sentence the user reads.
    ///   - detail: How it was detected, for the log and the Sentry payload.
    ///   - cancelReceiveTask: `false` when the caller runs inside the receive
    ///     task. See `teardownSession(cancelReceiveTask:cancelReconnect:)`.
    private func handleTerminalCondition(
        _ error: StreamingError,
        detail: String,
        cancelReceiveTask: Bool
    ) {
        // A terminal condition can be signalled twice for one fault — the
        // provider's error frame and the close that follows it both mean "no
        // credits". The first one already tore the session down and told the
        // user; a second report would only overwrite that message with a copy
        // of itself and re-run a teardown that has already happened.
        guard !didHandleTerminalProviderError else {
            logger.info("Terminal condition already handled — ignoring \(detail, privacy: .public)")
            return
        }
        didInitiateClose = true
        didHandleTerminalProviderError = true
        lastError = error

        let description = error.localizedDescription
        logger.error("Terminal streaming condition (\(detail, privacy: .public)): \(description, privacy: .public)")
        noteStage("terminal")
        if AppLogger.isErrorLoggingEnabled {
            var extras = sessionDiagnostics()
            extras["detail"] = detail
            SentryService.capture(
                error: error,
                message: "Streaming session refused (terminal)",
                extras: extras,
                tags: [
                    "component": "StreamingTranscriptionClient",
                    "provider": strategy.transcriptionProviderLabel,
                    "operation": "handleTerminalCondition",
                    // Same tag the provider-error path sets, so account-state noise
                    // can be filtered server-side in one rule rather than two.
                    "terminal": "true"
                ]
            )
        }

        teardownSession(cancelReceiveTask: cancelReceiveTask, cancelReconnect: true)
        onConnectionStateChange?(.error(description))

        if isStartingSession {
            pendingStartupFailure = error
            return
        }

        onError?(error)
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
    ///
    /// Internal rather than private only so `StreamingTurnBoundaryTests` can
    /// drive it with a stub strategy. The turn-boundary rule below is a decision
    /// this client makes about a provider frame, and there is no other seam that
    /// reaches it without a live socket.
    func processServerMessage(_ jsonString: String) async {
        guard let event = strategy.parseMessage(jsonString) else {
            logger.debug("Unhandled message from provider")
            return
        }

        if StreamingProviderErrorPolicy.isUsefulProviderSuccessEvent(event) {
            reportProviderSuccess()
        }

        switch event {
        case .sessionStarted(let id):
            await MainActor.run {
                self.sessionId = id ?? "direct"
                self.logger.info("Session started: \(self.sessionId ?? "unknown", privacy: .public)")
            }

        case .finalTranscript(let text):
            await MainActor.run {
                // The COUNT of committed segments, never one of them. This is
                // what tells a later report whether the user had been given
                // anything at all before the session broke.
                self.finalsDelivered += 1
                self.onTranscriptUpdate?(text, true)
            }

        // NOT gated by `completeEndsSessionBeforeStop`, deliberately, and the
        // same way round as Windows. This arm exists for providers whose final
        // flush and completion arrive in ONE frame, which is a shape only a
        // post-stop flush produces; Gemini — the only provider that answers
        // `false` — reaches it nowhere else. Gating it would also mean deciding
        // what to do with the text half of a frame whose completion half was
        // suppressed, and the answer to that is "the same as `.finalTranscript`",
        // which is what a provider emitting a mid-session boundary sends anyway.
        case .finalTranscriptAndSessionComplete(let text, let duration, let credits):
            await MainActor.run {
                self.finalsDelivered += 1
                self.onTranscriptUpdate?(text, true)
                self.didReceiveSessionComplete = true
                self.logger.info("Session complete: \(duration, privacy: .public)s, \(credits, privacy: .public) credits")
                self.onSessionComplete?(duration, credits)
            }

        case .partialTranscript(let text):
            await MainActor.run {
                self.partialsDelivered += 1
                self.onTranscriptUpdate?(text, false)
            }

        case .sessionComplete(let duration, let credits):
            // A TURN BOUNDARY IS NOT THE END OF THE SESSION.
            //
            // For five of the six remote providers this frame arrives once, at
            // the end, and `completeEndsSessionBeforeStop` (default true) keeps
            // the unconditional behaviour this arm always had. Gemini is the
            // exception: it emits `serverContent.generationComplete` every time
            // it finishes generating for an utterance, so a two-sentence
            // dictation sees one mid-stream with more audio still to come.
            // Latching `didReceiveSessionComplete` there releases the stop
            // sequence's `waitForSessionComplete` at the first pause and the
            // last utterance's final never arrives.
            //
            // Nothing needs flushing on a turn boundary: the turn's own text
            // already arrived as its own `.finalTranscript` beforehand, and the
            // current partial is deliberately left alone so a preview that was
            // never committed survives into the next turn.
            //
            // The decision lives here rather than in the strategy because
            // "has the user asked to stop yet?" is the client's state and only
            // the client's. Same split as Windows
            // (`StreamingTranscriptionClient.cs:648-678`) and the backend proxy
            // (`ws-streaming-shared.ts`, `complete` arm).
            if !strategy.completeEndsSessionBeforeStop && !stopRequested {
                logger.debug(
                    "Turn boundary from \(self.strategy.transcriptionProviderLabel, privacy: .public), session continues"
                )
                return
            }

            await MainActor.run {
                self.didReceiveSessionComplete = true
                self.logger.info("Session complete: \(duration, privacy: .public)s, \(credits, privacy: .public) credits")
                self.onSessionComplete?(duration, credits)
            }

        case .error(let message):
            // Classified out here so the flags below can be the FIRST statements
            // inside the main-actor block, ahead of every callback.
            let outcome = StreamingProviderErrorPolicy.outcome(forProviderMessage: message)
            let isTerminal = outcome == .terminal
            let terminalTag = isTerminal ? "true" : "false"

            // A terminal provider error (no credits, dead key) is followed by
            // the provider closing the socket itself, and providers commonly
            // repeat the error frame on the way down. The first frame already
            // tore the session down and told the user, so a repeat must not
            // re-report to the user — but it still gets its own capture, tagged
            // as a repeat: the second frame is not always the same fault, and
            // dropping it silently deletes the only record of a distinct,
            // actionable fault.
            if didHandleTerminalProviderError {
                let repeatError = StreamingError.serverError(message)
                lastError = repeatError
                logger.error(
                    "Repeat provider error after a terminal one: \(message, privacy: .public) terminal=\(terminalTag, privacy: .public)"
                )
                if AppLogger.isErrorLoggingEnabled {
                    var extras = sessionDiagnostics()
                    extras["serverMessage"] = message
                    SentryService.capture(
                        error: repeatError,
                        message: "WebSocket provider error (repeat after terminal)",
                        extras: extras,
                        tags: [
                            "component": "StreamingTranscriptionClient",
                            "provider": strategy.transcriptionProviderLabel,
                            "operation": "processServerMessage",
                            "terminal": terminalTag,
                            "repeatAfterTerminal": "true"
                        ]
                    )
                }
                return
            }

            await MainActor.run {
                let error = StreamingError.serverError(message)
                // Is startSession() still in flight — at any point between
                // opening the socket and reporting success? If so this error,
                // not a 10-second timeout and not a silent teardown under a
                // successful start, is what should fail it.
                let failsStartup = isTerminal && self.isStartingSession

                if isTerminal {
                    // Claim the provider's impending close as expected. Without
                    // this, receiveLoop sees didInitiateClose == false, reports
                    // "WebSocket unexpected disconnect" (HYPERWHISPER-MH) and
                    // starts a reconnect that can only fail the same way
                    // (HYPERWHISPER-MG) — whose generic "reconnect failed" toast
                    // then overwrites the actionable message this error is about
                    // to show (HYPERWHISPER-RW). The user still gets that
                    // message, either through onError below or through the
                    // startup failure handed to startSession().
                    self.didInitiateClose = true
                    self.didHandleTerminalProviderError = true
                }

                self.logger.error("Provider error: \(message, privacy: .public) terminal=\(terminalTag, privacy: .public)")
                self.lastError = error

                // THE FAULT THIS WHOLE CHANGE SERVES.
                //
                // The report carried the provider's sentence and nothing about
                // our side of it, so "buffer only has 85.00ms of audio"
                // (HYPERWHISPER-S8) named a client-side condition that the
                // client had recorded nowhere. `streaming_stage` says whether
                // this arrived mid-session or during the stop flush,
                // `streaming_audio_elapsed_ms` says how long the microphone had
                // actually been open, and `streaming_audio_send_failures` says
                // whether the audio was ever accepted.
                self.noteStage("provider_error")
                if AppLogger.isErrorLoggingEnabled {
                    var extras = self.sessionDiagnostics()
                    extras["serverMessage"] = message
                    SentryService.capture(
                        error: error,
                        message: "WebSocket provider error",
                        extras: extras,
                        tags: [
                            "component": "StreamingTranscriptionClient",
                            "provider": self.strategy.transcriptionProviderLabel,
                            "operation": "processServerMessage",
                            // Lets account-state noise (no credits, dead key) be
                            // filtered server-side without shipping a release, while
                            // the capture itself stays — a terminal error is still
                            // worth seeing, just not worth alerting on.
                            "terminal": terminalTag
                        ]
                    )
                }

                if isTerminal {
                    // RELEASE THE MICROPHONE AND THE SOCKET BEFORE REPORTING.
                    //
                    // StreamingClientProtocol.onError promises "the session is
                    // already torn down", and RecordingTranscriptionFlow takes it
                    // at its word: its handler only drops its reference to this
                    // client, it never calls stopSession(). Nothing else will —
                    // the client has no deinit and its own URLSession retains it
                    // as the delegate. Before didInitiateClose was set here the
                    // doomed reconnect happened to stop the audio engine on its
                    // way out; suppressing that reconnect removed the only thing
                    // that turned the microphone off, leaving the AVAudioEngine
                    // tap installed (orange mic indicator lit until quit, a new
                    // engine stacked on every later recording).
                    //
                    // Runs on the receive task, so its own handle must not be
                    // cancelled here — the loop breaks as soon as we return.
                    self.teardownSession(cancelReceiveTask: false, cancelReconnect: true)
                }

                self.onConnectionStateChange?(.error(message))

                if failsStartup {
                    // startSession() is still in flight. Hand it this error so it
                    // fails now, carrying the provider's own wording, instead of
                    // timing out after 10s and reporting a generic "Failed to
                    // start" over the top of it — or, later in startup, instead
                    // of reporting a success it no longer has.
                    //
                    // Both of its remaining suspension points read this:
                    // waitForSessionStarted() polls it, and startSession() checks
                    // it again after capture.start().
                    //
                    // No onError on this path: startSession() throws, and the
                    // flow's catch is what reports and unwinds a failed start.
                    // Firing both would report one fault in two toasts, with the
                    // less useful one landing last.
                    self.pendingStartupFailure = error
                    self.logger.error("Terminal provider error before session start — failing startup")
                    return
                }

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
    ///
    /// EARLY TERMINAL FAILURE:
    /// The same poll also watches `pendingStartupFailure`. A provider that
    /// answers a dead key or an exhausted balance before it ever sends a
    /// session-started frame would otherwise leave startup sitting here for the
    /// full timeout and then fail with a generic `connectionTimeout`, whose
    /// "Failed to start" toast buries the actionable message the provider
    /// actually sent (HYPERWHISPER-RW).
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
                while true {
                    if let failure = await MainActor.run(body: { self?.pendingStartupFailure }) {
                        throw failure
                    }
                    if await MainActor.run(body: { self?.sessionId }) != nil {
                        return
                    }
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
    /// 6. If failed: tear the session down, enter .error state — unless
    ///    stopSession()/cancel() deliberately aborted this reconnect, in which
    ///    case it tears down just as thoroughly but stays silent, because that
    ///    caller is already mid-teardown and driving its own UI
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

        // CLAIM OWNERSHIP OF THIS RECONNECT.
        //
        // This function only ever runs inside the receive task, so `receiveTask`
        // is the task executing this very code — right up until the hand-over
        // below points that slot at the replacement listener. Recording the
        // owner separately is what lets stopSession()/cancel() cancel the task
        // actually performing the reconnect instead of its replacement.
        reconnectOwnerTask = receiveTask
        defer { reconnectOwnerTask = nil }

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
            noteStage("reconnect_exhausted")
            if AppLogger.isErrorLoggingEnabled {
                var extras = sessionDiagnostics()
                extras["reconnectCount"] = "\(reconnectCount)"
                SentryService.capture(
                    error: StreamingError.serverError("Reconnect cycle limit reached"),
                    message: "WebSocket reconnect cycle exhausted",
                    extras: extras,
                    tags: [
                        "component": "StreamingTranscriptionClient",
                        "provider": strategy.transcriptionProviderLabel,
                        "operation": "handleUnexpectedDisconnect"
                    ]
                )
            }
            didInitiateClose = true
            // Nothing has been handed over yet, so `receiveTask` is still the
            // task running this code and `reconnectOwnerTask` is the same task
            // again — cancelling either would cancel ourselves. Both loops end
            // the moment we return.
            teardownSession(cancelReceiveTask: false, cancelReconnect: false)
            onConnectionStateChange?(.error("Connection lost after multiple retries"))
            onError?(StreamingError.serverError("Connection lost after multiple retries"))
            return
        }

        await MainActor.run {
            self.isConnected = false
            self.onConnectionStateChange?(.reconnecting)
        }
        noteStage("reconnecting")

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

        // DID SOMEBODY DELIBERATELY STOP US WHILE WE SLEPT?
        //
        // `didInitiateClose`, not the nil-ness of `currentConfig`, is the honest
        // test. stopSession() sets the flag FIRST and only nils the config after
        // its stop sequence, which can take up to ~10s; the sleep above is 1s,
        // so a config-only guard wakes inside that window, sails through and
        // opens a fresh billable socket in the middle of a teardown.
        //
        // Quiet is correct here and only here: the caller that set the flag
        // (stopSession() via stopStreamingTranscription, or cancel()) asked for
        // this and settles the UI to .idle itself. Firing .error would show a
        // connection failure the user never hit, and RecordingTranscriptionFlow's
        // onError handler would tear the session state down underneath the stop
        // that is still running. A drop the user did NOT ask for stays loud —
        // that is the split the catch below re-applies (HYPERWHISPER-MG).
        //
        // Resources are released either way: a "quiet" path that left the audio
        // engine running would be a far worse bug than a stray toast.
        if didInitiateClose {
            logger.info("Reconnect abandoned (cancelled-by-teardown): the session was stopped during the reconnect wait")
            teardownSession(cancelReceiveTask: false, cancelReconnect: false)
            return
        }

        // A live session whose config or URL is missing is a genuine failure,
        // and stays exactly as loud as it has always been.
        guard let config = currentConfig,
              let url = strategy.buildWebSocketURL(config: config) else {
            logger.error("Reconnect failed: no usable WebSocket URL for the saved config")
            // This one reports through the flow's own `onError` capture rather
            // than raising a second event here; the stage is what tells that
            // event which of the reconnect's exits it came from.
            noteStage("reconnect_no_url")
            teardownSession(cancelReceiveTask: false, cancelReconnect: false)
            onConnectionStateChange?(.error("Connection lost"))
            onError?(StreamingError.serverError("Connection lost and reconnect failed"))
            return
        }

        // Detach the audio callback before assigning the new WebSocket task.
        // The capture engine keeps running during reconnect, and the callback
        // re-reads self.webSocketTask on every invocation — without this, audio
        // chunks land on the new socket before startMessages configure the
        // session (OpenAI Realtime rejects appends sent before session.update).
        // The callback is re-wired below once the session is re-established.
        audioCapture?.onAudioData = nil

        // Rebuild WebSocket connection. Release the dead socket first — every
        // other place in this file that replaces or drops webSocketTask cancels
        // it, and dropping the last reference without cancelling leaks a
        // URLSessionWebSocketTask that URLSession still holds.
        teardownWebSocket()
        webSocketTask = makeWebSocketTask(url: url, config: config)
        webSocketTask?.resume()

        // Reset session ID so waitForSessionStarted can detect the new ready message
        sessionId = nil
        disconnectedDuringReconnect = false
        // The fresh socket is a fresh session, so its first turn boundary is not
        // terminal either. Belt-and-braces today — the `didInitiateClose` guard
        // above abandons any reconnect that raced a stop, and `stopRequested` is
        // only ever raised together with that flag — but the two are set for
        // different reasons and the guard is not this line's to depend on.
        stopRequested = false

        // HAND THE receiveTask SLOT OVER TO THE REPLACEMENT LOOP — DO NOT CANCEL
        // THE OLD ONE.
        //
        // This function has exactly one caller (receiveLoop), so it always runs
        // *inside* receiveTask — self.receiveTask is the task executing this very
        // code. Cancelling it here cancelled the reconnect itself: every await
        // below then ran cancelled, and waitForSessionStarted's task group spawns
        // children that are born already-cancelled, so both throw immediately and
        // group.next() rethrows. The reconnect could only ever fail with
        // CancellationError, well before the 10s timeout (HYPERWHISPER-MG).
        //
        // There is also nothing to cancel: the old loop is not blocked on
        // receive(), it is parked in this call and breaks the moment we return,
        // so it can never race the replacement loop for the new socket.
        //
        // From here on `receiveTask` is the REPLACEMENT listener, not this task.
        // The handle that still points at the reconnect is `reconnectOwnerTask`,
        // claimed at the top of this function — cancelling `receiveTask` alone
        // from stopSession() would orphan the reconnect, which is exactly the
        // ~10s-late "reconnect failed" report this file used to produce.
        receiveTask = nil
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
            let generation = sessionGeneration
            audioCapture?.onAudioData = { [weak self] pcmData in
                guard let self = self, let ws = reconnectedSocket else { return }
                self.strategy.onAudioSendOpportunity { msg in
                    ws.send(msg) { _ in }
                }
                let encoded = self.strategy.encodeAudioChunk(pcmData)
                // Same recording as the initial wiring — a socket that comes
                // back and then refuses every chunk is the worst version of
                // this fault, because the UI says "streaming" throughout.
                ws.send(encoded) { [weak self] error in
                    guard let error else { return }
                    let nsError = error as NSError
                    let domain = nsError.domain
                    let code = nsError.code
                    Task { @MainActor [weak self] in
                        self?.noteAudioSendFailure(generation: generation, domain: domain, code: code)
                    }
                }
            }

            await MainActor.run {
                self.isConnected = true
                self.onConnectionStateChange?(.streaming)
                self.logger.info("Reconnect succeeded")
                self.noteStage("streaming")
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
            // SAMPLE Task.isCancelled FIRST — it is task-local, so it answers
            // for whichever task is running *right now*, and any suspension
            // point below would be asking the wrong question about the wrong
            // task. TranscriptionCancellationPolicy's doc comment demands this.
            let isTaskCancelled = Task.isCancelled

            // ...and sample didInitiateClose before the line below sets it: it
            // is only true at this point if somebody ELSE — stopSession() or
            // cancel() — deliberately ended this session.
            let wasDeliberatelyStopped = didInitiateClose

            // WAS THIS RECONNECT ABORTED ON PURPOSE, OR DID IT REALLY FAIL?
            //
            // `reconnectOwnerTask` keeps pointing at the task running this
            // function for as long as the reconnect is in flight, even after the
            // receiveTask hand-over above. That is what lets stopSession() — the
            // user releasing the shortcut, or cancel() — genuinely abort a
            // reconnect mid-flight, and therefore what makes `Task.isCancelled`
            // here mean "teardown cancelled us" instead of "somebody cancelled
            // our replacement listener" (HYPERWHISPER-MG).
            //
            // Reuse the existing policy instead of testing `error is
            // CancellationError`: the AND with the task flag is the entire point.
            // A CancellationError raised on a *live* task is still a provider
            // failure (providers and URLSession raise one while tearing their own
            // work down, with no transcript for the user), and treating that as
            // benign is what hid HYPERWHISPER-SQ. That direction is pinned by
            // TranscriptionCancellationPolicyTests
            // .cancellationErrorOnALiveTaskIsStillAProviderFailure.
            let outcome = TranscriptionCancellationPolicy.outcome(
                for: error,
                isTaskCancelled: isTaskCancelled
            )

            // BOTH halves are required to go quiet, and that is the point.
            //
            // A cancelled task alone is not consent: this reconnect only exists
            // because the connection dropped under a user who is still speaking,
            // and the audio engine is deliberately kept warm across it, so
            // whatever they said after the drop is already lost. Staying silent
            // there hands them a truncated transcript with no toast, which is
            // what stopStreamingTranscription's success path then pastes.
            //
            // The one case that genuinely warrants silence is a teardown the
            // caller asked for: it set didInitiateClose, it is already unwinding
            // and it drives its own UI to .idle. Reporting there would show a
            // connection failure the user never hit, and
            // RecordingTranscriptionFlow's onError handler would tear the session
            // state down underneath the stop that is still running (it clears
            // streamingService, resets recordingState and shows an error alert).
            let isDeliberateTeardown = wasDeliberatelyStopped && outcome == .genuineCancellation
            // Computed out here: privacy-aware interpolation can't nest a ternary.
            let outcomeLabel = isDeliberateTeardown ? "cancelled-by-teardown" : "provider-failure"

            // PUBLISH AND SNAPSHOT BEFORE THE TEARDOWN BELOW.
            //
            // `teardownSession` nils `webSocketTask` and `sessionId`, so a set
            // gathered after it reports `streaming_close_code: 0` and
            // `streaming_session_id: "none"` on every reconnect failure — two
            // fields that read as "the socket carried no close code" when the
            // truth is "nobody asked it in time". An empty measurement is worse
            // than an absent one, because it looks like an answer.
            //
            // Both the scope copy and the event's own copy are taken here, from
            // the same state, so the two can never disagree about a key they
            // share.
            noteStage(isDeliberateTeardown ? "reconnect_abandoned" : "reconnect_failed")
            let failureDiagnostics = sessionDiagnostics()

            // Reconnect failed — prevent the leftover replacement listener from
            // triggering another cycle. `receiveTask` is that listener, not this
            // task (the hand-over above), so cancelling it here is correct;
            // `reconnectOwnerTask` IS this task and must not be cancelled.
            didInitiateClose = true
            teardownSession(cancelReceiveTask: true, cancelReconnect: false)

            // Unconditional: whatever we report upwards, the transition stays
            // traceable in the local log.
            logger.error(
                "Reconnect failed (\(outcomeLabel, privacy: .public)): \(error.localizedDescription, privacy: .public)"
            )

            guard !isDeliberateTeardown else { return }

            onConnectionStateChange?(.error("Connection lost"))
            onError?(StreamingError.serverError("Connection lost and reconnect failed"))

            // Same split for Sentry: a reconnect somebody deliberately aborted is
            // not a defect, and reporting one buries the real reconnect failures
            // it is grouped with (HYPERWHISPER-MG).
            if AppLogger.isErrorLoggingEnabled {
                let nsError = error as NSError
                var extras = failureDiagnostics
                extras["reconnectCount"] = "\(reconnectCount)"
                extras["error_domain"] = nsError.domain
                extras["error_code"] = nsError.code
                SentryService.capture(
                    error: StreamingErrorReportingPolicy.sentrySafeError(error),
                    message: "WebSocket reconnect failed",
                    extras: extras,
                    tags: [
                        "component": "StreamingTranscriptionClient",
                        "provider": strategy.transcriptionProviderLabel,
                        "operation": "reconnect",
                        "errorCode": "\(nsError.code)"
                    ]
                )
            }
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

    // MARK: - Teardown

    /// Stop the microphone and settle the audio-facing published state.
    ///
    /// One helper instead of the four hand-copied versions this used to be —
    /// which had already drifted apart (only the retry-limit path reset
    /// `isConnected`). Leaving the AVAudioEngine tap installed is the most
    /// expensive bug in this file: the orange microphone indicator stays lit
    /// until the app quits, and every later recording stacks another engine
    /// on top of the abandoned one.
    private func teardownAudioCapture() {
        audioCapture?.onAudioData = nil
        audioCapture?.stop()
        audioCapture = nil
        isStreaming = false
        isConnected = false
        onAudioLevel?(0)
    }

    /// Close and release the WebSocket.
    ///
    /// - Parameter closeCode: `.normalClosure` for a stop the user asked for,
    ///   `.abnormalClosure` for a session ending on a fault.
    private func teardownWebSocket(closeCode: URLSessionWebSocketTask.CloseCode = .abnormalClosure) {
        webSocketTask?.cancel(with: closeCode, reason: nil)
        webSocketTask = nil
    }

    /// Release the URLSession.
    ///
    /// `URLSession(configuration:delegate:delegateQueue:)` retains its delegate
    /// and this client is its own delegate, so without this the client — and the
    /// audio engine it owns — outlives every other reference to it.
    private func teardownURLSession() {
        urlSession?.invalidateAndCancel()
        urlSession = nil
    }

    /// Release everything this session owns, for every exit that does not run
    /// through `stopSession()`.
    ///
    /// `StreamingClientProtocol.onError` promises callers that "the session is
    /// already torn down", and `RecordingTranscriptionFlow` takes that at its
    /// word — its handler only drops its reference to the client, it never calls
    /// `stopSession()`. So every path that fires `onError` has to come through
    /// here first.
    ///
    /// - Parameters:
    ///   - cancelReceiveTask: `false` when the caller is itself running inside
    ///     the receive task — cancelling there cancels the caller. The receive
    ///     loop ends on its own as soon as the socket is gone.
    ///   - cancelReconnect: `false` when the caller is itself the in-flight
    ///     reconnect, for the same reason.
    private func teardownSession(cancelReceiveTask: Bool, cancelReconnect: Bool) {
        teardownAudioCapture()

        if cancelReceiveTask {
            receiveTask?.cancel()
        }
        receiveTask = nil

        if cancelReconnect {
            reconnectOwnerTask?.cancel()
            reconnectOwnerTask = nil
        }

        teardownWebSocket()
        teardownURLSession()

        currentConfig = nil
        sessionId = nil
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

            // The other door onto the same decision as receiveLoop's catch: a
            // protocol-level terminal close must not start a reconnect cycle
            // that can only reproduce it, and must not end the session in
            // silence either. URLSession gives no ordering guarantee between
            // this delegate and that catch, so both have to check;
            // handleTerminalCondition drops whichever arrives second. Here the
            // receive task IS a different task, so cancelling it is both safe
            // and what actually stops the loop.
            if StreamingProviderErrorPolicy.isTerminalCloseCode(rawCode) {
                if StreamingProviderErrorPolicy.isProviderUnavailableClose(
                    code: rawCode,
                    reason: reasonString,
                    provider: streamingProvider
                ) {
                    reportDefinitiveProviderFailure(
                        TranscriptionError.providerNotAvailable(
                            provider: strategy.transcriptionProviderLabel,
                            reason: "Streaming service closed with 1011 Internal Error"
                        )
                    )
                }
                handleTerminalCondition(
                    .serverError("The transcription service ended the session (code \(rawCode))"),
                    detail: "terminal close code \(rawCode) (delegate)",
                    cancelReceiveTask: true
                )
                return
            }

            // Whichever of this delegate and receiveLoop's catch sees the 4001
            // first reports it; handleTerminalCondition drops the second. Both
            // doors are covered because URLSession gives no ordering guarantee
            // between them — the comment in receiveLoop's catch is the same
            // point from the other side.
            //
            // Unlike that caller, this runs on a task of its own, so the receive
            // task is a different task and cancelling it is both safe and the
            // thing that stops the loop.
            if rawCode == 4001 {
                handleTerminalCondition(
                    .insufficientCredits,
                    detail: "close code 4001 (delegate)",
                    cancelReceiveTask: true
                )
                return
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
    /// The account has no balance left — a 402 on the upgrade, or a 4001 close.
    ///
    /// Its own case rather than a `serverError(String)`: the server's own
    /// wording for this is an HTTP status or a close code, so there is no
    /// message to pass through, and the user needs a localized sentence naming
    /// the fix. Wrapping it as a `serverError` would also read to the user as
    /// "Server error: …" — the one description that is wrong here, because the
    /// server is working exactly as designed.
    case insufficientCredits
    /// The key or network was refused on the upgrade. Keep the response status
    /// because HyperWhisper Cloud uses 403 for a temporary network block.
    case unauthorized(statusCode: Int?)

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
        case .insufficientCredits:
            // The same sentence the batch path shows for a 402
            // (`TranscriptionError.insufficientCredits`). One condition, one
            // wording, whichever path the user hit it on.
            return "transcription.error.insufficientCredits".localized
        case .unauthorized(let statusCode):
            if statusCode == 403 {
                return "transcription.error.forbidden.hyperWhisperCloud".localized
            }
            return "transcription.error.unauthorized.generic".localized
        }
    }

    /// Whether this error names something only the user can fix — no credits, a
    /// dead key, a billing hold.
    ///
    /// Callers use it to decide what to *report*, never what to *show*: the
    /// user sees the same sentence either way.
    ///
    /// The two dedicated cases answer for themselves rather than through
    /// `StreamingProviderErrorPolicy.outcome(forProviderMessage:)`, because
    /// their descriptions are localized — matching the English markers against
    /// a German toast would call an exhausted balance transient and put it back
    /// in Sentry, in exactly the locales nobody checks.
    var isTerminalForUser: Bool {
        switch self {
        case .insufficientCredits, .unauthorized:
            return true
        case .serverError(let message):
            // The provider's own wording, passed through untranslated.
            return StreamingProviderErrorPolicy.outcome(forProviderMessage: message) == .terminal
        case .invalidURL, .connectionTimeout, .audioEngineError:
            return false
        }
    }
}
