//
//  TranscriptionProviderRouter.swift
//  hyperwhisper
//
//  TRANSCRIPTION PROVIDER COORDINATOR
//  This class manages transcription provider selection and coordination.
//
//  Key Features:
//  - Multi-provider support (Local, HyperWhisper Cloud, OpenAI, Groq, Deepgram, etc.)
//  - Provider health checks before transcription
//  - API key validation and configuration
//  - Automatic provider initialization
//  - Language-aware provider selection
//
//  Architecture Notes:
//  - Extracted from TranscriptionPipeline to separate provider concerns
//  - Coordinates between multiple provider implementations
//  - Handles both local and cloud transcription providers
//

import Foundation
import SwiftUI

/// Coordinates selection and configuration of transcription providers
@MainActor
class TranscriptionProviderRouter {

    // MARK: - Provider Instances

    /// Local Whisper provider using libwhisper.cpp
    private weak var localProvider: LibWhisperProvider?

    /// Parakeet provider for FluidAudio transcription
    private weak var parakeetProvider: ParakeetProvider?

    /// Parakeet model manager for state refresh
    private weak var parakeetModelManager: ParakeetModelManager?

    /// Qwen3 ASR provider for FluidAudio Qwen3 transcription
    private var qwen3AsrProvider: (any TranscriptionProvider)?

    /// Qwen3 ASR model manager for state refresh
    private weak var qwen3AsrModelManager: Qwen3AsrModelManager?

    /// Nemotron 3.5 ASR provider (latin + multilingual variants)
    private var nemotronProvider: (any TranscriptionProvider)?

    /// Nemotron model manager for state refresh
    private weak var nemotronModelManager: NemotronModelManager?

    /// Apple SpeechAnalyzer provider (stored as protocol type to avoid @available on stored properties)
    private var speechAnalyzerProvider: (any TranscriptionProvider)?

    /// HyperWhisper Cloud provider (built-in, credit-based)
    private var hyperwhisperCloudProvider: HyperWhisperCloudProvider?

    /// Cloud Whisper provider (OpenAI/Groq)
    private let cloudProvider = CloudWhisperProvider()

    /// Additional cloud providers
    private let deepgramProvider = DeepgramProvider()
    private let assemblyAIProvider = AssemblyAIProvider()
    private let elevenLabsProvider = ElevenLabsProvider()
    private let mistralProvider = MistralProvider()
    private let sonioxProvider = SonioxProvider()
    private let geminiTranscriptionProvider = GeminiTranscriptionProvider()
    private let grokSTTProvider = GrokSTTProvider()

    /// HyperWhisper-Cloud-routed providers (no BYOK). Constructed lazily when
    /// the HW Cloud managers are available, mirroring `hyperwhisperCloudProvider`.
    private var azureMAIProvider: AzureMAIProvider?
    private var googleChirpProvider: GoogleChirpProvider?

    // MARK: - Dependencies

    /// Health monitor for validating provider readiness
    weak var providerHealthManager: CloudProviderHealthManager?

    /// License manager for HyperWhisper Cloud identifier
    weak var licenseManager: LicenseManager?

    /// Credit manager for HyperWhisper Cloud balance tracking
    weak var creditManager: HyperWhisperCloudManager?

    /// Settings manager for API keys
    weak var settingsManager: SettingsManager?

    /// llama.cpp runtime controller for the embedded local LLM. Used to probe
    /// post-processing readiness before transcription completes.
    weak var llamaServerController: LlamaServerController?

    /// Catalog of installed local LLM weights, used to resolve the GGUF path
    /// when running the local pre-flight check.
    weak var localModelManager: LocalModelManager?

    // MARK: - Initialization

    init() {
        // Providers will be configured as needed
    }

    // MARK: - Dependency Injection

