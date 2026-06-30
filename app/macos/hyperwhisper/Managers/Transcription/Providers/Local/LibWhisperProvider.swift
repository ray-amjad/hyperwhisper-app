//
//  LibWhisperProvider.swift
//  hyperwhisper
//
//  Transcription provider using libwhisper.cpp
//  Replaces LocalWhisperProvider with instant model loading (no warm-up!)
//

import Foundation
import AVFoundation
import os

// Stable residency id for the loaded whisper.cpp context (one at a time).
// Shared by load/cleanup (registration) and transcribe (busy markers).
private let libWhisperResidencyId = "stt.libwhisper"

/// LibWhisper provider for local transcription using whisper.cpp
/// Key advantage: NO WARM-UP NEEDED - models load in 2-5 seconds!
class LibWhisperProvider: TranscriptionProvider {
    // MARK: - Properties

    /// Returns the display name of the currently loaded model
    /// Falls back to "LibWhisper" if no model is loaded
    var name: String {
        currentModel?.displayName ?? "LibWhisper"
    }
    
    /// The whisper context for transcription
    private var whisperContext: WhisperContext?
    
    /// Currently loaded model
    private var currentModel: WhisperCppModel?
    
    /// Model manager for accessing downloaded models
    /// DEPENDENCY INJECTION: This is now provided from the app's shared instance
    /// to avoid state drift between UI and provider
    private let modelManager: WhisperModelManager
    
