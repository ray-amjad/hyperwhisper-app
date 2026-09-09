import Foundation
import AVFoundation
import os
import FluidAudio

// Stable residency id for the batch Parakeet runtime — one manager is resident
// at a time, so a V2→V3 switch reuses this slot. Shared by the Runtime (load/
// release + registration) and `transcribe` (busy markers).
private let parakeetResidencyId = "stt.parakeet"

// PARAKEET PROVIDER:
// TranscriptionProvider implementation for NVIDIA's Parakeet ASR models
// Supports both V2 (English-only) and V3 (Multilingual) with version-aware loading
// Automatically switches runtime when mode uses a different version
@available(macOS 13.0, *)
final class ParakeetProvider: TranscriptionProvider {

    enum ReportedFailureReason {
        static let initializeRuntime = "Failed to initialize Parakeet runtime"
        static let loadRuntime = "Failed to load Parakeet runtime"
        static let unknownModelPrefix = "Unknown Parakeet model '"
    }

    // RUNTIME ACTOR:
    // Manages the AsrManager singleton with version tracking
    // Reloads the manager when switching between V2 and V3 models
    private actor Runtime {
        private var manager: AsrManager?
        // VARIANT-KEYED LOAD TASKS:
        // Keyed by AsrModelVersion so an in-flight V2 load can't be returned
        // to a caller asking for V3 (and vice versa). The previous single-task
        // shape returned whatever version was loading regardless of what was
        // requested.
        private var loadTasks: [AsrModelVersion: Task<AsrManager, Error>] = [:]
        private var activeVersion: AsrModelVersion?  // Tracks which version is loaded
        // INVALIDATION GENERATION:
        // Bumped by reset()/invalidate(version:). A load that was already in
        // flight when an invalidation landed detects the bump on resume and
        // discards its result instead of caching it. Without this, deleting a
        // model while its load is awaiting would repopulate `manager`/
        // `activeVersion` with the pre-delete weights once the load finishes
        // (cancel() only helps if downloadAndLoad cooperatively throws).
        private var generation = 0

        private let memoryLog = Logger(subsystem: "com.hyperwhisper.app", category: "memory")

        // VERSION-AWARE MANAGER ACCESS:
        // Returns cached manager if version matches, otherwise reloads
        // This ensures the correct model is loaded for transcription
        func currentManager(for version: AsrModelVersion) async throws -> AsrManager {
            // STEP 1: Return cached manager if version matches
            if let manager, activeVersion == version {
                // Cache hit — NOT a load. (The provider-level "runtime ready" log
                // fires on this path too, so this distinguishes a warm reuse from
                // an actual cold load in the telemetry.)
                memoryLog.info("model.cache.hit id=\(parakeetResidencyId, privacy: .public)")
                return manager
            }

            // STEP 2: Wait for an existing load task for THIS version.
            if let inFlight = loadTasks[version] {
                return try await inFlight.value
            }

            // STEP 2.5: If ANOTHER version is already loading, drain it before
            // proceeding. Without this, two near-simultaneous requests for
            // different versions would both see `activeVersion == nil` (because
            // neither load has finished yet), each spawn its own load task, and
            // overlap — the second to finish overwrites `manager`/`activeVersion`,
            // orphaning the first ~700 MB manager instance. Awaiting the in-flight
            // task serializes version transitions without a global lock.
            let anotherVersionWasLoading = !loadTasks.isEmpty
            for (otherVersion, otherTask) in loadTasks where otherVersion != version {
                _ = try? await otherTask.value
            }

            // STEP 3: Reset if switching versions (only after we know we'll
            // start a new load — switching while ANOTHER version is mid-load
            // would orphan that task).
            let activeVersionBeforeLoad = activeVersion
            if activeVersion != nil && activeVersion != version {
                await reset()
            }

            // STEP 4: Create new load task keyed to this version.
            // Capture the generation now so an invalidate() that lands while
            // the load is in flight makes us discard the result below.
            let loadGeneration = generation
            let coldLoadStart = Date()
            let task = Task<AsrManager, Error> {
                // Use version-specific loading API from FluidAudio
                let models = try await AsrModels.downloadAndLoad(version: version)
                // FluidAudio 0.15.x: AsrManager became an `actor`; legacy
                // `initialize(models:)` and `resetDecoderState()` are gone. Pass
                // models at init and clear the shared ML array cache via reset().
                let manager = AsrManager(config: .default, models: models)
                await manager.reset()
                return manager
            }

            loadTasks[version] = task
            var failureStage = "download_and_load"
            do {
                let value = try await task.value
                // Only clear our own entry — an invalidate() during the await
                // may have removed it and a newer load may now occupy the key.
                if loadTasks[version] == task {
                    loadTasks[version] = nil
                }
                // GENERATION CHECK:
                // If the version was invalidated (model deleted) while this
                // load was awaiting, do NOT cache the stale result.
                guard generation == loadGeneration else {
                    failureStage = "invalidated_after_load"
                    await value.cleanup()
                    throw CancellationError()
                }
                manager = value
                activeVersion = version  // Track which version is loaded

                // Telemetry: a genuine COLD load (distinct from the cache hits above).
                let coldMs = Int(Date().timeIntervalSince(coldLoadStart) * 1000)
                memoryLog.info("model.load.cold id=\(parakeetResidencyId, privacy: .public) version=\(String(describing: version), privacy: .public) durationMs=\(coldMs, privacy: .public) footprintMB=\(MemoryFootprint.currentMB(), privacy: .public)")

                // Register for memory-pressure eviction. Stable id, so this also
                // overwrites the prior entry on a version switch. Weak capture so
                // the registry never keeps this Runtime alive.
                await ModelResidencyRegistry.shared.register(id: parakeetResidencyId, tier: .stt) { [weak self] in
                    await self?.reset()
                }
                return value
            } catch {
                if loadTasks[version] == task {
                    loadTasks[version] = nil
                }
                let elapsedMs = Int(Date().timeIntervalSince(coldLoadStart) * 1000)
                let nsError = error as NSError
                let isCancellation = error is CancellationError
                    || (nsError.domain == NSURLErrorDomain
                        && nsError.code == URLError.cancelled.rawValue)
                if isCancellation && failureStage == "download_and_load" {
                    failureStage = "load_cancelled"
                }
                let requestedVersion = String(describing: version)
                let previousVersion = activeVersionBeforeLoad.map { String(describing: $0) } ?? "none"

                if isCancellation {
                    memoryLog.info(
                        "model.load.cancelled id=\(parakeetResidencyId, privacy: .public) stage=\(failureStage, privacy: .public) version=\(requestedVersion, privacy: .public) previousVersion=\(previousVersion, privacy: .public) durationMs=\(elapsedMs, privacy: .public)"
                    )
                    if AppLogger.isErrorLoggingEnabled {
                        let cachePresentAfterFailure = AsrModels.modelsExist(
                            at: AsrModels.defaultCacheDirectory(for: version)
                        )
                        SentryService.captureMessage(
                            "Parakeet runtime load cancelled",
                            level: .info,
                            extras: Self.failureExtras(
                                stage: failureStage,
                                elapsedMs: elapsedMs,
                                generation: loadGeneration,
                                anotherVersionWasLoading: anotherVersionWasLoading,
                                cachePresent: cachePresentAfterFailure,
                                errorCode: nsError.code
                            ),
                            tags: Self.failureTags(
                                requestedVersion: requestedVersion,
                                previousVersion: previousVersion,
                                errorDomain: nsError.domain
                            ),
                            includeRecentLogs: false
                        )
                    }
                } else {
                    memoryLog.error(
                        "model.load.failed id=\(parakeetResidencyId, privacy: .public) stage=\(failureStage, privacy: .public) version=\(requestedVersion, privacy: .public) previousVersion=\(previousVersion, privacy: .public) durationMs=\(elapsedMs, privacy: .public) errorDomain=\(nsError.domain, privacy: .public) errorCode=\(nsError.code, privacy: .public)"
                    )
                    if AppLogger.isErrorLoggingEnabled {
                        let cachePresentAfterFailure = AsrModels.modelsExist(
                            at: AsrModels.defaultCacheDirectory(for: version)
                        )
                        SentryService.capture(
                            error: SentryService.identifierOnlyError(error),
                            message: "Parakeet runtime load failed",
                            extras: Self.failureExtras(
                                stage: failureStage,
                                elapsedMs: elapsedMs,
                                generation: loadGeneration,
                                anotherVersionWasLoading: anotherVersionWasLoading,
                                cachePresent: cachePresentAfterFailure,
                                errorCode: nsError.code
                            ),
                            tags: Self.failureTags(
                                requestedVersion: requestedVersion,
                                previousVersion: previousVersion,
                                errorDomain: nsError.domain
                            ),
                            fingerprint: [
                                "parakeet-runtime-load",
                                requestedVersion,
                                nsError.domain,
                                String(nsError.code),
                            ],
                            includeRecentLogs: false
                        )
                    }
                }
                throw error
            }
        }

        private static func failureExtras(
            stage: String,
            elapsedMs: Int,
            generation: Int,
            anotherVersionWasLoading: Bool,
            cachePresent: Bool,
            errorCode: Int
        ) -> [String: Any] {
            [
                "parakeet_load_stage": stage,
                "parakeet_load_duration_ms": elapsedMs,
                "parakeet_load_generation": generation,
                "parakeet_another_version_was_loading": anotherVersionWasLoading,
                "parakeet_cache_present_after_failure": cachePresent,
                "parakeet_error_code": errorCode,
            ]
        }

        private static func failureTags(
            requestedVersion: String,
            previousVersion: String,
            errorDomain: String
        ) -> [String: String] {
            [
                "component": "models",
                "parakeet_model_version": requestedVersion,
                "parakeet_previous_version": previousVersion,
                "parakeet_error_domain": errorDomain,
            ]
        }

        func reset() async {
            generation += 1  // Invalidate any in-flight loads (see GENERATION CHECK)
            await manager?.cleanup()
            manager = nil
            loadTasks.removeAll()
            activeVersion = nil
            await ModelResidencyRegistry.shared.deregister(id: parakeetResidencyId)
        }

        /// Drop the cached manager and any in-flight load for a single version.
        /// Used after `deleteModel` so the next transcription re-loads from
        /// disk instead of returning the stale in-memory manager.
        func invalidate(version: AsrModelVersion) async {
            generation += 1  // Invalidate any in-flight loads (see GENERATION CHECK)
            if let task = loadTasks.removeValue(forKey: version) {
                task.cancel()
            }
            if activeVersion == version {
                await manager?.cleanup()
                manager = nil
                activeVersion = nil
                await ModelResidencyRegistry.shared.deregister(id: parakeetResidencyId)
            }
        }

        func isLoaded() -> Bool {
            manager != nil
        }

        func currentVersion() -> AsrModelVersion? {
            activeVersion
        }
    }

