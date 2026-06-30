//
//  AIPostProcessor.swift
//  hyperwhisper
//
//  AI Post-Processing Service
//  Handles both streaming and non-streaming post-processing of transcribed text
//  using OpenAI's Chat Completions API
//

import Foundation
import SwiftUI

/// Per-invocation carrier for "did an LLM actually run and mutate the text?".
///
/// `AIPostProcessor` is `@MainActor`, so concurrent callers never run in true
/// parallel — but every `await` inside a post-processing call is a suspension
/// point at which *another* call can interleave. That makes the shared
/// `didMutateLastRun` property unreliable across overlapping requests (e.g. two
/// Local API `/post-process` calls): one request can observe a flag written by
/// the other. A caller that needs an honest, request-scoped answer passes its
/// own `MutationSignal` and reads `signal.didMutate` after the call returns —
/// the signal is captured by that call's closures alone, so it can't be
/// clobbered by a concurrent invocation.
final class MutationSignal {
    var didMutate: Bool = false
}

/// AI Post-Processor for transcribed text
/// This service enhances transcribed text based on mode presets using OpenAI's API
/// 
/// Key Features:
/// - Non-streaming post-processing for simple enhancement
/// - SSE streaming for real-time text processing with UI updates
/// - Uses PromptBuilder for consistent system prompts
/// - Callbacks for streaming state to avoid direct AppState dependency
@MainActor
class AIPostProcessor: ObservableObject {
    
    // MARK: - Callbacks
    
    /// Callback for streaming state changes
    var onStreamingStateChange: ((Bool) -> Void)?
    
    /// Callback for streaming text updates
    var onStreamingTextUpdate: ((String) -> Void)?

    /// Callback for post-processing errors that should be shown to user
    /// Called when a recoverable error occurs (e.g., invalid API credentials)
    /// The method will still return original text as fallback
    var onPostProcessingError: ((TranscriptionError) -> Void)?

    /// Set to `true` when the most recent `performAIPostProcessing*` call actually
    /// produced post-processed text (i.e. an LLM ran and returned a non-empty result).
    /// Stays `false` when a failure path returned the raw transcript unchanged.
    /// Read by the in-app pipeline to set `wasPostProcessed` honestly.
    ///
    /// NOTE: this shared property is only reliable when calls do not overlap
    /// (the in-app pipeline post-processes one recording at a time). Callers that
    /// can run concurrently — e.g. the Local API — MUST instead pass their own
    /// `MutationSignal` and read its `didMutate`, because an interleaved call can
    /// reset/set this flag while the awaiting call is suspended. See `MutationSignal`.
    private(set) var didMutateLastRun: Bool = false

    // MARK: - Private Properties

    /// Settings manager for API keys
    private let settingsManager: SettingsManager

    /// License manager for HyperWhisper Cloud authentication
    /// Required for standalone HyperWhisper Cloud post-processing
    weak var licenseManager: LicenseManager?

    /// Gemma 4 sampling parameters from Google's official documentation.
    /// Source: https://ai.google.dev/gemma/docs/core
    private let localLLMSamplingParameters: [String: Any] = [
        "temperature": 1.0,
        "top_p": 0.95,
        "top_k": 40,
        "min_p": 0.0
    ]

    /// Resolver for on-device local models
    weak var localModelManager: LocalModelManager?

    /// Controller that manages the embedded llama.cpp runtime for local models
    weak var llamaServerController: LlamaServerController?

    /// Manager for custom OpenAI-compatible post-processing endpoints
    weak var customPostProcessingManager: CustomPostProcessingManager?

    // MARK: - Initialization
    
    init(settingsManager: SettingsManager) {
        self.settingsManager = settingsManager
    }
    
    // MARK: - Public Methods
    
    /// Perform AI post-processing using OpenAI Chat Completions API
    /// AI POST-PROCESSING PIPELINE:
    /// This method sends transcribed text to OpenAI's Chat Completions API for enhancement
    /// based on the mode's preset type (message, mail, note, meeting, custom)
    ///
    /// - Parameters:
    ///   - text: The raw transcribed text to process
    ///   - mode: The transcription mode containing preset and processing settings
    ///   - applicationContext: Optional pre-captured application context (if nil, will gather fresh)
    /// - Returns: The AI-enhanced text, or original text if post-processing fails
    func performAIPostProcessing(text: String, mode: Mode?, applicationContext: ApplicationContext? = nil, mutationSignal: MutationSignal? = nil) async throws -> String {
        let signal = mutationSignal ?? MutationSignal()
        didMutateLastRun = false
        // CHECK IF POST-PROCESSING IS ENABLED:
        // Only perform AI post-processing if mode is set to cloud
        guard let mode = mode else {
            AppLogger.transcription.debug("AI post-processing disabled: mode is nil")
            return text
        }

        let processingMode = PostProcessingMode(rawValue: mode.postProcessingMode) ?? .off
        guard processingMode != .off else {
            AppLogger.transcription.debug("AI post-processing disabled for mode: \(mode.name ?? "unknown", privacy: .public)")
            return text
        }
        
        // PRESET CHECK:
        // Get the preset for formatting instructions
        guard let preset = mode.preset else {
            AppLogger.transcription.debug("No preset defined for mode: \(mode.name ?? "unknown", privacy: .public)")
            return text
        }
        
        // If there's nothing to process, bail early
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            AppLogger.transcription.debug("Skipping AI post-processing: transcript is empty")
            return text
        }
        AppLogger.transcription.debug("Transcript length for post-processing: \(trimmed.count, privacy: .public) characters")
        
        // DETERMINE PROVIDER:
        // Get the post-processing provider from mode (default to HyperWhisper Cloud)
        let defaultProvider = processingMode.defaultProvider ?? .hyperwhisper
        var providerString = mode.postProcessingProvider ?? defaultProvider.rawValue
        if processingMode == .local {
            providerString = PostProcessingProvider.localLLM.rawValue
        }

        // CUSTOM ENDPOINT CHECK:
        // Custom endpoints use the format "custom:<uuid>" - route them to the custom handler
        if CustomPostProcessingEndpoint.isCustomProviderString(providerString) {
            return try await performCustomEndpointPostProcessing(
                text: trimmed,
                providerString: providerString,
                mode: mode,
                applicationContext: applicationContext,
                mutationSignal: signal
            )
        }

        guard let provider = PostProcessingProvider(rawValue: providerString) else {
            AppLogger.transcription.warning("Invalid post-processing provider: \(providerString, privacy: .public)")
            return text
        }

        // HYPERWHISPER CLOUD STANDALONE POST-PROCESSING:
        // HyperWhisper Cloud can now be used as a standalone post-processing provider
        // independently of the transcription provider. This enables:
        // - Local Whisper transcription + HyperWhisper Cloud post-processing
        // - OpenAI transcription + HyperWhisper Cloud post-processing
        // - etc.
        if provider == .hyperwhisper {
            return try await performHyperWhisperCloudPostProcessing(
                text: trimmed,
                mode: mode,
                applicationContext: applicationContext,
                mutationSignal: signal
            )
        }

