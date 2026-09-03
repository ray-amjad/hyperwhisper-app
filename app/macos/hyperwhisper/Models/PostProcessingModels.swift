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

        // MARK: - OpenAI Models (GPT-5.6 Luna is default)
        PostProcessingModel(
            id: "gpt-5.6-luna",
            displayName: "GPT-5.6 Luna",
            isAvailable: true,
            description: "Latest generation, fastest",
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
            description: "Fast, lightweight",
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
            description: "High quality, capable Sonnet model",
            provider: .anthropic
        ),
        PostProcessingModel(
            id: "claude-sonnet-5",
            displayName: "Claude Sonnet 5",
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
            id: "gemini-3.1-pro-preview",
            displayName: "Gemini 3.1 Pro",
            isAvailable: true,
            description: "Latest pro-level intelligence",
            provider: .gemini
        ),
        PostProcessingModel(
            id: "gemini-3.1-flash-lite",
            displayName: "Gemini 3.1 Flash Lite",
            isAvailable: true,
            description: "Lightweight, fast and cost-efficient",
            provider: .gemini
        ),
        PostProcessingModel(
            id: "gemini-3.6-flash",
            displayName: "Gemini 3.6 Flash",
            isAvailable: true,
            description: "Capable flash model, frontier performance for agentic tasks",
            provider: .gemini
        ),
        PostProcessingModel(
            id: "gemini-3.7-flash",
            displayName: "Gemini 3.7 Flash",
            isAvailable: true,
            description: "Latest flash model, frontier performance for agentic tasks",
            provider: .gemini
        ),
        PostProcessingModel(
            id: "gemini-3.5-flash-lite",
            displayName: "Gemini 3.5 Flash Lite",
            isAvailable: true,
            description: "Latest lightweight flash, fast and cost-efficient",
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
            id: "qwen/qwen3.6-27b",
            displayName: "Qwen 3.6 27B",
            isAvailable: true,
            description: "Latest Qwen, strong quality-to-speed ratio",
            provider: .groq
        ),

        // MARK: - xAI Grok Models
        PostProcessingModel(
            id: "grok-4.3",
            displayName: "Grok 4.3",
            isAvailable: true,
            description: "SpaceXAI's Grok 4.3 with reasoning disabled for low-latency text enhancement",
            provider: .grok
        ),
        PostProcessingModel(
            id: "grok-4.5",
            displayName: "Grok 4.5",
            isAvailable: true,
            description: "SpaceXAI's Grok 4.5 with reasoning disabled for low-latency text enhancement",
            provider: .grok
        ),
        PostProcessingModel(
            id: "grok-4.6",
            displayName: "Grok 4.6",
            isAvailable: true,
            description: "SpaceXAI's latest Grok model with a 500k context window and reasoning disabled for low-latency text enhancement",
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
            id: "gemma-4-31b",
            displayName: "Gemma 4 31B",
            isAvailable: true,
            description: "Fast, efficient general-purpose model",
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
            id: "mistral-medium-3.5",
            displayName: "Mistral Medium 3.5",
            isAvailable: true,
            description: "High quality, multilingual, balanced cost",
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
        .openai: [
            "gpt-4.1-nano": "gpt-5-nano",
        ],
        .anthropic: [
            // Deprecated 2026-02-16: claude-haiku-4.5 → claude-haiku-4-5
            "claude-haiku-4.5": "claude-haiku-4-5",
            "claude-3-5-haiku-latest": "claude-haiku-4-5",
            "claude-sonnet-4-5-20250929": "claude-sonnet-4-5",
            // Alias for claude-sonnet-4-20250514, retired 2026-06-15 → claude-sonnet-4-5
            "claude-sonnet-4-0": "claude-sonnet-4-5",
        ],
        .cerebras: [
            // Deprecated 2026-02-16: llama-3.3-70b → gpt-oss-120b
            "llama-3.3-70b": "gpt-oss-120b",
            "llama-3.1-8b": "gemma-4-31b",
            "llama3.1-8b": "gemma-4-31b",
            "qwen-3-235b-a22b-instruct-2507": "gpt-oss-120b",
            // zai-glm-4.7 scheduled for deprecation 2026-08-17 (Cerebras
            // inference-docs.cerebras.ai/models/zai-glm-47 + change-log, checked
            // 2026-08-07). Cerebras has NOT published an official successor model
            // in the public Inference API catalog as of this writing — GLM-5.1
            // exists only on Cerebras's separate Dedicated Endpoints product, not
            // the public chat-completions catalog this app's BYOK integration
            // calls. Redirecting to gpt-oss-120b, the sole "Production"-tier
            // general-purpose model in that catalog and the target every other
            // non-Z.ai Cerebras deprecation in this table redirects to.
            "zai-glm-4.7": "gpt-oss-120b",
        ],
        .gemini: [
            "gemini-3-pro-preview": "gemini-3.1-pro-preview",
            "gemini-3.1-flash-lite-preview": "gemini-3.1-flash-lite",
            "gemini-2.0-flash": "gemini-3.6-flash",
            "gemini-2.0-flash-lite": "gemini-3.1-flash-lite",
        ],
        .groq: [
            // Decommissioned by Groq 2026-07-17 → openai/gpt-oss-120b (GroqCloud deprecation notice)
            "llama-3.3-70b-versatile": "openai/gpt-oss-120b",
            "llama-3.1-8b-instant": "openai/gpt-oss-120b",
            "meta-llama/llama-4-scout-17b-16e-instruct": "openai/gpt-oss-120b",
            "qwen/qwen3-32b": "openai/gpt-oss-120b",
            // Shut down by Groq 2025-10-10 — chain kimi → kimi-k2-instruct-0905 → openai/gpt-oss-120b
            "moonshotai/kimi-k2-instruct": "openai/gpt-oss-120b",
            // Shut down by Groq 2026-03-09 → openai/gpt-oss-120b (matches Windows)
            "meta-llama/llama-4-maverick-17b-128e-instruct": "openai/gpt-oss-120b",
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
        ],
        .mistral: [
            // Weights retired 2026-07-31. Mistral names Ministral 3 8B as the
            // successor, but we redirect to mistral-small-latest — already in
            // this list and the HyperWhisper Cloud default for Mistral.
            "open-mistral-nemo": "mistral-small-latest",
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
