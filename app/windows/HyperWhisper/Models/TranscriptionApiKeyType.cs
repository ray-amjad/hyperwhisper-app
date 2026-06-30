// TRANSCRIPTION API KEY TYPE ENUM
// Defines the API key types for cloud transcription providers that need their own keys.
// Note: OpenAI, Groq, Gemini, and Grok share API keys with PostProcessingProvider.
// Most of these providers have separate keys specifically for transcription services.

namespace HyperWhisper.Models;

/// <summary>
/// API key types for cloud transcription providers that require their own key access path.
/// These are separate from PostProcessingProvider because:
/// 1. OpenAI/Groq/Gemini/Grok share keys between post-processing and transcription
/// 2. Most remaining providers only do transcription (not post-processing)
/// 3. This enum preserves compatibility for existing transcription key call sites
/// </summary>
public enum TranscriptionApiKeyType
{
    /// <summary>Deepgram - Nova models for transcription. No key prefix.</summary>
    Deepgram,

    /// <summary>AssemblyAI - Universal/SLAM transcription. No key prefix.</summary>
    AssemblyAI,

    /// <summary>ElevenLabs - Scribe transcription. No key prefix.</summary>
    ElevenLabs,

    /// <summary>Mistral - Voxtral transcription. No key prefix.</summary>
    Mistral,

    /// <summary>Soniox - async transcription. No key prefix.</summary>
    Soniox,

    /// <summary>Grok (xAI) - speech-to-text. Stored under the shared GrokApiKey for compatibility.</summary>
    Grok
}

/// <summary>
/// Extension methods for TranscriptionApiKeyType enum.
/// Provides utilities for key storage, validation, and display.
/// </summary>
public static class TranscriptionApiKeyTypeExtensions
{
    /// <summary>
    /// Gets the setting name used for storing the API key in ApiKeyService.
    /// Format: "{Provider}ApiKey" for consistency with PostProcessingProvider.
    /// </summary>
    public static string GetSettingName(this TranscriptionApiKeyType type) => type switch
    {
        TranscriptionApiKeyType.Deepgram => "DeepgramApiKey",
        TranscriptionApiKeyType.AssemblyAI => "AssemblyAIApiKey",
        TranscriptionApiKeyType.ElevenLabs => "ElevenLabsApiKey",
        TranscriptionApiKeyType.Mistral => "MistralApiKey",
        TranscriptionApiKeyType.Soniox => "SonioxApiKey",
        TranscriptionApiKeyType.Grok => "GrokApiKey",
        _ => ""
    };

    /// <summary>
    /// Gets the human-readable display name for the provider.
    /// Used in Settings UI labels and error messages.
    /// </summary>
    public static string GetDisplayName(this TranscriptionApiKeyType type) => type switch
    {
        TranscriptionApiKeyType.Deepgram => "Deepgram",
        TranscriptionApiKeyType.AssemblyAI => "AssemblyAI",
        TranscriptionApiKeyType.ElevenLabs => "ElevenLabs",
        TranscriptionApiKeyType.Mistral => "Mistral",
        TranscriptionApiKeyType.Soniox => "Soniox",
        TranscriptionApiKeyType.Grok => "Grok",
        _ => type.ToString()
    };

    /// <summary>
    /// Gets the expected key prefix for format validation.
    /// Returns null if the provider has no specific prefix requirement.
    /// </summary>
    public static string? GetKeyPrefix(this TranscriptionApiKeyType type) => type switch
    {
        TranscriptionApiKeyType.Grok => "xai-",
        // Deepgram, AssemblyAI, ElevenLabs, and Mistral have no specific prefix
        _ => null
    };

    /// <summary>
    /// Gets the minimum key length for format validation.
    /// Keys shorter than this are considered invalid.
    /// </summary>
    public static int GetMinLength(this TranscriptionApiKeyType type) => type switch
    {
        TranscriptionApiKeyType.Deepgram => 32,
        TranscriptionApiKeyType.AssemblyAI => 32,
        TranscriptionApiKeyType.ElevenLabs => 20,
        TranscriptionApiKeyType.Mistral => 20,
        TranscriptionApiKeyType.Soniox => 10,
        TranscriptionApiKeyType.Grok => 20,
        _ => 10
    };

    /// <summary>
    /// Gets the URL where users can obtain an API key for this provider.
    /// Used in Settings UI as a helpful link.
    /// </summary>
    public static string GetApiKeyUrl(this TranscriptionApiKeyType type) => type switch
    {
        TranscriptionApiKeyType.Deepgram => "https://console.deepgram.com/",
        TranscriptionApiKeyType.AssemblyAI => "https://www.assemblyai.com/app/account",
        TranscriptionApiKeyType.ElevenLabs => "https://elevenlabs.io/app/settings/api-keys",
        TranscriptionApiKeyType.Mistral => "https://console.mistral.ai/api-keys/",
        TranscriptionApiKeyType.Soniox => "https://console.soniox.com",
        TranscriptionApiKeyType.Grok => "https://console.x.ai/",
        _ => ""
    };

    /// <summary>
    /// Gets all transcription API key types as an array.
    /// Used for iterating in Settings UI and bulk operations.
    /// </summary>
    public static TranscriptionApiKeyType[] GetAll() => new[]
    {
        TranscriptionApiKeyType.Deepgram,
        TranscriptionApiKeyType.AssemblyAI,
        TranscriptionApiKeyType.ElevenLabs,
        TranscriptionApiKeyType.Mistral,
        TranscriptionApiKeyType.Soniox,
        TranscriptionApiKeyType.Grok
    };
}
