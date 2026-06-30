import Foundation
import AVFoundation
import os
import FluidAudio

// NEMOTRON BATCH PROVIDER:
//
// Wraps FluidAudio's `StreamingNemotronMultilingualAsrManager` as a batch
// `TranscriptionProvider`. Even though the model is a streaming RNN-T under the hood,
// the provider contract here is "give me a finished audio file, return the final
// transcript" — we just feed the whole file in via `process(samples:)` and then call
// `finish()` to flush the tail.
//
// Two on-disk variants share this provider:
//   - nemotron-asr-3.5-latin         → ~6 Latin-script languages, smaller/faster
//   - nemotron-asr-3.5-multilingual  → ~40 languages incl. zh/ja/ko/ar
//
// Variant selection comes from `mode.model`; the language hint comes from
// `mode.language` (passed through `setLanguage(_:)` once per transcription).

@available(macOS 14.0, *)
final class NemotronProvider: TranscriptionProvider {

    // RUNTIME ACTOR:
    // Caches the shared model bundle PER VARIANT. Each transcribe() call mints
    // its own `StreamingNemotronMultilingualAsrManager` from the cached bundle —
    // FluidAudio explicitly blesses this pattern (`SharedNemotronMultilingualModels`
    // is Sendable, MLModel prediction is thread-safe, loadFromShared is cheap).
    //
    // Why per-call manager:
    // 1. Two modes pinned to different variants don't fight over a single cached
    //    manager (B1: previously a V2-bound load could be returned to a V3-bound
    //    caller because the loadTask key didn't carry the variant).
    // 2. A foreground recording and a Local API batch call no longer share
    //    accumulated decoder state (B2).
    // 3. The per-call manager is freed at end of transcribe() via cleanup(), so
    //    no decoder state survives across calls (B3).
    private actor Runtime {
        private var sharedByVariant: [NemotronModelManager.Variant: SharedNemotronMultilingualModels] = [:]
        private var loadTasks: [NemotronModelManager.Variant: Task<SharedNemotronMultilingualModels, Error>] = [:]

        func currentShared(for variant: NemotronModelManager.Variant) async throws -> SharedNemotronMultilingualModels {
            if let cached = sharedByVariant[variant] { return cached }
            if let inFlight = loadTasks[variant] { return try await inFlight.value }

            let task = Task<SharedNemotronMultilingualModels, Error> {
                try await StreamingNemotronMultilingualAsrManager.downloadAndPreloadShared(
                    languageCode: variant.downloadLanguageHint,
                    chunkMs: NemotronModelManager.Constants.chunkMs,
                    to: nil,
                    configuration: nil,
                    progressHandler: nil
                )
            }
            loadTasks[variant] = task
            do {
                let shared = try await task.value
                loadTasks[variant] = nil
                sharedByVariant[variant] = shared
                return shared
            } catch {
                loadTasks[variant] = nil
                throw error
            }
        }

        func reset() async {
            sharedByVariant.removeAll()
            for (_, task) in loadTasks {
                task.cancel()
            }
            loadTasks.removeAll()
        }

        /// Drop the cached shared bundle for a single variant and cancel any
        /// in-flight load task for it. Used after `deleteModel` so the next
        /// `transcribe` / streaming session re-downloads and re-preloads from
        /// disk instead of returning the stale in-memory bundle.
        func invalidate(variant: NemotronModelManager.Variant) async {
            if let task = loadTasks.removeValue(forKey: variant) {
                task.cancel()
            }
            sharedByVariant.removeValue(forKey: variant)
        }
    }

    let name: String = "Nemotron 3.5"

