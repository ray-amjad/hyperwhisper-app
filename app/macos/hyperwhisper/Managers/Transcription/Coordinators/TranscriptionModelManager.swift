//
//  TranscriptionModelManager.swift
//  hyperwhisper
//
//  TRANSCRIPTION MODEL COORDINATOR
//  This class manages model preparation, loading, and availability for transcription.
//
//  Key Features:
//  - Model preloading and exclusive loading (unloads other models)
//  - Parakeet (FluidAudio) model integration
//  - Local LLM runtime management for post-processing
//  - Model availability checking
//  - Surfaces preparation errors to the user without silent local fallbacks
//
//  Architecture Notes:
//  - Extracted from TranscriptionPipeline to separate model management concerns
//  - Coordinates between LibWhisperProvider, ParakeetProvider, and LlamaServerController
//  - Handles language-aware model selection (English-optimized vs multilingual)
//

import Foundation
import SwiftUI

/// Manages model preparation and availability for transcription
@MainActor
class TranscriptionModelManager: ObservableObject {

    // MARK: - Dependencies

    /// Local whisper.cpp provider for model loading
    private weak var localProvider: LibWhisperProvider?

    /// Parakeet model manager for FluidAudio ASR
    private weak var parakeetModelManager: ParakeetModelManager?

    /// Parakeet provider for FluidAudio transcription
    private weak var parakeetProvider: ParakeetProvider?

    /// Qwen3 ASR model manager for FluidAudio Qwen3 models
    private weak var qwen3AsrModelManager: Qwen3AsrModelManager?

    /// Qwen3 ASR provider for FluidAudio transcription
    private var qwen3AsrProvider: (any TranscriptionProvider)?

    /// Nemotron 3.5 ASR model manager (latin + multilingual variants)
    private weak var nemotronModelManager: NemotronModelManager?

    /// Nemotron 3.5 ASR provider
    private var nemotronProvider: (any TranscriptionProvider)?

    /// Apple Speech Analyzer provider for macOS 26+ on-device transcription
    private var speechAnalyzerProvider: (any TranscriptionProvider)?

    /// Local model manager for post-processing
    weak var localModelManager: LocalModelManager?

    /// Server controller for local post-processing runtime (llama.cpp)
    let llamaServerController = LlamaServerController()

    /// Reference to app state for error display
    private weak var appState: AppState?

    /// State update callback (updates TranscriptionPipeline.state)
    var onStateChange: ((TranscriptionState) -> Void)?

    // MARK: - Model Ready State

    enum ModelReadyState: Equatable {
        case none
        case loading(name: String)
        case ready(name: String)
    }

    // MARK: - Published Properties

    /// Lifecycle state of the currently active transcription model
    @Published var modelReadyState: ModelReadyState = .none

    /// Available Whisper models for local transcription
    @Published var availableModels: [WhisperModel] = []

    /// Currently selected model for local transcription
    @Published var selectedModel: WhisperModel = .base

    // MARK: - Initialization

    init() {
        // Dependencies will be injected after initialization
    }

    // MARK: - Dependency Injection

    /// Inject dependencies for model coordination
    /// - Parameters:
    ///   - localProvider: LibWhisperProvider for whisper.cpp models
    ///   - parakeetModelManager: Manager for Parakeet models
    ///   - parakeetProvider: Provider for Parakeet transcription
    ///   - appState: App state for error display
    func setDependencies(
        localProvider: LibWhisperProvider?,
        parakeetModelManager: ParakeetModelManager?,
        parakeetProvider: ParakeetProvider?,
        qwen3AsrModelManager: Qwen3AsrModelManager?,
        qwen3AsrProvider: (any TranscriptionProvider)?,
        nemotronModelManager: NemotronModelManager?,
        nemotronProvider: (any TranscriptionProvider)?,
        speechAnalyzerProvider: (any TranscriptionProvider)?,
        appState: AppState?
    ) {
        self.localProvider = localProvider
        self.parakeetModelManager = parakeetModelManager
        self.parakeetProvider = parakeetProvider
        if let qwen3AsrModelManager { self.qwen3AsrModelManager = qwen3AsrModelManager }
        if let qwen3AsrProvider { self.qwen3AsrProvider = qwen3AsrProvider }
        self.nemotronModelManager = nemotronModelManager
        self.nemotronProvider = nemotronProvider
        self.speechAnalyzerProvider = speechAnalyzerProvider
        self.appState = appState
    }