        // API KEY CHECK:
        // Ensure API key is configured for the selected provider
        // HyperWhisper Cloud and local providers don't require API keys
        let apiKey = settingsManager.postProcessingAPIKey(for: provider)
        if provider.requiresAPIKey {
            guard !apiKey.isEmpty else {
                AppLogger.transcription.warning("No API key for post-processing provider: \(provider.displayName, privacy: .public)")
                return text
            }
        }
        
        // CONNECTIVITY CHECK:
        // If offline, skip AI post-processing and return original text
        if processingMode.requiresInternet && !NetworkStatus.shared.isOnline {
            AppLogger.transcription.warning("Offline; skipping AI post-processing and returning original text")
            return text
        }
        
        // Use the language model from the mode, with provider-aware fallback
        var languageModel = mode.languageModel ?? ""

        if provider == .localLLM {
            guard let localManager = localModelManager else {
                AppLogger.transcription.info("AIPostProcessor [non-streaming]: localModelManager is nil — skipping local LLM post-processing")
                return text
            }
            let installedModels = localManager.downloadedModels
            guard !installedModels.isEmpty else {
                AppLogger.transcription.warning("Local LLM selected but no downloaded weights available; returning original text.")
                return text
            }
            if !installedModels.contains(where: { $0.id == languageModel }) {
                let fallback = installedModels.first!
                AppLogger.transcription.info("Using installed local model fallback: \(fallback.id, privacy: .public)")
                languageModel = fallback.id
            }
            guard let server = llamaServerController else {
                AppLogger.transcription.error("Server controller unavailable for local LLM; returning original text.")
                onPostProcessingError?(TranscriptionError.localRuntimeUnavailable(reason: "controller unavailable"))
                return text
            }
            do {
                let resolved = installedModels.first(where: { $0.id == languageModel }) ?? installedModels.first!
                guard let modelURL = resolved.localURL else {
                    AppLogger.transcription.error("Local model file not found on disk; returning original text.")
                    onPostProcessingError?(TranscriptionError.localRuntimeUnavailable(reason: "model file missing on disk"))
                    return text
                }
                let resolvedId = try await ensureLocalRuntimeRunning(
                    server: server,
                    resolved: resolved,
                    modelURL: modelURL
                )
                if resolvedId != languageModel {
                    AppLogger.transcription.info("Using available local model: \(resolvedId, privacy: .public)")
                    languageModel = resolvedId
                }
            } catch {
                AppLogger.transcription.error("Local LLM runtime not ready: \(error.localizedDescription, privacy: .public)")
                onPostProcessingError?(TranscriptionError.localRuntimeUnavailable(reason: error.localizedDescription))
                return text
            }
        }

        // Claim LLM residency for the rest of this pass so a memory-pressure
        // event can't stop llama-server mid-request. The local LLM has no
        // markBusy/markIdle anywhere else — without this, a CRITICAL pressure
        // event would run the eviction closure (tier .llm) and stop the server
        // in the middle of an active chat-completion request, failing the pass.
        // Released on every exit via defer.
        let claimedLocalLLM = (provider == .localLLM)
        if claimedLocalLLM {
            await ModelResidencyRegistry.shared.markBusy(id: LlamaServerController.residencyId)
        }
        defer {
            if claimedLocalLLM {
                // markIdle is actor-isolated; defer is synchronous — fire-and-forget.
                Task { await ModelResidencyRegistry.shared.markIdle(id: LlamaServerController.residencyId) }
            }
        }

        if provider != .localLLM {
            // Resolve deprecated model IDs to their replacements
            languageModel = PostProcessingModels.resolvedModelId(languageModel, provider: provider)

            // Validate the model belongs to the selected provider, fallback to provider default if not
            if languageModel.isEmpty || PostProcessingModels.model(withId: languageModel, provider: provider) == nil {
                let fallback = PostProcessingModels.defaultModel(for: provider)?.id ?? provider.defaultModel
                if fallback != languageModel {
                    AppLogger.transcription.info("Falling back to default \(provider.displayName, privacy: .public) model: \(fallback, privacy: .public)")
                }
                languageModel = fallback
            }
        }
        
        AppLogger.transcription.info("Starting AI post-processing for preset: \(preset, privacy: .public)")
        AppLogger.transcription.info("Post-processing provider: \(provider.displayName, privacy: .public)")
        AppLogger.transcription.info("Language model: \(languageModel, privacy: .public)")
        
        // BUILD SYSTEM PROMPT (centralized):
        // Fetch vocabulary data for improved accuracy
        let vocabularyItems = PersistenceController.shared.fetchAllVocabularyItems()
        
        // LOG APPLICATION CONTEXT:
        // Use pre-captured context if available, otherwise gather fresh.
        // Privacy: Only metadata is logged. Focused text snippet is NOT logged.
        let appContext = applicationContext ?? ApplicationContextGatherer.shared.gatherContext()
        AppLogger.transcription.info("=== APPLICATION CONTEXT ===")
        AppLogger.transcription.info("Context source: \(applicationContext != nil ? "Pre-captured at recording start" : "Fresh gather", privacy: .public)")
        AppLogger.transcription.info("Active App: \(appContext.appName, privacy: .public)")
        AppLogger.transcription.info("Bundle ID: \(appContext.bundleId, privacy: .public)")
        AppLogger.transcription.info("Category: \(appContext.category, privacy: .public)")
        AppLogger.transcription.info("Description: \(appContext.description, privacy: .public)")
        AppLogger.transcription.info("Browser Tab Title: \(appContext.browserTabTitle ?? "None", privacy: .public)")
        AppLogger.transcription.info("Context Quality: \(appContext.contextQuality, privacy: .public)")
        AppLogger.transcription.info("Focused Element Role: \(appContext.focusedElement.role ?? "None", privacy: .public)")
        AppLogger.transcription.info("Focused Element Title: \(appContext.focusedElement.title ?? "None", privacy: .public)")
        AppLogger.transcription.info("Text Input Format: \(appContext.textInputFormat, privacy: .public)")
        AppLogger.transcription.info("=== END CONTEXT ===")

        let systemPrompt = PromptBuilder.systemPrompt(for: mode, applicationContext: appContext)
        let systemInfo = PromptBuilder.systemInfo(for: mode, vocabulary: vocabularyItems, applicationContext: appContext)

        // PREPARE API REQUEST:
        let endpoint = provider.chatEndpoint
        guard let url = URL(string: endpoint) else {
            AppLogger.transcription.warning("Invalid post-processing endpoint for provider: \(provider.displayName, privacy: .public)")
            return text
        }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        if provider.requiresAPIKey {
            if provider.usesStandardAuth {
                request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
            } else {
                request.setValue(apiKey, forHTTPHeaderField: "x-api-key")
            }
        }
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.timeoutInterval = 30

        // Build user message: dynamic system info + transcript
        // System info is separated from the system prompt for prompt caching —
        // the static system prompt stays identical across requests.
        let userContent = """
        \(systemInfo)

        --TRANSCRIPT--
        \(trimmed)
        --ENDTRANSCRIPT--
        """

