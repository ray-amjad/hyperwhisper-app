import Foundation
import AVFoundation
import CoreML
import os
import FluidAudio

// QWEN3 RUNTIME LOAD WITH A COMPUTE-UNIT FALLBACK:
// FluidAudio loads Qwen3 with `.all` by default. On some Macs the Neural Engine
// cannot compile the decoder (CoreML error -14), and that used to be the end of
// it: "Failed to initialize runtime", with only the reason string in Sentry.
// The same model loads and transcribes on CPU+GPU, so a failed `.all` load now
// tries `.cpuAndGPU` once before it gives up.
//
// Kept outside the `@available(macOS 15.0, *)` class: the pipeline (macOS 14)
// reads `ReportedFailure` to skip a second Sentry issue, and the tests drive
// `loadWithFallback` with a fake loader instead of a 4 GB CoreML model.
enum Qwen3AsrRuntimeLoad {

    static let providerName = "Qwen3 ASR"

    /// The order the runtime tries. `.all` first: when the Neural Engine
    /// compiles the model, it is the fast path.
    static let computeUnitAttempts: [MLComputeUnits] = [.all, .cpuAndGPU]

    /// One attempt that threw. Identifiers only: an error's description or
    /// userInfo can carry the model path, which holds the account name.
    struct Failure: Equatable {
        let computeUnits: MLComputeUnits
        let errorDomain: String
        let errorCode: Int
    }

    /// Every attempt failed. The provider maps this to a user-facing
    /// `TranscriptionError` and the runtime has already reported it.
    struct AllAttemptsFailedError: Error {
        let failures: [Failure]
        /// The last attempt's error, with identifiers only.
        let lastError: Error
    }

    struct Loaded<Value> {
        let value: Value
        let computeUnits: MLComputeUnits
        /// The attempts that failed before `computeUnits` worked. Empty when
        /// the first attempt worked.
        let failures: [Failure]
    }

    /// Try `load` with each compute-unit option in `attempts`, in order, and
    /// return the first one that works.
    ///
    /// A cancellation is never retried: a `CancellationError`, or any error on
    /// a cancelled task, is rethrown as it is.
    static func loadWithFallback<Value>(
        attempts: [MLComputeUnits] = computeUnitAttempts,
        load: (MLComputeUnits) async throws -> Value
    ) async throws -> Loaded<Value> {
        var failures: [Failure] = []
        var lastError: Error = CancellationError()
        for units in attempts {
            do {
                let value = try await load(units)
                return Loaded(value: value, computeUnits: units, failures: failures)
            } catch {
                if error is CancellationError || Task.isCancelled {
                    throw error
                }
                let nsError = error as NSError
                failures.append(Failure(
                    computeUnits: units,
                    errorDomain: nsError.domain,
                    errorCode: nsError.code
                ))
                lastError = SentryService.identifierOnlyError(error)
            }
        }
        throw AllAttemptsFailedError(failures: failures, lastError: lastError)
    }

    static func label(for units: MLComputeUnits) -> String {
        switch units {
        case .all: return "all"
        case .cpuAndGPU: return "cpuAndGPU"
        case .cpuOnly: return "cpuOnly"
        case .cpuAndNeuralEngine: return "cpuAndNeuralEngine"
        @unknown default: return "unknown(\(units.rawValue))"
        }
    }

    // MARK: - What reaches Sentry

    /// One Sentry report for one runtime load. Built by a pure function so a
    /// test can pin exactly what leaves the machine.
    struct SentryReport {
        enum Kind: Equatable {
            /// A fallback worked. A Sentry LOG (warning), not an Issue: the
            /// user got a transcript, we only want to count how often.
            case fallbackSucceeded
            /// Every attempt failed. A Sentry EVENT (an Issue).
            case allAttemptsFailed
        }
        let kind: Kind
        let message: String
        /// The error for `SentryService.capture`, `nil` for a log.
        let error: Error?
        let extras: [String: Any]
        let tags: [String: String]
        let fingerprint: [String]?
    }

