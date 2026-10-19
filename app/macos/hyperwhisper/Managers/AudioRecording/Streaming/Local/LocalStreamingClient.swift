//
//  LocalStreamingClient.swift
//  hyperwhisper
//
//  On-device streaming client shared by every local engine (Parakeet,
//  Nemotron). Plays the same role as `StreamingTranscriptionClient` for the
//  cloud path, but the audio goes to a local streaming session instead of a
//  WebSocket.
//
//  The session lifecycle lives here once: warm up the model, build the
//  session, wire its callbacks, start the audio capture, and unwind on each
//  cancellation point. Only the model step differs per engine, and that sits
//  behind `LocalStreamingEngine` (see `LocalParakeetStreamingClient.swift`
//  and `LocalNemotronStreamingClient.swift`).
//

import Foundation
import os

// MARK: - Seams

/// The local streaming session a client drives. `ParakeetStreamingSession`
/// and `NemotronStreamingSession` conform; they wrap different FluidAudio
/// APIs and stay separate types.
protocol LocalStreamingSession: Actor {
    func setCallbacks(
        onConfirmedDelta: (@Sendable (String) -> Void)?,
        onVolatile: (@Sendable (String) -> Void)?,
        onError: (@Sendable (Error) -> Void)?
    ) async

    /// Start the recognizer loop.
    func start() async throws

    /// Thread-safe sample ingest, called from the AVAudioEngine RT tap thread.
    /// Must be non-blocking.
    nonisolated func feed(_ samples: [Float])

    /// Drain gracefully and return the session's final text. What that text
    /// is differs per session type; see `LocalStreamingFinalTextDelivery`.
    func finish() async throws -> String

    /// Abort without a final transcript. Idempotent: a no-op once `finish()`
    /// or `cancel()` ran, and safe before `start()` ran.
    func cancel() async

    /// Wall-clock seconds since `start()`.
    func elapsedSeconds() async -> Double
}

/// How the final text of a session reaches `onTranscriptUpdate(_, true)`.
enum LocalStreamingFinalTextDelivery {
    /// The session does NOT emit its final text through `onConfirmedDelta`;
    /// `finish()` returns it and the client delivers it synchronously, before
    /// the `.idle` / `onSessionComplete` cleanup.
    case finishReturnValue

    /// The session emits its final tail through `onConfirmedDelta` inside
    /// `finish()`, and `finish()` returns the whole accumulated transcript.
    /// Delivering that return value would type the transcript a second time,
    /// so the client drops it.
    case confirmedDeltaCallback
}

/// Whether the engine's model files are on disk.
enum LocalStreamingModelCheck {
    /// `logDetail` follows "warmup phase: " in the log.
    case present(logDetail: String)
    /// `stateMessage` goes to `onConnectionStateChange(.error(_))`, then the
    /// client throws `error`.
    case missing(stateMessage: String, error: Error)
}

/// The engine-specific part of a local streaming client: the label, the
/// on-disk check, the model load, and the session init.
@MainActor
protocol LocalStreamingEngine {
    associatedtype Models
    associatedtype Session: LocalStreamingSession

    static var finalTextDelivery: LocalStreamingFinalTextDelivery { get }

    /// `StreamingClientProtocol.transcriptionProviderLabel`.
    var providerLabel: String { get }
    /// os_log category.
    var logCategory: String { get }
    /// Engine name in log lines, e.g. "Nemotron".
    var logName: String { get }
    /// Model variant/version in the "Starting ..." log line.
    var logVariant: String { get }
    /// What `loadModels()` loads, in log lines ("models", "shared models").
    var modelsLogName: String { get }

    func checkModelsPresent() -> LocalStreamingModelCheck
    func loadModels() async throws -> Models
    func makeSession(models: Models) async throws -> Session
}

/// The audio capture a client drives. `StreamingAudioCapture` in production;
/// a fake in unit tests, so no microphone is needed.
protocol StreamingAudioCapturing: AnyObject {
    var onFloat32Samples: (([Float]) -> Void)? { get set }
    var onAudioLevel: ((Float) -> Void)? { get set }
    func start() async throws
    func stop()
}