        // Build request body — Anthropic uses native Messages API with cache_control
        var requestBody: [String: Any]
        if provider == .anthropic {
            request.setValue("2023-06-01", forHTTPHeaderField: "anthropic-version")
            requestBody = [
                "model": languageModel,
                "max_tokens": 4096,
                "system": [
                    [
                        "type": "text",
                        "text": systemPrompt,
                        "cache_control": ["type": "ephemeral"]
                    ] as [String: Any]
                ],
                "messages": [
                    ["role": "user", "content": userContent]
                ]
            ]
        } else {
            let messages: [[String: Any]] = [
                ["role": "system", "content": systemPrompt],
                ["role": "user", "content": userContent]
            ]
            requestBody = [
                "model": languageModel,
                "messages": messages,
            ]
        }

        if provider == .localLLM {
            localLLMSamplingParameters.forEach { requestBody[$0.key] = $0.value }
            // Match Anthropic path: cap output so verbose presets can't stretch a
            // post-process run to multiple minutes. 4096 is generous for normal output.
            requestBody["max_tokens"] = 4096
        }

        request.httpBody = try JSONSerialization.data(withJSONObject: requestBody)

        // MAKE API CALL:
        // Use centralized retry logic with post-processing configuration
        let config = RetryConfiguration.postProcessing
        
