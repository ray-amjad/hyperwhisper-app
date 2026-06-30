//
//  TranscriptionPipeline.swift
//  hyperwhisper
//
//  Created by Rehman Amjad on 16/08/2025.
//

import Foundation
import SwiftUI
import Combine

/// Coordinates transcription operations and delegates work to specialized components.
///
/// **Responsibilities:**
/// - Provider selection and health checks
/// - Model preparation and lifecycle
/// - Transcription execution, cancellation, and state updates
/// - Post-processing orchestration and caching
/// - Vocabulary management and retry handling
///
/// Stored properties live here; behavior is split into focused extensions.
@MainActor
class TranscriptionPipeline: ObservableObject {

    // MARK: - Dependencies

    /// Shared model manager for whisper.cpp models.
    /// Inject via `setModelManager` immediately after initialization.
    private var whisperModelManager: WhisperModelManager?

    /// Parakeet model manager for FluidAudio ASR models.
    private var parakeetModelManager: ParakeetModelManager?
    /// Local Parakeet provider (FluidAudio).
    private var parakeetProvider: ParakeetProvider?

    /// Qwen3 ASR model manager for FluidAudio Qwen3 models.
    private var qwen3AsrModelManager: Qwen3AsrModelManager?
    /// Local Qwen3 ASR provider (FluidAudio).
    private var qwen3AsrProvider: (any TranscriptionProvider)?

    /// Nemotron 3.5 ASR model manager (FluidAudio Nemotron Multilingual).
    private var nemotronModelManager: NemotronModelManager?
    /// Local Nemotron 3.5 ASR provider (FluidAudio).
    private var nemotronProvider: (any TranscriptionProvider)?

    /// Concrete handle for the streaming client to share the provider's
    /// per-variant `Runtime` cache (avoids 1–3 s repeat CoreML preload on each PTT).
    @available(macOS 14.0, *)
    var nemotronProviderForStreaming: NemotronProvider? {
        nemotronProvider as? NemotronProvider
    }

    /// Apple SpeechAnalyzer provider (stored as protocol type to avoid @available on stored properties).
    private var speechAnalyzerProvider: (any TranscriptionProvider)?

    // MARK: - Published Properties

    /// Currently selected provider (local or cloud).
    @Published var selectedProvider: TranscriptionProviderType = .local

    /// Current manager state (idle, transcribing, error, etc).
    @Published var state: TranscriptionState = .idle

    /// Available Whisper models for local transcription.
    @Published var availableModels: [WhisperModel] = []

    /// Currently selected model for local transcription.
    /// This is managed by LibWhisperProvider.
    @Published var selectedModel: WhisperModel = .base

    /// Last transcription result.
    @Published var lastTranscription: TranscriptionResult?

    // MARK: - Computed UI Properties

    /// True if the manager is in the process of transcribing.
    var isTranscribing: Bool {
        if case .transcribing = state { return true }
        return false
    }

    /// The current transcription progress (0.0 to 1.0).
    var transcriptionProgress: Float {
        if case .transcribing(_, let progress) = state { return progress }
        return 0.0
    }

    /// The current error message, if any.
    var errorMessage: String? {
        if case .error(let message) = state { return message }
        return nil
    }

    // MARK: - Coordinators and Helpers

    /// Coordinates provider selection and configuration.
    let providerCoordinator = TranscriptionProviderRouter()

    /// Coordinates model preparation and management.
    let modelCoordinator = TranscriptionModelManager()

    /// Handles custom vocabulary replacements.
    let vocabularyProcessor = VocabularyProcessor()

    /// Manages transcription result caching.
    let cache = TranscriptionResultCache()

    /// Handles retry logic for failed transcriptions.
    var retryHandler: TranscriptionRetryController!

    // MARK: - Internal State

    /// Local Whisper provider using libwhisper.cpp (initialized after model manager injection).
    var localProvider: LibWhisperProvider!

    /// AI post-processor for text enhancement.
    var aiPostProcessor: AIPostProcessor?

    /// Tracks which settings manager the AI post-processor is bound to.
    weak var aiPostProcessorSettingsReference: SettingsManager?

