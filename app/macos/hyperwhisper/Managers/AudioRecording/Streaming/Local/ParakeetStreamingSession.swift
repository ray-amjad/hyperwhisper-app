//
//  ParakeetStreamingSession.swift
//  hyperwhisper
//
//  Agreement-based streaming for on-device Parakeet (v2). Replaces v1's
//  `StreamingAsrManager` wrapper, which never emitted incremental updates
//  during recording (all inference fired on stop → users saw text paste
//  after hitting the stop button).
//
//  Algorithm:
//  - Maintain a rolling ring buffer of 16 kHz Float32 samples.
//  - Every `transcribeIntervalSeconds` (1 s), slice from the current
//    `hypothesisStartTime` to the tail, pad 1 s of trailing silence so the
//    TDT decoder emits final-position punctuation, and run a fresh batch
//    pass through `AsrManager.transcribe(_ samples:source:)`. The batch
//    API is explicitly stateless — it calls `resetDecoderState()` after
//    every pass (see FluidAudio `AsrManager.swift` line 351), so each pass
//    is independent.
//  - Feed the token timings into `WordAgreementEngine`, which only confirms
//    a prefix when 3 consecutive passes agree, 3 sentence-enders have
//    landed, and the last 3 boundary words all clear 0.6 confidence.
//  - On each confirm: emit the newly-confirmed text, trim the ring buffer
//    to the new hypothesis start (bounded memory for long recordings),
//    and track `trimmedSampleCount` so absolute-time seeks survive.
//  - `finish()` runs one final batch pass on the un-confirmed tail and
//    emits the trimmed text whole as the last committed delta.
//
//  Why a dedicated `AsrManager` (not `ParakeetProvider.Runtime`'s):
//  - `AsrManager.transcribe` is not safe to call concurrently on a single
//    instance (shared decoder state machinery).
//  - History-rebuild batch jobs can fire during live streaming; sharing
//    would interleave passes and corrupt both.
//  - Memory cost is one extra set of CoreML weights per-session. Acceptable
//    tradeoff for correctness; revisit with a mutex if it bites.
//

import FluidAudio
import Foundation
import os