        do {
            return try await performWithRetry(config: config) { attempt in
                AppLogger.network.debug("Post-processing attempt \(attempt, privacy: .public) of \(config.maxAttempts, privacy: .public)")
                AppLogger.network.info("POST \(endpoint, privacy: .public)")
                AppLogger.network.debug("Request model: \(languageModel, privacy: .public)")
                AppLogger.network.debug("Request provider: \(provider.displayName, privacy: .public)")
                AppLogger.logTranscription(.apiCall(endpoint: endpoint, status: 0))
                
                let (data, response) = try await URLSession.shared.data(for: request)
                
                guard let httpResponse = response as? HTTPURLResponse else {
                    throw TranscriptionError.invalidResponse(details: nil)
                }
                AppLogger.logTranscription(.apiCall(endpoint: endpoint, status: httpResponse.statusCode))
                
                if httpResponse.statusCode == 200 {
                    // Parse successful response — format differs by provider
                    let responseContent: String?
                    if provider == .anthropic {
                        // Anthropic native: { "content": [{ "type": "text", "text": "..." }] }
                        if let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                           let contentBlocks = json["content"] as? [[String: Any]],
                           let firstBlock = contentBlocks.first,
                           let text = firstBlock["text"] as? String {
                            responseContent = text
                        } else {
                            responseContent = nil
                        }
                    } else {
                        // OpenAI-compatible: { "choices": [{ "message": { "content": "..." } }] }
                        if let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                           let choices = json["choices"] as? [[String: Any]],
                           let firstChoice = choices.first,
                           let message = firstChoice["message"] as? [String: Any],
                           let text = message["content"] as? String {
                            responseContent = text
                        } else {
                            responseContent = nil
                        }
                    }

                    if let responseContent = responseContent {
                        let trimmedResponse = responseContent.trimmingCharacters(in: .whitespacesAndNewlines)
                        let result = TranscriptionTextProcessing.extractCleanedFromWrapped(trimmedResponse)
                        if result.isEmpty {
                            // The model didn't emit the strict <<CLEANED>> wrapper.
                            // Don't silently discard its output — fall back to the
                            // lenient strip (stray markers removed); only revert to
                            // the original transcript if even that is empty.
                            let lenient = TranscriptionTextProcessing.stripWrapperMarkers(trimmedResponse)
                            if lenient.isEmpty {
                                AppLogger.transcription.warning("Empty content returned from API; falling back to original")
                                return trimmed
                            }
                            AppLogger.transcription.info("AI post-processing completed via lenient fallback (no <<CLEANED>> wrapper)")
                            self.didMutateLastRun = true; signal.didMutate = true
                            return lenient
                        }
                        AppLogger.transcription.info("AI post-processing completed successfully")
                        AppLogger.network.info("Response from \(provider.displayName, privacy: .public): \(result.count, privacy: .public) characters")
                        self.didMutateLastRun = true; signal.didMutate = true
                        return result
                    }

                    AppLogger.network.warning("Unexpected API response format")
                    throw TranscriptionError.invalidResponse(details: nil)
                } else {
                    // Log error response and map to appropriate error type
                    let _ = String(data: data, encoding: .utf8) ?? "No response"
                    AppLogger.network.error("API Error - Status: \(httpResponse.statusCode, privacy: .public)")

                    let status = httpResponse.statusCode
                    switch status {
                    case 401, 403:
                        throw TranscriptionError.unauthorized(provider: provider.displayName)
                    case 400, 413, 415, 422:
                        throw TranscriptionError.invalidRequest
                    case 429:
                        throw TranscriptionError.invalidResponse(details: "HTTP \(status)")
                    case 500...599:
                        throw TranscriptionError.invalidResponse(details: "HTTP \(status)")
                    default:
                        throw TranscriptionError.invalidResponse(details: "HTTP \(status)")
                    }
                }
            }
        } catch {
            // FALLBACK:
            // If all retries fail, return the original text
            AppLogger.transcription.error("AI post-processing failed: \(error.localizedDescription, privacy: .public)")

            // NOTIFY USER of actionable credential errors (can be fixed in Settings)
            if let transcriptionError = error as? TranscriptionError,
               transcriptionError.shouldSurfaceInline {
                onPostProcessingError?(transcriptionError)
            } else if isLocalRuntimeNetworkFailure(error, provider: provider) {
                // Local LLM hit a connectivity/5xx failure after retries — surface so the user
                // knows post-processing was skipped (raw transcript still returned).
                let wrapped = TranscriptionError.localRuntimeUnavailable(reason: error.localizedDescription)
                AppLogger.transcription.warning("Local LLM unreachable after retries — surfacing as localRuntimeUnavailable")
                onPostProcessingError?(wrapped)
            }

            return trimmed
        }
    }

    /// Streaming variant of AI post-processing using OpenAI Chat Completions SSE
    /// Attempts streaming first; if it fails before any chunk arrives, falls back to non-streaming.
    /// If it fails after chunks arrived, throws `.streamingInterrupted` and keeps partial text in callbacks.
    func performAIPostProcessingStreaming(text: String, mode: Mode?, applicationContext: ApplicationContext? = nil, mutationSignal: MutationSignal? = nil) async throws -> String {
        let signal = mutationSignal ?? MutationSignal()
        didMutateLastRun = false
        // Only perform when enabled
        guard let mode = mode else { return text }

        let processingMode = PostProcessingMode(rawValue: mode.postProcessingMode) ?? .off
        guard processingMode != .off else { return text }

        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return text }
        
        // DETERMINE PROVIDER:
        let defaultProvider = processingMode.defaultProvider ?? .hyperwhisper
        var providerString = mode.postProcessingProvider ?? defaultProvider.rawValue
        if processingMode == .local {
            providerString = PostProcessingProvider.localLLM.rawValue
        }

        // CUSTOM ENDPOINT CHECK:
        // Custom endpoints use the format "custom:<uuid>" - route them to the custom handler
        // Custom endpoints may not support streaming, so use non-streaming for safety
        if CustomPostProcessingEndpoint.isCustomProviderString(providerString) {
            return try await performCustomEndpointPostProcessing(
                text: trimmed,
                providerString: providerString,
                mode: mode,
                applicationContext: applicationContext,
                mutationSignal: signal
            )
        }

        guard let provider = PostProcessingProvider(rawValue: providerString) else { return text }

        // HYPERWHISPER CLOUD: Use non-streaming method (backend doesn't support SSE streaming)
        if provider == .hyperwhisper {
            return try await performHyperWhisperCloudPostProcessing(
                text: trimmed,
                mode: mode,
                applicationContext: applicationContext,
                mutationSignal: signal
            )
        }

        // CLOUD PROVIDERS → non-streaming path. Post-processing output is short
        // (transcript cleanup) and cloud LLMs are fast, so SSE's live-typing benefit
        // doesn't justify its complexity — and streaming is where the Gemini
        // coalesced-final-chunk truncation lived. Streaming is retained ONLY for the
        // on-device local LLM, where progressive output genuinely helps and the
        // llama-server EPIPE/SIGPIPE handling lives. This mirrors the Windows app,
        // which post-processes non-streaming for every provider.
        if provider != .localLLM {
            return try await performAIPostProcessing(
                text: trimmed,
                mode: mode,
                applicationContext: applicationContext,
                mutationSignal: signal
            )
        }

        // From here `provider` is guaranteed `.localLLM` — every other provider was
        // routed to the non-streaming path above. The local LLM needs no API key.
        if processingMode.requiresInternet && !NetworkStatus.shared.isOnline { return text }

        // BUILD SYSTEM PROMPT (centralized):
        // Fetch vocabulary data for improved accuracy
        let vocabularyItems = PersistenceController.shared.fetchAllVocabularyItems()
        
        // LOG APPLICATION CONTEXT (streaming):
        // Use pre-captured context if available, otherwise gather fresh
        let appContext = applicationContext ?? ApplicationContextGatherer.shared.gatherContext()
        AppLogger.transcription.info("=== APPLICATION CONTEXT (Streaming) ===")
        AppLogger.transcription.info("Context source: \(applicationContext != nil ? "Pre-captured at recording start" : "Fresh gather", privacy: .public)")
        AppLogger.transcription.info("Active App: \(appContext.appName, privacy: .public)")
        AppLogger.transcription.info("Bundle ID: \(appContext.bundleId, privacy: .public)")
        AppLogger.transcription.info("Category: \(appContext.category, privacy: .public)")
        AppLogger.transcription.info("Description: \(appContext.description, privacy: .public)")
        AppLogger.transcription.info("Browser Tab Title: \(appContext.browserTabTitle ?? "None", privacy: .public)")
        AppLogger.transcription.info("Context Quality: \(appContext.contextQuality, privacy: .public)")
        AppLogger.transcription.info("Focused Element Role: \(appContext.focusedElement.role ?? "None", privacy: .public)")
        AppLogger.transcription.info("Focused Element Title: \(appContext.focusedElement.title ?? "None", privacy: .public)")
        AppLogger.transcription.info("Text Input Format: \(appContext.textInputFormat, privacy: .public)")
        AppLogger.transcription.info("=== END CONTEXT ===")

        let systemPrompt = PromptBuilder.systemPrompt(for: mode, applicationContext: appContext)
        let systemInfo = PromptBuilder.systemInfo(for: mode, vocabulary: vocabularyItems, applicationContext: appContext)

        let endpoint = provider.chatEndpoint
        guard let url = URL(string: endpoint) else { return text }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("text/event-stream", forHTTPHeaderField: "Accept")
        // Belt-and-suspenders with the drain-through-EOF loop below: tells
        // cpp-httplib not to expect keep-alive so it doesn't write to a
        // half-closed socket and crash with EPIPE/SIGPIPE.
        request.setValue("close", forHTTPHeaderField: "Connection")
        request.timeoutInterval = 60

        // Build user message: dynamic system info + transcript
        let userContent = """
        \(systemInfo)

        --TRANSCRIPT--
        \(trimmed)
        --ENDTRANSCRIPT--
        """
        
        // Use the language model from the mode, with provider-aware fallback
        var languageModel = mode.languageModel ?? ""

        guard let localManager = localModelManager else {
            AppLogger.transcription.info("AIPostProcessor [streaming]: localModelManager is nil — skipping local LLM post-processing")
            return text
        }
        let installedModels = localManager.downloadedModels
        guard !installedModels.isEmpty else {
            AppLogger.transcription.warning("Local LLM selected but no downloaded weights available; returning original text.")
            return text
        }
        if !installedModels.contains(where: { $0.id == languageModel }) {
            let fallback = installedModels.first!
            AppLogger.transcription.info("Using installed local model fallback: \(fallback.id, privacy: .public)")
            languageModel = fallback.id
        }
        guard let server = llamaServerController else {
            AppLogger.transcription.error("Server controller unavailable for local LLM; returning original text.")
            onPostProcessingError?(TranscriptionError.localRuntimeUnavailable(reason: "controller unavailable"))
            return text
        }
        do {
            let resolved = installedModels.first(where: { $0.id == languageModel }) ?? installedModels.first!
            guard let modelURL = resolved.localURL else {
                AppLogger.transcription.error("Local model file not found on disk; returning original text.")
                onPostProcessingError?(TranscriptionError.localRuntimeUnavailable(reason: "model file missing on disk"))
                return text
            }
            let resolvedId = try await server.ensureRunning(
                modelId: resolved.id,
                modelURL: modelURL
            )
            if resolvedId != languageModel {
                AppLogger.transcription.info("Using available local model: \(resolvedId, privacy: .public)")
                languageModel = resolvedId
            }
        } catch {
            AppLogger.transcription.error("Local LLM runtime not ready: \(error.localizedDescription, privacy: .public)")
            onPostProcessingError?(TranscriptionError.localRuntimeUnavailable(reason: error.localizedDescription))
            return text
        }

        // Claim LLM residency for the rest of this streaming pass (including the
        // SSE stream and any non-streaming fallback) so a memory-pressure event
        // can't stop llama-server mid-request. See the non-streaming variant for
        // the full rationale. Released on every exit via defer.
        await ModelResidencyRegistry.shared.markBusy(id: LlamaServerController.residencyId)
        defer {
            Task { await ModelResidencyRegistry.shared.markIdle(id: LlamaServerController.residencyId) }
        }

        // Build request body — the local LLM (llama-server) speaks the
        // OpenAI-compatible chat API. Cloud providers (incl. Anthropic's native
        // Messages API) never reach here; they were routed to the non-streaming
        // path above, so this is the only request shape the streaming path needs.
        let messages: [[String: Any]] = [
            ["role": "system", "content": systemPrompt],
            ["role": "user", "content": userContent]
        ]
        var requestBody: [String: Any] = [
            "model": languageModel,
            "messages": messages,
            "stream": true
        ]

        localLLMSamplingParameters.forEach { requestBody[$0.key] = $0.value }
        // Cap output so verbose presets can't stretch a post-process run to
        // multiple minutes. 4096 is generous for normal output.
        requestBody["max_tokens"] = 4096

        request.httpBody = try JSONSerialization.data(withJSONObject: requestBody)

        // Prepare streaming UI state via callbacks
        onStreamingStateChange?(true)
        onStreamingTextUpdate?("")

        var buffer = ""
        var receivedAnyChunk = false
        var localLLMContentDone = false
        // Tracks whether the stream ended on a real terminator (`[DONE]` or the
        // OpenAI-compatible `finish_reason`). If llama-server closes the socket
        // mid-response without a terminator, `bytes.lines` ends without throwing —
        // without this flag a truncated buffer would fall through to the success
        // path and be pasted as if complete. See the post-loop guard below.
        var sawTerminator = false
        do {
            AppLogger.network.debug("Starting streaming post-processing via SSE")
            AppLogger.network.info("POST (streaming) \(endpoint, privacy: .public)")
            AppLogger.network.debug("Request model: \(languageModel, privacy: .public)")
            AppLogger.network.debug("Request provider: \(provider.displayName, privacy: .public)")
            let (bytes, response) = try await URLSession.shared.bytes(for: request)
            guard let httpResponse = response as? HTTPURLResponse else {
                throw TranscriptionError.invalidResponse(details: nil)
            }
            guard httpResponse.statusCode == 200 else {
                // Map HTTP status codes to appropriate errors (matching non-streaming behavior)
                switch httpResponse.statusCode {
                case 401, 403:
                    throw TranscriptionError.unauthorized(provider: provider.displayName)
                case 400, 413, 415, 422:
                    throw TranscriptionError.invalidRequest
                default:
                    throw TranscriptionError.invalidResponse(details: "HTTP \(httpResponse.statusCode)")
                }
            }
            for try await line in bytes.lines {
                if line.hasPrefix(":") || line.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { continue }
                guard line.hasPrefix("data:") else { continue }
                let dataLine = String(line.dropFirst(5)).trimmingCharacters(in: .whitespaces)
                if dataLine == "[DONE]" { sawTerminator = true; break }
                guard let jsonData = dataLine.data(using: .utf8) else { continue }
                guard let obj = try? JSONSerialization.jsonObject(with: jsonData) as? [String: Any] else { continue }

                // OpenAI-compatible SSE: choices[0].delta.content. The local LLM
                // (llama-server) is the only provider that reaches the streaming
                // path; cloud providers — including Anthropic's native Messages
                // SSE — were routed to the non-streaming path above.
                guard let choices = obj["choices"] as? [[String: Any]],
                      let first = choices.first else { continue }
                // Append any content on THIS chunk BEFORE acting on finish_reason.
                // Gemini's OpenAI-compat layer coalesces the final content delta
                // and `finish_reason` into a single chunk; the previous order
                // (check finish_reason → break) dropped that last — often largest —
                // delta and truncated the result to a prefix. Standard OpenAI sends
                // the terminal finish_reason in a separate content-less chunk, so
                // reading content first is a no-op there. llama.cpp can coalesce
                // like Gemini, so this ordering protects the local path too.
                if !localLLMContentDone,
                   let delta = first["delta"] as? [String: Any],
                   let content = delta["content"] as? String, !content.isEmpty {
                    receivedAnyChunk = true
                    buffer += content
                    let display = TranscriptionTextProcessing.sanitizeStreamingBuffer(buffer)
                    onStreamingTextUpdate?(display)
                }
                if let finishReason = first["finish_reason"] as? String, !finishReason.isEmpty {
                    sawTerminator = true
                    // Don't break mid-stream — cancelling the URLSessionDataTask
                    // before llama-server's trailing `data: [DONE]` write makes the
                    // server crash with EPIPE/SIGPIPE on the next socket write.
                    // Treat finish_reason as "content done"; keep iterating until
                    // [DONE] arrives (handled above) or the server closes the socket
                    // (EOF terminates `bytes.lines` naturally).
                    localLLMContentDone = true
                    continue
                }
            }
            onStreamingStateChange?(false)
            // The stream ended without a real terminator yet we already received
            // content — the local LLM closed mid-response (e.g. after an error event,
            // or a graceful HTTP/2 close before completion). `extractCleanedFromWrapped`
            // doesn't require a closing tag, so the partial buffer would otherwise be
            // returned as a complete result and pasted. The partial post-processed text
            // only ever lived in the streaming PREVIEW surface (onStreamingTextUpdate) —
            // it was never typed into the user's app — so there is nothing to retract.
            // Deliver the RAW transcript instead of throwing .streamingInterrupted, which
            // the error handler treats as a total transcription failure (marks it
            // "failed", pastes nothing — the data-loss bug). Clear the stale preview,
            // surface the inline notice, and hand `trimmed` back so the normal paste
            // path delivers the transcript. (A terminator-less close with zero chunks
            // falls through to the empty-result fallback below, which also returns the
            // raw transcript — the correct behavior, so don't hijack it.)
            if !sawTerminator && receivedAnyChunk {
                AppLogger.transcription.warning("Streaming post-processing ended without terminator after partial output — delivering raw transcript · provider=\(provider.displayName, privacy: .public)")
                onStreamingTextUpdate?("")
                onPostProcessingError?(.localRuntimeUnavailable(reason: "Local AI stopped before finishing"))
                return trimmed
            }
            let trimmedBuffer = buffer.trimmingCharacters(in: .whitespacesAndNewlines)
            let result = TranscriptionTextProcessing.extractCleanedFromWrapped(trimmedBuffer)
            if result.isEmpty {
                // The model didn't emit the strict <<CLEANED>> wrapper. Before
                // treating this as "no content", try a lenient strip of the raw
                // buffer — a model that omitted the marker still produced
                // post-processed text we should not silently discard.
                let lenient = TranscriptionTextProcessing.stripWrapperMarkers(trimmedBuffer)
                if !lenient.isEmpty {
                    AppLogger.transcription.info("Streaming AI post-processing completed via lenient fallback (no <<CLEANED>> wrapper) · provider=\(provider.displayName, privacy: .public)")
                    didMutateLastRun = true; signal.didMutate = true
                    return lenient
                }
                // Stream finished cleanly but produced no usable cleaned content. Two ways
                // we land here: (a) server never sent a delta.content chunk, or (b) it sent
                // tokens that were all wrapper / thinking / whitespace and extractCleaned
                // returned empty. Previously we silently returned the raw transcript and the
                // pipeline reported postProcessingSkipped=true with no user feedback —
                // surface it instead so the user knows post-processing didn't run.
                let reason = receivedAnyChunk
                    ? "Model returned no usable content (empty after cleanup)"
                    : "Model returned no content"
                let bufferPreview = String(buffer.prefix(200))
                AppLogger.transcription.warning("Streaming post-processing returned empty result · provider=\(provider.displayName, privacy: .public) · receivedAnyChunk=\(receivedAnyChunk) · bufferLen=\(buffer.count) · bufferPreview=\(bufferPreview, privacy: .public)")
                if provider == .localLLM {
                    onPostProcessingError?(.localRuntimeUnavailable(reason: reason))
                }
                return trimmed
            }
            AppLogger.transcription.info("Streaming AI post-processing completed successfully")
            AppLogger.network.info("Streamed response from \(provider.displayName, privacy: .public): \(result.count, privacy: .public) characters")
            didMutateLastRun = true; signal.didMutate = true
            return result
        } catch let cancel as CancellationError {
            // Cooperative cancellation; ensure we drop streaming flag and propagate
            onStreamingStateChange?(false)
            throw cancel
        } catch {
            onStreamingStateChange?(false)
            if receivedAnyChunk {
                // Local-LLM stream threw mid-response after partial output. As above, the
                // partial text only lived in the preview surface — nothing was typed — so
                // deliver the RAW transcript instead of throwing .streamingInterrupted
                // (which the error handler treats as a total failure and pastes nothing).
                AppLogger.transcription.warning("Streaming interrupted after partial output — delivering raw transcript: \(error.localizedDescription, privacy: .public)")
                onStreamingTextUpdate?("")
                onPostProcessingError?(.localRuntimeUnavailable(reason: error.localizedDescription))
                return trimmed
            } else {
                // No chunks arrived — check if this is an actionable credential error
                AppLogger.transcription.debug("Streaming failed early: \(error.localizedDescription, privacy: .public)")

                // NOTIFY USER of actionable credential errors (can be fixed in Settings)
                // Return original text as fallback instead of retrying (avoids duplicate request)
                if let transcriptionError = error as? TranscriptionError,
                   transcriptionError.shouldSurfaceInline {
                    AppLogger.transcription.warning("Streaming post-processing failed with actionable error: \(transcriptionError.localizedDescription, privacy: .public)")
                    onPostProcessingError?(transcriptionError)
                    return trimmed
                }

                // Local LLM unreachable — non-streaming retry would hit the same dead endpoint.
                // Surface immediately and return raw transcript.
                if isLocalRuntimeNetworkFailure(error, provider: provider) {
                    let wrapped = TranscriptionError.localRuntimeUnavailable(reason: error.localizedDescription)
                    AppLogger.transcription.warning("Local LLM unreachable during streaming — surfacing as localRuntimeUnavailable")
                    onPostProcessingError?(wrapped)
                    return trimmed
                }

                // For non-actionable errors (network issues, etc.), fallback to non-streaming
                AppLogger.transcription.debug("Falling back to non-streaming implementation")
                return try await performAIPostProcessing(text: trimmed, mode: mode, applicationContext: applicationContext, mutationSignal: signal)
            }
        }
    }

    // MARK: - HyperWhisper Cloud Post-Processing

    /// Performs standalone post-processing via HyperWhisper Cloud /post-process endpoint
    ///
    /// DECOUPLED ARCHITECTURE:
    /// HyperWhisper Cloud post-processing can now be used independently of transcription provider.
    /// The /post-process endpoint has separate billing from transcription.
    ///
    /// API CONTRACT:
    /// - Endpoint: POST /post-process
    /// - Request: JSON body with { text, prompt, license_key OR device_id }
    /// - Response: { corrected, cost: { usd, credits } }
    ///
    /// - Parameters:
    ///   - text: The transcribed text to enhance
    ///   - mode: The transcription mode for building the prompt
    ///   - applicationContext: Application context for prompt building
    /// - Returns: The AI-enhanced text, or original text if processing fails
    private func performHyperWhisperCloudPostProcessing(
        text: String,
        mode: Mode?,
        applicationContext: ApplicationContext?,
        mutationSignal: MutationSignal? = nil
    ) async throws -> String {
        let signal = mutationSignal ?? MutationSignal()
        // STEP 1: Verify license manager is available
        guard let licenseManager = licenseManager else {
            AppLogger.transcription.error("HyperWhisper Cloud post-processing failed: License manager not available")
            return text
        }

        // STEP 2: Check network connectivity
        guard NetworkStatus.shared.isOnline else {
            AppLogger.transcription.warning("HyperWhisper Cloud post-processing skipped: Offline")
            return text
        }

        // STEP 3: Get authentication identifier (license key or device ID)
        let (identifier, isLicensed) = licenseManager.getTranscriptionIdentifier()
        AppLogger.network.debug("HyperWhisper Cloud post-processing · licensed=\(isLicensed, privacy: .public)")

        // STEP 4: Build system prompt
        // Mode is required for prompt building - if nil, use a minimal prompt
        guard let mode = mode else {
            AppLogger.transcription.warning("HyperWhisper Cloud post-processing: No mode provided, using minimal prompt")
            return text
        }

        let vocabularyItems = PersistenceController.shared.fetchAllVocabularyItems()
        let appContext = applicationContext ?? ApplicationContextGatherer.shared.gatherContext()
        let systemPrompt = PromptBuilder.systemPrompt(
            for: mode,
            applicationContext: appContext
        )
        let systemInfo = PromptBuilder.systemInfo(
            for: mode,
            vocabulary: vocabularyItems,
            applicationContext: appContext
        )

        // STEP 5: Build request URL
        guard let url = URL(string: NetworkConfig.hyperwhisperCloudURL + NetworkConfig.hyperwhisperCloudPostProcessEndpoint) else {
            AppLogger.transcription.error("HyperWhisper Cloud post-processing failed: Invalid URL")
            return text
        }

        // STEP 6: Build request
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("HyperWhisper/\(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0")",
                        forHTTPHeaderField: "User-Agent")
        request.timeoutInterval = 60

        let cloudPPModel = CloudPostProcessingModel.fromStorageValue(mode.cloudPostProcessingModel)
        if let llmHeader = cloudPPModel.llmProviderHeader {
            request.setValue(llmHeader, forHTTPHeaderField: "X-LLM-Provider")
        }
        if let llmModelHeader = cloudPPModel.llmModelHeader {
            request.setValue(llmModelHeader, forHTTPHeaderField: "X-LLM-Model")
        }

        // STEP 7: Build JSON body with text, prompt, and identifier
        // Concatenate static system prompt + dynamic system info for HyperWhisper Cloud
        // (the backend constructs its own API call from the combined prompt)
        var body: [String: Any] = [
            "text": text,
            "prompt": systemPrompt + "\n\n" + systemInfo
        ]

        if isLicensed {
            body["license_key"] = identifier
        } else {
            body["device_id"] = identifier
        }

        do {
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
        } catch {
            AppLogger.transcription.error("Failed to serialize HyperWhisper Cloud post-processing request: \(error.localizedDescription, privacy: .public)")
            return text
        }

        // STEP 8: Perform request with retry logic
        let config = RetryConfiguration.postProcessing

        do {
            return try await performWithRetry(config: config) { attempt in
                AppLogger.network.debug("HyperWhisper Cloud post-processing attempt \(attempt, privacy: .public) of \(config.maxAttempts, privacy: .public)")
                AppLogger.network.info("POST \(NetworkConfig.hyperwhisperCloudPostProcessEndpoint, privacy: .public)")

                let (data, response) = try await URLSession.shared.data(for: request)

                guard let httpResponse = response as? HTTPURLResponse else {
                    throw TranscriptionError.invalidResponse(details: "Invalid server response")
                }

                // Handle successful response
                if httpResponse.statusCode == 200 {
                    guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                          let corrected = json["corrected"] as? String else {
                        AppLogger.network.warning("HyperWhisper Cloud post-processing: Unexpected response format")
                        throw TranscriptionError.invalidResponse(details: nil)
                    }

                    // Log cost information if available
                    if let cost = json["cost"] as? [String: Any],
                       let credits = cost["credits"] as? Double {
                        AppLogger.network.info("HyperWhisper Cloud post-processing · credits=\(credits, privacy: .public)")
                    }

                    let trimmedCorrected = corrected.trimmingCharacters(in: .whitespacesAndNewlines)
                    let result = TranscriptionTextProcessing.extractCleanedFromWrapped(trimmedCorrected)
                    if result.isEmpty {
                        // No strict <<CLEANED>> wrapper — fall back to the lenient
                        // strip before reverting to the original transcript.
                        let lenient = TranscriptionTextProcessing.stripWrapperMarkers(trimmedCorrected)
                        if lenient.isEmpty {
                            AppLogger.transcription.warning("Empty content returned from HyperWhisper Cloud; falling back to original")
                            return text
                        }
                        AppLogger.transcription.info("HyperWhisper Cloud post-processing completed via lenient fallback (no <<CLEANED>> wrapper)")
                        self.didMutateLastRun = true; signal.didMutate = true
                        return lenient
                    }

                    AppLogger.transcription.info("HyperWhisper Cloud post-processing completed successfully")
                    AppLogger.network.info("Response: \(result.count, privacy: .public) characters")
                    self.didMutateLastRun = true; signal.didMutate = true
                    return result
                }

                // Handle error responses
                try self.handleHyperWhisperCloudError(statusCode: httpResponse.statusCode, data: data)
                throw TranscriptionError.invalidResponse(details: "HTTP \(httpResponse.statusCode)")
            }
        } catch {
            // FALLBACK: Return original text on failure
            AppLogger.transcription.error("HyperWhisper Cloud post-processing failed: \(error.localizedDescription, privacy: .public)")

            // NOTIFY USER of actionable credential errors (can be fixed in Settings)
            if let transcriptionError = error as? TranscriptionError,
               transcriptionError.shouldSurfaceInline {
                onPostProcessingError?(transcriptionError)
            }

            return text
        }
    }

    /// Handles HTTP error responses from HyperWhisper Cloud /post-process endpoint
    private func handleHyperWhisperCloudError(statusCode: Int, data: Data) throws {
        let responseString = String(data: data, encoding: .utf8) ?? "No response body"
        AppLogger.network.error("HyperWhisper Cloud post-processing error · status=\(statusCode, privacy: .public)")

        // Try to parse structured error from JSON response
        if let errorJson = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let errorMessage = errorJson["message"] as? String ?? errorJson["error"] as? String {

            AppLogger.network.error("HyperWhisper Cloud API error · message=\(errorMessage, privacy: .public)")

            switch statusCode {
            case 402:
                let denial = HyperWhisperCloudCreditDenial(errorJson: errorJson, message: errorMessage)
                if let invalidMessage = denial.invalidExhaustedBalanceMessage {
                    throw TranscriptionError.invalidResponse(details: invalidMessage)
                }
                throw TranscriptionError.insufficientCredits(
                    remaining: denial.remainingForTranscriptionError,
                    required: denial.requiredForTranscriptionError
                )
            case 429:
                throw TranscriptionError.rateLimited(retryAfter: nil)
            case 401, 403:
                throw TranscriptionError.unauthorized(provider: "HyperWhisper Cloud")
            case 400:
                throw TranscriptionError.invalidRequest
            case 500...599:
                throw TranscriptionError.serverError(statusCode: statusCode, message: errorMessage)
            default:
                throw TranscriptionError.invalidResponse(details: "HTTP \(statusCode)")
            }
        }

        // Generic error handling
        let preview = responseString.prefix(200)
        AppLogger.network.error("HyperWhisper Cloud HTTP error (no JSON) · bodyPreview=\(preview, privacy: .private)")

        switch statusCode {
        case 402:
            throw TranscriptionError.insufficientCredits(remaining: 0, required: 0)
        case 429:
            throw TranscriptionError.rateLimited(retryAfter: nil)
        case 401, 403:
            throw TranscriptionError.unauthorized(provider: "HyperWhisper Cloud")
        case 500...599:
            throw TranscriptionError.serverError(statusCode: statusCode, message: "HyperWhisper Cloud server error")
        default:
            throw TranscriptionError.invalidResponse(details: "HTTP \(statusCode)")
        }
    }

    // MARK: - Custom Endpoint Post-Processing

    /// Performs post-processing via a user-configured custom OpenAI-compatible endpoint
    ///
    /// CUSTOM ENDPOINTS:
    /// Users can configure their own OpenAI-compatible endpoints (e.g., Ollama, LM Studio, OpenRouter)
    /// for post-processing. Each endpoint stores its URL, model name, and optional API key.
    ///
    /// - Parameters:
    ///   - text: The transcribed text to enhance
    ///   - providerString: The provider string in format "custom:<uuid>"
    ///   - mode: The transcription mode for building the prompt
    ///   - applicationContext: Application context for prompt building
    /// - Returns: The AI-enhanced text, or original text if processing fails
    private func performCustomEndpointPostProcessing(
        text: String,
        providerString: String,
        mode: Mode?,
        applicationContext: ApplicationContext?,
        mutationSignal: MutationSignal? = nil
    ) async throws -> String {
        let signal = mutationSignal ?? MutationSignal()
        // STEP 1: Parse endpoint ID from provider string
        guard let endpointId = CustomPostProcessingEndpoint.parseCustomProviderString(providerString) else {
            AppLogger.transcription.error("Custom endpoint post-processing failed: Invalid provider string \(providerString, privacy: .public)")
            return text
        }

        // STEP 2: Get endpoint configuration from manager
        guard let manager = customPostProcessingManager,
              let endpoint = manager.getEndpoint(id: endpointId) else {
            AppLogger.transcription.error("Custom endpoint post-processing failed: Endpoint not found \(endpointId.uuidString, privacy: .public)")
            return text
        }

        // STEP 3: Validate endpoint URL
        guard let url = URL(string: endpoint.endpointURL) else {
            AppLogger.transcription.error("Custom endpoint post-processing failed: Invalid URL \(endpoint.displayURL, privacy: .public)")
            return text
        }

        // STEP 4: Check network connectivity
        guard NetworkStatus.shared.isOnline else {
            AppLogger.transcription.warning("Custom endpoint post-processing skipped: Offline")
            return text
        }

        // STEP 5: Build system prompt
        guard let mode = mode else {
            AppLogger.transcription.warning("Custom endpoint post-processing: No mode provided")
            return text
        }

        let vocabularyItems = PersistenceController.shared.fetchAllVocabularyItems()
        let appContext = applicationContext ?? ApplicationContextGatherer.shared.gatherContext()
        let systemPrompt = PromptBuilder.systemPrompt(
            for: mode,
            applicationContext: appContext
        )
        let systemInfo = PromptBuilder.systemInfo(
            for: mode,
            vocabulary: vocabularyItems,
            applicationContext: appContext
        )

        // STEP 6: Build request
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.timeoutInterval = 60

        // STEP 7: Add API key if configured (Bearer auth)
        let apiKey = KeychainManager.shared.getCustomEndpointAPIKey(for: endpointId)
        if !apiKey.isEmpty {
            request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        }

        // STEP 8: Build OpenAI-compatible request body
        let userContent = """
        \(systemInfo)

        --TRANSCRIPT--
        \(text)
        --ENDTRANSCRIPT--
        """

        let messages: [[String: Any]] = [
            ["role": "system", "content": systemPrompt],
            ["role": "user", "content": userContent]
        ]

        let requestBody: [String: Any] = [
            "model": endpoint.modelName,
            "messages": messages
        ]

        do {
            request.httpBody = try JSONSerialization.data(withJSONObject: requestBody)
        } catch {
            AppLogger.transcription.error("Custom endpoint post-processing failed: JSON serialization error \(error.localizedDescription, privacy: .public)")
            return text
        }

        // STEP 9: Log request details
        AppLogger.transcription.info("Starting custom endpoint post-processing")
        AppLogger.network.info("POST \(endpoint.displayURL, privacy: .public)")
        AppLogger.network.debug("Model: \(endpoint.modelName, privacy: .public)")

        // STEP 10: Perform request with retry logic
        let config = RetryConfiguration.postProcessing

        do {
            return try await performWithRetry(config: config) { attempt in
                AppLogger.network.debug("Custom endpoint post-processing attempt \(attempt, privacy: .public) of \(config.maxAttempts, privacy: .public)")

                let (data, response) = try await URLSession.shared.data(for: request)

                guard let httpResponse = response as? HTTPURLResponse else {
                    throw TranscriptionError.invalidResponse(details: "Invalid server response")
                }

                // Handle successful response
                if httpResponse.statusCode == 200 {
                    // Parse OpenAI-compatible response: { "choices": [{ "message": { "content": "..." } }] }
                    guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                          let choices = json["choices"] as? [[String: Any]],
                          let firstChoice = choices.first,
                          let message = firstChoice["message"] as? [String: Any],
                          let content = message["content"] as? String else {
                        AppLogger.network.warning("Custom endpoint returned unexpected response format")
                        throw TranscriptionError.invalidResponse(details: "Invalid response format")
                    }

                    let trimmedContent = content.trimmingCharacters(in: .whitespacesAndNewlines)
                    let result = TranscriptionTextProcessing.extractCleanedFromWrapped(trimmedContent)
                    if result.isEmpty {
                        // No strict <<CLEANED>> wrapper — fall back to the lenient
                        // strip before reverting to the original transcript.
                        let lenient = TranscriptionTextProcessing.stripWrapperMarkers(trimmedContent)
                        if lenient.isEmpty {
                            AppLogger.transcription.warning("Empty content returned from custom endpoint; falling back to original")
                            return text
                        }
                        AppLogger.transcription.info("Custom endpoint post-processing completed via lenient fallback (no <<CLEANED>> wrapper)")
                        self.didMutateLastRun = true; signal.didMutate = true
                        return lenient
                    }

                    AppLogger.transcription.info("Custom endpoint post-processing completed successfully")
                    AppLogger.network.info("Response from \(endpoint.name, privacy: .public): \(result.count, privacy: .public) characters")
                    self.didMutateLastRun = true; signal.didMutate = true
                    return result
                }

                // Handle error responses
                let responseString = String(data: data, encoding: .utf8) ?? "No response body"
                AppLogger.network.error("Custom endpoint HTTP error \(httpResponse.statusCode, privacy: .public): \(responseString.prefix(200), privacy: .public)")

                switch httpResponse.statusCode {
                case 401, 403:
                    throw TranscriptionError.unauthorized(provider: endpoint.name)
                case 400, 413, 415, 422:
                    throw TranscriptionError.invalidRequest
                case 429:
                    throw TranscriptionError.rateLimited(retryAfter: nil)
                case 500...599:
                    throw TranscriptionError.serverError(statusCode: httpResponse.statusCode, message: "Custom endpoint server error")
                default:
                    throw TranscriptionError.invalidResponse(details: "HTTP \(httpResponse.statusCode)")
                }
            }
        } catch {
            // FALLBACK: Return original text on failure
            AppLogger.transcription.error("Custom endpoint post-processing failed: \(error.localizedDescription, privacy: .public)")

            // NOTIFY USER of actionable credential errors (can be fixed in Settings)
            if let transcriptionError = error as? TranscriptionError,
               transcriptionError.shouldSurfaceInline {
                onPostProcessingError?(transcriptionError)
            }

            return text
        }
    }

    // MARK: - Local Runtime Launch

    /// Ensures the embedded llama-server is up, retrying ONCE on a transient launch
    /// failure (OOM, launch race). Deterministic capability/asset errors — Intel,
    /// Rosetta, missing executable, missing model — are NOT retried (a retry would
    /// fail identically). Returns the resolved model id; throws on final failure so
    /// the caller surfaces `localRuntimeUnavailable` and returns the raw transcript.
    private func ensureLocalRuntimeRunning(server: LlamaServerController, resolved: LocalModel, modelURL: URL) async throws -> String {
        do {
            return try await server.ensureRunning(modelId: resolved.id, modelURL: modelURL)
        } catch let error as LlamaServerController.Error {
            switch error {
            case .launchFailed, .healthCheckFailed:
                AppLogger.transcription.warning("Local runtime launch failed (\(error.localizedDescription, privacy: .public)) — retrying once")
                return try await server.ensureRunning(modelId: resolved.id, modelURL: modelURL)
            case .executableNotFound, .modelNotFound, .unsupportedArchitecture, .needsNativeRelaunch:
                throw error
            }
        }
    }

    // MARK: - Failure Classification

    /// Returns `true` when the error indicates the embedded llama-server (the local LLM runtime)
    /// is not reachable for the duration of the call — connection refused, dropped TCP, timeout,
    /// or a 5xx response (llama-server returns `503 {"status":"loading model"}` while swapping
    /// the GGUF on its own port). Used to wrap raw network/HTTP errors into
    /// `TranscriptionError.localRuntimeUnavailable` so the inline banner surfaces.
    /// Returns `false` for any provider other than `.localLLM`.
    private func isLocalRuntimeNetworkFailure(_ error: Error, provider: PostProcessingProvider) -> Bool {
        guard provider == .localLLM else { return false }

        if let urlError = error as? URLError {
            switch urlError.code {
            case .cannotConnectToHost, .networkConnectionLost, .timedOut,
                 .notConnectedToInternet, .cannotFindHost, .dnsLookupFailed,
                 .resourceUnavailable, .badServerResponse:
                return true
            default:
                return false
            }
        }

        if let txnError = error as? TranscriptionError,
           case .invalidResponse(let details) = txnError,
           let details = details,
           details.hasPrefix("HTTP 5") {
            return true
        }

        return false
    }
}