    /// Current transcription task (for cancellation).
    var currentTask: Task<TranscriptionResult, Error>?

    // MARK: - Persisted Settings

    /// API key for OpenAI.
    @AppStorage("openAIAPIKey") var openAIAPIKey: String = ""

    /// API key for Groq.
    @AppStorage("groqAPIKey") var groqAPIKey: String = ""

    /// Whether to use OpenAI for transcription.
    @AppStorage("useOpenAITranscription") var useOpenAITranscription: Bool = false

    // MARK: - External References

    /// Reference to app state for UI updates during post-processing.
    weak var appState: AppState?

    /// Reference to settings manager for provider config.
    /// Updates downstream coordinators and post-processors when changed.
    weak var settingsManager: SettingsManager? {
        didSet {
            guard settingsManager !== oldValue else { return }
            providerCoordinator.setManagers(
                healthManager: providerHealthManager,
                licenseManager: licenseManager,
                creditManager: creditManager,
                settingsManager: settingsManager
            )
            retryHandler?.setSettingsManager(settingsManager)
            Task { @MainActor [weak self] in
                self?.setupAIPostProcessor()
            }
        }
    }

    /// Reference to cloud provider health manager.
    weak var providerHealthManager: CloudProviderHealthManager? {
        didSet {
            guard providerHealthManager !== oldValue else { return }
            providerCoordinator.setManagers(
                healthManager: providerHealthManager,
                licenseManager: licenseManager,
                creditManager: creditManager,
                settingsManager: settingsManager
            )
        }
    }

    /// Reference to license manager.
    weak var licenseManager: LicenseManager? {
        didSet {
            guard licenseManager !== oldValue else { return }
            providerCoordinator.setManagers(
                healthManager: providerHealthManager,
                licenseManager: licenseManager,
                creditManager: creditManager,
                settingsManager: settingsManager
            )
            aiPostProcessor?.licenseManager = licenseManager
        }
    }

    /// Reference to credit manager (HyperWhisper Cloud).
    weak var creditManager: HyperWhisperCloudManager? {
        didSet {
            guard creditManager !== oldValue else { return }
            providerCoordinator.setManagers(
                healthManager: providerHealthManager,
                licenseManager: licenseManager,
                creditManager: creditManager,
                settingsManager: settingsManager
            )
        }
    }

    /// Reference to custom post-processing endpoint manager.
    weak var customPostProcessingManager: CustomPostProcessingManager? {
        didSet {
            guard customPostProcessingManager !== oldValue else { return }
            aiPostProcessor?.customPostProcessingManager = customPostProcessingManager
        }
    }

    // MARK: - Connection Pre-Warm

    /// Fires a HyperWhisper Cloud connection warmup if the currently-selected mode
    /// resolves to cloud. Call from hotkey-down paths — fire-and-forget.
    @MainActor
    func prewarmCloudConnectionIfActive() {
        let snapshot = appState?.selectedModeSnapshot
        providerCoordinator.prewarmCloudConnectionIfActive(
            model: snapshot?.model,
            cloudProvider: snapshot?.rawCloudProvider
        )
    }

    // MARK: - Foreground Keepalive

    /// Periodic warmup ticker that runs only while the app is frontmost.
    /// macOS `URLSession`'s pooled HTTP/2 connection idles out after a few
    /// minutes — pinging /warmup every 45s keeps the pool warm so that a
    /// hotkey press after a long idle window still reuses the connection
    /// instead of paying TCP+TLS handshake again.
    private var keepaliveTimer: DispatchSourceTimer?
    private var keepaliveObserverTokens: [NSObjectProtocol] = []
    private static let keepaliveInterval: TimeInterval = 45

    @MainActor
    private func startKeepaliveTicker() {
        let center = NotificationCenter.default
        let activeToken = center.addObserver(
            forName: NSApplication.didBecomeActiveNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            self?.resumeKeepaliveTimer()
        }
        let inactiveToken = center.addObserver(
            forName: NSApplication.willResignActiveNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            self?.suspendKeepaliveTimer()
        }
        keepaliveObserverTokens.append(contentsOf: [activeToken, inactiveToken])

        // Start in the correct state for the current foreground/background status.
        if NSApplication.shared.isActive {
            resumeKeepaliveTimer()
        }
    }

