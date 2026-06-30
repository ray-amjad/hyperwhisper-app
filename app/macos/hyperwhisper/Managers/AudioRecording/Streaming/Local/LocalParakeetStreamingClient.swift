//
//  LocalParakeetStreamingClient.swift
//  hyperwhisper
//
//  On-device streaming client. Plays the same role as
//  `StreamingTranscriptionClient` for the cloud path, but the audio path
//  goes to a local `ParakeetStreamingSession` (wrapping FluidAudio's
//  `StreamingAsrManager`) instead of a WebSocket.
//

import Foundation
import FluidAudio
import os

@available(macOS 13.0, *)
@MainActor
final class LocalParakeetStreamingClient: NSObject, ObservableObject, StreamingClientProtocol {

    // MARK: - StreamingClientProtocol

    var onTranscriptUpdate: ((String, Bool) -> Void)?
    var onSessionComplete: ((Double, Double) -> Void)?
    var onError: ((Error) -> Void)?
    var onConnectionStateChange: ((StreamingConnectionState) -> Void)?
    var onAudioLevel: ((Float) -> Void)?

    var transcriptionProviderLabel: String {
        switch version {
        case .v2: return "Parakeet V2 (On-Device Streaming)"
        case .v3: return "Parakeet V3 (On-Device Streaming)"
        case .tdtCtc110m: return "Parakeet (On-Device Streaming)"
        @unknown default: return "Parakeet (On-Device Streaming)"
        }
    }

    // MARK: - Private

    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "LocalParakeetStreaming")
    private let version: AsrModelVersion

    private var audioCapture: StreamingAudioCapture?
    private var session: ParakeetStreamingSession?
    private var isActive = false

    private func elapsedMilliseconds(since start: Date) -> String {
        String(format: "%.0f", Date().timeIntervalSince(start) * 1000)
    }

    // MARK: - Init

    init(version: AsrModelVersion) {
        self.version = version
        super.init()
    }

    // MARK: - Session lifecycle

    func startSession(config: StreamingSessionConfig) async throws {
        let startupBeganAt = Date()
        logger.info("Starting local Parakeet streaming session (\(String(describing: self.version), privacy: .public))")

        onConnectionStateChange?(.warmingUp)

        // Ensure the model bundle is on disk. The higher-level flow is
        // responsible for surfacing the download UI before we get here —
        // this is a defensive check only.
        let directory = AsrModels.defaultCacheDirectory(for: version)
        guard AsrModels.modelsExist(at: directory) else {
            onConnectionStateChange?(.error("Parakeet model not downloaded"))
            throw ParakeetStreamingError.modelsNotAvailable
        }
        logger.info(
            "Local Parakeet warmup phase: models present at \(directory.path, privacy: .public) elapsedMs=\(self.elapsedMilliseconds(since: startupBeganAt), privacy: .public)"
        )

        // Load models (cheap: already on disk, memory-mapped by CoreML).
        let models: AsrModels
        do {
            logger.info("Local Parakeet warmup phase: loading models")
            models = try await AsrModels.downloadAndLoad(version: version)
            logger.info(
                "Local Parakeet warmup phase complete: loading models elapsedMs=\(self.elapsedMilliseconds(since: startupBeganAt), privacy: .public)"
            )
        } catch is CancellationError {
            logger.info("Model load cancelled externally; collapsing to idle")
            onConnectionStateChange?(.idle)
            throw CancellationError()
        } catch {
            logger.error("Failed to load Parakeet models: \(error.localizedDescription, privacy: .public)")
            onConnectionStateChange?(.error(error.localizedDescription))
            throw error
        }
        try Task.checkCancellation()

        let session: ParakeetStreamingSession
        do {
            logger.info("Local Parakeet warmup phase: initializing session")
            session = try await ParakeetStreamingSession(models: models)
            logger.info(
                "Local Parakeet warmup phase complete: session init elapsedMs=\(self.elapsedMilliseconds(since: startupBeganAt), privacy: .public)"
            )
        } catch is CancellationError {
            logger.info("Session init cancelled externally; collapsing to idle")
            onConnectionStateChange?(.idle)
            throw CancellationError()
        } catch {
            logger.error("Failed to construct Parakeet streaming session: \(error.localizedDescription, privacy: .public)")
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
                    self.logger.error("Session error: \(error.localizedDescription, privacy: .public)")
                    self.onConnectionStateChange?(.error(error.localizedDescription))
                    self.onError?(error)
                }
            }
        )
        // Cancelled between setCallbacks and session.start(): the session
        // has no internal tasks yet, so dropping it is sufficient.
        if Task.isCancelled {
            self.session = nil
            onConnectionStateChange?(.idle)
            throw CancellationError()
        }

        do {
            logger.info("Local Parakeet startup phase: starting recognizer")
            try await session.start()
            logger.info(
                "Local Parakeet startup phase complete: recognizer started elapsedMs=\(self.elapsedMilliseconds(since: startupBeganAt), privacy: .public)"
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
            logger.error("Failed to start streaming session: \(error.localizedDescription, privacy: .public)")
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
        let capture = StreamingAudioCapture()
        capture.onAudioLevel = { [weak self] level in
            Task { @MainActor [weak self] in
                self?.onAudioLevel?(level)
            }
        }
        capture.onFloat32Samples = { [weak session] samples in
            // Runs on AVAudioEngine RT thread — must be non-blocking.
            // `feed` is nonisolated and appends to an NSLock-protected ring buffer
            // (lock-light, no await).
            session?.feed(samples)
        }
        self.audioCapture = capture

        do {
            logger.info("Local Parakeet startup phase: starting audio capture")
            try await capture.start()
            logger.info(
                "Local Parakeet startup phase complete: audio capture started elapsedMs=\(self.elapsedMilliseconds(since: startupBeganAt), privacy: .public)"
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
            logger.error("Audio capture failed to start: \(error.localizedDescription, privacy: .public)")
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
            "Local Parakeet streaming session started totalElapsedMs=\(self.elapsedMilliseconds(since: startupBeganAt), privacy: .public)"
        )
    }

    /// Graceful stop: halt audio capture first so no new buffers queue up,
    /// drain the session (which emits every remaining chunk as a confirmed
    /// delta before returning), fire `onSessionComplete`. Ordering prevents
    /// losing the final window on short utterances.
    func stopSession() async {
        guard isActive else { return }
        isActive = false

        logger.info("Stopping local Parakeet streaming session")
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

        // 2. Drain the session. `finish()` internally:
        //    - flushes pending buffers to FluidAudio
        //    - awaits the recognizer's flushRemaining (yields final updates)
        //    - closes the update stream and drains the subscriber
        //    - emits any chunks not already typed as confirmed deltas
        //      (covers the final volatile chunk + mixed-confidence gaps)
        do {
            _ = try await session.finish()
        } catch {
            logger.error("Session finish failed: \(error.localizedDescription, privacy: .public)")
            onError?(error)
        }

        self.session = nil
        onConnectionStateChange?(.idle)
        onSessionComplete?(elapsed, 0)
        logger.info("Local Parakeet streaming session stopped (\(elapsed, privacy: .public)s)")
    }

    /// Abort — drop the tail, no final update, no session-complete.
    func cancel() async {
        guard isActive || session != nil else { return }
        isActive = false

        logger.info("Cancelling local Parakeet streaming session")
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