extension StreamingAudioCapture: StreamingAudioCapturing {}

// MARK: - Client

@MainActor
final class LocalStreamingClient<Engine: LocalStreamingEngine>: NSObject, ObservableObject, StreamingClientProtocol {

    // MARK: - StreamingClientProtocol

    var onTranscriptUpdate: ((String, Bool) -> Void)?
    var onSessionComplete: ((Double, Double) -> Void)?
    var onError: ((Error) -> Void)?
    var onDefinitiveProviderFailure: ((Error) -> Void)?
    var onProviderSuccess: (() -> Void)?
    var onConnectionStateChange: ((StreamingConnectionState) -> Void)?
    var onAudioLevel: ((Float) -> Void)?

    var transcriptionProviderLabel: String { engine.providerLabel }

    // MARK: - Private

    let engine: Engine
    private let logger: Logger
    private let makeAudioCapture: @MainActor () -> StreamingAudioCapturing

    private var audioCapture: StreamingAudioCapturing?
    private var session: Engine.Session?
    private var isActive = false

    private func elapsedMilliseconds(since start: Date) -> String {
        String(format: "%.0f", Date().timeIntervalSince(start) * 1000)
    }

    // MARK: - Init

    /// `makeAudioCapture` is a test seam; production uses `StreamingAudioCapture`.
    init(
        engine: Engine,
        makeAudioCapture: @escaping @MainActor () -> StreamingAudioCapturing = { StreamingAudioCapture() }
    ) {
        self.engine = engine
        self.logger = Logger(subsystem: "com.hyperwhisper.app", category: engine.logCategory)
        self.makeAudioCapture = makeAudioCapture
        super.init()
    }

    // MARK: - Session lifecycle

