//
//  PostProcessingModels.swift
//  hyperwhisper
//
//  Post-Processing Models Configuration
//  Defines available language models for each post-processing provider
//

import Foundation

/// Post-Processing Model structure
struct PostProcessingModel {
    /// The model identifier used by the API
    let id: String
    
    /// The user-friendly display name shown in the UI
    let displayName: String
    
    /// Whether this model is available for general use
    let isAvailable: Bool
    
    /// Model description for tooltips
    let description: String
    
    /// Which provider this model belongs to
    let provider: PostProcessingProvider
}

/// Central registry of all available post-processing models
struct PostProcessingModels {
    
    /// All available post-processing models grouped by provider
    static let availableModels: [PostProcessingModel] = [
        // MARK: - HyperWhisper Cloud Model (built-in)
        PostProcessingModel(
            id: "hyperwhisper-cloud",
            displayName: "HyperWhisper Cloud",
            isAvailable: true,
            description: "models.postProcessing.hyperwhisper.description".localized,
            provider: .hyperwhisper
        ),

        // MARK: - OpenAI Models (GPT-4.1 Nano is default)
        PostProcessingModel(
            id: "gpt-4.1-nano",
            displayName: "GPT-4.1 Nano",
            isAvailable: true,
            description: "models.postProcessing.gpt4.1.nano.description".localized,
            provider: .openai
        ),
        PostProcessingModel(
            id: "gpt-4.1-mini",
            displayName: "GPT-4.1 Mini",
            isAvailable: true,
            description: "models.postProcessing.gpt4.1.mini.description".localized,
            provider: .openai
        ),
        PostProcessingModel(
            id: "gpt-4.1",
            displayName: "GPT-4.1",
            isAvailable: true,
            description: "models.postProcessing.gpt4.1.description".localized,
            provider: .openai
        ),
        PostProcessingModel(
            id: "gpt-5-nano",
            displayName: "GPT-5 Nano",
            isAvailable: true,
            description: "models.postProcessing.gpt5.nano.description".localized,
            provider: .openai
        ),
        PostProcessingModel(
            id: "gpt-5-mini",
            displayName: "GPT-5 Mini",
            isAvailable: true,
            description: "models.postProcessing.gpt5.mini.description".localized,
            provider: .openai
        ),
        PostProcessingModel(
            id: "gpt-5",
            displayName: "GPT-5",
            isAvailable: true,
            description: "models.postProcessing.gpt5.description".localized,
            provider: .openai
        ),
        PostProcessingModel(
            id: "gpt-5.1",
            displayName: "GPT-5.1",
            isAvailable: true,
            description: "models.postProcessing.gpt5.1.description".localized,
            provider: .openai
        ),
        PostProcessingModel(
            id: "gpt-5.2",
            displayName: "GPT-5.2",
            isAvailable: true,
            description: "models.postProcessing.gpt5.2.description".localized,
            provider: .openai
        ),
        PostProcessingModel(
            id: "gpt-5.4-nano",
            displayName: "GPT-5.4 Nano",
            isAvailable: true,
            description: "Latest generation, fastest",
            provider: .openai
        ),
        PostProcessingModel(
            id: "gpt-5.4-mini",
            displayName: "GPT-5.4 Mini",
            isAvailable: true,
            description: "Latest generation, balanced",
            provider: .openai
        ),
        PostProcessingModel(
            id: "gpt-5.4",
            displayName: "GPT-5.4",
            isAvailable: true,
            description: "Latest generation, highest quality",
            provider: .openai
        ),

        // MARK: - Anthropic Models
        PostProcessingModel(
            id: "claude-haiku-4-5",
            displayName: "Claude 4.5 Haiku",
            isAvailable: true,
            description: "models.postProcessing.claude4.5.haiku.description".localized,
            provider: .anthropic
        ),
        PostProcessingModel(
            id: "claude-sonnet-4-0",
            displayName: "Claude 4 Sonnet",
            isAvailable: true,
            description: "models.postProcessing.claude4.sonnet.description".localized,
            provider: .anthropic
        ),
        PostProcessingModel(
            id: "claude-sonnet-4-5",
            displayName: "Claude 4.5 Sonnet",
            isAvailable: true,
            description: "High quality, latest Sonnet model",
            provider: .anthropic
        ),
        PostProcessingModel(
            id: "claude-sonnet-4-6",
            displayName: "Claude 4.6 Sonnet",
            isAvailable: true,
            description: "Latest, most capable Sonnet model",
            provider: .anthropic
        ),

        // MARK: - Google Gemini Models
        PostProcessingModel(
            id: "gemini-3-flash-preview",
            displayName: "Gemini 3 Flash",
            isAvailable: true,
            description: "models.postProcessing.gemini3.flash.description".localized,
            provider: .gemini
        ),
        PostProcessingModel(
            id: "gemini-3.5-flash",
            displayName: "Gemini 3.5 Flash",
            isAvailable: true,
            description: "Most intelligent flash model, frontier performance for agentic tasks",
            provider: .gemini
        ),
        PostProcessingModel(
            id: "gemini-2.5-flash",
            displayName: "Gemini 2.5 Flash",
            isAvailable: true,
            description: "models.postProcessing.gemini2.5.flash.description".localized,
            provider: .gemini
        ),
        PostProcessingModel(
            id: "gemini-2.5-flash-lite",
            displayName: "Gemini 2.5 Flash Lite",
            isAvailable: true,
            description: "models.postProcessing.gemini2.5.flashLite.description".localized,
            provider: .gemini
        ),
        PostProcessingModel(
            id: "gemini-2.5-pro",
            displayName: "Gemini 2.5 Pro",
            isAvailable: true,
            description: "High quality, advanced reasoning",
            provider: .gemini
        ),
        PostProcessingModel(
            id: "gemini-3-pro-preview",
            displayName: "Gemini 3 Pro",
            isAvailable: true,
            description: "Latest pro-level intelligence",
            provider: .gemini
        ),
        PostProcessingModel(
            id: "gemini-3.1-flash-lite-preview",
            displayName: "Gemini 3.1 Flash Lite",
            isAvailable: true,
            description: "Next-gen lightweight flash",
            provider: .gemini
        ),

        // MARK: - Groq Models (ultra-fast inference)
        PostProcessingModel(
            id: "openai/gpt-oss-120b",
            displayName: "GPT OSS 120B",
            isAvailable: true,
            description: "models.postProcessing.groq.gptoss.120b.description".localized,
            provider: .groq
        ),
        PostProcessingModel(
            id: "openai/gpt-oss-20b",
            displayName: "GPT OSS 20B",
            isAvailable: true,
            description: "models.postProcessing.groq.gptoss.20b.description".localized,
            provider: .groq
        ),
        PostProcessingModel(
            id: "meta-llama/llama-4-maverick-17b-128e-instruct",
            displayName: "Llama 4 Maverick 17B",
            isAvailable: true,
            description: "Latest Llama 4, high quality",
            provider: .groq
        ),
        PostProcessingModel(
            id: "moonshotai/kimi-k2-instruct",
            displayName: "Kimi K2",
            isAvailable: true,
            description: "Strong agentic reasoning",
            provider: .groq
        ),

        // MARK: - xAI Grok Models
        PostProcessingModel(
            id: "grok-4.3",
            displayName: "Grok 4.3",
            isAvailable: true,
            description: "xAI's Grok 4.3 with reasoning disabled for low-latency text enhancement",
            provider: .grok
        ),

        // MARK: - Cerebras Models (ultra-fast inference)
        PostProcessingModel(
            id: "gpt-oss-120b",
            displayName: "GPT OSS 120B",
            isAvailable: true,
            description: "models.postProcessing.cerebras.gptoss.120b.description".localized,
            provider: .cerebras
        ),
        PostProcessingModel(
            id: "llama3.1-8b",
            displayName: "Llama 3.1 8B",
            isAvailable: true,
            description: "models.postProcessing.cerebras.llama31.8b.description".localized,
            provider: .cerebras
        ),
        PostProcessingModel(
            id: "qwen-3-235b-a22b-instruct-2507",
            displayName: "Qwen 3 235B Instruct (Preview)",
            isAvailable: true,
            description: "models.postProcessing.cerebras.qwen3.235b.description".localized,
            provider: .cerebras
        ),
        PostProcessingModel(
            id: "zai-glm-4.7",
            displayName: "Z.ai GLM 4.7 (Preview)",
            isAvailable: true,
            description: "models.postProcessing.cerebras.zai.glm47.description".localized,
            provider: .cerebras
        ),

        // MARK: - Mistral Models
        PostProcessingModel(
            id: "mistral-small-latest",
            displayName: "Mistral Small",
            isAvailable: true,
            description: "Fast, multilingual, cost-efficient",
            provider: .mistral
        ),
        PostProcessingModel(
            id: "open-mistral-nemo",
            displayName: "Mistral Nemo",
            isAvailable: true,
            description: "Compact 12B, multilingual",
            provider: .mistral
        ),

        // MARK: - Local LLM Models
        PostProcessingModel(
            id: "gemma-4-E2B-it-Q4_K_M.gguf",
            displayName: "Gemma 4 E2B (Q4)",
            isAvailable: true,
            description: "models.postProcessing.gemma4.e2b.description".localized,
            provider: .localLLM
        ),
        PostProcessingModel(
            id: "gemma-4-E4B-it-Q4_K_M.gguf",
            displayName: "Gemma 4 E4B (Q4)",
            isAvailable: true,
            description: "models.postProcessing.gemma4.e4b.description".localized,
            provider: .localLLM
        ),
        PostProcessingModel(
            id: "gemma-4-12b-it-Q4_K_M.gguf",
            displayName: "Gemma 4 12B (Q4)",
            isAvailable: true,
            description: "models.postProcessing.gemma4.12b.description".localized,
            provider: .localLLM
        ),
        PostProcessingModel(
            id: "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
            displayName: "Gemma 4 26B MoE (Q4)",
            isAvailable: true,
            description: "models.postProcessing.gemma4.26b.moe.description".localized,
            provider: .localLLM
        ),
        PostProcessingModel(
            id: "gemma-4-31B-it-Q4_K_M.gguf",
            displayName: "Gemma 4 31B Dense (Q4)",
            isAvailable: true,
            description: "models.postProcessing.gemma4.31b.description".localized,
            provider: .localLLM
        )
    ]
    
