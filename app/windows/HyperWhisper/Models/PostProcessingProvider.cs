// POST-PROCESSING PROVIDER ENUM
// Defines the AI providers available for post-processing transcriptions.
// Each provider has a corresponding API endpoint and authentication method.

using HyperWhisper.Localization;

namespace HyperWhisper.Models;

/// <summary>
/// AI providers that can be used for post-processing transcriptions.
/// </summary>
public enum PostProcessingProvider
{
    /// <summary>No post-processing - returns raw transcription.</summary>
    None,

    /// <summary>HyperWhisper Cloud - built-in cloud post-processing (no API key required).</summary>
    HyperWhisperCloud,

    /// <summary>OpenAI API (GPT-4.1 models).</summary>
    OpenAI,

    /// <summary>Anthropic API (Claude models).</summary>
    Anthropic,

    /// <summary>Groq API (Llama/Mixtral models - fast inference).</summary>
    Groq,

    /// <summary>xAI Grok API (Grok models - fast inference).</summary>
    Grok,

    /// <summary>Google Gemini API (Gemini models - fast and efficient).</summary>
    Gemini,

    /// <summary>Cerebras API (Llama models - ultra-fast inference).</summary>
    Cerebras,

    /// <summary>Mistral API (Mistral models - fast, multilingual). Shares the Mistral STT key.</summary>
    Mistral,

    /// <summary>Local on-device LLM (offline post-processing).</summary>
    LocalLlm
}

/// <summary>
/// Extension methods for PostProcessingProvider enum.
/// Provides conversion utilities for display, serialization, and configuration.
/// </summary>
public static class PostProcessingProviderExtensions
{
    /// <summary>
    /// Gets the human-readable display name for the provider.
    /// Used in UI dropdowns and labels.
    /// </summary>
    public static string ToDisplayName(this PostProcessingProvider provider) => provider switch
    {
        PostProcessingProvider.None => Loc.S("common.off"),
        PostProcessingProvider.HyperWhisperCloud => Loc.S("provider.hyperwhisper"),
        PostProcessingProvider.OpenAI => Loc.S("provider.openai"),
        PostProcessingProvider.Anthropic => Loc.S("provider.anthropic"),
        PostProcessingProvider.Groq => Loc.S("provider.groq"),
        PostProcessingProvider.Grok => Loc.S("provider.grok"),
        PostProcessingProvider.Gemini => Loc.S("provider.gemini"),
        PostProcessingProvider.Cerebras => Loc.S("provider.cerebras"),
        PostProcessingProvider.Mistral => Loc.S("provider.mistral"),
        PostProcessingProvider.LocalLlm => Loc.S("provider.localLlm"),
        _ => provider.ToString()
    };

    /// <summary>
    /// Gets the string value for JSON serialization.
    /// Used when saving mode settings to disk.
    /// </summary>
    public static string ToStringValue(this PostProcessingProvider provider) => provider switch
    {
        PostProcessingProvider.None => "none",
        PostProcessingProvider.HyperWhisperCloud => "hyperwhispercloud",
        PostProcessingProvider.OpenAI => "openai",
        PostProcessingProvider.Anthropic => "anthropic",
        PostProcessingProvider.Groq => "groq",
        PostProcessingProvider.Grok => "grok",
        PostProcessingProvider.Gemini => "gemini",
        PostProcessingProvider.Cerebras => "cerebras",
        PostProcessingProvider.Mistral => "mistral",
        PostProcessingProvider.LocalLlm => "local_llm",
        _ => "none"
    };

    /// <summary>
    /// Parses a string value back to the enum.
    /// Used when loading mode settings from disk.
    /// </summary>
    public static PostProcessingProvider FromString(string? value) => value?.ToLowerInvariant() switch
    {
        "hyperwhispercloud" => PostProcessingProvider.HyperWhisperCloud,
        "hyperwhisper" => PostProcessingProvider.HyperWhisperCloud,
        "hyperwhisper_cloud" => PostProcessingProvider.HyperWhisperCloud,
        "openai" => PostProcessingProvider.OpenAI,
        "anthropic" => PostProcessingProvider.Anthropic,
        "groq" => PostProcessingProvider.Groq,
        "grok" => PostProcessingProvider.Grok,
        "gemini" => PostProcessingProvider.Gemini,
        "cerebras" => PostProcessingProvider.Cerebras,
        "mistral" => PostProcessingProvider.Mistral,
        "local_llm" => PostProcessingProvider.LocalLlm,
        "local" => PostProcessingProvider.LocalLlm,
        _ => PostProcessingProvider.None
    };

    /// <summary>
    /// Normalizes provider strings from other platforms while preserving custom endpoint IDs.
    /// macOS stores HyperWhisper Cloud post-processing as "hyperwhisper"; Windows
    /// historically stores it as "hyperwhispercloud".
    /// </summary>
    public static string? NormalizeStorageValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        if (CustomPostProcessingEndpoint.IsCustomProviderString(value))
            return value;

        var provider = FromString(value);
        return provider == PostProcessingProvider.None ? value : provider.ToStringValue();
    }

    /// <summary>
    /// Converts Windows provider strings to the shared backup/schema value.
    /// </summary>
    public static string? ToUniversalStorageValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        if (CustomPostProcessingEndpoint.IsCustomProviderString(value))
            return value;

        var provider = FromString(value);
        return provider switch
        {
            PostProcessingProvider.HyperWhisperCloud => "hyperwhisper",
            PostProcessingProvider.None => value,
            _ => provider.ToStringValue()
        };
    }

    /// <summary>
    /// Gets the setting name used for storing the API key.
    /// Each provider has a unique key stored via ApiKeyService.
    /// HyperWhisperCloud doesn't require an API key.
    /// </summary>
    public static string GetApiKeySettingName(this PostProcessingProvider provider) => provider switch
    {
        PostProcessingProvider.HyperWhisperCloud => "", // No API key required
        PostProcessingProvider.OpenAI => "OpenAIApiKey",
        PostProcessingProvider.Anthropic => "AnthropicApiKey",
        PostProcessingProvider.Groq => "GroqApiKey",
        PostProcessingProvider.Grok => "GrokApiKey",
        PostProcessingProvider.Gemini => "GeminiApiKey",
        PostProcessingProvider.Cerebras => "CerebrasApiKey",
        // Mistral post-processing reuses the existing Mistral STT key store
        // (TranscriptionApiKeyType.Mistral => "MistralApiKey"), mirroring how macOS
        // shares `mistralAPIKey` across transcription and post-processing.
        PostProcessingProvider.Mistral => "MistralApiKey",
        PostProcessingProvider.LocalLlm => "",
        _ => ""
    };

    /// <summary>
    /// Returns true if this provider requires an API key.
    /// HyperWhisperCloud and None do not require API keys.
    /// </summary>
    public static bool RequiresApiKey(this PostProcessingProvider provider) => provider switch
    {
        PostProcessingProvider.None => false,
        PostProcessingProvider.HyperWhisperCloud => false,
        PostProcessingProvider.LocalLlm => false,
        _ => true
    };
}