    /// Set provider dependencies
    /// - Parameters:
    ///   - localProvider: LibWhisperProvider for local transcription
    ///   - parakeetProvider: ParakeetProvider for FluidAudio
    ///   - parakeetModelManager: Manager for Parakeet models
    func setProviders(
        localProvider: LibWhisperProvider?,
        parakeetProvider: ParakeetProvider?,
        parakeetModelManager: ParakeetModelManager?,
        speechAnalyzerProvider: (any TranscriptionProvider)?,
        qwen3AsrProvider: (any TranscriptionProvider)?,
        qwen3AsrModelManager: Qwen3AsrModelManager?,
        nemotronProvider: (any TranscriptionProvider)?,
        nemotronModelManager: NemotronModelManager?
    ) {
        self.localProvider = localProvider
        self.parakeetProvider = parakeetProvider
        self.parakeetModelManager = parakeetModelManager
        self.speechAnalyzerProvider = speechAnalyzerProvider
        self.qwen3AsrProvider = qwen3AsrProvider
        self.qwen3AsrModelManager = qwen3AsrModelManager
        self.nemotronProvider = nemotronProvider
        self.nemotronModelManager = nemotronModelManager
    }

    /// Set health and license dependencies
    /// - Parameters:
    ///   - healthManager: Cloud provider health manager
    ///   - licenseManager: License manager for HyperWhisper Cloud
    ///   - creditManager: Credit manager for HyperWhisper Cloud
    ///   - settingsManager: Settings manager for API keys
    func setManagers(
        healthManager: CloudProviderHealthManager?,
        licenseManager: LicenseManager?,
        creditManager: HyperWhisperCloudManager?,
        settingsManager: SettingsManager?
    ) {
        self.providerHealthManager = healthManager
        self.licenseManager = licenseManager
        self.creditManager = creditManager
        self.settingsManager = settingsManager
    }

    /// Configure cloud provider with API key
    /// - Parameter apiKey: OpenAI API key
    func setupCloudProvider(with apiKey: String) {
        if !apiKey.isEmpty {
            cloudProvider.configure(apiKey: apiKey)
        }
    }

    /// Refresh API configuration when settings change
    func refreshConfiguration(openAIAPIKey: String) {
        if !openAIAPIKey.isEmpty {
            cloudProvider.configure(apiKey: openAIAPIKey)
            AppLogger.network.debug("Refreshed OpenAI configuration")
        }
    }

    // MARK: - Provider Selection

