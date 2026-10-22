//
//  ModeData.swift
//  HyperWhisper
//
//  The mode data transfer object, and the helpers and limits it uses.
//

import SwiftUI

// MARK: - Helper Functions

/// Check if a model is English-only
/// - Whisper models: has .en suffix (e.g., base.en, small.en)
/// - Parakeet V2: contains "v2" in the name (English-only, highest recall)
func isEnglishOnlyModel(provider: ProviderType, model: String) -> Bool {
    guard provider == .local else { return false }
    // Whisper English-only models have .en suffix
    if model.hasSuffix(".en") { return true }
    // Parakeet V2 is English-only (V3 is multilingual)
    if model.lowercased().hasPrefix("parakeet") && model.lowercased().contains("v2") { return true }
    return false
}

// Maximum number of characters allowed for user-supplied system prompts
let userSystemPromptCharacterLimit = 8000

// Maximum number of characters allowed for Gemini custom transcription prompts
let geminiCustomPromptCharacterLimit = 2000

// MARK: - Mode Data Transfer Object

/// Used by CreateModeView and EditModeView
struct ModeData {
    let id: UUID
    let name: String
    let preset: String
    let language: String
    let model: String
    let punctuation: Bool
    let capitalization: Bool
    let profanityFilter: Bool
    let customInstructions: String
    let languageModel: String  // Language model (e.g., "gpt-5.6-luna")
    let cloudProvider: String  // Cloud provider (e.g., "openai")
    let cloudTranscriptionModel: String  // Cloud transcription model (e.g., "whisper-1")
    let postProcessingMode: PostProcessingMode  // Post-processing mode (off/cloud/local)
    let postProcessingProvider: String  // Post-processing provider (e.g., "openai", "anthropic", "gemini")
    let englishSpelling: EnglishSpelling  // English spelling variant (american/british/australian/canadian)
    let userSystemPrompt: String  // Optional user-supplied system prompt append
    let useStreamingTranscription: Bool  // Whether to use real-time streaming transcription (HyperWhisper Cloud only)
    let cloudAccuracyTier: CloudAccuracyTier  // Accuracy tier for HyperWhisper Cloud (medium/high/highest)
    let removeTrailingPeriod: Bool  // Strip trailing period from transcriptions (useful for casual contexts)
    let enableScreenOCR: Bool  // Capture visible screen text to improve accuracy of names and terms
    let geminiCustomPrompt: String  // Custom instructions for Gemini transcription prompt
    let cloudPostProcessingModel: CloudPostProcessingModel  // LLM for HyperWhisper Cloud post-processing
    let cloudTranscriptionDomain: String?  // X-STT-Domain value ("medical") or nil — HyperWhisper Cloud only

    init(id: UUID = UUID(),
         name: String,
         preset: String = "hyper",
         language: String,
         model: String,
         punctuation: Bool,
         capitalization: Bool,
         profanityFilter: Bool,
         customInstructions: String = "",
         languageModel: String = PostProcessingModels.defaultModel(for: .openai)?.id ?? "gpt-5.6-luna",
         cloudProvider: String = "hyperwhisper",
         cloudTranscriptionModel: String = "whisper-1",
         postProcessingMode: PostProcessingMode = .cloud,
         postProcessingProvider: String? = nil,
         englishSpelling: EnglishSpelling = .american,
         userSystemPrompt: String = "",
         useStreamingTranscription: Bool = false,
         cloudAccuracyTier: CloudAccuracyTier = .deepgramNova3,
         removeTrailingPeriod: Bool = false,
         enableScreenOCR: Bool = false,
         geminiCustomPrompt: String = "",
         cloudPostProcessingModel: CloudPostProcessingModel = .grokFast,
         cloudTranscriptionDomain: String? = nil) {
        self.id = id
        self.name = name
        self.preset = preset
        self.language = LanguageData.canonicalLanguageCode(language)
        self.model = model
        self.punctuation = punctuation
        self.capitalization = capitalization
        self.profanityFilter = profanityFilter
        self.customInstructions = customInstructions
        self.languageModel = languageModel
        self.cloudProvider = cloudProvider
        self.cloudTranscriptionModel = cloudTranscriptionModel
        self.postProcessingMode = postProcessingMode
        self.englishSpelling = englishSpelling
        self.userSystemPrompt = String(userSystemPrompt.prefix(userSystemPromptCharacterLimit))
        self.useStreamingTranscription = useStreamingTranscription
        self.cloudAccuracyTier = cloudAccuracyTier
        self.removeTrailingPeriod = removeTrailingPeriod
        self.enableScreenOCR = enableScreenOCR
        self.geminiCustomPrompt = String(geminiCustomPrompt.prefix(geminiCustomPromptCharacterLimit))
        self.cloudPostProcessingModel = cloudPostProcessingModel
        // Normalize empty → nil so an unset domain is consistently nil downstream.
        let trimmedDomain = cloudTranscriptionDomain?.trimmingCharacters(in: .whitespacesAndNewlines)
        self.cloudTranscriptionDomain = (trimmedDomain?.isEmpty == false) ? trimmedDomain : nil
        if let provider = postProcessingProvider {
            self.postProcessingProvider = provider
        } else if let defaultProvider = postProcessingMode.defaultProvider {
            self.postProcessingProvider = defaultProvider.storageValue
        } else {
            self.postProcessingProvider = PostProcessingProvider.hyperwhisper.storageValue
        }
    }