    // MARK: - Model Preparation

    /// Prepare a model for use (preload it immediately when mode is selected)
    /// MODEL PREPARATION FLOW:
    /// 1. Check if transcription is in progress (don't interrupt)
    /// 2. Cancel any other ongoing preparation task
    /// 3. Determine which model to load based on mode settings
    /// 4. Exclusively load the model (unload any previously loaded model)
    /// 5. Surface errors to the user without falling back to a different local model
    ///
    /// This ensures the model is ready before transcription starts, reducing latency.
    ///
    /// - Parameter mode: The transcription mode containing model information
    func prepareModel(for mode: Mode?, currentState: TranscriptionState, cancelTranscription: @escaping () -> Void) async {
        let modelId = (mode?.model ?? "").lowercased()

        // GUARD: Don't interrupt a transcription
        if case .transcribing = currentState {
            AppLogger.models.warning("Model preparation cancelled, transcription in progress.")
            return
        }

        // Cancel any other ongoing task like warming
        cancelTranscription()

        // CLOUD/NO MODE CASE: No local preload — cloud failures surface as errors
        if modelId.isEmpty || modelId == "cloud" {
            modelReadyState = .ready(name: "Cloud")
            return
        }

        // SPEECH ANALYZER MODEL CASE: Prepare Apple Speech Analyzer for macOS 26+
        if isSpeechAnalyzerModel(modelId) {
            #if canImport(Speech)
            if #available(macOS 26.0, *),
               let provider = speechAnalyzerProvider as? AppleSpeechAnalyzerProvider {
                modelReadyState = .loading(name: "Apple Speech")
                do {
                    try await provider.prepareIfNeeded(language: extractLanguage(from: mode))
                    modelReadyState = .ready(name: "Apple Speech")
                } catch {
                    AppLogger.models.error("Failed to prepare Speech Analyzer: \(error.localizedDescription, privacy: .public)")
                    modelReadyState = .none
                    onStateChange?(.error(message: "Failed to prepare Speech Analyzer."))
                }
            } else {
                AppLogger.models.warning("Speech Analyzer unavailable (requires macOS 26+)")
                modelReadyState = .none
                onStateChange?(.error(message: "Apple Speech Analyzer requires macOS 26 or later."))
            }
            #else
            AppLogger.models.warning("Speech Analyzer unavailable (SDK too old)")
            modelReadyState = .none
            onStateChange?(.error(message: "Apple Speech Analyzer is not available in this build."))
            #endif
            return
        }

        // PARAKEET MODEL CASE: Prepare Parakeet provider for FluidAudio (V2 or V3)
        // Both versions are handled by ParakeetProvider with version-aware loading
        if #available(macOS 13.0, *),
           isParakeetModel(modelId),
           let parakeetProvider,
           let parakeetModelManager {
            let parakeetDisplayName = modelId.lowercased().contains("v2") ? "Parakeet V2" : "Parakeet V3"
            AppLogger.models.info("Preparing Parakeet model for mode selection: \(modelId)")
            modelReadyState = .loading(name: parakeetDisplayName)
            parakeetModelManager.refreshState()
            do {
                let lang = extractLanguage(from: mode)
                // Pass specific modelId to prepare the correct version (V2 or V3)
                try await parakeetProvider.prepareIfNeeded(language: lang, modelId: modelId)
                modelReadyState = .ready(name: parakeetDisplayName)
            } catch is CancellationError {
                AppLogger.models.info("Parakeet preparation cancelled")
            } catch {
                AppLogger.models.error("Failed to prepare Parakeet provider: \(error.localizedDescription, privacy: .public)")
                modelReadyState = .none
                onStateChange?(.error(message: "Failed to prepare \(parakeetDisplayName)."))
                if AppLogger.isErrorLoggingEnabled {
                    SentryService.capture(error: error, message: "Parakeet prepare failed", extras: ["modelId": modelId], tags: ["component": "models"])
                }
            }
            return
        }

        // QWEN3 ASR MODEL CASE: Prepare Qwen3 ASR provider for FluidAudio
        if #available(macOS 15.0, *),
           isQwen3AsrModel(modelId),
           let provider = qwen3AsrProvider as? Qwen3AsrProvider,
           let qwen3AsrModelManager {
            let displayName = Qwen3AsrModelManager.Constants.displayName
            AppLogger.models.info("Preparing Qwen3 ASR model for mode selection: \(modelId)")
            modelReadyState = .loading(name: displayName)
            qwen3AsrModelManager.refreshState()
            do {
                let lang = extractLanguage(from: mode)
                try await provider.prepareIfNeeded(language: lang, modelId: modelId)
                modelReadyState = .ready(name: displayName)
            } catch is CancellationError {
                AppLogger.models.info("Qwen3 ASR preparation cancelled")
            } catch {
                AppLogger.models.error("Failed to prepare Qwen3 ASR provider: \(error.localizedDescription, privacy: .public)")
                modelReadyState = .none
                onStateChange?(.error(message: "Failed to prepare \(displayName)."))
                if AppLogger.isErrorLoggingEnabled {
                    SentryService.capture(error: error, message: "Qwen3 ASR prepare failed", extras: ["modelId": modelId], tags: ["component": "models"])
                }
            }
            return
        }

        // NEMOTRON 3.5 MODEL CASE: Prepare the matching FluidAudio variant
        // (latin or multilingual). Without this branch, the first PTT after a
        // mode-switch pays the multi-second `downloadAndPreloadShared` cost
        // inside the recording's hot path.
        if #available(macOS 14.0, *),
           isNemotronModel(modelId),
           let provider = nemotronProvider as? NemotronProvider,
           let nemotronModelManager {
            let displayName = modelId.lowercased().contains("multilingual")
                ? NemotronModelManager.Constants.multilingualDisplayName
                : NemotronModelManager.Constants.latinDisplayName
            AppLogger.models.info("Preparing Nemotron model for mode selection: \(modelId)")
            modelReadyState = .loading(name: displayName)
            nemotronModelManager.refreshState()
            do {
                try await provider.prepareIfNeeded(language: extractLanguage(from: mode), modelId: modelId)
                modelReadyState = .ready(name: displayName)
            } catch {
                AppLogger.models.error("Failed to prepare Nemotron: \(error.localizedDescription, privacy: .public)")
            }
            return
        }

        // WHISPER MODEL CASE: Load specific whisper.cpp model
        guard let target = mapModelIdToWhisperModel(modelId) else {
            AppLogger.models.warning("Could not map model ID to WhisperModel: \(modelId, privacy: .public)")
            return
        }

        await loadWhisperModel(target, mode: mode)
    }