    static func sentryReport(
        failures: [Failure],
        loadedOn: MLComputeUnits?,
        lastError: Error?,
        elapsedMs: Int
    ) -> SentryReport? {
        guard let first = failures.first else { return nil }

        var extras: [String: Any] = [
            "qwen3_load_duration_ms": elapsedMs,
            "qwen3_load_attempts": failures.map { label(for: $0.computeUnits) },
        ]
        for failure in failures {
            let key = label(for: failure.computeUnits)
            extras["qwen3_\(key)_error_domain"] = failure.errorDomain
            extras["qwen3_\(key)_error_code"] = failure.errorCode
        }
        // The first failure is the Neural Engine one, and the one this whole
        // fallback exists for, so it is the one that gets the searchable tags.
        var tags: [String: String] = [
            "component": "transcription",
            "provider": providerName,
            "qwen3_first_error_domain": first.errorDomain,
            "qwen3_first_error_code": String(first.errorCode),
        ]

        if let loadedOn {
            extras["qwen3_loaded_compute_units"] = label(for: loadedOn)
            tags["qwen3_loaded_compute_units"] = label(for: loadedOn)
            return SentryReport(
                kind: .fallbackSucceeded,
                message: "Qwen3 ASR loaded on a fallback compute unit",
                error: nil,
                extras: extras,
                tags: tags,
                fingerprint: nil
            )
        }

        let last = failures[failures.count - 1]
        return SentryReport(
            kind: .allAttemptsFailed,
            message: "Qwen3 ASR runtime load failed",
            error: lastError.map(SentryService.identifierOnlyError)
                ?? NSError(domain: last.errorDomain, code: last.errorCode, userInfo: nil),
            extras: extras,
            tags: tags,
            fingerprint: [
                "qwen3-asr-runtime-load",
                first.errorDomain,
                String(first.errorCode),
                last.errorDomain,
                String(last.errorCode),
            ]
        )
    }

    static func send(_ report: SentryReport) {
        guard AppLogger.isErrorLoggingEnabled else { return }
        switch report.kind {
        case .fallbackSucceeded:
            SentryService.captureDiagnosticMessage(
                report.message,
                severity: .warning,
                extras: report.extras,
                tags: report.tags,
                includeRecentLogs: false
            )
        case .allAttemptsFailed:
            SentryService.capture(
                error: report.error ?? CancellationError(),
                message: report.message,
                extras: report.extras,
                tags: report.tags,
                fingerprint: report.fingerprint,
                includeRecentLogs: false
            )
        }
    }

    // MARK: - What reaches the user

    /// The reason the user reads when every attempt failed. The runtime has
    /// already sent the real CoreML domain and code to Sentry, so the pipeline
    /// and the model manager match on this reason and do not send a second,
    /// less useful issue built from it.
    static let reportedFailureReason =
        "The model could not load on this Mac, on the Neural Engine or on CPU+GPU. Download it again, or choose another model."

    static func userFacingError() -> TranscriptionError {
        .providerNotAvailable(provider: providerName, reason: reportedFailureReason)
    }

    /// True for the error `userFacingError()` makes: Sentry already has it.
    static func isReportedAtSource(_ error: Error) -> Bool {
        guard case .providerNotAvailable(let provider, let reason)? = error as? TranscriptionError else {
            return false
        }
        return provider == providerName && reason == reportedFailureReason
    }
}

@available(macOS 15.0, *)
final class Qwen3AsrProvider: TranscriptionProvider {

    private actor Runtime {
        private var manager: Qwen3AsrManager?
        // DIRECTORY TRACKING:
        // Remember which directory the cached manager was loaded from. Qwen3 has
        // two on-disk variants (f32 / int8) and `resolvedModelDirectory()` can
        // switch between them after a delete + re-download. Without this, a
        // cached f32 manager would keep serving inference even once the on-disk
        // install changed to int8 — reading weights from a now-deleted directory.
        private var loadedDirectory: URL?
        private var loadGeneration = 0
        private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "Qwen3AsrProvider")

        private static func milliseconds(since start: ContinuousClock.Instant) -> Int {
            let elapsed = ContinuousClock.now - start
            return Int(elapsed.components.seconds * 1000)
                + Int(elapsed.components.attoseconds / 1_000_000_000_000_000)
        }