    func startSession(config: StreamingSessionConfig) async throws {
        let startupBeganAt = Date()
        let name = engine.logName
        let modelsName = engine.modelsLogName
        logger.info("Starting \(name, privacy: .public) streaming session (\(self.engine.logVariant, privacy: .public))")

        onConnectionStateChange?(.warmingUp)

        // Ensure the model bundle is on disk. The higher-level flow is
        // responsible for surfacing the download UI before we get here —
        // this is a defensive check only.
        switch engine.checkModelsPresent() {
        case .missing(let stateMessage, let error):
            onConnectionStateChange?(.error(stateMessage))
            throw error
        case .present(let logDetail):
            logger.info(
                "\(name, privacy: .public) warmup phase: \(logDetail, privacy: .public) elapsedMs=\(self.elapsedMilliseconds(since: startupBeganAt), privacy: .public)"
            )
        }

        let models: Engine.Models
        do {
            logger.info("\(name, privacy: .public) warmup phase: loading \(modelsName, privacy: .public)")
            models = try await engine.loadModels()
            logger.info(
                "\(name, privacy: .public) warmup phase complete: \(modelsName, privacy: .public) loaded elapsedMs=\(self.elapsedMilliseconds(since: startupBeganAt), privacy: .public)"
            )
        } catch is CancellationError {
            logger.info("Model load cancelled externally; collapsing to idle")
            onConnectionStateChange?(.idle)
            throw CancellationError()
        } catch {
            // Fixed identifiers only (#1980): the description can hold a /Users/<name>/ path.
            let errorSummary = LocalModelLoadFailureDetail.publicSummary(for: error)
            logger.error("Failed to load \(name, privacy: .public) \(modelsName, privacy: .public): \(errorSummary, privacy: .public) description=\(error.localizedDescription, privacy: .private)")
            onConnectionStateChange?(.error(error.localizedDescription))
            throw error
        }
        try Task.checkCancellation()

        let session: Engine.Session
        do {
            logger.info("\(name, privacy: .public) warmup phase: initializing session")
            session = try await engine.makeSession(models: models)
            logger.info(
                "\(name, privacy: .public) warmup phase complete: session init elapsedMs=\(self.elapsedMilliseconds(since: startupBeganAt), privacy: .public)"
            )
        } catch is CancellationError {
            logger.info("Session init cancelled externally; collapsing to idle")
            onConnectionStateChange?(.idle)
            throw CancellationError()
        } catch {
            // Fixed identifiers only (#1980): the description can hold a /Users/<name>/ path.
            let errorSummary = LocalModelLoadFailureDetail.publicSummary(for: error)
            logger.error("Failed to construct \(name, privacy: .public) streaming session: \(errorSummary, privacy: .public) description=\(error.localizedDescription, privacy: .private)")
            onConnectionStateChange?(.error(error.localizedDescription))
            throw error
        }
        self.session = session

        // Wire session → client callbacks. These fire on the session actor;
        // we hop to the main actor for callback delivery.
        await session.setCallbacks(
            onConfirmedDelta: { [weak self] delta in
                Task { @MainActor [weak self] in
                    self?.onTranscriptUpdate?(delta, true)
                }
            },
            onVolatile: { [weak self] preview in
                Task { @MainActor [weak self] in
                    // Forward to the notch preview via onTranscriptUpdate
                    // (isFinal == false). Callers wire this into
                    // `AppState.streamingText`.
                    self?.onTranscriptUpdate?(preview, false)
                }
            },
            onError: { [weak self] (error: Error) in
                Task { @MainActor [weak self] in
                    guard let self else { return }
                    // Fixed identifiers only (#1980): the description can hold a /Users/<name>/ path.
                    let errorSummary = LocalModelLoadFailureDetail.publicSummary(for: error)
                    self.logger.error("Session error: \(errorSummary, privacy: .public) description=\(error.localizedDescription, privacy: .private)")
                    self.onConnectionStateChange?(.error(error.localizedDescription))
                    self.onError?(error)
                }
            }
        )
        if Task.isCancelled {
            // Tear down the session we just spun up; `cancel()` is idempotent
            // (it no-ops if start() never ran) so it's safe even though the
            // recognizer task hasn't been started yet.
            await session.cancel()
            self.session = nil
            onConnectionStateChange?(.idle)
            throw CancellationError()
        }

        do {
            logger.info("\(name, privacy: .public) startup phase: starting recognizer")
            try await session.start()
            logger.info(
                "\(name, privacy: .public) startup phase complete: recognizer started elapsedMs=\(self.elapsedMilliseconds(since: startupBeganAt), privacy: .public)"
            )
        } catch is CancellationError {
            // External cancel — e.g. the enclosing toggleTask was cancelled
            // by a rapid second shortcut press. Don't surface as error:
            // collapse cleanly to idle so the UI isn't stuck on amber.
            logger.info("Session start cancelled externally; cleaning up")
            await session.cancel()
            self.session = nil
            onConnectionStateChange?(.idle)
            throw CancellationError()
        } catch {
            // Fixed identifiers only (#1980): the description can hold a /Users/<name>/ path.
            let errorSummary = LocalModelLoadFailureDetail.publicSummary(for: error)
            logger.error("Failed to start \(name, privacy: .public) session: \(errorSummary, privacy: .public) description=\(error.localizedDescription, privacy: .private)")
            // start() may have partly initialized state before throwing; cancel()
            // drops callbacks and resets the underlying FluidAudio manager so we
            // don't leave a half-live session retained via its partial callback.
            await session.cancel()
            self.session = nil
            onConnectionStateChange?(.error(error.localizedDescription))
            throw error
        }
        if Task.isCancelled {
            await session.cancel()
            self.session = nil
            onConnectionStateChange?(.idle)
            throw CancellationError()
        }

        // Start audio capture and feed raw Float32 samples directly into
        // the session (resampling & ring-buffering handled internally).
        let capture = makeAudioCapture()
        capture.onAudioLevel = { [weak self] level in
            Task { @MainActor [weak self] in
                self?.onAudioLevel?(level)
            }
        }
        capture.onFloat32Samples = { [weak session] samples in
            // Runs on AVAudioEngine RT thread — must be non-blocking.
            // `feed` is nonisolated and appends to an NSLock-protected buffer
            // (lock-light, no await).
            session?.feed(samples)
        }
        self.audioCapture = capture

        do {
            logger.info("\(name, privacy: .public) startup phase: starting audio capture")
            try await capture.start()
            logger.info(
                "\(name, privacy: .public) startup phase complete: audio capture started elapsedMs=\(self.elapsedMilliseconds(since: startupBeganAt), privacy: .public)"
            )
        } catch is CancellationError {
            // Mirror the session.start cancellation handling: silent unwind.
            logger.info("Audio capture start cancelled externally; cleaning up")
            capture.stop()
            self.audioCapture = nil
            await session.cancel()
            self.session = nil
            onConnectionStateChange?(.idle)
            throw CancellationError()
        } catch {
            // Fixed identifiers only (#1980): the description can hold a /Users/<name>/ path.
            let errorSummary = LocalModelLoadFailureDetail.publicSummary(for: error)
            logger.error("Audio capture failed to start: \(errorSummary, privacy: .public) description=\(error.localizedDescription, privacy: .private)")
            self.audioCapture = nil
            await session.cancel()
            self.session = nil
            onConnectionStateChange?(.error(error.localizedDescription))
            throw error
        }
        if Task.isCancelled {
            capture.stop()
            self.audioCapture = nil
            await session.cancel()
            self.session = nil
            onConnectionStateChange?(.idle)
            throw CancellationError()
        }

        isActive = true
        onConnectionStateChange?(.streaming)
        logger.info(
            "\(name, privacy: .public) streaming session started totalElapsedMs=\(self.elapsedMilliseconds(since: startupBeganAt), privacy: .public)"
        )
    }

