//
//  LocalNemotronStreamingClient.swift
//  hyperwhisper
//
//  On-device streaming client for Nemotron 3.5 ASR Streaming Multilingual.
//  Mirrors `LocalParakeetStreamingClient` but talks to a
//  `NemotronStreamingSession` instead.
//

import Foundation
import FluidAudio
import os

@available(macOS 14.0, *)
@MainActor
final class LocalNemotronStreamingClient: NSObject, ObservableObject, StreamingClientProtocol {

    // MARK: - StreamingClientProtocol

    var onTranscriptUpdate: ((String, Bool) -> Void)?
    var onSessionComplete: ((Double, Double) -> Void)?
    var onError: ((Error) -> Void)?
    var onConnectionStateChange: ((StreamingConnectionState) -> Void)?
    var onAudioLevel: ((Float) -> Void)?

    var transcriptionProviderLabel: String {
        switch variant {
        case .latin: return "Nemotron 3.5 Latin (On-Device Streaming)"
        case .multilingual: return "Nemotron 3.5 Multilingual (On-Device Streaming)"
        }
    }

    // MARK: - Private

    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "LocalNemotronStreaming")
    private let variant: NemotronModelManager.Variant
    private let language: String?

    /// Optional provider handle used to share the batch-side `Runtime` cache.
    /// When wired (current code), the first streaming PTT after a batch run
    /// (or a prior streaming session) skips the ~1–3 s shared-model preload.
    /// When nil (preview/test), the client falls back to a direct FluidAudio
    /// download+preload call.
    private weak var provider: NemotronProvider?

    private var audioCapture: StreamingAudioCapture?
    private var session: NemotronStreamingSession?
    private var isActive = false

    private func elapsedMilliseconds(since start: Date) -> String {
        String(format: "%.0f", Date().timeIntervalSince(start) * 1000)
    }

    // MARK: - Init

    init(variant: NemotronModelManager.Variant, language: String?, provider: NemotronProvider? = nil) {
        self.variant = variant
        self.language = language
        self.provider = provider
        super.init()
    }

    // MARK: - Session lifecycle

    func startSession(config: StreamingSessionConfig) async throws {
        let startupBeganAt = Date()
        logger.info("Starting Nemotron streaming session (\(self.variant.rawValue, privacy: .public))")

        onConnectionStateChange?(.warmingUp)

        // Defensive existence check — UI surfaces the download flow upstream.
        let variantModelId = variant == .latin
            ? NemotronModelManager.Constants.latinModelId
            : NemotronModelManager.Constants.multilingualModelId
        guard NemotronModelManager.isVariantInstalled(variantModelId) else {
            onConnectionStateChange?(.error("Nemotron model not downloaded"))
            throw NemotronStreamingError.modelsNotAvailable
        }
        logger.info(
            "Nemotron warmup phase: model files present elapsedMs=\(self.elapsedMilliseconds(since: startupBeganAt), privacy: .public)"
        )

        // Load shared models. The provider's Runtime actor caches the bundle
        // per variant, so a second PTT (or a streaming PTT after a batch run)
        // skips the CoreML compile/load entirely. When no provider is wired
        // (preview/test), fall back to a direct FluidAudio call.
        let shared: SharedNemotronMultilingualModels
        do {
            logger.info("Nemotron warmup phase: preloading shared models")
            if let provider {
                shared = try await provider.sharedModels(for: variant)
            } else {
                shared = try await StreamingNemotronMultilingualAsrManager.downloadAndPreloadShared(
                    languageCode: variant.downloadLanguageHint,
                    chunkMs: NemotronModelManager.Constants.chunkMs,
                    to: nil,
                    configuration: nil,
                    progressHandler: nil
                )
            }
            logger.info(
                "Nemotron warmup phase complete: shared models loaded elapsedMs=\(self.elapsedMilliseconds(since: startupBeganAt), privacy: .public)"
            )
        } catch is CancellationError {
            logger.info("Model load cancelled externally; collapsing to idle")
            onConnectionStateChange?(.idle)
            throw CancellationError()
        } catch {
            logger.error("Failed to load Nemotron shared models: \(error.localizedDescription, privacy: .public)")
            onConnectionStateChange?(.error(error.localizedDescription))
            throw error
        }
        try Task.checkCancellation()

        let session: NemotronStreamingSession
        do {
            logger.info("Nemotron warmup phase: initializing session")
            session = try await NemotronStreamingSession(shared: shared, variant: variant, language: language)
            logger.info(
                "Nemotron warmup phase complete: session init elapsedMs=\(self.elapsedMilliseconds(since: startupBeganAt), privacy: .public)"
            )
        } catch is CancellationError {
            logger.info("Session init cancelled externally; collapsing to idle")
            onConnectionStateChange?(.idle)
            throw CancellationError()
        } catch {
            logger.error("Failed to construct Nemotron streaming session: \(error.localizedDescription, privacy: .public)")
            onConnectionStateChange?(.error(error.localizedDescription))
            throw error
        }
        self.session = session

        await session.setCallbacks(
            onConfirmedDelta: { [weak self] delta in
                Task { @MainActor [weak self] in
                    self?.onTranscriptUpdate?(delta, true)
                }
            },
            onVolatile: { [weak self] preview in
                Task { @MainActor [weak self] in
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
            logger.info("Nemotron startup phase: starting recognizer")
            try await session.start()
            logger.info(
                "Nemotron startup phase complete: recognizer started elapsedMs=\(self.elapsedMilliseconds(since: startupBeganAt), privacy: .public)"
            )
        } catch is CancellationError {
            logger.info("Session start cancelled externally; cleaning up")
            await session.cancel()
            self.session = nil
            onConnectionStateChange?(.idle)
            throw CancellationError()
        } catch {
            logger.error("Failed to start Nemotron session: \(error.localizedDescription, privacy: .public)")
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

        let capture = StreamingAudioCapture()
        capture.onAudioLevel = { [weak self] level in
            Task { @MainActor [weak self] in
                self?.onAudioLevel?(level)
            }
        }
        capture.onFloat32Samples = { [weak session] samples in
            session?.feed(samples)
        }
        self.audioCapture = capture

        do {
            logger.info("Nemotron startup phase: starting audio capture")
            try await capture.start()
            logger.info(
                "Nemotron startup phase complete: audio capture started elapsedMs=\(self.elapsedMilliseconds(since: startupBeganAt), privacy: .public)"
            )
        } catch is CancellationError {
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
            "Nemotron streaming session started totalElapsedMs=\(self.elapsedMilliseconds(since: startupBeganAt), privacy: .public)"
        )
    }

    /// Graceful stop: halt audio capture first, drain the session, emit final delta,
    /// fire `onSessionComplete`.
    func stopSession() async {
        guard isActive else { return }
        isActive = false

        logger.info("Stopping Nemotron streaming session")
        onConnectionStateChange?(.disconnecting)

        audioCapture?.stop()
        audioCapture = nil
        onAudioLevel?(0)

        guard let session else {
            onConnectionStateChange?(.idle)
            return
        }

        let elapsed = await session.elapsedSeconds()
        do {
            // Deliver the final transcript synchronously (isFinal=true) instead
            // of letting NemotronStreamingSession emit it via onConfirmedDelta.
            // The callback path hops through MainActor and can race against the
            // cleanup below; the synchronous return value path is deterministic.
            let finalText = try await session.finish()
            if !finalText.isEmpty {
                onTranscriptUpdate?(finalText, true)
            }
        } catch {
            logger.error("Nemotron session finish failed: \(error.localizedDescription, privacy: .public)")
            onError?(error)
        }

        self.session = nil
        onConnectionStateChange?(.idle)
        onSessionComplete?(elapsed, 0)
        logger.info("Nemotron streaming session stopped (\(elapsed, privacy: .public)s)")
    }

    /// Abort — no final delta, no session-complete.
    func cancel() async {
        guard isActive || session != nil else { return }
        isActive = false

        logger.info("Cancelling Nemotron streaming session")
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