        func ensureLoaded(modelDirectory: URL) async throws -> Qwen3AsrManager {
            if let manager, loadedDirectory == modelDirectory {
                return manager
            }

            // Drop the stale manager before loading the new directory so a load
            // failure can't leave a manager paired with the wrong directory.
            manager = nil
            loadedDirectory = nil

            let generation = loadGeneration
            let start = ContinuousClock.now
            let mgr: Qwen3AsrManager
            do {
                let loaded = try await Qwen3AsrRuntimeLoad.loadWithFallback { units in
                    let candidate = Qwen3AsrManager()
                    try await candidate.loadModels(from: modelDirectory, computeUnits: units)
                    return candidate
                }
                mgr = loaded.value
                let elapsedMs = Self.milliseconds(since: start)
                let unitsLabel = Qwen3AsrRuntimeLoad.label(for: loaded.computeUnits)
                logger.info("Qwen3 ASR runtime loaded computeUnits=\(unitsLabel, privacy: .public) durationMs=\(elapsedMs, privacy: .public) failedAttempts=\(loaded.failures.count, privacy: .public)")
                if let report = Qwen3AsrRuntimeLoad.sentryReport(
                    failures: loaded.failures,
                    loadedOn: loaded.computeUnits,
                    lastError: nil,
                    elapsedMs: elapsedMs
                ) {
                    Qwen3AsrRuntimeLoad.send(report)
                }
            } catch let error as Qwen3AsrRuntimeLoad.AllAttemptsFailedError {
                let elapsedMs = Self.milliseconds(since: start)
                for failure in error.failures {
                    let unitsLabel = Qwen3AsrRuntimeLoad.label(for: failure.computeUnits)
                    logger.error("Qwen3 ASR runtime load failed computeUnits=\(unitsLabel, privacy: .public) errorDomain=\(failure.errorDomain, privacy: .public) errorCode=\(failure.errorCode, privacy: .public)")
                }
                if let report = Qwen3AsrRuntimeLoad.sentryReport(
                    failures: error.failures,
                    loadedOn: nil,
                    lastError: error.lastError,
                    elapsedMs: elapsedMs
                ) {
                    Qwen3AsrRuntimeLoad.send(report)
                }
                throw error
            }
            guard loadGeneration == generation else {
                throw CancellationError()
            }
            manager = mgr
            loadedDirectory = modelDirectory
            return mgr
        }