    @MainActor
    private func resumeKeepaliveTimer() {
        if keepaliveTimer != nil { return }
        let timer = DispatchSource.makeTimerSource(queue: .main)
        timer.schedule(
            deadline: .now() + Self.keepaliveInterval,
            repeating: Self.keepaliveInterval
        )
        timer.setEventHandler { [weak self] in
            guard let self else { return }
            let snapshot = self.appState?.selectedModeSnapshot
            self.providerCoordinator.prewarmCloudConnectionIfActiveForced(
                model: snapshot?.model,
                cloudProvider: snapshot?.rawCloudProvider
            )
        }
        timer.resume()
        keepaliveTimer = timer
    }

    @MainActor
    private func suspendKeepaliveTimer() {
        keepaliveTimer?.cancel()
        keepaliveTimer = nil
    }

    deinit {
        // Pipeline lives for app lifetime in normal flows, but remove
        // observers and cancel the timer for hygiene so a recreated
        // pipeline doesn't leave dangling notification tokens behind.
        // Block observers returned by addObserver(forName:) are removable
        // from any context via removeObserver(_:).
        for token in keepaliveObserverTokens {
            NotificationCenter.default.removeObserver(token)
        }
        keepaliveTimer?.cancel()
    }

    // MARK: - Initialization

    init() {
        // Local provider is initialized when the model manager is injected.
        // No warm-up callbacks are needed (models load instantly).
        retryHandler = TranscriptionRetryController(transcriptionPipeline: self)

        // Sync model coordinator state back to the manager.
        modelCoordinator.onStateChange = { [weak self] newState in
            self?.state = newState
        }

        // Defer any @Published mutations to avoid publishing within view updates.
        Task { @MainActor [weak self] in
            self?.providerCoordinator.setupCloudProvider(with: self?.openAIAPIKey ?? "")
            self?.setupAIPostProcessor()
            self?.startKeepaliveTicker()
        }

        AppLogger.transcription.info("TranscriptionPipeline initialized - OpenAI configured: \(!self.openAIAPIKey.isEmpty, privacy: .public), Use OpenAI: \(self.useOpenAITranscription, privacy: .public)")
    }

    // MARK: - Dependency Injection

    /// Push the current provider + model-manager set into both coordinators in
    /// lockstep. Every setter reads back from `self.*` so we can't drift between
    /// "what setProviders was told" and "what setDependencies was told".
    private func rewireCoordinators() {
        providerCoordinator.setProviders(
            localProvider: localProvider,
            parakeetProvider: parakeetProvider,
            parakeetModelManager: parakeetModelManager,
            speechAnalyzerProvider: speechAnalyzerProvider,
            qwen3AsrProvider: qwen3AsrProvider,
            qwen3AsrModelManager: qwen3AsrModelManager,
            nemotronProvider: nemotronProvider,
            nemotronModelManager: nemotronModelManager
        )

        modelCoordinator.setDependencies(
            localProvider: localProvider,
            parakeetModelManager: parakeetModelManager,
            parakeetProvider: parakeetProvider,
            qwen3AsrModelManager: qwen3AsrModelManager,
            qwen3AsrProvider: qwen3AsrProvider,
            nemotronModelManager: nemotronModelManager,
            nemotronProvider: nemotronProvider,
            speechAnalyzerProvider: speechAnalyzerProvider,
            appState: appState
        )
    }

    /// Injects the shared Whisper model manager and configures the local provider.
    /// - Parameter modelManager: The app's shared `WhisperModelManager` instance.
    func setModelManager(_ modelManager: WhisperModelManager) {
        whisperModelManager = modelManager

        if localProvider == nil {
            localProvider = LibWhisperProvider(modelManager: modelManager)
            rewireCoordinators()
            AppLogger.transcription.info("✅ LibWhisperProvider created with injected model manager")
        }
    }