    /// Prepare the local LLM runtime for the active mode
    /// LOCAL RUNTIME MANAGEMENT:
    /// - Starts the bundled llama.cpp server when mode uses local post-processing
    /// - Stops the server when mode doesn't need it (saves resources)
    /// - Validates model availability before starting
    /// - Handles errors gracefully with user-friendly messages
    ///
    /// - Parameter mode: The active transcription mode
    func prepareLocalRuntime(for mode: Mode?) async {
        guard let mode else {
            // No mode selected → stop server
            llamaServerController.stop(reason: .modeChanged)
            return
        }

        let processingMode = PostProcessingMode(rawValue: mode.postProcessingMode) ?? .off
        guard processingMode == .local else {
            // Not using local post-processing → stop server
            llamaServerController.stop(reason: .providerDisabled)
            return
        }

        let providerId = mode.postProcessingProvider ?? processingMode.defaultProvider?.rawValue ?? ""
        guard let provider = PostProcessingProvider(rawValue: providerId), provider.isLocal else {
            // Not using a local provider → stop server
            llamaServerController.stop(reason: .providerDisabled)
            return
        }
        // Signal "warming up" immediately so the status bar shows feedback
        llamaServerController.markPending()

        // Try to start the llama-server with the preferred model
        do {
            // Resolve local model — let llama-server auto-detect from GGUF metadata
            guard let qwenManager = localModelManager else {
                AppLogger.transcription.info("Local LLM selected but localModelManager is nil — not yet wired")
                return  // Don't stop server; a later call will wire it
            }
            let installedModels = qwenManager.downloadedModels
            guard !installedModels.isEmpty else {
                llamaServerController.stop(reason: .modelMissing)
                AppLogger.transcription.warning("Local LLM selected but no weights are installed")
                return
            }
            let preferredId = mode.languageModel ?? ""
            let resolved = installedModels.first(where: { $0.id == preferredId }) ?? installedModels.first!
            guard let modelURL = resolved.localURL else {
                llamaServerController.stop(reason: .modelMissing)
                return
            }
            _ = try await llamaServerController.ensureRunning(
                modelId: resolved.id,
                modelURL: modelURL
            )
        } catch let serverError as LlamaServerController.Error {
            handleLocalRuntimeError(serverError)
        } catch {
            AppLogger.transcription.error("Unexpected local runtime error: \(error.localizedDescription, privacy: .public)")
            appState?.showError("llama.runtime.genericError".localized(arguments: error.localizedDescription))
        }
    }

