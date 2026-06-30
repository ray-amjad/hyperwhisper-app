//
//  NemotronStreamingSession.swift
//  hyperwhisper
//
//  On-device streaming wrapper around FluidAudio's
//  `StreamingNemotronMultilingualAsrManager`. Plays the same role as
//  `ParakeetStreamingSession` but drops the WordAgreementEngine:
//
//  Nemotron's RNN-T is stable enough on per-chunk hypotheses that we trust
//  the output as-is. Per-chunk callback text → volatile preview (notch).
//  `finish()` emission → single confirmed delta typed into the focused app.
//  This means the user may see the tail re-render once or twice as the
//  decoder refines it; per design we accept that vs. introducing latency
//  for a stability gate the model doesn't need.
//

import AVFoundation
import FluidAudio
import Foundation
import os

@available(macOS 14.0, *)
actor NemotronStreamingSession {

    // MARK: - Types

    typealias StringHandler = @Sendable (String) -> Void
    typealias ErrorHandler = @Sendable (Error) -> Void

    // MARK: - Dependencies

    private let manager: StreamingNemotronMultilingualAsrManager
    private let variant: NemotronModelManager.Variant
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "NemotronStreamingSession")

    // MARK: - Audio ring buffer
    //
    // Same RT-thread ingest model as ParakeetStreamingSession: NSLock-protected
    // append from `feed(_:)` on the AVAudioEngine RT tap; drained on the actor's
    // worker by the pass loop. Lock contention is microseconds because
    // `[Float].append` is amortized O(1) and the worker holds the lock only long
    // enough to swap out the pending samples.

    nonisolated private let bufferLock = NSLock()
    nonisolated(unsafe) private var pendingSamples: [Float] = []

    // MARK: - State

    private var didFinish: Bool = false
    private var didCancel: Bool = false
    private var startedAt: Date?
    private var lastEmittedPartial: String = ""

    // MARK: - Tasks

    private var passTask: Task<Void, Never>?
    private var partialConsumerTask: Task<Void, Never>?
    private var partialContinuation: AsyncStream<String>.Continuation?

    // MARK: - Callbacks

    private var onConfirmedDelta: StringHandler?
    private var onVolatile: StringHandler?
    private var onError: ErrorHandler?

    // MARK: - Constants

    // Poll cadence for draining the audio ring buffer into FluidAudio's manager.
    // FluidAudio handles chunking internally at `chunkSamples` (35840 samples for
    // 2240ms @ 16kHz) — anything we feed under that just buffers until the next
    // chunk completes. 250ms keeps p99 latency low without wasting CPU when no
    // chunk boundary has crossed.
    private let passIntervalSeconds: Double = 0.25

    // MARK: - Init

    init(shared: SharedNemotronMultilingualModels, variant: NemotronModelManager.Variant, language: String?) async throws {
        self.variant = variant
        let mgr = StreamingNemotronMultilingualAsrManager()
        try await mgr.loadFromShared(shared)
        await mgr.reset()
        // Resolve the language hint: "auto"/nil/empty → nil so FluidAudio uses
        // `default_prompt_id`. Otherwise pass through; FluidAudio's
        // `promptId(forLanguage:)` accepts bare codes ("en") and BCP-47 forms
        // ("en-US"), falling back to default if unmapped.
        let resolved: String? = {
            guard let language, !language.isEmpty, language != "auto" else { return nil }
            return language
        }()
        await mgr.setLanguage(resolved)
        self.manager = mgr
    }

    // MARK: - Public API

    func setCallbacks(
        onConfirmedDelta: StringHandler?,
        onVolatile: StringHandler?,
        onError: ErrorHandler?
    ) async {
        self.onConfirmedDelta = onConfirmedDelta
        self.onVolatile = onVolatile
        self.onError = onError

        // Wire FluidAudio's partial callback → our volatile hop via an
        // AsyncStream consumed serially by a single actor-bound task. The
        // closure's synchronous `yield(_:)` is FIFO by construction, so a
        // burst of partials always reaches `handlePartial` in the order
        // FluidAudio emitted them — closing the actor-scheduler reorder
        // window that the previous "Task { await handlePartial(...) }"
        // version left open.
        let (stream, continuation) = AsyncStream<String>.makeStream(bufferingPolicy: .unbounded)
        self.partialContinuation = continuation

        partialConsumerTask?.cancel()
        partialConsumerTask = Task { [weak self] in
            for await partial in stream {
                if Task.isCancelled { break }
                await self?.handlePartial(partial)
            }
        }

        let handler: NemotronMultilingualPartialCallback = { [weak self] partial in
            // `yield` is non-blocking and synchronous; no Task hop required.
            Task { [weak self] in
                await self?.enqueuePartial(partial)
            }
        }
        await manager.setPartialCallback(handler)
    }

    /// Bridge from the synchronous FluidAudio callback (non-isolated) into the
    /// actor's serial AsyncStream consumer. Just enqueues — actual UI hop
    /// happens in `handlePartial`.
    private func enqueuePartial(_ partial: String) {
        partialContinuation?.yield(partial)
    }

    /// Start the streaming loop. Spawns the pass worker task.
    func start() async throws {
        didFinish = false
        didCancel = false
        lastEmittedPartial = ""
        startedAt = Date()

        bufferLock.lock()
        pendingSamples.removeAll()
        bufferLock.unlock()

        logger.notice("Nemotron streaming session started (\(self.variant.rawValue, privacy: .public))")

        let sleepNanos = UInt64(passIntervalSeconds * 1_000_000_000)
        passTask = Task { [weak self] in
            while !Task.isCancelled {
                do {
                    try await Task.sleep(nanoseconds: sleepNanos)
                } catch {
                    break
                }
                if Task.isCancelled { break }
                guard let self else { return }
                if await self.didFinish { return }
                await self.drainPending()
            }
        }
    }

    /// Thread-safe sample ingest. Called from the AVAudioEngine RT tap thread.
    nonisolated func feed(_ samples: [Float]) {
        bufferLock.lock()
        pendingSamples.append(contentsOf: samples)
        bufferLock.unlock()
    }

    /// Drain gracefully. Cancels the pass loop, drains the buffer one last time,
    /// flushes the tail with `finish()`, and emits the full final text as a
    /// single confirmed delta.
    func finish() async throws -> String {
        guard !didFinish else { return "" }
        didFinish = true

        passTask?.cancel()
        _ = await passTask?.value
        passTask = nil

        // Tear down the partial consumer FIRST: close the stream and await
        // the consumer task so no in-flight partial races with the
        // synchronous final-delta return below. `didFinish=true` was set
        // above, so any partial still queued is dropped by `handlePartial`.
        partialContinuation?.finish()
        partialContinuation = nil
        _ = await partialConsumerTask?.value
        partialConsumerTask = nil

        // Drain any final samples that landed AFTER the last pass-loop tick,
        // including late RT-thread feeds that arrive between
        // `audioCapture.stop()` and this call. Bound the wait at 100 ms or two
        // consecutive empty observations to avoid stalling stop indefinitely.
        // If the underlying process call is cancelled mid-drain, break out
        // immediately instead of spinning to the 100 ms cap.
        let drainStart = Date()
        var emptyChecks = 0
        drainLoop: while emptyChecks < 2, Date().timeIntervalSince(drainStart) < 0.100 {
            do {
                let hadSamples = try await drainPendingReportingNonEmpty()
                if hadSamples {
                    emptyChecks = 0
                } else {
                    emptyChecks += 1
                }
            } catch is CancellationError {
                break drainLoop
            } catch {
                break drainLoop
            }
            try? await Task.sleep(nanoseconds: 10_000_000) // 10 ms
        }

        let finalText: String
        do {
            finalText = try await manager.finish()
        } catch is CancellationError {
            return ""
        } catch {
            logger.error("finish() failed: \(error.localizedDescription, privacy: .public)")
            onError?(error)
            return ""
        }

        let trimmed = finalText.trimmingCharacters(in: .whitespacesAndNewlines)
        if !trimmed.isEmpty {
            logger.info("emit final confirmed delta via return value: \(trimmed.count, privacy: .public) chars")
        }
        // Caller (LocalNemotronStreamingClient.stopSession) delivers this
        // synchronously to onTranscriptUpdate. Emitting onConfirmedDelta here
        // hops through MainActor and can race with stopSession's subsequent
        // cleanup — under MainActor pressure the typed text would land too
        // late or not at all.
        return trimmed
    }

    /// Abort without producing a final transcript. Drops callbacks FIRST so any
    /// in-flight pass becomes a no-op before teardown.
    func cancel() async {
        guard !didFinish && !didCancel else { return }
        didCancel = true
        didFinish = true

        onConfirmedDelta = nil
        onVolatile = nil
        onError = nil

        passTask?.cancel()
        _ = await passTask?.value
        passTask = nil

        partialContinuation?.finish()
        partialContinuation = nil
        _ = await partialConsumerTask?.value
        partialConsumerTask = nil

        // Drop the partial callback so FluidAudio stops retaining our closure
        // (which captures `self` via a `[weak self]` wrapper) before the manager
        // reset blocks while clearing state.
        await manager.setPartialCallback({ _ in })
        await manager.reset()
        logger.notice("Nemotron streaming session cancelled")
    }

    /// Elapsed wall-clock time since `start()` was called.
    func elapsedSeconds() -> Double {
        guard let startedAt else { return 0 }
        return Date().timeIntervalSince(startedAt)
    }

    // MARK: - Private

    private func drainPending() async {
        _ = try? await drainPendingReportingNonEmpty()
    }

    /// Drain the pending sample buffer into FluidAudio.
    /// - Returns: true if there were samples to drain.
    /// - Throws: re-throws `CancellationError` so the `finish()` drain loop
    ///   can break out promptly (the older "swallow + return true" shape made
    ///   the loop spin to its 100 ms cap on every cancelled stop).
    @discardableResult
    private func drainPendingReportingNonEmpty() async throws -> Bool {
        bufferLock.lock()
        let samples = pendingSamples
        pendingSamples.removeAll(keepingCapacity: true)
        bufferLock.unlock()

        guard !samples.isEmpty else { return false }

        do {
            _ = try await manager.process(samples: samples)
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            logger.error("process() failed: \(error.localizedDescription, privacy: .public)")
            onError?(error)
        }
        return true
    }

    private func handlePartial(_ partial: String) {
        let trimmed = partial.trimmingCharacters(in: .whitespacesAndNewlines)
        // Guard didFinish in addition to didCancel: a late FluidAudio partial
        // that lands during `finish()`'s drain phase used to race the
        // synchronous final delta — the volatile preview would briefly
        // overwrite the just-typed final text. With `didFinish` checked here,
        // late partials no-op once `finish()` has begun teardown.
        guard !trimmed.isEmpty, trimmed != lastEmittedPartial, !didCancel, !didFinish else { return }
        lastEmittedPartial = trimmed
        onVolatile?(trimmed)
    }
}

enum NemotronStreamingError: LocalizedError {
    case modelsNotAvailable

    var errorDescription: String? {
        switch self {
        case .modelsNotAvailable:
            return "Nemotron model is not downloaded. Install it from Settings → Models first."
        }
    }
}