    private let runtime = Runtime()
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "NemotronProvider")

    /// Optional handle on the model manager used for actionable error
    /// surfacing — when a load fails on a variant that the metadata-only
    /// probe said was installed, we flip a "broken" flag the Library row
    /// reads to show "Re-download" instead of leaving the user guessing.
    weak var modelManager: NemotronModelManager?

    init() {}

    var isAvailable: Bool {
        // Either variant counts as "available" for the generic provider check;
        // per-model availability is checked by `isAvailable(for:)`.
        NemotronModelManager.isVariantInstalled(NemotronModelManager.Constants.latinModelId)
            || NemotronModelManager.isVariantInstalled(NemotronModelManager.Constants.multilingualModelId)
    }

    func isAvailable(for modelId: String) -> Bool {
        NemotronModelManager.isVariantInstalled(modelId)
    }

    func prepareIfNeeded(language: String?, modelId: String? = nil) async throws {
        guard let modelId, let variant = NemotronModelManager.variant(forModelId: modelId) else {
            logger.error("prepareIfNeeded called without a Nemotron modelId")
            throw TranscriptionError.providerNotAvailable(provider: "Nemotron", reason: "Missing Nemotron model id")
        }
        guard isAvailable(for: modelId) else {
            logger.error("Nemotron \(variant.rawValue, privacy: .public) not downloaded")
            throw TranscriptionError.modelNotDownloaded
        }
        do {
            _ = try await runtime.currentShared(for: variant)
            logger.info("Nemotron \(variant.rawValue, privacy: .public) shared models ready")
            await clearBrokenFlag(for: modelId)
        } catch {
            logger.error("Failed to initialize Nemotron \(variant.rawValue, privacy: .public): \(error.localizedDescription, privacy: .public)")
            await runtime.reset()
            await flagBroken(for: modelId)
            throw TranscriptionError.providerNotAvailable(provider: "Nemotron", reason: "Failed to initialize Nemotron runtime")
        }
    }

    /// Public, cache-reusing access to the preloaded `SharedNemotronMultilingualModels`
    /// for a given variant. Streaming sessions share the Runtime actor's cache so
    /// the first PTT after a batch transcribe (or another streaming session) skips
    /// the ~1–3 s CoreML compile+load.
    func sharedModels(for variant: NemotronModelManager.Variant) async throws -> SharedNemotronMultilingualModels {
        try await runtime.currentShared(for: variant)
    }

    /// Drop a variant from the Runtime cache. Call after the on-disk install
    /// changes (delete, redownload) so the next session re-reads from disk
    /// instead of returning a stale bundle.
    func invalidateRuntime(for variant: NemotronModelManager.Variant) async {
        await runtime.invalidate(variant: variant)
    }

    private func flagBroken(for modelId: String) async {
        await MainActor.run { [weak modelManager] in
            modelManager?.markVariantBroken(modelId)
        }
    }

    private func clearBrokenFlag(for modelId: String) async {
        await MainActor.run { [weak modelManager] in
            modelManager?.clearVariantBroken(modelId)
        }
    }

    func transcribe(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary]) async throws -> String {
        guard let modelId = mode?.model,
              let variant = NemotronModelManager.variant(forModelId: modelId)
        else {
            logger.error("transcribe called without a Nemotron modelId")
            throw TranscriptionError.providerNotAvailable(provider: "Nemotron", reason: "Mode is missing a Nemotron model")
        }

        // PRE-FLIGHT FILE VALIDATION (matches Parakeet path):
        let fm = FileManager.default
        guard fm.fileExists(atPath: audioURL.path) else {
            logger.error("Audio file not found: \(audioURL.lastPathComponent, privacy: .public)")
            throw TranscriptionError.providerNotAvailable(provider: "Nemotron", reason: "Audio file not found")
        }
        guard fm.isReadableFile(atPath: audioURL.path) else {
            logger.error("Audio file not readable: \(audioURL.lastPathComponent, privacy: .public)")
            throw TranscriptionError.providerNotAvailable(provider: "Nemotron", reason: "Audio file is not readable")
        }
        if let attrs = try? fm.attributesOfItem(atPath: audioURL.path),
           let size = attrs[.size] as? Int64, size < 5000 {
            logger.error("Audio file too small: \(size) bytes")
            throw TranscriptionError.providerNotAvailable(provider: "Nemotron", reason: "Audio file is too small (\(size) bytes). Please record for longer.")
        }

        guard isAvailable(for: modelId) else {
            logger.error("Nemotron \(variant.rawValue, privacy: .public) transcription requested without model installed")
            throw TranscriptionError.modelNotDownloaded
        }

        let shared: SharedNemotronMultilingualModels
        do {
            shared = try await runtime.currentShared(for: variant)
        } catch {
            logger.error("Failed to load Nemotron \(variant.rawValue, privacy: .public) shared models: \(error.localizedDescription, privacy: .public)")
            await runtime.reset()
            await flagBroken(for: modelId)
            throw TranscriptionError.providerNotAvailable(provider: "Nemotron", reason: "Failed to load Nemotron runtime")
        }

        // Resolve language: mode.language wins, falling back to `language` arg, then "auto".
        // FluidAudio's `promptId(forLanguage:)` accepts bare codes ("en") and BCP-47
        // forms ("en-US") — we just pass through whatever the mode has.
        let effective = (mode?.language?.isEmpty == false ? mode?.language : nil) ?? language
        let langHint: String? = (effective == nil || effective == "auto") ? nil : effective

        // Mint a fresh per-call manager from the cached shared bundle. Concurrent
        // calls for the SAME variant won't fight over decoder state; concurrent
        // calls for DIFFERENT variants don't churn the cache because each variant
        // has its own slot. Cleanup at end-of-call frees the per-stream buffers.
        let manager = StreamingNemotronMultilingualAsrManager()
        do {
            try await manager.loadFromShared(shared)
        } catch {
            logger.error("loadFromShared failed for Nemotron \(variant.rawValue, privacy: .public): \(error.localizedDescription, privacy: .public)")
            await manager.cleanup()
            await flagBroken(for: modelId)
            throw TranscriptionError.providerNotAvailable(provider: "Nemotron", reason: "Failed to load Nemotron runtime")
        }
        await manager.setLanguage(langHint)

        let samples: [Float]
        do {
            samples = try Self.loadAudioSamples(from: audioURL)
        } catch {
            logger.error("Audio conversion failed: \(error.localizedDescription, privacy: .public)")
            await manager.cleanup()
            throw TranscriptionError.providerNotAvailable(provider: "Nemotron", reason: "Audio conversion failed: \(error.localizedDescription)")
        }

        do {
            _ = try await manager.process(samples: samples)
            var text = try await manager.finish()
            await manager.cleanup()

            if !vocabulary.isEmpty {
                let phoneticMatcher = PhoneticVocabularyMatcher(vocabulary: vocabulary)
                text = phoneticMatcher.apply(to: text)
                text = applyVocabulary(text, vocabulary: vocabulary)
            }
            return text.trimmingCharacters(in: .whitespacesAndNewlines)
        } catch {
            await manager.cleanup()
            let errorDescription = error.localizedDescription
            let errorType = String(describing: type(of: error))
            logger.error("Nemotron \(variant.rawValue, privacy: .public) transcription failed: \(errorType, privacy: .public) - \(errorDescription, privacy: .public)")

            var diagnostic: [String: Any] = [
                "errorType": errorType,
                "errorDescription": errorDescription,
                "variant": variant.rawValue,
                "modelId": modelId,
                "audioFile": audioURL.lastPathComponent
            ]
            if let attrs = try? fm.attributesOfItem(atPath: audioURL.path),
               let size = attrs[.size] as? Int64 {
                diagnostic["fileSizeBytes"] = size
            }
            diagnostic["languageParam"] = language ?? "nil"
            diagnostic["modeLanguage"] = mode?.language ?? "nil"
            diagnostic["modeModel"] = mode?.model ?? "nil"
            diagnostic["vocabularyCount"] = vocabulary.count

            SentryService.addBreadcrumb(
                message: "Nemotron transcription error",
                category: "nemotron.transcription",
                level: .error,
                data: diagnostic
            )

            throw TranscriptionError.providerNotAvailable(provider: "Nemotron", reason: "Transcription failed: \(errorDescription)")
        }
    }

    // Load audio file and convert to 16kHz mono Float32 samples.
    // Same converter recipe as Qwen3AsrProvider — Nemotron expects 16 kHz mono Float.
    private static func loadAudioSamples(from url: URL) throws -> [Float] {
        let file = try AVAudioFile(forReading: url)
        let targetFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16000, channels: 1, interleaved: false)!

        guard let converter = AVAudioConverter(from: file.processingFormat, to: targetFormat) else {
            throw TranscriptionError.providerNotAvailable(provider: "Nemotron", reason: "Cannot create audio converter")
        }

        let ratio = 16000.0 / file.processingFormat.sampleRate
        let estimatedFrames = AVAudioFrameCount(Double(file.length) * ratio) + 1024
        guard let outputBuffer = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: estimatedFrames) else {
            throw TranscriptionError.providerNotAvailable(provider: "Nemotron", reason: "Cannot allocate audio buffer")
        }

        guard let inputBuffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: 4096) else {
            throw TranscriptionError.providerNotAvailable(provider: "Nemotron", reason: "Cannot allocate input buffer for processing format \(file.processingFormat)")
        }

        var error: NSError?
        converter.convert(to: outputBuffer, error: &error) { _, outStatus in
            do {
                try file.read(into: inputBuffer)
                if inputBuffer.frameLength == 0 {
                    outStatus.pointee = .endOfStream
                    return nil
                }
                outStatus.pointee = .haveData
                return inputBuffer
            } catch {
                outStatus.pointee = .endOfStream
                return nil
            }
        }

        if let error {
            throw error
        }

        guard let channelData = outputBuffer.floatChannelData?[0] else {
            throw TranscriptionError.providerNotAvailable(provider: "Nemotron", reason: "No audio data after conversion")
        }

        return Array(UnsafeBufferPointer(start: channelData, count: Int(outputBuffer.frameLength)))
    }

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