    /// Logger
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "LibWhisperProvider")
    
    /// Current transcription task
    private var transcriptionTask: Task<String, Error>?
    
    /// Pending model to load (set by setModel, loaded on transcribe)
    private var pendingModel: WhisperModel?
    
    /// Track if model is ready
    private var isModelReady: Bool = false

    /// Timestamp granularities requested for the next transcribe(...) call.
    /// Empty = text only (default; zero extra cost).
    private var requestedGranularities: TimestampGranularities = []

    /// Timestamps from the most recent transcribe(...) call, or nil when none
    /// were requested or the run produced no kept text. Read after the awaited
    /// transcribe(...) returns (serialized API path — see R5 in the plan).
    private(set) var lastTimestamps: TranscriptionTimestamps?

    // MARK: - Initialization
    
    /// Initialize with the shared model manager
    /// - Parameter modelManager: The app's shared WhisperModelManager instance
    init(modelManager: WhisperModelManager) {
        self.modelManager = modelManager
        logger.debug("🎨 LibWhisperProvider initialized with injected model manager")
    }
    
    // MARK: - TranscriptionProvider Protocol

    /// Opt into timestamp extraction for the next transcribe(...) call.
    func setTimestampGranularities(_ granularities: TimestampGranularities) {
        requestedGranularities = granularities
    }

    /// Check if provider is available
    var isAvailable: Bool {
        // Provider is available if we have a context OR if we can load a model
        let hasContext = whisperContext != nil
        let hasDownloadedModels = !modelManager.downloadedModels.isEmpty
        let available = hasContext || hasDownloadedModels
        
        logger.debug("🔍 Provider availability check: hasContext=\(hasContext), hasDownloadedModels=\(hasDownloadedModels), available=\(available)")
        return available
    }
    
    /// Initialize the provider
    func initialize() async throws {
        logger.info("🎉 LibWhisperProvider initialized")
        
        // ENSURE MODELS ARE SCANNED: Scan once during initialization
        // This ensures the model list is populated before any operations
        if modelManager.downloadedModels.isEmpty {
            logger.debug("🔍 Initial model scan...")
            await modelManager.scanDownloadedModels()
        }
    }
    
    /// Load a model for transcription
    /// - Parameter modelName: Name of the model (e.g., "tiny", "base.en")
    func loadModel(named modelName: String) async throws {
        logger.info("📦 Loading model: \(modelName)")
        
        // PERFORMANCE: Only scan if we truly have no models
        // This check is cheap compared to filesystem scanning
        if modelManager.downloadedModels.isEmpty {
            logger.debug("🔍 No models in cache, scanning...")
            await modelManager.scanDownloadedModels()
        }
        
        // Find the model in downloaded models
        guard let model = self.modelManager.downloadedModels.first(where: { $0.name == modelName }),
              let modelPath = model.url?.path else {
            logger.error("❌ Model not found: \(modelName). Available models: \(self.modelManager.downloadedModels.map { $0.name })")
            throw TranscriptionError.modelNotDownloaded
        }
        
        logger.info("🔧 Found model at path: \(modelPath)")
        
        // Release previous context if exists
        if let existingContext = whisperContext {
            logger.info("♾️ Releasing previous model context")
            await existingContext.releaseResources()
        }
        
        // Create new context with the model
        // THIS IS FAST! No warm-up needed!
        do {
            logger.info("🎨 Creating WhisperContext for \(modelName)...")
            let coldLoadStart = Date()
            whisperContext = try await WhisperContext.createContext(path: modelPath)
            currentModel = model
            isModelReady = true
            logger.info("✅ Model loaded successfully: \(modelName) - READY TO TRANSCRIBE!")

            // Telemetry + register for memory-pressure eviction. The whisper
            // context was previously never freed until a model switch; this lets
            // it be reclaimed when idle under pressure. Weak capture.
            let coldMs = Int(Date().timeIntervalSince(coldLoadStart) * 1000)
            AppLogger.memory.info("model.load.cold id=\(libWhisperResidencyId, privacy: .public) durationMs=\(coldMs, privacy: .public) footprintMB=\(MemoryFootprint.currentMB(), privacy: .public)")
            await ModelResidencyRegistry.shared.register(id: libWhisperResidencyId, tier: .stt) { [weak self] in
                await self?.cleanup()
            }
        } catch {
            logger.error("❌ Failed to load model: \(error)")
            isModelReady = false
            throw TranscriptionError.providerNotAvailable(provider: "Local Whisper", reason: "Failed to load model '\(modelName)'")
        }
    }
    
    /// Transcribe audio file
    /// - Parameters:
    ///   - audioURL: URL of the audio file
    ///   - language: Language code for transcription
    ///   - mode: Optional mode with settings
    ///   - vocabulary: Custom vocabulary list
    /// - Returns: Transcribed text
    func transcribe(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary]) async throws -> String {
        // Reset any timestamps from a previous run; only the kept run below sets them.
        lastTimestamps = nil
        // Snapshot the requested granularities for this run, then clear so they
        // don't leak into the next call on this shared provider instance — each
        // call must re-arm via setTimestampGranularities (matches "arm next call").
        let granularities = requestedGranularities
        requestedGranularities = []
        let wantTimestamps = !granularities.isEmpty
        let includeWords = granularities.contains(.word)

        // If we have a pending model but haven't loaded it yet, load it now
        if let pending = pendingModel {
            logger.info("🔄 Loading pending model before transcription: \(pending.rawValue)")
            try await loadModel(named: pending.rawValue)
            pendingModel = nil
        }
        
        guard let context = whisperContext else {
            logger.error("❌ No whisper context available for transcription")
            logger.error("   Available models: \(self.modelManager.downloadedModels.map { $0.name })")
            logger.error("   Current model: \(self.currentModel?.name ?? "none")")
            logger.error("   Model ready: \(self.isModelReady)")
            throw TranscriptionError.providerNotAvailable(provider: "Local Whisper", reason: "No model loaded. Please download a model first.")
        }
        
        logger.info("Starting transcription with model: \(self.currentModel?.name ?? "unknown")")
        
        // Cancel any existing transcription
        transcriptionTask?.cancel()

        // Claim residency BEFORE the task can start touching the context, so a
        // concurrent memory-pressure eviction can't free it mid-pass. The
        // registry is an actor: this markBusy happens-before any later evict,
        // and the task below only begins after this await returns — closing the
        // pressure/start race where a late claim left the entry briefly idle.
        await ModelResidencyRegistry.shared.markBusy(id: libWhisperResidencyId)

        // Create new transcription task
        let task = Task<String, Error> {
            // Load and convert audio to required format
            let samples = try await loadAndConvertAudio(from: audioURL)
            logger.info("🎧 Loaded audio samples: \(samples.count)")
            if samples.isEmpty {
                logger.error("❌ No audio samples after conversion")
            }

            // Early no-speech guard: avoid hallucinated text on near-silent recordings.
            if self.isLikelySilent(samples) {
                logger.info("🔇 Audio appears near-silent - returning no speech detected")
                throw TranscriptionError.noSpeechDetected
            }
            
            // Set language - always call setLanguage to ensure proper state
            if let language = language {
                // Specific language requested
                await context.setLanguage(language)
            } else if currentModel?.isEnglishOnly == true {
                // English-only model requires language to be set to "en"
                await context.setLanguage("en")
            } else {
                // Automatic language detection - pass nil to enable auto-detect
                await context.setLanguage(nil)
            }
            
            // Set custom vocabulary/prompt
            if !vocabulary.isEmpty {
                let prompt = vocabulary.filter { $0.replacement == nil || $0.replacement!.isEmpty }.map { $0.word ?? "" }.joined(separator: " ")
                await context.setPrompt(prompt)
            }
            
            // When timestamps are requested, read text + timings together from the
            // same whisper run so the captured timings always belong to the *kept*
            // text (handles the empty-output retries below). Otherwise use the
            // byte-identical text-only path. Captured timestamps are committed to
            // `self.lastTimestamps` only after a non-empty result survives.
            var capturedTimed: TranscriptionTimestamps?
            func readKeptText() async -> String {
                guard wantTimestamps else {
                    capturedTimed = nil
                    return await context.getTranscription()
                }
                let timed = await context.getTimedTranscription(includeWords: includeWords)
                capturedTimed = TranscriptionTimestamps(
                    segments: timed.segments.map {
                        TranscriptionSegmentTimestamp(id: $0.id, start: $0.start, end: $0.end, text: $0.text)
                    },
                    words: timed.words?.map {
                        TranscriptionWordTimestamp(word: $0.word, start: $0.start, end: $0.end, probability: $0.probability)
                    },
                    rawText: timed.rawText
                )
                return timed.cleanedText
            }

            // Perform transcription
            var success = await context.fullTranscribe(samples: samples, wordTimestamps: includeWords)

            guard success else {
                throw TranscriptionError.providerNotAvailable(provider: "Local Whisper", reason: "Transcription failed. The audio may be corrupted or too short.")
            }

            // Get transcription result
            var text = await readKeptText()

            // Fallbacks for empty output:
            // 1) Force the detected language (if any) and retry once. Helps when auto-detect is
            //    correct but decoding returns an empty string.
            // 2) If still empty, retry with translation→English to recover usable text.
            if text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                if let detected = await context.getDetectedLanguage() {
                    logger.warning("⚠️ Empty result. Retrying with detected language forced: \(detected)")
                    await context.setLanguage(detected)
                    success = await context.fullTranscribe(samples: samples, wordTimestamps: includeWords)
                    if success {
                        text = await readKeptText()
                    }
                }
                // If still empty, try translation to English
                if text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    logger.warning("⚠️ Still empty after forcing language. Retrying with translation → English")
                    success = await context.fullTranscribe(samples: samples, translate: true, wordTimestamps: includeWords)
                    if success {
                        text = await readKeptText()
                    }
                }
            }

            if text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                logger.info("🔇 Whisper output remained empty after retries")
                // No-speech: leave lastTimestamps nil (graceful omission).
                throw TranscriptionError.noSpeechDetected
            }

            // Commit timings from the run that produced the kept text.
            self.lastTimestamps = capturedTimed

            // PRIVACY: Never log transcript content - only metadata
            // This ensures user speech is never exposed in system logs or diagnostics
            logger.info("Transcription completed - length: \(text.count, privacy: .public) chars")

            return text
        }
        
        transcriptionTask = task

        // Residency already claimed above (before the task could start). Release
        // it once the pass finishes, on every exit path.
        do {
            let output = try await task.value
            await ModelResidencyRegistry.shared.markIdle(id: libWhisperResidencyId)
            return output
        } catch {
            await ModelResidencyRegistry.shared.markIdle(id: libWhisperResidencyId)
            if error is CancellationError {
                logger.info("Transcription cancelled")
                throw TranscriptionError.streamingInterrupted
            }
            throw error
        }
    }
    
    /// Cancel current transcription
    func cancelTranscription() {
        transcriptionTask?.cancel()
        transcriptionTask = nil
        logger.info("Transcription cancelled")
    }
    
    /// Clean up resources
    func cleanup() async {
        transcriptionTask?.cancel()
        if let context = whisperContext {
            await context.releaseResources()
        }
        whisperContext = nil
        currentModel = nil
        isModelReady = false
        await ModelResidencyRegistry.shared.deregister(id: libWhisperResidencyId)
        logger.info("🧽 Provider cleaned up")
    }
    
    // MARK: - Compatibility Methods for TranscriptionPipeline
    
    /// Set model (compatibility method for TranscriptionPipeline)
    /// This defers actual loading until transcription time for efficiency
    func setModel(_ model: WhisperModel) {
        if currentModel?.name == model.rawValue, whisperContext != nil, isModelReady {
            if pendingModel != nil {
                logger.debug("🎯 Clearing pending model reload because \(model.rawValue) is already active")
            } else {
                logger.debug("🎯 Skipping model reload because \(model.rawValue) is already active")
            }
            pendingModel = nil
            return
        }

        if pendingModel == model {
            logger.debug("🎯 Pending model already set: \(model.rawValue)")
            return
        }

        logger.info("🎯 setModel called with: \(model.rawValue)")
        pendingModel = model
        // Don't load immediately - defer until transcription
    }
    
    /// Check if a model is downloaded (compatibility method)
    func isModelDownloaded(_ modelName: String) -> Bool {
        // PERFORMANCE: Don't trigger async scans here - this method is called frequently
        // The model manager already scans on init and after downloads
        // Just check the current cached state
        
        let isDownloaded = self.modelManager.downloadedModels.contains { $0.name == modelName }
        
        // Only log in debug builds to avoid log spam
        #if DEBUG
        if !isDownloaded && modelManager.downloadedModels.isEmpty {
            logger.debug("⚠️ No models cached, may need scan")
        }
        #endif
        
        return isDownloaded
    }
    
    // MARK: - Audio Processing

    /// Lightweight silence detector for already-normalized Float32 PCM samples.
    /// Uses both peak and RMS to avoid false positives on very short clicks.
    private func isLikelySilent(_ samples: [Float]) -> Bool {
        guard !samples.isEmpty else { return true }

        let stride = max(1, samples.count / 48_000) // cap work to ~48k points
        var peak: Float = 0
        var sumSquares: Double = 0
        var measured = 0
        var index = 0

        while index < samples.count {
            let amplitude = abs(samples[index])
            if amplitude > peak {
                peak = amplitude
            }
            sumSquares += Double(amplitude * amplitude)
            measured += 1
            index += stride
        }

        let rms = sqrt(sumSquares / Double(max(1, measured)))
        return peak < 0.003 && rms < 0.0008
    }

    /// Load and convert audio to the format required by whisper.cpp
    /// 
    /// ROBUST CONVERSION PIPELINE:
    /// - Handles any common format (WAV/AIFF/MP3/AAC/M4A)
    /// - Processes in chunks to minimize memory usage
    /// - Smart downmixing for multi-channel audio
    /// - Comprehensive error handling and recovery
    /// - Optional normalization to prevent clipping
    /// 
    /// - Parameter audioURL: URL of the audio file
    /// - Returns: Audio samples in Float32 format at 16kHz mono
    private func loadAndConvertAudio(from audioURL: URL) async throws -> [Float] {
        // Use the new robust AudioConverter utility
        let converter = AudioConverter()
        
        // Configure conversion options for whisper.cpp
        let options = AudioConverter.ConversionOptions(
            chunkSize: 32768,  // 32K frames for balanced memory/performance
            targetSampleRate: 16000.0,  // Required by whisper.cpp
            normalize: false,  // Don't alter audio levels by default
            progressHandler: { progress in
                // Log progress for long conversions
                if progress.truncatingRemainder(dividingBy: 0.25) < 0.01 {
                    self.logger.debug("🎵 Audio conversion: \(Int(progress * 100))%")
                }
            }
        )
        
        do {
            // Perform robust chunked conversion
            let samples = try await converter.convert(from: audioURL, options: options)
            
            // Validate output
            guard !samples.isEmpty else {
                logger.error("❌ Audio conversion produced empty output")
                throw TranscriptionError.audioConversionFailed
            }
            
            // Log conversion success
            let duration = Float(samples.count) / 16000.0
            logger.info("✅ Audio converted: \(samples.count) samples (\(String(format: "%.1f", duration))s @ 16kHz)")
            
            // Additional validation for whisper.cpp requirements
            if samples.count < 1600 {  // Less than 0.1 seconds
                logger.warning("⚠️ Very short audio: \(samples.count) samples")
            }
            
            return samples
            
        } catch let error as AudioConverter.ConversionError {
            // Map AudioConverter errors to TranscriptionError
            logger.error("❌ Audio conversion error: \(error.localizedDescription)")

            switch error {
            case .invalidAudioFile, .unsupportedFormat:
                throw TranscriptionError.invalidAudioFormat
            case .emptyAudioFile:
                throw TranscriptionError.audioFileNotFound
            case .conversionFailed:
                throw TranscriptionError.audioConversionFailed
            }
            
        } catch {
            // Unexpected errors
            logger.error("❌ Unexpected audio conversion error: \(error)")
            throw TranscriptionError.audioConversionFailed
        }
    }
    
    // MARK: - Model Management
    
    /// Get list of available models
    func getAvailableModels() -> [String] {
        // IMPORTANT: Return the shared model manager's downloaded models
        // This ensures consistency with the UI and prevents state drift
        return modelManager.downloadedModels.map { $0.name }
    }
    
    /// Get the currently loaded model name
    func getCurrentModelName() -> String? {
        return currentModel?.name
    }
    
    /// Preload a model exclusively (for compatibility)
    func preloadExclusively(_ model: WhisperModel, language: String?, preferEnglishOptimized: Bool) async throws {
        // In libwhisper.cpp, models load instantly so we just load the model
        var modelName = model.rawValue
        
        // If English is preferred and available, use English variant
        if preferEnglishOptimized && !modelName.hasSuffix(".en") {
            let englishVariant = modelName + ".en"
            if isModelDownloaded(englishVariant) {
                modelName = englishVariant
            }
        }
        
        try await loadModel(named: modelName)
    }
    
    /// Delete a model (for compatibility)
    func deleteModel(_ model: WhisperModel) throws {
        Task {
            if let modelToDelete = modelManager.downloadedModels.first(where: { $0.name == model.rawValue }) {
                await modelManager.deleteModel(modelToDelete)
            }
        }
    }
    
    /// Get total size of downloaded models
    func getModelsSize() -> Int64 {
        return modelManager.downloadedModels.reduce(0) { $0 + $1.sizeInBytes }
    }
}
