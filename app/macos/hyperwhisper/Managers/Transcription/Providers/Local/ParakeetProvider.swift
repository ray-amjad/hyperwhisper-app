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
            for (otherVersion, otherTask) in loadTasks where otherVersion != version {
                _ = try? await otherTask.value
            }

            // STEP 3: Reset if switching versions (only after we know we'll
            // start a new load — switching while ANOTHER version is mid-load
            // would orphan that task).
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
                throw error
            }
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
    private func version(for modelId: String) -> AsrModelVersion {
        modelId.lowercased().contains("v2") ? .v2 : .v3
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
        let targetVersion = version(for: modelId)
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
            targetVersion = version(for: modelId)
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
            logger.error("Failed to initialize Parakeet \(String(describing: targetVersion)): \(error.localizedDescription)")
            await runtime.reset()
            throw TranscriptionError.providerNotAvailable(provider: "Parakeet", reason: "Failed to initialize Parakeet runtime")
        }
    }

    // VERSION-AWARE TRANSCRIPTION:
    // Uses the model specified in the mode, defaulting to V3
    // Automatically switches runtime if mode uses different version
    func transcribe(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary]) async throws -> String {
        // STEP 1: Determine which version to use from mode
        let modelId = mode?.model ?? ParakeetModelManager.Constants.v3ModelId
        let targetVersion = version(for: modelId)

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
            logger.error("Audio file not found: \(audioURL.lastPathComponent, privacy: .public)")
            throw TranscriptionError.providerNotAvailable(provider: "Parakeet", reason: "Audio file not found")
        }

        guard fm.isReadableFile(atPath: audioURL.path) else {
            logger.error("Audio file not readable: \(audioURL.lastPathComponent, privacy: .public)")
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
            logger.error("Failed to initialize Parakeet \(String(describing: targetVersion)) runtime: \(error.localizedDescription, privacy: .public)")
            await runtime.reset()
            throw TranscriptionError.providerNotAvailable(provider: "Parakeet", reason: "Failed to load Parakeet runtime")
        }

        // Mark busy so a memory-pressure event can't evict the runtime mid-pass.
        await ModelResidencyRegistry.shared.markBusy(id: parakeetResidencyId)

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
                let phoneticMatcher = PhoneticVocabularyMatcher(vocabulary: vocabulary)
                text = phoneticMatcher.apply(to: text)
            }

            // STEP 4b: Exact vocabulary replacements (case-insensitive string match)
            if !vocabulary.isEmpty {
                text = applyVocabulary(text, vocabulary: vocabulary)
            }
            await ModelResidencyRegistry.shared.markIdle(id: parakeetResidencyId)
            return text.trimmingCharacters(in: .whitespacesAndNewlines)
        } catch {
            await ModelResidencyRegistry.shared.markIdle(id: parakeetResidencyId)
            // IMPROVED ERROR HANDLING:
            // Instead of masking all errors with a generic message, expose the actual
            // FluidAudio error so users and Sentry can see what really went wrong.
            let errorDescription = error.localizedDescription
            let errorType = String(describing: type(of: error))

            logger.error("Parakeet \(String(describing: targetVersion)) transcription failed: \(errorType) - \(errorDescription, privacy: .public)")

            // COMPREHENSIVE DIAGNOSTIC DATA FOR SENTRY:
            // Collect all relevant context so we can diagnose issues without guessing
            var diagnosticData: [String: Any] = [
                "errorType": errorType,
                "errorDescription": errorDescription,
                "modelVersion": String(describing: targetVersion),
                "modelId": modelId,
                "audioFile": audioURL.lastPathComponent,
                "audioPath": audioURL.path
            ]

            // Audio file metadata
            if let attrs = try? fm.attributesOfItem(atPath: audioURL.path) {
                if let size = attrs[.size] as? Int64 {
                    diagnosticData["fileSizeBytes"] = size
                    // Estimate duration: 16kHz mono 16-bit = 32KB/sec
                    let estimatedDurationSec = Double(size) / 32000.0
                    diagnosticData["estimatedDurationSec"] = String(format: "%.2f", estimatedDurationSec)
                }
                if let modDate = attrs[.modificationDate] as? Date {
                    diagnosticData["fileModified"] = ISO8601DateFormatter().string(from: modDate)
                }
            }
            diagnosticData["fileExtension"] = audioURL.pathExtension

            // Language/mode configuration
            diagnosticData["languageParam"] = language ?? "nil"
            diagnosticData["modeName"] = mode?.name ?? "nil"
            diagnosticData["modeLanguage"] = mode?.language ?? "nil"
            diagnosticData["modeModel"] = mode?.model ?? "nil"
            diagnosticData["vocabularyCount"] = vocabulary.count

            // Log full error context to Sentry for debugging
            SentryService.addBreadcrumb(
                message: "FluidAudio transcription error",
                category: "parakeet.transcription",
                level: .error,
                data: diagnosticData
            )

            // Expose the actual error to the user instead of generic message
            throw TranscriptionError.providerNotAvailable(
                provider: "Parakeet",
                reason: "Transcription failed: \(errorDescription)"
            )
        }
    }

    // VOCABULARY POST-PROCESSING:
    // Applies custom vocabulary replacements to the transcribed text
    // Case-insensitive and diacritic-insensitive matching
    private func applyVocabulary(_ text: String, vocabulary: [Vocabulary]) -> String {
        var updated = text
        for entry in vocabulary {
            guard let word = entry.word?.trimmingCharacters(in: .whitespacesAndNewlines), !word.isEmpty else {
                continue
            }
            guard let replacement = entry.replacement?.trimmingCharacters(in: .whitespacesAndNewlines), !replacement.isEmpty else {
                continue
            }
            updated = updated.replacingOccurrences(of: word, with: replacement, options: [.caseInsensitive, .diacriticInsensitive], range: nil)
        }
        return updated
    }
}