    /// Convenience helper that reloads the runtime for a persisted mode identifier
    /// - Parameter modeId: UUID string of the mode
    func refreshLocalRuntime(forModeId modeId: String?) async {
        guard let modeId, UUID(uuidString: modeId) != nil else {
            llamaServerController.stop(reason: .modeChanged)
            return
        }

        let mode = await PersistenceController.shared.fetchModeInBackground(withId: modeId)
        await prepareLocalRuntime(for: mode)
    }

    // MARK: - Model Management

    /// Check which models are available locally
    /// Scans the models directory and updates the availableModels list
    func checkAvailableModels() {
        availableModels = []

        // Guard against being called before provider is initialized
        guard let provider = localProvider else {
            AppLogger.models.debug("Provider not initialized, skipping model check")
            return
        }

        AppLogger.models.info("🔍 Checking available models...")

        // Check models directory for downloaded models
        // PATH CONSISTENCY: Using lowercase "hyperwhisper" to match WhisperModelManager
        let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        let modelsDirectory = appSupport.appendingPathComponent("hyperwhisper/models")

        AppLogger.models.info("📁 Checking models in: \(modelsDirectory.path)")

        for model in WhisperModel.allCases {
            let isDownloaded = provider.isModelDownloaded(model.rawValue)
            AppLogger.models.debug("   Model \(model.rawValue): \(isDownloaded ? "✅" : "❌")")
            if isDownloaded {
                availableModels.append(model)
            }
        }

        // Set selected model to first available or base
        if !availableModels.isEmpty {
            provider.setModel(availableModels.first!)
            AppLogger.models.info("🎯 Set default model to: \(self.availableModels.first!.rawValue)")
        } else {
            AppLogger.models.warning("⚠️ No models available, will use cloud fallback")
        }

        AppLogger.models.info("📦 Available models: \(self.availableModels.map { $0.rawValue }, privacy: .public)")
    }

    /// Rescan available local models (exposed for startup sync)
    func rescanAvailableLocalModels() {
        // Only scan if provider is initialized
        guard localProvider != nil else {
            AppLogger.models.debug("Skipping model scan - provider not yet initialized")
            return
        }
        checkAvailableModels()
    }

    /// Delete a downloaded model to free up space
    /// - Parameter model: The model to delete
    func deleteModel(_ model: WhisperModel) throws {
        try localProvider?.deleteModel(model)

        // Remove from available models list
        availableModels.removeAll { $0 == model }

        // If this was the selected model, pick another
        if selectedModel == model {
            selectedModel = availableModels.first ?? .base
        }
    }

    /// Get the total size of downloaded models
    /// - Returns: Total size in bytes
    func getModelsSize() -> Int64 {
        return localProvider?.getModelsSize() ?? 0
    }

    /// Preload a model and warm it up
    /// - Parameter model: The model to preload
    func preloadModel(_ model: WhisperModel) async {
        do {
            // Don't prefer English-optimized when no language is specified (auto-detect)
            try await localProvider?.preloadExclusively(model, language: nil, preferEnglishOptimized: false)
            AppLogger.models.info("Model \(model.rawValue) is preloaded and warmed")
        } catch {
            // Non-fatal: we'll still be able to load on first use
            AppLogger.models.debug("Failed to preload model \(model.rawValue): \(error.localizedDescription, privacy: .public)")
        }
    }

