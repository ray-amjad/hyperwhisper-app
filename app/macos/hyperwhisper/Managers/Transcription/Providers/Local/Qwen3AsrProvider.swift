import Foundation
import AVFoundation
import os
import FluidAudio

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

        func ensureLoaded(modelDirectory: URL) async throws -> Qwen3AsrManager {
            if let manager, loadedDirectory == modelDirectory {
                return manager
            }

            // Drop the stale manager before loading the new directory so a load
            // failure can't leave a manager paired with the wrong directory.
            manager = nil
            loadedDirectory = nil

            let generation = loadGeneration
            let mgr = Qwen3AsrManager()
            try await mgr.loadModels(from: modelDirectory)
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
        } catch {
            logger.error("Failed to initialize Qwen3 ASR: \(error.localizedDescription)")
            await runtime.reset()
            throw TranscriptionError.providerNotAvailable(provider: "Qwen3 ASR", reason: "Failed to initialize runtime")
        }
    }

    func transcribe(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary]) async throws -> String {
        let fm = FileManager.default
        guard fm.fileExists(atPath: audioURL.path) else {
            logger.error("Audio file not found: \(audioURL.lastPathComponent, privacy: .public)")
            throw TranscriptionError.providerNotAvailable(provider: "Qwen3 ASR", reason: "Audio file not found")
        }

        guard fm.isReadableFile(atPath: audioURL.path) else {
            logger.error("Audio file not readable: \(audioURL.lastPathComponent, privacy: .public)")
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
        } catch {
            logger.error("Failed to load Qwen3 ASR runtime: \(error.localizedDescription, privacy: .public)")
            await runtime.reset()
            throw TranscriptionError.providerNotAvailable(provider: "Qwen3 ASR", reason: "Failed to load runtime")
        }

        let audioSamples: [Float]
        do {
            audioSamples = try Self.loadAudioSamples(from: audioURL)
        } catch {
            logger.error("Audio conversion failed: \(error.localizedDescription, privacy: .public)")
            throw TranscriptionError.providerNotAvailable(provider: "Qwen3 ASR", reason: "Audio conversion failed: \(error.localizedDescription)")
        }

        let effectiveLanguage = mode?.language ?? language
        let langHint: String? = (effectiveLanguage == nil || effectiveLanguage == "auto") ? nil : effectiveLanguage

        do {
            var text = try await manager.transcribe(audioSamples: audioSamples, language: langHint)
            if !vocabulary.isEmpty {
                text = applyVocabulary(text, vocabulary: vocabulary)
            }
            return text.trimmingCharacters(in: .whitespacesAndNewlines)
        } catch {
            let errorDescription = error.localizedDescription
            logger.error("Qwen3 ASR transcription failed: \(errorDescription, privacy: .public)")

            SentryService.addBreadcrumb(
                message: "Qwen3 ASR transcription error",
                category: "qwen3asr.transcription",
                level: .error,
                data: [
                    "errorDescription": errorDescription,
                    "audioFile": audioURL.lastPathComponent,
                    "language": langHint ?? "auto"
                ]
            )

            throw TranscriptionError.providerNotAvailable(
                provider: "Qwen3 ASR",
                reason: "Transcription failed: \(errorDescription)"
            )
        }
    }

    /// Load audio file and convert to 16kHz mono Float32 samples for Qwen3 ASR.
    private static func loadAudioSamples(from url: URL) throws -> [Float] {
        let file = try AVAudioFile(forReading: url)
        let targetFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16000, channels: 1, interleaved: false)!

        guard let converter = AVAudioConverter(from: file.processingFormat, to: targetFormat) else {
            throw TranscriptionError.providerNotAvailable(provider: "Qwen3 ASR", reason: "Cannot create audio converter")
        }

        let ratio = 16000.0 / file.processingFormat.sampleRate
        let estimatedFrames = AVAudioFrameCount(Double(file.length) * ratio) + 1024
        guard let outputBuffer = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: estimatedFrames) else {
            throw TranscriptionError.providerNotAvailable(provider: "Qwen3 ASR", reason: "Cannot allocate audio buffer")
        }

        guard let inputBuffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: 4096) else {
            throw TranscriptionError.providerNotAvailable(provider: "Qwen3 ASR", reason: "Cannot allocate input buffer for processing format \(file.processingFormat)")
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
            throw TranscriptionError.providerNotAvailable(provider: "Qwen3 ASR", reason: "No audio data after conversion")
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