    /// Per-provider deprecated model ID mappings — maps retired model IDs to their replacements.
    /// Each provider manages its own deprecation cycle independently.
    /// When a provider deprecates a model, add the old ID → new ID under that provider's entry.
    private static let deprecatedModelMappings: [PostProcessingProvider: [String: String]] = [
        .anthropic: [
            // Deprecated 2026-02-16: claude-haiku-4.5 → claude-haiku-4-5
            "claude-haiku-4.5": "claude-haiku-4-5",
            "claude-3-5-haiku-latest": "claude-haiku-4-5",
            "claude-sonnet-4-5-20250929": "claude-sonnet-4-5",
        ],
        .cerebras: [
            // Deprecated 2026-02-16: llama-3.3-70b → gpt-oss-120b
            "llama-3.3-70b": "gpt-oss-120b",
            // Model ID format changed: llama-3.1-8b → llama3.1-8b
            "llama-3.1-8b": "llama3.1-8b",
        ],
        .gemini: [:],
        .groq: [
            // Decommissioned by Groq 2026-07-17 → openai/gpt-oss-120b (GroqCloud deprecation notice)
            "llama-3.3-70b-versatile": "openai/gpt-oss-120b",
            "llama-3.1-8b-instant": "openai/gpt-oss-120b",
            "meta-llama/llama-4-scout-17b-16e-instruct": "openai/gpt-oss-120b",
            "qwen/qwen3-32b": "openai/gpt-oss-120b",
        ],
        .grok: [
            // Retired 2026-05-15 — all grok-4-* fast variants redirect to grok-4.3.
            "grok-4-1-fast-non-reasoning": "grok-4.3",
            "grok-4.1-fast-non-reasoning": "grok-4.3",
            "grok-4-fast-non-reasoning": "grok-4.3",
            "grok-4-1-fast-reasoning": "grok-4.3",
            "grok-4-fast-reasoning": "grok-4.3",
        ],
        .localLLM: [
            // Migrated from Qwen 3.5 to Gemma 4 (2026-04)
            "Qwen3.5-4B-Q4_K_M.gguf": "gemma-4-E2B-it-Q4_K_M.gguf",
            "Qwen3.5-9B-Q4_K_M.gguf": "gemma-4-E4B-it-Q4_K_M.gguf",
        ]
    ]