    // MARK: - Private Helper Methods

    /// Check if a model ID is a Parakeet model (V2 or V3)
    /// PARAKEET MODEL DETECTION:
    /// Matches any model ID starting with "parakeet-tdt-" prefix
    /// This handles both V2 (English-only) and V3 (Multilingual) models
    private func isParakeetModel(_ modelId: String) -> Bool {
        modelId.lowercased().hasPrefix("parakeet-tdt-")
    }

    /// Check if a model ID is a Qwen3 ASR model
    private func isQwen3AsrModel(_ modelId: String) -> Bool {
        modelId.lowercased() == Qwen3AsrModelManager.Constants.modelId
    }

    /// Check if a model ID is a Nemotron 3.5 ASR model (latin or multilingual).
    private func isNemotronModel(_ modelId: String) -> Bool {
        modelId.lowercased().hasPrefix("nemotron-asr-3.5-")
    }

    /// Check if a model ID is the Apple Speech Analyzer model
    private func isSpeechAnalyzerModel(_ modelId: String) -> Bool {
        modelId == "apple-speech-analyzer"
    }

    /// Load a specific Whisper model with language preferences
    private func loadWhisperModel(_ target: WhisperModel, mode: Mode?) async {
        do {
            let name = mode?.name ?? "Default"
            AppLogger.models.info("Switching to offline model: \(target.rawValue, privacy: .public) (mode: \(name, privacy: .public))")
            modelReadyState = .loading(name: target.name)
            onStateChange?(.idle)

            let lang = extractLanguage(from: mode)
            // Only prefer English-optimized model when explicitly set to English
            let preferEnglish = lang != nil && LanguageData.isEnglish(lang!)

            try await localProvider?.preloadExclusively(target, language: lang, preferEnglishOptimized: preferEnglish)
            modelReadyState = .ready(name: target.name)
            onStateChange?(.idle)
            AppLogger.models.info("Model \(target.rawValue, privacy: .public) ready for transcription")
        } catch is CancellationError {
            AppLogger.models.info("Model switch cancelled")
            onStateChange?(.idle)
        } catch {
            AppLogger.models.error("Failed to load model \(target.rawValue, privacy: .public): \(error.localizedDescription, privacy: .public)")
            modelReadyState = .none
            onStateChange?(.error(message: "Failed to load \(target.rawValue)."))
            if AppLogger.isErrorLoggingEnabled {
                SentryService.capture(error: error, message: "Model switch failed", extras: ["target": target.rawValue], tags: ["component": "models"])
            }
        }
    }

    /// Extract language from mode (converts "auto" to nil)
    private func extractLanguage(from mode: Mode?) -> String? {
        guard let raw = mode?.language?.lowercased() else { return nil }
        return raw == "auto" ? nil : raw
    }

    /// Handle local llama-server errors with user-friendly messages.
    private func handleLocalRuntimeError(_ serverError: LlamaServerController.Error) {
        switch serverError {
        case .modelNotFound:
            llamaServerController.stop(reason: .modelMissing)
            AppLogger.transcription.warning("Local LLM post-processing selected but no weights are installed")
        case .executableNotFound:
            AppLogger.transcription.error("Local LLM runtime executable missing")
            appState?.showError("llama.runtime.missing".localized)
        case .launchFailed(let message):
            AppLogger.transcription.error("Failed to launch local LLM runtime: \(message, privacy: .public)")
            appState?.showError("Could not start the local LLM runtime. \(message)")
        case .healthCheckFailed:
            AppLogger.transcription.error("Local LLM runtime failed health check")
            appState?.showError("Local LLM runtime did not start correctly. Check logs for details.")
        case .unsupportedArchitecture:
            AppLogger.transcription.warning("Local LLM runtime unavailable on Intel — local post-processing disabled")
        case .needsNativeRelaunch:
            AppLogger.transcription.warning("Local LLM runtime unavailable under Rosetta — native relaunch required")
            appState?.showError(serverError.localizedDescription)
        }
    }
}