    /// Select the appropriate provider for the given mode
    /// PROVIDER SELECTION FLOW:
    /// 1. Determine if cloud transcription is needed based on model string
    /// 2. For cloud: Identify which cloud provider to use
    /// 3. Validate API keys (except for HyperWhisper Cloud)
    /// 4. Configure the selected provider
    /// 5. For local: Select between LibWhisper and Parakeet
    /// 6. Check provider health if applicable
    /// 7. Return ready-to-use provider
    ///
    /// - Parameters:
    ///   - mode: Transcription mode containing provider preferences
    ///   - vocabulary: Custom vocabulary for local transcription
    /// - Returns: Configured and ready transcription provider
    /// - Throws: TranscriptionError if provider unavailable or misconfigured
    func selectProvider(for mode: Mode?, vocabulary: [Vocabulary]) async throws -> TranscriptionProvider {
        let rawModel = (mode?.model ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        // Empty model id (legacy/imported modes) is treated as cloud — matches prepareModel's behaviour
        let modelString = rawModel.isEmpty ? "cloud" : rawModel
        let useCloudForThisMode = modelString.lowercased() == "cloud"
        let language = extractLanguage(from: mode)

        // LOCAL PROVIDER SELECTION
        if !useCloudForThisMode {
            // Lowercase to match the coordinator's prepareModel, which lowercases
            // mode.model up front. The router's local-model predicates compare
            // against lowercase constants (e.g. Qwen3 "qwen3-asr-0.6b",
            // "apple-speech-analyzer"), so a non-canonically-cased id from a
            // hand-edited / cross-platform backup would otherwise be prepared by
            // the coordinator yet rejected here as "Unknown local model".
            return try await selectLocalProvider(modelId: modelString.lowercased(), language: language)
        }

        // CLOUD PROVIDER SELECTION
        let cloudProviderType = mode?.cloudProvider.flatMap { CloudProvider(rawValue: $0) } ?? .hyperwhisper

        // Validate API key (except for HyperWhisper Cloud which uses license/device ID)
        guard let settings = settingsManager else {
            throw TranscriptionError.apiKeyMissing(provider: cloudProviderType.displayName)
        }

        if cloudProviderType.requiresAPIKey {
            let apiKey = settings.apiKey(for: cloudProviderType)
            guard !apiKey.isEmpty else {
                throw TranscriptionError.apiKeyMissing(provider: cloudProviderType.displayName)
            }

            // Configure selected cloud provider
            switch cloudProviderType {
            case .hyperwhisper, .microsoftAzureSpeech, .googleSpeech:
                // Unreachable: these providers return `false` from `requiresAPIKey`
                // and are filtered by the outer guard. Kept explicit so the switch
                // stays exhaustive without a `default` arm that would silently
                // swallow future providers; `assertionFailure` catches a
                // regression where the outer guard is loosened.
                assertionFailure("BYOK-free providers should not enter the API-key configuration switch")
            case .openai, .groq:
                cloudProvider.configure(apiKey: apiKey, provider: cloudProviderType)
            case .deepgram:
                deepgramProvider.configure(apiKey: apiKey)
            case .assemblyAI:
                assemblyAIProvider.configure(apiKey: apiKey)
            case .elevenLabs:
                elevenLabsProvider.configure(apiKey: apiKey)
            case .mistral:
                mistralProvider.configure(apiKey: apiKey)
            case .soniox:
                sonioxProvider.configure(apiKey: apiKey)
            case .gemini:
                geminiTranscriptionProvider.configure(apiKey: apiKey)
            case .grok:
                grokSTTProvider.configure(apiKey: apiKey)
            }
        }

        // Select cloud provider instance
        let provider: TranscriptionProvider
        switch cloudProviderType {
        case .hyperwhisper:
            // Initialize HyperWhisper Cloud provider if needed
            if hyperwhisperCloudProvider == nil,
               let licenseManager = licenseManager,
               let creditManager = creditManager {
                hyperwhisperCloudProvider = HyperWhisperCloudProvider(
                    licenseManager: licenseManager,
                    creditManager: creditManager,
                    settingsManager: settingsManager
                )
            }
            guard let hwProvider = hyperwhisperCloudProvider else {
                // SENTRY BREADCRUMB: Track HyperWhisper Cloud provider initialization failure
                SentryService.addBreadcrumb(
                    message: "HyperWhisper Cloud provider failed to initialize",
                    category: "transcription.provider",
                    data: [
                        "provider": "HyperWhisper Cloud",
                        "hasLicenseManager": licenseManager != nil,
                        "hasCreditManager": creditManager != nil,
                        "reason": "nilProvider"
                    ]
                )
                throw TranscriptionError.providerNotAvailable(provider: "HyperWhisper Cloud", reason: "Failed to initialize provider")
            }
            provider = hwProvider
        case .openai, .groq:
            provider = cloudProvider
        case .deepgram:
            provider = deepgramProvider
        case .assemblyAI:
            provider = assemblyAIProvider
        case .elevenLabs:
            provider = elevenLabsProvider
        case .mistral:
            provider = mistralProvider
        case .soniox:
            provider = sonioxProvider
        case .gemini:
            provider = geminiTranscriptionProvider
        case .grok:
            provider = grokSTTProvider
        case .microsoftAzureSpeech:
            ensureAzureMAIProvider()
            guard let azure = azureMAIProvider else {
                throw TranscriptionError.providerNotAvailable(provider: "Microsoft MAI-Transcribe", reason: "Failed to initialize provider")
            }
            provider = azure
        case .googleSpeech:
            ensureGoogleChirpProvider()
            guard let google = googleChirpProvider else {
                throw TranscriptionError.providerNotAvailable(provider: "Google Chirp 3", reason: "Failed to initialize provider")
            }
            provider = google
        }

        // Check provider health before use
        if let healthManager = providerHealthManager {
            let status = await healthManager.ensureHealthy(cloudProviderType)
            if !status.isHealthy {
                AppLogger.network.error("Cloud provider not healthy · provider=\(cloudProviderType.displayName, privacy: .public) · status=\(status.statusText, privacy: .public)")
                throw errorForHealthStatus(status, providerDisplayName: cloudProviderType.displayName)
            }
        }

        // Verify provider is available
        guard provider.isAvailable else {
            AppLogger.transcription.error("❌ Provider not available: \(provider.name)")

            // SENTRY BREADCRUMB: Track provider availability check failure
            SentryService.addBreadcrumb(
                message: "Provider isAvailable check failed",
                category: "transcription.provider",
                data: [
                    "provider": provider.name,
                    "cloudProviderType": cloudProviderType.rawValue,
                    "isAvailable": false,
                    "reason": "isAvailableReturnedFalse"
                ]
            )

            throw TranscriptionError.providerNotAvailable(provider: provider.name, reason: "Provider is not configured or ready")
        }

        // Check network connectivity for cloud providers
        if !NetworkStatus.shared.isOnline {
            throw TranscriptionError.transientNetwork(details: "No internet connection")
        }

        return provider
    }

    /// Check post-processing provider health
    /// - Parameter mode: Mode containing post-processing settings
    /// - Throws: TranscriptionError if provider unhealthy
    func checkPostProcessingProviderHealth(for mode: Mode?) async throws {
        let processingMode = mode.flatMap { PostProcessingMode(rawValue: $0.postProcessingMode) } ?? .off
        guard processingMode != .off else { return }

        let resolvedPostProcessingProviderId: String = {
            if processingMode == .local {
                return PostProcessingProvider.localLLM.rawValue
            }
            return mode?.postProcessingProvider ?? processingMode.defaultProvider?.rawValue ?? PostProcessingProvider.hyperwhisper.rawValue
        }()

        let resolvedPostProcessingProvider = PostProcessingProvider(rawValue: resolvedPostProcessingProviderId)

        // Skip health check for providers that don't need it
        guard let postProvider = resolvedPostProcessingProvider,
              postProvider != .hyperwhisper,
              postProvider.requiresHealthCheck else {
            return
        }

        // Local LLM: probe the llama-server runtime directly. The standard cloud
        // health manager doesn't cover the embedded runtime, so we run the same
        // ensureRunning() call AIPostProcessor would make — if the GGUF is
        // corrupt or missing, this throws before transcription completes and
        // the existing inline-error UI surfaces it.
        if postProvider == .localLLM {
            try await runLocalLLMHealthCheck(for: mode)
            return
        }

        // Run health check
        if let healthManager = providerHealthManager {
            let status = await healthManager.ensureHealthy(postProvider)
            if !status.isHealthy {
                AppLogger.network.error("Post-processing provider not healthy · provider=\(postProvider.displayName, privacy: .public) · status=\(status.statusText, privacy: .public)")
                throw errorForHealthStatus(status, providerDisplayName: postProvider.displayName)
            }
        }
    }

    /// Probe the embedded llama-server runtime for the model the given mode would use.
    /// Throws `TranscriptionError.localRuntimeUnavailable` if the runtime cannot start.
    private func runLocalLLMHealthCheck(for mode: Mode?) async throws {
        guard let manager = localModelManager else {
            throw TranscriptionError.localRuntimeUnavailable(reason: "manager unavailable")
        }
        let installed = manager.downloadedModels
        guard !installed.isEmpty else {
            throw TranscriptionError.localRuntimeUnavailable(reason: "no local model downloaded")
        }

        let requestedId = mode?.languageModel ?? ""
        let resolved = installed.first(where: { $0.id == requestedId }) ?? installed.first!
        guard let modelURL = resolved.localURL else {
            throw TranscriptionError.localRuntimeUnavailable(reason: "model file missing on disk")
        }
        guard let server = llamaServerController else {
            throw TranscriptionError.localRuntimeUnavailable(reason: "controller unavailable")
        }

        do {
            _ = try await server.ensureRunning(modelId: resolved.id, modelURL: modelURL)
        } catch {
            AppLogger.network.error("Local LLM pre-flight failed · model=\(resolved.id, privacy: .public) · error=\(error.localizedDescription, privacy: .public)")
            throw TranscriptionError.localRuntimeUnavailable(reason: error.localizedDescription)
        }
    }

    // MARK: - Mode-less Resolution (Local API)

    /// Resolve a provider directly from string identifiers, without a stored
    /// `Mode`. Used by the in-app Local HTTP API where the caller specifies
    /// engine + model in the request body instead of referencing a saved mode.
    ///
    /// Engine identifiers (case-insensitive):
    /// - Local: `whisperLocal`, `whisper`, `parakeet`, `qwen3Asr`, `appleSpeech`.
    /// - Cloud: the CloudProvider rawValue (`openai`, `groq`, `deepgram`,
    ///   `assemblyai`, `elevenlabs`, `mistral`, `soniox`, `gemini`, `grok`,
    ///   `hyperwhisper`, `microsoftazurespeech`, `googlespeech`).
    ///   `cloud` is an alias for `hyperwhisper`. All identifiers are matched
    ///   case-insensitively via `engine.lowercased()`.
    ///
    /// - Parameters:
    ///   - engine: Engine identifier (see above).
    ///   - model: Engine-specific model id (e.g. "large-v3", "gpt-4o-transcribe").
    ///     For Apple Speech and Qwen3 ASR the model arg is ignored — those
    ///     engines have a single model.
    ///   - language: BCP-47 language code, or "auto"/nil for auto-detect.
    func resolveProvider(engine: String, model: String?, language: String?) async throws -> TranscriptionProvider {
        let normalizedEngine = engine.lowercased()
        let resolvedLanguage: String? = (language?.lowercased() == "auto") ? nil : language

        // Cloud engine? Map directly via CloudProvider rawValue, plus a "cloud" alias.
        let cloudType: CloudProvider?
        if normalizedEngine == "cloud" {
            cloudType = .hyperwhisper
        } else {
            cloudType = CloudProvider(rawValue: normalizedEngine)
        }
        if let cloudType {
            return try await selectCloudProviderForLocalAPI(cloudProviderType: cloudType)
        }

        // Local engine? Map to a model id understood by `selectLocalProvider`.
        let modelString: String
        switch normalizedEngine {
        case "whisperlocal", "whisper", "libwhisper":
            guard let m = model?.trimmingCharacters(in: .whitespacesAndNewlines), !m.isEmpty else {
                throw TranscriptionError.providerNotAvailable(provider: "Whisper", reason: "Missing 'model' for whisperLocal engine")
            }
            modelString = m
        case "parakeet":
            modelString = model?.isEmpty == false ? model! : "parakeet-tdt-v3-multilingual"
        case "qwen3asr", "qwen3", "qwen3-asr":
            modelString = Qwen3AsrModelManager.Constants.modelId
        case "applespeech", "apple", "apple-speech", "apple-speech-analyzer", "speech-analyzer":
            modelString = "apple-speech-analyzer"
        default:
            throw TranscriptionError.providerNotAvailable(provider: engine, reason: "Unknown engine '\(engine)'")
        }

        return try await selectLocalProvider(modelId: modelString, language: resolvedLanguage)
    }

    /// Cloud-side counterpart used by `resolveProvider`. Mirrors the cloud
    /// branch of `selectProvider(for:vocabulary:)` but takes a `CloudProvider`
    /// directly. Kept in this file so it can read the same private provider
    /// instances and configuration helpers.
    private func selectCloudProviderForLocalAPI(cloudProviderType: CloudProvider) async throws -> TranscriptionProvider {
        guard let settings = settingsManager else {
            throw TranscriptionError.apiKeyMissing(provider: cloudProviderType.displayName)
        }

        if cloudProviderType.requiresAPIKey {
            let apiKey = settings.apiKey(for: cloudProviderType)
            guard !apiKey.isEmpty else {
                throw TranscriptionError.apiKeyMissing(provider: cloudProviderType.displayName)
            }
            switch cloudProviderType {
            case .hyperwhisper, .microsoftAzureSpeech, .googleSpeech:
                // Unreachable: filtered by the outer `requiresAPIKey` guard.
                // Mirrors the same dead-arm pattern in `selectProvider(for:vocabulary:)`.
                assertionFailure("BYOK-free providers should not enter the API-key configuration switch")
            case .openai, .groq:
                cloudProvider.configure(apiKey: apiKey, provider: cloudProviderType)
            case .deepgram:
                deepgramProvider.configure(apiKey: apiKey)
            case .assemblyAI:
                assemblyAIProvider.configure(apiKey: apiKey)
            case .elevenLabs:
                elevenLabsProvider.configure(apiKey: apiKey)
            case .mistral:
                mistralProvider.configure(apiKey: apiKey)
            case .soniox:
                sonioxProvider.configure(apiKey: apiKey)
            case .gemini:
                geminiTranscriptionProvider.configure(apiKey: apiKey)
            case .grok:
                grokSTTProvider.configure(apiKey: apiKey)
            }
        }

        let provider: TranscriptionProvider
        switch cloudProviderType {
        case .hyperwhisper:
            ensureHyperWhisperCloudProvider()
            guard let hw = hyperwhisperCloudProvider else {
                throw TranscriptionError.providerNotAvailable(provider: "HyperWhisper Cloud", reason: "Failed to initialize provider")
            }
            provider = hw
        case .openai, .groq:
            provider = cloudProvider
        case .deepgram:
            provider = deepgramProvider
        case .assemblyAI:
            provider = assemblyAIProvider
        case .elevenLabs:
            provider = elevenLabsProvider
        case .mistral:
            provider = mistralProvider
        case .soniox:
            provider = sonioxProvider
        case .gemini:
            provider = geminiTranscriptionProvider
        case .grok:
            provider = grokSTTProvider
        case .microsoftAzureSpeech:
            ensureAzureMAIProvider()
            guard let azure = azureMAIProvider else {
                throw TranscriptionError.providerNotAvailable(provider: "Microsoft MAI-Transcribe", reason: "Failed to initialize provider")
            }
            provider = azure
        case .googleSpeech:
            ensureGoogleChirpProvider()
            guard let google = googleChirpProvider else {
                throw TranscriptionError.providerNotAvailable(provider: "Google Chirp 3", reason: "Failed to initialize provider")
            }
            provider = google
        }

        if let healthManager = providerHealthManager {
            let status = await healthManager.ensureHealthy(cloudProviderType)
            if !status.isHealthy {
                throw errorForHealthStatus(status, providerDisplayName: cloudProviderType.displayName)
            }
        }

        guard provider.isAvailable else {
            throw TranscriptionError.providerNotAvailable(provider: provider.name, reason: "Provider is not configured or ready")
        }

        if !NetworkStatus.shared.isOnline {
            throw TranscriptionError.transientNetwork(details: "No internet connection")
        }

        return provider
    }

    // MARK: - Connection Pre-Warm

    /// Returns true if `/transcribe` for the given mode inputs would resolve to
    /// the HyperWhisper Cloud Fly backend — including the Azure-MAI and
    /// Google-Chirp providers, which both pin `X-STT-Provider` against the
    /// same host. Mirrors the resolution at the top of `selectProvider`.
    func isHyperWhisperCloudActive(model: String?, cloudProvider: String?) -> Bool {
        let modelString = (model ?? "base").lowercased()
        guard modelString == "cloud" else { return false }
        let resolved = cloudProvider.flatMap { CloudProvider(rawValue: $0) } ?? .hyperwhisper
        return resolved.routesViaHyperWhisperCloud
    }

    /// Fires a connection warmup only if HyperWhisper Cloud would be selected for
    /// the given mode. Safe to call from any hotkey-down path. Fire-and-forget.
    /// Reuses the cached provider instance (which owns the shared `URLSession`),
    /// constructing it if needed so the first warmup also primes the pool.
    func prewarmCloudConnectionIfActive(model: String?, cloudProvider: String?) {
        guard isHyperWhisperCloudActive(model: model, cloudProvider: cloudProvider) else { return }
        ensureHyperWhisperCloudProvider()
        hyperwhisperCloudProvider?.prewarmConnection()
    }

    /// Variant that bypasses the 60s warmup debounce. Used by the foreground
    /// keepalive ticker so its ~45s cadence isn't absorbed into the debounce.
    /// Hotkey paths must keep using `prewarmCloudConnectionIfActive`.
    func prewarmCloudConnectionIfActiveForced(model: String?, cloudProvider: String?) {
        guard isHyperWhisperCloudActive(model: model, cloudProvider: cloudProvider) else { return }
        ensureHyperWhisperCloudProvider()
        hyperwhisperCloudProvider?.prewarmConnectionForced()
    }

    private func ensureHyperWhisperCloudProvider() {
        if hyperwhisperCloudProvider == nil,
           let licenseManager = licenseManager,
           let creditManager = creditManager {
            hyperwhisperCloudProvider = HyperWhisperCloudProvider(
                licenseManager: licenseManager,
                creditManager: creditManager,
                settingsManager: settingsManager
            )
        }
    }

    private func ensureAzureMAIProvider() {
        if azureMAIProvider == nil,
           let licenseManager = licenseManager,
           let creditManager = creditManager {
            azureMAIProvider = AzureMAIProvider(
                licenseManager: licenseManager,
                creditManager: creditManager
            )
        }
    }

    private func ensureGoogleChirpProvider() {
        if googleChirpProvider == nil,
           let licenseManager = licenseManager,
           let creditManager = creditManager {
            googleChirpProvider = GoogleChirpProvider(
                licenseManager: licenseManager,
                creditManager: creditManager
            )
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

    /// Check if a model ID is the Qwen3 ASR model
    private func isQwen3AsrModel(_ modelId: String) -> Bool {
        modelId == Qwen3AsrModelManager.Constants.modelId
    }

    /// Check if a model ID is a Nemotron 3.5 ASR model (latin or multilingual)
    /// Prefix-matches "nemotron-asr-3.5-" so both variants route to NemotronProvider.
    private func isNemotronModel(_ modelId: String) -> Bool {
        modelId.lowercased().hasPrefix("nemotron-asr-3.5-")
    }

    /// Check if a model ID is the Apple SpeechAnalyzer model
    private func isSpeechAnalyzerModel(_ modelId: String) -> Bool {
        modelId == "apple-speech-analyzer"
    }

    /// Select local provider based on model ID
    /// DECISION TREE:
    /// - If model ID maps to WhisperModel enum → Use LibWhisperProvider
    /// - If model ID is Parakeet identifier (V2 or V3) → Use ParakeetProvider
    /// - Otherwise → Throw; we no longer silently fall back to a different local model
    ///
    /// - Parameters:
    ///   - modelId: Model identifier string
    ///   - language: Optional language code
    /// - Returns: Configured local provider
    /// - Throws: TranscriptionError if provider unavailable
    private func selectLocalProvider(modelId: String, language: String?) async throws -> TranscriptionProvider {
        guard let localProvider else {
            AppLogger.transcription.error("Local provider unavailable")
            throw TranscriptionError.providerNotAvailable(provider: "Local Whisper", reason: "Local provider not initialized")
        }

        // Try to map to WhisperModel first
        if let mapped = mapModelIdToWhisperModel(modelId) {
            try await MainActor.run {
                localProvider.setModel(mapped)
            }
            AppLogger.transcription.info("✅ Model set to: \(mapped.rawValue)")
            return localProvider
        }

        // Check for Apple SpeechAnalyzer model
        if isSpeechAnalyzerModel(modelId),
           let provider = speechAnalyzerProvider,
           provider.isAvailable {
            AppLogger.transcription.info("✅ SpeechAnalyzer provider selected")
            return provider
        }

        // Check for Qwen3 ASR model
        if #available(macOS 15.0, *),
           isQwen3AsrModel(modelId),
           let provider = qwen3AsrProvider as? Qwen3AsrProvider {
            if let qwen3AsrModelManager {
                await MainActor.run {
                    qwen3AsrModelManager.refreshState()
                }
            }
            try await provider.prepareIfNeeded(language: language, modelId: modelId)
            AppLogger.transcription.info("✅ Qwen3 ASR provider selected for model: \(modelId)")
            return provider
        }

        // Check for Nemotron 3.5 model (latin or multilingual)
        // Both variants are handled by NemotronProvider with variant-aware loading.
        if #available(macOS 14.0, *),
           isNemotronModel(modelId),
           let provider = nemotronProvider as? NemotronProvider {
            if let nemotronModelManager {
                await MainActor.run {
                    nemotronModelManager.refreshState()
                }
            }
            try await provider.prepareIfNeeded(language: language, modelId: modelId)
            AppLogger.transcription.info("✅ Nemotron provider selected for model: \(modelId)")
            return provider
        }

        // Check for Parakeet model (V2 or V3)
        // Both versions are handled by ParakeetProvider with version-aware loading
        if #available(macOS 13.0, *),
           isParakeetModel(modelId),
           let parakeetProvider {
            if let parakeetModelManager {
                await MainActor.run {
                    parakeetModelManager.refreshState()
                }
            }
            // Pass specific modelId to prepare the correct version (V2 or V3)
            try await parakeetProvider.prepareIfNeeded(language: language, modelId: modelId)
            AppLogger.transcription.info("✅ Parakeet provider selected for model: \(modelId)")
            return parakeetProvider
        }

        AppLogger.transcription.warning("⚠️ Unknown local model: \(modelId, privacy: .public)")
        throw TranscriptionError.providerNotAvailable(provider: "Local", reason: "Unknown local model: \(modelId)")
    }

    /// Extract language from mode (converts "auto" to nil)
    private func extractLanguage(from mode: Mode?) -> String? {
        guard let raw = mode?.language?.lowercased() else { return nil }
        return raw == "auto" ? nil : raw
    }

    /// Translate a provider health status into the specific TranscriptionError
    /// HEALTH→ERROR FLOW:
    /// 1. CloudProviderHealthManager returns an unhealthy status
    /// 2. We map that enum back into the human-readable error enum
    /// 3. The thrown error bubbles up and presents the relevant toast
    ///
    /// - Parameters:
    ///   - status: Health status from provider health manager
    ///   - providerDisplayName: Human-readable provider name
    /// - Returns: Appropriate TranscriptionError
    private func errorForHealthStatus(_ status: ProviderHealth, providerDisplayName: String) -> TranscriptionError {
        switch status {
        case .unauthorized:
            return .unauthorized(provider: providerDisplayName)
        case .unreachable:
            return .transientNetwork(details: "Provider unreachable")
        case .notInstalled:
            return .modelNotDownloaded
        case .checking, .unknown:
            return .providerNotAvailable(provider: providerDisplayName, reason: "Provider health check failed")
        case .healthy:
            // Defensive fallback – should never happen
            return .providerNotAvailable(provider: providerDisplayName, reason: "Unexpected health status")
        }
    }
}