@available(macOS 13.0, *)
actor ParakeetStreamingSession {

    // MARK: - Types

    typealias StringHandler = @Sendable (String) -> Void
    typealias ErrorHandler = @Sendable (Error) -> Void

    // MARK: - Dependencies

    // Dedicated manager — isolated from ParakeetProvider's batch runtime.
    private let manager: AsrManager
    private let agreement: WordAgreementEngine
    private let config: AgreementConfig
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "ParakeetStreamingSession")

    // MARK: - Audio ring buffer
    //
    // `feed(_:)` is called from the AVAudioEngine RT tap thread, so the
    // buffer must be accessible without crossing an actor hop. NSLock is
    // cheaper than actor reentrancy and keeps the RT thread non-blocking
    // in the common case (contention is only against the pass worker,
    // which holds the lock for microseconds).
    //
    // `trimmedSampleCount` tracks how many samples have been dropped from
    // the front of `audioBuffer` so that absolute-time seeking (based on
    // word timings in absolute-recording time) still works after trimming.
    nonisolated private let bufferLock = NSLock()
    nonisolated(unsafe) private var audioBuffer: [Float] = []
    nonisolated(unsafe) private var trimmedSampleCount: Int = 0

    // MARK: - Pass bookkeeping (actor-isolated)

    private var lastTranscribedSampleCount: Int = 0
    private var isTranscribing: Bool = false   // single-flight guard (no reentrant transcribe)
    private var didFinish: Bool = false
    private var lastVolatileEmitted: String = ""
    private var startedAt: Date?
    private var accumulatedConfirmed: [String] = []  // for finish()'s full-text return
    private var passCounter: Int = 0

    // MARK: - Tasks

    private var passTask: Task<Void, Never>?

    // MARK: - Callbacks

    private var onConfirmedDelta: StringHandler?
    private var onVolatile: StringHandler?
    private var onError: ErrorHandler?

    // MARK: - Audio constants (hardcoded — battle-tested values)

    private let sampleRate: Double = 16000
    private let minNewSamples: Int = 8000           // 0.5 s
    private let maxSingleChunkSamples: Int = 240_000 // 15 s — above this, chunking kicks in upstream
    private let trailingSilenceSamples: Int = 16_000 // 1 s — unlocks final-position punctuation

    // MARK: - Init

    init(models: AsrModels, config: AgreementConfig = AgreementConfig()) async throws {
        self.config = config
        self.agreement = WordAgreementEngine(config: config)
        // FluidAudio 0.15.x: AsrManager became an actor; legacy
        // `initialize(models:)` / `resetDecoderState()` are gone. Pass models at
        // init and clear the shared ML array cache via reset().
        let m = AsrManager(config: .default, models: models)
        await m.reset()
        self.manager = m
    }

    // MARK: - Public API

    func setCallbacks(
        onConfirmedDelta: StringHandler?,
        onVolatile: StringHandler?,
        onError: ErrorHandler?
    ) {
        self.onConfirmedDelta = onConfirmedDelta
        self.onVolatile = onVolatile
        self.onError = onError
    }

    /// Start the streaming loop. Spawns the pass worker task.
    func start() async throws {
        agreement.reset()
        lastTranscribedSampleCount = 0
        isTranscribing = false
        didFinish = false
        lastVolatileEmitted = ""
        accumulatedConfirmed.removeAll()
        passCounter = 0
        startedAt = Date()

        bufferLock.lock()
        audioBuffer.removeAll()
        trimmedSampleCount = 0
        bufferLock.unlock()

        let intervalSec = config.transcribeIntervalSeconds
        logger.notice("Parakeet streaming session started (interval=\(intervalSec, privacy: .public)s)")

        let sleepNanos = UInt64(intervalSec * 1_000_000_000)
        passTask = Task { [weak self] in
            while !Task.isCancelled {
                do {
                    try await Task.sleep(nanoseconds: sleepNanos)
                } catch {
                    break
                }
                if Task.isCancelled { break }
                await self?.runPass()
            }
        }
    }

    /// Thread-safe sample ingest. Called from the AVAudioEngine RT tap via
    /// `StreamingAudioCapture.onFloat32Samples`. Lock is held only for the
    /// append — microseconds even on long buffers because `[Float].append`
    /// is amortized O(1).
    nonisolated func feed(_ samples: [Float]) {
        bufferLock.lock()
        audioBuffer.append(contentsOf: samples)
        bufferLock.unlock()
    }

    /// Drain gracefully. Cancels the pass loop, runs one final clean pass
    /// on the un-confirmed tail, emits the tail as a single confirmed
    /// delta, and returns the full accumulated transcript.
    func finish() async throws -> String {
        guard !didFinish else { return "" }
        didFinish = true

        passTask?.cancel()
        _ = await passTask?.value
        passTask = nil

        // Late RT-thread feeds can land between `audioCapture.stop()` and this
        // call. Snapshot the buffer size, wait briefly, and re-check — if more
        // samples landed during the window, include them in the final pass.
        // Bounded at 100 ms or two consecutive empty observations.
        let drainStart = Date()
        var emptyChecks = 0
        var lastSeenCount = audioBufferAbsoluteCount()
        while emptyChecks < 2, Date().timeIntervalSince(drainStart) < 0.100 {
            try? await Task.sleep(nanoseconds: 10_000_000) // 10 ms
            let nowCount = audioBufferAbsoluteCount()
            if nowCount > lastSeenCount {
                lastSeenCount = nowCount
                emptyChecks = 0
            } else {
                emptyChecks += 1
            }
        }

        let tailText = await runFinalPass() ?? ""
        if !tailText.isEmpty {
            logger.info(
                "emit final delta: chars=\(tailText.count, privacy: .public) spaces=\(Self.whitespaceCount(tailText), privacy: .public) words=\(Self.wordCount(tailText), privacy: .public) text=\(Self.diagnosticExcerpt(tailText), privacy: .public)"
            )
            accumulatedConfirmed.append(tailText)
            onConfirmedDelta?(tailText)
        }

        let full = accumulatedConfirmed
            .joined(separator: " ")
            .trimmingCharacters(in: .whitespaces)
        logger.notice("Parakeet streaming session finished: \(full.count, privacy: .public) chars")
        return full
    }

    /// Abort without producing a final transcript. Callbacks are dropped
    /// FIRST so any in-flight pass becomes a no-op before teardown —
    /// prevents a late-arriving delta from typing into the focused app
    /// after the user already cancelled.
    func cancel() async {
        guard !didFinish else { return }
        didFinish = true

        onConfirmedDelta = nil
        onVolatile = nil
        onError = nil

        passTask?.cancel()
        _ = await passTask?.value
        passTask = nil

        await manager.cleanup()
        logger.notice("Parakeet streaming session cancelled")
    }

    /// Elapsed wall-clock time since `start()` was called.
    func elapsedSeconds() -> Double {
        guard let startedAt else { return 0 }
        return Date().timeIntervalSince(startedAt)
    }

    // MARK: - Private

    private func audioBufferAbsoluteCount() -> Int {
        bufferLock.lock()
        let n = trimmedSampleCount + audioBuffer.count
        bufferLock.unlock()
        return n
    }

    private func runPass() async {
        if isTranscribing { return }
        if didFinish { return }

        // Snapshot absolute sample count under lock.
        bufferLock.lock()
        let absoluteSampleCount = trimmedSampleCount + audioBuffer.count
        bufferLock.unlock()

        // Gates: need at least 0.5 s of NEW audio and 1 s total buffered.
        if absoluteSampleCount - lastTranscribedSampleCount < minNewSamples { return }
        if absoluteSampleCount < Int(sampleRate) { return }

        isTranscribing = true
        defer { isTranscribing = false }

        // Seek to the start of the first unconfirmed word (or the end of
        // confirmed text if nothing unconfirmed yet). Expressed in
        // absolute recording time, then converted to a buffer-relative
        // index by subtracting the trimmed-sample offset.
        let seekTime = agreement.hypothesisStartTime > 0
            ? agreement.hypothesisStartTime
            : agreement.confirmedEndTime
        let seekSample = max(0, Int(seekTime * sampleRate))

        bufferLock.lock()
        let bufferRelativeSeek = max(0, seekSample - trimmedSampleCount)
        let sliceEnd = audioBuffer.count
        if bufferRelativeSeek >= sliceEnd {
            bufferLock.unlock()
            return
        }
        var slice = Array(audioBuffer[bufferRelativeSeek..<sliceEnd])
        bufferLock.unlock()

        // Pad 1 s trailing silence if it fits under the chunker's 15 s
        // cap. Without this, the TDT decoder never emits final-position
        // punctuation and the agreement engine's sentence-ender rule
        // never fires past sentence 1.
        if slice.count + trailingSilenceSamples <= maxSingleChunkSamples {
            slice.append(contentsOf: repeatElement(Float(0), count: trailingSilenceSamples))
        }

        // Minimum 1 s guard (post-pad slice is always >= trailing silence
        // when pad was applied, but be explicit).
        if slice.count < Int(sampleRate) { return }

        passCounter += 1
        let passIndex = passCounter

        let t0 = Date()
        let result: ASRResult
        do {
            // FluidAudio 0.15.x removed the per-call `source:` arg. The batch
            // path is stateless per pass (see file header), so allocate a fresh
            // TdtDecoderState each call — equivalent to the old resetDecoderState().
            var decoderState = try TdtDecoderState()
            result = try await manager.transcribe(slice, decoderState: &decoderState, language: nil)
        } catch is CancellationError {
            return
        } catch {
            logger.error("pass #\(passIndex, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
            onError?(error)
            return
        }
        let infMs = Int(Date().timeIntervalSince(t0) * 1000)

        lastTranscribedSampleCount = absoluteSampleCount

        // No timings: show the raw text as a volatile hint and bail.
        // This shouldn't happen in practice with TDT models, but keeps
        // the UI alive if the model ever regresses on timings output.
        guard let timings = result.tokenTimings, !timings.isEmpty else {
            let text = result.text.trimmingCharacters(in: .whitespaces)
            if !text.isEmpty && text != lastVolatileEmitted {
                lastVolatileEmitted = text
                onVolatile?(text)
            }
            logger.notice("pass #\(passIndex, privacy: .public): seek=\(seekTime, privacy: .public)s slice=\(slice.count, privacy: .public) inf=\(infMs, privacy: .public)ms conf=\(result.confidence, privacy: .public) notimings")
            return
        }

        let timeOffset = Double(seekSample) / sampleRate
        let words = WordAgreementEngine.words(from: timings, transcript: result.text, timeOffset: timeOffset)
        if words.isEmpty { return }

        let ar = agreement.processTranscriptionResult(words: words, resultConfidence: result.confidence)

        if !ar.newlyConfirmedText.isEmpty {
            let normalized = Self.normalizeSentence(ar.newlyConfirmedText)
            if !normalized.isEmpty {
                logger.info(
                    "emit confirmed delta: chars=\(normalized.count, privacy: .public) spaces=\(Self.whitespaceCount(normalized), privacy: .public) words=\(Self.wordCount(normalized), privacy: .public) text=\(Self.diagnosticExcerpt(normalized), privacy: .public)"
                )
                accumulatedConfirmed.append(normalized)
                onConfirmedDelta?(normalized)
            }
        }
        if !ar.fullText.isEmpty && ar.fullText != lastVolatileEmitted {
            lastVolatileEmitted = ar.fullText
            onVolatile?(ar.fullText)
        }

        let confirmedIncrement = ar.newlyConfirmedText.isEmpty ? 0 : 1
        logger.notice("pass #\(passIndex, privacy: .public): seek=\(seekTime, privacy: .public)s slice=\(slice.count, privacy: .public) inf=\(infMs, privacy: .public)ms conf=\(result.confidence, privacy: .public) words=\(words.count, privacy: .public) confirmed+=\(confirmedIncrement, privacy: .public)")

        // Trim the audio buffer to the new hypothesis start. Keeps memory
        // bounded regardless of recording length.
        let newHypStart = agreement.hypothesisStartTime
        if newHypStart > 0 {
            let safeTrim = max(0, Int(newHypStart * sampleRate))
            let toTrim = safeTrim - trimmedSampleCount
            if toTrim > 0 {
                bufferLock.lock()
                let actual = min(toTrim, audioBuffer.count)
                if actual > 0 {
                    audioBuffer.removeFirst(actual)
                    trimmedSampleCount += actual
                }
                bufferLock.unlock()
            }
        }
    }

    /// Final flush at `finish()`: same seek/slice/pad as `runPass` but
    /// returns the raw trimmed text. The agreement engine is skipped —
    /// this IS the final tail, so emit the whole thing as one commit.
    private func runFinalPass() async -> String? {
        if isTranscribing { return nil }

        bufferLock.lock()
        let absoluteSampleCount = trimmedSampleCount + audioBuffer.count
        bufferLock.unlock()

        let seekTime = agreement.hypothesisStartTime > 0
            ? agreement.hypothesisStartTime
            : agreement.confirmedEndTime
        let seekSample = max(0, Int(seekTime * sampleRate))

        bufferLock.lock()
        let bufferRelativeSeek = max(0, seekSample - trimmedSampleCount)
        let sliceEnd = audioBuffer.count
        if bufferRelativeSeek >= sliceEnd {
            bufferLock.unlock()
            return nil
        }
        var slice = Array(audioBuffer[bufferRelativeSeek..<sliceEnd])
        bufferLock.unlock()

        if slice.count + trailingSilenceSamples <= maxSingleChunkSamples {
            slice.append(contentsOf: repeatElement(Float(0), count: trailingSilenceSamples))
        }

        if slice.count < Int(sampleRate) { return nil }

        isTranscribing = true
        defer { isTranscribing = false }

        let t0 = Date()
        let result: ASRResult
        do {
            // FluidAudio 0.15.x removed the per-call `source:` arg. The batch
            // path is stateless per pass (see file header), so allocate a fresh
            // TdtDecoderState each call — equivalent to the old resetDecoderState().
            var decoderState = try TdtDecoderState()
            result = try await manager.transcribe(slice, decoderState: &decoderState, language: nil)
        } catch is CancellationError {
            return nil
        } catch {
            logger.error("final pass failed: \(error.localizedDescription, privacy: .public)")
            onError?(error)
            return nil
        }
        let infMs = Int(Date().timeIntervalSince(t0) * 1000)

        lastTranscribedSampleCount = absoluteSampleCount

        let text = Self.normalizeSentence(result.text)
        logger.notice(
            "final pass: seek=\(seekTime, privacy: .public)s slice=\(slice.count, privacy: .public) inf=\(infMs, privacy: .public)ms conf=\(result.confidence, privacy: .public) chars=\(text.count, privacy: .public) spaces=\(Self.whitespaceCount(text), privacy: .public) words=\(Self.wordCount(text), privacy: .public) text=\(Self.diagnosticExcerpt(text), privacy: .public)"
        )
        return text.isEmpty ? nil : text
    }

    private static func normalizeSentence(_ text: String) -> String {
        var s = text
        s = s.replacingOccurrences(of: "\\s+", with: " ", options: .regularExpression)
        s = s.replacingOccurrences(of: "\\s+([,.!?;:])", with: "$1", options: .regularExpression)
        s = s.replacingOccurrences(of: "([.!?;])([A-Za-z])", with: "$1 $2", options: .regularExpression)
        return s.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private static func whitespaceCount(_ text: String) -> Int {
        text.reduce(into: 0) { count, character in
            if character.isWhitespace {
                count += 1
            }
        }
    }

    private static func wordCount(_ text: String) -> Int {
        text.split(whereSeparator: \.isWhitespace).count
    }

    private static func diagnosticExcerpt(_ text: String, limit: Int = 120) -> String {
        let escaped = text
            .replacingOccurrences(of: "\n", with: "\\n")
            .replacingOccurrences(of: "\t", with: "\\t")
        let excerpt = String(escaped.prefix(limit))
        return "\"\(excerpt)\""
    }
}

// MARK: - Errors

enum ParakeetStreamingError: LocalizedError {
    case modelsNotAvailable

    var errorDescription: String? {
        switch self {
        case .modelsNotAvailable:
            return "Parakeet model is not downloaded. Install it from Settings → Models first."
        }
    }
}