    /// Resolves a model ID, replacing deprecated IDs with their current replacements
    /// - Parameters:
    ///   - id: The model ID (possibly deprecated)
    ///   - provider: The provider the model belongs to
    /// - Returns: The resolved model ID (replacement if deprecated, original otherwise)
    static func resolvedModelId(_ id: String, provider: PostProcessingProvider) -> String {
        if let providerMappings = deprecatedModelMappings[provider],
           let replacement = providerMappings[id],
           model(withId: replacement, provider: provider) != nil {
            return replacement
        }
        return id
    }

    /// Get models for a specific provider
    /// - Parameter provider: The post-processing provider to filter by
    /// - Returns: Array of models for that provider
    static func models(for provider: PostProcessingProvider) -> [PostProcessingModel] {
        availableModels.filter { $0.provider == provider && $0.isAvailable }
    }
    
    /// Get a model by its ID and provider
    /// - Parameters:
    ///   - id: The model ID to look up
    ///   - provider: The provider the model belongs to
    /// - Returns: The PostProcessingModel if found, nil otherwise
    static func model(withId id: String, provider: PostProcessingProvider) -> PostProcessingModel? {
        availableModels.first { $0.id == id && $0.provider == provider }
    }
    
    /// Get the display name for a model ID
    /// - Parameters:
    ///   - id: The model ID to look up
    ///   - provider: The provider the model belongs to
    /// - Returns: The display name if found, or the ID itself as fallback
    static func displayName(for id: String, provider: PostProcessingProvider) -> String {
        // Try to find the model directly
        if let model = model(withId: id, provider: provider) {
            return model.displayName
        }

        let resolvedId = resolvedModelId(id, provider: provider)
        if resolvedId != id,
           let model = model(withId: resolvedId, provider: provider) {
            return model.displayName
        }

        // Fallback to raw ID if not found
        return id
    }
    
    /// Get default model for a provider
    /// - Parameter provider: The post-processing provider
    /// - Returns: The default model for that provider
    static func defaultModel(for provider: PostProcessingProvider) -> PostProcessingModel? {
        models(for: provider).first { $0.id == provider.defaultModel } ?? models(for: provider).first
    }
}
