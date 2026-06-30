//
//  APIKeySettingsManager.swift
//  hyperwhisper
//
//  API KEY SETTINGS MANAGER
//  Manages secure storage and validation of API keys for all cloud providers.
//
//  RESPONSIBILITIES:
//  - Secure API key storage using macOS Keychain
//  - API key validation for transcription and post-processing providers
//  - Migration from UserDefaults to Keychain (one-time on upgrade)
//  - Missing API key detection and reporting
//
//  SECURITY:
//  - All keys stored in Keychain (never in UserDefaults or logs)
//  - Automatic migration from old UserDefaults-based storage
//  - Published properties for UI binding (actual keys not logged)
//
//  SUPPORTED PROVIDERS:
//  - Transcription: HyperWhisper Cloud, OpenAI, Groq, Deepgram, AssemblyAI, ElevenLabs
//  - Post-processing: OpenAI, Anthropic, Google Gemini, Local LLM
//

import Foundation
import SwiftUI
import Combine
import os

/// Manages API keys for all cloud providers with secure Keychain storage
@MainActor
class APIKeySettingsManager: ObservableObject {

    // MARK: - Logger

    /// Logger for API key settings operations
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "APIKeySettings")

    // MARK: - API Keys (Published Properties)

    /// Flag to prevent saving back to keychain during initial load
    /// Prevents infinite loop: load from keychain → didSet → save to keychain
    private var isLoadingFromKeychain = false

    /// OpenAI API key for cloud transcription and post-processing
    /// Stored securely in macOS Keychain, never in UserDefaults
    /// Used by: CloudWhisperProvider, AIPostProcessor
    @Published var openAIAPIKey: String = "" {
        didSet {
            // Only save to keychain when not loading from keychain
            guard !isLoadingFromKeychain else { return }
            saveAPIKeyToKeychain(openAIAPIKey, for: .openAI)
        }
    }

    /// Groq API key for cloud transcription
    /// Stored securely in macOS Keychain, never in UserDefaults
    /// Used by: CloudWhisperProvider
    @Published var groqAPIKey: String = "" {
        didSet {
            guard !isLoadingFromKeychain else { return }
            saveAPIKeyToKeychain(groqAPIKey, for: .groq)
        }
    }

    /// Anthropic API key for post-processing
    /// Stored securely in macOS Keychain, never in UserDefaults
    /// Used by: AIPostProcessor
    @Published var anthropicAPIKey: String = "" {
        didSet {
            guard !isLoadingFromKeychain else { return }
            saveAPIKeyToKeychain(anthropicAPIKey, for: .anthropic)
        }
    }

    /// Google Gemini API key for post-processing
    /// Stored securely in macOS Keychain, never in UserDefaults
    /// Used by: AIPostProcessor
    @Published var geminiAPIKey: String = "" {
        didSet {
            guard !isLoadingFromKeychain else { return }
            saveAPIKeyToKeychain(geminiAPIKey, for: .gemini)
        }
    }

    /// Deepgram API key for cloud transcription
    /// Stored securely in macOS Keychain, never in UserDefaults
    /// Used by: DeepgramProvider
    @Published var deepgramAPIKey: String = "" {
        didSet {
            guard !isLoadingFromKeychain else { return }
            saveAPIKeyToKeychain(deepgramAPIKey, for: .deepgram)
        }
    }

    /// AssemblyAI API key for cloud transcription
    /// Stored securely in macOS Keychain, never in UserDefaults
    /// Used by: AssemblyAIProvider
    @Published var assemblyAIAPIKey: String = "" {
        didSet {
            guard !isLoadingFromKeychain else { return }
            saveAPIKeyToKeychain(assemblyAIAPIKey, for: .assemblyAI)
        }
    }

    /// ElevenLabs API key for cloud transcription
    /// Stored securely in macOS Keychain, never in UserDefaults
    /// Used by: ElevenLabsProvider
    @Published var elevenLabsAPIKey: String = "" {
        didSet {
            guard !isLoadingFromKeychain else { return }
            saveAPIKeyToKeychain(elevenLabsAPIKey, for: .elevenLabs)
        }
    }

    /// Mistral API key for cloud transcription
    /// Stored securely in macOS Keychain, never in UserDefaults
    /// Used by: MistralProvider
    @Published var mistralAPIKey: String = "" {
        didSet {
            guard !isLoadingFromKeychain else { return }
            saveAPIKeyToKeychain(mistralAPIKey, for: .mistral)
        }
    }

    /// Soniox API key for cloud transcription
    /// Stored securely in macOS Keychain, never in UserDefaults
    /// Used by: SonioxProvider
    @Published var sonioxAPIKey: String = "" {
        didSet {
            guard !isLoadingFromKeychain else { return }
            saveAPIKeyToKeychain(sonioxAPIKey, for: .soniox)
        }
    }

    /// Cerebras API key for post-processing
    /// Stored securely in macOS Keychain, never in UserDefaults
    /// Used by: AIPostProcessor for Cerebras post-processing
    @Published var cerebrasAPIKey: String = "" {
        didSet {
            guard !isLoadingFromKeychain else { return }
            saveAPIKeyToKeychain(cerebrasAPIKey, for: .cerebras)
        }
    }

    /// Grok (xAI) API key for cloud transcription
    /// Stored securely in macOS Keychain, never in UserDefaults
    /// Used by: GrokSTTProvider
    @Published var grokAPIKey: String = "" {
        didSet {
            guard !isLoadingFromKeychain else { return }
            saveAPIKeyToKeychain(grokAPIKey, for: .grok)
        }
    }

    /// Whether to use OpenAI for transcription
    /// Stored in UserDefaults (not sensitive)
    @AppStorage("useOpenAITranscription") var useOpenAITranscription: Bool = false

    // MARK: - Published Error State

    /// Validation error for API key operations
    /// Displayed in UI when keychain operations fail
    @Published var validationError: String?

    // MARK: - Initialization

    init() {
        // CRITICAL: Migrate API keys from UserDefaults to Keychain
        // This happens on first launch after the security update
        // Must happen before loading keys from keychain
        migrateAPIKeysToKeychain()

        // Load API keys from secure keychain storage
        loadAPIKeysFromKeychain()
    }

    // MARK: - Public Methods - Transcription Providers

    /// Get API key for a specific cloud transcription provider
    /// - Parameter provider: The cloud provider
    /// - Returns: The API key if set, empty string otherwise
    func apiKey(for provider: CloudProvider) -> String {
        switch provider {
        case .hyperwhisper:
            return ""  // HyperWhisper Cloud doesn't require an API key
        case .openai:
            return openAIAPIKey
        case .groq:
            return groqAPIKey
        case .deepgram:
            return deepgramAPIKey
        case .assemblyAI:
            return assemblyAIAPIKey
        case .elevenLabs:
            return elevenLabsAPIKey
        case .mistral:
            return mistralAPIKey
        case .soniox:
            return sonioxAPIKey
        case .gemini:
            return geminiAPIKey
        case .grok:
            return grokAPIKey
        case .microsoftAzureSpeech, .googleSpeech:
            return ""  // HyperWhisper Cloud only — no BYOK in v1
        }
    }

    /// Set API key for a specific cloud transcription provider
    /// - Parameters:
    ///   - key: The API key to set
    ///   - provider: The cloud provider
    func setAPIKey(_ key: String, for provider: CloudProvider) {
        switch provider {
        case .hyperwhisper:
            break  // HyperWhisper Cloud doesn't require an API key
        case .openai:
            openAIAPIKey = key
        case .groq:
            groqAPIKey = key
        case .deepgram:
            deepgramAPIKey = key
        case .assemblyAI:
            assemblyAIAPIKey = key
        case .elevenLabs:
            elevenLabsAPIKey = key
        case .mistral:
            mistralAPIKey = key
        case .soniox:
            sonioxAPIKey = key
        case .gemini:
            geminiAPIKey = key
        case .grok:
            grokAPIKey = key
        case .microsoftAzureSpeech, .googleSpeech:
            break  // HyperWhisper Cloud only — no BYOK in v1
        }
    }

    /// Check if a cloud provider has an API key configured
    /// - Parameter provider: The cloud provider to check
    /// - Returns: True if API key is set, false otherwise
    func hasAPIKey(for provider: CloudProvider) -> Bool {
        // Providers that don't require API keys are always considered "configured"
        guard provider.requiresAPIKey else { return true }
        return !apiKey(for: provider).isEmpty
    }

    // MARK: - Public Methods - Post-Processing Providers

    /// Get API key for a specific post-processing provider
    /// - Parameter provider: The post-processing provider
    /// - Returns: The API key if set, empty string otherwise
    func postProcessingAPIKey(for provider: PostProcessingProvider) -> String {
        switch provider {
        case .hyperwhisper:
            return ""  // HyperWhisper Cloud doesn't require an API key
        case .openai:
            return openAIAPIKey
        case .anthropic:
            return anthropicAPIKey
        case .gemini:
            return geminiAPIKey
        case .groq:
            return groqAPIKey  // Shared with transcription
        case .grok:
            return grokAPIKey  // Shared with Grok/xAI transcription
        case .cerebras:
            return cerebrasAPIKey
        case .mistral:
            return mistralAPIKey  // Shared with Mistral/Voxtral transcription
        case .localLLM:
            return ""  // Local Qwen doesn't require an API key
        }
    }

    /// Set API key for a specific post-processing provider
    /// - Parameters:
    ///   - key: The API key to set
    ///   - provider: The post-processing provider
    func setPostProcessingAPIKey(_ key: String, for provider: PostProcessingProvider) {
        switch provider {
        case .hyperwhisper:
            break  // HyperWhisper Cloud doesn't require an API key
        case .openai:
            openAIAPIKey = key
        case .anthropic:
            anthropicAPIKey = key
        case .gemini:
            geminiAPIKey = key
        case .groq:
            groqAPIKey = key  // Shared with transcription
        case .grok:
            grokAPIKey = key  // Shared with Grok/xAI transcription
        case .cerebras:
            cerebrasAPIKey = key
        case .mistral:
            mistralAPIKey = key  // Shared with Mistral/Voxtral transcription
        case .localLLM:
            break  // Local Qwen doesn't require an API key
        }
    }

    /// Check if a post-processing provider has an API key configured
    /// - Parameter provider: The post-processing provider to check
    /// - Returns: True if API key is set, false otherwise
    func hasPostProcessingAPIKey(for provider: PostProcessingProvider) -> Bool {
        guard provider.requiresAPIKey else { return true }
        return !postProcessingAPIKey(for: provider).isEmpty
    }

    // MARK: - Public Methods - Validation

    /// Get missing API keys for a given mode configuration
    /// - Parameter mode: The Mode entity to check
    /// - Returns: Array of missing API keys with their context
    func getMissingAPIKeys(for mode: Mode) -> [MissingAPIKey] {
        var missingKeys: [MissingAPIKey] = []

        // Check if we're offline first - if so and cloud is needed, return early
        let isCloudTranscription = (mode.model ?? "base").lowercased() == "cloud"
        if isCloudTranscription && !NetworkStatus.shared.isOnline {
            return [MissingAPIKey(context: .offline)]
        }

        // Check transcription API key if using cloud
        if isCloudTranscription {
            let cloudProviderString = mode.cloudProvider ?? "hyperwhisper"
            if let cloudProvider = CloudProvider(rawValue: cloudProviderString) {
                if !hasAPIKey(for: cloudProvider) {
                    missingKeys.append(MissingAPIKey(context: .transcription(cloudProvider)))
                }
            }
        }

        // Check post-processing API key if post-processing is enabled
        let postProcessingMode = mode.postProcessingMode
        if postProcessingMode != 0 { // 0 means no post-processing
            let postProviderString = mode.postProcessingProvider ?? "hyperwhisper"
            if let postProvider = PostProcessingProvider(rawValue: postProviderString) {
                if postProvider.requiresAPIKey && !hasPostProcessingAPIKey(for: postProvider) {
                    // Check if we already added this provider for transcription (deduplication)
                    let alreadyAdded = missingKeys.contains { key in
                        if case .transcription(let transcriptionProvider) = key.context {
                            return transcriptionProvider.rawValue == postProvider.rawValue
                        }
                        return false
                    }

                    if !alreadyAdded {
                        missingKeys.append(MissingAPIKey(context: .postProcessing(postProvider)))
                    }
                }
            }
        }

        return missingKeys
    }

    /// Get missing API keys for a mode snapshot (thread-safe, no Core Data dependency).
    func getMissingAPIKeys(for snapshot: ModeSnapshot) -> [MissingAPIKey] {
        var missingKeys: [MissingAPIKey] = []

        let isCloudTranscription = snapshot.model.lowercased() == "cloud"
        if isCloudTranscription && !NetworkStatus.shared.isOnline {
            return [MissingAPIKey(context: .offline)]
        }

        if isCloudTranscription {
            if let cloudProvider = CloudProvider(rawValue: snapshot.cloudProvider) {
                if !hasAPIKey(for: cloudProvider) {
                    missingKeys.append(MissingAPIKey(context: .transcription(cloudProvider)))
                }
            }
        }

        if snapshot.postProcessingMode != 0 {
            if let postProvider = PostProcessingProvider(rawValue: snapshot.postProcessingProvider) {
                if postProvider.requiresAPIKey && !hasPostProcessingAPIKey(for: postProvider) {
                    let alreadyAdded = missingKeys.contains { key in
                        if case .transcription(let transcriptionProvider) = key.context {
                            return transcriptionProvider.rawValue == postProvider.rawValue
                        }
                        return false
                    }
                    if !alreadyAdded {
                        missingKeys.append(MissingAPIKey(context: .postProcessing(postProvider)))
                    }
                }
            }
        }

        return missingKeys
    }

    /// Check if only post-processing keys are missing (non-blocking scenario)
    /// - Parameter missingKeys: Array of missing API keys
    /// - Returns: True if only post-processing keys are missing
    static func onlyPostProcessingKeysMissing(_ missingKeys: [MissingAPIKey]) -> Bool {
        guard !missingKeys.isEmpty else { return false }
        return missingKeys.allSatisfy { key in
            if case .postProcessing = key.context {
                return true
            }
            return false
        }
    }

    // MARK: - Private Methods - Keychain Management

    /// Migrate API keys from UserDefaults to Keychain
    /// This runs once on first launch after the security update
    /// IDEMPOTENT: Safe to call multiple times
    private func migrateAPIKeysToKeychain() {
        // Attempt migration - this is idempotent and safe to call multiple times
        if KeychainManager.shared.migrateFromUserDefaults() {
            logger.info("✅ API keys successfully migrated to secure keychain storage")
        }
    }

    /// Load all API keys from keychain
    /// CRITICAL: Sets isLoadingFromKeychain flag to prevent save loop
    private func loadAPIKeysFromKeychain() {
        // Set flag to prevent didSet from saving back to keychain during load
        isLoadingFromKeychain = true
        defer { isLoadingFromKeychain = false }  // Always reset flag when done

        // Load each API key from secure storage
        openAIAPIKey = KeychainManager.shared.getAPIKey(for: .openAI)
        groqAPIKey = KeychainManager.shared.getAPIKey(for: .groq)
        anthropicAPIKey = KeychainManager.shared.getAPIKey(for: .anthropic)
        geminiAPIKey = KeychainManager.shared.getAPIKey(for: .gemini)
        deepgramAPIKey = KeychainManager.shared.getAPIKey(for: .deepgram)
        assemblyAIAPIKey = KeychainManager.shared.getAPIKey(for: .assemblyAI)
        elevenLabsAPIKey = KeychainManager.shared.getAPIKey(for: .elevenLabs)
        mistralAPIKey = KeychainManager.shared.getAPIKey(for: .mistral)
        sonioxAPIKey = KeychainManager.shared.getAPIKey(for: .soniox)
        cerebrasAPIKey = KeychainManager.shared.getAPIKey(for: .cerebras)
        grokAPIKey = KeychainManager.shared.getAPIKey(for: .grok)

        // Log configuration status (without exposing actual keys)
        let summary = KeychainManager.shared.getConfigurationSummary()
        logger.info("📋 API Key Configuration Status:")
        for (provider, isConfigured) in summary {
            logger.info("  • \(provider, privacy: .public): \(isConfigured ? "✅ Configured" : "❌ Not configured", privacy: .public)")
        }
    }

    /// Save an API key to keychain
    /// - Parameters:
    ///   - key: The API key to save (empty string deletes the key)
    ///   - type: The type of API key
    private func saveAPIKeyToKeychain(_ key: String, for type: KeychainManager.APIKeyType) {
        do {
            if key.isEmpty {
                // Delete the key if empty string is provided
                try KeychainManager.shared.deleteAPIKey(for: type)
            } else {
                // Save the new key
                try KeychainManager.shared.saveAPIKey(key, for: type)
            }
        } catch {
            // Log error but don't crash - API key functionality should degrade gracefully
            logger.error("❌ Failed to update \(type.displayName, privacy: .public) API key: \(error.localizedDescription, privacy: .public)")
            validationError = "Failed to save API key: \(error.localizedDescription)"
        }
    }
}

// MARK: - Supporting Types

/// Structure representing a missing API key with context
struct MissingAPIKey: Equatable {
    enum Context: Equatable {
        case transcription(CloudProvider)
        case postProcessing(PostProcessingProvider)
        case offline // Special case when offline and cloud is needed
    }

    let context: Context

    /// Display name for the missing key (e.g., "Groq (transcription)")
    var displayName: String {
        switch context {
        case .transcription(let provider):
            return "\(provider.displayName) (transcription)"
        case .postProcessing(let provider):
            return "\(provider.displayName) (post-processing)"
        case .offline:
            return "Internet connection"
        }
    }

    /// Short name without context (used when deduping)
    var providerName: String {
        switch context {
        case .transcription(let provider):
            return provider.displayName
        case .postProcessing(let provider):
            return provider.displayName
        case .offline:
            return "Offline"
        }
    }
}

// MARK: - CloudProviderAPIKeyProviding Conformance

extension APIKeySettingsManager: CloudProviderAPIKeyProviding {}