        func reset() {
            loadGeneration += 1
            manager = nil
            loadedDirectory = nil
        }
    }

    let name: String = "Qwen3 ASR"

    private let runtime = Runtime()
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "Qwen3AsrProvider")

    init() {}

    /// Drop the cached manager so the next transcription re-reads from disk.
    /// Call after the on-disk install changes (delete, re-download, variant
    /// swap) — otherwise the runtime keeps serving the stale in-memory weights
    /// loaded from a now-deleted directory.
    func invalidateRuntime() async {
        await runtime.reset()
    }

    private static func resolvedModelDirectory() throws -> URL {
        if Qwen3AsrModels.modelsExist(at: Qwen3AsrModels.defaultCacheDirectory(variant: .f32)) {
            return Qwen3AsrModels.defaultCacheDirectory(variant: .f32)
        } else if Qwen3AsrModels.modelsExist(at: Qwen3AsrModels.defaultCacheDirectory(variant: .int8)) {
            return Qwen3AsrModels.defaultCacheDirectory(variant: .int8)
        }
        throw TranscriptionError.modelNotDownloaded
    }

    var isAvailable: Bool {
        (try? Self.resolvedModelDirectory()) != nil
    }

    func isAvailable(for modelId: String) -> Bool {
        isAvailable
    }

    func prepareIfNeeded(language: String?, modelId: String? = nil) async throws {
        let directory = try Self.resolvedModelDirectory()

        do {
            _ = try await runtime.ensureLoaded(modelDirectory: directory)
            logger.info("Qwen3 ASR runtime ready")
        } catch is Qwen3AsrRuntimeLoad.AllAttemptsFailedError {
            // Logged and sent to Sentry, with each attempt's real domain and
            // code, inside `ensureLoaded`.
            await runtime.reset()
            throw Qwen3AsrRuntimeLoad.userFacingError()
        } catch {
            let nsError = error as NSError
            logger.error("Failed to initialize Qwen3 ASR; errorDomain=\(nsError.domain, privacy: .public) errorCode=\(nsError.code, privacy: .public)")
            await runtime.reset()
            throw TranscriptionError.providerNotAvailable(provider: "Qwen3 ASR", reason: "Failed to initialize runtime")
        }
    }

    func transcribe(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary]) async throws -> String {
        let fm = FileManager.default
        guard fm.fileExists(atPath: audioURL.path) else {
            logger.error("Qwen3 ASR audio file not found")
            throw TranscriptionError.providerNotAvailable(provider: "Qwen3 ASR", reason: "Audio file not found")
        }

        guard fm.isReadableFile(atPath: audioURL.path) else {
            logger.error("Qwen3 ASR audio file not readable")
            throw TranscriptionError.providerNotAvailable(provider: "Qwen3 ASR", reason: "Audio file is not readable")
        }

        if let attrs = try? fm.attributesOfItem(atPath: audioURL.path),
           let size = attrs[.size] as? Int64, size < 5000 {
            logger.error("Audio file too small: \(size) bytes")
            throw TranscriptionError.providerNotAvailable(provider: "Qwen3 ASR", reason: "Audio file is too small (\(size) bytes). Please record for longer.")
        }

        let directory = try Self.resolvedModelDirectory()

        let manager: Qwen3AsrManager
        do {
            manager = try await runtime.ensureLoaded(modelDirectory: directory)
        } catch is Qwen3AsrRuntimeLoad.AllAttemptsFailedError {
            await runtime.reset()
            throw Qwen3AsrRuntimeLoad.userFacingError()
        } catch {
            let nsError = error as NSError
            logger.error("Failed to load Qwen3 ASR runtime; errorDomain=\(nsError.domain, privacy: .public) errorCode=\(nsError.code, privacy: .public)")
            await runtime.reset()
            throw TranscriptionError.providerNotAvailable(provider: "Qwen3 ASR", reason: "Failed to load runtime")
        }

        let audioSamples: [Float]
        do {
            audioSamples = try LocalAudioSampleLoader.loadMono16kSamples(from: audioURL, providerName: "Qwen3 ASR", logger: logger)
        } catch {
            let nsError = error as NSError
            logger.error("Qwen3 ASR audio conversion failed; errorDomain=\(nsError.domain, privacy: .public) errorCode=\(nsError.code, privacy: .public)")
            throw TranscriptionError.providerNotAvailable(provider: "Qwen3 ASR", reason: "Audio conversion failed: \(error.localizedDescription)")
        }

        let effectiveLanguage = mode?.language ?? language
        let langHint: String? = (effectiveLanguage == nil || effectiveLanguage == "auto") ? nil : effectiveLanguage

        do {
            var text = try await manager.transcribe(audioSamples: audioSamples, language: langHint)
            if !vocabulary.isEmpty {
                text = VocabularyProcessor.applySubstringVocabulary(to: text, vocabulary: vocabulary)
            }
            return text.trimmingCharacters(in: .whitespacesAndNewlines)
        } catch {
            // `Task.isCancelled` is task-local: read it once here, at the catch
            // site, and hand the value to the policy — the policy never reads it.
            let isTaskCancelled = Task.isCancelled

            // A cancellation that the caller actually asked for is benign: the
            // pipeline already maps `CancellationError` to `.idle` without a
            // Sentry capture. Re-wrapping it as `.providerNotAvailable` is what
            // defeated that and produced HYPERWHISPER-SQ. Note this is NOT the
            // same as a bare `CancellationError` — see TranscriptionCancellationPolicy.
            //
            // Nothing to release first here: this provider's manager is cached on
            // the `Runtime` actor across calls and takes no per-call residency
            // claim, so the catch owns no cleanup on either exit.
            if TranscriptionCancellationPolicy.outcome(
                for: error,
                isTaskCancelled: isTaskCancelled
            ) == .genuineCancellation {
                logger.info("Qwen3 ASR transcription cancelled by the caller")
                throw CancellationError()
            }

            let errorDescription = error.localizedDescription
            let nsError = error as NSError
            logger.error("Qwen3 ASR transcription failed; errorDomain=\(nsError.domain, privacy: .public) errorCode=\(nsError.code, privacy: .public)")

            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "Qwen3 ASR transcription error",
                    category: "qwen3asr.transcription",
                    level: .error,
                    data: [
                        "errorDomain": nsError.domain,
                        "errorCode": nsError.code,
                        // Not the file NAME: the import flow makes it the user's
                        // own document name. The extension is the diagnostic part.
                        "audioFileExtension": audioURL.pathExtension,
                        "language": langHint ?? "auto"
                    ]
                )
            }

            throw TranscriptionError.providerNotAvailable(
                provider: "Qwen3 ASR",
                reason: "Transcription failed: \(errorDescription)"
            )
        }
    }
}