    /// Injects the Parakeet model manager for FluidAudio provider.
    func setParakeetModelManager(_ modelManager: ParakeetModelManager) {
        parakeetModelManager = modelManager
        if #available(macOS 13.0, *) {
            if parakeetProvider == nil {
                parakeetProvider = ParakeetProvider()
                AppLogger.transcription.info("✅ ParakeetProvider initialized")
            }
            // Mirror the Nemotron wiring: when a version is deleted, drop the
            // matching cached AsrManager so the next transcription re-reads
            // from disk instead of serving the stale in-memory weights.
            let provider = parakeetProvider
            modelManager.onVersionInvalidated = { [weak provider] version in
                await provider?.invalidateRuntime(for: version)
            }
        } else {
            parakeetProvider = nil
        }
        rewireCoordinators()
    }

    /// Injects the local model manager for post-processing.
    func setLocalModelManager(_ modelManager: LocalModelManager) {
        modelCoordinator.localModelManager = modelManager
        aiPostProcessor?.localModelManager = modelManager
        providerCoordinator.localModelManager = modelManager
        providerCoordinator.llamaServerController = modelCoordinator.llamaServerController
    }

    /// Injects the Qwen3 ASR model manager for FluidAudio Qwen3 provider.
    func setQwen3AsrModelManager(_ modelManager: Qwen3AsrModelManager) {
        qwen3AsrModelManager = modelManager
        if #available(macOS 15.0, *) {
            if qwen3AsrProvider == nil {
                qwen3AsrProvider = Qwen3AsrProvider()
                AppLogger.transcription.info("✅ Qwen3AsrProvider initialized")
            }
            // Mirror the Nemotron/Parakeet wiring: when the model is deleted,
            // drop the cached manager so the next transcription re-reads from
            // disk instead of serving the stale in-memory weights (which may be
            // a different variant after a delete + re-download).
            let provider = qwen3AsrProvider as? Qwen3AsrProvider
            modelManager.onModelInvalidated = { [weak provider] in
                await provider?.invalidateRuntime()
            }
        } else {
            qwen3AsrProvider = nil
        }
        rewireCoordinators()
    }

    /// Injects the Nemotron 3.5 ASR model manager for FluidAudio Nemotron provider.
    func setNemotronModelManager(_ modelManager: NemotronModelManager) {
        nemotronModelManager = modelManager
        if #available(macOS 14.0, *) {
            if nemotronProvider == nil {
                nemotronProvider = NemotronProvider()
                AppLogger.transcription.info("✅ NemotronProvider initialized")
            }
            let provider = nemotronProvider as? NemotronProvider
            // Give the provider a weak back-reference to the manager so it can
            // flag corrupted installs (markVariantBroken) on load failure.
            provider?.modelManager = modelManager
            // Mirror the other direction: when a variant is deleted, drop the
            // matching cached shared bundle so the next session re-reads from disk.
            modelManager.onVariantInvalidated = { [weak provider] variant in
                await provider?.invalidateRuntime(for: variant)
            }
        } else {
            nemotronProvider = nil
        }
        rewireCoordinators()
    }

    /// Creates and wires up the Apple SpeechAnalyzer provider if available on macOS 26+.
    func setSpeechAnalyzerProvider() {
        #if canImport(Speech)
        if #available(macOS 26.0, *) {
            let provider = AppleSpeechAnalyzerProvider()
            if provider.isAvailable {
                speechAnalyzerProvider = provider
                AppLogger.transcription.info("✅ AppleSpeechAnalyzerProvider initialized and available")
            } else {
                speechAnalyzerProvider = nil
                AppLogger.transcription.info("AppleSpeechAnalyzerProvider not available on this device")
            }
        } else {
            speechAnalyzerProvider = nil
        }
        #else
        speechAnalyzerProvider = nil
        #endif
        rewireCoordinators()
    }
}

// MARK: - Provider Type Enum

/// Types of transcription providers.
enum TranscriptionProviderType: String, CaseIterable {
    case local = "Local"
    case cloud = "Cloud"

    var description: String {
        switch self {
        case .local:
            return "On-device processing"
        case .cloud:
            return "OpenAI Whisper API"
        }
    }
}
