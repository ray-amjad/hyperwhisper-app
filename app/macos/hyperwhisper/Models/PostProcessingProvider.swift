//
//  PostProcessingProvider.swift
//  hyperwhisper
//
//  Post-Processing Provider Configuration
//  Defines available providers for AI text enhancement (OpenAI, Anthropic, Google Gemini)
//  Separate from CloudProvider which handles transcription
//

import Foundation

/// Post-Processing Provider enum for AI text enhancement
/// These providers are used for post-processing transcribed text, not for transcription itself
enum PostProcessingProvider: String, CaseIterable, Identifiable {
    case hyperwhisper = "hyperwhisper"  // FIRST: Built-in default provider
    case openai = "openai"
    case anthropic = "anthropic"
    case gemini = "gemini"
    case groq = "groq"
    case grok = "grok"
    case cerebras = "cerebras"
    case mistral = "mistral"
    case localLLM = "local_llm"

    var id: String { rawValue }

    /// Display name for the provider
    var displayName: String {
        switch self {
        case .hyperwhisper:
            return "HyperWhisper Cloud"
        case .openai:
            return "OpenAI"
        case .anthropic:
            return "Anthropic"
        case .gemini:
            return "Google Gemini"
        case .groq:
            return "Groq"
        case .grok:
            return "Grok"
        case .cerebras:
            return "Cerebras"
        case .mistral:
            return "Mistral"
        case .localLLM:
            return "Local LLM"
        }
    }

    /// Description for tooltips
    var description: String {
        switch self {
        case .hyperwhisper:
            return "Built-in AI text enhancement with credit-based usage. No API key needed."
        case .openai:
            return "OpenAI's GPT models for text enhancement"
        case .anthropic:
            return "Anthropic's Claude models for advanced text processing"
        case .gemini:
            return "Google's Gemini models for efficient text enhancement"
        case .groq:
            return "Groq's ultra-fast LLM inference for text enhancement"
        case .grok:
            return "xAI's Grok models for text enhancement"
        case .cerebras:
            return "Cerebras' ultra-fast LLM inference for text enhancement"
        case .mistral:
            return "Mistral's models for fast, multilingual text enhancement"
        case .localLLM:
            return "Runs an on-device language model via llama.cpp. Private and offline."
        }
    }

    /// API endpoint for chat completions
    /// All providers use OpenAI-compatible endpoints for consistency
    var chatEndpoint: String {
        switch self {
        case .hyperwhisper:
            return NetworkConfig.hyperwhisperCloudURL
        case .openai:
            return "https://api.openai.com/v1/chat/completions"
        case .anthropic:
            // Anthropic native Messages API (supports cache_control for prompt caching)
            return "https://api.anthropic.com/v1/messages"
        case .gemini:
            // Gemini provides OpenAI-compatible endpoint
            return "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions"
        case .groq:
            // Groq provides OpenAI-compatible endpoint
            return "https://api.groq.com/openai/v1/chat/completions"
        case .grok:
            // xAI provides an OpenAI-compatible endpoint
            return "https://api.x.ai/v1/chat/completions"
        case .cerebras:
            // Cerebras provides OpenAI-compatible endpoint
            return "https://api.cerebras.ai/v1/chat/completions"
        case .mistral:
            // Mistral provides an OpenAI-compatible endpoint
            return "https://api.mistral.ai/v1/chat/completions"
        case .localLLM:
            return "http://127.0.0.1:\(LlamaServerController.Configuration.default.port)/v1/chat/completions"
        }
    }

    /// API key URL for getting keys
    var apiKeyURL: String {
        switch self {
        case .hyperwhisper:
            return "https://www.hyperwhisper.com"
        case .openai:
            return "https://platform.openai.com/api-keys"
        case .anthropic:
            return "https://console.anthropic.com/settings/keys"
        case .gemini:
            return "https://aistudio.google.com/app/apikey"
        case .groq:
            return "https://console.groq.com/keys"
        case .grok:
            return "https://console.x.ai/"
        case .cerebras:
            return "https://cloud.cerebras.ai/"
        case .mistral:
            return "https://console.mistral.ai/api-keys"
        case .localLLM:
            return ""
        }
    }

    /// Whether this provider uses standard OpenAI authentication
    /// OpenAI and Gemini use "Authorization: Bearer {key}"
    /// Anthropic uses "x-api-key: {key}" (native Messages API)
    var usesStandardAuth: Bool {
        switch self {
        case .hyperwhisper:
            return false  // Uses device_id/license_key instead
        case .openai, .gemini, .groq, .grok, .cerebras, .mistral:
            return true
        case .anthropic:
            // Anthropic native Messages API uses x-api-key header
            return false
        case .localLLM:
            return false
        }
    }

    /// Default model for this provider
    var defaultModel: String {
        switch self {
        case .hyperwhisper:
            return "hyperwhisper-cloud"  // Built-in cloud service identifier
        case .openai:
            return "gpt-4.1-nano"
        case .anthropic:
            return "claude-3-5-haiku-latest"
        case .gemini:
            return "gemini-2.5-flash"
        case .groq:
            return "openai/gpt-oss-120b"
        case .grok:
            return "grok-4.3"
        case .cerebras:
            return "gpt-oss-120b"
        case .mistral:
            return "mistral-small-latest"
        case .localLLM:
            return "gemma-4-E2B-it-Q4_K_M.gguf"
        }
    }

    /// Whether this provider requires an API key and external connectivity
    var requiresAPIKey: Bool {
        switch self {
        case .hyperwhisper, .localLLM:
            return false
        default:
            return true
        }
    }

    /// Whether the health checker should probe this provider
    var requiresHealthCheck: Bool {
        requiresAPIKey || self == .localLLM
    }

    /// Whether this provider is meant to run completely offline
    var isLocal: Bool {
        self == .localLLM
    }
}