    let name: String = "Parakeet TDT"

    private let runtime = Runtime()
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "ParakeetProvider")

    init() {}

    // VERSION DETECTION HELPER:
    // Determines AsrModelVersion from model ID string
    // Matches the pattern used in ParakeetModelManager
    private func version(for modelId: String) -> AsrModelVersion? {
        guard let canonicalModelId = ParakeetModelManager.Constants.canonicalModelId(for: modelId) else {
            return nil
        }
        return canonicalModelId == ParakeetModelManager.Constants.v2ModelId ? .v2 : .v3
    }

    /// Report an unsupported model identifier without sending arbitrary text.
    /// A numeric version suffix is enough to identify stale catalog entries.
    private func reportUnknownModel(_ modelId: String) {
        let versionPrefix = "parakeet-tdt-0.6b-v"
        let versionNumber = modelId.hasPrefix(versionPrefix)
            ? Int(modelId.dropFirst(versionPrefix.count))
            : nil
        let identifierClass = versionNumber == nil ? "unrecognized_format" : "unsupported_version"

        logger.error("Parakeet rejected an unknown model id; idLength=\(modelId.count, privacy: .public) class=\(identifierClass, privacy: .public)")
        guard AppLogger.isErrorLoggingEnabled else { return }

        var extras: [String: Any] = [
            "parakeet_load_stage": "model_resolution",
            "parakeet_model_id_length": modelId.count,
        ]
        if let versionNumber {
            extras["parakeet_unknown_model_version"] = versionNumber
        }
        SentryService.captureMessage(
            "Parakeet preparation rejected",
            level: .error,
            extras: extras,
            tags: [
                "component": "models",
                "parakeet_failure_kind": "unknown_model",
                "parakeet_unknown_model_class": identifierClass,
            ],
            includeRecentLogs: false
        )
    }

    // ANY VERSION AVAILABLE:
    // Returns true if any Parakeet version is downloaded
    var isAvailable: Bool {
        let v2Available = AsrModels.modelsExist(at: AsrModels.defaultCacheDirectory(for: .v2))
        let v3Available = AsrModels.modelsExist(at: AsrModels.defaultCacheDirectory(for: .v3))
        return v2Available || v3Available
    }

    // SPECIFIC VERSION AVAILABLE:
    // Returns true if the specified model is downloaded
    func isAvailable(for modelId: String) -> Bool {
        guard let targetVersion = version(for: modelId) else {
            return false
        }
        return AsrModels.modelsExist(at: AsrModels.defaultCacheDirectory(for: targetVersion))
    }

    /// Drop a version from the Runtime cache. Call after the on-disk install
    /// changes (delete, redownload) so the next transcription re-reads from
    /// disk instead of serving a stale in-memory `AsrManager`.
    func invalidateRuntime(for modelVersion: AsrModelVersion) async {
        await runtime.invalidate(version: modelVersion)
    }

    // PREPARE VERSION-SPECIFIC RUNTIME:
    // Loads the specified Parakeet version into memory
    // If no modelId provided, defaults to V3 for backward compatibility
    func prepareIfNeeded(language: String?, modelId: String? = nil) async throws {
        let targetVersion: AsrModelVersion
        if let modelId {
            guard let version = version(for: modelId) else {
                reportUnknownModel(modelId)
                throw TranscriptionError.providerNotAvailable(
                    provider: "Parakeet",
                    reason: Self.ReportedFailureReason.unknownModelPrefix + modelId + "'"
                )
            }
            targetVersion = version
        } else {
            // Default to V3 for backward compatibility
            targetVersion = .v3
        }

        // Verify model is downloaded
        let directory = AsrModels.defaultCacheDirectory(for: targetVersion)
        guard AsrModels.modelsExist(at: directory) else {
            logger.error("Parakeet \(String(describing: targetVersion)) not downloaded")
            throw TranscriptionError.modelNotDownloaded
        }

        // Load the runtime for this version
        do {
            _ = try await runtime.currentManager(for: targetVersion)
            logger.info("Parakeet \(String(describing: targetVersion)) runtime ready")
        } catch {
            let nsError = error as NSError
            logger.error("Failed to initialize Parakeet \(String(describing: targetVersion)); errorDomain=\(nsError.domain, privacy: .public) errorCode=\(nsError.code, privacy: .public)")
            await runtime.reset()
            throw TranscriptionError.providerNotAvailable(
                provider: "Parakeet",
                reason: Self.ReportedFailureReason.initializeRuntime
            )
        }
    }

    // VERSION-AWARE TRANSCRIPTION:
    // Uses the model specified in the mode, defaulting to V3
    // Automatically switches runtime if mode uses different version
    func transcribe(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary]) async throws -> String {
        // STEP 1: Determine which version to use from mode
        let modelId = mode?.model ?? ParakeetModelManager.Constants.v3ModelId
        guard let targetVersion = version(for: modelId) else {
            reportUnknownModel(modelId)
            throw TranscriptionError.providerNotAvailable(
                provider: "Parakeet",
                reason: Self.ReportedFailureReason.unknownModelPrefix + modelId + "'"
            )
        }

        // STEP 2: Verify model is downloaded
        let directory = AsrModels.defaultCacheDirectory(for: targetVersion)
        guard AsrModels.modelsExist(at: directory) else {
            logger.error("Parakeet \(String(describing: targetVersion)) transcription requested without model installed")
            throw TranscriptionError.modelNotDownloaded
        }

        // STEP 2.5: PRE-FLIGHT AUDIO FILE VALIDATION
        // Catch obvious issues early before initializing the model runtime
        // This prevents confusing "audio corrupted" errors when the real issue is simpler
        let fm = FileManager.default
        guard fm.fileExists(atPath: audioURL.path) else {
            logger.error("Parakeet audio file not found")
            throw TranscriptionError.providerNotAvailable(provider: "Parakeet", reason: "Audio file not found")
        }

        guard fm.isReadableFile(atPath: audioURL.path) else {
            logger.error("Parakeet audio file not readable")
            throw TranscriptionError.providerNotAvailable(provider: "Parakeet", reason: "Audio file is not readable")
        }

        // Check minimum file size (at least 5KB for meaningful audio)
        // A 16kHz mono 16-bit WAV needs ~32KB per second, so 5KB is ~0.15 seconds
        if let attrs = try? fm.attributesOfItem(atPath: audioURL.path),
           let size = attrs[.size] as? Int64, size < 5000 {
            logger.error("Audio file too small: \(size) bytes")
            throw TranscriptionError.providerNotAvailable(provider: "Parakeet", reason: "Audio file is too small (\(size) bytes). Please record for longer.")
        }

        // STEP 2.6: V2 LANGUAGE VALIDATION
        // Parakeet V2 only supports English - catch language mismatches early
        // with a clear error message instead of a confusing FluidAudio failure
        if targetVersion == .v2 {
            let effectiveLanguage = mode?.language ?? language ?? "en"
            // V2 supports: "en" (English) and "auto" (will default to English)
            if effectiveLanguage != "en" && effectiveLanguage != "auto" {
                logger.error("Parakeet V2 language mismatch: \(effectiveLanguage, privacy: .public)")
                throw TranscriptionError.providerNotAvailable(
                    provider: "Parakeet V2",
                    reason: "Parakeet V2 only supports English. Please switch to Parakeet V3 for other languages, or change your mode's language setting to English."
                )
            }
        }

        // STEP 3: Get/load the appropriate runtime
        let manager: AsrManager
        do {
            manager = try await runtime.currentManager(for: targetVersion)
        } catch {
            let nsError = error as NSError
            logger.error("Failed to initialize Parakeet \(String(describing: targetVersion)) runtime; errorDomain=\(nsError.domain, privacy: .public) errorCode=\(nsError.code, privacy: .public)")
            await runtime.reset()
            throw TranscriptionError.providerNotAvailable(
                provider: "Parakeet",
                reason: Self.ReportedFailureReason.loadRuntime
            )
        }

        // Mark busy so a memory-pressure event can't evict the runtime mid-pass.
        //
        // SCOPE, stated exactly. This site does NOT implement the
        // `ResidentRuntimeClaim.acquire` contract — there is no claim-then-read
        // and no reload — and bringing it up to that contract is deliberately
        // out of HYPERWHISPER-SQ's scope, which is the whisper.cpp arm. What the
        // three branches below do and do not cover:
        //
        // - `.claimed` — the ordinary path, and the only one that owes a
        //   `markIdle`. A second concurrent claimer reaches this same shared
        //   provider (`TranscribeEndpoint`), so a release after a REFUSED claim
        //   would be a release this pass has no token for. Hence the optional
        //   token on both exits below: no token, no release.
        // - `.evicting` — HANDLED, by failing instead of transcribing. `manager`
        //   was fetched above, before the claim, and `ParakeetRuntime.reset()`
        //   suspends inside `await manager?.cleanup()` while still holding the
        //   reference — and `Runtime` is a reentrant actor, so a concurrent
        //   `currentManager(for:)` takes the cache-hit branch and hands back
        //   that same manager for the whole ~700 MB CoreML teardown. Running
        //   `transcribe` against it is transcribing on a torn-down runtime. It
        //   used to do exactly that, silently.
        // - `.notResident` — NOT handled, on purpose, and this is the residual
        //   hole. Nothing is registered under this id, which per `ClaimResult`
        //   need not mean a teardown, so the pass proceeds UNCLAIMED (and
        //   therefore unprotected against an eviction starting mid-pass). For
        //   Parakeet specifically that reading is weaker than it is for the
        //   whisper arm, because `currentManager` registers inside its own cold
        //   load: a missing entry here more often means a completed eviction
        //   than an unfinished registration. Closing it needs the claim-first
        //   read this site does not have.
        let parakeetReceipt = await ModelResidencyRegistry.shared.markBusy(id: parakeetResidencyId)
        if parakeetReceipt.result == .evicting {
            logger.error("Parakeet residency claim refused: the runtime is being freed under memory pressure, so the manager fetched above is mid-teardown")
            throw TranscriptionError.localSpeechModelEvicted(model: modelId)
        }
        // The token IS the "did we claim?" flag, and it is also what repays the
        // claim: a `nil` here is the `.notResident` branch below, and there is
        // no way to spell a release without one.
        let parakeetToken = parakeetReceipt.token
        if parakeetToken == nil {
            logger.notice("Parakeet is not registered for residency; proceeding unclaimed (see the note above)")
        }

        // STEP 4: Perform transcription
        // FluidAudio 0.15.x removed the per-call `source:` arg and threads
        // decoder state through `decoderState: inout` so batch jobs can run on
        // a fresh state every call (no leftover hidden state from a prior pass).
        do {
            var decoderState = try TdtDecoderState()
            let result = try await manager.transcribe(audioURL, decoderState: &decoderState, language: nil)
            var text = result.text

            // STEP 4a: Phonetic vocabulary matching (Beider-Morse)
            // Catches phonetically similar misrecognitions before exact matching
            if !vocabulary.isEmpty {
                text = VocabularyProcessor.applyPhoneticVocabulary(to: text, vocabulary: vocabulary)
            }

            // STEP 4b: Exact vocabulary replacements (case-insensitive string match)
            if !vocabulary.isEmpty {
                text = VocabularyProcessor.applySubstringVocabulary(to: text, vocabulary: vocabulary)
            }
            if let parakeetToken {
                await ModelResidencyRegistry.shared.markIdle(parakeetToken)
            }
            return text.trimmingCharacters(in: .whitespacesAndNewlines)
        } catch {
            if let parakeetToken {
                await ModelResidencyRegistry.shared.markIdle(parakeetToken)
            }

            // `Task.isCancelled` is task-local: read it once here, at the catch
            // site, and hand the value to the policy — the policy never reads it.
            //
            // Deliberately AFTER the residency release above: a cancelled pass
            // still owes its `markIdle`, or the ~700 MB runtime stays pinned
            // against memory-pressure eviction for the process lifetime.
            let isTaskCancelled = Task.isCancelled

            // A cancellation that the caller actually asked for is benign: the
            // pipeline already maps `CancellationError` to `.idle` without a
            // Sentry capture. Re-wrapping it as `.providerNotAvailable` is what
            // defeated that and produced HYPERWHISPER-SQ. Note this is NOT the
            // same as a bare `CancellationError` — see TranscriptionCancellationPolicy.
            if TranscriptionCancellationPolicy.outcome(
                for: error,
                isTaskCancelled: isTaskCancelled
            ) == .genuineCancellation {
                logger.info("Parakeet transcription cancelled by the caller")
                throw CancellationError()
            }

            // Preserve the detailed error for the user-facing result. Logs keep
            // only the error identifiers because descriptions can contain paths.
            let errorDescription = error.localizedDescription
            let nsError = error as NSError

            logger.error("Parakeet \(String(describing: targetVersion)) transcription failed; errorDomain=\(nsError.domain, privacy: .public) errorCode=\(nsError.code, privacy: .public)")

            // Keep the breadcrumb behind the error-reporting opt-in gate.
            if AppLogger.isErrorLoggingEnabled {
                var diagnosticData: [String: Any] = [
                    "errorDomain": nsError.domain,
                    "errorCode": nsError.code,
                    "modelVersion": String(describing: targetVersion),
                    "modelId": modelId,
                    "audioFileExtension": audioURL.pathExtension,
                ]
                if let attrs = try? fm.attributesOfItem(atPath: audioURL.path) {
                    if let size = attrs[.size] as? Int64 {
                        diagnosticData["fileSizeBytes"] = size
                        diagnosticData["estimatedDurationSec"] = String(
                            format: "%.2f",
                            Double(size) / 32000.0
                        )
                    }
                    if let modDate = attrs[.modificationDate] as? Date {
                        diagnosticData["fileModified"] = ISO8601DateFormatter().string(from: modDate)
                    }
                }
                diagnosticData["languageParam"] = language ?? "nil"
                diagnosticData["modeLanguage"] = mode?.language ?? "nil"
                diagnosticData["modeModel"] = mode?.model ?? "nil"
                diagnosticData["vocabularyCount"] = vocabulary.count
                SentryService.addBreadcrumb(
                    message: "FluidAudio transcription error",
                    category: "parakeet.transcription",
                    level: .error,
                    data: diagnosticData
                )
            }

            // Expose the actual error to the user instead of generic message
            throw TranscriptionError.providerNotAvailable(
                provider: "Parakeet",
                reason: "Transcription failed: \(errorDescription)"
            )
        }
    }
}
