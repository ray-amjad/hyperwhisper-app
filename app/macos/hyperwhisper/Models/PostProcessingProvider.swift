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

    /// Accepts every spelling of a provider id that any HyperWhisper head has
    /// stored, and folds it onto the matching case.
    ///
    /// macOS has always stored HyperWhisper Cloud post-processing as
    /// `"hyperwhisper"`. Windows and the Linux/portable head store
    /// `"hyperwhispercloud"`, and older builds wrote `"hyperwhisper_cloud"`.
    /// A universal `.hwbackup` carries the value verbatim — the exporter does
    /// not fold it (`ApplicationBackupExport.cs`) — so restoring a Linux backup
    /// on macOS used to land a token the synthesised initialiser rejected. That
    /// failure is silent and partial: `AIPostProcessor` returns the raw
    /// transcript while `ModeCard` and `ModePostProcessingSettings` still show
    /// "HyperWhisper Cloud", because their `?? .hyperwhisper` fallbacks fire on
    /// nil and hide the mismatch.
    ///
    /// Overriding `init?(rawValue:)` rather than adding a separate parser is
    /// deliberate: the compiler routes all 20 existing
    /// `PostProcessingProvider(rawValue:)` call sites through it, so no read
    /// site can be missed here or re-introduced without the tolerance later.
    ///
    /// Mirrors `PostProcessingProviderExtensions.FromString`
    /// (`app/windows/HyperWhisper/Models/PostProcessingProvider.cs`) and
    /// `LinuxPostProcessingRouter.TryResolveProvider`, whose alias set is pinned
    /// by `HyperWhisper.Linux.Composition.Tests`.
    ///
    /// Unrecognised values still return nil, so a `"custom:<uuid>"` endpoint
    /// string keeps falling through to `CustomPostProcessingEndpoint` at the
    /// call sites that check it first.
    init?(rawValue: String) {
        let normalized = rawValue
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased()
        switch normalized {
        case "hyperwhisper", "hyperwhispercloud", "hyperwhisper_cloud":
            self = .hyperwhisper
        default:
            // Match against the declared raw values directly. Calling
            // `PostProcessingProvider(rawValue:)` here would recurse forever —
            // this IS that initialiser.
            guard let match = Self.allCases.first(where: { $0.rawValue == normalized }) else {
                return nil
            }
            self = match
        }
    }

    var id: String { rawValue }

    /// The canonical cross-platform spelling to PERSIST for this provider.
    ///
    /// `rawValue` deliberately stays `"hyperwhisper"`: it is wired into API-key
    /// setting names, `ProviderHealth` string keys and SwiftUI picker tags, and
    /// re-canonicalising it would ripple through all of them. But the token the
    /// three heads agree to *store* for HyperWhisper Cloud is
    /// `"hyperwhispercloud"`, so use this wherever macOS itself chooses the
    /// provider. A value supplied by a caller — Local API `POST`/`PATCH`, a
    /// backup restore — is stored verbatim instead, matching Windows'
    /// `ModesEndpoints` contract of "tolerant on read, verbatim on write".
    ///
    /// Mirrors `PostProcessingProviderExtensions.ToStringValue` on Windows.
    var storageValue: String {
        switch self {
        case .hyperwhisper:
            return "hyperwhispercloud"
        default:
            return rawValue
        }
    }

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
            return "SpaceXAI's Grok models for text enhancement"
        case .cerebras:
            return "Cerebras' ultra-fast LLM inference for text enhancement"
        case .mistral:
            return "Mistral's models for fast, multilingual text enhancement"
        case .localLLM:
            return "Runs an on-device language model via llama.cpp. Private and offline."
        }
    }

    /// The same provider as the shared Rust core names it.
    ///
    /// The endpoint table, the auth headers and the request bodies all live in
    /// the core (issue #282). This app used to carry its own copy of each, and
    /// the Windows app carried a third — one provider added on one platform did
    /// not exist on the other. Build a request with `llmBuildRequest`; do not
    /// re-add a `chatEndpoint` here.
    var coreProvider: HwLlmProvider {
        switch self {
        case .hyperwhisper:
            return .hyperWhisperCloud
        case .openai:
            return .openAi
        case .anthropic:
            return .anthropic
        case .gemini:
            return .gemini
        case .groq:
            return .groq
        case .grok:
            return .grok
        case .cerebras:
            return .cerebras
        case .mistral:
            return .mistral
        case .localLLM:
            return .localLlama
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

    /// Default model for this provider
    var defaultModel: String {
        switch self {
        case .hyperwhisper:
            return "hyperwhisper-cloud"  // Built-in cloud service identifier
        case .openai:
            return "gpt-5.6-luna"
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