    /// Determines if this mode can work offline (no internet required)
    var isOfflineCapable: Bool {
        // Mode is offline-capable when ALL components can work offline:
        // 1. ASR (Automatic Speech Recognition) provider is local
        // 2. Post-processing is either off or local (not cloud)
        let asrProvider = ProviderType(rawValue: model == "cloud" ? "cloud" : "local") ?? .local
        let asrIsLocal = asrProvider == .local
        let postProcessingIsOffline = postProcessingMode != .cloud

        return asrIsLocal && postProcessingIsOffline
    }

    /// The status chip a mode card shows next to the mode's name.
    /// - Parameter isLocalModelDownloaded: whether the mode's on-device
    ///   transcription model is on disk. Ignored for a cloud ASR mode.
    func cardStatusChip(isLocalModelDownloaded: Bool) -> ModeCardStatusChip {
        // Same reading of the id as TranscriptionProviderRouter: trimmed,
        // any casing, and empty means cloud.
        let id = model.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        if id.isEmpty || id == "cloud" {
            return .none
        }
        // A local mode whose model is gone fails with "model not downloaded"
        // (#2036), so it must not read as ready to use.
        if !isLocalModelDownloaded {
            return .modelMissing
        }
        return isOfflineCapable ? .offline : .none
    }

    /// Create ModeData from Core Data Mode entity
    init(from mode: Mode) {
        self.id = mode.id ?? UUID()
        self.name = mode.name ?? "Default"
        // Migrate removed "voiceToText" preset → set post-processing to off
        let rawPreset = mode.preset ?? "hyper"
        let isLegacyVoiceToText = rawPreset == "voiceToText"
        self.preset = isLegacyVoiceToText ? "hyper" : rawPreset
        self.language = LanguageData.canonicalLanguageCode(mode.language)
        self.model = mode.model ?? "base"
        self.cloudProvider = CloudProvider.effectiveModeStorageValue(mode.cloudProvider)
        self.cloudTranscriptionModel = CloudTranscriptionModels.resolveModelAlias(
            mode.cloudTranscriptionModel ?? "whisper-1",
            provider: CloudProvider.parse(self.cloudProvider)
        )
        // Convert from Core Data Int16 to enum, default to cloud for backward compatibility
        let processingMode = isLegacyVoiceToText ? .off : (PostProcessingMode(rawValue: mode.postProcessingMode) ?? .cloud)
        self.postProcessingMode = processingMode

        if processingMode == .local {
            let stored = PostProcessingProvider(rawValue: mode.postProcessingProvider ?? "")
            self.languageModel = mode.languageModel ?? PostProcessingProvider.localLLM.defaultModel
            self.postProcessingProvider = PostProcessingProvider.localLLM.rawValue
        } else {
            self.languageModel = mode.languageModel ?? "gpt-5.6-luna"
            if let provider = mode.postProcessingProvider {
                self.postProcessingProvider = provider
            } else if let defaultProvider = processingMode.defaultProvider {
                self.postProcessingProvider = defaultProvider.storageValue
            } else {
                self.postProcessingProvider = PostProcessingProvider.hyperwhisper.storageValue
            }
        }
        self.punctuation = mode.punctuation
        self.capitalization = mode.capitalization
        self.profanityFilter = mode.profanityFilter
        self.customInstructions = mode.customInstructions ?? ""
        self.englishSpelling = EnglishSpelling(rawValue: mode.englishSpelling ?? "american") ?? .american
        if let prompt = mode.userSystemPrompt {
            self.userSystemPrompt = String(prompt.prefix(userSystemPromptCharacterLimit))
        } else {
            self.userSystemPrompt = ""
        }
        self.useStreamingTranscription = mode.useStreamingTranscription
        self.cloudAccuracyTier = CloudAccuracyTier.fromStorageValue(mode.cloudAccuracyTier)
        self.removeTrailingPeriod = mode.removeTrailingPeriod
        self.enableScreenOCR = mode.enableScreenOCR
        if let prompt = mode.geminiCustomPrompt {
            self.geminiCustomPrompt = String(prompt.prefix(geminiCustomPromptCharacterLimit))
        } else {
            self.geminiCustomPrompt = ""
        }
        self.cloudPostProcessingModel = CloudPostProcessingModel.fromStorageValue(mode.cloudPostProcessingModel)
        let storedDomain = mode.cloudTranscriptionDomain?.trimmingCharacters(in: .whitespacesAndNewlines)
        self.cloudTranscriptionDomain = (storedDomain?.isEmpty == false) ? storedDomain : nil
    }
}