    /// Graceful stop: halt audio capture first so no new buffers queue up,
    /// drain the session, deliver the final text, fire `onSessionComplete`.
    /// Ordering prevents losing the final window on short utterances.
    func stopSession() async {
        guard isActive else { return }
        isActive = false

        let name = engine.logName
        logger.info("Stopping \(name, privacy: .public) streaming session")
        onConnectionStateChange?(.disconnecting)

        // 1. Stop audio capture first — no new input from this point on.
        audioCapture?.stop()
        audioCapture = nil
        onAudioLevel?(0)

        guard let session else {
            onConnectionStateChange?(.idle)
            return
        }

        let elapsed = await session.elapsedSeconds()

        // 2. Drain the session and deliver its final text exactly once.
        do {
            let finalText = try await session.finish()
            switch Engine.finalTextDelivery {
            case .finishReturnValue:
                // Deliver the final transcript synchronously (isFinal=true)
                // instead of through onConfirmedDelta: the callback path hops
                // through MainActor and can race against the cleanup below;
                // the synchronous return value path is deterministic.
                if !finalText.isEmpty {
                    onTranscriptUpdate?(finalText, true)
                }
            case .confirmedDeltaCallback:
                // finish() already emitted its tail through onConfirmedDelta;
                // finalText is the whole accumulated transcript, so typing it
                // here would deliver the transcript twice.
                break
            }
        } catch {
            // Fixed identifiers only (#1980): the description can hold a /Users/<name>/ path.
            let errorSummary = LocalModelLoadFailureDetail.publicSummary(for: error)
            logger.error("\(name, privacy: .public) session finish failed: \(errorSummary, privacy: .public) description=\(error.localizedDescription, privacy: .private)")
            onError?(error)
        }

        self.session = nil
        onConnectionStateChange?(.idle)
        onSessionComplete?(elapsed, 0)
        logger.info("\(name, privacy: .public) streaming session stopped (\(elapsed, privacy: .public)s)")
    }

    /// Abort — drop the tail, no final update, no session-complete.
    func cancel() async {
        guard isActive || session != nil else { return }
        isActive = false

        logger.info("Cancelling \(self.engine.logName, privacy: .public) streaming session")
        audioCapture?.stop()
        audioCapture = nil
        onAudioLevel?(0)

        if let session {
            await session.cancel()
        }
        self.session = nil

        onConnectionStateChange?(.idle)
    }
}
